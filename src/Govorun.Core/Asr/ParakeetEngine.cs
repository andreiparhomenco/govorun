using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Govorun.Core.Asr;

/// <summary>Result of a transcription, including the measured real-time factor.</summary>
public sealed record TranscriptionResult(string Text, double AudioSeconds, double ElapsedSeconds)
{
    public double Rtf => ElapsedSeconds > 0 ? AudioSeconds / ElapsedSeconds : 0;
}

/// <summary>
/// NVIDIA Parakeet TDT 0.6B V3 inference on CPU via ONNX Runtime.
/// Pipeline: nemo128 preprocessor (log-mel) → conformer encoder → greedy
/// token-and-duration transducer (TDT) decoding with the stateful decoder_joint.
/// Sessions are created once and reused for the process lifetime.
/// </summary>
public sealed class ParakeetEngine : IDisposable
{
    public const int SampleRate = 16_000;
    private const int MaxTokensPerStep = 10;
    private const int MaxChunkSeconds = 60;

    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoderJoint;
    private readonly Vocabulary _vocab;
    private readonly bool _targetsAreInt32;
    private readonly int[] _state1Dims;
    private readonly int[] _state2Dims;
    private readonly object _lock = new();

    public ParakeetEngine(ModelPaths paths)
    {
        // Each session builds its own intra-op thread pool, so one shared "use every
        // core" option meant three pools fighting over the machine and freezing the UI.
        // The encoder is the only sizeable graph; the other two are given a single
        // thread because pool wake-up costs more than the work itself — decoder_joint
        // especially, which runs once per frame on a [1, D, 1] tensor.
        using var heavyOptions = SessionOptionsWith(Math.Max(1, Environment.ProcessorCount / 2));
        using var lightOptions = SessionOptionsWith(1);

        _preprocessor = LoadSession(paths.Preprocessor, lightOptions);
        _encoder = LoadSession(paths.Encoder, heavyOptions);
        _decoderJoint = LoadSession(paths.DecoderJoint, lightOptions);
        _vocab = new Vocabulary(paths.Vocab);

        _targetsAreInt32 = _decoderJoint.InputMetadata["targets"].ElementType == typeof(int);
        _state1Dims = FixedStateDims(_decoderJoint.InputMetadata["input_states_1"].Dimensions);
        _state2Dims = FixedStateDims(_decoderJoint.InputMetadata["input_states_2"].Dimensions);
    }

    private static SessionOptions SessionOptionsWith(int intraOpThreads) => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = intraOpThreads,
    };

    /// <summary>
    /// A missing Visual C++ Runtime surfaces as a TypeInitializationException from
    /// whichever session loads first; translate it for all three rather than only
    /// the preprocessor, since load order is an implementation detail.
    /// </summary>
    private static InferenceSession LoadSession(string path, SessionOptions options)
    {
        try
        {
            return new InferenceSession(path, options);
        }
        catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException)
        {
            throw new InvalidOperationException(
                "Не удалось загрузить движок распознавания (onnxruntime). " +
                "Обычно причина — отсутствие Visual C++ Runtime. " +
                "Переустановите Govorun свежим инсталлятором или установите пакет " +
                "https://aka.ms/vs/17/release/vc_redist.x64.exe", ex);
        }
    }

    private static int[] FixedStateDims(int[] dims) =>
        [dims[0] > 0 ? dims[0] : 1, 1, dims[2] > 0 ? dims[2] : 640];

    /// <summary>Transcribes 16 kHz mono float32 audio.</summary>
    public TranscriptionResult Transcribe(ReadOnlyMemory<float> samples)
    {
        var sw = Stopwatch.StartNew();
        var parts = new List<string>();
        lock (_lock)
        {
            foreach (var chunk in AudioChunker.Split(samples, SampleRate, MaxChunkSeconds))
                parts.Add(TranscribeChunk(chunk.Span));
        }
        sw.Stop();
        var text = string.Join(" ", parts.Where(p => p.Length > 0)).Trim();
        return new TranscriptionResult(text, (double)samples.Length / SampleRate, sw.Elapsed.TotalSeconds);
    }

    private string TranscribeChunk(ReadOnlySpan<float> samples)
    {
        if (samples.Length < SampleRate / 10) return "";

        // Preprocessor: waveforms [1, N] float32, waveforms_lens [1] int64 → features, features_lens.
        var waveform = new DenseTensor<float>(samples.ToArray(), [1, samples.Length]);
        var waveformLens = new DenseTensor<long>(new long[] { samples.Length }, [1]);
        using var preOut = _preprocessor.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("waveforms", waveform),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", waveformLens),
        });
        var features = (DenseTensor<float>)preOut.First(v => v.Name == "features").Value;
        var featuresLens = ToInt64Array(preOut.First(v => v.Name == "features_lens").Value);

        // Encoder: audio_signal [1, 128, T], length [1] → outputs [1, D, T'], encoded_lengths [1].
        var lengthInput = _encoder.InputMetadata["length"].ElementType == typeof(int)
            ? NamedOnnxValue.CreateFromTensor("length", new DenseTensor<int>(new[] { (int)featuresLens[0] }, [1]))
            : NamedOnnxValue.CreateFromTensor("length", new DenseTensor<long>(new[] { featuresLens[0] }, [1]));
        using var encOut = _encoder.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("audio_signal", features),
            lengthInput,
        });
        var encoderOut = (DenseTensor<float>)encOut.First(v => v.Name == "outputs").Value;
        var encodedLen = (int)ToInt64Array(encOut.First(v => v.Name == "encoded_lengths").Value)[0];

        return _vocab.Decode(DecodeTdt(encoderOut, encodedLen));
    }

    /// <summary>
    /// Greedy TDT decoding (ported from onnx-asr): at frame t run decoder_joint with the
    /// last emitted token and LSTM states; emit argmax token unless blank; advance t by the
    /// predicted duration (skip), or by 1 on blank / when max tokens per frame is reached.
    /// </summary>
    private List<int> DecodeTdt(DenseTensor<float> encoderOut, int encodedLen)
    {
        int dim = encoderOut.Dimensions[1];
        int frames = encoderOut.Dimensions[2];
        encodedLen = Math.Min(encodedLen, frames);
        var encBuffer = encoderOut.Buffer;

        var tokens = new List<int>();
        var state1 = new DenseTensor<float>(new float[_state1Dims[0] * _state1Dims[2]], _state1Dims);
        var state2 = new DenseTensor<float>(new float[_state2Dims[0] * _state2Dims[2]], _state2Dims);
        var frame = new float[dim];
        int vocabSize = _vocab.Count;

        int t = 0, emitted = 0;
        while (t < encodedLen)
        {
            // encoder_outputs expects [1, D, 1]: column t of the [1, D, T'] output.
            var span = encBuffer.Span;
            for (int d = 0; d < dim; d++) frame[d] = span[d * frames + t];

            int lastToken = tokens.Count > 0 ? tokens[^1] : _vocab.BlankIndex;
            var inputs = new List<NamedOnnxValue>(5)
            {
                NamedOnnxValue.CreateFromTensor("encoder_outputs", new DenseTensor<float>(frame.AsMemory(), [1, dim, 1])),
                NamedOnnxValue.CreateFromTensor("input_states_1", state1),
                NamedOnnxValue.CreateFromTensor("input_states_2", state2),
            };
            if (_targetsAreInt32)
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<int>(new[] { lastToken }, [1, 1])));
                inputs.Add(NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<int>(new[] { 1 }, [1])));
            }
            else
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor("targets", new DenseTensor<long>(new[] { (long)lastToken }, [1, 1])));
                inputs.Add(NamedOnnxValue.CreateFromTensor("target_length", new DenseTensor<long>(new[] { 1L }, [1])));
            }

            using var stepOut = _decoderJoint.Run(inputs);
            var logits = ((DenseTensor<float>)stepOut.First(v => v.Name == "outputs").Value).Buffer.Span;

            // Logits layout: [vocabSize token logits + blank][duration logits].
            int token = ArgMax(logits[..vocabSize]);
            int step = ArgMax(logits[vocabSize..]);

            if (token != _vocab.BlankIndex)
            {
                tokens.Add(token);
                emitted++;
                CopyState(stepOut, "output_states_1", state1);
                CopyState(stepOut, "output_states_2", state2);
            }

            if (step > 0)
            {
                t += step;
                emitted = 0;
            }
            else if (token == _vocab.BlankIndex || emitted == MaxTokensPerStep)
            {
                t += 1;
                emitted = 0;
            }
        }

        return tokens;
    }

    private static void CopyState(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, string name, DenseTensor<float> target)
    {
        var value = (DenseTensor<float>)outputs.First(v => v.Name == name).Value;
        value.Buffer.Span.CopyTo(target.Buffer.Span);
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    private static long[] ToInt64Array(object tensorValue) => tensorValue switch
    {
        DenseTensor<long> t => t.Buffer.ToArray(),
        DenseTensor<int> t => Array.ConvertAll(t.Buffer.ToArray(), x => (long)x),
        _ => throw new InvalidOperationException($"Unexpected tensor type {tensorValue.GetType()}"),
    };

    public void Dispose()
    {
        _preprocessor.Dispose();
        _encoder.Dispose();
        _decoderJoint.Dispose();
    }
}

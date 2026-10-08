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
/// Sber GigaAM v3 E2E inference on CPU via ONNX Runtime.
/// Pipeline: log-mel preprocessor (64 bins) → conformer encoder with a CTC head →
/// greedy CTC decoding. The "E2E" model emits punctuation, capitalisation and
/// normalised numbers itself, so no post-processing restores them.
/// Sessions are created once and reused for the process lifetime.
/// </summary>
public sealed class AsrEngine : IDisposable
{
    public const int SampleRate = 16_000;
    private const int MaxChunkSeconds = 60;

    private readonly InferenceSession _preprocessor;
    private readonly InferenceSession _model;
    private readonly Vocabulary _vocab;
    private readonly bool _lengthsAreInt32;
    private readonly object _lock = new();

    public AsrEngine(ModelPaths paths)
    {
        // Each session builds its own intra-op thread pool, so one shared "use every
        // core" option meant pools fighting over the machine and freezing the UI.
        // The model is the only sizeable graph; the preprocessor runs once per chunk
        // on a small input, where pool wake-up costs more than the work itself.
        using var heavyOptions = SessionOptionsWith(Math.Max(1, Environment.ProcessorCount / 2));
        using var lightOptions = SessionOptionsWith(1);

        _preprocessor = LoadSession(paths.Preprocessor, lightOptions);
        _model = LoadSession(paths.Model, heavyOptions);
        _vocab = new Vocabulary(paths.Vocab);

        _lengthsAreInt32 = _model.InputMetadata["feature_lengths"].ElementType == typeof(int);
    }

    /// <remarks>
    /// The CPU memory arena is disabled deliberately. With it on, the process sat at
    /// ~1.77 GB private after the first dictation although the weights are far smaller:
    /// prepacked weights are carved from the arena at load and it grows in oversized
    /// blocks it never returns. Without it memory plateaus near the weight size
    /// (govorun-cli mem). Measured cost: a few percent of RTF on short dictations.
    /// The memory pattern planner needs the arena, so it is switched off alongside it.
    /// </remarks>
    private static SessionOptions SessionOptionsWith(int intraOpThreads) => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = intraOpThreads,
        EnableCpuMemArena = false,
        EnableMemoryPattern = false,
    };

    /// <summary>
    /// A missing Visual C++ Runtime surfaces as a TypeInitializationException from
    /// whichever session loads first; translate it for both rather than only the
    /// preprocessor, since load order is an implementation detail.
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

        // Preprocessor: waveforms [1, N] float32, waveforms_lens [1] int64 → features [1, 64, T].
        var waveform = new DenseTensor<float>(samples.ToArray(), [1, samples.Length]);
        var waveformLens = new DenseTensor<long>(new long[] { samples.Length }, [1]);
        using var preOut = _preprocessor.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("waveforms", waveform),
            NamedOnnxValue.CreateFromTensor("waveforms_lens", waveformLens),
        });
        var features = (DenseTensor<float>)preOut.First(v => v.Name == "features").Value;
        var featuresLens = ToInt64Array(preOut.First(v => v.Name == "features_lens").Value);

        // Model: features [1, 64, T], feature_lengths [1] → log_probs [1, T', vocab].
        var lengthInput = _lengthsAreInt32
            ? NamedOnnxValue.CreateFromTensor("feature_lengths", new DenseTensor<int>(new[] { (int)featuresLens[0] }, [1]))
            : NamedOnnxValue.CreateFromTensor("feature_lengths", new DenseTensor<long>(new[] { featuresLens[0] }, [1]));
        using var modelOut = _model.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("features", features),
            lengthInput,
        });
        var logProbs = (DenseTensor<float>)modelOut.First(v => v.Name == "log_probs").Value;

        return _vocab.Decode(DecodeCtc(logProbs));
    }

    /// <summary>
    /// Greedy CTC decoding: take the most likely token per frame, then collapse runs of
    /// the same token and drop blanks. A repeated word the speaker actually said survives
    /// because CTC puts a blank frame between the two emissions.
    /// </summary>
    private List<int> DecodeCtc(DenseTensor<float> logProbs)
    {
        int frames = logProbs.Dimensions[1];
        int vocabSize = logProbs.Dimensions[2];
        var data = logProbs.Buffer.Span;

        var tokens = new List<int>();
        int previous = -1;
        for (int t = 0; t < frames; t++)
        {
            int token = ArgMax(data.Slice(t * vocabSize, vocabSize));
            if (token != previous && token != _vocab.BlankIndex) tokens.Add(token);
            previous = token;
        }
        return tokens;
    }

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        int best = 0;
        for (int i = 1; i < values.Length; i++)
            if (values[i] > values[best]) best = i;
        return best;
    }

    private static long[] ToInt64Array(object tensor) => tensor switch
    {
        DenseTensor<long> l => l.ToArray(),
        DenseTensor<int> i => Array.ConvertAll(i.ToArray(), v => (long)v),
        _ => throw new InvalidOperationException($"Unexpected length tensor type {tensor.GetType()}"),
    };

    public void Dispose()
    {
        _preprocessor.Dispose();
        _model.Dispose();
    }
}

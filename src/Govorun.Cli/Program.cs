using Govorun.Core.Asr;
using Govorun.Core.Audio;
using Govorun.Core.Benchmark;
using Govorun.Core.Text;
using NAudio.Wave;

if (args.Length == 0)
{
    Console.WriteLine("""
        Govorun CLI — smoke tests for the ASR core.

        Usage:
          govorun-cli bench [modelsDir]             Measure RTF on synthetic audio
          govorun-cli mem [modelsDir]               Private memory after load and 10/60/120 s runs
          govorun-cli transcribe <file.wav> [dir]   Transcribe a wav file
          govorun-cli inject "<text>"               E2E test of text injection via Notepad
          govorun-cli mic [seconds]                 List capture devices, record, report RMS
        """);
    return 1;
}

var command = args[0].ToLowerInvariant();

if (command == "mic")
{
    var devices = AudioRecorder.ListDevices();
    if (devices.Count == 0)
    {
        Console.Error.WriteLine("No active capture devices found");
        return 1;
    }
    foreach (var d in devices)
        Console.WriteLine($"{(d.IsDefault ? "*" : " ")} {d.Name}");

    int seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 3;
    var recorder = new AudioRecorder();
    if (args.Length > 2 && int.TryParse(args[2], out var idx) && idx < devices.Count)
        recorder.DeviceId = devices[idx].Id;
    float peak = 0;
    recorder.LevelChanged += level => peak = Math.Max(peak, level);
    Console.WriteLine($"Recording {seconds} s from {(recorder.DeviceId is null ? "default device" : "device #" + args[2])}...");
    recorder.Start();
    Thread.Sleep(seconds * 1000);
    var recorded = recorder.Stop();
    double rms = recorded.Length > 0 ? Math.Sqrt(recorded.Select(x => (double)x * x).Average()) : 0;
    Console.WriteLine($"Samples: {recorded.Length} ({(double)recorded.Length / AsrEngine.SampleRate:F1} s @16kHz), RMS: {rms:F4}, peak level: {peak:F4}");
    return recorded.Length > 0 ? 0 : 1;
}

if (command == "model-info")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: govorun-cli model-info <model.onnx>");
        return 1;
    }
    using var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
    using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(args[1], options);

    static void Dump(string title, IReadOnlyDictionary<string, Microsoft.ML.OnnxRuntime.NodeMetadata> meta)
    {
        Console.WriteLine(title);
        foreach (var (name, m) in meta)
            Console.WriteLine($"  {name,-20} {m.ElementType.Name,-7} [{string.Join(", ", m.Dimensions)}]");
    }

    Dump("inputs:", session.InputMetadata);
    Dump("outputs:", session.OutputMetadata);
    if (session.ModelMetadata.CustomMetadataMap.Count > 0)
    {
        Console.WriteLine("metadata:");
        foreach (var (k, v) in session.ModelMetadata.CustomMetadataMap)
            Console.WriteLine($"  {k} = {v}");
    }
    return 0;
}

if (command == "inject-debug")
    return Govorun.Cli.InjectE2E.Debug();
if (command == "inject")
    return Govorun.Cli.InjectE2E.Run(args.Length > 1 ? args[1] : "Привет, Говорун! Injection test 123.");

string modelsDir = command switch
{
    "bench" or "mem" when args.Length > 1 => args[1],
    "transcribe" when args.Length > 2 => args[2],
    _ => ModelPaths.DefaultDirectory,
};

Console.WriteLine($"Loading model from {modelsDir}...");
var loadStart = System.Diagnostics.Stopwatch.StartNew();
using var engine = new AsrEngine(ModelPaths.Locate(modelsDir));
Console.WriteLine($"Model loaded in {loadStart.Elapsed.TotalSeconds:F1} s");

switch (command)
{
    case "bench":
    {
        var result = RtfBenchmark.Run(engine);
        Console.WriteLine($"Audio: {result.AudioSeconds:F1} s, elapsed: {result.ElapsedSeconds:F2} s, RTF: {result.Rtf:F1}");
        return 0;
    }
    case "mem":
    {
        // Private bytes, not working set: Windows trims the working set of an idle
        // process, which made the in-app watchdog numbers swing between 50 MB and 1.3 GB.
        static long PrivateMb()
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return self.PrivateMemorySize64 / (1024 * 1024);
        }

        Console.WriteLine($"after load          private {PrivateMb(),5} MB");
        foreach (var (label, seconds) in new[] { ("10 s", 10), ("60 s", 60), ("120 s", 120), ("10 s again", 10) })
        {
            var result = engine.Transcribe(RtfBenchmark.GenerateTestSignal(seconds));
            Console.WriteLine($"after {label,-13} private {PrivateMb(),5} MB   ({result.ElapsedSeconds:F2} s, RTF {result.Rtf:F1})");
        }
        return 0;
    }
    case "transcribe":
    {
        var samples = ReadWav(args[1]);
        var result = engine.Transcribe(samples);
        Console.WriteLine($"Audio: {result.AudioSeconds:F1} s, elapsed: {result.ElapsedSeconds:F2} s, RTF: {result.Rtf:F1}");
        Console.WriteLine($"Text: {TextCleaner.Clean(result.Text)}");
        return 0;
    }
    default:
        Console.Error.WriteLine($"Unknown command '{command}'");
        return 1;
}

static float[] ReadWav(string path)
{
    using var reader = new AudioFileReader(path); // gives float32 at source rate/channels
    var samples = new List<float>();
    var buffer = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels];
    int read;
    while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
    {
        int channels = reader.WaveFormat.Channels;
        for (int i = 0; i < read; i += channels)
        {
            float mono = 0;
            for (int c = 0; c < channels && i + c < read; c++) mono += buffer[i + c];
            samples.Add(mono / channels);
        }
    }
    return AudioRecorder.Resample(samples, reader.WaveFormat.SampleRate, AsrEngine.SampleRate);
}

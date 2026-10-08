using System.IO;

namespace Govorun.Core.Asr;

/// <summary>Locations of the GigaAM v3 ONNX model files.</summary>
public sealed record ModelPaths(string Preprocessor, string Model, string Vocab)
{
    /// <summary>
    /// Resolves model files in <paramref name="directory"/>. Prefers the int8-quantized
    /// model (smaller, shipped in the installer) and falls back to fp32.
    /// </summary>
    public static ModelPaths Locate(string directory)
    {
        string Require(string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) throw new FileNotFoundException($"'{path}' not found.");
            return path;
        }

        var int8 = Path.Combine(directory, "v3_e2e_ctc.int8.onnx");
        var model = File.Exists(int8) ? int8 : Require("v3_e2e_ctc.onnx");

        // The log-mel extractor is a separate graph with the same waveform-in,
        // features-out contract; onnx-asr builds and ships it (MIT).
        return new ModelPaths(Require("gigaam_v3.onnx"), model, Require("v3_e2e_ctc_vocab.txt"));
    }

    /// <summary>Default model directory: "models" next to the executable.</summary>
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, "models");
}

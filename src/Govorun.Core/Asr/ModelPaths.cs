using System.IO;

namespace Govorun.Core.Asr;

/// <summary>Locations of the Parakeet TDT ONNX model files.</summary>
public sealed record ModelPaths(string Preprocessor, string Encoder, string DecoderJoint, string Vocab)
{
    /// <summary>
    /// Resolves model files in <paramref name="directory"/>. Prefers int8-quantized
    /// variants (smaller, shipped in the installer) and falls back to fp32.
    /// </summary>
    public static ModelPaths Locate(string directory)
    {
        string Pick(string baseName)
        {
            var int8 = Path.Combine(directory, $"{baseName}.int8.onnx");
            var fp32 = Path.Combine(directory, $"{baseName}.onnx");
            if (File.Exists(int8)) return int8;
            if (File.Exists(fp32)) return fp32;
            throw new FileNotFoundException($"Model file '{baseName}[.int8].onnx' not found in '{directory}'.");
        }

        var preprocessor = Path.Combine(directory, "nemo128.onnx");
        var vocab = Path.Combine(directory, "vocab.txt");
        if (!File.Exists(preprocessor)) throw new FileNotFoundException($"'{preprocessor}' not found.");
        if (!File.Exists(vocab)) throw new FileNotFoundException($"'{vocab}' not found.");

        return new ModelPaths(preprocessor, Pick("encoder-model"), Pick("decoder_joint-model"), vocab);
    }

    /// <summary>Default model directory: "models" next to the executable.</summary>
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, "models");
}

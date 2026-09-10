using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Govorun.Core.Asr;

/// <summary>
/// SentencePiece-style vocabulary of the NeMo export: one "token id" pair per line,
/// "▁" marks a word boundary (becomes a space), "&lt;blk&gt;" is the RNNT blank.
/// </summary>
public sealed partial class Vocabulary
{
    // Same joining rule as onnx-asr: drop leading whitespace and word-internal
    // spaces, keep a single space at word boundaries.
    [GeneratedRegex(@"\A\s|\s\B|(\s)\b")]
    private static partial Regex DecodeSpacePattern();

    private readonly string[] _tokens;

    public int Count => _tokens.Length;
    public int BlankIndex { get; }

    public Vocabulary(string vocabFile)
    {
        var map = new Dictionary<int, string>();
        foreach (var line in File.ReadLines(vocabFile, Encoding.UTF8))
        {
            if (line.Length == 0) continue;
            var sep = line.LastIndexOf(' ');
            var token = line[..sep].Replace('▁', ' ');
            var id = int.Parse(line[(sep + 1)..]);
            map[id] = token;
        }

        _tokens = new string[map.Count];
        foreach (var (id, token) in map) _tokens[id] = token;
        BlankIndex = Array.IndexOf(_tokens, "<blk>");
        if (BlankIndex < 0) BlankIndex = _tokens.Length - 1;
    }

    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids) sb.Append(_tokens[id]);
        return DecodeSpacePattern().Replace(sb.ToString(), m => m.Groups[1].Success ? " " : "");
    }
}

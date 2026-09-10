using System.Text.RegularExpressions;

namespace Govorun.Core.Text;

/// <summary>
/// Post-processing of model output: strips CJK hallucinations, collapses
/// runaway word/phrase repetitions, and normalizes whitespace and punctuation spacing.
/// </summary>
public static partial class TextCleaner
{
    // CJK ideographs, kana, hangul, fullwidth forms — Parakeet V3 is trained on
    // European languages; these only ever appear as hallucinations.
    [GeneratedRegex(@"[ᄀ-ᇿ⺀-〿぀-ヿ㄰-㆏ㇰ-䶿一-鿿ꀀ-꓏가-힯豈-﫿＀-￯]+")]
    private static partial Regex CjkPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpacePattern();

    [GeneratedRegex(@"\s+([,.!?;:…])")]
    private static partial Regex SpaceBeforePunctuationPattern();

    private const int MaxRepeats = 3;

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        text = CjkPattern().Replace(text, " ");
        text = CollapseRepeats(text);
        text = SpaceBeforePunctuationPattern().Replace(text, "$1");
        text = MultiSpacePattern().Replace(text, " ");
        return text.Trim();
    }

    /// <summary>
    /// Collapses more than <see cref="MaxRepeats"/> consecutive repetitions of the same
    /// word or phrase (up to 5 words long) down to a single occurrence — the classic
    /// ASR hallucination loop.
    /// </summary>
    internal static string CollapseRepeats(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>(words.Length);
        int i = 0;
        while (i < words.Length)
        {
            bool collapsed = false;
            for (int phraseLen = 1; phraseLen <= 5 && !collapsed; phraseLen++)
            {
                if (i + phraseLen > words.Length) break;
                int repeats = 1;
                while (i + (repeats + 1) * phraseLen <= words.Length &&
                       PhraseEquals(words, i, i + repeats * phraseLen, phraseLen))
                    repeats++;

                if (repeats > MaxRepeats)
                {
                    for (int k = 0; k < phraseLen; k++) result.Add(words[i + k]);
                    i += repeats * phraseLen;
                    collapsed = true;
                }
            }
            if (!collapsed)
            {
                result.Add(words[i]);
                i++;
            }
        }
        return string.Join(' ', result);
    }

    private static bool PhraseEquals(string[] words, int a, int b, int len)
    {
        for (int k = 0; k < len; k++)
            if (!string.Equals(words[a + k], words[b + k], StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}

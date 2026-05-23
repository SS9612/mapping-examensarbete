using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Mapping_LIA.Services.Normalization;

public sealed class TextNormalizer : ITextNormalizer
{
    private readonly NormalizationOptions _opt;
    private readonly HashSet<string> _stopwords;
    private readonly string[] _keepVersionFor;

    private static readonly Regex Splitters = new(@"[/|,]+|[\(\)\[\]{}]", RegexOptions.Compiled);
    private static readonly Regex NonTech = new(@"[^\p{L}0-9\.\+#\-\s]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex MultiDots = new(@"\.{2,}", RegexOptions.Compiled);
    private static readonly Regex MultiHyphens = new(@"-{2,}", RegexOptions.Compiled);
    private static readonly Regex VersionToken = new(@"\b(?:(?:v(?:er(?:sion)?)?\s*)?\d+(?:\.\d+){0,2})\b", RegexOptions.Compiled);
    private static readonly Regex Tokenizer = new(@"\s+", RegexOptions.Compiled);

    public TextNormalizer(IOptions<NormalizationOptions> opt)
    {
        _opt = opt.Value;

        _stopwords = new HashSet<string>(_opt.Stopwords ?? Array.Empty<string>(),
            StringComparer.Ordinal);

        _keepVersionFor = (_opt.KeepVersionFor ?? Array.Empty<string>())
            .Select(k => k.ToLowerInvariant())
            .ToArray();
    }
    // Normalizes text by cleaning up special characters, applying aliases and removing stopwords
    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // handles things like "ﬁ" -> "fi"
        var s = input.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();

        s = Splitters.Replace(s, " ");
        s = NonTech.Replace(s, " ");

        s = MultiDots.Replace(s, ".");
        s = MultiHyphens.Replace(s, "-");

        s = Spaces.Replace(s, " ").Trim();

        if (_opt.DropStandaloneVersions)
            s = DropStandaloneVersionsSmart(s, _keepVersionFor);

        s = ApplyAliases(s, _opt.Aliases);
        s = RemoveStopwords(s, _stopwords);

        s = Spaces.Replace(s, " ").Trim();

        return s;
    }

    // Normalizes text for search by folding diacritics and special characters
    public string NormalizeForSearch(string input)
    {
        var s = Normalize(input);
        if (!_opt.FoldDiacritics) return s;

        // decomposes characters (é -> e + ´), then recomposes (e + ´ -> é)
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(capacity: normalized.Length);

        foreach (var ch in normalized)
        {
            var uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark) // Keep base characters, drop diacritics
                sb.Append(ch);
        }

        var folded = sb.ToString().Normalize(NormalizationForm.FormC);

        return Spaces.Replace(folded, " ").Trim();
    }

    // Helpers 

    // Applies alias mapping to whole text and individual tokens
    private static string ApplyAliases(string text, IReadOnlyDictionary<string, string> aliases)
    {
        if (aliases.TryGetValue(text, out var whole))
            text = whole;

        var tokens = Tokenizer.Split(text);
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (aliases.TryGetValue(t, out var mapped))
                tokens[i] = mapped;
        }

        text = string.Join(' ', tokens);
        return Spaces.Replace(text, " ").Trim();
    }
    // Removes stopwords when they appear as separate tokens
    private static string RemoveStopwords(string text, IReadOnlySet<string> stopwords)
    {
        if (stopwords.Count == 0) return text;

        var tokens = Tokenizer.Split(text);
        var kept = new List<string>(tokens.Length);

        foreach (var t in tokens)
        {
            if (!stopwords.Contains(t))
                kept.Add(t);
        }

        return string.Join(' ', kept);
    }
    // Removes version tokens 
    private static string DropStandaloneVersionsSmart(string text, string[] keepFor)
    {
        if (keepFor.Length > 0 && keepFor.Any(k => text.Contains(k, StringComparison.Ordinal)))
            return text;

        return VersionToken.Replace(text, "").Trim();
    }
}
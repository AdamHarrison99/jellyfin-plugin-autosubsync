using System.Globalization;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Reduces any subtitle language code a user or a container might carry to one canonical form.
public static class LanguageCodes
{
    // ISO 639-2/B bibliographic codes, as emitted by ffmpeg and many containers.
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.Ordinal)
    {
        ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["chi"] = "zho",
        ["cze"] = "ces", ["dut"] = "nld", ["fre"] = "fra", ["geo"] = "kat", ["ger"] = "deu",
        ["gre"] = "ell", ["ice"] = "isl", ["mac"] = "mkd", ["mao"] = "mri", ["may"] = "msa",
        ["per"] = "fas", ["rum"] = "ron", ["slo"] = "slk", ["tib"] = "bod", ["wel"] = "cym"
    };

    // Two-letter forms, plus the country codes users reach for when they guess.
    private static readonly Dictionary<string, string> TwoLetter = new(StringComparer.Ordinal)
    {
        ["ar"] = "ara", ["bg"] = "bul", ["ca"] = "cat", ["cs"] = "ces", ["da"] = "dan",
        ["de"] = "deu", ["el"] = "ell", ["en"] = "eng", ["es"] = "spa", ["et"] = "est",
        ["fa"] = "fas", ["fi"] = "fin", ["fr"] = "fra", ["he"] = "heb", ["hi"] = "hin",
        ["hr"] = "hrv", ["hu"] = "hun", ["id"] = "ind", ["is"] = "isl", ["it"] = "ita",
        ["ja"] = "jpn", ["ko"] = "kor", ["lt"] = "lit", ["lv"] = "lav", ["ms"] = "msa",
        ["nb"] = "nob", ["nl"] = "nld", ["no"] = "nor", ["pl"] = "pol", ["pt"] = "por",
        ["ro"] = "ron", ["ru"] = "rus", ["sk"] = "slk", ["sl"] = "slv", ["sr"] = "srp",
        ["sv"] = "swe", ["th"] = "tha", ["tr"] = "tur", ["uk"] = "ukr", ["vi"] = "vie",
        ["zh"] = "zho",

        // ! Not language codes. Users type them anyway.
        ["cn"] = "zho", ["cz"] = "ces", ["dk"] = "dan", ["ee"] = "est", ["gr"] = "ell",
        ["jp"] = "jpn", ["kr"] = "kor", ["ua"] = "ukr", ["rs"] = "srp", ["se"] = "swe"
    };

    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim().ToLowerInvariant();

        // Region qualifiers carry no subtitle meaning here.
        var separator = trimmed.IndexOfAny(['-', '_']);
        if (separator > 0)
        {
            trimmed = trimmed[..separator];
        }

        if (trimmed.Length == 3)
        {
            return Bibliographic.GetValueOrDefault(trimmed, trimmed);
        }

        if (trimmed.Length != 2)
        {
            return trimmed;
        }

        return TwoLetter.TryGetValue(trimmed, out var mapped)
            ? mapped
            : FromCultureInfo(trimmed) ?? trimmed;
    }

    // ! Mirrors Jellyfin's own rule: keep a script-bearing locale, else emit 639-2/T.
    public static string? ForFilename(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();

        return trimmed.Contains('-', StringComparison.Ordinal) ? trimmed : Normalize(trimmed);
    }

    public static bool Matches(IReadOnlyCollection<string> allowList, string? language)
    {
        if (allowList.Count == 0)
        {
            return true;
        }

        var normalized = Normalize(language);
        if (normalized is null)
        {
            return false;
        }

        foreach (var allowed in allowList)
        {
            if (string.Equals(Normalize(allowed), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? FromCultureInfo(string twoLetter)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(twoLetter);
            var three = culture.ThreeLetterISOLanguageName;

            return string.IsNullOrEmpty(three) || three.Length != 3 ? null : three.ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }
}

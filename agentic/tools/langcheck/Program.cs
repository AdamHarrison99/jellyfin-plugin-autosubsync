using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Subtitles;

// Exercises the real LanguageCodes.cs, linked by the csproj. See agentic/ARCHITECTURE.md.
var normalizations = new (string? Input, string? Expected)[]
{
    ("en", "eng"), ("eng", "eng"), ("EN", "eng"), (" en ", "eng"), ("en-US", "eng"),
    ("ja", "jpn"), ("jpn", "jpn"), ("jp", "jpn"),
    ("de", "deu"), ("deu", "deu"), ("ger", "deu"),
    ("fr", "fra"), ("fre", "fra"), ("zh", "zho"), ("chi", "zho"), ("cn", "zho"),
    ("pt-BR", "por"), ("pt_br", "por"), ("ko", "kor"), ("kr", "kor"),
    ("es", "spa"), ("nb", "nob"), ("el", "ell"), ("gre", "ell"),
    (null, null), ("", null), ("   ", null),
    ("qqq", "qqq"), ("klingon", "klingon")
};

// The allowlist and the stream can each use any form; matching is on the canonical one.
var matches = new (string[] AllowList, string? Language, bool Expected)[]
{
    ([], "eng", true),
    ([], null, true),
    (["en"], "eng", true),
    (["eng"], "en", true),
    (["jp"], "jpn", true),
    (["ja"], "jpn", true),
    (["eng", "es"], "spa", true),
    (["ger"], "deu", true),
    (["de"], "ger", true),
    (["en"], "spa", false),
    (["eng"], null, false),
    (["en"], "", false)
};

// Jellyfin keeps a hyphenated locale verbatim and reduces everything else to 639-2/T.
var filenames = new (string? Input, string? Expected)[]
{
    ("en", "eng"), ("eng", "eng"), ("ger", "deu"), ("jp", "jpn"),
    ("zh-Hans", "zh-Hans"), ("zh-Hant", "zh-Hant"), ("pt-BR", "pt-BR"),
    ("und", "und"), (null, null), ("", null)
};

// Tessdata names, which are 639-2/T except where a script splits a language. ! The unsuffixed
// model is Cyrillic for Serbian and Latin for Azerbaijani and Uzbek. Null = no language flag.
var tessdata = new (string? Input, string? Expected)[]
{
    ("eng", "eng"), ("en", "eng"), ("de", "deu"), ("ger", "deu"), ("qqq", "qqq"),
    ("zh-Hant", "chi_tra"), ("chi-Hant", "chi_tra"), ("ZH-HANT", "chi_tra"),
    ("zh-Hans", "chi_sim"), ("zh_hans", "chi_sim"), ("zh-Hans-CN", "chi_sim"),
    ("zh", "chi_sim"), ("zho", "chi_sim"), ("chi", "chi_sim"), ("cn", "chi_sim"),
    ("sr-Latn", "srp_latn"), ("sr_latn", "srp_latn"), ("srp-Latn", "srp_latn"),
    ("SR-LATN", "srp_latn"), ("sr", "srp"), ("sr-Cyrl", "srp"),
    ("aze-Cyrl", "aze_cyrl"), ("uzb-Cyrl", "uzb_cyrl"), ("aze", "aze"), ("uzb", "uzb"),
    ("no", "nor"), ("nb", "nor"), ("nob", "nor"), ("nno", "nor"),
    ("kur", "kmr"), ("tgl", "fil"),
    ("und", null), ("mis", null), ("mul", null), ("zxx", null),
    (null, null), ("", null), ("   ", null)
};

var failures = 0;

foreach (var (input, expected) in tessdata)
{
    var actual = TesseractLanguage.Resolve(input);
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"FAIL  Resolve({Show(input)}) = {Show(actual)}, want {Show(expected)}");
        failures++;
    }
}


foreach (var (input, expected) in normalizations)
{
    var actual = LanguageCodes.Normalize(input);
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"FAIL  Normalize({Show(input)}) = {Show(actual)}, want {Show(expected)}");
        failures++;
    }
}

foreach (var (input, expected) in filenames)
{
    var actual = LanguageCodes.ForFilename(input);
    if (!string.Equals(actual, expected, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"FAIL  ForFilename({Show(input)}) = {Show(actual)}, want {Show(expected)}");
        failures++;
    }
}

foreach (var (allowList, language, expected) in matches)
{
    var actual = LanguageCodes.Matches(allowList, language);
    if (actual != expected)
    {
        Console.Error.WriteLine(
            $"FAIL  Matches([{string.Join(", ", allowList)}], {Show(language)}) = {actual}, want {expected}");
        failures++;
    }
}

if (failures > 0)
{
    Console.Error.WriteLine($"\nlangcheck: {failures} failure(s)");
    return 1;
}

Console.WriteLine(
    $"langcheck: {normalizations.Length} normalizations, {filenames.Length} filename tags, "
    + $"{matches.Length} matches, {tessdata.Length} tessdata names, all pass");
return 0;

static string Show(string? value) => value is null ? "null" : $"\"{value}\"";

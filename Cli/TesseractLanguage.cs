using Jellyfin.Plugin.AutoSubSync.Subtitles;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Turns a subtitle language tag into the tessdata name the OCR reader loads.
public static class TesseractLanguage
{
    // ! The reader knows none of these codes, and zho names no model under any script.
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["nob"] = "nor", ["nno"] = "nor", ["kur"] = "kmr", ["tgl"] = "fil", ["zho"] = "chi_sim"
    };

    // ! No pattern to follow. The unsuffixed model is Cyrillic for Serbian, Latin for the rest.
    private static readonly Dictionary<(string Code, string Script), string> Scripts = new()
    {
        [("zho", "hans")] = "chi_sim",
        [("zho", "hant")] = "chi_tra",
        [("srp", "latn")] = "srp_latn",
        [("aze", "cyrl")] = "aze_cyrl",
        [("uzb", "cyrl")] = "uzb_cyrl"
    };

    // ! Placeholders, not languages. Naming one asks the reader for a model that cannot exist.
    private static readonly HashSet<string> Untagged =
        new(StringComparer.Ordinal) { "und", "mis", "mul", "zxx" };

    public static string? Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var parts = language.Trim().ToLowerInvariant()
            .Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);

        if (ScriptedName(parts) is { } script)
        {
            return script;
        }

        if (LanguageCodes.Normalize(language) is not { } code)
        {
            return null;
        }

        return Aliases.TryGetValue(code, out var tessdata) ? tessdata
            : Untagged.Contains(code) ? null
            : code;
    }

    // ! Only the tag carries the script, so this runs before the code is normalized away.
    private static string? ScriptedName(string[] parts)
    {
        if (parts.Length < 2 || LanguageCodes.Normalize(parts[0]) is not { } code)
        {
            return null;
        }

        foreach (var part in parts)
        {
            if (Scripts.TryGetValue((code, part), out var name))
            {
                return name;
            }
        }

        return null;
    }
}

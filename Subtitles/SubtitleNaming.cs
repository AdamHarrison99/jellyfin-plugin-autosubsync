namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Builds and recognizes Jellyfin external-subtitle filenames.
public static class SubtitleNaming
{
    private static readonly char[] InvalidNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private const int MaxCollisionAttempts = 10;

    private const int MaxVariantLength = 40;

    public static string BuildSidecarPath(
        string videoPath,
        string? language,
        bool isForced,
        bool isHearingImpaired,
        string markerSuffix,
        string extension = ".srt",
        string? variant = null)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? string.Empty;

        // Jellyfin matches sidecars by filename stem, not by item display name.
        var stem = Path.GetFileNameWithoutExtension(videoPath);

        var segments = new List<string> { Sanitize(stem) };

        // ! A token Jellyfin cannot resolve becomes part of the title, not the language.
        var tag = LanguageCodes.ForFilename(language);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            segments.Add(Sanitize(tag));
        }

        if (isForced)
        {
            segments.Add("forced");
        }

        if (isHearingImpaired)
        {
            segments.Add("sdh");
        }

        // ! Only set when one item carries several tracks of the same language; omitting it
        //   where it is needed makes the second track overwrite the first.
        if (!string.IsNullOrWhiteSpace(variant))
        {
            var token = Sanitize(variant);
            if (token.Length > MaxVariantLength)
            {
                token = token[..MaxVariantLength].TrimEnd();
            }

            if (token.Length > 0)
            {
                segments.Add(token);
            }
        }

        segments.Add(Sanitize(markerSuffix));

        return Path.Combine(directory, string.Join('.', segments) + extension);
    }

    // Returns null when every candidate is taken.
    public static string? ResolveCollision(string desiredPath, string markerSuffix)
    {
        if (!File.Exists(desiredPath) || IsPluginOutput(desiredPath, markerSuffix))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var i = 2; i <= MaxCollisionAttempts; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}.{i}{extension}");
            if (!File.Exists(candidate) || IsPluginOutput(candidate, markerSuffix))
            {
                return candidate;
            }
        }

        return null;
    }

    // ! Discovery relies on this to skip plugin output.
    public static bool IsPluginOutput(string path, string markerSuffix)
    {
        if (string.IsNullOrWhiteSpace(markerSuffix))
        {
            return false;
        }

        // Matches a dotted segment only.
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Contains('.' + markerSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string value)
    {
        var cleaned = new string(value.Where(c => !InvalidNameChars.Contains(c)).ToArray());
        return cleaned.Trim().Trim('.');
    }
}

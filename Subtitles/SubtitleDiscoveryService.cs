using System.Globalization;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Turns a library item into the subtitle tracks worth acting on, one per slot.
public class SubtitleDiscoveryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt"
    };

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ISubtitleExtractor _extractor;
    private readonly ILogger<SubtitleDiscoveryService> _logger;

    public SubtitleDiscoveryService(
        IMediaSourceManager mediaSourceManager,
        ISubtitleExtractor extractor,
        ILogger<SubtitleDiscoveryService> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _extractor = extractor;
        _logger = logger;
    }

    private sealed record Candidate(SubtitleTarget Target, SubtitleSourceRank Rank, bool IsExternal);

    public IReadOnlyList<SubtitleTarget> Discover(BaseItem item, PluginConfiguration config)
    {
        var targets = new List<SubtitleTarget>();

        if (string.IsNullOrEmpty(item.Path))
        {
            return targets;
        }

        var streams = _mediaSourceManager
            .GetMediaStreams(item.Id)
            .Where(s => s.Type == MediaStreamType.Subtitle);

        var candidates = new List<Candidate>();

        // ! One candidate per file and VobSub stream. Jellyfin can name the same sidecar twice,
        //   and a VobSub pair arrives as two streams that resolve to one payload.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var stream in streams)
        {
            foreach (var candidate in BuildCandidates(item, stream, config))
            {
                if (SeenKey(candidate.Target) is { } key && !seen.Add(key))
                {
                    _logger.LogDebug(
                        "{Item}: {Path} was offered more than once",
                        item.Name,
                        candidate.Target.SubtitlePath);
                    continue;
                }

                if (!IsProcessable(candidate, config))
                {
                    _logger.LogDebug(
                        "{Item}: skipping {Origin} track, disabled by configuration",
                        item.Name,
                        candidate.Target.Origin);
                    continue;
                }

                candidates.Add(candidate);
            }
        }

        if (config.SkipEmbeddedWhenExternalExists)
        {
            DropCoveredEmbedded(candidates, item);
        }

        AssignVariants(candidates);

        // Cheapest sources first, so text work completes before any OCR starts.
        foreach (var candidate in candidates.OrderBy(c => c.Rank))
        {
            targets.Add(candidate.Target);
        }

        return targets;
    }

    private static bool IsProcessable(Candidate candidate, PluginConfiguration config)
        => candidate.IsExternal ? config.ProcessExternalSubtitles : config.ProcessEmbeddedSubtitles;

    // ! Opt-in, and it discards signs-and-songs tracks along with the rest.
    private void DropCoveredEmbedded(List<Candidate> candidates, BaseItem item)
    {
        var covered = candidates
            .Where(c => c.IsExternal && c.Target.UnsupportedReason is null)
            .Select(c => LanguageKey(c.Target.Language))
            .ToHashSet(StringComparer.Ordinal);

        if (covered.Count == 0)
        {
            return;
        }

        candidates.RemoveAll(c =>
        {
            if (c.IsExternal || !covered.Contains(LanguageKey(c.Target.Language)))
            {
                return false;
            }

            _logger.LogDebug(
                "{Item}: skipping embedded track {Index}, an external {Language} subtitle covers it",
                item.Name,
                c.Target.StreamIndex,
                c.Target.Language ?? "unlabelled");
            return true;
        });
    }

    private static string LanguageKey(string? language)
        => LanguageCodes.Normalize(language) ?? string.Empty;

    // ! Two tracks sharing a language build the same sidecar name; without a variant the
    //   second overwrites the first.
    private static void AssignVariants(List<Candidate> candidates)
    {
        var groups = candidates.GroupBy(c => new SubtitleSlot(
            LanguageKey(c.Target.Language),
            c.Target.IsForced,
            c.Target.IsHearingImpaired));

        foreach (var group in groups)
        {
            if (group.Count() < 2)
            {
                continue;
            }

            foreach (var candidate in group)
            {
                candidate.Target.Variant = VariantFor(candidate.Target);
            }
        }
    }

    internal static string VariantFor(SubtitleTarget target)
    {
        // ! Ahead of the title. Every stream of one index carries the container's single title,
        //   so a title-first variant names them all the same file.
        if (target.VobSubStream is int vobSubStream)
        {
            return "vobsub" + vobSubStream.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(target.Title))
        {
            return target.Title;
        }

        return target.StreamIndex is int index
            ? "track" + index.ToString(CultureInfo.InvariantCulture)
            : Path.GetFileNameWithoutExtension(target.SubtitlePath ?? string.Empty);
    }

    // One stream of a VobSub index is its own unit of work, so one file can yield several.
    private static string? SeenKey(SubtitleTarget target)
        => target.SubtitlePath is { } path
            ? path + "\0" + (target.VobSubStream?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
            : null;

    private IEnumerable<Candidate> BuildCandidates(BaseItem item, MediaStream stream, PluginConfiguration config)
    {
        if (!stream.IsExternal)
        {
            if (PassesLanguageFilter(stream.Language, config))
            {
                yield return BuildEmbeddedCandidate(item, stream, config);
            }

            yield break;
        }

        if (string.IsNullOrEmpty(stream.Path))
        {
            yield break;
        }

        var path = ResolveSidecarPath(stream.Path);

        // Never re-sync our own output, and never let it occupy a slot.
        if (SubtitleNaming.IsPluginOutput(path, config.MarkerSuffix))
        {
            yield break;
        }

        var declared = DeclaredVobSubStreams(path);

        if (declared.Count == 0)
        {
            if (PassesLanguageFilter(stream.Language, config))
            {
                var single = BuildExternalCandidate(item, stream, config, path);
                if (single is not null)
                {
                    yield return single;
                }
            }

            yield break;
        }

        // ! Filter on what the index declares, never on the container's one language. Jellyfin
        //   reports a single language for the pair, which would drop every other stream unseen.
        foreach (var track in declared)
        {
            if (PassesLanguageFilter(track.Language, config))
            {
                yield return BuildVobSubCandidate(item, stream, config, path, track);
            }
        }
    }

    // The streams a paired index declares, and only where it declares more than one.
    private static IReadOnlyList<VobSubTrack> DeclaredVobSubStreams(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".sub", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var index = VobSubIndex.IndexFor(path);
        if (!File.Exists(index))
        {
            return [];
        }

        var tracks = VobSubIndex.Read(index);
        return tracks.Count > 1 ? tracks : [];
    }

    private static Candidate BuildVobSubCandidate(
        BaseItem item,
        MediaStream stream,
        PluginConfiguration config,
        string path,
        VobSubTrack track)
    {
        var target = new SubtitleTarget
        {
            ItemId = item.Id,
            ItemName = item.Name,
            VideoPath = item.Path,
            Origin = SubtitleOrigin.External,
            SubtitlePath = path,
            Language = track.Language,
            Codec = stream.Codec,
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            Title = stream.Title,
            VobSubStream = track.Index,
            Key = SubtitleTarget.ExternalStreamKey(item.Path, path, track.Index)
        };

        MarkImageTrack(target, "VobSub", config);
        return new Candidate(target, SubtitleSourceRank.ExternalImage, IsExternal: true);
    }

    private Candidate? BuildExternalCandidate(
        BaseItem item,
        MediaStream stream,
        PluginConfiguration config,
        string path)
    {
        var target = new SubtitleTarget
        {
            ItemId = item.Id,
            ItemName = item.Name,
            VideoPath = item.Path,
            Origin = SubtitleOrigin.External,
            SubtitlePath = path,
            Language = stream.Language,
            Codec = stream.Codec,
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            Title = stream.Title,
            Key = SubtitleTarget.ExternalKey(item.Path, path)
        };

        var extension = Path.GetExtension(path);
        var imageLabel = ImageSidecarLabel(path, extension);
        var isImage = imageLabel is not null;

        // ! Image first. A bitmap sidecar must never reach the cue check or the sync engine.
        if (imageLabel is not null)
        {
            MarkImageTrack(target, imageLabel, config);
        }
        else if (!SupportedExtensions.Contains(extension))
        {
            target.UnsupportedReason =
                $"Unsupported: the sync engine does not read {extension} subtitles.";
        }
        else if (!SubtitleContent.HasCues(path))
        {
            _logger.LogDebug("{Item}: skipping {Path}, it holds no cues", item.Name, path);
            return null;
        }

        var rank = isImage ? SubtitleSourceRank.ExternalImage : SubtitleSourceRank.ExternalText;
        return new Candidate(target, rank, IsExternal: true);
    }

    // ! A VobSub pair is one track, and only its payload half carries bitmaps to OCR.
    //   Jellyfin names either half, so both have to land on the same file.
    internal static string ResolveSidecarPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".idx", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var payload = Path.ChangeExtension(path, ".sub");
        return File.Exists(payload) ? payload : path;
    }

    // ! An image track only stops being unsupported when OCR is enabled; without that the
    //   Convert stage never runs and the sync engine is handed a bitmap.
    private static void MarkImageTrack(SubtitleTarget target, string label, PluginConfiguration config)
    {
        if (config.ConvertImageSubtitles)
        {
            target.RequiresOcr = true;
            return;
        }

        target.UnsupportedReason = $"{label} is image-based; enable OCR to convert it to text.";
    }

    private Candidate BuildEmbeddedCandidate(BaseItem item, MediaStream stream, PluginConfiguration config)
    {
        var target = new SubtitleTarget
        {
            ItemId = item.Id,
            ItemName = item.Name,
            VideoPath = item.Path,
            Origin = SubtitleOrigin.Embedded,
            StreamIndex = stream.Index,
            Language = stream.Language,
            Codec = stream.Codec,
            IsForced = stream.IsForced,
            IsHearingImpaired = stream.IsHearingImpaired,
            Title = stream.Title,
            Key = SubtitleTarget.EmbeddedKey(stream.Index, stream.Codec)
        };

        var isText = _extractor.IsExtractableCodec(stream.Codec);

        if (!isText)
        {
            MarkImageTrack(target, stream.Codec ?? "This track", config);
        }

        var rank = isText ? SubtitleSourceRank.EmbeddedText : SubtitleSourceRank.EmbeddedImage;
        return new Candidate(target, rank, IsExternal: false);
    }

    // Names the bitmap format of a sidecar, or null when it holds text.
    private static string? ImageSidecarLabel(string subtitlePath, string extension)
    {
        if (string.Equals(extension, ".sup", StringComparison.OrdinalIgnoreCase))
        {
            return "PGS";
        }

        // ".sub" is MicroDVD text in some releases and VobSub bitmap in others; only the paired
        // ".idx" distinguishes them.
        if (!string.Equals(extension, ".sub", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(Path.ChangeExtension(subtitlePath, ".idx")) ? "VobSub" : null;
    }

    // ! An unidentified track may carry signs and songs for a wanted language; never filter it out.
    private static bool PassesLanguageFilter(string? language, PluginConfiguration config)
        => LanguageCodes.Normalize(language) is null
           || LanguageCodes.Matches(config.LanguageAllowList, language);
}

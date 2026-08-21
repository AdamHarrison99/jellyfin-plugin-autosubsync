using System.Globalization;
using Jellyfin.Plugin.AutoSubSync.Cli;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Services;
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
    private readonly ISubtitleSource _source;
    private readonly ProviderRetirement _retirement;
    private readonly ILogger<SubtitleDiscoveryService> _logger;

    public SubtitleDiscoveryService(
        IMediaSourceManager mediaSourceManager,
        ISubtitleExtractor extractor,
        ISubtitleSource source,
        ProviderRetirement retirement,
        ILogger<SubtitleDiscoveryService> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _extractor = extractor;
        _source = source;
        _retirement = retirement;
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

        // ! Kept whole. The gap test reads this list, never the filtered candidates built from it.
        var streams = _mediaSourceManager
            .GetMediaStreams(item.Id)
            .Where(s => s.Type == MediaStreamType.Subtitle)
            .ToList();

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

        if (!config.ProcessEmbeddedWhenExternalExists)
        {
            SuppressCoveredEmbedded(candidates, item);
        }

        if (!config.RunOcrWhenTextExists)
        {
            SuppressOcrCoveredByText(candidates, item);
        }

        AssignVariants(candidates);

        // ! After the suppression passes. An acquire target answers to none of them.
        candidates.AddRange(BuildAcquireCandidates(item, streams, config));

        // Cheapest sources first, so text work completes before any OCR starts.
        foreach (var candidate in candidates.OrderBy(c => c.Rank))
        {
            targets.Add(candidate.Target);
        }

        return targets;
    }

    // One target per wanted language the item has nothing in.
    private List<Candidate> BuildAcquireCandidates(
        BaseItem item,
        IReadOnlyList<MediaStream> streams,
        PluginConfiguration config)
    {
        if (Gaps(streams, config) is not { } gaps)
        {
            _logger.LogDebug(
                "{Item}: a subtitle here names no language, so no language can be called missing",
                item.Name);
            return [];
        }

        if (gaps.Count == 0)
        {
            return [];
        }

        // ! Before a single target is made. A server with no downloader would fill the panel,
        //   and so would a sweep continuing after every provider stopped answering.
        if (_retirement.Live(_source.Downloaders(item.Id, config)).Count == 0)
        {
            _logger.LogDebug("{Item}: no subtitle downloader is available", item.Name);
            return [];
        }

        return gaps.Select(language => BuildAcquireCandidate(item, language)).ToList();
    }

    private static Candidate BuildAcquireCandidate(BaseItem item, string language)
    {
        var target = new SubtitleTarget
        {
            ItemId = item.Id,
            ItemName = item.Name,
            VideoPath = item.Path,
            Origin = SubtitleOrigin.Acquired,
            Language = language,
            Key = SubtitleTarget.AcquireKey(language)
        };

        // ! Providers search by media path. A disc image or folder rip returns nothing, always.
        if (item is Video video && video.VideoType != VideoType.VideoFile)
        {
            target.UnsupportedReason =
                "Subtitle providers cannot search for a disc image or a disc folder rip.";
        }

        return new Candidate(target, SubtitleSourceRank.Acquire, IsExternal: true);
    }

    // Wanted languages this item has nothing in. Null where a subtitle names no language at all.
    internal static List<string>? Gaps(IReadOnlyList<MediaStream> streams, PluginConfiguration config)
    {
        if (!config.AcquireMissingSubtitles)
        {
            return [];
        }

        var wanted = WantedLanguages(config);
        if (wanted.Count == 0)
        {
            return [];
        }

        return Served(streams, config) is { } served
            ? wanted.Where(language => !served.Contains(language)).ToList()
            : null;
    }

    // Languages already served, or null where a subtitle names no language at all.
    private static HashSet<string>? Served(IReadOnlyList<MediaStream> streams, PluginConfiguration config)
    {
        var served = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stream in streams)
        {
            // ! The opt-out, and it is off by default. Most items without a sidecar carry a track.
            if (!stream.IsExternal && config.AcquireWhenEmbeddedExists)
            {
                continue;
            }

            // ! A track nobody labelled could be any language, so nothing can be proved missing.
            if (LanguageCodes.Normalize(stream.Language) is not { } key)
            {
                return null;
            }

            served.Add(key);
        }

        return served;
    }

    // The allow list, canonical and in the order it was typed. Empty leaves the feature inert.
    internal static List<string> WantedLanguages(PluginConfiguration config)
    {
        var wanted = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in config.LanguageAllowList)
        {
            if (LanguageCodes.Normalize(entry) is { } code && seen.Add(code))
            {
                wanted.Add(code);
            }
        }

        return wanted;
    }

    private static bool IsProcessable(Candidate candidate, PluginConfiguration config)
        => candidate.IsExternal ? config.ProcessExternalSubtitles : config.ProcessEmbeddedSubtitles;

    // ! Opt-in, and it sets aside signs-and-songs tracks along with the rest.
    private void SuppressCoveredEmbedded(List<Candidate> candidates, BaseItem item)
    {
        var covered = candidates
            .Where(c => c.IsExternal && c.Target.UnsupportedReason is null)
            .Select(c => LanguageKey(c.Target.Language))
            .ToHashSet(StringComparer.Ordinal);

        if (covered.Count == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            if (candidate.IsExternal
                || candidate.Target.UnsupportedReason is not null
                || !covered.Contains(LanguageKey(candidate.Target.Language)))
            {
                continue;
            }

            candidate.Target.UnsupportedReason =
                "An external subtitle in this language already covers this embedded track.";
            candidate.Target.SetAside = true;

            _logger.LogDebug(
                "{Item}: setting aside embedded track {Index}, an external {Language} subtitle covers it",
                item.Name,
                candidate.Target.StreamIndex,
                candidate.Target.Language ?? "unlabelled");
        }
    }

    private static string LanguageKey(string? language)
        => LanguageCodes.Normalize(language) ?? string.Empty;

    // ! Slot, not language. A signs track carries the language of the full one and has to survive.
    private void SuppressOcrCoveredByText(List<Candidate> candidates, BaseItem item)
    {
        var all = candidates.Select(c => c.Target).ToList();

        foreach (var candidate in candidates)
        {
            if (candidate.Target.UnsupportedReason is not null
                || !TextCovers(candidate.Target, all))
            {
                continue;
            }

            candidate.Target.UnsupportedReason =
                "A text subtitle in this language already serves this track, so it was not read.";
            candidate.Target.SetAside = true;

            _logger.LogDebug(
                "{Item}: setting aside the OCR for the {Language} track, a text subtitle serves it",
                item.Name,
                candidate.Target.Language ?? "unlabelled");
        }
    }

    // True when a readable track already serves this bitmap's slot.
    internal static bool TextCovers(SubtitleTarget target, IReadOnlyList<SubtitleTarget> all)
    {
        var slot = SlotOf(target);

        // An unlabelled track names no language, and two of them need not share one.
        if (!target.RequiresOcr || slot.Language.Length == 0)
        {
            return false;
        }

        // ! A title is the only mark a signs track carries when nothing flagged it forced.
        if (!string.IsNullOrWhiteSpace(target.Title) || SharesAnIndex(target, all))
        {
            return false;
        }

        foreach (var other in all)
        {
            if (!other.RequiresOcr && other.UnsupportedReason is null && SlotOf(other) == slot)
            {
                return true;
            }
        }

        return false;
    }

    // ! One MediaStream covers a whole index, so its flags are the same for every stream in it.
    //   Only the language is per-stream, and a forced twin is indistinguishable.
    private static bool SharesAnIndex(SubtitleTarget target, IReadOnlyList<SubtitleTarget> all)
    {
        if (target.VobSubStream is null)
        {
            return false;
        }

        foreach (var other in all)
        {
            if (other.VobSubStream is not null
                && other.VobSubStream != target.VobSubStream
                && string.Equals(other.SubtitlePath, target.SubtitlePath, StringComparison.OrdinalIgnoreCase)
                && SlotOf(other).Language == SlotOf(target).Language)
            {
                return true;
            }
        }

        return false;
    }

    private static SubtitleSlot SlotOf(SubtitleTarget target)
        => new(LanguageKey(target.Language), target.IsForced, target.IsHearingImpaired);

    // ! Two tracks sharing a language build the same sidecar name; without a variant the
    //   second overwrites the first.
    private static void AssignVariants(List<Candidate> candidates)
    {
        var groups = candidates.GroupBy(c => SlotOf(c.Target));

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
            target.UnsupportedReason = SyncEngine.UnsupportedReason(extension);
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

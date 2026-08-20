using Jellyfin.Plugin.AutoSubSync.Models;
using Jellyfin.Plugin.AutoSubSync.Subtitles;

namespace NamingCheck;

// Proves what happens when several tracks share one slot: distinct sidecar names, single-track
// names unchanged, and a bitmap skipped when readable text already serves that slot.
internal static class Program
{
    private const string Video = @"C:\media\Movie (2001)\Movie (2001).mkv";
    private const string Marker = "autosubsync";

    private static int _failures;

    private static int Main()
    {
        SingleTrackNameIsUnchanged();
        VariantSeparatesSameLanguageTracks();
        WithoutVariantSameLanguageTracksCollide();
        BlankVariantIsIgnored();
        VariantIsSanitized();
        LongVariantIsTruncated();
        FlagsPrecedeVariant();
        PluginOutputStillRecognized();
        VobSubPairResolvesToItsPayload();
        VobSubStreamsOfOneIndexGetDistinctNames();
        VobSubVariantIgnoresTheSharedTitle();
        VobSubVariantIsUnreachableForOtherTracks();
        TextCoversABitmapOfTheSameSlot();
        SignsAndSongsSurviveAFullTextTrack();
        AnotherLanguageDoesNotCoverABitmap();
        AnUnreadableTextTrackCoversNothing();
        AnUnlabelledTextTrackCoversNothing();
        ATitledBitmapIsNeverDropped();
        AVobSubStreamSharingAnIndexIsNeverDropped();

        Console.WriteLine(_failures == 0
            ? "namingcheck: all cases passed"
            : $"namingcheck: {_failures} case(s) failed");

        return _failures == 0 ? 0 : 1;
    }

    // A single-track item must keep the name it had before variants existed.
    private static void SingleTrackNameIsUnchanged()
    {
        var path = Build("eng");
        Expect(
            "single track keeps its historical name",
            Path.GetFileName(path),
            "Movie (2001).eng.autosubsync.srt");
    }

    private static void VariantSeparatesSameLanguageTracks()
    {
        var full = Build("eng", variant: "Full Subtitles");
        var signs = Build("eng", variant: "Signs and Songs");

        ExpectTrue("two eng tracks get distinct paths", full != signs);
        Expect(
            "variant sits before the marker",
            Path.GetFileName(signs),
            "Movie (2001).eng.Signs and Songs.autosubsync.srt");
    }

    // The hazard the variant exists to prevent.
    private static void WithoutVariantSameLanguageTracksCollide()
    {
        ExpectTrue(
            "same-language tracks collide when no variant is set",
            Build("eng") == Build("eng"));
    }

    private static void BlankVariantIsIgnored()
    {
        Expect(
            "whitespace variant adds no segment",
            Build("eng", variant: "   "),
            Build("eng"));
    }

    private static void VariantIsSanitized()
    {
        var path = Build("eng", variant: "Signs/Songs:2");
        ExpectTrue(
            "path separators never survive into the variant",
            Path.GetFileName(path) == "Movie (2001).eng.SignsSongs2.autosubsync.srt");
    }

    private static void LongVariantIsTruncated()
    {
        var name = Path.GetFileName(Build("eng", variant: new string('x', 200)));
        ExpectTrue("an over-long variant is truncated", name.Length < 100);
        ExpectTrue("a truncated variant keeps the marker", name.Contains(".autosubsync.srt", StringComparison.Ordinal));
    }

    private static void FlagsPrecedeVariant()
    {
        Expect(
            "forced and sdh come before the variant",
            Path.GetFileName(Build("eng", forced: true, sdh: true, variant: "Signs")),
            "Movie (2001).eng.forced.sdh.Signs.autosubsync.srt");
    }

    // Discovery skips its own output by this check; a variant must not hide it.
    private static void PluginOutputStillRecognized()
    {
        ExpectTrue(
            "a variant filename is still recognized as plugin output",
            SubtitleNaming.IsPluginOutput(Build("eng", variant: "Signs"), Marker));
    }

    // Jellyfin can name either half of a VobSub pair. Only the ".sub" holds anything to OCR.
    private static void VobSubPairResolvesToItsPayload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "namingcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var idx = Path.Combine(dir, "Movie (2001).eng.idx");
            var sub = Path.Combine(dir, "Movie (2001).eng.sub");
            File.WriteAllText(idx, "# VobSub index");
            File.WriteAllText(sub, "bitmap bytes");

            Expect(
                "an .idx with its .sub beside it resolves to the .sub",
                SubtitleDiscoveryService.ResolveSidecarPath(idx),
                sub);

            Expect(
                "a .sub is left alone",
                SubtitleDiscoveryService.ResolveSidecarPath(sub),
                sub);

            var orphan = Path.Combine(dir, "Orphan (1999).eng.idx");
            File.WriteAllText(orphan, "# VobSub index");

            // ! An index with no payload stays unresolved, so it is reported unsupported
            //   rather than sent to OCR as a file with no bitmaps in it.
            Expect(
                "an .idx with no .sub stays an .idx",
                SubtitleDiscoveryService.ResolveSidecarPath(orphan),
                orphan);

            var text = Path.Combine(dir, "Movie (2001).ita.srt");
            Expect("a text sidecar is untouched", SubtitleDiscoveryService.ResolveSidecarPath(text), text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // "Sandakan No. 8" carries two zh streams in one index: same language, same flags, one payload.
    private static void VobSubStreamsOfOneIndexGetDistinctNames()
    {
        var first = Build("zh", variant: SubtitleDiscoveryService.VariantFor(VobSubTarget(1)));
        var second = Build("zh", variant: SubtitleDiscoveryService.VariantFor(VobSubTarget(2)));

        ExpectTrue("two zh streams of one index get distinct paths", first != second);
        Expect(
            "the declared stream index is what separates them",
            Path.GetFileName(first),
            "Movie (2001).zho.vobsub1.autosubsync.srt");
    }

    // ! Jellyfin reports one title for the whole pair, so the title cannot decide the variant.
    private static void VobSubVariantIgnoresTheSharedTitle()
    {
        var first = VobSubTarget(1);
        var second = VobSubTarget(2);
        first.Title = "Chinese";
        second.Title = "Chinese";

        ExpectTrue(
            "a shared title does not collapse two streams onto one name",
            SubtitleDiscoveryService.VariantFor(first) != SubtitleDiscoveryService.VariantFor(second));
    }

    // A single-stream VobSub and every text sidecar must keep naming the way they always did.
    private static void VobSubVariantIsUnreachableForOtherTracks()
    {
        var sidecar = VobSubTarget(1);
        sidecar.VobSubStream = null;
        sidecar.Title = "Chinese";

        Expect("a track with no declared stream keeps its title", SubtitleDiscoveryService.VariantFor(sidecar), "Chinese");

        var embedded = new SubtitleTarget { Origin = SubtitleOrigin.Embedded, VideoPath = Video, StreamIndex = 3 };
        Expect("an embedded track still names its container index", SubtitleDiscoveryService.VariantFor(embedded), "track3");
    }

    // An SRT already serves the slot, so its PGS twin never reaches the OCR.
    private static void TextCoversABitmapOfTheSameSlot()
    {
        var bitmap = Bitmap("eng");
        ExpectTrue(
            "a text track of the same slot covers a bitmap",
            SubtitleDiscoveryService.TextCovers(bitmap, [Text("eng"), bitmap]));
    }

    // ! The case that killed the language-wide rule. Signs carry the language of the full track.
    private static void SignsAndSongsSurviveAFullTextTrack()
    {
        var signs = Bitmap("eng", forced: true);
        ExpectTrue(
            "a forced signs track survives a full text track",
            !SubtitleDiscoveryService.TextCovers(signs, [Text("eng"), signs]));

        var sdh = Bitmap("eng", sdh: true);
        ExpectTrue(
            "a hearing-impaired bitmap survives a plain text track",
            !SubtitleDiscoveryService.TextCovers(sdh, [Text("eng"), sdh]));
    }

    private static void AnotherLanguageDoesNotCoverABitmap()
    {
        var bitmap = Bitmap("zh");
        ExpectTrue(
            "an English text track does not cover a Chinese bitmap",
            !SubtitleDiscoveryService.TextCovers(bitmap, [Text("eng"), bitmap]));
    }

    // A track carrying a reason is one the plugin refused, and it serves nothing.
    private static void AnUnreadableTextTrackCoversNothing()
    {
        var bitmap = Bitmap("eng");
        var refused = Text("eng");
        refused.UnsupportedReason = "Unsupported: the sync engine does not read .txt subtitles.";

        ExpectTrue(
            "a refused text track does not cover a bitmap",
            !SubtitleDiscoveryService.TextCovers(bitmap, [refused, bitmap]));
    }

    // Two unlabelled tracks need not be the same language, so neither covers the other.
    private static void AnUnlabelledTextTrackCoversNothing()
    {
        var bitmap = Bitmap(null);
        ExpectTrue(
            "an unlabelled text track does not cover an unlabelled bitmap",
            !SubtitleDiscoveryService.TextCovers(bitmap, [Text(null), bitmap]));
    }

    // ! Anime ships a full track and a signs track both tagged eng and both non-forced, so the
    //   slot cannot separate them. The title is the only thing that can.
    private static void ATitledBitmapIsNeverDropped()
    {
        var signs = Bitmap("eng");
        signs.Title = "Signs & Songs";

        ExpectTrue(
            "a titled bitmap survives an untitled text track",
            !SubtitleDiscoveryService.TextCovers(signs, [Text("eng"), signs]));
    }

    // ! Every stream of one index shares a MediaStream, so the forced flag cannot be trusted.
    private static void AVobSubStreamSharingAnIndexIsNeverDropped()
    {
        var first = VobSubTarget(1);
        var second = VobSubTarget(2);
        first.Language = "eng";
        second.Language = "eng";
        first.RequiresOcr = true;
        second.RequiresOcr = true;

        ExpectTrue(
            "two eng streams of one index both survive a text track",
            !SubtitleDiscoveryService.TextCovers(first, [Text("eng"), first, second]));

        var only = VobSubTarget(1);
        only.Language = "eng";
        only.RequiresOcr = true;

        ExpectTrue(
            "the only eng stream of an index is still covered",
            SubtitleDiscoveryService.TextCovers(only, [Text("eng"), only]));
    }

    private static SubtitleTarget Text(string? language, bool forced = false, bool sdh = false) => new()
    {
        Origin = SubtitleOrigin.External,
        VideoPath = Video,
        SubtitlePath = @"C:\media\Movie (2001)\Movie (2001).en.srt",
        Language = language,
        IsForced = forced,
        IsHearingImpaired = sdh
    };

    private static SubtitleTarget Bitmap(string? language, bool forced = false, bool sdh = false) => new()
    {
        Origin = SubtitleOrigin.Embedded,
        VideoPath = Video,
        StreamIndex = 3,
        Language = language,
        IsForced = forced,
        IsHearingImpaired = sdh,
        RequiresOcr = true
    };

    private static SubtitleTarget VobSubTarget(int stream) => new()
    {
        Origin = SubtitleOrigin.External,
        VideoPath = Video,
        SubtitlePath = @"C:\media\Movie (2001)\Movie (2001).sub",
        Language = "zh",
        VobSubStream = stream
    };

    private static string Build(
        string? language,
        bool forced = false,
        bool sdh = false,
        string? variant = null)
        => SubtitleNaming.BuildSidecarPath(Video, language, forced, sdh, Marker, ".srt", variant);

    private static void Expect(string what, string actual, string expected)
        => ExpectTrue($"{what} (got '{actual}', wanted '{expected}')", actual == expected);

    private static void ExpectTrue(string what, bool condition)
    {
        if (condition)
        {
            Console.WriteLine($"  ok    {what}");
            return;
        }

        Console.WriteLine($"  FAIL  {what}");
        _failures++;
    }
}

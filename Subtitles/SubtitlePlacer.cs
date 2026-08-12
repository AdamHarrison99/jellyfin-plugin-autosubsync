using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Data;
using Jellyfin.Plugin.AutoSubSync.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

public record PlacementResult(string OutputPath, string? BackupPath, SubtitleProvenance Provenance);

// Moves a synced scratch file to its final location.
public class SubtitlePlacer
{
    // ! Serializes collision resolution; two tracks of one video can land together.
    private readonly Lock _gate = new();

    private readonly BackupVault _vault;
    private readonly ILogger<SubtitlePlacer> _logger;

    public SubtitlePlacer(BackupVault vault, ILogger<SubtitlePlacer> logger)
    {
        _vault = vault;
        _logger = logger;
    }

    public PlacementResult? Place(
        SubtitleTarget target,
        SyncRecord record,
        string scratchPath,
        PluginConfiguration config)
    {
        // ! An OCR'd source is a bitmap. Text must never be written over it.
        var overwrite = target.Origin == SubtitleOrigin.External
                        && config.ExternalWriteMode == ExternalWriteMode.Overwrite
                        && !target.RequiresOcr
                        && !string.IsNullOrEmpty(target.SubtitlePath)
                        && SameFormat(target.SubtitlePath, scratchPath);

        lock (_gate)
        {
            return overwrite
                ? Overwrite(target.SubtitlePath!, record, scratchPath)
                : SideBySide(target, scratchPath, config);
        }
    }

    // ! Stripping rewrites ASS as SubRip. Writing that over the original's name misnames it.
    private static bool SameFormat(string? originalPath, string scratchPath)
        => string.Equals(
            Path.GetExtension(originalPath),
            Path.GetExtension(scratchPath),
            StringComparison.OrdinalIgnoreCase);

    // ! Overwriting without a backup destroys the user's file. There is no configuration for it.
    private PlacementResult? Overwrite(
        string originalPath,
        SyncRecord record,
        string scratchPath)
    {
        var backupPath = _vault.Store(record.Id, originalPath);
        if (backupPath is null)
        {
            _logger.LogWarning("Backup failed for {Path}; leaving the original in place", originalPath);
            return null;
        }

        return TryMove(scratchPath, originalPath)
            ? new PlacementResult(originalPath, backupPath, SubtitleProvenance.Retimed)
            : null;
    }

    private PlacementResult? SideBySide(SubtitleTarget target, string scratchPath, PluginConfiguration config)
    {
        var desired = SubtitleNaming.BuildSidecarPath(
            target.VideoPath,
            target.Language,
            target.IsForced,
            target.IsHearingImpaired,
            config.MarkerSuffix,
            Path.GetExtension(scratchPath),
            target.Variant);

        var destination = SubtitleNaming.ResolveCollision(desired, config.MarkerSuffix);
        if (destination is null)
        {
            _logger.LogWarning("No free sidecar filename for {Path}", target.VideoPath);
            return null;
        }

        return TryMove(scratchPath, destination)
            ? new PlacementResult(destination, null, SubtitleProvenance.Created)
            : null;
    }

    private bool TryMove(string source, string destination)
    {
        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Move(source, destination, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Failed to place the synced subtitle at {Path}", destination);
            return false;
        }
    }
}

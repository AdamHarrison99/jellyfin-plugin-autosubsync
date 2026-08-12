using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Owns the payload cache on disk, keyed by tool, payload version and runtime identifier.
public class PayloadStore
{
    private const string StagingPrefix = ".staging-";
    private const string RetiredPrefix = ".retired-";

    private readonly string _root;
    private readonly string _scratch;
    private readonly ILogger<PayloadStore> _logger;

    public PayloadStore(IApplicationPaths applicationPaths, ILogger<PayloadStore> logger)
        : this(
            Path.Combine(applicationPaths.PluginConfigurationsPath, "AutoSubSync"),
            applicationPaths.TempDirectory,
            logger)
    {
    }

    internal PayloadStore(string home, string tempDirectory, ILogger<PayloadStore> logger)
    {
        _logger = logger;
        _root = Path.Combine(home, "payloads");

        // ! A container's /tmp is often a small tmpfs; the archive runs to hundreds of megabytes.
        _scratch = Path.Combine(tempDirectory, "autosubsync-payload");
    }

    public string Root => _root;

    public string CreateScratchPath(string extension)
    {
        Directory.CreateDirectory(_scratch);
        return Path.Combine(_scratch, $"payload-{Guid.NewGuid():N}{extension}");
    }

    public string DirectoryFor(PayloadTool tool, string rid)
        => Path.Combine(_root, tool.Name, tool.Version, rid);

    public string? ResolveExecutable(PayloadTool tool, string? rid)
    {
        if (string.IsNullOrEmpty(rid))
        {
            return null;
        }

        var path = Path.Combine(DirectoryFor(tool, rid), tool.ExecutableName);
        return File.Exists(path) ? path : null;
    }

    public long MeasureBytes(PayloadTool tool, string rid)
    {
        try
        {
            var directory = DirectoryFor(tool, rid);
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to measure the {Tool} payload for {Rid}", tool.Name, rid);
            return 0;
        }
    }

    // A staging directory beside the target, so promotion is a rename on one volume.
    public string CreateStagingDirectory(PayloadTool tool, string rid)
    {
        var parent = Path.Combine(_root, tool.Name, tool.Version);
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, StagingPrefix + rid + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(staging);
        return staging;
    }

    // ! Retire the old payload, never delete it first; a failed move would leave nothing.
    public void Promote(string stagingDirectory, PayloadTool tool, string rid)
    {
        var destination = DirectoryFor(tool, rid);
        var retired = Path.Combine(
            _root, tool.Name, tool.Version, RetiredPrefix + rid + "-" + Guid.NewGuid().ToString("N")[..8]);

        var hadPrevious = Directory.Exists(destination);
        if (hadPrevious)
        {
            Directory.Move(destination, retired);
        }

        try
        {
            Directory.Move(stagingDirectory, destination);
        }
        catch
        {
            if (hadPrevious)
            {
                Directory.Move(retired, destination);
            }

            throw;
        }

        if (hadPrevious)
        {
            DiscardStaging(retired);
        }
    }

    public void DiscardStaging(string stagingDirectory)
    {
        try
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove the staging directory {Path}", stagingDirectory);
        }
    }

    // ! Only ever call this once the pinned version resolves; it is the last copy that works.
    public int PruneSuperseded(PayloadTool tool)
    {
        var toolRoot = Path.Combine(_root, tool.Name);
        if (!Directory.Exists(toolRoot))
        {
            return 0;
        }

        var removed = 0;

        foreach (var directory in Directory.EnumerateDirectories(toolRoot))
        {
            var name = Path.GetFileName(directory);
            if (string.Equals(name, tool.Version, StringComparison.Ordinal))
            {
                PruneStaging(directory);
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
                _logger.LogInformation("Removed superseded {Tool} payload {Version}", tool.Name, name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Failed to remove the superseded {Tool} payload {Version}", tool.Name, name);
            }
        }

        return removed;
    }

    // Leftovers from an interrupted install.
    private void PruneStaging(string versionDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(versionDirectory))
        {
            var name = Path.GetFileName(directory);

            if (name.StartsWith(StagingPrefix, StringComparison.Ordinal)
                || name.StartsWith(RetiredPrefix, StringComparison.Ordinal))
            {
                DiscardStaging(directory);
            }
        }
    }
}

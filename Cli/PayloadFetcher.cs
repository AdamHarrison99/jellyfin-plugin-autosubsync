using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

public enum PayloadFetchOutcome
{
    AlreadyInstalled,
    Installed,
    NoAssetForPlatform,
    Busy,
    DownloadFailed,
    HashMismatch,
    ExtractFailed
}

public record PayloadFetchResult(PayloadFetchOutcome Outcome, string Message)
{
    public bool Succeeded => Outcome is PayloadFetchOutcome.Installed or PayloadFetchOutcome.AlreadyInstalled;
}

// Downloads, verifies and installs a pinned tool payload.
public class PayloadFetcher
{
    private const int MaxAttempts = 3;
    private const int CopyBufferBytes = 81920;
    private const int LogEveryPercent = 25;

    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15)];

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _single = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private readonly PayloadStore _store;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PayloadFetcher> _logger;

    public PayloadFetcher(
        PayloadStore store,
        IHttpClientFactory httpClientFactory,
        ILogger<PayloadFetcher> logger)
    {
        _store = store;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsRunning(PayloadTool tool) => _running.ContainsKey(tool.Name);

    public async Task<PayloadFetchResult> EnsureAsync(PayloadTool tool, CancellationToken cancellationToken)
    {
        var rid = PlatformRid.Current;
        var asset = tool.For(rid);

        if (asset is null)
        {
            return new PayloadFetchResult(
                PayloadFetchOutcome.NoAssetForPlatform,
                $"No {tool.Name} payload is published for {PlatformRid.Describe()}.");
        }

        if (_store.ResolveExecutable(tool, rid) is not null)
        {
            return new PayloadFetchResult(PayloadFetchOutcome.AlreadyInstalled, "The payload is installed.");
        }

        // ! Single-flight per tool. A scheduled task and a button press can arrive together.
        var gate = _single.GetOrAdd(tool.Name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new PayloadFetchResult(PayloadFetchOutcome.Busy, "A payload install is already running.");
        }

        _running[tool.Name] = 0;

        try
        {
            if (_store.ResolveExecutable(tool, rid) is not null)
            {
                return new PayloadFetchResult(PayloadFetchOutcome.AlreadyInstalled, "The payload is installed.");
            }

            return await DownloadAndInstallAsync(tool, asset, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.TryRemove(tool.Name, out _);
            gate.Release();
        }
    }

    private async Task<PayloadFetchResult> DownloadAndInstallAsync(
        PayloadTool tool,
        PayloadAsset asset,
        CancellationToken cancellationToken)
    {
        var scratch = _store.CreateScratchPath(ExtensionFor(asset));

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await DownloadAsync(tool, asset, scratch, cancellationToken).ConfigureAwait(false);
                return Install(tool, scratch, asset, deleteSource: true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(scratch);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                TryDelete(scratch);
                _logger.LogWarning(
                    ex,
                    "{Tool} download attempt {Attempt} of {Max} failed",
                    tool.Name,
                    attempt,
                    MaxAttempts);

                if (attempt == MaxAttempts)
                {
                    return new PayloadFetchResult(
                        PayloadFetchOutcome.DownloadFailed,
                        $"Could not download the payload after {MaxAttempts} attempts: {ex.Message}");
                }

                await Task.Delay(Backoff[attempt - 1], cancellationToken).ConfigureAwait(false);
            }
        }

        return new PayloadFetchResult(PayloadFetchOutcome.DownloadFailed, "Could not download the payload.");
    }

    private async Task DownloadAsync(
        PayloadTool tool,
        PayloadAsset asset,
        string destination,
        CancellationToken cancellationToken)
    {
        var url = tool.UrlFor(asset);

        _logger.LogInformation(
            "Downloading {Tool} {Version} payload {Payload} for {Rid} ({Megabytes} MB) from {Url}",
            tool.Name,
            tool.ToolVersion,
            tool.Version,
            asset.Rid,
            asset.Size / 1_048_576,
            url);

        var client = _httpClientFactory.CreateClient(NamedClient.Default);

        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true);

        var buffer = new byte[CopyBufferBytes];
        long received = 0;
        var reported = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;

            var percent = total <= 0 ? 0 : (int)(received * 100 / total);
            if (percent >= reported + LogEveryPercent)
            {
                reported = percent - (percent % LogEveryPercent);
                _logger.LogInformation("{Tool} payload download {Percent}%", tool.Name, reported);
            }
        }
    }

    // ! Verify first. Nothing is unpacked from an archive whose hash has not matched.
    internal PayloadFetchResult Install(
        PayloadTool tool,
        string archivePath,
        PayloadAsset asset,
        bool deleteSource)
    {
        var actual = HashFile(archivePath);
        if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError(
                "{Tool} payload hash mismatch for {Rid}: expected {Expected}, got {Actual}",
                tool.Name,
                asset.Rid,
                asset.Sha256,
                actual);

            TryDelete(archivePath);
            return new PayloadFetchResult(
                PayloadFetchOutcome.HashMismatch,
                "The payload did not match its expected checksum and was discarded.");
        }

        var staging = _store.CreateStagingDirectory(tool, asset.Rid);

        try
        {
            ExtractChecked(archivePath, staging, asset.Format);

            var executable = Path.Combine(staging, tool.ExecutableName);
            if (!File.Exists(executable))
            {
                _store.DiscardStaging(staging);
                return new PayloadFetchResult(
                    PayloadFetchOutcome.ExtractFailed,
                    $"The payload contains no {tool.ExecutableName}.");
            }

            MakeExecutable(executable);
            _store.Promote(staging, tool, asset.Rid);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _store.DiscardStaging(staging);
            _logger.LogError(ex, "Failed to unpack the {Tool} payload for {Rid}", tool.Name, asset.Rid);
            return new PayloadFetchResult(
                PayloadFetchOutcome.ExtractFailed,
                $"Could not unpack the payload: {ex.Message}");
        }
        finally
        {
            if (deleteSource)
            {
                TryDelete(archivePath);
            }
        }

        _store.PruneSuperseded(tool);
        _logger.LogInformation(
            "Installed {Tool} payload {Version} for {Rid}", tool.Name, tool.Version, asset.Rid);

        return new PayloadFetchResult(
            PayloadFetchOutcome.Installed,
            $"Installed {tool.Name} {tool.ToolVersion} for {asset.Rid}.");
    }

    internal static void ExtractChecked(string archivePath, string destination, PayloadArchiveFormat format)
    {
        if (format == PayloadArchiveFormat.TarGz)
        {
            ExtractTarGz(archivePath, destination);
            return;
        }

        ExtractZip(archivePath, destination);
    }

    // ! Every entry's resolved path must stay inside the target; an archive can carry '../'.
    private static string ResolveInside(string root, string prefix, string entryName)
    {
        var target = Path.GetFullPath(Path.Combine(root, entryName));

        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry '{entryName}' resolves outside the payload directory.");
        }

        return target;
    }

    private static (string Root, string Prefix) RootOf(string destination)
    {
        var root = Path.GetFullPath(destination);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return (root, prefix);
    }

    private static void ExtractZip(string archivePath, string destination)
    {
        var (root, prefix) = RootOf(destination);

        using var archive = ZipFile.OpenRead(archivePath);

        foreach (var entry in archive.Entries)
        {
            var target = ResolveInside(root, prefix, entry.FullName);

            // A directory entry carries an empty name.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static void ExtractTarGz(string archivePath, string destination)
    {
        var (root, prefix) = RootOf(destination);

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            // ! Links are skipped, not resolved. A symlink target is not path-checkable here.
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile
                or TarEntryType.Directory))
            {
                continue;
            }

            var target = ResolveInside(root, prefix, entry.Name);

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static string ExtensionFor(PayloadAsset asset)
        => asset.Format == PayloadArchiveFormat.TarGz ? ".tar.gz" : ".zip";

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Archive extraction drops the executable bit.
    private void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set the executable bit on {Path}", path);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete {Path}", path);
        }
    }
}

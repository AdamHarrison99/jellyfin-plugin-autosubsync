using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

// Stages a VobSub payload once per source file so each of its streams can be converted alone.
public sealed class VobSubStaging
{
    private const string FolderName = "vobsub";
    private const string PayloadName = "source.sub";

    private const int CopyBufferBytes = 1 << 20;

    // A staged payload outlives the streams that share it; anything older is from a dead scan.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private readonly string _root;
    private readonly ILogger<VobSubStaging> _logger;

    public VobSubStaging(string scratchRoot, ILogger<VobSubStaging> logger)
    {
        _root = Path.Combine(scratchRoot, FolderName);
        _logger = logger;
    }

    // ! The converter resolves the payload by filename beside the index and takes no flag for it,
    //   so a split index is only readable with a copy of the payload next to it.
    public async Task<string?> StageAsync(string subPath, int streamIndex, CancellationToken cancellationToken)
    {
        var index = VobSubIndex.IndexFor(subPath);

        if (!File.Exists(subPath) || !File.Exists(index))
        {
            return null;
        }

        try
        {
            var folder = Path.Combine(_root, Fingerprint(subPath));
            Directory.CreateDirectory(folder);

            var payload = Path.Combine(folder, PayloadName);

            if (!File.Exists(payload))
            {
                await CopyOnceAsync(subPath, payload, cancellationToken).ConfigureAwait(false);
            }

            var split = Path.Combine(
                folder,
                Path.GetFileNameWithoutExtension(PayloadName)
                + "." + streamIndex.ToString(CultureInfo.InvariantCulture) + ".idx");

            // The payload the converter opens is named after the index, so each stream needs its own.
            var paired = Path.ChangeExtension(split, ".sub");

            if (!File.Exists(paired))
            {
                await LinkAsync(payload, paired, cancellationToken).ConfigureAwait(false);
            }

            if (!VobSubIndex.TryWriteSingle(index, streamIndex, split))
            {
                return null;
            }

            // ! Keeps the sweep off a folder still in use. Conversion follows within one timeout.
            Touch(folder);
            return split;
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Could not stage {Path} stream {Stream}: {Message}", subPath, streamIndex, ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug("Could not stage {Path} stream {Stream}: {Message}", subPath, streamIndex, ex.Message);
            return null;
        }
    }

    // Drops staged payloads left by a scan that is over.
    public void Sweep()
    {
        if (!Directory.Exists(_root))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - StaleAfter;

        foreach (var folder in SafeFolders())
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < cutoff)
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // Still in use, or already gone.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to remove.
            }
        }
    }

    private static void Touch(string folder)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // The sweep falls back to whatever the filesystem recorded.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    private IEnumerable<string> SafeFolders()
    {
        try
        {
            return Directory.EnumerateDirectories(_root);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ! Two streams of one file can stage at the same time. Copy aside then move, so a half-written
    //   payload is never the one another stream opens.
    private static async Task CopyOnceAsync(string source, string destination, CancellationToken cancellationToken)
    {
        var pending = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

        await using (var reader = new FileStream(
            source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, useAsync: true))
        await using (var writer = new FileStream(
            pending, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, useAsync: true))
        {
            await reader.CopyToAsync(writer, CopyBufferBytes, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            File.Move(pending, destination);
        }
        catch (IOException)
        {
            File.Delete(pending);
        }
    }

    // A link where the platform grants one, a copy where it does not.
    private static async Task LinkAsync(string payload, string destination, CancellationToken cancellationToken)
    {
        // ! Hard link first on Windows. A symbolic link there needs a privilege a service account
        //   rarely holds, and the fallback copies the whole payload once per stream.
        if (OperatingSystem.IsWindows() && CreateHardLinkW(destination, payload, IntPtr.Zero))
        {
            return;
        }

        try
        {
            File.CreateSymbolicLink(destination, payload);
            return;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }

        await CopyOnceAsync(payload, destination, cancellationToken).ConfigureAwait(false);
    }

    // DllImport, not LibraryImport: the generated marshaller needs AllowUnsafeBlocks project-wide.
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private static string Fingerprint(string subPath)
    {
        var info = new FileInfo(subPath);
        var seed = subPath.ToUpperInvariant()
            + "|" + info.Length.ToString(CultureInfo.InvariantCulture)
            + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16];
    }
}

using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AutoSubSync.Data;

// Fingerprints for the idempotency guard.
public static class FileFingerprint
{
    private const int ChunkSize = 64 * 1024;

    // For subtitle files.
    public static string? TryComputeFull(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // For media files: size plus the first and last 64KB, never a full read.
    public static string? TryComputePartial(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var length = stream.Length;

            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            hasher.AppendData(Encoding.UTF8.GetBytes(length.ToString(System.Globalization.CultureInfo.InvariantCulture)));

            // ! Must be ReadExactly, not Read.
            var head = (int)Math.Min(length, ChunkSize);
            var buffer = new byte[ChunkSize];

            stream.ReadExactly(buffer, 0, head);
            hasher.AppendData(buffer, 0, head);

            if (length > ChunkSize)
            {
                var tail = (int)Math.Min(length - ChunkSize, ChunkSize);
                stream.Seek(-tail, SeekOrigin.End);
                stream.ReadExactly(buffer, 0, tail);
                hasher.AppendData(buffer, 0, tail);
            }

            return Convert.ToHexString(hasher.GetHashAndReset());
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

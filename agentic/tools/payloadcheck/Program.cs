using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AutoSubSync.Cli;
using Microsoft.Extensions.Logging.Abstractions;

// Exercises the real PayloadFetcher and PayloadStore, linked by the csproj.
// See agentic/ARCHITECTURE.md for what each case protects.
var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "payloadcheck-" + Guid.NewGuid().ToString("N")[..8]);
var rid = PlatformRid.Current ?? "linux-x64";
var zipTool = PayloadManifest.AssyCli;
var tarTool = PayloadManifest.Seconv;

try
{
    Check("a good archive installs and resolves", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var archive = BuildZip(home, "good.zip", zip => WithExecutable(zip, zipTool));
        var asset = AssetFor(archive, PayloadArchiveFormat.Zip);

        var result = fetcher.Install(zipTool, archive, asset, deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.Installed, $"outcome was {result.Outcome}");
        Expect(store.ResolveExecutable(zipTool, rid) is not null, "the executable did not resolve");
        Expect(!File.Exists(archive), "the source archive was not deleted");
    });

    Check("a tar.gz archive installs and resolves", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var archive = BuildTarGz(home, "good.tar.gz", entries =>
        {
            entries[tarTool.ExecutableName] = "#!/bin/false\n";
            entries["libSkiaSharp.so"] = "payload";
        });
        var asset = AssetFor(archive, PayloadArchiveFormat.TarGz);

        var result = fetcher.Install(tarTool, archive, asset, deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.Installed, $"outcome was {result.Outcome}");
        Expect(store.ResolveExecutable(tarTool, rid) is not null, "the executable did not resolve");

        var sibling = Path.Combine(store.DirectoryFor(tarTool, rid), "libSkiaSharp.so");
        Expect(File.Exists(sibling), "a sibling file was not extracted");
    });

    Check("two tools install side by side", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var first = BuildZip(home, "one.zip", zip => WithExecutable(zip, zipTool));
        fetcher.Install(zipTool, first, AssetFor(first, PayloadArchiveFormat.Zip), deleteSource: true);

        var second = BuildTarGz(home, "two.tar.gz", entries => entries[tarTool.ExecutableName] = "#!/bin/false\n");
        fetcher.Install(tarTool, second, AssetFor(second, PayloadArchiveFormat.TarGz), deleteSource: true);

        Expect(store.ResolveExecutable(zipTool, rid) is not null, "the first tool was displaced by the second");
        Expect(store.ResolveExecutable(tarTool, rid) is not null, "the second tool did not install");
    });

    Check("a corrupted archive is refused, deleted, and unpacks nothing", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var archive = BuildZip(home, "corrupt.zip", zip => WithExecutable(zip, zipTool));
        var asset = AssetFor(archive, PayloadArchiveFormat.Zip) with { Sha256 = new string('0', 64) };

        var result = fetcher.Install(zipTool, archive, asset, deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.HashMismatch, $"outcome was {result.Outcome}");
        Expect(!File.Exists(archive), "the rejected archive was left on disk");
        Expect(store.ResolveExecutable(zipTool, rid) is null, "a rejected archive was installed anyway");
        Expect(!Directory.Exists(store.DirectoryFor(zipTool, rid)), "a payload directory was created");
    });

    Check("a traversal entry is rejected and writes nothing outside the target", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var escapee = Path.Combine(store.Root, zipTool.Name, zipTool.Version, "escaped.txt");

        var archive = BuildZip(home, "slip.zip", zip =>
        {
            WithExecutable(zip, zipTool);
            Write(zip, "../escaped.txt", "owned");
        });

        var result = fetcher.Install(zipTool, archive, AssetFor(archive, PayloadArchiveFormat.Zip), deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.ExtractFailed, $"outcome was {result.Outcome}");
        Expect(!File.Exists(escapee), "the traversal entry escaped the payload directory");
        Expect(store.ResolveExecutable(zipTool, rid) is null, "a poisoned archive was installed");
        Expect(NoStagingLeft(store, zipTool), "a staging directory was left behind");
    });

    Check("a traversal entry in a tar.gz is rejected", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var escapee = Path.Combine(store.Root, tarTool.Name, tarTool.Version, "escaped.txt");

        var archive = BuildTarGz(home, "slip.tar.gz", entries =>
        {
            entries[tarTool.ExecutableName] = "#!/bin/false\n";
            entries["../escaped.txt"] = "owned";
        });

        var result = fetcher.Install(tarTool, archive, AssetFor(archive, PayloadArchiveFormat.TarGz), deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.ExtractFailed, $"outcome was {result.Outcome}");
        Expect(!File.Exists(escapee), "the traversal entry escaped the payload directory");
        Expect(store.ResolveExecutable(tarTool, rid) is null, "a poisoned archive was installed");
        Expect(NoStagingLeft(store, tarTool), "a staging directory was left behind");
    });

    Check("an archive without the binary is rejected", () =>
    {
        var (store, fetcher, home) = NewHarness();
        var archive = BuildZip(home, "empty.zip", zip => Write(zip, "readme.txt", "nothing here"));

        var result = fetcher.Install(zipTool, archive, AssetFor(archive, PayloadArchiveFormat.Zip), deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.ExtractFailed, $"outcome was {result.Outcome}");
        Expect(store.ResolveExecutable(zipTool, rid) is null, "an archive with no binary was installed");
        Expect(NoStagingLeft(store, zipTool), "a staging directory was left behind");
    });

    Check("installing replaces an existing payload", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var first = BuildZip(home, "first.zip", zip =>
        {
            WithExecutable(zip, zipTool);
            Write(zip, "marker.txt", "first");
        });
        fetcher.Install(zipTool, first, AssetFor(first, PayloadArchiveFormat.Zip), deleteSource: true);

        var second = BuildZip(home, "second.zip", zip =>
        {
            WithExecutable(zip, zipTool);
            Write(zip, "marker.txt", "second");
        });
        var result = fetcher.Install(zipTool, second, AssetFor(second, PayloadArchiveFormat.Zip), deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.Installed, $"outcome was {result.Outcome}");

        var marker = Path.Combine(store.DirectoryFor(zipTool, rid), "marker.txt");
        Expect(File.ReadAllText(marker) == "second", "the previous payload was not replaced");
    });

    Check("pruning removes superseded versions and keeps the current one", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var stale = Path.Combine(store.Root, zipTool.Name, "0.1", rid);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "old.txt"), "stale");

        var archive = BuildZip(home, "prune.zip", zip => WithExecutable(zip, zipTool));
        fetcher.Install(zipTool, archive, AssetFor(archive, PayloadArchiveFormat.Zip), deleteSource: true);

        Expect(
            !Directory.Exists(Path.Combine(store.Root, zipTool.Name, "0.1")),
            "a superseded version survived");
        Expect(store.ResolveExecutable(zipTool, rid) is not null, "the current payload was pruned");
    });

    Check("pruning one tool leaves the other alone", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var other = Path.Combine(store.Root, tarTool.Name, "0.1", rid);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "old.txt"), "stale");

        var archive = BuildZip(home, "prune-scope.zip", zip => WithExecutable(zip, zipTool));
        fetcher.Install(zipTool, archive, AssetFor(archive, PayloadArchiveFormat.Zip), deleteSource: true);

        Expect(Directory.Exists(other), "pruning one tool deleted another tool's payload");
    });

    Check("a failed promotion restores the previous payload", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var good = BuildZip(home, "before.zip", zip =>
        {
            WithExecutable(zip, zipTool);
            Write(zip, "marker.txt", "before");
        });
        fetcher.Install(zipTool, good, AssetFor(good, PayloadArchiveFormat.Zip), deleteSource: true);

        var staging = store.CreateStagingDirectory(zipTool, rid);
        Directory.Delete(staging);

        try
        {
            store.Promote(staging, zipTool, rid);
            Expect(false, "promoting a missing staging directory should have thrown");
        }
        catch (DirectoryNotFoundException)
        {
            // The promotion failed as intended.
        }

        var marker = Path.Combine(store.DirectoryFor(zipTool, rid), "marker.txt");
        Expect(File.Exists(marker), "the previous payload was not restored");
        Expect(File.ReadAllText(marker) == "before", "the restored payload is not the original");
    });

    Check("a failed install leaves an existing payload in place", () =>
    {
        var (store, fetcher, home) = NewHarness();

        var good = BuildZip(home, "keep.zip", zip => WithExecutable(zip, zipTool));
        fetcher.Install(zipTool, good, AssetFor(good, PayloadArchiveFormat.Zip), deleteSource: true);

        var bad = BuildZip(home, "bad.zip", zip => WithExecutable(zip, zipTool));
        var asset = AssetFor(bad, PayloadArchiveFormat.Zip) with { Sha256 = new string('f', 64) };
        var result = fetcher.Install(zipTool, bad, asset, deleteSource: true);

        Expect(result.Outcome == PayloadFetchOutcome.HashMismatch, $"outcome was {result.Outcome}");
        Expect(
            store.ResolveExecutable(zipTool, rid) is not null,
            "a rejected download destroyed the working payload");
    });
}
finally
{
    TryDeleteTree(sandbox);
}

if (failures > 0)
{
    Console.Error.WriteLine($"\npayloadcheck: {failures} failure(s)");
    return 1;
}

Console.WriteLine("payloadcheck: all cases pass");
return 0;

void Check(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"  ok    {name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL  {name}: {ex.Message}");
        failures++;
    }
}

void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

(PayloadStore Store, PayloadFetcher Fetcher, string Home) NewHarness()
{
    var home = Path.Combine(sandbox, Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(home);

    var store = new PayloadStore(home, Path.Combine(home, "temp"), NullLogger<PayloadStore>.Instance);
    var fetcher = new PayloadFetcher(store, new NoHttp(), NullLogger<PayloadFetcher>.Instance);
    return (store, fetcher, home);
}

string BuildZip(string home, string name, Action<ZipArchive> fill)
{
    var path = Path.Combine(home, name);
    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
    {
        fill(zip);
    }

    return path;
}

string BuildTarGz(string home, string name, Action<Dictionary<string, string>> fill)
{
    var entries = new Dictionary<string, string>(StringComparer.Ordinal);
    fill(entries);

    var path = Path.Combine(home, name);

    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
    using (var gzip = new GZipStream(stream, CompressionMode.Compress))
    using (var writer = new TarWriter(gzip, TarEntryFormat.Pax))
    {
        foreach (var (entryName, content) in entries)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
            };

            writer.WriteEntry(entry);
        }
    }

    return path;
}

void WithExecutable(ZipArchive zip, PayloadTool tool)
{
    Write(zip, tool.ExecutableName, "#!/bin/false\n");
    Write(zip, "_internal/support.txt", "payload");
}

void Write(ZipArchive zip, string entryName, string content)
{
    using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
    writer.Write(content);
}

PayloadAsset AssetFor(string archivePath, PayloadArchiveFormat format)
{
    using var stream = File.OpenRead(archivePath);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    return new PayloadAsset(rid, Path.GetFileName(archivePath), hash, new FileInfo(archivePath).Length, format);
}

bool NoStagingLeft(PayloadStore store, PayloadTool tool)
{
    var versionDirectory = Path.Combine(store.Root, tool.Name, tool.Version);
    return !Directory.Exists(versionDirectory)
           || !Directory.EnumerateDirectories(versionDirectory, ".staging-*").Any();
}

void TryDeleteTree(string path)
{
    try
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch (IOException)
    {
        Console.Error.WriteLine($"note: could not clean up {path}");
    }
}

internal sealed class NoHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
        => throw new NotSupportedException("payloadcheck never downloads.");
}

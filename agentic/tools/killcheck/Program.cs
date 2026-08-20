using System.Diagnostics;
using System.Management;

namespace KillCheck;

// Answers one question: when the plugin cancels a sync, does every child process actually die?
//
// AssyCliRunner kills with Process.Kill(entireProcessTree: true). That is the only thing standing
// between a cancelled scheduled task and a machine full of orphaned assy-cli workers, because
// ffsubsync runs its engine through multiprocessing and each worker is another copy of the exe.
// This spawns a real sync, waits for the workers to appear, kills the way the plugin kills, and
// reports anything left breathing.
internal static class Program
{
    private static int Main(string[] args)
    {
        var exe = Arg(args, "--exe");
        var video = Arg(args, "--video");
        var subtitle = Arg(args, "--subtitle");
        var output = Arg(args, "--out");
        var tool = Arg(args, "--tool") ?? "ffsubsync";
        var settle = int.TryParse(Arg(args, "--settle"), out var s) ? s : 40;

        if (exe is null || video is null || subtitle is null || output is null)
        {
            Console.Error.WriteLine(
                "usage: killcheck --exe <assy-cli> --video <path> --subtitle <path> --out <path> "
                + "[--tool ffsubsync] [--settle 40]");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("killcheck reads the process table through WMI and is Windows-only.");
            return 2;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in new[]
                 {
                     "--no-color", "sync", video, subtitle, "-o", output,
                     "-t", tool, "--json", "--no-prefix"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Process.Start returned null");

        // Drained so a full pipe cannot stall the child while we wait for it to fan out.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Console.WriteLine($"parent pid : {process.Id}");
        Console.WriteLine($"settling   : {settle}s");

        var deadline = DateTime.UtcNow.AddSeconds(settle);
        var descendants = new Dictionary<int, string>();

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(2000);
            descendants = Descendants(process.Id);
            if (descendants.Count > 0 && process.HasExited)
            {
                break;
            }
        }

        descendants = Descendants(process.Id);

        if (process.HasExited)
        {
            Console.Error.WriteLine(
                $"The sync finished on its own in under {settle}s, so nothing was killed. "
                + "Use a longer reference or a shorter --settle.");
            return 2;
        }

        Console.WriteLine($"descendants: {descendants.Count}");
        foreach (var (id, name) in descendants.OrderBy(d => d.Key))
        {
            Console.WriteLine($"  {id} {name}");
        }

        if (descendants.Count == 0)
        {
            Console.Error.WriteLine(
                "No child processes appeared. Either the engine does not fan out, or the freeze is "
                + "broken again and the workers are dying on launch.");
            return 2;
        }

        var watched = descendants.Keys.Append(process.Id).ToList();

        Console.WriteLine("kill       : Process.Kill(entireProcessTree: true)");
        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        Thread.Sleep(3000);

        var survivors = watched.Where(IsAlive).ToList();

        if (survivors.Count == 0)
        {
            Console.WriteLine($"PASS: all {watched.Count} processes are gone");
            return 0;
        }

        Console.Error.WriteLine($"FAIL: {survivors.Count} orphaned");
        foreach (var id in survivors)
        {
            Console.Error.WriteLine($"  {id} {NameOf(id)}");
        }

        Console.Error.WriteLine(
            "Kill(entireProcessTree: true) did not reap the tree. A cancelled scheduled task would "
            + "leave these running.");
        return 1;
    }

    private static bool IsAlive(int id)
    {
        try
        {
            return !Process.GetProcessById(id).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NameOf(int id)
    {
        try
        {
            return Process.GetProcessById(id).ProcessName;
        }
        catch (ArgumentException)
        {
            return "(exited)";
        }
    }

    // ! Walked through WMI, not Process.Parent; .NET exposes no parent id.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<int, string> Descendants(int root)
    {
        var table = new List<(int Id, int Parent, string Name)>();

        using (var searcher = new ManagementObjectSearcher(
                   "SELECT ProcessId, ParentProcessId, Name FROM Win32_Process"))
        using (var results = searcher.Get())
        {
            foreach (var row in results)
            {
                table.Add((
                    Convert.ToInt32(row["ProcessId"]),
                    Convert.ToInt32(row["ParentProcessId"]),
                    (string)row["Name"]));
                row.Dispose();
            }
        }

        var found = new Dictionary<int, string>();
        var frontier = new Queue<int>();
        frontier.Enqueue(root);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();

            foreach (var row in table.Where(r => r.Parent == current && !found.ContainsKey(r.Id)))
            {
                found[row.Id] = row.Name;
                frontier.Enqueue(row.Id);
            }
        }

        return found;
    }

    private static string? Arg(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

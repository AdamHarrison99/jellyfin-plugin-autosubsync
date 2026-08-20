using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Services;

// Answers one question: does a refresh the plugin's own writes provoked come back as work?

var failures = 0;
var sandbox = Path.Combine(Path.GetTempPath(), "gatecheck-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);

var itemId = Guid.NewGuid();
var video = Write("Movie (2001).mkv", "not really a video");
var subtitle = Write("Movie (2001).eng.srt", "1\n00:00:01,000 --> 00:00:02,000\nhello\n");

Console.WriteLine("-- the refresh loop --");

Check("an item never seen before is work", () =>
{
    var gate = Gate(subtitle);
    Expect(gate.HasWorkToDo(itemId, video, Config()), "a cold item was gated");
});

// ! The defect this exists for. Syncing rewrites the sidecar, Jellyfin refreshes the item, and
//   the refresh arrived as a fresh change because nothing recorded the post-write state.
Check("a refresh after the plugin's own write is not work", () =>
{
    var gate = Gate(subtitle);
    var config = Config();

    Touch(subtitle, "1\n00:00:01,240 --> 00:00:02,240\nhello\n");
    gate.Commit(itemId, video, config);

    Expect(!gate.HasWorkToDo(itemId, video, config), "the plugin's own refresh came back as work");
});

Check("a repeated refresh stays gated", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, video, config);

    for (var i = 0; i < 5; i++)
    {
        Expect(!gate.HasWorkToDo(itemId, video, config), $"refresh {i + 1} reopened the item");
    }
});

Console.WriteLine();
Console.WriteLine("-- what must still reopen an item --");

Check("a subtitle edited by someone else is work", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, video, config);

    Touch(subtitle, "1\n00:00:09,000 --> 00:00:11,000\nsomeone edited this\n");
    Expect(gate.HasWorkToDo(itemId, video, config), "an edited sidecar stayed gated");
});

Check("a replaced video is work", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, video, config);

    Touch(video, "a different encode entirely");
    Expect(gate.HasWorkToDo(itemId, video, config), "a replaced video stayed gated");
});

Check("a new sidecar appearing is work", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, video, config);

    gate.Externals = [subtitle, Write("Movie (2001).fre.srt", "bonjour")];
    Expect(gate.HasWorkToDo(itemId, video, config), "a new sidecar stayed gated");
});

Check("a sidecar disappearing is work", () =>
{
    var gone = Write("Movie (2001).ita.srt", "ciao");
    var gate = Gate(subtitle, gone);
    var config = Config();
    gate.Commit(itemId, video, config);

    File.Delete(gone);
    Expect(gate.HasWorkToDo(itemId, video, config), "a removed sidecar stayed gated");
});

Console.WriteLine();
Console.WriteLine("-- settings are retroactive through the gate --");

// ! The gate sits in front of the per-record checks, so a setting they honour has to be in
//   the stamp too or the item never reaches them.
Check("turning hearing-impaired removal on reopens a gated item", () =>
{
    var gate = Gate(subtitle);
    gate.Commit(itemId, video, Config());

    var stripped = Config();
    stripped.RemoveHearingImpairedTags = true;
    Expect(gate.HasWorkToDo(itemId, video, stripped), "a changed output setting stayed gated");
});

Check("leaving dry run reopens a gated item", () =>
{
    var dry = Config();
    dry.DryRunMode = true;

    var gate = Gate(subtitle);
    gate.Commit(itemId, video, dry);

    Expect(gate.HasWorkToDo(itemId, video, Config()), "leaving dry run stayed gated");
});

// ! The fallback added no setting, so without this token in the stamp every refusal it would now
//   decide differently stays closed and the panel keeps reporting a verdict nothing would produce.
Check("the check revision travels in the outcome stamp", () =>
{
    var stamp = Config().OutcomeStamp();
    Expect(
        !string.IsNullOrWhiteSpace(PluginConfiguration.CheckRevision),
        "the check revision is empty, so bumping it could never be seen");
    Expect(
        stamp.Contains(PluginConfiguration.CheckRevision, StringComparison.Ordinal),
        $"the outcome stamp '{stamp}' does not carry the check revision");
});

Check("a record stamped under an earlier check revision is not current", () =>
{
    var current = Config().OutcomeStamp();
    var earlier = current.Replace(PluginConfiguration.CheckRevision, "check1", StringComparison.Ordinal);
    Expect(earlier != current, "an earlier revision stamped the same, so no refusal would reopen");
});

Check("a throttling change does not reopen a gated item", () =>
{
    var gate = Gate(subtitle);
    gate.Commit(itemId, video, Config());

    var faster = Config();
    faster.MaxConcurrentSyncs = 4;
    faster.PerSyncTimeoutMinutes = 45;
    Expect(!gate.HasWorkToDo(itemId, video, faster), "a throttling change rewrote the library");
});

Console.WriteLine();
Console.WriteLine("-- degenerate input --");

Check("an item with no path is always work", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, null, config);
    Expect(gate.HasWorkToDo(itemId, null, config), "a pathless item was gated on an unreadable signature");
});

Check("forgetting an item reopens it", () =>
{
    var gate = Gate(subtitle);
    var config = Config();
    gate.Commit(itemId, video, config);

    gate.Forget(itemId);
    Expect(gate.HasWorkToDo(itemId, video, config), "a forgotten item stayed gated");
});

Directory.Delete(sandbox, true);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "gatecheck ok" : $"gatecheck FAILED with {failures} failure(s)");
return failures == 0 ? 0 : 1;

string Write(string name, string content)
{
    var path = Path.Combine(sandbox, name);
    File.WriteAllText(path, content);
    return path;
}

// ! Distinct write times, or a same-second rewrite looks unchanged and every case passes for
//   the wrong reason.
void Touch(string path, string content)
{
    File.WriteAllText(path, content);
    File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
}

StubGate Gate(params string[] externals) => new(externals);

PluginConfiguration Config()
{
    var config = new PluginConfiguration { DryRunMode = false };
    config.Normalize();
    return config;
}

void Check(string name, Action body)
{
    try
    {
        body();
        Console.WriteLine($"  ok   {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"  FAIL {name}: {ex.Message}");
    }
}

void Expect(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

// The gate with its media-server lookup replaced by a list the harness can widen.
internal sealed class StubGate : ItemChangeGate
{
    public StubGate(string[] externals)
        : base(null!)
    {
        Externals = externals;
    }

    public string[] Externals { get; set; }

    internal override IEnumerable<string> ExternalSubtitlePaths(Guid itemId) => Externals;
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Jellyfin.Plugin.AutoSubSync.Models;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Cli;

// Spawns the pinned assy-cli and parses its output.
public class AssyCliRunner : IAssyCliRunner
{
    private const int StandardErrorTailChars = 4000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly AssyRuntime _runtime;
    private readonly AssyConfigFile _configFile;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<AssyCliRunner> _logger;

    public AssyCliRunner(
        AssyRuntime runtime,
        AssyConfigFile configFile,
        IMediaEncoder mediaEncoder,
        ILogger<AssyCliRunner> logger)
    {
        _runtime = runtime;
        _configFile = configFile;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    public Task<AssyInvocationResult> SyncAsync(
        string videoPath,
        string subtitlePath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var config = GetConfiguration();

        if (_runtime.ExecutablePath is not { } exe)
        {
            return Task.FromResult(UnavailableResult(_runtime.GetStatus()));
        }

        if (_configFile.Ensure() is not { } configPath)
        {
            return Task.FromResult(ConfigUnavailableResult());
        }

        var invocation = AssyArgumentBuilder.BuildSync(
            config, exe, configPath, videoPath, subtitlePath, outputPath);
        return RunAsync(invocation, config, expectJson: true, cancellationToken);
    }

    public Task<AssyInvocationResult> ShiftAsync(
        string subtitlePath,
        int milliseconds,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var config = GetConfiguration();

        if (_runtime.ExecutablePath is not { } exe)
        {
            return Task.FromResult(UnavailableResult(_runtime.GetStatus()));
        }

        if (_configFile.Ensure() is not { } configPath)
        {
            return Task.FromResult(ConfigUnavailableResult());
        }

        var invocation = AssyArgumentBuilder.BuildShift(
            exe, configPath, subtitlePath, milliseconds, outputPath);
        return RunAsync(invocation, config, expectJson: true, cancellationToken);
    }

    private static PluginConfiguration GetConfiguration()
        => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static AssyInvocationResult UnavailableResult(RuntimeStatus status) => new()
    {
        ExitCode = 2,
        StandardError = status.Message
    };

    // ! Never spawn without it. assy-cli treats a missing --config-file path as an empty config
    //   and runs on upstream defaults, which is the behaviour the file exists to prevent.
    private static AssyInvocationResult ConfigUnavailableResult() => new()
    {
        ExitCode = 2,
        StandardError = "The assy-cli configuration file could not be written."
    };

    private async Task<AssyInvocationResult> RunAsync(
        AssyArgumentBuilder.Invocation invocation,
        PluginConfiguration config,
        bool expectJson,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in invocation.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ApplyEnvironment(startInfo);

        _logger.LogDebug(
            "Running {File} {Args}",
            invocation.FileName,
            string.Join(' ', invocation.Arguments));

        var result = new AssyInvocationResult();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start {File}", invocation.FileName);
            result.ExitCode = 2;
            result.StandardError = ex.Message;
            return result;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.PerSyncTimeoutMinutes > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(config.PerSyncTimeoutMinutes));
        }

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.TimedOut = !cancellationToken.IsCancellationRequested;
            KillProcessTree(process);

            // Let the async readers drain what was already buffered.
            await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);

            // ! Only a timeout is an engine failure. Rethrowing an external cancel is what stops
            //   the task from taking the next target.
            if (!result.TimedOut)
            {
                throw;
            }

            result.ExitCode = 1;
            result.StandardError = Tail(stderr.ToString());
            return result;
        }

        result.ExitCode = process.ExitCode;
        result.StandardOutput = stdout.ToString();

        // ! Read before the tail is cut. The engine prints these well above its last 4000 chars.
        var diagnostics = stderr.ToString();
        result.Alignment = EngineAlignment.From(diagnostics);
        result.StandardError = Tail(diagnostics);

        if (expectJson)
        {
            result.Result = ParseLastJsonObject(result.StandardOutput);
        }

        return result;
    }

    // Passed through to the child; every other variable is dropped.
    private static readonly string[] PassThroughVariables =
    [
        "HOME", "TMPDIR", "TMP", "TEMP",
        "LANG", "LC_ALL", "LC_CTYPE",
        "SystemRoot", "windir", "COMSPEC", "PATHEXT", "NUMBER_OF_PROCESSORS", "USERPROFILE"
    ];

    // ! Pinned to one thread each, so a queue permit costs about one core.
    private static readonly string[] ThreadLimitVariables =
    [
        "OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS",
        "NUMEXPR_NUM_THREADS", "VECLIB_MAXIMUM_THREADS"
    ];

    // Allowlisted, not inherited.
    private void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();

        foreach (var name in PassThroughVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                startInfo.Environment[name] = value;
            }
        }

        // ! Set after the pass-through, so an inherited value never wins.
        foreach (var name in ThreadLimitVariables)
        {
            startInfo.Environment[name] = "1";
        }

        var encoderDir = Path.GetDirectoryName(_mediaEncoder.EncoderPath);
        var systemPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        startInfo.Environment["PATH"] = string.IsNullOrEmpty(encoderDir)
            ? systemPath
            : encoderDir + Path.PathSeparator + systemPath;
    }

    // Last per-pair result, stepping over batch mode's trailing summary envelope.
    internal static AssyResult? ParseLastJsonObject(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(lines[i]);

                if (document.RootElement.TryGetProperty("summary", out _))
                {
                    continue;
                }

                if (!document.RootElement.TryGetProperty("ok", out _))
                {
                    continue;
                }

                var parsed = JsonSerializer.Deserialize<AssyResult>(lines[i], SerializerOptions);
                if (parsed is not null)
                {
                    return parsed;
                }
            }
            catch (JsonException)
            {
                // Not JSON; keep scanning.
            }
        }

        return null;
    }

    private void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to kill the assy-cli process tree");
        }
    }

    private static string Tail(string value)
        => value.Length <= StandardErrorTailChars
            ? value
            : value[^StandardErrorTailChars..];
}

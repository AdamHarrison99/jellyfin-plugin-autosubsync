using System.Diagnostics;
using System.Text;
using Jellyfin.Plugin.AutoSubSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Subtitles;

internal record FfmpegOutcome(bool Succeeded, string StandardError);

// One ffmpeg invocation, under the same timeout and kill-the-tree rules as the sync engines.
internal static class FfmpegProcess
{
    private const int BoundedSlackChars = 64 * 1024;

    private const int StandardErrorTailChars = 4000;
    private const int DrainMilliseconds = 200;

    public static async Task<FfmpegOutcome> RunAsync(
        ProcessStartInfo startInfo,
        ILogger logger,
        CancellationToken cancellationToken,
        int keepChars = StandardErrorTailChars)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var stderr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        // ! Drained, not read. A full stdout pipe deadlocks the child.
        process.OutputDataReceived += (_, _) => { };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                AppendBounded(stderr, e.Data, keepChars);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start {File}", startInfo.FileName);
            return new FfmpegOutcome(false, ex.Message);
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
            var timedOut = !cancellationToken.IsCancellationRequested;
            KillProcessTree(process, logger);

            // Let the async readers drain what was already buffered.
            await Task.Delay(DrainMilliseconds, CancellationToken.None).ConfigureAwait(false);

            if (!timedOut)
            {
                throw;
            }

            return new FfmpegOutcome(false, "ffmpeg timed out. " + Tail(stderr.ToString(), keepChars));
        }

        return new FfmpegOutcome(process.ExitCode == 0, Tail(stderr.ToString(), keepChars));
    }

    private static void KillProcessTree(Process process, ILogger logger)
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
            logger.LogWarning(ex, "Failed to kill the ffmpeg process tree");
        }
    }

    // ! Trimmed as it grows, ¬only at the end. A chatty child holds its whole output in
    //   memory until its timeout fires; the slack keeps the trim amortized.
    private static void AppendBounded(StringBuilder builder, string line, int keep)
    {
        builder.AppendLine(line);

        if (builder.Length > keep + BoundedSlackChars)
        {
            builder.Remove(0, builder.Length - keep);
        }
    }

    private static string Tail(string value, int keepChars)
        => value.Length <= keepChars ? value : value[^keepChars..];
}

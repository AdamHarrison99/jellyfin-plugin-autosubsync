using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutoSubSync.Services;

// Hill-climbs the concurrency level that moves the most work per unit time.
public class AdaptiveConcurrency
{
    // ! Twelve, not six. The decision margin sits near the standard error of this mean, so
    //   halving the margin without doubling the sample makes the climb chase its own noise.
    private const int SamplesPerLevel = 12;

    private const double MeaningfulChange = 0.07;

    // ! Held below the peak the climb found, so a settled run leaves the box some room.
    private const int SettleBackOff = 1;

    private const int ResettleAfterSamples = 150;

    private const double BytesPerGigabyte = 1_000_000_000d;

    private readonly Lock _gate = new();
    private readonly ILogger<AdaptiveConcurrency> _logger;

    private int _level = 1;
    private int _samples;
    private double _sumMsPerGigabyte;
    private double _sumObserved;

    private int _step = 1;
    private int _peak = 1;
    private int _measuredLevel;
    private double _measuredThroughput;

    private bool _probing = true;
    private int _samplesSinceSettled;

    public AdaptiveConcurrency(ILogger<AdaptiveConcurrency> logger)
    {
        _logger = logger;
    }

    public int CurrentLevel(int ceiling)
    {
        lock (_gate)
        {
            ceiling = Math.Max(1, ceiling);

            if (_level > ceiling)
            {
                _level = ceiling;
                _measuredLevel = 0;
                ResetLevelStatistics();
            }

            return _level;
        }
    }

    // Wall time under the semaphore, against the bytes that had to be read to earn it.
    // ! observedConcurrency is what actually ran, not what was permitted.
    public void Report(
        int levelInEffect,
        long elapsedMs,
        long referenceBytes,
        int ceiling,
        int observedConcurrency)
    {
        if (elapsedMs <= 0 || referenceBytes <= 0 || observedConcurrency <= 0)
        {
            return;
        }

        lock (_gate)
        {
            ceiling = Math.Max(1, ceiling);

            // ! A level change mid-run makes the sample unattributable.
            if (levelInEffect != _level)
            {
                return;
            }

            if (!_probing)
            {
                CountTowardsResettle(ceiling);
                return;
            }

            _samples++;
            _sumMsPerGigabyte += elapsedMs / (referenceBytes / BytesPerGigabyte);
            _sumObserved += Math.Min(observedConcurrency, _level);

            if (_samples < SamplesPerLevel)
            {
                return;
            }

            Decide(ceiling);
        }
    }

    private void Decide(int ceiling)
    {
        var throughput = (_sumObserved / _samples) / (_sumMsPerGigabyte / _samples);
        var previousLevel = _measuredLevel;
        var previousThroughput = _measuredThroughput;

        _measuredLevel = _level;
        _measuredThroughput = throughput;
        ResetLevelStatistics();

        if (previousLevel == 0)
        {
            Move(ceiling);
            return;
        }

        if (throughput > previousThroughput * (1 + MeaningfulChange))
        {
            Move(ceiling);
            return;
        }

        if (throughput < previousThroughput * (1 - MeaningfulChange))
        {
            _step = -_step;
            Settle(previousLevel);
            return;
        }

        // Flat: the extra slot bought nothing, so keep the cheaper of the two.
        Settle(Math.Min(_level, previousLevel));
    }

    private void Move(int ceiling)
    {
        var next = Math.Clamp(_level + _step, 1, ceiling);

        if (next == _level)
        {
            Settle(_level);
            return;
        }

        _level = next;
        _logger.LogDebug("Concurrency probing at {Level}", _level);
    }

    private void Settle(int level)
    {
        _probing = false;
        _samplesSinceSettled = 0;
        _measuredLevel = 0;
        _measuredThroughput = 0;

        // ! The peak is what re-probing measures from. Backing off the operating level and then
        //   climbing from *that* would take another slot off on every resettle, forever.
        _peak = level;

        // ! Never below two. Backing a peak of two down to one costs half the throughput, which
        //   is not the small margin this is meant to leave.
        _level = Math.Max(level - SettleBackOff, Math.Min(level, 2));

        _logger.LogInformation(
            "Concurrency settled at {Level} concurrent syncs, one below the {Peak} the climb found",
            _level,
            _peak);
    }

    private void CountTowardsResettle(int ceiling)
    {
        _samplesSinceSettled++;

        if (_samplesSinceSettled < ResettleAfterSamples)
        {
            return;
        }

        // Load on the box changes; what was optimal an hour ago may not be.
        _probing = true;
        _samplesSinceSettled = 0;

        // ! From the peak, not the backed-off level: the comparison that ends the climb has to be
        //   made against the same level it was made against last time.
        _level = Math.Clamp(_peak, 1, ceiling);

        // ! Probe towards the end that has room, or the next move is a no-op forever.
        _step = _level < ceiling ? 1 : -1;

        ResetLevelStatistics();
        _logger.LogDebug("Re-probing concurrency from {Level}", _level);
    }

    private void ResetLevelStatistics()
    {
        _samples = 0;
        _sumMsPerGigabyte = 0;
        _sumObserved = 0;
    }
}

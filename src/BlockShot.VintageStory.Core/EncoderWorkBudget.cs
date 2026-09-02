namespace BlockShot.VintageStory.Core;

/// <summary>Calculates a rest period that enforces both a frame-rate and CPU duty-cycle ceiling.</summary>
public static class EncoderWorkBudget
{
    public static TimeSpan RestAfter(
        TimeSpan workDuration,
        int maximumFramesPerSecond,
        double maximumDutyCycle)
    {
        if (workDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(workDuration));
        if (maximumFramesPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(maximumFramesPerSecond));
        if (maximumDutyCycle is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(maximumDutyCycle));

        var minimumPeriod = TimeSpan.FromSeconds(1d / maximumFramesPerSecond);
        var rateLimitedRest = minimumPeriod - workDuration;
        var dutyLimitedRest = TimeSpan.FromTicks(
            checked((long)Math.Ceiling(workDuration.Ticks * (1d / maximumDutyCycle - 1d))));
        return TimeSpan.FromTicks(Math.Max(0, Math.Max(rateLimitedRest.Ticks, dutyLimitedRest.Ticks)));
    }
}

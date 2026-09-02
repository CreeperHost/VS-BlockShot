using BlockShot.VintageStory.Core;
using Xunit;

namespace BlockShot.VintageStory.Core.Tests;

public sealed class VideoTimingTests
{
    [Fact]
    public void Default_cadence_requests_fifteen_frames_over_one_second_at_sixty_hz()
    {
        var cadence = new VideoCaptureCadence();
        var captures = new List<TimeSpan>();

        for (var renderFrame = 0; renderFrame < 60; renderFrame++)
        {
            var timestamp = TimeSpan.FromSeconds(renderFrame / 60d);
            if (cadence.ShouldCapture(timestamp)) captures.Add(timestamp);
        }

        Assert.Equal(15, captures.Count);
        for (var index = 0; index < captures.Count; index++)
        {
            var ideal = TimeSpan.FromSeconds(index / 15d);
            Assert.InRange(captures[index] - ideal, TimeSpan.Zero, TimeSpan.FromSeconds(1d / 60d));
        }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(42)]
    public void Fifteen_fps_budget_sustains_observed_encoder_work_with_seventy_five_percent_duty(
        int workMilliseconds)
    {
        var work = TimeSpan.FromMilliseconds(workMilliseconds);
        var rest = EncoderWorkBudget.RestAfter(
            work,
            VideoCaptureCadence.DefaultFramesPerSecond,
            maximumDutyCycle: 0.75);

        var cycle = work + rest;
        Assert.InRange(
            cycle,
            TimeSpan.FromSeconds(1d / 15d) - TimeSpan.FromTicks(1),
            TimeSpan.FromSeconds(1d / 15d) + TimeSpan.FromTicks(1));
    }

    [Fact]
    public void Late_render_frame_skips_missed_slots_without_drifting_the_clock()
    {
        var cadence = new VideoCaptureCadence();

        Assert.True(cadence.ShouldCapture(TimeSpan.Zero));
        Assert.True(cadence.ShouldCapture(TimeSpan.FromMilliseconds(205)));
        Assert.InRange(
            cadence.NextFrameAt,
            TimeSpan.FromMilliseconds(266),
            TimeSpan.FromMilliseconds(267));
        Assert.False(cadence.ShouldCapture(TimeSpan.FromMilliseconds(250)));
    }
}

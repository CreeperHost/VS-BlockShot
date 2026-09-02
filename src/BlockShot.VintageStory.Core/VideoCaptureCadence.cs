namespace BlockShot.VintageStory.Core;

/// <summary>
/// Advances a real-time capture clock without accumulating drift when render frames arrive late.
/// </summary>
public sealed class VideoCaptureCadence
{
    public const int DefaultFramesPerSecond = 15;

    public VideoCaptureCadence(int framesPerSecond = DefaultFramesPerSecond)
    {
        if (framesPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        FrameInterval = TimeSpan.FromSeconds(1d / framesPerSecond);
    }

    public TimeSpan FrameInterval { get; }

    public TimeSpan NextFrameAt { get; private set; }

    public bool ShouldCapture(TimeSpan timestamp)
    {
        if (timestamp < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (timestamp < NextFrameAt) return false;

        do
        {
            NextFrameAt += FrameInterval;
        }
        while (NextFrameAt <= timestamp);
        return true;
    }
}

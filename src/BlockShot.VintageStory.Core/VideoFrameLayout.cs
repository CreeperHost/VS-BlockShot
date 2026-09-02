namespace BlockShot.VintageStory.Core;

/// <summary>
/// Describes an aspect-preserving video image centred inside an encoder-compatible canvas.
/// </summary>
public readonly record struct VideoFrameLayout(
    int ContentWidth,
    int ContentHeight,
    int CanvasWidth,
    int CanvasHeight)
{
    public int PaddingLeft => (CanvasWidth - ContentWidth) / 2;
    public int PaddingTop => (CanvasHeight - ContentHeight) / 2;

    public static VideoFrameLayout FitInside(
        int sourceWidth,
        int sourceHeight,
        int maximumWidth,
        int maximumHeight,
        int alignment = 16)
    {
        if (sourceWidth < 2 || sourceHeight < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "The source must be at least 2 by 2 pixels.");
        }
        if (alignment < 2 || (alignment & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "The alignment must be a positive even number.");
        }

        var alignedMaximumWidth = maximumWidth - (maximumWidth % alignment);
        var alignedMaximumHeight = maximumHeight - (maximumHeight % alignment);
        if (alignedMaximumWidth < alignment || alignedMaximumHeight < alignment)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumWidth), "The maximum canvas must contain one aligned block.");
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                alignedMaximumWidth / (double)sourceWidth,
                alignedMaximumHeight / (double)sourceHeight));
        var contentWidth = Math.Max(2, (int)Math.Floor(sourceWidth * scale) & ~1);
        var contentHeight = Math.Max(2, (int)Math.Floor(sourceHeight * scale) & ~1);
        var canvasWidth = AlignUp(contentWidth, alignment);
        var canvasHeight = AlignUp(contentHeight, alignment);

        return new VideoFrameLayout(contentWidth, contentHeight, canvasWidth, canvasHeight);
    }

    private static int AlignUp(int value, int alignment) =>
        checked(((value + alignment - 1) / alignment) * alignment);
}

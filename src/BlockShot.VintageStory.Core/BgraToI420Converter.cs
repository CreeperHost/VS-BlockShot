namespace BlockShot.VintageStory.Core;

/// <summary>
/// Converts tightly packed, top-down BGRA pixels to a tightly packed I420 frame.
/// The destination is reusable so video recording does not allocate a large YUV
/// buffer for every encoded frame.
/// </summary>
public static class BgraToI420Converter
{
    public static int RequiredByteCount(int width, int height)
    {
        ValidateDimensions(width, height);
        return checked(width * height * 3 / 2);
    }

    public static void Convert(
        ReadOnlySpan<byte> bgra,
        Span<byte> i420,
        int width,
        int height)
    {
        var pixelCount = checked(width * height);
        var requiredSourceBytes = checked(pixelCount * 4);
        var requiredDestinationBytes = RequiredByteCount(width, height);
        if (bgra.Length < requiredSourceBytes)
        {
            throw new ArgumentException("The BGRA source buffer is too small.", nameof(bgra));
        }
        if (i420.Length < requiredDestinationBytes)
        {
            throw new ArgumentException("The I420 destination buffer is too small.", nameof(i420));
        }

        var uOffset = pixelCount;
        var vOffset = pixelCount + pixelCount / 4;
        for (var y = 0; y < height; y += 2)
        {
            var row0 = y * width;
            var row1 = row0 + width;
            var chromaRow = y / 2 * (width / 2);
            for (var x = 0; x < width; x += 2)
            {
                var p00 = (row0 + x) * 4;
                var p01 = p00 + 4;
                var p10 = (row1 + x) * 4;
                var p11 = p10 + 4;

                WriteLuma(bgra, p00, i420, row0 + x);
                WriteLuma(bgra, p01, i420, row0 + x + 1);
                WriteLuma(bgra, p10, i420, row1 + x);
                WriteLuma(bgra, p11, i420, row1 + x + 1);

                var blue = bgra[p00] + bgra[p01] + bgra[p10] + bgra[p11];
                var green = bgra[p00 + 1] + bgra[p01 + 1] + bgra[p10 + 1] + bgra[p11 + 1];
                var red = bgra[p00 + 2] + bgra[p01 + 2] + bgra[p10 + 2] + bgra[p11 + 2];
                blue = (blue + 2) / 4;
                green = (green + 2) / 4;
                red = (red + 2) / 4;

                var chroma = chromaRow + x / 2;
                // VP8/WebM uses digital BT.601 Y'CbCr. Keep both chroma planes in studio
                // range (16..240); mixing full-range V with limited-range U visibly
                // over-emphasises red after standards-compliant browser decoding.
                i420[uOffset + chroma] = ClampToByte(((-38 * red - 74 * green + 112 * blue + 128) >> 8) + 128);
                i420[vOffset + chroma] = ClampToByte(((112 * red - 94 * green - 18 * blue + 128) >> 8) + 128);
            }
        }
    }

    private static void WriteLuma(
        ReadOnlySpan<byte> bgra,
        int sourceOffset,
        Span<byte> luma,
        int destinationOffset)
    {
        var blue = bgra[sourceOffset];
        var green = bgra[sourceOffset + 1];
        var red = bgra[sourceOffset + 2];
        luma[destinationOffset] = ClampToByte(((66 * red + 129 * green + 25 * blue + 128) >> 8) + 16);
    }

    private static byte ClampToByte(int value) => (byte)Math.Clamp(value, 0, 255);

    private static void ValidateDimensions(int width, int height)
    {
        if (width < 2 || height < 2 || (width & 1) != 0 || (height & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "I420 dimensions must be positive, even values of at least two pixels.");
        }
    }
}

using BlockShot.VintageStory.Core;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;
using Xunit;

namespace BlockShot.VintageStory.Core.Tests;

public sealed class MediaPipelineTests
{
    [Theory]
    [InlineData(0, 0, 0, 16, 128, 128)]
    [InlineData(255, 255, 255, 235, 128, 128)]
    [InlineData(128, 128, 128, 126, 128, 128)]
    [InlineData(0, 0, 255, 82, 90, 240)]
    [InlineData(0, 255, 0, 144, 54, 34)]
    [InlineData(255, 0, 0, 41, 240, 110)]
    [InlineData(80, 160, 200, 156, 87, 151)]
    public void Bgra_to_i420_emits_digital_bt601_studio_range(
        byte blue,
        byte green,
        byte red,
        byte expectedY,
        byte expectedU,
        byte expectedV)
    {
        var bgra = new byte[2 * 2 * 4];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = blue;
            bgra[offset + 1] = green;
            bgra[offset + 2] = red;
            bgra[offset + 3] = 255;
        }
        var i420 = new byte[BgraToI420Converter.RequiredByteCount(2, 2)];

        BgraToI420Converter.Convert(bgra, i420, 2, 2);

        Assert.All(i420[..4], value => Assert.Equal(expectedY, value));
        Assert.Equal(expectedU, i420[4]);
        Assert.Equal(expectedV, i420[5]);
    }

    [Fact]
    public void Bgra_to_i420_writes_tightly_packed_y_then_u_then_v_planes()
    {
        const int width = 4;
        const int height = 2;
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            SetBgra(bgra, width, 0, y, blue: 0, green: 0, red: 255);
            SetBgra(bgra, width, 1, y, blue: 0, green: 0, red: 255);
            SetBgra(bgra, width, 2, y, blue: 255, green: 0, red: 0);
            SetBgra(bgra, width, 3, y, blue: 255, green: 0, red: 0);
        }
        var i420 = new byte[BgraToI420Converter.RequiredByteCount(width, height)];

        BgraToI420Converter.Convert(bgra, i420, width, height);

        Assert.Equal<byte>([82, 82, 41, 41, 82, 82, 41, 41], i420[..8]);
        Assert.Equal<byte>([90, 240], i420[8..10]);
        Assert.Equal<byte>([240, 110], i420[10..12]);
    }

    [Fact]
    public void Pooled_i420_path_round_trips_bgra_primary_colours_through_vp8()
    {
        AssertRoundTrip(blue: 0, green: 0, red: 255, dominantOffset: 2);
        AssertRoundTrip(blue: 0, green: 255, red: 0, dominantOffset: 1);
        AssertRoundTrip(blue: 255, green: 0, red: 0, dominantOffset: 0);
    }

    [Fact]
    public void Bgra_to_i420_reuses_the_supplied_destination_without_allocating()
    {
        const int width = 16;
        const int height = 16;
        var bgra = new byte[width * height * 4];
        var i420 = new byte[BgraToI420Converter.RequiredByteCount(width, height)];
        BgraToI420Converter.Convert(bgra, i420, width, height);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var count = 0; count < 20; count++)
        {
            BgraToI420Converter.Convert(bgra, i420, width, height);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void AssertRoundTrip(byte blue, byte green, byte red, int dominantOffset)
    {
        const int width = 16;
        const int height = 16;
        var bgra = new byte[width * height * 4];
        for (var offset = 0; offset < bgra.Length; offset += 4)
        {
            bgra[offset] = blue;
            bgra[offset + 1] = green;
            bgra[offset + 2] = red;
            bgra[offset + 3] = 255;
        }
        var i420 = new byte[BgraToI420Converter.RequiredByteCount(width, height)];
        BgraToI420Converter.Convert(bgra, i420, width, height);

        using var encoder = new VP8Codec { BaseQIndex = 20 };
        var encoded = encoder.EncodeVideo(
            width,
            height,
            i420,
            VideoPixelFormatsEnum.I420,
            VideoCodecsEnum.VP8);
        using var decoder = new VP8Codec();
        var decoded = decoder.DecodeVideo(
            encoded,
            VideoPixelFormatsEnum.Bgra,
            VideoCodecsEnum.VP8).Single();

        AssertBgr(decoded.Sample, width, 8, 8, dominantOffset);
    }

    [Theory]
    [InlineData(10, 115)]
    [InlineData(40, 120)]
    [InlineData(100, 300)]
    public void Encoder_budget_enforces_rate_and_twenty_five_percent_duty_cycle(
        int workMilliseconds,
        int expectedRestMilliseconds)
    {
        var rest = EncoderWorkBudget.RestAfter(
            TimeSpan.FromMilliseconds(workMilliseconds),
            maximumFramesPerSecond: 8,
            maximumDutyCycle: 0.25);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedRestMilliseconds), rest);
    }

    private static void AssertBgr(byte[] pixels, int width, int x, int y, int dominantOffset)
    {
        // SIPSorcery's DecodeVideo currently returns packed BGR regardless of the requested
        // output enum. That is sufficient for a known-colour encoder regression here.
        var offset = (y * width + x) * 3;
        var dominant = pixels[offset + dominantOffset];
        var firstOther = pixels[offset + (dominantOffset + 1) % 3];
        var secondOther = pixels[offset + (dominantOffset + 2) % 3];
        var observed = $"BGR={pixels[offset]},{pixels[offset + 1]},{pixels[offset + 2]}";
        Assert.True(dominant > 180, $"Expected channel {dominantOffset} to dominate; {observed}.");
        Assert.True(dominant > firstOther + 100);
        Assert.True(dominant > secondOther + 100);
    }

    private static void SetBgra(
        byte[] pixels,
        int width,
        int x,
        int y,
        byte blue,
        byte green,
        byte red)
    {
        var offset = (y * width + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = 255;
    }
}

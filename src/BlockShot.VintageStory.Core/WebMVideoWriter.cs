using System.Buffers.Binary;
using System.Text;

namespace BlockShot.VintageStory.Core;

/// <summary>
/// Writes a seekable, video-only WebM stream from already encoded VP8 frames.
/// Timestamps use a one millisecond timecode scale, matching the SimpleBlock range
/// required by BlockShot's thirty second recording limit.
/// </summary>
public sealed class WebMVideoWriter : IDisposable
{
    private const uint Ebml = 0x1A45DFA3;
    private const uint Segment = 0x18538067;
    private const uint Info = 0x1549A966;
    private const uint Tracks = 0x1654AE6B;
    private const uint Cluster = 0x1F43B675;
    private const uint Timecode = 0xE7;
    private const uint SimpleBlock = 0xA3;
    private const long MaximumRelativeTimecode = short.MaxValue;

    private readonly Stream output;
    private readonly bool leaveOpen;
    private readonly int width;
    private readonly int height;
    private readonly int framesPerSecond;
    private readonly long durationPosition;
    private bool completed;
    private bool disposed;
    private long lastTimestampMilliseconds = -1;

    public WebMVideoWriter(
        Stream output,
        int width,
        int height,
        int framesPerSecond,
        bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        if (!output.CanWrite || !output.CanSeek)
        {
            throw new ArgumentException("A writable, seekable stream is required for WebM output.", nameof(output));
        }

        this.output = output;
        this.leaveOpen = leaveOpen;
        this.width = width;
        this.height = height;
        this.framesPerSecond = framesPerSecond;

        WriteEbmlHeader();
        WriteId(output, Segment);
        WriteUnknownSize(output);
        durationPosition = WriteInfo();
        WriteTracks();
        WriteId(output, Cluster);
        WriteUnknownSize(output);
        WriteUnsignedElement(output, Timecode, 0);
    }

    public int FrameCount { get; private set; }

    public void WriteFrame(ReadOnlySpan<byte> encodedVp8, TimeSpan timestamp, bool keyFrame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) throw new InvalidOperationException("The WebM stream has already been completed.");
        if (encodedVp8.IsEmpty) throw new ArgumentException("A VP8 frame cannot be empty.", nameof(encodedVp8));

        var milliseconds = checked((long)Math.Round(timestamp.TotalMilliseconds, MidpointRounding.AwayFromZero));
        if (milliseconds < 0 || milliseconds > MaximumRelativeTimecode)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp),
                "The frame timestamp must fit within the thirty-two second WebM cluster window.");
        }
        if (milliseconds < lastTimestampMilliseconds)
        {
            throw new ArgumentException("VP8 frame timestamps must be monotonic.", nameof(timestamp));
        }

        WriteId(output, SimpleBlock);
        WriteVariableSize(output, checked((ulong)encodedVp8.Length + 4));
        output.WriteByte(0x81); // Track number 1 as an EBML variable-length integer.
        Span<byte> relativeTimecode = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(relativeTimecode, checked((short)milliseconds));
        output.Write(relativeTimecode);
        output.WriteByte(keyFrame ? (byte)0x80 : (byte)0x00);
        output.Write(encodedVp8);

        lastTimestampMilliseconds = milliseconds;
        FrameCount++;
    }

    public void Complete(TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (completed) return;
        if (FrameCount == 0) throw new InvalidOperationException("A WebM video must contain at least one VP8 frame.");

        var durationMilliseconds = Math.Max(
            duration.TotalMilliseconds,
            lastTimestampMilliseconds + (1000d / framesPerSecond));
        if (!double.IsFinite(durationMilliseconds) || durationMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var endPosition = output.Position;
        output.Position = durationPosition;
        WriteFloat64(output, durationMilliseconds);
        output.Position = endPosition;
        output.Flush();
        completed = true;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!leaveOpen) output.Dispose();
    }

    private void WriteEbmlHeader()
    {
        using var content = new MemoryStream();
        WriteUnsignedElement(content, 0x4286, 1); // EBMLVersion
        WriteUnsignedElement(content, 0x42F7, 1); // EBMLReadVersion
        WriteUnsignedElement(content, 0x42F2, 4); // EBMLMaxIDLength
        WriteUnsignedElement(content, 0x42F3, 8); // EBMLMaxSizeLength
        WriteStringElement(content, 0x4282, "webm");
        WriteUnsignedElement(content, 0x4287, 2); // DocTypeVersion
        WriteUnsignedElement(content, 0x4285, 2); // DocTypeReadVersion
        WriteMaster(output, Ebml, content);
    }

    private long WriteInfo()
    {
        using var content = new MemoryStream();
        WriteUnsignedElement(content, 0x2AD7B1, 1_000_000); // One millisecond in nanoseconds.
        WriteStringElement(content, 0x4D80, "BlockShot");
        WriteStringElement(content, 0x5741, "BlockShot for Vintage Story");
        WriteId(content, 0x4489); // Duration
        WriteVariableSize(content, 8);
        var durationOffset = content.Position;
        WriteFloat64(content, 0);

        WriteId(output, Info);
        WriteVariableSize(output, checked((ulong)content.Length));
        var contentStart = output.Position;
        content.Position = 0;
        content.CopyTo(output);
        return contentStart + durationOffset;
    }

    private void WriteTracks()
    {
        using var video = new MemoryStream();
        WriteUnsignedElement(video, 0xB0, checked((ulong)width)); // PixelWidth
        WriteUnsignedElement(video, 0xBA, checked((ulong)height)); // PixelHeight

        using var track = new MemoryStream();
        WriteUnsignedElement(track, 0xD7, 1); // TrackNumber
        WriteUnsignedElement(track, 0x73C5, 1); // TrackUID
        WriteUnsignedElement(track, 0x83, 1); // Video track
        WriteUnsignedElement(track, 0x9C, 0); // Lacing disabled
        WriteStringElement(track, 0x86, "V_VP8");
        WriteUnsignedElement(track, 0x23E383, checked((ulong)Math.Round(1_000_000_000d / framesPerSecond)));
        WriteMaster(track, 0xE0, video);

        using var tracks = new MemoryStream();
        WriteMaster(tracks, 0xAE, track);
        WriteMaster(output, Tracks, tracks);
    }

    private static void WriteMaster(Stream target, uint id, MemoryStream content)
    {
        WriteId(target, id);
        WriteVariableSize(target, checked((ulong)content.Length));
        content.Position = 0;
        content.CopyTo(target);
    }

    private static void WriteUnsignedElement(Stream target, uint id, ulong value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        var first = 0;
        while (first < 7 && bytes[first] == 0) first++;
        WriteId(target, id);
        WriteVariableSize(target, checked((ulong)(8 - first)));
        target.Write(bytes[first..]);
    }

    private static void WriteStringElement(Stream target, uint id, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteId(target, id);
        WriteVariableSize(target, checked((ulong)bytes.Length));
        target.Write(bytes);
    }

    private static void WriteFloat64(Stream target, double value)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        target.Write(bytes);
    }

    private static void WriteId(Stream target, uint id)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, id);
        var first = 0;
        while (first < 3 && bytes[first] == 0) first++;
        target.Write(bytes[first..]);
    }

    private static void WriteVariableSize(Stream target, ulong value)
    {
        for (var length = 1; length <= 8; length++)
        {
            var valueBits = length * 7;
            var maximum = (1UL << valueBits) - 2;
            if (value > maximum) continue;

            Span<byte> encoded = stackalloc byte[8];
            var remaining = value;
            for (var index = length - 1; index >= 0; index--)
            {
                encoded[index] = (byte)remaining;
                remaining >>= 8;
            }
            encoded[0] |= (byte)(1 << (8 - length));
            target.Write(encoded[..length]);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(value), "The EBML element is too large.");
    }

    private static void WriteUnknownSize(Stream target)
    {
        target.WriteByte(0x01);
        for (var index = 1; index < 8; index++) target.WriteByte(0xFF);
    }
}

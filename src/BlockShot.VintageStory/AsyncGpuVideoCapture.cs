using System.Buffers;
using System.Runtime.InteropServices;
using BlockShot.VintageStory.Core;
using OpenTK.Graphics.OpenGL4;

namespace BlockShot.VintageStory;

/// <summary>
/// Downscales and flips the final framebuffer on the GPU, then transfers it through
/// a ring of pixel-buffer objects. Fence polling always uses a zero timeout.
/// </summary>
internal sealed class AsyncGpuVideoCapture : IDisposable
{
    // Completed transfers are drained before each scheduled capture. One PBO therefore covers
    // the 15fps cadence without keeping another full-frame GPU allocation resident.
    private const int ReadbackSlotCount = 1;

    private readonly VideoFrameLayout layout;
    private readonly int byteCount;
    private readonly Stack<ReadbackSlot> available = new(ReadbackSlotCount);
    private readonly Queue<ReadbackSlot> pending = new(ReadbackSlotCount);
    private int framebuffer;
    private int texture;
    private bool disposed;

    public AsyncGpuVideoCapture(VideoFrameLayout layout)
    {
        this.layout = layout;
        byteCount = checked(layout.CanvasWidth * layout.CanvasHeight * 4);
        CreateResources();
    }

    public int PendingCount => pending.Count;

    public bool TryIssue(int sourceWidth, int sourceHeight, TimeSpan timestamp)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sourceWidth < 1 || sourceHeight < 1 || available.Count == 0) return false;

        var slot = available.Pop();
        var previousReadFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        var previousDrawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        var previousReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        var previousPackAlignment = GL.GetInteger(GetPName.PackAlignment);
        var framebufferSrgbEnabled = GL.IsEnabled(EnableCap.FramebufferSrgb);
        try
        {
            ClearPreviousErrors();

            // Match Vintage Story's native screenshot path: the final, colour-correct image
            // is in the default back buffer. The framebuffer left bound at Done can be an
            // internal post-process target with a different colour representation.
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);

            // The back buffer contains display-ready sRGB bytes. With FRAMEBUFFER_SRGB enabled,
            // BlitFramebuffer decodes an sRGB read buffer to linear light; our Rgba8 draw target
            // cannot re-encode it, so the video would no longer match a direct screenshot read.
            // Disable conversion only for this pixel copy and restore Vintage Story's state below.
            if (framebufferSrgbEnabled) GL.Disable(EnableCap.FramebufferSrgb);
            GL.BlitFramebuffer(
                0,
                0,
                sourceWidth,
                sourceHeight,
                layout.PaddingLeft,
                layout.PaddingTop + layout.ContentHeight,
                layout.PaddingLeft + layout.ContentWidth,
                layout.PaddingTop,
                ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Linear);

            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, framebuffer);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
            GL.ReadPixels(
                0,
                0,
                layout.CanvasWidth,
                layout.CanvasHeight,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                IntPtr.Zero);

            var error = GL.GetError();
            if (error != ErrorCode.NoError)
            {
                throw new InvalidOperationException($"OpenGL rejected asynchronous BlockShot capture ({error}).");
            }

            slot.Fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            if (slot.Fence == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenGL could not create a BlockShot readback fence.");
            }
            slot.Timestamp = timestamp;
            pending.Enqueue(slot);
            return true;
        }
        catch
        {
            if (slot.Fence != IntPtr.Zero)
            {
                GL.DeleteSync(slot.Fence);
                slot.Fence = IntPtr.Zero;
            }
            available.Push(slot);
            throw;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
            GL.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            GL.ReadBuffer((ReadBufferMode)previousReadBuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDrawFramebuffer);
            if (framebufferSrgbEnabled) GL.Enable(EnableCap.FramebufferSrgb);
        }
    }

    /// <summary>
    /// Returns false when the oldest GPU transfer is not ready. A true result with
    /// a null frame means the completed transfer was intentionally discarded.
    /// </summary>
    public bool TryTakeCompleted(bool copyPixels, out PooledVideoFrame? frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        frame = null;
        if (pending.Count == 0) return false;

        var slot = pending.Peek();
        var status = GL.ClientWaitSync(slot.Fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 0);
        if (status == WaitSyncStatus.TimeoutExpired) return false;
        if (status == WaitSyncStatus.WaitFailed)
        {
            throw new InvalidOperationException("OpenGL failed while polling a BlockShot readback fence.");
        }

        pending.Dequeue();
        GL.DeleteSync(slot.Fence);
        slot.Fence = IntPtr.Zero;

        byte[]? pixels = null;
        var mapped = IntPtr.Zero;
        var unmapSucceeded = true;
        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        try
        {
            if (copyPixels)
            {
                GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
                mapped = GL.MapBufferRange(
                    BufferTarget.PixelPackBuffer,
                    IntPtr.Zero,
                    byteCount,
                    MapBufferAccessMask.MapReadBit);
                if (mapped == IntPtr.Zero)
                {
                    throw new InvalidOperationException("OpenGL could not map a completed BlockShot frame.");
                }

                pixels = ArrayPool<byte>.Shared.Rent(byteCount);
                Marshal.Copy(mapped, pixels, 0, byteCount);
            }
        }
        catch
        {
            if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        finally
        {
            if (mapped != IntPtr.Zero) unmapSucceeded = GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
            available.Push(slot);
        }

        if (!unmapSucceeded)
        {
            if (pixels is not null) ArrayPool<byte>.Shared.Return(pixels);
            throw new InvalidOperationException("OpenGL reported corrupt BlockShot pixel-buffer data.");
        }
        if (pixels is not null) frame = new PooledVideoFrame(pixels, slot.Timestamp);
        return true;
    }

    public static void Return(PooledVideoFrame frame) => ArrayPool<byte>.Shared.Return(frame.Pixels);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        foreach (var slot in pending)
        {
            if (slot.Fence != IntPtr.Zero) GL.DeleteSync(slot.Fence);
        }
        foreach (var slot in pending.Concat(available))
        {
            if (slot.Buffer != 0) GL.DeleteBuffer(slot.Buffer);
        }
        pending.Clear();
        available.Clear();

        if (framebuffer != 0) GL.DeleteFramebuffer(framebuffer);
        if (texture != 0) GL.DeleteTexture(texture);
        framebuffer = 0;
        texture = 0;
    }

    private void CreateResources()
    {
        var previousReadFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        var previousDrawFramebuffer = GL.GetInteger(GetPName.DrawFramebufferBinding);
        var previousTexture = GL.GetInteger(GetPName.TextureBinding2D);
        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        try
        {
            texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba8,
                layout.CanvasWidth,
                layout.CanvasHeight,
                0,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                new byte[byteCount]);

            framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"OpenGL could not create BlockShot's capture framebuffer ({status}).");
            }

            for (var index = 0; index < ReadbackSlotCount; index++)
            {
                var slot = new ReadbackSlot { Buffer = GL.GenBuffer() };
                GL.BindBuffer(BufferTarget.PixelPackBuffer, slot.Buffer);
                GL.BufferData(BufferTarget.PixelPackBuffer, byteCount, IntPtr.Zero, BufferUsageHint.StreamRead);
                available.Push(slot);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
            GL.BindTexture(TextureTarget.Texture2D, previousTexture);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, previousDrawFramebuffer);
        }
    }

    private static void ClearPreviousErrors()
    {
        while (GL.GetError() != ErrorCode.NoError)
        {
        }
    }

    private sealed class ReadbackSlot
    {
        public int Buffer;
        public IntPtr Fence;
        public TimeSpan Timestamp;
    }
}

internal sealed record PooledVideoFrame(byte[] Pixels, TimeSpan Timestamp);

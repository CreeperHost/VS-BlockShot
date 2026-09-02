using System.Buffers;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockShot.VintageStory;

/// <summary>
/// Captures the composed framebuffer without synchronously reading it back to the CPU.
/// OpenGL stays on the render thread; copying, flipping, PNG encoding, and disk I/O run on a
/// worker after a zero-timeout fence poll reports that the pixel-buffer transfer is ready.
/// </summary>
internal sealed class BlockShotCaptureRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly object sync = new();
    private CaptureRequest? pending;
    private CaptureRequest? readback;
    private CaptureResult? completed;
    private Task? encodingTask;
    private int pixelBuffer;
    private int bufferBytes;
    private IntPtr fence;
    private IntPtr mappedPixels;
    private bool preparationFailed;
    private bool disposed;

    public BlockShotCaptureRenderer(ICoreClientAPI api)
    {
        this.api = api;
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "blockshot-capture");
    }

    public double RenderOrder => 2.1;

    public int RenderRange => 0;

    public bool Queue(string path, Action<string?, Exception?> completedCallback)
    {
        lock (sync)
        {
            if (disposed || pending is not null || readback is not null) return false;
            pending = new CaptureRequest(path, completedCallback);
            return true;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (disposed) return;
        FinalizeCompletedCapture();
        if (readback is not null)
        {
            BeginEncodingWhenReady();
            return;
        }

        // Resize during ordinary rendering, before a capture request, so even the first capture
        // does not pay for a full-frame buffer allocation.
        PrepareReadbackBuffer(api.Render.FrameWidth, api.Render.FrameHeight);

        CaptureRequest? request;
        lock (sync)
        {
            request = pending;
            pending = null;
        }
        if (request is null) return;

        try
        {
            IssueReadback(request, api.Render.FrameWidth, api.Render.FrameHeight);
        }
        catch (Exception error)
        {
            CleanupGpuTransfer();
            CompleteOnMainThread(request, null, error);
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            pending = null;
        }
        api.Event.UnregisterRenderer(this, EnumRenderStage.Done);

        // A mapped PBO must remain alive until its worker has copied the bytes. Mod disposal is
        // rare and is the only path allowed to wait for an already-running encode operation.
        try
        {
            encodingTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }
        CleanupGpuTransfer();
        if (pixelBuffer != 0) GL.DeleteBuffer(pixelBuffer);
        pixelBuffer = 0;
        bufferBytes = 0;
    }

    private void PrepareReadbackBuffer(int width, int height)
    {
        if (width < 1 || height < 1 || readback is not null || encodingTask is not null) return;
        var requiredBytes = checked(width * height * 4);
        if (pixelBuffer != 0 && bufferBytes == requiredBytes) return;

        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        try
        {
            if (pixelBuffer == 0) pixelBuffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pixelBuffer);
            GL.BufferData(BufferTarget.PixelPackBuffer, requiredBytes, IntPtr.Zero, BufferUsageHint.StreamRead);
            bufferBytes = requiredBytes;
            preparationFailed = false;
        }
        catch (Exception error)
        {
            bufferBytes = 0;
            if (!preparationFailed)
            {
                preparationFailed = true;
                api.Logger.Warning("BlockShot could not prepare asynchronous screenshot capture: {0}", error.Message);
            }
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
        }
    }

    private void IssueReadback(CaptureRequest request, int width, int height)
    {
        var requiredBytes = checked(width * height * 4);
        if (pixelBuffer == 0 || bufferBytes != requiredBytes)
        {
            PrepareReadbackBuffer(width, height);
        }
        if (pixelBuffer == 0 || bufferBytes != requiredBytes)
        {
            throw new InvalidOperationException("BlockShot could not allocate its asynchronous screenshot buffer.");
        }

        var previousReadFramebuffer = GL.GetInteger(GetPName.ReadFramebufferBinding);
        var previousReadBuffer = GL.GetInteger(GetPName.ReadBuffer);
        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        var previousPackAlignment = GL.GetInteger(GetPName.PackAlignment);
        try
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
            GL.ReadBuffer(ReadBufferMode.Back);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pixelBuffer);
            GL.ReadPixels(0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, IntPtr.Zero);
            fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
            if (fence == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenGL could not create a BlockShot screenshot fence.");
            }
            readback = request with { Width = width, Height = height };
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
            GL.PixelStore(PixelStoreParameter.PackAlignment, previousPackAlignment);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, previousReadFramebuffer);
            GL.ReadBuffer((ReadBufferMode)previousReadBuffer);
        }
    }

    private void BeginEncodingWhenReady()
    {
        if (readback is null || encodingTask is not null || fence == IntPtr.Zero) return;
        var status = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, 0);
        if (status == WaitSyncStatus.TimeoutExpired) return;
        if (status == WaitSyncStatus.WaitFailed)
        {
            var request = readback;
            CleanupGpuTransfer();
            readback = null;
            CompleteOnMainThread(
                request,
                null,
                new InvalidOperationException("OpenGL failed while polling the BlockShot screenshot fence."));
            return;
        }

        GL.DeleteSync(fence);
        fence = IntPtr.Zero;
        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        try
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pixelBuffer);
            mappedPixels = GL.MapBufferRange(
                BufferTarget.PixelPackBuffer,
                IntPtr.Zero,
                bufferBytes,
                MapBufferAccessMask.MapReadBit);
            if (mappedPixels == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenGL could not map the completed BlockShot screenshot.");
            }
        }
        catch (Exception error)
        {
            var request = readback;
            CleanupGpuTransfer();
            readback = null;
            CompleteOnMainThread(request, null, error);
            return;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
        }

        var encodingRequest = readback;
        var source = mappedPixels;
        encodingTask = Task.Run(() => EncodeMappedPixels(encodingRequest, source));
    }

    private void EncodeMappedPixels(CaptureRequest request, IntPtr source)
    {
        CaptureResult result;
        var byteCount = checked(request.Width * request.Height * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            Marshal.Copy(source, pixels, 0, byteCount);
            Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
            using var bitmap = new BitmapExternal(request.Width, request.Height);
            var destination = bitmap.PixelsPtrAndLock;
            var stride = checked(request.Width * 4);
            for (var y = 0; y < request.Height; y++)
            {
                var sourceOffset = (request.Height - 1 - y) * stride;
                for (var x = sourceOffset + 3; x < sourceOffset + stride; x += 4)
                {
                    pixels[x] = 255;
                }
                Marshal.Copy(pixels, sourceOffset, IntPtr.Add(destination, y * stride), stride);
            }
            bitmap.Save(request.Path);
            result = new CaptureResult(request, request.Path, null);
        }
        catch (Exception error)
        {
            result = new CaptureResult(request, null, error);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }

        lock (sync)
        {
            completed = result;
        }
    }

    private void FinalizeCompletedCapture()
    {
        CaptureResult? result;
        lock (sync)
        {
            result = completed;
            completed = null;
        }
        if (result is null) return;

        CleanupGpuTransfer();
        readback = null;
        encodingTask = null;
        CompleteOnMainThread(result.Request, result.Path, result.Error);
    }

    private void CleanupGpuTransfer()
    {
        if (fence != IntPtr.Zero)
        {
            GL.DeleteSync(fence);
            fence = IntPtr.Zero;
        }
        if (mappedPixels == IntPtr.Zero || pixelBuffer == 0) return;

        var previousPixelPackBuffer = GL.GetInteger(GetPName.PixelPackBufferBinding);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, pixelBuffer);
        GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, previousPixelPackBuffer);
        mappedPixels = IntPtr.Zero;
    }

    private void CompleteOnMainThread(CaptureRequest request, string? path, Exception? error) =>
        api.Event.EnqueueMainThreadTask(
            () => request.Completed(path, error),
            "blockshot-capture-complete");

    private sealed record CaptureRequest(
        string Path,
        Action<string?, Exception?> Completed,
        int Width = 0,
        int Height = 0);

    private sealed record CaptureResult(CaptureRequest Request, string? Path, Exception? Error);
}

using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BlockShot.VintageStory;

/// <summary>Captures the fully composed framebuffer on the next completed render frame.</summary>
internal sealed class BlockShotCaptureRenderer : IRenderer
{
    private readonly ICoreClientAPI api;
    private readonly object sync = new();
    private CaptureRequest? pending;
    private bool disposed;

    public BlockShotCaptureRenderer(ICoreClientAPI api)
    {
        this.api = api;
        api.Event.RegisterRenderer(this, EnumRenderStage.Done, "blockshot-capture");
    }

    public double RenderOrder => 2.1;

    public int RenderRange => 0;

    public bool Queue(string path, Action<string?, Exception?> completed)
    {
        lock (sync)
        {
            if (disposed || pending is not null) return false;
            pending = new CaptureRequest(path, completed);
            return true;
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        CaptureRequest? request;
        lock (sync)
        {
            request = pending;
            pending = null;
        }
        if (request is null) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(request.Path)!);
            using var bitmap = api.Render.GrabScreenshot(
                api.Render.FrameWidth,
                api.Render.FrameHeight,
                scaleScreenshot: false,
                flip: true);
            bitmap.Save(request.Path);
            request.Completed(request.Path, null);
        }
        catch (Exception error)
        {
            request.Completed(null, error);
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
    }

    private sealed record CaptureRequest(string Path, Action<string?, Exception?> Completed);
}

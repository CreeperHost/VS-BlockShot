using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace BlockShot.VintageStory;

internal sealed record BlockShotPreviewPixels(int Width, int Height, int[] Bgra)
{
    private const int MaximumDimension = 2048;
    private const int MaximumPixels = MaximumDimension * MaximumDimension;

    public static BlockShotPreviewPixels DecodePng(byte[] png, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(png);
        using var stream = new MemoryStream(png, writable: false);
        using var bitmap = new BitmapExternal(stream, logger);
        if (bitmap.Width <= 0 || bitmap.Height <= 0 ||
            bitmap.Width > MaximumDimension || bitmap.Height > MaximumDimension ||
            (long)bitmap.Width * bitmap.Height > MaximumPixels)
        {
            throw new InvalidDataException("BlockShot returned an invalid preview size.");
        }

        var pixels = bitmap.Pixels;
        if (pixels.Length != bitmap.Width * bitmap.Height)
        {
            throw new InvalidDataException("BlockShot returned incomplete preview pixels.");
        }
        return new BlockShotPreviewPixels(bitmap.Width, bitmap.Height, [.. pixels]);
    }
}

/// <summary>Renders a dialog-owned texture without taking ownership of its GPU lifetime.</summary>
internal sealed class BlockShotPreviewElement(ICoreClientAPI api, ElementBounds bounds)
    : GuiElement(api, bounds)
{
    public LoadedTexture? Texture { get; set; }

    public override void RenderInteractiveElements(float deltaTime)
    {
        var texture = Texture;
        if (texture is null || texture.TextureId == 0 || texture.Width <= 0 || texture.Height <= 0) return;

        var scale = Math.Min(Bounds.OuterWidth / texture.Width, Bounds.OuterHeight / texture.Height);
        var width = texture.Width * scale;
        var height = texture.Height * scale;
        var x = Bounds.renderX + ((Bounds.OuterWidth - width) / 2);
        var y = Bounds.renderY + ((Bounds.OuterHeight - height) / 2);
        Render2DTexture(texture.TextureId, x, y, width, height, 50f, ColorUtil.WhiteArgbVec);
    }
}

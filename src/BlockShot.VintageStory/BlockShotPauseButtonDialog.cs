using Cairo;
using Vintagestory.API.Client;

namespace BlockShot.VintageStory;

/// <summary>Public-API companion button shown beside the vanilla escape menu.</summary>
internal sealed class BlockShotPauseButtonDialog : GuiDialog
{
    private const string MineTogetherModId = "minetogether";
    private const double DefaultLeft = 20;
    private const double MineTogetherRight = 170;
    private const double ButtonGap = 6;
    private const double ButtonSize = 46;
    private const double IconInset = 7;
    private const double IconSize = 32;
    private readonly Action openBlockShot;

    public BlockShotPauseButtonDialog(ICoreClientAPI api, Action openBlockShot)
        : base(api)
    {
        this.openBlockShot = openBlockShot;
        var left = api.ModLoader.IsModEnabled(MineTogetherModId)
            ? MineTogetherRight + ButtonGap
            : DefaultLeft;
        var bounds = ElementBounds.Fixed(EnumDialogArea.LeftTop, left, 20, ButtonSize, ButtonSize);
        var composer = capi.Gui
            .CreateCompo("blockshot-pause-button", bounds)
            .AddButton(string.Empty, OnOpen, ElementBounds.Fixed(0, 0, ButtonSize, ButtonSize));
        composer.AddDynamicCustomDraw(
            ElementBounds.Fixed(IconInset, IconInset, IconSize, IconSize),
            DrawCameraIcon,
            "blockshot-pause-icon");
        SingleComposer = composer.Compose();
    }

    public override string ToggleKeyCombinationCode => "blockshot-pause-button";

    public override double DrawOrder => 0.9;

    public override double InputOrder => -0.1;

    public override bool ShouldReceiveKeyboardEvents() => false;

    private static void DrawCameraIcon(Context context, ImageSurface surface, ElementBounds _)
    {
        var scale = Math.Min(surface.Width, surface.Height) / IconSize;
        var offsetX = (surface.Width - (IconSize * scale)) / 2;
        var offsetY = (surface.Height - (IconSize * scale)) / 2;

        context.Save();
        context.Translate(offsetX, offsetY);
        context.Scale(scale, scale);
        context.SetSourceRGBA(0.02, 0.68, 0.34, 1);
        context.LineWidth = 2.5;

        context.Rectangle(3.5, 9.5, 25, 17);
        context.Stroke();
        context.MoveTo(9, 9.5);
        context.LineTo(11.5, 5.5);
        context.LineTo(18.5, 5.5);
        context.LineTo(21, 9.5);
        context.Stroke();
        context.Arc(16, 18, 5.25, 0, Math.PI * 2);
        context.Stroke();
        context.Arc(24.5, 13.5, 1.25, 0, Math.PI * 2);
        context.Fill();
        context.Restore();
    }

    private bool OnOpen()
    {
        TryClose();
        openBlockShot();
        return true;
    }
}

using System.Numerics;
using Content.Client.UserInterface.Systems.Hotbar.Widgets;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Client.ResourceManagement;
using Robust.Client.UserInterface;
using Robust.Shared.Enums;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Client side of the Proper Kung Fu ultimate: the red 0-100% bar above the hotbar for the local master,
/// and the big "alpha's call" text a summoned Pack Dog sees for a moment.
/// </summary>
public sealed class ProperKungFuUltimateOverlaySystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayMan = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private ProperKungFuUltimateBarOverlay _bar = default!;
    private ProperKungFuPackCallOverlay _call = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ProperKungFuPackSummonedEvent>(OnPackSummoned);

        _bar = new ProperKungFuUltimateBarOverlay();
        _call = new ProperKungFuPackCallOverlay();
        _overlayMan.AddOverlay(_bar);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay(_bar);
        _overlayMan.RemoveOverlay(_call);
    }

    private void OnPackSummoned(ProperKungFuPackSummonedEvent ev)
    {
        // The server sends this only to the summoned dog's own session.
        _call.Start = _timing.CurTime;
        _call.Duration = TimeSpan.FromSeconds(Math.Max(0.1f, ev.Duration));
        if (!_overlayMan.HasOverlay<ProperKungFuPackCallOverlay>())
            _overlayMan.AddOverlay(_call);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (_overlayMan.HasOverlay<ProperKungFuPackCallOverlay>() && _timing.CurTime > _call.Start + _call.Duration)
            _overlayMan.RemoveOverlay(_call);
    }
}

/// <summary>
/// A red bar with the charge percentage right above the hotbar, only for the local master (not pupils or clones).
/// While the ultimate runs it shows the time left instead.
/// </summary>
public sealed class ProperKungFuUltimateBarOverlay : Overlay
{
    [Dependency] private readonly IEntityManager _entMan = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    private static readonly Color Back = Color.FromHex("#1A0A0CD0");
    private static readonly Color Frame = Color.FromHex("#3A0E14");
    private static readonly Color Fill = Color.FromHex("#C8102E");
    private static readonly Color FillFull = Color.FromHex("#FF3040");
    private static readonly Color FillActive = Color.FromHex("#FF9020");

    private readonly Font _font;

    public ProperKungFuUltimateBarOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 10);
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        return _player.LocalEntity is { } local
               && _entMan.TryGetComponent<ProperKungFuComponent>(local, out var comp)
               && comp.HasPassives;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (_player.LocalEntity is not { } local || !_entMan.TryGetComponent<ProperKungFuComponent>(local, out var comp))
            return;

        var handle = args.ScreenHandle;
        var scale = _ui.RootControl.UIScale;
        var size = new Vector2(240f, 14f) * scale;

        // Centered right above the hotbar; the bottom of the window if the layout has none.
        Vector2 center;
        float top;
        if (_ui.ActiveScreen?.GetWidget<HotbarGui>() is { } hotbar && hotbar.Visible && hotbar.PixelSize.Y > 0)
        {
            var rect = hotbar.GlobalPixelRect;
            center = new Vector2((rect.Left + rect.Right) / 2f, rect.Top);
            top = rect.Top - size.Y - 8f * scale;
        }
        else
        {
            var vp = args.ViewportBounds;
            center = new Vector2((vp.Left + vp.Right) / 2f, vp.Bottom);
            top = vp.Bottom - 170f * scale;
        }

        var box = UIBox2.FromDimensions(new Vector2(center.X - size.X / 2f, top), size);
        var pad = new Vector2(2f * scale, 2f * scale);
        handle.DrawRect(new UIBox2(box.TopLeft - pad, box.BottomRight + pad), Frame);
        handle.DrawRect(box, Back);

        string text;
        if (comp.UltimateActive && comp.UltimateEndTime is { } end)
        {
            var left = end - _timing.CurTime;
            if (left < TimeSpan.Zero)
                left = TimeSpan.Zero;
            var frac = (float) Math.Clamp(left.TotalSeconds / Math.Max(1, comp.UltimateDuration.TotalSeconds), 0, 1);
            handle.DrawRect(new UIBox2(box.Left, box.Top, box.Left + box.Width * frac, box.Bottom), FillActive);
            text = Loc.GetString("proper-kung-fu-ultimate-bar-active", ("time", $"{(int) left.TotalMinutes}:{left.Seconds:D2}"));
        }
        else
        {
            var frac = Math.Clamp(comp.UltimateCharge / Math.Max(1f, comp.MaxUltimateCharge), 0f, 1f);
            var pulse = frac >= 1f ? 0.75f + 0.25f * MathF.Sin((float) _timing.RealTime.TotalSeconds * 6f) : 1f;
            handle.DrawRect(new UIBox2(box.Left, box.Top, box.Left + box.Width * frac, box.Bottom),
                (frac >= 1f ? FillFull : Fill).WithAlpha(pulse));
            text = Loc.GetString("proper-kung-fu-ultimate-bar", ("percent", (int) MathF.Floor(frac * 100f)));
        }

        var dims = handle.GetDimensions(_font, text, scale);
        var pos = new Vector2(box.Center.X - dims.X / 2f, box.Center.Y - dims.Y / 2f);
        handle.DrawString(_font, pos + new Vector2(1f, 1f) * scale, text, scale, Color.Black);
        handle.DrawString(_font, pos, text, scale, Color.White);
    }
}

/// <summary>
/// Big blood-red text in the middle of the summoned Pack Dog's screen that fades out over its duration.
/// </summary>
public sealed class ProperKungFuPackCallOverlay : Overlay
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IUserInterfaceManager _ui = default!;
    [Dependency] private readonly IResourceCache _cache = default!;

    public override OverlaySpace Space => OverlaySpace.ScreenSpace;

    public TimeSpan Start;
    public TimeSpan Duration = TimeSpan.FromSeconds(2);

    private readonly Font _font;

    public ProperKungFuPackCallOverlay()
    {
        IoCManager.InjectDependencies(this);
        _font = new VectorFont(_cache.GetResource<FontResource>("/Fonts/NotoSans/NotoSans-Bold.ttf"), 26);
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        var elapsed = (float) ((_timing.CurTime - Start).TotalSeconds / Math.Max(0.1, Duration.TotalSeconds));
        if (elapsed is < 0f or > 1f)
            return;

        // Full strength for the first 60%, then fades out.
        var alpha = elapsed < 0.6f ? 1f : 1f - (elapsed - 0.6f) / 0.4f;
        var handle = args.ScreenHandle;
        var scale = _ui.RootControl.UIScale;
        var text = Loc.GetString("proper-kung-fu-summon-pack-call");
        var dims = handle.GetDimensions(_font, text, scale);
        var vp = args.ViewportBounds;
        var pos = new Vector2((vp.Left + vp.Right) / 2f - dims.X / 2f, vp.Top + vp.Height * 0.3f - dims.Y / 2f);

        foreach (var off in new[] { new Vector2(-2, 0), new Vector2(2, 0), new Vector2(0, -2), new Vector2(0, 2) })
        {
            handle.DrawString(_font, pos + off * scale, text, scale, Color.Black.WithAlpha(alpha * 0.85f));
        }

        handle.DrawString(_font, pos, text, scale, Color.FromHex("#FF3030").WithAlpha(alpha));
    }
}

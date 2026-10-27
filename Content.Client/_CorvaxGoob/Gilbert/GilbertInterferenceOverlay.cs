using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Full-viewport effect near Gilbert (v8: no static): branching crimson discharges and a pulsing red frame.
/// <see cref="Intensity"/> is set by <see cref="GilbertInterferenceSystem"/>; nothing is drawn at zero.
/// </summary>
public sealed class GilbertInterferenceOverlay : Overlay
{
    public static readonly ProtoId<ShaderPrototype> Shader = "GilbertInterference";

    /// <summary>Below this the overlay isn't drawn at all.</summary>
    public const float DrawThreshold = 0.005f;

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;

    /// <summary>Current (already smoothed) interference, 0..1.</summary>
    public float Intensity;

    public GilbertInterferenceOverlay()
    {
        IoCManager.InjectDependencies(this);
        _shader = _prototypeManager.Index(Shader).InstanceUnique();
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (Intensity < DrawThreshold)
            return false;

        if (!_entityManager.TryGetComponent(_playerManager.LocalEntity, out EyeComponent? eye))
            return false;

        return args.Viewport.Eye == eye.Eye;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        var handle = args.WorldHandle;
        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _shader.SetParameter("intensity", Intensity);
        handle.UseShader(_shader);
        handle.DrawRect(args.WorldBounds, Color.White);
        handle.UseShader(null);
    }
}

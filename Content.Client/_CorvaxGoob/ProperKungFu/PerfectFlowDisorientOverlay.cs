using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.StatusEffectNew;
using Robust.Client.Graphics;
using Robust.Client.Player;
using Robust.Shared.Enums;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Shows the Perfect Flow disorientation overlay for the local player while the status effect lasts.
/// </summary>
public sealed class PerfectFlowDisorientSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly IOverlayManager _overlayMan = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    private PerfectFlowDisorientOverlay _overlay = default!;

    public override void Initialize()
    {
        base.Initialize();

        // The shared system already owns StatusEffectApplied/Removed for the effect entity, so follow the
        // mob-side component it adds and removes together with the status effect.
        SubscribeLocalEvent<ProperKungFuDisorientedComponent, ComponentStartup>(OnApplied);
        SubscribeLocalEvent<ProperKungFuDisorientedComponent, ComponentShutdown>(OnRemoved);
        SubscribeLocalEvent<ProperKungFuDisorientedStatusEffectComponent, StatusEffectRelayedEvent<LocalPlayerAttachedEvent>>(OnPlayerAttached);
        SubscribeLocalEvent<ProperKungFuDisorientedStatusEffectComponent, StatusEffectRelayedEvent<LocalPlayerDetachedEvent>>(OnPlayerDetached);

        _overlay = new();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayMan.RemoveOverlay(_overlay);
    }

    private void OnApplied(Entity<ProperKungFuDisorientedComponent> ent, ref ComponentStartup args)
    {
        if (_player.LocalEntity != ent.Owner)
            return;

        _overlay.FlashStart = _timing.CurTime;
        _overlayMan.AddOverlay(_overlay);
    }

    private void OnRemoved(Entity<ProperKungFuDisorientedComponent> ent, ref ComponentShutdown args)
    {
        if (_player.LocalEntity != ent.Owner)
            return;

        _overlayMan.RemoveOverlay(_overlay);
    }

    private void OnPlayerAttached(Entity<ProperKungFuDisorientedStatusEffectComponent> ent, ref StatusEffectRelayedEvent<LocalPlayerAttachedEvent> args)
    {
        _overlayMan.AddOverlay(_overlay);
    }

    private void OnPlayerDetached(Entity<ProperKungFuDisorientedStatusEffectComponent> ent, ref StatusEffectRelayedEvent<LocalPlayerDetachedEvent> args)
    {
        _overlayMan.RemoveOverlay(_overlay);
    }
}

/// <summary>
/// Heavy blur, double vision, colour split, a pulsing dark red vignette and a fading white flash,
/// stronger than a flashbang aftereffect and drunkenness together. Fades out over the last second and a half.
/// </summary>
public sealed class PerfectFlowDisorientOverlay : Overlay
{
    private static readonly ProtoId<ShaderPrototype> Shader = "ProperKungFuDisoriented";

    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly IEntitySystemManager _sysMan = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    private readonly StatusEffectsSystem _statusEffects;

    public override OverlaySpace Space => OverlaySpace.WorldSpace;
    public override bool RequestScreenTexture => true;

    private readonly ShaderInstance _shader;

    public TimeSpan FlashStart;

    private const float FlashTime = 1.2f;
    private const float FadeOutTime = 1.5f;

    private float _strength;
    private float _flash;

    public PerfectFlowDisorientOverlay()
    {
        IoCManager.InjectDependencies(this);
        _statusEffects = _sysMan.GetEntitySystem<StatusEffectsSystem>();
        _shader = _prototypeManager.Index(Shader).InstanceUnique();
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        _strength = 0f;
        _flash = 0f;

        if (_playerManager.LocalEntity is not { } player
            || !_statusEffects.TryGetEffectsEndTimeWithComp<ProperKungFuDisorientedStatusEffectComponent>(player, out var endTime))
            return;

        var now = _timing.CurTime;
        var left = endTime is { } end ? (float) (end - now).TotalSeconds : FadeOutTime;
        _strength = Math.Clamp(left / FadeOutTime, 0f, 1f);

        var sinceFlash = (float) (now - FlashStart).TotalSeconds;
        if (sinceFlash >= 0f && sinceFlash < FlashTime)
        {
            var k = 1f - sinceFlash / FlashTime;
            _flash = 0.85f * k * k;
        }
    }

    protected override bool BeforeDraw(in OverlayDrawArgs args)
    {
        if (!_entityManager.TryGetComponent(_playerManager.LocalEntity, out EyeComponent? eyeComp))
            return false;

        if (args.Viewport.Eye != eyeComp.Eye)
            return false;

        return _strength > 0f || _flash > 0f;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (ScreenTexture == null)
            return;

        var handle = args.WorldHandle;
        _shader.SetParameter("SCREEN_TEXTURE", ScreenTexture);
        _shader.SetParameter("Strength", _strength);
        _shader.SetParameter("Flash", _flash);
        handle.UseShader(_shader);
        handle.DrawRect(args.WorldBounds, Color.White);
        handle.UseShader(null);
    }
}

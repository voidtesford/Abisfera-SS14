using Content.Shared._CorvaxGoob.Redspace;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.Redspace;

/// <summary>
/// Plays the one-shot unfold animation of the unfolding stand-in in sync with the server start time
/// (a fresh spawn starts at frame 0; late joiners / PVS re-entry jump to the right frame), and drives the
/// crimson light: a glow that builds while the pillars rise, a bright flare when they connect, then settles.
/// </summary>
public sealed class RedspaceGateUnfoldingSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RedspaceGateUnfoldingComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<RedspaceGateUnfoldingComponent, AfterAutoHandleStateEvent>(OnState);
    }

    private void OnStartup(Entity<RedspaceGateUnfoldingComponent> ent, ref ComponentStartup args) => Sync(ent);

    private void OnState(Entity<RedspaceGateUnfoldingComponent> ent, ref AfterAutoHandleStateEvent args) => Sync(ent);

    /// <summary>
    /// Seconds since the unfold started, clamped to [0, duration].
    /// </summary>
    public float Elapsed(RedspaceGateUnfoldingComponent comp)
    {
        if (comp.Start == TimeSpan.Zero)
            return 0f;

        var elapsed = (_timing.CurTime - comp.Start).TotalSeconds;
        return (float) Math.Clamp(elapsed, 0, comp.Duration.TotalSeconds);
    }

    private void Sync(Entity<RedspaceGateUnfoldingComponent> ent)
    {
        if (!TryComp<SpriteComponent>(ent, out var sprite)
            || !_sprite.LayerMapTryGet((ent, sprite), RedspaceGateUnfoldingLayers.Unfold, out var layer, false))
            return;

        _sprite.LayerSetVisible((ent, sprite), layer, true);
        _sprite.LayerSetAutoAnimated((ent, sprite), layer, true);
        _sprite.LayerSetAnimationTime((ent, sprite), layer, Elapsed(ent.Comp));
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var query = EntityQueryEnumerator<RedspaceGateUnfoldingComponent, PointLightComponent>();
        while (query.MoveNext(out var uid, out var comp, out var light))
        {
            var t = Elapsed(comp);
            var connect = (float) comp.ConnectTime.TotalSeconds;
            float energy, radius;
            if (t < connect)
            {
                // Builds up while the pillars rise.
                var k = t / connect;
                energy = 0.4f + 1.2f * k;
                radius = 1.5f + 1.5f * k;
            }
            else
            {
                // Flare at the moment of connection, decaying into the ring glow.
                var since = t - connect;
                var flare = MathF.Exp(-since * 6f);
                energy = 1.6f + 6f * flare;
                radius = 3f + 3f * flare;
            }

            _light.SetEnergy(uid, energy, light);
            _light.SetRadius(uid, radius, light);
        }
    }
}

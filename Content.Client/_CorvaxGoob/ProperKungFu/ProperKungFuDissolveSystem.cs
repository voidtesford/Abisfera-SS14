using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Robust.Client.GameObjects;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Fades, drifts and scales <see cref="ProperKungFuDissolveComponent"/> effects every frame: the 一寸死拳 kanji
/// dissolving upward, the death punch flash, its shockwave ring and the crackling residue.
/// </summary>
public sealed class ProperKungFuDissolveSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly PointLightSystem _light = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ProperKungFuDissolveComponent, ComponentStartup>(OnStartup);
    }

    private void OnStartup(Entity<ProperKungFuDissolveComponent> ent, ref ComponentStartup args)
    {
        var comp = ent.Comp;
        comp.Start = _timing.RealTime;
        comp.DriftTotal = comp.Drift;
        if (comp.DriftJitter > 0f)
            comp.DriftTotal += _random.NextAngle().ToVec() * _random.NextFloat(0f, comp.DriftJitter);

        if (TryComp<SpriteComponent>(ent, out var sprite))
        {
            comp.BaseScale = sprite.Scale;
            comp.BaseOffset = sprite.Offset;
            comp.BaseAlpha = sprite.Color.A;
        }

        if (TryComp<PointLightComponent>(ent, out var light))
            comp.BaseEnergy = light.Energy;

        comp.Started = true;
        Apply(ent, 0f);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.RealTime;
        var query = EntityQueryEnumerator<ProperKungFuDissolveComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Started)
                Apply((uid, comp), (float) (now - comp.Start).TotalSeconds);
        }
    }

    private void Apply(Entity<ProperKungFuDissolveComponent> ent, float t)
    {
        var comp = ent.Comp;
        var fade = comp.Fade > 0f ? Math.Clamp((t - comp.Hold) / comp.Fade, 0f, 1f) : (t >= comp.Hold ? 1f : 0f);
        var life = comp.Hold + comp.Fade;
        var scaleTime = comp.ScaleTime > 0f ? comp.ScaleTime : life;
        var grow = scaleTime > 0f ? Math.Clamp(t / scaleTime, 0f, 1f) : 1f;
        grow = 1f - (1f - grow) * (1f - grow);
        var scale = comp.ScaleFrom + (comp.ScaleTo - comp.ScaleFrom) * grow;

        // Drift eases in with the fade: the effect lifts off as it dissolves.
        var lift = fade * (2f - fade);

        if (TryComp<SpriteComponent>(ent, out var sprite))
        {
            _sprite.SetColor((ent.Owner, sprite), sprite.Color.WithAlpha(comp.BaseAlpha * (1f - fade)));
            _sprite.SetScale((ent.Owner, sprite), comp.BaseScale * scale);
            _sprite.SetOffset((ent.Owner, sprite), comp.BaseOffset + comp.DriftTotal * lift);
        }

        if (TryComp<PointLightComponent>(ent, out var light))
            _light.SetEnergy(ent, comp.BaseEnergy * (1f - fade), light);
    }
}

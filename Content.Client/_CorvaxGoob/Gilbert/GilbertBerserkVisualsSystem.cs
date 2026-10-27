using Content.Shared._CorvaxGoob.Gilbert;
using Robust.Client.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Berserk (v6) look on top of everything he wears: neon red arms (brightest at the hands, dim at the shoulders),
/// discharges looping over his body, white-hot eyes with red flames rising from them, and a flickering, brighter
/// red light. Fades in while he charges up, flares while he dies, gone once he is dead.
/// </summary>
public sealed class GilbertBerserkVisualsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;
    [Dependency] private readonly PointLightSystem _light = default!;

    private static readonly ResPath Rsi = new("_CorvaxGoob/Mobs/Gilbert/berserk.rsi");

    private static readonly (GilbertBerserkLayers Key, string State)[] Layers =
    {
        (GilbertBerserkLayers.Arms, "arms"),
        (GilbertBerserkLayers.Discharge, "discharge"),
        (GilbertBerserkLayers.EyesRage, "eyes_rage"),
        (GilbertBerserkLayers.EyesFlame, "eyes_flame"),
    };

    public const float BaseLightEnergy = 2.4f;

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertBerserkComponent, SpriteComponent>();
        while (query.MoveNext(out var uid, out var comp, out var sprite))
        {
            var visible = comp.Phase is GilbertBerserkPhase.Transition or GilbertBerserkPhase.Rage or GilbertBerserkPhase.Dying;
            Entity<SpriteComponent?> ent = (uid, sprite);
            if (!visible)
            {
                foreach (var (key, _) in Layers)
                {
                    if (_sprite.LayerMapTryGet(ent, key, out var idx, false))
                        _sprite.LayerSetVisible(ent, idx, false);
                }
                continue;
            }

            var (arms, discharge, eyes) = Levels(comp, now);
            var t = (float) now.TotalSeconds + uid.Id * 0.37f;
            // Flame-like flicker shared by the eyes and the light.
            var flicker = 0.82f + 0.12f * MathF.Sin(t * 23f) + 0.06f * MathF.Sin(t * 57f + 1.3f);

            SetLayer(ent, GilbertBerserkLayers.Arms, "arms", arms * (0.9f + 0.1f * MathF.Sin(t * 9f)));
            SetLayer(ent, GilbertBerserkLayers.Discharge, "discharge", discharge);
            SetLayer(ent, GilbertBerserkLayers.EyesRage, "eyes_rage", eyes);
            SetLayer(ent, GilbertBerserkLayers.EyesFlame, "eyes_flame", eyes * flicker);

            if (TryComp<PointLightComponent>(uid, out var light))
                _light.SetEnergy(uid, BaseLightEnergy * (0.5f + 0.5f * eyes) * flicker * (1f + discharge * 0.3f), light);
        }
    }

    /// <summary>Alpha of the arms, the body discharges and the eyes for the current phase.</summary>
    public static (float Arms, float Discharge, float Eyes) Levels(GilbertBerserkComponent comp, TimeSpan now)
    {
        var t = (float) (now - comp.PhaseStart).TotalSeconds;
        switch (comp.Phase)
        {
            case GilbertBerserkPhase.Transition:
            {
                // Charging up to the burst, then everything at full.
                var charge = Math.Clamp(t / MathF.Max((float) comp.BurstAt.TotalSeconds, 0.01f), 0f, 1f);
                return (charge * charge, MathF.Min(1f, 0.2f + charge), MathF.Min(1f, charge * 1.3f));
            }
            case GilbertBerserkPhase.Rage:
                return (1f, 0.65f, 1f);
            case GilbertBerserkPhase.Dying:
            {
                var k = Math.Clamp(t / MathF.Max((float) comp.DyingDuration.TotalSeconds, 0.01f), 0f, 1f);
                var surge = 0.75f + 0.25f * MathF.Sin(t * (12f + 30f * k));
                return (surge, 0.8f + 0.2f * k, 1f);
            }
            default:
                return (0f, 0f, 0f);
        }
    }

    private void SetLayer(Entity<SpriteComponent?> ent, GilbertBerserkLayers key, string state, float alpha)
    {
        if (!_sprite.LayerMapTryGet(ent, key, out var index, false))
        {
            // Added on first use, so it lands on top of the eye layers and every clothing layer.
            index = _sprite.AddLayer(ent, new SpriteSpecifier.Rsi(Rsi, state));
            _sprite.LayerMapSet(ent, key, index);
            ent.Comp!.LayerSetShader(index, "unshaded");
        }

        _sprite.LayerSetVisible(ent, index, alpha > 0.01f);
        _sprite.LayerSetColor(ent, index, Color.White.WithAlpha(Math.Clamp(alpha, 0f, 1f)));
    }
}

public enum GilbertBerserkLayers : byte
{
    Arms,
    Discharge,
    EyesRage,
    EyesFlame,
}

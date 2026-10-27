using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// How strongly Gilbert's presence interferes with a viewer's screen.
/// The client runs it every frame for the local player's entity to drive the interference overlay;
/// nothing extra is networked, it only reads positions, mob states and the berserk phase the client already has.
/// </summary>
public sealed class GilbertProximitySystem : EntitySystem
{
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>No interference beyond this many tiles.</summary>
    public const float MaxRange = 12f;

    /// <summary>Full interference at or below this many tiles (v5: 6 tiles, ramping up from 12).</summary>
    public const float FullRange = 6f;

    /// <summary>
    /// 0 at <see cref="MaxRange"/> and beyond, 1 at <see cref="FullRange"/> and closer, smooth in between.
    /// </summary>
    public static float IntensityAt(float distance)
    {
        return IntensityAt(distance, MaxRange, FullRange);
    }

    /// <summary>
    /// 0 at <paramref name="maxRange"/> and beyond, 1 at <paramref name="fullRange"/> and closer, smooth in between.
    /// </summary>
    public static float IntensityAt(float distance, float maxRange, float fullRange)
    {
        if (float.IsNaN(distance) || distance >= maxRange)
            return 0f;

        if (distance <= fullRange)
            return 1f;

        var t = (maxRange - distance) / (maxRange - fullRange);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Interference from one Gilbert at <paramref name="distance"/>: the normal 12/6-tile curve (0..1), or in his
    /// second phase (v6) the wider berserk curve (18/9 tiles by default) scaled up to
    /// <see cref="GilbertBerserkComponent.InterferenceStrength"/> (values above 1 are the stronger "rage" look).
    /// </summary>
    public static float IntensityFrom(float distance, GilbertBerserkComponent? berserk)
    {
        if (berserk == null || berserk.Phase is GilbertBerserkPhase.None or GilbertBerserkPhase.Dead)
            return IntensityAt(distance);

        var k = IntensityAt(distance, berserk.InterferenceRange, berserk.InterferenceFullRange);
        return k * berserk.InterferenceStrength;
    }

    /// <summary>
    /// Can this entity see the interference at all? Gilbert himself, ghosts and observers don't.
    /// </summary>
    public bool CanBeAffected(EntityUid viewer)
    {
        if (TerminatingOrDeleted(viewer))
            return false;

        return !HasComp<GilbertComponent>(viewer)
               && !HasComp<GhostComponent>(viewer)
               && HasComp<MobStateComponent>(viewer);
    }

    /// <summary>
    /// Target interference for <paramref name="viewer"/>: the strongest one from any living Gilbert on the same map.
    /// </summary>
    public float GetIntensity(EntityUid viewer)
    {
        if (!CanBeAffected(viewer))
            return 0f;

        var viewerXform = Transform(viewer);
        if (viewerXform.MapID == Robust.Shared.Map.MapId.Nullspace)
            return 0f;

        var viewerPos = _transform.GetWorldPosition(viewerXform);
        var best = 0f;

        var query = EntityQueryEnumerator<GilbertComponent, MobStateComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out var mobState, out var xform))
        {
            if (uid == viewer || xform.MapID != viewerXform.MapID || !_mobState.IsAlive(uid, mobState))
                continue;

            var distance = (_transform.GetWorldPosition(xform) - viewerPos).Length();
            TryComp<GilbertBerserkComponent>(uid, out var berserk);
            best = MathF.Max(best, IntensityFrom(distance, berserk));
        }

        return best;
    }
}

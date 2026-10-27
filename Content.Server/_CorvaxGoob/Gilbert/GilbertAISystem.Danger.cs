using System.Linq;
using System.Numerics;
using Content.Server.Explosion.EntitySystems;
using Content.Shared.Explosion.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Physics;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Danger awareness. Any armed timed explosive he knows about (his own planted C4 / thrown grenade, or someone
/// else's primed grenade, C4 or other active timer charge he sees or hears ticking) is a danger zone: a circle with
/// the explosion radius computed from its <see cref="ExplosiveComponent"/> plus a margin. While he stands in one he
/// drops whatever he was doing (combat, search, arrest...) and runs to the nearest reachable spot outside every
/// zone, preferring one with no line of sight to the bomb, and never along a line that cuts through another zone.
/// He keeps facing his target while running (turning does not slow him down).
/// </summary>
public sealed partial class GilbertAISystem
{
    [Dependency] private readonly ExplosionSystem _explosion = default!;

    /// <summary>Armed charges further than this are not noticed at all.</summary>
    private const float DangerScanRange = 16f;

    /// <summary>An armed charge within this range is heard (arming click / beeping) even out of sight.</summary>
    private const float DangerHearRange = 8f;

    /// <summary>Extra distance kept beyond the computed blast radius.</summary>
    public const float DangerMargin = 1.5f;

    /// <summary>Radius assumed for armed things with no explosive component (flashbangs, smoke, shrapnel...).</summary>
    private const float DefaultDangerRadius = 4f;

    /// <summary>
    /// Blast radius of an explosive (tiles from the centre that get any damage), without the margin.
    /// </summary>
    public float GetBlastRadius(EntityUid uid)
    {
        if (TryComp<ExplosiveComponent>(uid, out var explosive))
            return 0.5f + _explosion.IntensityToRadius(explosive.TotalIntensity, explosive.IntensitySlope,
                explosive.MaxIntensity);

        return DefaultDangerRadius;
    }

    /// <summary>Radius he keeps away from an armed charge: blast radius plus <see cref="DangerMargin"/>.</summary>
    public float GetDangerRadius(EntityUid uid) => GetBlastRadius(uid) + DangerMargin;

    /// <summary>Is this charge held by Gilbert himself (in his hands / pockets)?</summary>
    private bool IsCarriedBy(EntityUid item, EntityUid holder)
    {
        var current = item;
        while (_container.TryGetContainingContainer((current, null, null), out var container))
        {
            current = container.Owner;
            if (current == holder)
                return true;
        }

        return false;
    }

    /// <summary>Remembers his own charges so he tracks them even out of sight and earshot.</summary>
    public void TrackOwnCharge(Entity<GilbertAIComponent> ent, EntityUid charge) => ent.Comp.KnownDangers.Add(charge);

    /// <summary>
    /// Refreshes the set of armed charges he knows about and returns the ones he is currently inside the zone of.
    /// </summary>
    private void RefreshDangers(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        comp.KnownDangers.RemoveWhere(d => TerminatingOrDeleted(d) || !HasComp<ActiveTimerTriggerComponent>(d));

        var selfMap = _transform.GetMapCoordinates(ent.Owner, xform);
        foreach (var charge in _lookup.GetEntitiesInRange<ActiveTimerTriggerComponent>(selfMap, DangerScanRange))
        {
            if (comp.KnownDangers.Contains(charge.Owner))
                continue;

            var dist = (_transform.GetWorldPosition(charge.Owner) - selfMap.Position).Length();
            if (dist <= DangerHearRange || CanSeeThing(ent, xform, charge.Owner))
                comp.KnownDangers.Add(charge.Owner);
        }
    }

    /// <summary>Line of sight to an object (no vision cone check beyond his normal one).</summary>
    private bool CanSeeThing(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityUid thing)
    {
        // A charge in someone's hands or on a wall is still visible; CanSee only needs the map position.
        return CanSee(ent, thing, ent.Comp, xform);
    }

    /// <summary>Is the world point inside any known danger zone (other than <paramref name="except"/>)?</summary>
    private bool InDanger(Entity<GilbertAIComponent> ent, Vector2 point, EntityUid? except = null)
    {
        foreach (var d in ent.Comp.KnownDangers)
        {
            if (d == except || TerminatingOrDeleted(d) || IsCarriedBy(d, ent))
                continue;

            if ((_transform.GetWorldPosition(d) - point).Length() < GetDangerRadius(d))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Danger handling for this think. True while he is escaping a blast (everything else waits).
    /// </summary>
    private bool UpdateDanger(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        RefreshDangers(ent, xform);

        // A primed charge stuck in his own hands outside a throw (throw failed, task aborted): get rid of it.
        foreach (var d in comp.KnownDangers)
        {
            if (comp.Task != GilbertTask.Grenade && !TerminatingOrDeleted(d) && IsCarriedBy(d, ent))
                _hands.TryDrop(ent.Owner, d, checkActionBlocker: false);
        }

        var pos = _transform.GetWorldPosition(xform);
        if (!InDanger(ent, pos))
        {
            if (comp.FleeingDanger)
            {
                comp.FleeingDanger = false;
                comp.FleePoint = null;
                _steering.Unregister(ent);
                _speed.RefreshMovementSpeedModifiers(ent);
            }

            return false;
        }

        // Finishing a throw takes a fraction of a second and gets the grenade away from him: let it happen.
        if (comp.Task == GilbertTask.Grenade)
            return false;

        if (!comp.FleeingDanger)
        {
            comp.FleeingDanger = true;
            comp.FleePoint = null;
            _speed.RefreshMovementSpeedModifiers(ent);
            StopCombatComponents(ent);
            // Waiting out his own C4 is part of the task; anything else is dropped.
            if (comp.Task is not (GilbertTask.None or GilbertTask.PlantC4))
                AbortTask(ent);
        }

        // Re-plan when there is no spot yet, the spot became dangerous (new charge) or he got there.
        if (comp.FleePoint is not { } spot
            || InDanger(ent, _transform.ToMapCoordinates(spot).Position)
            || (_transform.ToMapCoordinates(spot).Position - pos).Length() < 0.3f
            || now >= comp.FleeReplan)
        {
            comp.FleePoint = TryFindSafeSpot(ent, xform, out var found) ? found : null;
            comp.FleeReplan = now + TimeSpan.FromSeconds(1.5);
        }

        if (comp.FleePoint is { } dest)
            RegisterMove(ent, dest, 0.25f);
        else
            RunAwayBlind(ent, xform);

        return true;
    }

    /// <summary>No good spot found: just run straight away from the nearest charge.</summary>
    private void RunAwayBlind(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        var pos = _transform.GetWorldPosition(xform);
        EntityUid? nearest = null;
        var best = float.MaxValue;
        foreach (var d in ent.Comp.KnownDangers)
        {
            if (TerminatingOrDeleted(d))
                continue;
            var dist = (_transform.GetWorldPosition(d) - pos).Length();
            if (dist < best)
            {
                best = dist;
                nearest = d;
            }
        }

        if (nearest is not { } bomb)
            return;

        var away = pos - _transform.GetWorldPosition(bomb);
        if (away.LengthSquared() < 0.01f)
            away = _random.NextAngle().ToVec();
        var run = Vector2.Normalize(away) * (GetDangerRadius(bomb) - best + 1f);
        RegisterMove(ent, xform.Coordinates.Offset(run), 0.25f);
    }

    /// <summary>
    /// A free floor tile outside every danger zone that he can reach in a straight line without crossing another
    /// zone or getting closer to the bomb he is escaping. Score = walking distance, plus a penalty when the spot is
    /// in the open (line of sight to a charge) and a big one when he can't make it before the timer runs out.
    /// </summary>
    public bool TryFindSafeSpot(Entity<GilbertAIComponent> ent, TransformComponent xform, out EntityCoordinates spot)
    {
        spot = default;
        if (_transform.GetGrid(xform.Coordinates) is not { } g || !TryComp<MapGridComponent>(g, out var grid))
            return false;

        var selfMap = _transform.GetMapCoordinates(ent.Owner, xform);
        var origin = _transform.WithEntityId(xform.Coordinates, g).Position;
        var speed = TryComp<MovementSpeedModifierComponent>(ent, out var move) ? Math.Max(1f, move.CurrentSprintSpeed) : 4f;

        // The charges he has to get out of right now.
        var threats = new List<(EntityUid Uid, Vector2 Pos, float Radius, float Time)>();
        var maxRadius = 0f;
        foreach (var d in ent.Comp.KnownDangers)
        {
            if (TerminatingOrDeleted(d) || IsCarriedBy(d, ent))
                continue;
            var radius = GetDangerRadius(d);
            var time = TryComp<ActiveTimerTriggerComponent>(d, out var timer) ? timer.TimeRemaining : 5f;
            threats.Add((d, _transform.GetWorldPosition(d), radius, time));
            maxRadius = Math.Max(maxRadius, radius + (_transform.GetWorldPosition(d) - selfMap.Position).Length());
        }

        if (threats.Count == 0)
            return false;

        var bestScore = float.MaxValue;
        var found = false;
        var searchRange = Math.Min(DangerScanRange, maxRadius + 4f);

        for (var i = 0; i < 64; i++)
        {
            var local = origin + _random.NextAngle().ToVec() * _random.NextFloat(1f, searchRange);
            var tile = _map.TileIndicesFor(g, grid, new EntityCoordinates(g, local));
            if (!_map.TryGetTileRef(g, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty
                || _turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var candidate = _map.GridTileToLocal(g, grid, tile);
            var candMap = _transform.ToMapCoordinates(candidate);
            var walk = (candMap.Position - selfMap.Position).Length();
            if (walk >= bestScore)
                continue;

            var ok = true;
            var score = walk;
            foreach (var (uid, bombPos, radius, time) in threats)
            {
                // Outside the zone...
                if ((candMap.Position - bombPos).Length() < radius)
                {
                    ok = false;
                    break;
                }

                // ...and the straight run there must not come closer to the bomb than he already is
                // (no running past it), nor clip a zone he is not in yet.
                var here = (selfMap.Position - bombPos).Length();
                var closest = DistanceToSegment(bombPos, selfMap.Position, candMap.Position);
                if (closest < Math.Min(here, radius) - 0.3f)
                {
                    ok = false;
                    break;
                }

                // Out in the open vs. behind a wall.
                var exposed = (candMap.Position - bombPos).Length();
                if (_interaction.InRangeUnobstructed(new MapCoordinates(bombPos, selfMap.MapId), candMap, exposed + 0.5f,
                        CollisionGroup.Opaque, IsMob))
                    score += 3f;

                // Can't get there before it goes off.
                if (walk / speed > time)
                    score += 20f;
            }

            if (!ok || score >= bestScore)
                continue;

            // Reachable directly.
            if (!_interaction.InRangeUnobstructed(selfMap, candMap, walk + 0.5f, CollisionGroup.Impassable, IsMob))
                continue;

            bestScore = score;
            spot = candidate;
            found = true;
        }

        return found;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var len = ab.LengthSquared();
        if (len < 0.0001f)
            return (p - a).Length();
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / len, 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    /// <summary>
    /// Timer to set on a charge planted at <paramref name="plantAt"/>: time to walk to a safe spot
    /// (straight-line distance to the edge of the zone x1.5 for detours, at his sprint speed) plus 3 s, rounded up
    /// to one of the charge's delay options.
    /// </summary>
    public float PlanChargeDelay(Entity<GilbertAIComponent> ent, EntityUid charge)
    {
        var speed = TryComp<MovementSpeedModifierComponent>(ent, out var move) ? Math.Max(1f, move.CurrentSprintSpeed) : 4f;
        var needed = GetDangerRadius(charge) * 1.5f / speed + 3f;
        if (!TryComp<OnUseTimerTriggerComponent>(charge, out var timer))
            return needed;

        var delay = Math.Max(timer.Delay, needed);
        if (timer.DelayOptions is { Count: > 0 } options)
        {
            var pick = options.Where(o => o >= needed).DefaultIfEmpty(options.Max()).Min();
            delay = Math.Max(pick, needed);
        }

        return delay;
    }

    /// <summary>Starts planting <paramref name="charge"/> (held or stored) on <paramref name="wall"/> (admin / tests).</summary>
    public void BeginPlantCharge(EntityUid uid, EntityUid charge, EntityUid wall)
    {
        if (!TryComp<GilbertAIComponent>(uid, out var comp) || comp.Task != GilbertTask.None)
            return;

        comp.NextC4 = _timing.CurTime + comp.C4Cooldown;
        StartTask((uid, comp), GilbertTask.PlantC4, charge, wall, TimeSpan.FromSeconds(30));
    }

    /// <summary>Would a grenade thrown at <paramref name="target"/> catch him in its blast?</summary>
    public bool GrenadeTooClose(Entity<GilbertAIComponent> ent, EntityUid grenade, EntityUid target)
    {
        return DistanceTo(ent, target) <= GetDangerRadius(grenade);
    }
}

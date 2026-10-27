using System.Numerics;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Target choice and cover. Every visible hostile gets a threat score (lower = deal with first): distance, minus a
/// lot for a gun in hand, minus some for a melee weapon, minus most for whoever just hurt him. He keeps his current
/// target unless someone else is clearly worse (<see cref="GilbertAIComponent.TargetSwitchMargin"/>), so he does not
/// flip between two people every think. Out of shells with an enemy close, he steps behind cover before reloading.
/// </summary>
public sealed partial class GilbertAISystem
{
    /// <summary>Being hurt by someone keeps them top priority this long.</summary>
    private static readonly TimeSpan AttackerMemory = TimeSpan.FromSeconds(5);

    /// <summary>He gives up walking to cover after this long and reloads where he is.</summary>
    private static readonly TimeSpan CoverMoveTime = TimeSpan.FromSeconds(4);

    /// <summary>Only bother with cover when the enemy is this close.</summary>
    private const float CoverThreatRange = 8f;

    public float ThreatScore(Entity<GilbertAIComponent> ent, EntityUid other, TimeSpan now)
    {
        var score = DistanceTo(ent, other);

        var gun = false;
        var melee = false;
        foreach (var held in _hands.EnumerateHeld(other))
        {
            if (HasComp<GunComponent>(held))
                gun = true;
            else if (HasComp<MeleeWeaponComponent>(held))
                melee = true;
        }

        if (gun)
            score -= 6f;
        else if (melee)
            score -= 3f;

        if (ent.Comp.LastAttacker == other && now - ent.Comp.LastAttackedAt < AttackerMemory)
            score -= 8f;

        return score;
    }

    private EntityUid? FindVisibleEnemy(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        var now = _timing.CurTime;
        EntityUid? best = null;
        var bestScore = float.MaxValue;

        foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(xform.Coordinates, ent.Comp.VisionRange))
        {
            if (other.Owner == ent.Owner || !IsHostile(ent, other) || !CanSee(ent, other, ent.Comp, xform))
                continue;

            var score = ThreatScore(ent, other, now);
            if (score < bestScore)
            {
                bestScore = score;
                best = other;
            }
        }

        // Hysteresis: stick with the current (still visible) target unless the best one is clearly more dangerous.
        if (ent.Comp.Target is { } current && current != best && best != null && !TerminatingOrDeleted(current)
            && IsHostile(ent, current) && CanSee(ent, current, ent.Comp, xform)
            && ThreatScore(ent, current, now) - bestScore <= ent.Comp.TargetSwitchMargin)
            return current;

        return best;
    }

    /// <summary>
    /// A free tile within <see cref="GilbertAIComponent.CoverSearchRange"/> he can walk to in a straight line where
    /// a wall (or anything else bullets don't pass) is between him and <paramref name="threat"/>. Nearest wins.
    /// </summary>
    public bool TryFindCover(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityUid threat, out EntityCoordinates cover)
    {
        cover = default;
        if (_transform.GetGrid(xform.Coordinates) is not { } g || !TryComp<MapGridComponent>(g, out var grid))
            return false;

        var threatMap = _transform.GetMapCoordinates(threat);
        var selfMap = _transform.GetMapCoordinates(ent.Owner, xform);
        if (threatMap.MapId != selfMap.MapId)
            return false;

        var origin = _transform.WithEntityId(xform.Coordinates, g).Position;
        var bestDist = float.MaxValue;
        var found = false;

        for (var i = 0; i < 32; i++)
        {
            var dist = _random.NextFloat(1.5f, Math.Max(1.6f, ent.Comp.CoverSearchRange - 0.5f));
            var local = origin + _random.NextAngle().ToVec() * dist;
            var tile = _map.TileIndicesFor(g, grid, new EntityCoordinates(g, local));
            if (!_map.TryGetTileRef(g, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty
                || _turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var candidate = _map.GridTileToLocal(g, grid, tile);
            var candMap = _transform.ToMapCoordinates(candidate);
            var walk = (candMap.Position - selfMap.Position).Length();
            if (walk >= bestDist)
                continue;

            // He must be able to get there directly...
            if (!_interaction.InRangeUnobstructed(selfMap, candMap, walk + 0.5f, CollisionGroup.Impassable, IsMob))
                continue;

            // ...and it must be out of the threat's line of fire.
            var exposed = (candMap.Position - threatMap.Position).Length();
            if (_interaction.InRangeUnobstructed(threatMap, candMap, exposed + 0.5f, CollisionGroup.BulletImpassable, IsMob))
                continue;

            bestDist = walk;
            cover = candidate;
            found = true;
        }

        return found;
    }

    private bool IsMob(EntityUid uid) => HasComp<MobStateComponent>(uid);

    /// <summary>Starting a reload: pick cover if an enemy is in view and close.</summary>
    private void PlanReloadCover(Entity<GilbertAIComponent> ent, TransformComponent xform, EntityUid target, bool visible, TimeSpan now)
    {
        ent.Comp.CoverPoint = null;
        if (!visible || DistanceTo(ent, target) > CoverThreatRange)
            return;

        if (TryFindCover(ent, xform, target, out var cover))
        {
            ent.Comp.CoverPoint = cover;
            ent.Comp.CoverUntil = now + CoverMoveTime;
        }
    }

    /// <summary>Walks to the planned cover; true while still on the way.</summary>
    private bool MoveToCover(Entity<GilbertAIComponent> ent, TimeSpan now)
    {
        if (ent.Comp.CoverPoint is not { } cover)
            return false;

        var here = _transform.GetMapCoordinates(ent.Owner);
        var there = _transform.ToMapCoordinates(cover);
        if (now >= ent.Comp.CoverUntil || here.MapId != there.MapId || (here.Position - there.Position).Length() <= 0.4f)
        {
            ent.Comp.CoverPoint = null;
            _steering.Unregister(ent);
            return false;
        }

        RegisterMove(ent, cover, 0.3f);
        return true;
    }
}

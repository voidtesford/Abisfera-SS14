using System.Linq;
using System.Numerics;
using Content.Server.NPC.Components;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Doors.Components;
using Content.Shared.Maps;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v9: always on the move.
/// <list type="bullet">
/// <item>Paths go through airlocks: opened with his all-access ID, forced open (super strength) when the ID doesn't
/// work; in berserk they are smashed.</item>
/// <item>A fading memory of where he has been: fresh areas are avoided, forgotten ones are interesting again.</item>
/// <item>Pacing on the spot / stuck for a few seconds: whatever is in the way gets broken if he can, otherwise the
/// destination is blacklisted for a while and another one is picked.</item>
/// <item>Losing sight of an enemy: the escape route is guessed from their last position and velocity (doors and
/// corridors in that direction first), then the rooms around it are searched.</item>
/// <item>Berserk: roams non-stop towards where living things are, smashing through anything.</item>
/// </list>
/// </summary>
public sealed partial class GilbertAISystem
{
    /// <summary>
    /// Normal phase: doors he can open, access airlocks (ID), forcing them with his bare hands when the ID fails
    /// (5 s, not through bolts), and v9 (t259u) climbing over tables.
    /// </summary>
    public const PathFlags GilbertPathFlags = PathFlags.Interact | PathFlags.Access | PathFlags.Prying | PathFlags.Climbing;

    /// <summary>
    /// Berserk: plain doors are pushed open, airlocks ripped open instantly even when bolted (v9, t258u), tables
    /// vaulted (fast), everything else in the way is smashed.
    /// </summary>
    public const PathFlags BerserkPathFlags = PathFlags.Interact | PathFlags.Access | PathFlags.Prying | PathFlags.Climbing
                                              | PathFlags.Smashing;

    #region Steering

    /// <summary>
    /// Registers a walk. A stale "no path" left over from the previous destination is cleared when the new one is
    /// somewhere else (<paramref name="exact"/>: any other spot), so a new walk is never dead on arrival.
    /// </summary>
    private NPCSteeringComponent SteerTo(EntityUid uid, EntityCoordinates coordinates, bool exact = false)
    {
        if (TryComp<NPCSteeringComponent>(uid, out var existing) && existing.Status == SteeringStatus.NoPath
            && !existing.Coordinates.Equals(coordinates)
            && (exact || !_transform.InRange(existing.Coordinates, coordinates, 1.5f)))
        {
            existing.Status = SteeringStatus.Moving;
            existing.FailedPathCount = 0;
        }

        var steering = _steering.Register(uid, coordinates, existing);
        steering.Flags = InBerserk(uid) ? BerserkPathFlags : GilbertPathFlags;
        return steering;
    }

    #endregion

    #region Memory

    /// <summary>
    /// How "fresh" an area is for him: 1 = never been there (or long forgotten), 0 = he is standing in it.
    /// The memory fades exponentially (<see cref="GilbertAIComponent.VisitHalfLife"/>).
    /// </summary>
    public float Novelty(GilbertAIComponent comp, (EntityUid Grid, Vector2i Cell) cell, TimeSpan now)
    {
        if (!comp.Visited.TryGetValue(cell, out var at))
            return 1f;

        var age = (now - at).TotalSeconds;
        if (age >= comp.ExploreMemory.TotalSeconds)
            return 1f;

        var half = Math.Max(1.0, comp.VisitHalfLife.TotalSeconds);
        return 1f - (float) Math.Pow(0.5, age / half);
    }

    public bool IsBlacklisted(GilbertAIComponent comp, (EntityUid Grid, Vector2i Cell) cell, TimeSpan now)
    {
        return comp.Blacklist.TryGetValue(cell, out var until) && until > now;
    }

    private (EntityUid Grid, Vector2i Cell)? CellAt(EntityCoordinates coords, GilbertAIComponent comp)
    {
        var gridUid = _transform.GetGrid(coords);
        if (gridUid is not { } g || !TryComp<MapGridComponent>(g, out var grid))
            return null;

        return (g, CellOfTile(_map.TileIndicesFor(g, grid, coords), comp));
    }

    public bool IsBlacklisted(GilbertAIComponent comp, EntityCoordinates coords)
    {
        return CellAt(coords, comp) is { } cell && IsBlacklisted(comp, cell, _timing.CurTime);
    }

    /// <summary>Somewhere he got stuck going to: left alone for <see cref="GilbertAIComponent.BlacklistTime"/>.</summary>
    public void BlacklistSpot(GilbertAIComponent comp, EntityCoordinates coords)
    {
        if (CellAt(coords, comp) is not { } cell)
            return;

        var now = _timing.CurTime;
        comp.Blacklist[cell] = now + comp.BlacklistTime;
        if (comp.Blacklist.Count > 64)
        {
            foreach (var key in comp.Blacklist.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                comp.Blacklist.Remove(key);
        }
    }

    /// <summary>A floor tile with nothing solid on it.</summary>
    private bool IsFreeFloor(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        return _map.TryGetTileRef(gridUid, grid, tile, out var tileRef) && !tileRef.Tile.IsEmpty
               && !_turf.IsSpace(tileRef) && !_turf.IsTileBlocked(tileRef, CollisionGroup.Impassable);
    }

    #endregion

    #region Oscillation

    /// <summary>
    /// Walking somewhere but still within <see cref="GilbertAIComponent.OscillationRadius"/> of where he was
    /// <paramref name="window"/> ago (pacing back and forth on a tile, pushing into a wall): true once, then the
    /// window restarts. Standing for a reason (do-after, aiming at someone in view...) doesn't count.
    /// </summary>
    private bool CheckOscillation(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, TimeSpan window,
        bool standingReason)
    {
        var comp = ent.Comp;
        var pos = _transform.GetWorldPosition(xform);
        if (standingReason || !TryComp<NPCSteeringComponent>(ent, out var steering) || steering.Status == SteeringStatus.InRange)
        {
            comp.OscillationAnchor = pos;
            comp.OscillationSince = now;
            comp.OscillationGoal = null;
            return false;
        }

        var goal = steering.Coordinates;
        if (comp.OscillationGoal is not { } old || !old.IsValid(EntityManager) || !goal.IsValid(EntityManager)
            || !_transform.InRange(old, goal, 2f)
            || (pos - comp.OscillationAnchor).LengthSquared() > comp.OscillationRadius * comp.OscillationRadius)
        {
            comp.OscillationGoal = goal;
            comp.OscillationAnchor = pos;
            comp.OscillationSince = now;
            return false;
        }

        if (now - comp.OscillationSince < window)
            return false;

        comp.Oscillations++;
        comp.OscillationAnchor = pos;
        comp.OscillationSince = now;
        comp.OscillationGoal = null;
        return true;
    }

    /// <summary>
    /// Normal phase: pacing in place. Hunting: break the way open (C4 on the airlock / wall). Otherwise the
    /// destination is blacklisted and the state picks a new one (see <see cref="OnStalled"/>).
    /// </summary>
    private void UpdateOscillation(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        TryComp<NPCSteeringComponent>(ent, out var steering);
        var standing = HasStandingReason(ent, steering, now) || comp.State == GilbertAIState.Combat
                       && now - comp.LastSeenTarget < TimeSpan.FromSeconds(2);
        if (!CheckOscillation(ent, xform, now, comp.OscillationWindow, standing) || steering == null)
            return;

        BlacklistSpot(comp, steering.Coordinates);
        OnStalled(ent, xform, now);
    }

    #endregion

    #region Prediction

    /// <summary>Remembers how the enemy was moving while he could see them.</summary>
    private void RecordEnemyMotion(GilbertAIComponent comp, EntityUid target)
    {
        var vel = _physics.GetMapLinearVelocity(target);
        // Keep the last real heading when they stop for a moment (behind a corner, at a door).
        if (vel.LengthSquared() > 0.09f)
            comp.LastSeenVelocity = vel;
        else
            comp.LastSeenVelocity *= 0.9f;
    }

    /// <summary>
    /// Guesses where an enemy who was last seen at <paramref name="lastKnown"/> moving with
    /// <paramref name="velocity"/> went: the best door / airlock roughly in that direction (and a few tiles past it),
    /// or the farthest open point along the heading. Fills <see cref="GilbertAIComponent.PursuitPlan"/> and centres
    /// the room search there, leaning the same way.
    /// </summary>
    public void BuildPursuitPlan(Entity<GilbertAIComponent> ent, EntityCoordinates lastKnown, Vector2 velocity)
    {
        var comp = ent.Comp;
        comp.PursuitPlan.Clear();
        comp.SearchCenter = lastKnown;
        comp.SearchBias = Vector2.Zero;

        var speed = velocity.Length();
        if (speed < 0.3f || !lastKnown.IsValid(EntityManager))
            return;

        var dir = velocity / speed;
        comp.SearchBias = dir;
        var lastMap = _transform.ToMapCoordinates(lastKnown);
        var gridUid = _transform.GetGrid(lastKnown);
        if (gridUid is not { } g || !TryComp<MapGridComponent>(g, out var grid))
            return;

        var cosCone = MathF.Cos(MathHelper.DegreesToRadians(comp.PredictDoorCone));

        // Doors / airlocks along their heading: the likeliest way out of the room.
        EntityUid? bestDoor = null;
        var bestScore = float.MinValue;
        foreach (var door in _lookup.GetEntitiesInRange<DoorComponent>(lastKnown, comp.PredictMaxDistance))
        {
            var delta = _transform.GetWorldPosition(door) - lastMap.Position;
            var dist = delta.Length();
            if (dist < 0.5f)
                continue;

            var cos = Vector2.Dot(dir, delta / dist);
            if (cos < cosCone)
                continue;

            var score = cos * 10f - dist * 0.6f;
            if (score > bestScore)
            {
                bestScore = score;
                bestDoor = door;
            }
        }

        // How far they could have got along the heading before a wall stops them.
        var reach = Math.Clamp(speed * 3f, 4f, comp.PredictMaxDistance);
        var ray = new CollisionRay(lastMap.Position, dir, (int) CollisionGroup.Impassable);
        foreach (var hit in _physics.IntersectRay(lastMap.MapId, ray, reach, ent.Owner))
        {
            if (TryComp<DoorComponent>(hit.HitEntity, out _))
                continue;
            reach = MathF.Min(reach, hit.Distance - 0.8f);
        }

        if (bestDoor is { } chosen)
        {
            var doorPos = _transform.GetWorldPosition(chosen);
            comp.PursuitPlan.Add(_map.GridTileToLocal(g, grid, _map.TileIndicesFor(g, grid,
                _transform.ToCoordinates(g, new MapCoordinates(doorPos, lastMap.MapId)))));

            // A few tiles past the door, the same way they were going.
            var through = Vector2.Normalize(doorPos - lastMap.Position);
            for (var step = 4f; step >= 2f; step -= 1f)
            {
                var beyond = doorPos + through * step;
                var tile = _map.TileIndicesFor(g, grid, _transform.ToCoordinates(g, new MapCoordinates(beyond, lastMap.MapId)));
                if (!IsFreeFloor(g, grid, tile))
                    continue;

                var beyondCoords = _map.GridTileToLocal(g, grid, tile);
                comp.PursuitPlan.Add(beyondCoords);
                comp.SearchCenter = beyondCoords;
                break;
            }

            comp.SearchBias = Vector2.Normalize(through + dir);
            return;
        }

        if (reach >= 1.5f)
        {
            var ahead = lastMap.Position + dir * reach;
            var tile = _map.TileIndicesFor(g, grid, _transform.ToCoordinates(g, new MapCoordinates(ahead, lastMap.MapId)));
            if (IsFreeFloor(g, grid, tile))
            {
                var aheadCoords = _map.GridTileToLocal(g, grid, tile);
                comp.PursuitPlan.Add(aheadCoords);
                comp.SearchCenter = aheadCoords;
            }
        }
    }

    /// <summary>The next waypoint of the guessed escape route that isn't blacklisted, if any.</summary>
    private bool TryPopPursuit(GilbertAIComponent comp, out EntityCoordinates point)
    {
        while (comp.PursuitPlan.Count > 0)
        {
            point = comp.PursuitPlan[0];
            comp.PursuitPlan.RemoveAt(0);
            if (point.IsValid(EntityManager) && !IsBlacklisted(comp, point))
                return true;
        }

        point = default;
        return false;
    }

    #endregion

    #region Berserk roaming

    /// <summary>
    /// The nearest living thing he can "smell" (any distance up to <see cref="GilbertAIComponent.BerserkScentRange"/>,
    /// through walls). Only the direction is used: he roams that way, he doesn't know the exact spot.
    /// </summary>
    private Vector2? BerserkScent(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        var myPos = _transform.GetWorldPosition(xform);
        Vector2? best = null;
        var bestDist = float.MaxValue;
        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(xform.Coordinates, ent.Comp.BerserkScentRange))
        {
            if (!IsBerserkVictim(ent, mob))
                continue;

            var delta = _transform.GetWorldPosition(mob) - myPos;
            var dist = delta.Length();
            if (dist < 0.5f || dist >= bestDist)
                continue;

            bestDist = dist;
            best = delta / dist;
        }

        return best;
    }

    /// <summary>
    /// Next roam destination in berserk: 8–25 tiles away, preferring areas he hasn't been to lately and the
    /// direction he smells someone in (see <see cref="BerserkScent"/>). Null in a cramped spot.
    /// </summary>
    public EntityCoordinates? PickBerserkRoam(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var scent = BerserkScent(ent, xform);
        var origin = _transform.WithEntityId(xform.Coordinates, gridUid).Position;
        var gridRot = _transform.GetWorldRotation(gridUid);
        EntityCoordinates? best = null;
        var bestScore = float.MinValue;

        for (var i = 0; i < comp.ExploreCandidates; i++)
        {
            var dist = _random.NextFloat(8f, 25f);
            var localDir = _random.NextAngle().ToVec();
            var tile = _map.TileIndicesFor(gridUid, grid, new EntityCoordinates(gridUid, origin + localDir * dist));
            // Floor only; whatever is between here and there gets smashed on the way.
            if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty || _turf.IsSpace(tileRef)
                || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var cell = (gridUid, CellOfTile(tile, comp));
            if (IsBlacklisted(comp, cell, now))
                continue;

            var score = Novelty(comp, cell, now) * 60f + _random.NextFloat(0f, 10f) + dist * 0.3f;
            if (scent is { } s)
                score += Vector2.Dot(gridRot.RotateVec(localDir), s) * 50f;

            if (score > bestScore)
            {
                bestScore = score;
                best = _map.GridTileToLocal(gridUid, grid, tile);
            }
        }

        return best;
    }

    /// <summary>
    /// Berserk with nobody in sight and nothing to search: keep moving to a fresh destination, smashing through
    /// anything; pacing / stuck goals are blacklisted and swapped.
    /// </summary>
    private void BerserkRoam(Entity<GilbertAIComponent> ent, TransformComponent xform, Vector2 myPos, float reach, TimeSpan now)
    {
        var comp = ent.Comp;
        var goal = comp.BerserkRoamGoal;
        var repick = goal is not { } g || !g.IsValid(EntityManager)
                     || _transform.InRange(xform.Coordinates, g, 1.5f)
                     || now - comp.BerserkRoamStarted > comp.BerserkRoamTimeout;

        if (!repick && comp.BerserkObstacle == null
            && CheckOscillation(ent, xform, now, comp.BerserkOscillationWindow, false))
        {
            // Pacing: something breakable on the way there gets smashed; otherwise forget that destination.
            if (goal is { } stuckGoal && TryFindObstacle(ent, xform, stuckGoal, 3f, out var obstacle))
            {
                comp.BerserkObstacle = obstacle;
                comp.BerserkObstacleSince = now;
            }
            else
            {
                repick = true;
            }
        }

        if (repick)
        {
            if (goal is { } old && !_transform.InRange(xform.Coordinates, old, 1.5f))
                BlacklistSpot(comp, old);

            goal = PickBerserkRoam(ent, xform, now);
            comp.BerserkRoamGoal = goal;
            comp.BerserkRoamStarted = now;
            comp.BerserkRoams++;
            if (goal == null)
            {
                // Cramped spot: a short step somewhere, still moving.
                var step = xform.Coordinates.Offset(_random.NextAngle().ToVec() * _random.NextFloat(2f, 5f));
                comp.BerserkRoamGoal = step;
                goal = step;
            }
        }

        BerserkApproach(ent, xform, goal!.Value, myPos, reach, now);
    }

    #endregion
}

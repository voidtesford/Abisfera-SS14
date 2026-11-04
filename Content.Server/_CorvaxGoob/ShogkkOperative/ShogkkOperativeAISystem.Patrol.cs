using System.Linq;
using System.Numerics;
using Content.Server.Atmos.Components;
using Content.Server.NPC.Components;
using Content.Shared.Atmos.Components;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

public sealed partial class ShogkkOperativeAISystem
{
    #region Patrol

    private void UpdatePatrol(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;

        if (TryStartHealing(ent, now))
            return;

        // «Докладываю»: every ReportInterval while on patrol (and never more often than the radio cooldown).
        if (now >= comp.NextReport && TryReport(ent, xform, now))
            comp.NextReport = now + comp.ReportInterval;

        // A fresh enemy position he knows about (an ally's call he didn't act on yet): go and check it.
        if (FreshKnownEnemy(comp, now) is { } known)
        {
            BeginAssist(ent, known, null);
            return;
        }

        if (now < comp.PauseUntil)
            return;

        if (comp.Destination is { } dest && now < comp.DestinationTimeout && !ReachedOrStuck(ent, xform, dest, 1.0f, now))
            return;

        if (comp.Destination != null)
        {
            // Arrived (or gave up): a short look around before the next leg.
            comp.Destination = null;
            _steering.Unregister(ent);
            comp.PauseUntil = now + TimeSpan.FromSeconds(_random.NextFloat(1f, 3f));
            return;
        }

        if (PickPatrolPoint(ent, xform, xform.Coordinates, comp.PatrolMinDistance, comp.PatrolMaxDistance, now) is not { } next)
        {
            comp.PauseUntil = now + TimeSpan.FromSeconds(2);
            return;
        }

        comp.Destination = next;
        comp.DestinationTimeout = now + TimeSpan.FromSeconds(30);
        comp.ProgressTime = now;
        comp.ProgressPosition = _transform.GetWorldPosition(xform);
        SteerTo(ent, next, 1.0f);
    }

    private EntityCoordinates? FreshKnownEnemy(ShogkkOperativeAIComponent comp, TimeSpan now)
    {
        EntityCoordinates? best = null;
        var bestTime = TimeSpan.MinValue;
        foreach (var (_, known) in comp.KnownEnemies)
        {
            if (now - known.Time > TimeSpan.FromSeconds(30) || known.Time <= bestTime)
                continue;

            best = known.Position;
            bestTime = known.Time;
        }

        return best;
    }

    /// <summary>
    /// The most "unknown" of a few random floor spots around <paramref name="center"/>: cells nobody (he or an ally who
    /// reported in) has been to lately score highest, given-up spots are skipped for a while.
    /// </summary>
    private EntityCoordinates? PickPatrolPoint(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform,
        EntityCoordinates center, float minDist, float maxDist, TimeSpan now)
    {
        var comp = ent.Comp;
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var centerMap = _transform.ToMapCoordinates(center);
        if (centerMap.MapId != xform.MapID)
            return null;

        EntityCoordinates? best = null;
        var bestScore = float.MinValue;
        for (var i = 0; i < 12; i++)
        {
            var angle = _random.NextFloat(0f, MathF.Tau);
            var dist = _random.NextFloat(minDist, maxDist);
            var world = centerMap.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * dist;
            var tile = _map.WorldToTile(gridUid, grid, world);
            if (!IsFreeFloor(gridUid, grid, tile))
                continue;

            var key = (gridUid, CellOf(comp, tile));
            if (comp.Blacklist.TryGetValue(key, out var until) && until > now)
                continue;

            var score = Novelty(comp, key, now) + _random.NextFloat(0f, 0.15f);
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = _map.GridTileToLocal(gridUid, grid, tile);
        }

        return best;
    }

    private bool IsFreeFloor(EntityUid gridUid, MapGridComponent grid, Vector2i tile)
    {
        if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty)
            return false;

        // Walls, windows, closed airlocks are airtight; nobody stands inside them.
        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (TryComp<AirtightComponent>(uid, out var airtight) && airtight.AirBlocked)
                return false;
        }

        return true;
    }

    /// <summary>1 for a cell never (or long ago) visited, down to 0 for one visited just now.</summary>
    private static float Novelty(ShogkkOperativeAIComponent comp, (EntityUid, Vector2i) key, TimeSpan now)
    {
        if (!comp.Visited.TryGetValue(key, out var when))
            return 1f;

        return (float) Math.Clamp((now - when).TotalSeconds / comp.VisitedForget.TotalSeconds, 0, 1);
    }

    private Vector2i CellOf(ShogkkOperativeAIComponent comp, Vector2i tile)
    {
        var size = Math.Max(1, comp.CellSize);
        return new Vector2i((int) MathF.Floor(tile.X / (float) size), (int) MathF.Floor(tile.Y / (float) size));
    }

    /// <summary>Cell (grid, cell) he stands in, or null off-grid.</summary>
    public (EntityUid Grid, Vector2i Cell)? CurrentCell(EntityUid uid, ShogkkOperativeAIComponent comp, TransformComponent xform)
    {
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        return (gridUid, CellOf(comp, tile));
    }

    private void MarkVisited(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        if (CurrentCell(ent, ent.Comp, xform) is not { } key)
            return;

        ent.Comp.Visited[key] = now;
        TrimVisited(ent.Comp);
    }

    private static void TrimVisited(ShogkkOperativeAIComponent comp)
    {
        if (comp.Visited.Count <= comp.MaxVisited)
            return;

        // Forget the oldest ~10% at once (so this sort runs rarely).
        var drop = comp.Visited.Count - comp.MaxVisited + comp.MaxVisited / 10;
        foreach (var key in comp.Visited.OrderBy(kv => kv.Value).Take(drop).Select(kv => kv.Key).ToList())
            comp.Visited.Remove(key);
    }

    #endregion

    #region Steering

    private void SteerTo(EntityUid uid, EntityCoordinates coordinates, float range)
    {
        // Re-planning on every think would restart the path each time: only when the goal really moved.
        if (TryComp<NPCSteeringComponent>(uid, out var existing))
        {
            if (existing.Status != SteeringStatus.NoPath && _transform.InRange(existing.Coordinates, coordinates, 0.75f))
            {
                existing.Range = range;
                return;
            }

            if (existing.Status == SteeringStatus.NoPath && !existing.Coordinates.Equals(coordinates))
            {
                existing.Status = SteeringStatus.Moving;
                existing.FailedPathCount = 0;
            }
        }

        var steering = _steering.Register(uid, coordinates, existing);
        steering.Range = range;
        steering.Flags |= OperativePathFlags;
    }

    /// <summary>
    /// True once within <paramref name="range"/> of <paramref name="dest"/>, or when the walk there is hopeless (no path,
    /// or no progress for <see cref="ShogkkOperativeAIComponent.StallTimeout"/>): that spot's cell is then avoided for a
    /// minute.
    /// </summary>
    private bool ReachedOrStuck(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, EntityCoordinates dest,
        float range, TimeSpan now)
    {
        var comp = ent.Comp;
        if (_transform.InRange(xform.Coordinates, dest, range + 0.3f))
            return true;

        var pos = _transform.GetWorldPosition(xform);
        if ((pos - comp.ProgressPosition).LengthSquared() > 0.25f)
        {
            comp.ProgressPosition = pos;
            comp.ProgressTime = now;
        }

        var noPath = TryComp<NPCSteeringComponent>(ent, out var steering) && steering.Status == SteeringStatus.NoPath;
        if (!noPath && now - comp.ProgressTime < comp.StallTimeout)
            return false;

        if (dest.EntityId is var gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var tile = _map.TileIndicesFor(gridUid, grid, dest);
            comp.Blacklist[(gridUid, CellOf(comp, tile))] = now + TimeSpan.FromSeconds(60);
        }

        comp.ProgressTime = now;
        _steering.Unregister(ent);
        return true;
    }

    #endregion
}

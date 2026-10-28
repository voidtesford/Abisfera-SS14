using System.Linq;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Passive patrol: distant (10–30 tiles), reachable destinations, preferring areas he hasn't been to lately.
/// Doors on the way are opened (see <see cref="OpenDoorsAhead"/>); unreachable picks are remembered and avoided.
/// </summary>
public sealed partial class GilbertAISystem
{
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TurfSystem _turf = default!;

    private (EntityUid Grid, Vector2i Cell)? CellOf(TransformComponent xform, GilbertAIComponent comp)
    {
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return null;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        return (gridUid, CellOfTile(tile, comp));
    }

    private static Vector2i CellOfTile(Vector2i tile, GilbertAIComponent comp)
    {
        var size = Math.Max(1, comp.ExploreCellSize);
        return new Vector2i((int) MathF.Floor(tile.X / (float) size), (int) MathF.Floor(tile.Y / (float) size));
    }

    /// <summary>Remembers the area he's in right now.</summary>
    private void MarkVisited(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        if (CellOf(xform, ent.Comp) is not { } cell)
            return;

        ent.Comp.Visited[cell] = now;

        // v9: loose memory of the surroundings too (he saw the neighbouring cells), as if half a minute ago.
        var loose = now - TimeSpan.FromSeconds(30);
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;

                var near = (cell.Grid, cell.Cell + new Vector2i(dx, dy));
                if (!ent.Comp.Visited.TryGetValue(near, out var at) || at < loose)
                    ent.Comp.Visited[near] = loose;
            }
        }
    }

    /// <summary>Has he been in the area around these coordinates recently?</summary>
    public bool IsVisited(Entity<GilbertAIComponent> ent, EntityCoordinates coords)
    {
        var gridUid = _transform.GetGrid(coords);
        if (gridUid is not { } g || !TryComp<MapGridComponent>(g, out var grid))
            return false;

        var cell = CellOfTile(_map.TileIndicesFor(g, grid, coords), ent.Comp);
        return ent.Comp.Visited.TryGetValue((g, cell), out var at) && _timing.CurTime - at < ent.Comp.ExploreMemory;
    }

    /// <summary>
    /// Picks the next patrol destination and starts walking there.
    /// <paramref name="failed"/> is a destination that just turned out unreachable: its area is marked as done.
    /// </summary>
    private void PickIdleDestination(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, EntityCoordinates? failed = null)
    {
        var comp = ent.Comp;
        comp.Route.Clear();
        comp.RouteLeg = null;

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            Wander(ent, xform.Coordinates, comp.WanderRadius);
            return;
        }

        if (failed is { } bad && _transform.GetGrid(bad) == gridUid)
        {
            comp.Visited[(gridUid, CellOfTile(_map.TileIndicesFor(gridUid, grid, bad), comp))] = now;
            BlacklistSpot(comp, bad);
        }

        // Forget very old visits so the memory doesn't grow without bound.
        if (comp.Visited.Count > 512)
        {
            foreach (var key in comp.Visited.Where(kv => now - kv.Value > comp.ExploreMemory).Select(kv => kv.Key).ToList())
                comp.Visited.Remove(key);
        }

        var origin = _transform.WithEntityId(xform.Coordinates, gridUid).Position;
        EntityCoordinates? best = null;
        var bestScore = float.MinValue;

        for (var i = 0; i < comp.ExploreCandidates; i++)
        {
            var dist = _random.NextFloat(comp.ExploreMinDistance, comp.ExploreMaxDistance);
            var local = origin + _random.NextAngle().ToVec() * dist;
            var candidate = new EntityCoordinates(gridUid, local);
            var tile = _map.TileIndicesFor(gridUid, grid, candidate);

            // Must be a floor tile with nothing solid on it (no walls, closed airlocks, machines).
            if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty
                || _turf.IsSpace(tileRef) || _turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
                continue;

            var cell = (gridUid, CellOfTile(tile, comp));
            // v9: places he got stuck going to are skipped for a while.
            if (IsBlacklisted(comp, cell, now))
                continue;

            // v9: fading memory: never seen / long forgotten areas first, the ones he just left last.
            // v9 (t256u): ...and chunks he walked through within the last 5 minutes (trail) are worth less.
            var unknown = KnownTile(comp, gridUid, tile) == null ? 10f : 0f;
            var score = Novelty(comp, cell, now) * TrailNovelty(comp, gridUid, ChunkOf(tile, comp), now) * 100f
                        + unknown + dist * 0.5f + _random.NextFloat(0f, 8f);

            if (score > bestScore)
            {
                bestScore = score;
                best = _map.GridTileToLocal(gridUid, grid, tile);
            }
        }

        // v9 (t256u): the remembered map offers places further along known corridors / behind doors.
        var myTile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        if (TryPickFromMentalMap(ent, gridUid, grid, myTile, now, out var remembered, out var memScore) && memScore > bestScore)
            best = remembered;

        if (best is not { } dest)
        {
            // Cramped spot (small room, shuttle): a short local walk instead.
            Wander(ent, xform.Coordinates, comp.WanderRadius);
            return;
        }

        // v9 (t256u): far trips follow the shortest route he knows (waypoints through the doors on the way).
        if (PlanRoute(comp, xform.Coordinates, dest, InBerserk(ent), out var waypoints, out var length)
            && length >= comp.RouteMinDistance && waypoints.Count > 1)
        {
            comp.RoutesPlanned++;
            comp.Route.AddRange(waypoints.Skip(1));
            RegisterMove(ent, waypoints[0], 1.5f);
            comp.RouteLeg = waypoints[0];
            return;
        }

        RegisterMove(ent, dest, 1f);
    }

    /// <summary>v9 (t256u): reached a patrol waypoint: on to the next one. False when the route is done.</summary>
    private bool NextRouteWaypoint(Entity<GilbertAIComponent> ent, EntityCoordinates reached)
    {
        var comp = ent.Comp;
        if (comp.Route.Count == 0 || comp.RouteLeg is not { } leg || !leg.Equals(reached))
        {
            comp.Route.Clear();
            comp.RouteLeg = null;
            return false;
        }

        var next = comp.Route[0];
        comp.Route.RemoveAt(0);
        RegisterMove(ent, next, comp.Route.Count == 0 ? 1f : 1.5f);
        comp.RouteLeg = comp.Route.Count == 0 ? null : next;
        return true;
    }
}

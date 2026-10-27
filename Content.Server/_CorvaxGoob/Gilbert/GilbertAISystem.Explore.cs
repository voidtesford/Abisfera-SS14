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
        if (CellOf(xform, ent.Comp) is { } cell)
            ent.Comp.Visited[cell] = now;
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

        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
        {
            Wander(ent, xform.Coordinates, comp.WanderRadius);
            return;
        }

        if (failed is { } bad && _transform.GetGrid(bad) == gridUid)
            comp.Visited[(gridUid, CellOfTile(_map.TileIndicesFor(gridUid, grid, bad), comp))] = now;

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
            float score;
            if (!comp.Visited.TryGetValue(cell, out var at) || now - at >= comp.ExploreMemory)
                score = 100000f;                         // never / long ago: best
            else
                score = (float) (now - at).TotalSeconds; // the longer ago, the better

            score += dist + _random.NextFloat(0f, 5f);

            if (score > bestScore)
            {
                bestScore = score;
                best = _map.GridTileToLocal(gridUid, grid, tile);
            }
        }

        if (best is not { } dest)
        {
            // Cramped spot (small room, shuttle): a short local walk instead.
            Wander(ent, xform.Coordinates, comp.WanderRadius);
            return;
        }

        RegisterMove(ent, dest, 1f);
    }
}

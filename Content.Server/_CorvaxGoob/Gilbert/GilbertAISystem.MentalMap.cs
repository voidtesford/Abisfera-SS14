using System.Linq;
using Content.Shared.Climbing.Components;
using Content.Shared.Doors;
using Content.Shared.Doors.Components;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Physics.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>v9 (t256u): what he remembers about one tile.</summary>
public enum GilbertTileKind : byte
{
    Free,
    Wall,
    /// <summary>A door / airlock he can get through (ID, pushing, forcing it).</summary>
    Door,
    /// <summary>A bolted or welded door: a wall for him until the berserk smashes it.</summary>
    DoorLocked,
    Space,
    /// <summary>v9 (t259u): a table / anything climbable: passable, at the price of the vault do-after.</summary>
    Climbable,
}

public struct GilbertTileMemory
{
    public GilbertTileKind Kind;
    public TimeSpan Seen;
}

/// <summary>One node of the coarse route graph: a square chunk of tiles (part of a room or corridor).</summary>
public sealed class GilbertChunkNode
{
    public int Passable;
    public int Doors;
    /// <summary>v9 (t259u): climbable tiles (tables) in the chunk.</summary>
    public int Climbs;
    public bool Dirty = true;
    /// <summary>Bit i: can walk into the neighbouring chunk in direction i (see <see cref="GilbertAISystem.ChunkDirs"/>).</summary>
    public byte Edges;
    /// <summary>The same for the berserk (bolted doors are smashed).</summary>
    public byte BerserkEdges;
    /// <summary>A walkable tile near the middle of the chunk: where routes go through it.</summary>
    public Vector2i? Anchor;
    /// <summary>Borders on tiles he has never seen.</summary>
    public bool Frontier;
}

public sealed class GilbertGridMemory
{
    public readonly Dictionary<Vector2i, GilbertTileMemory> Tiles = new();
    public readonly Dictionary<Vector2i, GilbertChunkNode> Chunks = new();
}

/// <summary>
/// v9 (t256u): his mental map. Tiles he has seen (walls, doors, floor, space) per grid, refreshed every time he sees
/// them again (a broken wall becomes floor, a door that got bolted becomes a wall for him, a destroyed door floor),
/// a coarse chunk graph on top of it for routes, and a 5-minute trail of where he walked.
/// </summary>
public sealed class GilbertMentalMap
{
    public readonly Dictionary<EntityUid, GilbertGridMemory> Grids = new();
    /// <summary>Last time he walked through a chunk (within <see cref="GilbertAIComponent.TrailMemory"/>).</summary>
    public readonly Dictionary<(EntityUid Grid, Vector2i Chunk), TimeSpan> TrailLast = new();
    /// <summary>His footsteps, oldest first (one entry per tile change), expiring after the trail memory.</summary>
    public readonly Queue<(TimeSpan At, EntityUid Grid, Vector2i Tile)> Trail = new();
    public (EntityUid Grid, Vector2i Tile)? LastTrailTile;

    /// <summary>Diagnostics / tests.</summary>
    public int Scans;
    public int TilesChanged;
}

public sealed partial class GilbertAISystem
{
    /// <summary>Right, up, left, down.</summary>
    public static readonly Vector2i[] ChunkDirs = { new(1, 0), new(0, 1), new(-1, 0), new(0, -1) };

    /// <summary>Scan offsets in order of distance, each with the offset one step closer to him (cheap line of sight).</summary>
    private readonly Dictionary<int, (Vector2i Offset, int Prev)[]> _scanTables = new();

    #region Scanning

    /// <summary>Throttled: remembers what he sees around him and where he walked.</summary>
    private void UpdateMentalMap(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
        RecordTrail(comp, gridUid, tile, now);

        if (now < comp.NextMapScan)
            return;

        comp.NextMapScan = now + comp.MapScanInterval;
        ScanAround(comp, gridUid, grid, tile, comp.MapScanRadius, now);
        PruneTrail(comp, now);
    }

    private (Vector2i Offset, int Prev)[] ScanTable(int radius)
    {
        if (_scanTables.TryGetValue(radius, out var table))
            return table;

        var offsets = new List<Vector2i>();
        for (var x = -radius; x <= radius; x++)
        {
            for (var y = -radius; y <= radius; y++)
            {
                if (x * x + y * y <= radius * radius)
                    offsets.Add(new Vector2i(x, y));
            }
        }

        offsets.Sort((a, b) => Math.Max(Math.Abs(a.X), Math.Abs(a.Y)).CompareTo(Math.Max(Math.Abs(b.X), Math.Abs(b.Y))));
        var index = new Dictionary<Vector2i, int>();
        for (var i = 0; i < offsets.Count; i++)
            index[offsets[i]] = i;

        table = new (Vector2i, int)[offsets.Count];
        for (var i = 0; i < offsets.Count; i++)
        {
            var o = offsets[i];
            var d = Math.Max(Math.Abs(o.X), Math.Abs(o.Y));
            var prev = -1;
            if (d > 0)
            {
                var p = new Vector2i((int) MathF.Round(o.X * (d - 1f) / d), (int) MathF.Round(o.Y * (d - 1f) / d));
                prev = index.GetValueOrDefault(p, 0);
            }

            table[i] = (o, prev);
        }

        _scanTables[radius] = table;
        return table;
    }

    /// <summary>
    /// Looks around <paramref name="center"/>: every tile within <paramref name="radius"/> that isn't hidden behind
    /// a wall or a closed door is (re-)classified and remembered. About 200 tiles, once a second.
    /// </summary>
    public void ScanAround(GilbertAIComponent comp, EntityUid gridUid, MapGridComponent grid, Vector2i center, int radius, TimeSpan now)
    {
        var table = ScanTable(radius);
        var visible = new bool[table.Length];
        var see = new bool[table.Length];
        if (!comp.MentalMap.Grids.TryGetValue(gridUid, out var memory))
        {
            memory = new GilbertGridMemory();
            comp.MentalMap.Grids[gridUid] = memory;
        }

        for (var i = 0; i < table.Length; i++)
        {
            var (offset, prev) = table[i];
            if (prev >= 0 && !(visible[prev] && see[prev]))
                continue;

            visible[i] = true;
            var tile = center + offset;
            var kind = ClassifyTile(gridUid, grid, tile, out var transparent);
            // His own tile never blocks his view.
            see[i] = transparent || prev < 0;
            Remember(comp, memory, tile, kind, now);
        }

        comp.MentalMap.Scans++;
    }

    /// <summary>What a tile is right now (and whether he can see past it).</summary>
    public GilbertTileKind ClassifyTile(EntityUid gridUid, MapGridComponent grid, Vector2i tile, out bool transparent)
    {
        transparent = true;
        if (!_map.TryGetTileRef(gridUid, grid, tile, out var tileRef) || tileRef.Tile.IsEmpty || _turf.IsSpace(tileRef))
            return GilbertTileKind.Space;

        var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (!TryComp<DoorComponent>(uid, out var door))
                continue;

            if (door.State == DoorState.Open)
                return GilbertTileKind.Free;

            transparent = IsSeeThrough(uid.Value);
            if (door.State == DoorState.Welded
                || TryComp<DoorBoltComponent>(uid, out var bolts) && bolts.BoltsDown)
                return GilbertTileKind.DoorLocked;

            return GilbertTileKind.Door;
        }

        // v9 (t259u): tables & co. (on the table layer, not Impassable) block walking but not climbing over
        // them, and not the view. Anything else solid on the same tile (a wall) still makes it a wall.
        var climb = false;
        var solid = false;
        anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (HasComp<ClimbableComponent>(uid))
                climb = true;
            else if (TryComp<PhysicsComponent>(uid, out var body) && body.CanCollide && body.Hard
                     && (body.CollisionLayer & (int) CollisionGroup.Impassable) != 0)
                solid = true;
        }

        if (climb && !solid)
            return GilbertTileKind.Climbable;

        if (!_turf.IsTileBlocked(tileRef, CollisionGroup.Impassable))
            return GilbertTileKind.Free;

        // Windows are walls he can look through.
        transparent = false;
        anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile);
        while (anchored.MoveNext(out var uid))
        {
            if (IsSeeThrough(uid.Value))
            {
                transparent = true;
                break;
            }
        }

        return GilbertTileKind.Wall;
    }

    private void Remember(GilbertAIComponent comp, GilbertGridMemory memory, Vector2i tile, GilbertTileKind kind, TimeSpan now)
    {
        var known = memory.Tiles.TryGetValue(tile, out var old);
        memory.Tiles[tile] = new GilbertTileMemory { Kind = kind, Seen = now };
        if (known && old.Kind == kind)
            return;

        if (known)
            comp.MentalMap.TilesChanged++;

        // Only the chunk graph around a change is rebuilt (lazily, when a route needs it).
        var chunk = ChunkOf(tile, comp);
        MarkChunkDirty(memory, chunk);
        foreach (var dir in ChunkDirs)
            MarkChunkDirty(memory, chunk + dir);
    }

    private static void MarkChunkDirty(GilbertGridMemory memory, Vector2i chunk)
    {
        if (!memory.Chunks.TryGetValue(chunk, out var node))
        {
            node = new GilbertChunkNode();
            memory.Chunks[chunk] = node;
        }

        node.Dirty = true;
    }

    public static Vector2i ChunkOf(Vector2i tile, GilbertAIComponent comp)
    {
        var size = Math.Max(1, comp.MapChunkSize);
        return new Vector2i((int) MathF.Floor(tile.X / (float) size), (int) MathF.Floor(tile.Y / (float) size));
    }

    /// <summary>What he remembers about this tile, if anything.</summary>
    public GilbertTileKind? KnownTile(GilbertAIComponent comp, EntityUid gridUid, Vector2i tile)
    {
        return comp.MentalMap.Grids.TryGetValue(gridUid, out var memory) && memory.Tiles.TryGetValue(tile, out var t)
            ? t.Kind
            : null;
    }

    private static bool Walkable(GilbertTileKind kind, bool berserk)
    {
        return kind is GilbertTileKind.Free or GilbertTileKind.Door or GilbertTileKind.Climbable
               || berserk && kind == GilbertTileKind.DoorLocked;
    }

    #endregion

    #region Trail

    private void RecordTrail(GilbertAIComponent comp, EntityUid gridUid, Vector2i tile, TimeSpan now)
    {
        var map = comp.MentalMap;
        comp.MentalMap.TrailLast[(gridUid, ChunkOf(tile, comp))] = now;
        if (map.LastTrailTile is { } last && last.Grid == gridUid && last.Tile == tile)
            return;

        map.LastTrailTile = (gridUid, tile);
        map.Trail.Enqueue((now, gridUid, tile));
    }

    private static void PruneTrail(GilbertAIComponent comp, TimeSpan now)
    {
        var map = comp.MentalMap;
        while (map.Trail.Count > 0 && now - map.Trail.Peek().At > comp.TrailMemory)
            map.Trail.Dequeue();

        if (map.TrailLast.Count > 0 && map.TrailLast.Values.Any(t => now - t > comp.TrailMemory))
        {
            foreach (var key in map.TrailLast.Where(kv => now - kv.Value > comp.TrailMemory).Select(kv => kv.Key).ToList())
                map.TrailLast.Remove(key);
        }
    }

    /// <summary>
    /// 1 = not walked through within the trail window (5 min), 0 = he is there right now; in between linearly.
    /// </summary>
    public float TrailNovelty(GilbertAIComponent comp, EntityUid gridUid, Vector2i chunk, TimeSpan now)
    {
        if (!comp.MentalMap.TrailLast.TryGetValue((gridUid, chunk), out var at))
            return 1f;

        var age = (now - at).TotalSeconds;
        var window = Math.Max(1.0, comp.TrailMemory.TotalSeconds);
        return age >= window ? 1f : (float) (age / window);
    }

    #endregion

    #region Route graph

    private void RefreshChunk(GilbertAIComponent comp, GilbertGridMemory memory, Vector2i chunk, GilbertChunkNode node)
    {
        if (!node.Dirty)
            return;

        node.Dirty = false;
        node.Passable = 0;
        node.Doors = 0;
        node.Climbs = 0;
        node.Edges = 0;
        node.BerserkEdges = 0;
        node.Anchor = null;
        node.Frontier = false;

        var size = Math.Max(1, comp.MapChunkSize);
        var origin = chunk * size;
        var middle = new System.Numerics.Vector2(origin.X + (size - 1) / 2f, origin.Y + (size - 1) / 2f);
        var bestDist = float.MaxValue;
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                var tile = origin + new Vector2i(x, y);
                if (!memory.Tiles.TryGetValue(tile, out var t) || !Walkable(t.Kind, true))
                    continue;

                if (t.Kind == GilbertTileKind.Climbable)
                {
                    node.Climbs++;
                }
                else if (t.Kind != GilbertTileKind.Free)
                {
                    node.Doors++;
                    if (t.Kind == GilbertTileKind.DoorLocked)
                        continue;
                }
                else
                {
                    node.Passable++;
                    var dist = (new System.Numerics.Vector2(tile.X, tile.Y) - middle).LengthSquared();
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        node.Anchor = tile;
                    }
                }

                foreach (var dir in ChunkDirs)
                {
                    if (!memory.Tiles.ContainsKey(tile + dir))
                        node.Frontier = true;
                }
            }
        }

        // A chunk that is nothing but a door: route through the door tile itself.
        if (node.Anchor == null && node.Doors + node.Climbs > 0)
        {
            for (var x = 0; x < size && node.Anchor == null; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var tile = origin + new Vector2i(x, y);
                    if (memory.Tiles.TryGetValue(tile, out var t) && t.Kind is GilbertTileKind.Door or GilbertTileKind.Climbable)
                    {
                        node.Anchor = tile;
                        break;
                    }
                }
            }
        }

        // Edges: some walkable tile on this side of the border next to a walkable tile on the other side.
        for (var d = 0; d < ChunkDirs.Length; d++)
        {
            var dir = ChunkDirs[d];
            for (var i = 0; i < size; i++)
            {
                var inside = dir.X switch
                {
                    1 => origin + new Vector2i(size - 1, i),
                    -1 => origin + new Vector2i(0, i),
                    _ => dir.Y == 1 ? origin + new Vector2i(i, size - 1) : origin + new Vector2i(i, 0),
                };

                if (!memory.Tiles.TryGetValue(inside, out var a) || !memory.Tiles.TryGetValue(inside + dir, out var b))
                    continue;

                if (Walkable(a.Kind, false) && Walkable(b.Kind, false))
                    node.Edges |= (byte) (1 << d);
                if (Walkable(a.Kind, true) && Walkable(b.Kind, true))
                    node.BerserkEdges |= (byte) (1 << d);
            }
        }
    }

    private GilbertChunkNode? Node(GilbertAIComponent comp, GilbertGridMemory memory, Vector2i chunk)
    {
        if (!memory.Chunks.TryGetValue(chunk, out var node))
            return null;

        RefreshChunk(comp, memory, chunk, node);
        return node.Anchor == null ? null : node;
    }

    /// <summary>
    /// Shortest known paths (in tiles) from <paramref name="start"/> to every remembered chunk he can reach,
    /// bounded by <see cref="GilbertAIComponent.RouteMaxExpanded"/>. With <paramref name="goal"/> it stops there.
    /// </summary>
    private Dictionary<Vector2i, (float Cost, Vector2i Prev)> ExpandChunks(GilbertAIComponent comp, GilbertGridMemory memory,
        Vector2i start, bool berserk, Vector2i? goal = null)
    {
        var result = new Dictionary<Vector2i, (float Cost, Vector2i Prev)>();
        if (Node(comp, memory, start) == null)
            return result;

        var size = Math.Max(1, comp.MapChunkSize);
        var open = new PriorityQueue<Vector2i, float>();
        result[start] = (0f, start);
        open.Enqueue(start, 0f);
        var done = new HashSet<Vector2i>();
        while (open.TryDequeue(out var current, out _))
        {
            if (!done.Add(current))
                continue;

            if (current == goal || done.Count > comp.RouteMaxExpanded)
                break;

            var node = Node(comp, memory, current)!;
            var edges = berserk ? node.BerserkEdges : node.Edges;
            var cost = result[current].Cost;
            for (var d = 0; d < ChunkDirs.Length; d++)
            {
                if ((edges & (1 << d)) == 0)
                    continue;

                var next = current + ChunkDirs[d];
                if (done.Contains(next) || Node(comp, memory, next) is not { } nextNode)
                    continue;

                // Doors cost a moment (opening / forcing them); v9 (t259u): a table costs the vault do-after
                // (in tiles of walking), so he goes round it only when that is really shorter.
                var step = size + (nextNode.Doors > 0 ? 1.5f : 0f)
                           + (nextNode.Climbs > 0 ? (berserk ? comp.BerserkClimbRouteCost : comp.ClimbRouteCost) : 0f);
                var total = cost + step;
                if (result.TryGetValue(next, out var known) && known.Cost <= total)
                    continue;

                result[next] = (total, current);
                var h = goal is { } g ? (g - next).Length * size : 0f;
                open.Enqueue(next, total + h);
            }
        }

        return result;
    }

    /// <summary>
    /// The shortest route he knows from <paramref name="from"/> to <paramref name="to"/> over remembered floor and
    /// doors: waypoints every few chunks and at every door on the way, ending at <paramref name="to"/>.
    /// False when either end is somewhere he doesn't know or there is no known way.
    /// </summary>
    public bool PlanRoute(GilbertAIComponent comp, EntityCoordinates from, EntityCoordinates to, bool berserk,
        out List<EntityCoordinates> waypoints, out float length)
    {
        waypoints = new List<EntityCoordinates>();
        length = 0f;
        var gridUid = _transform.GetGrid(from);
        if (gridUid is not { } g || _transform.GetGrid(to) != g || !TryComp<MapGridComponent>(g, out var grid)
            || !comp.MentalMap.Grids.TryGetValue(g, out var memory))
            return false;

        var start = ChunkOf(_map.TileIndicesFor(g, grid, from), comp);
        var goal = ChunkOf(_map.TileIndicesFor(g, grid, to), comp);
        if (Node(comp, memory, goal) == null)
            return false;

        var expanded = ExpandChunks(comp, memory, start, berserk, goal);
        if (!expanded.TryGetValue(goal, out var end))
            return false;

        length = end.Cost;
        var chain = new List<Vector2i>();
        var at = goal;
        while (at != start)
        {
            chain.Add(at);
            at = expanded[at].Prev;
        }

        chain.Reverse();
        // Thin the chain: every third chunk, every chunk with a door, never the goal chunk (the exact spot is).
        for (var i = 0; i < chain.Count - 1; i++)
        {
            var node = memory.Chunks[chain[i]];
            if (node.Doors == 0 && node.Climbs == 0 && (i + 1) % 3 != 0)
                continue;

            waypoints.Add(_map.GridTileToLocal(g, grid, node.Anchor!.Value));
        }

        waypoints.Add(to);
        return true;
    }

    /// <summary>
    /// Patrol pick over the mental map: every remembered chunk he can reach, scored by how long ago he walked
    /// there (trail), how stale the area feels, whether it borders unexplored space, and the known path length.
    /// </summary>
    private bool TryPickFromMentalMap(Entity<GilbertAIComponent> ent, EntityUid gridUid, MapGridComponent grid,
        Vector2i myTile, TimeSpan now, out EntityCoordinates dest, out float bestScore)
    {
        var comp = ent.Comp;
        dest = default;
        bestScore = float.MinValue;
        if (!comp.MentalMap.Grids.TryGetValue(gridUid, out var memory))
            return false;

        var expanded = ExpandChunks(comp, memory, ChunkOf(myTile, comp), InBerserk(ent));
        if (expanded.Count < 4)
            return false;

        var found = false;
        foreach (var (chunk, (cost, _)) in expanded)
        {
            if (cost < comp.ExploreMinDistance)
                continue;

            var node = memory.Chunks[chunk];
            if (node.Anchor is not { } anchor)
                continue;

            var cell = (gridUid, CellOfTile(anchor, comp));
            if (IsBlacklisted(comp, cell, now))
                continue;

            var novelty = Novelty(comp, cell, now) * TrailNovelty(comp, gridUid, chunk, now);
            var score = novelty * 100f + (node.Frontier ? 15f : 0f) + MathF.Min(cost, comp.ExploreMaxDistance) * 0.5f
                        - MathF.Max(0f, cost - comp.ExploreMaxDistance * 1.5f) * 0.5f + _random.NextFloat(0f, 8f);
            if (score <= bestScore)
                continue;

            bestScore = score;
            dest = _map.GridTileToLocal(gridUid, grid, anchor);
            found = true;
        }

        return found;
    }

    #endregion
}

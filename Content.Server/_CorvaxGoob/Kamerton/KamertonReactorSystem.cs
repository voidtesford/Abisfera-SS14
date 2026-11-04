using System.Linq;
using System.Numerics;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Shared.NodeContainer;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Popups;
using Content.Server.Power.Components;
using Content.Server.Singularity.EntitySystems;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Shared.Atmos;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Examine;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Popups;
using Content.Shared.Power;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Containers;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton reactor. Parts link up like the AME shielding through the <see cref="KamertonNodeGroup"/>; the
/// <see cref="KamertonReactorCoreComponent">core</see> reads its group (fuel arrays, synchronizers, heat sinks,
/// radiators inside, suppressors, the console and the power unit as the shell around them), then once a second:
/// pairs charged stable/unstable cells per synchronizer (50 kW each, 4 per synchronizer), drains them,
/// heats up, lets the heat sinks and radiators carry the heat away, feeds the power unit's HV supplier,
/// leaks weak redspace if the ring is broken. Heat is counted in units (0 = room temperature) up to a limit of
/// 500 + 250 per radiator; from 75% of it the core is in meltdown (no stop, arcs around it), at the limit it collapses.
/// </summary>
public sealed class KamertonReactorSystem : EntitySystem
{
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly DamageableSystem _damage = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly KamertonCellSystem _cells = default!;
    [Dependency] private readonly MapSystem _map = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly NodeContainerSystem _nodes = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly RedspaceMapSystem _redspace = default!;
    [Dependency] private readonly RedspaceSuppressionSystem _suppression = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly KamertonDroneSystem _drone = default!;

    public const float StepInterval = 1f;

    private readonly List<RedspaceSuppressor> _suppressors = new();
    private readonly HashSet<Entity<MobStateComponent>> _mobs = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<KamertonReactorCoreComponent, ExaminedEvent>(OnCoreExamined);
        SubscribeLocalEvent<KamertonReactorCoreComponent, ComponentShutdown>(OnCoreShutdown);
        SubscribeLocalEvent<KamertonFuelArrayComponent, ComponentStartup>(OnArrayStartup);
        SubscribeLocalEvent<KamertonFuelArrayComponent, EntInsertedIntoContainerMessage>((uid, comp, _) => UpdateArrayVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonFuelArrayComponent, EntRemovedFromContainerMessage>((uid, comp, _) => UpdateArrayVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonReactorPartComponent, ExaminedEvent>(OnPartExamined);

        SubscribeLocalEvent<KamertonControlModuleComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<KamertonControlModuleComponent, KamertonReactorStartMessage>(OnStartMessage);
        SubscribeLocalEvent<KamertonControlModuleComponent, KamertonReactorStopMessage>(OnStopMessage);
        SubscribeLocalEvent<KamertonControlModuleComponent, KamertonReactorScramMessage>(OnScramMessage);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<KamertonReactorCoreComponent>();
        while (query.MoveNext(out var uid, out var core))
        {
            if (core.ScramCooldown > 0f)
                core.ScramCooldown = MathF.Max(0f, core.ScramCooldown - frameTime);

            core.Accumulator += frameTime;
            if (core.Accumulator < StepInterval)
                continue;

            var dt = core.Accumulator;
            core.Accumulator = 0f;
            Step((uid, core), dt);
        }

        // Power units nobody drives any more stop supplying.
        var units = EntityQueryEnumerator<KamertonPowerUnitComponent, KamertonReactorPartComponent, PowerSupplierComponent>();
        while (units.MoveNext(out var uid, out _, out var part, out var supplier))
        {
            if (part.Core is { } c && TryComp<KamertonReactorCoreComponent>(c, out var cc) && cc.PowerUnit == uid)
                continue;

            if (supplier.MaxSupply != 0f)
            {
                supplier.MaxSupply = 0f;
                SetOn(uid, false);
            }
        }
    }

    #region Assembly

    public const string NodeName = "kamerton";

    private static readonly Vector2i[] Cardinals =
    {
        new(0, 1), new(0, -1), new(1, 0), new(-1, 0),
    };

    /// <summary>Gets the reactor node group <paramref name="uid"/> is in.</summary>
    public KamertonNodeGroup? GetGroup(EntityUid uid)
    {
        if (!TryComp<NodeContainerComponent>(uid, out var container)
            || !_nodes.TryGetNode<Node>(container, NodeName, out var node))
            return null;

        return node.NodeGroup as KamertonNodeGroup;
    }

    /// <summary>
    /// Shell parts close the reactor off: suppressors, the console, the power unit, the fuel storage and the heat
    /// sinks, which sit on the edge so their pipes can leave the reactor.
    /// </summary>
    public static bool IsShell(KamertonPartKind kind)
    {
        return kind is KamertonPartKind.Suppressor or KamertonPartKind.Console or KamertonPartKind.PowerUnit
            or KamertonPartKind.HeatSink or KamertonPartKind.FuelStorage;
    }

    /// <summary>Parts that carry heat to each other through heat pipes: radiators and heat sinks.</summary>
    public static bool IsHeatPart(KamertonPartKind kind)
    {
        return kind is KamertonPartKind.Radiator or KamertonPartKind.HeatSink;
    }

    /// <summary>
    /// Re-reads the reactor from the core's node group, like the AME reads its shielding group: every anchored
    /// part linked to the core through neighbouring parts belongs to it, whatever the shape. Each inner part
    /// (core, arrays, synchronizers, radiators) must have all 8 neighbours taken by parts of the
    /// group, the same test the AME uses to find its cores, so the inner parts end up walled in by shell parts;
    /// every empty neighbour tile counts towards <see cref="KamertonReactorCoreComponent.RingMissing"/>.
    /// </summary>
    public void Scan(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;

        foreach (var old in comp.Parts)
        {
            if (TryComp<KamertonReactorPartComponent>(old, out var oldPart) && oldPart.Core == core.Owner)
                oldPart.Core = null;
            if (TryComp<KamertonFuelArrayComponent>(old, out var oldArray))
                oldArray.Synchronizer = null;
        }

        comp.Parts.Clear();
        comp.Synchronizers.Clear();
        comp.StableArrays.Clear();
        comp.UnstableArrays.Clear();
        comp.HeatSinks.Clear();
        comp.Radiators.Clear();
        comp.LinkedRadiators.Clear();
        comp.LinkedHeatSinks.Clear();
        comp.RingParts.Clear();
        comp.Console = null;
        comp.PowerUnit = null;
        comp.Storage = null;
        comp.Slave = false;
        comp.Oversized = false;
        comp.RingMissing = 8;

        var xform = Transform(core);
        if (!xform.Anchored || xform.GridUid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var grid))
            return;

        var group = GetGroup(core);
        if (group == null)
            return;

        if (group.MasterCore != core.Owner)
        {
            comp.Slave = true;
            return;
        }

        if (group.Nodes.Count > comp.MaxParts)
        {
            comp.Oversized = true;
            return;
        }

        // Tile -> part for every anchored member of the group on the core's grid.
        var tiles = new Dictionary<Vector2i, (EntityUid Uid, KamertonReactorPartComponent Part)>();
        foreach (var node in group.Nodes)
        {
            var uid = node.Owner;
            if (!TryComp<KamertonReactorPartComponent>(uid, out var part))
                continue;

            var px = Transform(uid);
            if (!px.Anchored || px.GridUid != gridUid)
                continue;

            tiles.TryAdd(_map.TileIndicesFor(gridUid, grid, px.Coordinates), (uid, part));
        }

        var missing = new HashSet<Vector2i>();
        var syncTiles = new Dictionary<Vector2i, EntityUid>();
        var arrayTiles = new List<(Vector2i Tile, EntityUid Uid)>();

        foreach (var (tile, (uid, part)) in tiles)
        {
            if (!IsShell(part.Kind))
            {
                // AME-style surround test over the 3x3 square around the part.
                for (var dx = -1; dx <= 1; dx++)
                {
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var n = tile + new Vector2i(dx, dy);
                        if (!tiles.ContainsKey(n))
                            missing.Add(n);
                    }
                }
            }

            switch (part.Kind)
            {
                case KamertonPartKind.Core:
                    // Extra cores are slaves, they are not parts of this reactor.
                    continue;
                case KamertonPartKind.Suppressor:
                    comp.RingParts.Add(uid);
                    break;
                case KamertonPartKind.Console:
                    comp.Console ??= uid;
                    comp.RingParts.Add(uid);
                    break;
                case KamertonPartKind.PowerUnit:
                    comp.PowerUnit ??= uid;
                    comp.RingParts.Add(uid);
                    break;
                case KamertonPartKind.FuelStorage:
                    comp.Storage ??= uid;
                    comp.RingParts.Add(uid);
                    break;
                case KamertonPartKind.Synchronizer:
                    syncTiles.TryAdd(tile, uid);
                    comp.Synchronizers.Add(uid);
                    break;
                case KamertonPartKind.StableArray:
                case KamertonPartKind.UnstableArray:
                    arrayTiles.Add((tile, uid));
                    break;
                case KamertonPartKind.HeatSink:
                    comp.HeatSinks.Add(uid);
                    break;
                case KamertonPartKind.Radiator:
                    comp.Radiators.Add(uid);
                    break;
            }

            Claim(core, uid, part);
        }

        comp.RingMissing = missing.Count;
        TraceHeatLines(tiles, comp);

        // Every array goes to the first synchronizer of the group next to it (N, S, E, W).
        foreach (var (tile, uid) in arrayTiles)
        {
            if (!TryComp<KamertonFuelArrayComponent>(uid, out var array))
                continue;

            foreach (var off in Cardinals)
            {
                if (!syncTiles.TryGetValue(tile + off, out var sync))
                    continue;

                array.Synchronizer = sync;
                break;
            }

            if (array.Kind == KamertonCellKind.Stable)
                comp.StableArrays.Add(uid);
            else
                comp.UnstableArrays.Add(uid);
        }
    }

    /// <summary>
    /// Heat path: core and arrays -> radiators -> heat sinks -> gas. A radiator only carries heat away when a
    /// chain of neighbouring radiators (N/S/E/W) ends at a heat sink; a heat sink only takes heat when a linked
    /// radiator touches it. Unlinked radiators still add heat capacity but dump nothing.
    /// </summary>
    private static void TraceHeatLines(
        Dictionary<Vector2i, (EntityUid Uid, KamertonReactorPartComponent Part)> tiles,
        KamertonReactorCoreComponent comp)
    {
        var queue = new Queue<Vector2i>();
        foreach (var (tile, (_, part)) in tiles)
        {
            if (part.Kind != KamertonPartKind.HeatSink)
                continue;

            foreach (var off in Cardinals)
            {
                if (tiles.TryGetValue(tile + off, out var n) && n.Part.Kind == KamertonPartKind.Radiator)
                    queue.Enqueue(tile + off);
            }
        }

        while (queue.TryDequeue(out var tile))
        {
            if (!comp.LinkedRadiators.Add(tiles[tile].Uid))
                continue;

            foreach (var off in Cardinals)
            {
                if (tiles.TryGetValue(tile + off, out var n) && n.Part.Kind == KamertonPartKind.Radiator
                    && !comp.LinkedRadiators.Contains(n.Uid))
                    queue.Enqueue(tile + off);
            }
        }

        foreach (var (tile, (uid, part)) in tiles)
        {
            if (part.Kind != KamertonPartKind.HeatSink)
                continue;

            foreach (var off in Cardinals)
            {
                if (tiles.TryGetValue(tile + off, out var n) && comp.LinkedRadiators.Contains(n.Uid))
                {
                    comp.LinkedHeatSinks.Add(uid);
                    break;
                }
            }
        }
    }

    /// <summary>Neighbour offsets in <see cref="KamertonReactorVisuals.Links"/> bit order: N, S, E, W, NE, NW, SE, SW.</summary>
    private static readonly Vector2i[] Neighbours =
    {
        new(0, 1), new(0, -1), new(1, 0), new(-1, 0), new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
    };

    /// <summary>
    /// Sets the hull smoothing of every part in the group, like the AME shielding joins into one structure:
    /// <see cref="KamertonReactorVisuals.Links"/> holds the 8 neighbours that belong to the same node group,
    /// <see cref="KamertonReactorVisuals.HeatLinks"/> the N/S/E/W heat-pipe joints (radiator-radiator and
    /// radiator-heat sink).
    /// </summary>
    public void UpdateLinkVisuals(KamertonNodeGroup group)
    {
        var members = new HashSet<EntityUid>();
        foreach (var node in group.Nodes)
        {
            members.Add(node.Owner);
        }

        foreach (var uid in members)
        {
            var flags = 0;
            var heatFlags = 0;
            var kind = CompOrNull<KamertonReactorPartComponent>(uid)?.Kind;
            var xform = Transform(uid);
            if (xform.Anchored && xform.GridUid is { } gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
            {
                var tile = _map.TileIndicesFor(gridUid, grid, xform.Coordinates);
                for (var i = 0; i < Neighbours.Length; i++)
                {
                    var anchored = _map.GetAnchoredEntitiesEnumerator(gridUid, grid, tile + Neighbours[i]);
                    while (anchored.MoveNext(out var ent))
                    {
                        if (ent is not { } other || !members.Contains(other))
                            continue;

                        flags |= 1 << i;

                        // Radiator-radiator and radiator-heat sink joints carry a heat pipe.
                        var otherKind = CompOrNull<KamertonReactorPartComponent>(other)?.Kind;
                        if (i < 4 && kind is { } a && otherKind is { } b && IsHeatPart(a) && IsHeatPart(b)
                            && (a == KamertonPartKind.Radiator || b == KamertonPartKind.Radiator))
                            heatFlags |= 1 << i;
                        break;
                    }
                }
            }

            _appearance.SetData(uid, KamertonReactorVisuals.Links, flags);
            _appearance.SetData(uid, KamertonReactorVisuals.HeatLinks, heatFlags);
        }
    }

    private void Claim(Entity<KamertonReactorCoreComponent> core, EntityUid uid, KamertonReactorPartComponent part)
    {
        part.Core = core;
        core.Comp.Parts.Add(uid);
    }

    public bool IsAssembled(KamertonReactorCoreComponent core)
    {
        return !core.Slave && !core.Oversized && core.Synchronizers.Count > 0;
    }

    public bool RingComplete(KamertonReactorCoreComponent core)
    {
        return core.RingMissing <= 0;
    }

    #endregion

    #region Cells

    /// <summary>Charged cells in an array, in slot order.</summary>
    public List<(string Slot, Entity<KamertonCellComponent> Cell)> GetCells(Entity<KamertonFuelArrayComponent> array, bool charged)
    {
        var result = new List<(string, Entity<KamertonCellComponent>)>();
        for (var i = 1; i <= array.Comp.Slots; i++)
        {
            var slot = SlotId(i);
            if (_slots.GetItemOrNull(array, slot) is not { } item || !TryComp<KamertonCellComponent>(item, out var cell))
                continue;

            if (cell.Charged == charged && cell.Kind == array.Comp.Kind)
                result.Add((slot, (item, cell)));
        }

        return result;
    }

    public static string SlotId(int i) => $"cell_{i}";

    private void OnArrayStartup(Entity<KamertonFuelArrayComponent> ent, ref ComponentStartup args)
    {
        UpdateArrayVisuals(ent);
    }

    private void UpdateArrayVisuals(Entity<KamertonFuelArrayComponent> ent)
    {
        var count = 0;
        for (var i = 1; i <= ent.Comp.Slots; i++)
        {
            if (_slots.GetItemOrNull(ent, SlotId(i)) is { } item && TryComp<KamertonCellComponent>(item, out var cell) && cell.Charged)
                count++;
        }

        _appearance.SetData(ent, KamertonReactorVisuals.Cells, Math.Clamp(count, 0, 4));
    }

    /// <summary>
    /// Works out how many pairs every synchronizer drives right now: min(charged stable, charged unstable, max)
    /// over the arrays it claimed.
    /// </summary>
    public int CountPairs(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;
        comp.SyncPairs.Clear();
        var total = 0;

        foreach (var sync in comp.Synchronizers)
        {
            var stable = 0;
            var unstable = 0;
            foreach (var arrayUid in comp.StableArrays.Concat(comp.UnstableArrays))
            {
                if (!TryComp<KamertonFuelArrayComponent>(arrayUid, out var array) || array.Synchronizer != sync)
                    continue;

                var n = GetCells((arrayUid, array), true).Count;
                if (array.Kind == KamertonCellKind.Stable)
                    stable += n;
                else
                    unstable += n;
            }

            var pairs = Math.Min(Math.Min(stable, unstable), comp.MaxPairsPerSynchronizer);
            comp.SyncPairs[sync] = pairs;
            total += pairs;
        }

        return total;
    }

    /// <summary>Drains the cells in active pairs by <paramref name="dt"/> seconds; spent cells become empty.</summary>
    private void DrainCells(Entity<KamertonReactorCoreComponent> core, float dt)
    {
        var comp = core.Comp;
        foreach (var (sync, pairs) in comp.SyncPairs)
        {
            if (pairs <= 0)
                continue;

            DrainKind(comp.StableArrays, sync, pairs, dt);
            DrainKind(comp.UnstableArrays, sync, pairs, dt);
        }
    }

    private void DrainKind(List<EntityUid> arrays, EntityUid sync, int pairs, float dt)
    {
        var left = pairs;
        foreach (var arrayUid in arrays)
        {
            if (left <= 0)
                return;

            if (!TryComp<KamertonFuelArrayComponent>(arrayUid, out var array) || array.Synchronizer != sync)
                continue;

            foreach (var (slot, cell) in GetCells((arrayUid, array), true))
            {
                if (left <= 0)
                    return;

                left--;
                if (cell.Comp.Remaining < 0f)
                    cell.Comp.Remaining = array.CellLifetime;

                cell.Comp.Remaining -= dt;
                if (cell.Comp.Remaining <= 0f)
                    _cells.SwapInSlot(arrayUid, slot, cell);
            }
        }
    }

    #endregion

    #region Step

    /// <summary>One reactor tick of <paramref name="dt"/> seconds. Public for tests.</summary>
    public void Step(Entity<KamertonReactorCoreComponent> core, float dt)
    {
        var comp = core.Comp;
        if (comp.Collapsed)
            return;

        Scan(core);

        if (comp.Working && !IsAssembled(comp))
            comp.Working = false;

        var pairs = comp.Working ? CountPairs(core) : 0;
        if (!comp.Working)
            comp.SyncPairs.Clear();

        comp.ActivePairs = pairs;
        comp.TotalPower = pairs * comp.PowerPerPair;

        if (pairs > 0)
            DrainCells(core, dt);

        // Heat.
        comp.Heat += pairs * comp.HeatPerPair * dt;
        CoolHeatSinks(core, dt);
        if (!comp.Working)
            comp.Heat = MathF.Max(0f, comp.Heat - comp.PassiveCooling * dt);
        comp.Heat = MathF.Max(0f, comp.Heat);
        UpdateMeltdown(core);

        // Power.
        if (comp.PowerUnit is { } unit && TryComp<PowerSupplierComponent>(unit, out var supplier))
        {
            supplier.MaxSupply = comp.TotalPower;
            SetOn(unit, comp.TotalPower > 0f);
        }

        UpdateVisuals(core);

        if (comp.Working && pairs > 0 && !RingComplete(comp))
            Leak(core, dt);

        if (comp.Heat >= GetMaxHeat(comp))
        {
            Collapse(core);
            return;
        }

        UpdateUi(core);
    }

    /// <summary>Sets the core heat directly (admin tools and tests); the next step reacts to it.</summary>
    public void SetHeat(Entity<KamertonReactorCoreComponent> core, float heat)
    {
        core.Comp.Heat = MathF.Max(0f, heat);
        UpdateMeltdown(core);
    }

    /// <summary>Heat limit: 500 + 250 per radiator of the reactor. Reaching it collapses the reactor.</summary>
    public float GetMaxHeat(KamertonReactorCoreComponent comp)
    {
        return comp.BaseMaxHeat + comp.MaxHeatPerRadiator * comp.Radiators.Count;
    }

    /// <summary>Heat where meltdown starts.</summary>
    public float GetMeltdownHeat(KamertonReactorCoreComponent comp)
    {
        return GetMaxHeat(comp) * comp.MeltdownFraction;
    }

    /// <summary>Core temperature the heat stands for: one heat unit is one kelvin above room temperature.</summary>
    public static float CoreTemperature(KamertonReactorCoreComponent comp)
    {
        return comp.AmbientTemperature + comp.Heat;
    }

    private void UpdateMeltdown(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;
        var meltdown = comp.Heat >= GetMeltdownHeat(comp);
        if (meltdown == comp.Meltdown)
            return;

        comp.Meltdown = meltdown;
        if (meltdown)
            _popup.PopupEntity(Loc.GetString("kamerton-reactor-popup-meltdown"), core, PopupType.LargeCaution);
    }

    /// <summary>Heat moving from the core into gas, never past equilibrium. Returns joules.</summary>
    private float Exchange(KamertonReactorCoreComponent comp, GasMixture gas, float fraction)
    {
        var gasCap = _atmos.GetHeatCapacity(gas, true);
        if (gasCap <= 0f)
            return 0f;

        var coreCap = comp.JoulesPerHeat;
        var gap = CoreTemperature(comp) - gas.Temperature;
        var equilibrium = gap * gasCap * coreCap / (gasCap + coreCap);
        var q = equilibrium * Math.Clamp(fraction, 0f, 1f);

        // A cold core does not soak heat back out of hot gas below room temperature.
        if (q < 0f)
            q = 0f;

        q = MathF.Min(q, comp.Heat * coreCap);
        gas.Temperature = MathF.Max(Atmospherics.TCMB, gas.Temperature + q / gasCap);
        comp.Heat -= q / coreCap;
        return q;
    }

    private void CoolHeatSinks(Entity<KamertonReactorCoreComponent> core, float dt)
    {
        foreach (var uid in core.Comp.HeatSinks)
        {
            if (!TryComp<KamertonHeatSinkComponent>(uid, out var sink))
                continue;

            sink.LastFlow = 0f;
            sink.LastHeatRemoved = 0f;

            if (!_nodes.TryGetNode(uid, sink.PipeName, out PipeNode? pipe))
            {
                SetOn(uid, false);
                continue;
            }

            sink.LastInletTemperature = pipe.Air.Temperature;
            sink.LastOutletTemperature = pipe.Air.Temperature;

            // No radiator line feeds this heat sink: it is just a pipe.
            if (!core.Comp.LinkedHeatSinks.Contains(uid))
            {
                SetOn(uid, false);
                continue;
            }

            var removed = pipe.Air.RemoveVolume(sink.TransferRate * dt);
            if (removed.TotalMoles <= 0f)
            {
                SetOn(uid, false);
                continue;
            }

            var q = Exchange(core.Comp, removed, sink.Efficiency);
            sink.LastFlow = removed.TotalMoles / MathF.Max(dt, 0.001f);
            sink.LastHeatRemoved = q / MathF.Max(dt, 0.001f);
            sink.LastOutletTemperature = removed.Temperature;
            _atmos.Merge(pipe.Air, removed);
            SetOn(uid, q > 0f);
        }
    }

    #endregion

    #region Control

    /// <summary>Starts the reactor. Fails while collapsed, SCRAM-locked or not assembled.</summary>
    public bool TryStart(Entity<KamertonReactorCoreComponent> core, out string? reason)
    {
        reason = null;
        var comp = core.Comp;
        if (comp.Collapsed)
        {
            reason = "kamerton-reactor-popup-collapsed";
            return false;
        }

        if (comp.ScramCooldown > 0f)
        {
            reason = "kamerton-reactor-popup-scram-locked";
            return false;
        }

        Scan(core);
        if (!IsAssembled(comp))
        {
            reason = "kamerton-reactor-popup-not-assembled";
            return false;
        }

        comp.Working = true;
        UpdateVisuals(core);
        return true;
    }

    /// <summary>Stops the reactor unless it is in meltdown. Returns false (with a reason) when refused.</summary>
    public bool TryStop(Entity<KamertonReactorCoreComponent> core, out string? reason)
    {
        reason = null;
        if (core.Comp.Meltdown)
        {
            reason = "kamerton-reactor-popup-meltdown-locked";
            return false;
        }

        Stop(core);
        return true;
    }

    /// <summary>SCRAM unless the reactor is in meltdown.</summary>
    public bool TryScram(Entity<KamertonReactorCoreComponent> core, out string? reason)
    {
        reason = null;
        if (core.Comp.Meltdown)
        {
            reason = "kamerton-reactor-popup-meltdown-locked";
            return false;
        }

        Scram(core);
        return true;
    }

    public void Stop(Entity<KamertonReactorCoreComponent> core)
    {
        core.Comp.Working = false;
        core.Comp.ActivePairs = 0;
        core.Comp.TotalPower = 0f;
        if (core.Comp.PowerUnit is { } unit && TryComp<PowerSupplierComponent>(unit, out var supplier))
            supplier.MaxSupply = 0f;
        UpdateVisuals(core);
    }

    /// <summary>Emergency stop: stop + start lockout.</summary>
    public void Scram(Entity<KamertonReactorCoreComponent> core)
    {
        Stop(core);
        core.Comp.ScramCooldown = core.Comp.ScramLockout;
        _popup.PopupEntity(Loc.GetString("kamerton-reactor-popup-scram"), core, PopupType.LargeCaution);
    }

    public Entity<KamertonReactorCoreComponent>? ConsoleCore(EntityUid console)
    {
        if (TryComp<KamertonReactorPartComponent>(console, out var part)
            && part.Core is { } c
            && TryComp<KamertonReactorCoreComponent>(c, out var core)
            && core.Console == console)
            return (c, core);

        return null;
    }

    private void OnStartMessage(Entity<KamertonControlModuleComponent> ent, ref KamertonReactorStartMessage args)
    {
        if (ConsoleCore(ent) is not { } core)
            return;

        if (!TryStart(core, out var reason) && reason != null)
            _popup.PopupEntity(Loc.GetString(reason), ent, args.Actor);

        UpdateUi(core);
    }

    private void OnStopMessage(Entity<KamertonControlModuleComponent> ent, ref KamertonReactorStopMessage args)
    {
        if (ConsoleCore(ent) is not { } core)
            return;

        if (!TryStop(core, out var reason) && reason != null)
            _popup.PopupEntity(Loc.GetString(reason), ent, args.Actor, PopupType.MediumCaution);

        UpdateUi(core);
    }

    private void OnScramMessage(Entity<KamertonControlModuleComponent> ent, ref KamertonReactorScramMessage args)
    {
        if (ConsoleCore(ent) is not { } core)
            return;

        if (!TryScram(core, out var reason) && reason != null)
            _popup.PopupEntity(Loc.GetString(reason), ent, args.Actor, PopupType.MediumCaution);

        UpdateUi(core);
    }

    private void OnUiOpened(Entity<KamertonControlModuleComponent> ent, ref BoundUIOpenedEvent args)
    {
        if (ConsoleCore(ent) is { } core)
        {
            UpdateUi(core);
            return;
        }

        _ui.SetUiState(ent.Owner, KamertonReactorUiKey.Key, EmptyState());
    }

    private static KamertonReactorBoundUserInterfaceState EmptyState()
    {
        return new KamertonReactorBoundUserInterfaceState(false, false, false, 0f, 0f, 0f, 0f, 0f, 0, 0, false, 0,
            false, 0f, new List<KamertonArrayInfo>(), new List<KamertonHeatSinkInfo>(), 0);
    }

    public KamertonReactorBoundUserInterfaceState BuildState(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;
        var arrays = new List<KamertonArrayInfo>();
        foreach (var uid in comp.StableArrays.Concat(comp.UnstableArrays))
        {
            if (!TryComp<KamertonFuelArrayComponent>(uid, out var array))
                continue;

            var charged = GetCells((uid, array), true);
            var empty = GetCells((uid, array), false).Count;
            var min = -1f;
            foreach (var (_, cell) in charged)
            {
                var left = cell.Comp.Remaining < 0f ? array.CellLifetime : cell.Comp.Remaining;
                min = min < 0f ? left : MathF.Min(min, left);
            }

            arrays.Add(new KamertonArrayInfo(array.Kind == KamertonCellKind.Stable, charged.Count, empty, array.Slots, min));
        }

        var sinks = new List<KamertonHeatSinkInfo>();
        foreach (var uid in comp.HeatSinks)
        {
            if (TryComp<KamertonHeatSinkComponent>(uid, out var sink))
                sinks.Add(new KamertonHeatSinkInfo(sink.LastFlow, sink.LastInletTemperature, sink.LastOutletTemperature, sink.LastHeatRemoved));
        }

        var supplied = 0f;
        if (comp.PowerUnit is { } unit && TryComp<PowerSupplierComponent>(unit, out var supplier))
            supplied = supplier.CurrentSupply;

        var max = comp.Synchronizers.Count * comp.MaxPairsPerSynchronizer * comp.PowerPerPair;

        var state = new KamertonReactorBoundUserInterfaceState(true, IsAssembled(comp), comp.Working, comp.ScramCooldown,
            comp.Heat, GetMaxHeat(comp), comp.TotalPower, max, comp.ActivePairs, comp.Synchronizers.Count,
            RingComplete(comp), comp.RingMissing, comp.PowerUnit != null, supplied, arrays, sinks, comp.LinkedRadiators.Count);
        state.Drone = _drone.BuildInfo(core);
        state.MeltdownHeat = GetMeltdownHeat(comp);
        state.Meltdown = comp.Meltdown;
        return state;
    }

    public void UpdateUi(Entity<KamertonReactorCoreComponent> core)
    {
        if (core.Comp.Console is not { } console || !_ui.IsUiOpen(console, KamertonReactorUiKey.Key))
            return;

        _ui.SetUiState(console, KamertonReactorUiKey.Key, BuildState(core));
    }

    #endregion

    #region Leak and collapse

    /// <summary>Broken ring: weak redspace hits every unprotected living mob within LeakRange.</summary>
    private void Leak(Entity<KamertonReactorCoreComponent> core, float dt)
    {
        var comp = core.Comp;
        var coords = _transform.GetMapCoordinates(core);
        _suppression.GetSuppressors(coords.MapId, 0f, _suppressors);

        comp.LeakPopupAccumulator += dt;
        var popup = comp.LeakPopupAccumulator >= 10f;
        if (popup)
            comp.LeakPopupAccumulator = 0f;

        _mobs.Clear();
        _lookup.GetEntitiesInRange(coords, comp.LeakRange, _mobs);
        foreach (var mob in _mobs)
        {
            if (_mobState.IsDead(mob, mob.Comp) || _redspace.IsImmune(mob))
                continue;

            if (RedspaceSuppressionSystem.IsSuppressed(_suppressors, _transform.GetWorldPosition(mob), out _))
                continue;

            _damage.TryChangeDamage(mob, comp.LeakDamage * dt, ignoreResistances: true, interruptsDoAfters: false, origin: core);
            if (popup)
                _popup.PopupEntity(Loc.GetString("kamerton-reactor-popup-leak"), mob, mob, PopupType.SmallCaution);
        }
    }

    /// <summary>
    /// At the heat limit: a tesla ball at the core, small singularities around it,
    /// the whole map turns into normal redspace and the reactor is gone.
    /// </summary>
    public void Collapse(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;
        if (comp.Collapsed)
            return;

        comp.Collapsed = true;
        comp.Working = false;
        comp.TotalPower = 0f;
        comp.ActivePairs = 0;

        var coords = _transform.GetMapCoordinates(core);
        _popup.PopupEntity(Loc.GetString("kamerton-reactor-popup-collapse"), core, PopupType.LargeCaution);

        Spawn(comp.CollapseTesla, coords);
        for (var i = 0; i < comp.CollapseSingularities; i++)
        {
            var dist = _random.NextFloat(10f, comp.CollapseRange);
            var offset = _random.NextAngle().ToVec() * dist;
            Spawn(comp.CollapseSingularity, new MapCoordinates(coords.Position + offset, coords.MapId));
        }

        if (_map.TryGetMap(coords.MapId, out var mapUid))
            _redspace.SetRedspace(mapUid.Value, comp.CollapsePreset);

        if (comp.PowerUnit is { } unit && TryComp<PowerSupplierComponent>(unit, out var supplier))
            supplier.MaxSupply = 0f;

        foreach (var part in comp.Parts.ToArray())
        {
            if (!TerminatingOrDeleted(part))
                QueueDel(part);
        }

        QueueDel(core);
    }

    #endregion

    #region Visuals and examine

    private void SetOn(EntityUid uid, bool on)
    {
        _appearance.SetData(uid, KamertonReactorVisuals.On, on);
        if (_light.TryGetLight(uid, out var light))
            _light.SetEnabled(uid, on, light);
    }

    private void UpdateVisuals(Entity<KamertonReactorCoreComponent> core)
    {
        var comp = core.Comp;
        _appearance.SetData(core, KamertonReactorVisuals.On, comp.Working);
        var share = comp.Heat / MathF.Max(1f, GetMaxHeat(comp));
        var heat = share < 0.2f ? 0
            : share < 0.45f ? 1
            : share < comp.MeltdownFraction ? 2
            : 3;
        _appearance.SetData(core, KamertonReactorVisuals.Heat, heat);
        if (_light.TryGetLight(core, out var light))
            _light.SetEnabled(core, comp.Working || heat > 0, light);

        foreach (var sync in comp.Synchronizers)
            SetOn(sync, comp.SyncPairs.TryGetValue(sync, out var p) && p > 0);

        foreach (var rad in comp.Radiators)
            _appearance.SetData(rad, KamertonReactorVisuals.Heat, heat);

        var ringOn = comp.Working && RingComplete(comp);
        foreach (var ring in comp.RingParts)
        {
            if (TryComp<KamertonReactorPartComponent>(ring, out var part) && part.Kind == KamertonPartKind.Suppressor)
                SetOn(ring, ringOn);
        }
    }

    private void OnCoreExamined(Entity<KamertonReactorCoreComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var comp = ent.Comp;
        using (args.PushGroup(nameof(KamertonReactorCoreComponent)))
        {
            args.PushMarkup(Loc.GetString("kamerton-reactor-examine-heat", ("heat", MathF.Round(comp.Heat)), ("max", GetMaxHeat(comp))));
            if (comp.Meltdown)
                args.PushMarkup(Loc.GetString("kamerton-reactor-examine-meltdown"));
            args.PushMarkup(Loc.GetString("kamerton-reactor-examine-power", ("power", MathF.Round(comp.TotalPower / 1000f, 1)), ("pairs", comp.ActivePairs)));
            args.PushMarkup(Loc.GetString(comp.Working ? "kamerton-reactor-examine-running" : "kamerton-reactor-examine-stopped"));
            args.PushMarkup(RingComplete(comp)
                ? Loc.GetString("kamerton-reactor-examine-ring-ok")
                : Loc.GetString("kamerton-reactor-examine-ring-broken", ("missing", comp.RingMissing)));
        }
    }

    private void OnPartExamined(Entity<KamertonReactorPartComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange || ent.Comp.Kind == KamertonPartKind.Core)
            return;

        args.PushMarkup(Loc.GetString(ent.Comp.Core != null ? "kamerton-reactor-examine-part-linked" : "kamerton-reactor-examine-part-unlinked"));
    }

    private void OnCoreShutdown(Entity<KamertonReactorCoreComponent> ent, ref ComponentShutdown args)
    {
        foreach (var old in ent.Comp.Parts)
        {
            if (TryComp<KamertonReactorPartComponent>(old, out var part) && part.Core == ent.Owner)
                part.Core = null;
        }
    }

    #endregion
}

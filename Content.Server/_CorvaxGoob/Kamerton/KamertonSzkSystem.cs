using System.Linq;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Server.Construction;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton fuel storage and the SZK («СЗК», Kamerton refuelling system). The arrays sit walled in inside the
/// reactor, so only the SZK can reach them: once the SZK block is anchored in the shell of an assembled
/// reactor it lays one rail tile over every reactor tile and puts a carriage on the rails. The carriage moves
/// one tile per <see cref="KamertonSzkComponent.StepInterval"/>, picks charged cells up at the fuel storage
/// and puts them into free array slots, and takes spent cells out of the arrays. Spent cells leave through
/// the storage hatch onto the free tile next to the storage, ready for the converter. Removing or unanchoring
/// the SZK takes the lattice and the carriage away.
/// </summary>
public sealed class KamertonSzkSystem : EntitySystem
{
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly KamertonReactorSystem _reactor = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private static readonly Vector2i[] Cardinals = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0) };

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<KamertonFuelStorageComponent, ComponentStartup>(OnStorageStartup);
        SubscribeLocalEvent<KamertonFuelStorageComponent, InteractUsingEvent>(OnStorageInteractUsing,
            after: new[] { typeof(FlatpackSystem) });
        SubscribeLocalEvent<KamertonFuelStorageComponent, EntInsertedIntoContainerMessage>((uid, comp, _) => UpdateStorageVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonFuelStorageComponent, EntRemovedFromContainerMessage>((uid, comp, _) => UpdateStorageVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonFuelStorageComponent, ExaminedEvent>(OnStorageExamined);

        SubscribeLocalEvent<KamertonSzkComponent, ComponentStartup>(OnSzkStartup);
        SubscribeLocalEvent<KamertonSzkComponent, ComponentShutdown>(OnSzkShutdown);
        SubscribeLocalEvent<KamertonSzkComponent, ExaminedEvent>(OnSzkExamined);
        SubscribeLocalEvent<KamertonControlModuleComponent, KamertonSzkCommandMessage>(OnCommand);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<KamertonSzkComponent>();
        while (query.MoveNext(out var uid, out var szk))
        {
            Think((uid, szk), frameTime);
        }
    }

    #region Storage

    private void OnStorageStartup(Entity<KamertonFuelStorageComponent> ent, ref ComponentStartup args)
    {
        _containers.EnsureContainer<Container>(ent, ent.Comp.StableContainer);
        _containers.EnsureContainer<Container>(ent, ent.Comp.UnstableContainer);
        UpdateStorageVisuals(ent);
    }

    public BaseContainer GetStorage(Entity<KamertonFuelStorageComponent> storage, KamertonCellKind kind)
    {
        return _containers.EnsureContainer<Container>(storage,
            kind == KamertonCellKind.Stable ? storage.Comp.StableContainer : storage.Comp.UnstableContainer);
    }

    public int CountStored(Entity<KamertonFuelStorageComponent> storage, KamertonCellKind kind)
    {
        return GetStorage(storage, kind).ContainedEntities.Count;
    }

    /// <summary>Puts a charged cell into the storage; false when it is empty, the wrong thing or the storage is full.</summary>
    public bool TryStore(Entity<KamertonFuelStorageComponent> storage, EntityUid cell)
    {
        if (!TryComp<KamertonCellComponent>(cell, out var comp) || !comp.Charged)
            return false;

        var container = GetStorage(storage, comp.Kind);
        if (container.ContainedEntities.Count >= storage.Comp.Capacity)
            return false;

        return _containers.Insert(cell, container);
    }

    private void OnStorageInteractUsing(Entity<KamertonFuelStorageComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<KamertonCellComponent>(args.Used, out var cell))
            return;

        args.Handled = true;
        if (!cell.Charged)
        {
            _popup.PopupEntity(Loc.GetString("kamerton-storage-empty-cell"), ent, args.User);
            return;
        }

        if (!TryStore(ent, args.Used))
        {
            _popup.PopupEntity(Loc.GetString("kamerton-storage-full"), ent, args.User);
            return;
        }

        _popup.PopupEntity(Loc.GetString("kamerton-storage-loaded",
            ("stable", CountStored(ent, KamertonCellKind.Stable)),
            ("unstable", CountStored(ent, KamertonCellKind.Unstable)),
            ("capacity", ent.Comp.Capacity)), ent, args.User);
    }

    private void UpdateStorageVisuals(Entity<KamertonFuelStorageComponent> ent)
    {
        if (!_containers.TryGetContainer(ent, ent.Comp.StableContainer, out var stable)
            || !_containers.TryGetContainer(ent, ent.Comp.UnstableContainer, out var unstable))
            return;

        _appearance.SetData(ent, KamertonReactorVisuals.StorageStable, (stable.ContainedEntities.Count + 1) / 2);
        _appearance.SetData(ent, KamertonReactorVisuals.StorageUnstable, (unstable.ContainedEntities.Count + 1) / 2);
    }

    private void OnStorageExamined(Entity<KamertonFuelStorageComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("kamerton-storage-examine",
            ("stable", CountStored(ent, KamertonCellKind.Stable)),
            ("unstable", CountStored(ent, KamertonCellKind.Unstable)),
            ("capacity", ent.Comp.Capacity)));
    }

    #endregion

    #region Lattice

    private void OnSzkStartup(Entity<KamertonSzkComponent> ent, ref ComponentStartup args)
    {
        _containers.EnsureContainer<ContainerSlot>(ent, ent.Comp.CarriageContainer);
    }

    private void OnSzkShutdown(Entity<KamertonSzkComponent> ent, ref ComponentShutdown args)
    {
        Retract(ent);
    }

    /// <summary>The reactor this SZK serves: it has to be anchored and be the SZK its core found in the shell.</summary>
    public Entity<KamertonReactorCoreComponent>? ServedCore(Entity<KamertonSzkComponent> szk)
    {
        if (!Transform(szk).Anchored || !TryComp<KamertonReactorPartComponent>(szk, out var part)
            || part.Core is not { } coreUid || !TryComp<KamertonReactorCoreComponent>(coreUid, out var core)
            || core.Szk != szk.Owner || core.Footprint.Count == 0)
            return null;

        return (coreUid, core);
    }

    public bool Deployed(KamertonSzkComponent szk)
    {
        return szk.Rails.Count > 0;
    }

    /// <summary>Lays rails over the reactor footprint (and drops rails off tiles that left it).</summary>
    private void Deploy(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core)
    {
        var comp = szk.Comp;
        var grid = Transform(szk).GridUid;
        if (grid is not { } gridUid || !TryComp<MapGridComponent>(gridUid, out var gridComp))
            return;

        if (comp.Grid != gridUid)
            Retract(szk);

        comp.Grid = gridUid;
        var footprint = core.Comp.Footprint;
        if (comp.Rails.Count == footprint.Count && footprint.Keys.All(comp.Rails.ContainsKey))
        {
            EnsureCarriage(szk, core, gridUid, gridComp);
            return;
        }

        foreach (var (tile, rail) in comp.Rails.ToList())
        {
            if (footprint.ContainsKey(tile))
                continue;

            QueueDel(rail);
            comp.Rails.Remove(tile);
        }

        foreach (var tile in footprint.Keys)
        {
            if (comp.Rails.ContainsKey(tile))
                continue;

            var rail = Spawn(comp.RailPrototype, _map.GridTileToLocal(gridUid, gridComp, tile));
            EnsureComp<KamertonSzkRailComponent>(rail).Szk = szk;
            comp.Rails[tile] = rail;
        }

        foreach (var (tile, rail) in comp.Rails)
        {
            var flags = 0;
            for (var i = 0; i < Cardinals.Length; i++)
            {
                if (comp.Rails.ContainsKey(tile + Cardinals[i]))
                    flags |= 1 << i;
            }

            _appearance.SetData(rail, KamertonReactorVisuals.Rails, flags);
        }

        EnsureCarriage(szk, core, gridUid, gridComp);
    }

    private void EnsureCarriage(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core, EntityUid grid, MapGridComponent gridComp)
    {
        var comp = szk.Comp;
        if (comp.Carriage is { } existing && !TerminatingOrDeleted(existing) && comp.Rails.ContainsKey(comp.CarriageTile))
            return;

        if (comp.Carriage is { } old)
            QueueDel(old);

        comp.CarriageTile = ParkTile(szk, core);
        comp.Path.Clear();
        var carriage = Spawn(comp.CarriagePrototype, _map.GridTileToLocal(grid, gridComp, comp.CarriageTile));
        EnsureComp<KamertonSzkCarriageComponent>(carriage).Szk = szk;
        comp.Carriage = carriage;
        UpdateCarriageLook(szk);
    }

    /// <summary>Takes the rails and the carriage away; a carried cell drops next to the SZK block.</summary>
    public void Retract(Entity<KamertonSzkComponent> szk)
    {
        var comp = szk.Comp;
        foreach (var rail in comp.Rails.Values)
        {
            QueueDel(rail);
        }

        comp.Rails.Clear();
        if (comp.Carriage is { } carriage)
            QueueDel(carriage);

        comp.Carriage = null;
        comp.Path.Clear();
        comp.Grid = null;
        comp.State = KamertonSzkState.Idle;
        comp.Task = null;

        if (Held(szk) is { } held && _containers.TryGetContainer(szk, comp.CarriageContainer, out var container))
        {
            _containers.Remove(held, container);
            if (!TerminatingOrDeleted(szk))
                _transform.SetCoordinates(held, Transform(szk).Coordinates);
            _transform.AttachToGridOrMap(held);
        }
    }

    /// <summary>Where the carriage waits: on the fuel storage tile, or on the SZK block without a storage.</summary>
    private Vector2i ParkTile(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core)
    {
        if (core.Comp.Storage is { } storage && TileOf(core.Comp, storage) is { } st)
            return st;

        return TileOf(core.Comp, szk) ?? core.Comp.Footprint.Keys.First();
    }

    private static Vector2i? TileOf(KamertonReactorCoreComponent core, EntityUid uid)
    {
        foreach (var (tile, part) in core.Footprint)
        {
            if (part == uid)
                return tile;
        }

        return null;
    }

    #endregion

    #region Carriage

    private EntityUid? Held(Entity<KamertonSzkComponent> szk)
    {
        return _containers.TryGetContainer(szk, szk.Comp.CarriageContainer, out var container)
            ? container.ContainedEntities.FirstOrDefault() is { Valid: true } held ? held : null
            : null;
    }

    private KamertonSzkCarriageLook Look(Entity<KamertonSzkComponent> szk)
    {
        if (Held(szk) is not { } held || !TryComp<KamertonCellComponent>(held, out var cell))
            return KamertonSzkCarriageLook.Empty;

        if (!cell.Charged)
            return KamertonSzkCarriageLook.Spent;

        return cell.Kind == KamertonCellKind.Stable ? KamertonSzkCarriageLook.Stable : KamertonSzkCarriageLook.Unstable;
    }

    private void UpdateCarriageLook(Entity<KamertonSzkComponent> szk)
    {
        if (szk.Comp.Carriage is { } carriage)
            _appearance.SetData(carriage, KamertonReactorVisuals.Carriage, (int) Look(szk));
    }

    /// <summary>One tick: keep the lattice in shape, then one carriage move or action per interval.</summary>
    public void Think(Entity<KamertonSzkComponent> szk, float dt)
    {
        var comp = szk.Comp;
        if (ServedCore(szk) is not { } core)
        {
            if (Deployed(comp) || comp.Carriage != null)
                Retract(szk);
            return;
        }

        Deploy(szk, core);
        if (comp.Carriage == null)
            return;

        comp.Accumulator += dt;
        var interval = comp.Path.Count > 0 ? comp.StepInterval : comp.ActionInterval;
        if (comp.Accumulator < interval)
            return;

        comp.Accumulator = 0f;

        // Moving along the rails.
        if (comp.Path.Count > 0)
        {
            var next = comp.Path[0];
            comp.Path.RemoveAt(0);
            if (!comp.Rails.TryGetValue(next, out var rail))
            {
                comp.Path.Clear();
                return;
            }

            comp.CarriageTile = next;
            _transform.SetCoordinates(comp.Carriage.Value, Transform(rail).Coordinates);
            return;
        }

        if (Goal(szk, core) is { } goal && goal != comp.CarriageTile)
        {
            PathTo(comp, goal);
            if (comp.Path.Count > 0)
                return;
        }

        Act(szk, core);
    }

    /// <summary>Tile the carriage should be on for its next action, or null to act where it is.</summary>
    private Vector2i? Goal(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core)
    {
        var comp = szk.Comp;
        var park = ParkTile(szk, core);
        var held = Held(szk);
        var look = Look(szk);

        switch (comp.State)
        {
            case KamertonSzkState.Working when comp.Task == KamertonSzkCommand.Load:
                if (held == null)
                    return park;
                if (look is KamertonSzkCarriageLook.Stable or KamertonSzkCarriageLook.Unstable)
                {
                    var kind = look == KamertonSzkCarriageLook.Stable ? KamertonCellKind.Stable : KamertonCellKind.Unstable;
                    return FindArray(core, a => a.Comp.Kind == kind && FreeSlot(a) != null) ?? park;
                }

                return park;
            case KamertonSzkState.Working when comp.Task == KamertonSzkCommand.Unload:
                if (held == null)
                    return FindArray(core, a => SpentSlot(a) != null) ?? park;
                return park;
            default:
                return park;
        }
    }

    private void Act(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core)
    {
        var comp = szk.Comp;
        var park = ParkTile(szk, core);
        var held = Held(szk);
        var here = comp.CarriageTile;
        var array = core.Comp.Footprint.TryGetValue(here, out var partUid) && TryComp<KamertonFuelArrayComponent>(partUid, out var a)
            ? (Entity<KamertonFuelArrayComponent>?) (partUid, a)
            : null;

        switch (comp.State)
        {
            case KamertonSzkState.Working when comp.Task == KamertonSzkCommand.Load:
                if (held == null)
                {
                    if (here == park && Storage(core.Comp) is { } storage && TakeForLoad(szk, core, storage))
                        return;
                    FinishJob(szk, core);
                    return;
                }

                if (Look(szk) == KamertonSzkCarriageLook.Spent)
                {
                    if (here == park)
                        Deliver(szk, core, held.Value);
                    return;
                }

                if (array is { } target && target.Comp.Kind == Comp<KamertonCellComponent>(held.Value).Kind
                    && FreeSlot(target) is { } slot)
                {
                    Insert(szk, target, slot, held.Value);
                    return;
                }

                // Nowhere to put it any more: back to the storage with it.
                if (here == park)
                {
                    Deliver(szk, core, held.Value);
                    FinishJob(szk, core);
                }

                return;

            case KamertonSzkState.Working when comp.Task == KamertonSzkCommand.Unload:
                if (held == null)
                {
                    if (array is { } source && SpentSlot(source) is { } spentSlot)
                    {
                        Extract(szk, source, spentSlot);
                        return;
                    }

                    if (FindArray(core, x => SpentSlot(x) != null) == null)
                        FinishJob(szk, core);
                    return;
                }

                if (here == park)
                    Deliver(szk, core, held.Value);
                return;

            case KamertonSzkState.Parking:
                if (held is { } cargo && here == park)
                    Deliver(szk, core, cargo);
                if (Held(szk) == null && here == park)
                    comp.State = KamertonSzkState.Idle;
                return;

            default:
                if (comp.Auto && PickTask(core) is { } task)
                    Command(szk, task);
                return;
        }
    }

    private void FinishJob(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core)
    {
        var comp = szk.Comp;
        comp.Task = comp.Auto ? PickTask(core) : null;
        comp.State = comp.Task == null ? KamertonSzkState.Parking : KamertonSzkState.Working;
    }

    /// <summary>Grabs a charged cell at the storage of a kind some array has room for.</summary>
    private bool TakeForLoad(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core, Entity<KamertonFuelStorageComponent> storage)
    {
        foreach (var kind in new[] { KamertonCellKind.Stable, KamertonCellKind.Unstable })
        {
            var stored = GetStorage(storage, kind);
            if (stored.ContainedEntities.Count == 0 || FindArray(core, a => a.Comp.Kind == kind && FreeSlot(a) != null) == null)
                continue;

            var cell = stored.ContainedEntities[0];
            if (!_containers.TryGetContainer(szk, szk.Comp.CarriageContainer, out var carriage)
                || !_containers.Remove(cell, stored) || !_containers.Insert(cell, carriage))
                return false;

            Effect(szk, szk.Comp.PickupEffect, storage);
            _audio.PlayPvs(szk.Comp.PickupSound, storage);
            UpdateCarriageLook(szk);
            return true;
        }

        return false;
    }

    private void Insert(Entity<KamertonSzkComponent> szk, Entity<KamertonFuelArrayComponent> array, string slot, EntityUid cell)
    {
        if (!_containers.TryGetContainer(szk, szk.Comp.CarriageContainer, out var carriage))
            return;

        _containers.Remove(cell, carriage);
        if (!_slots.TryInsert(array, slot, cell, null, excludeUserAudio: true))
        {
            _containers.Insert(cell, carriage);
            return;
        }

        Effect(szk, szk.Comp.InsertEffect, array);
        _audio.PlayPvs(szk.Comp.InsertSound, array);
        UpdateCarriageLook(szk);
    }

    private void Extract(Entity<KamertonSzkComponent> szk, Entity<KamertonFuelArrayComponent> array, string slot)
    {
        if (!_containers.TryGetContainer(szk, szk.Comp.CarriageContainer, out var carriage)
            || !_slots.TryEject(array, slot, null, out var cell, excludeUserAudio: true))
            return;

        _containers.Insert(cell.Value, carriage);
        Effect(szk, szk.Comp.InsertEffect, array);
        _audio.PlayPvs(szk.Comp.PickupSound, array);
        UpdateCarriageLook(szk);
    }

    /// <summary>
    /// At the storage: a charged cell goes back in (or out of the hatch when the storage is full), a spent cell
    /// always goes out through the hatch onto the free tile next to the storage.
    /// </summary>
    private void Deliver(Entity<KamertonSzkComponent> szk, Entity<KamertonReactorCoreComponent> core, EntityUid cell)
    {
        if (!_containers.TryGetContainer(szk, szk.Comp.CarriageContainer, out var carriage))
            return;

        _containers.Remove(cell, carriage);
        var storage = Storage(core.Comp);
        if (storage is not { } s || !TryStore(s, cell))
        {
            var from = storage?.Owner ?? szk.Owner;
            _transform.SetCoordinates(cell, Hatch(from, core.Comp));
            _transform.AttachToGridOrMap(cell);
        }

        Effect(szk, szk.Comp.PickupEffect, storage?.Owner ?? szk.Owner);
        _audio.PlayPvs(szk.Comp.PickupSound, storage?.Owner ?? szk.Owner);
        UpdateCarriageLook(szk);
    }

    /// <summary>The free tile next to a shell part, outside the reactor.</summary>
    public EntityCoordinates Hatch(EntityUid part, KamertonReactorCoreComponent core)
    {
        var xform = Transform(part);
        if (xform.GridUid is not { } grid || !TryComp<MapGridComponent>(grid, out var gridComp))
            return xform.Coordinates;

        var tile = _map.TileIndicesFor(grid, gridComp, xform.Coordinates);
        foreach (var off in Cardinals)
        {
            if (!core.Footprint.ContainsKey(tile + off))
                return _map.GridTileToLocal(grid, gridComp, tile + off);
        }

        return xform.Coordinates;
    }

    private void Effect(Entity<KamertonSzkComponent> szk, Robust.Shared.Prototypes.EntProtoId proto, EntityUid at)
    {
        Spawn(proto, Transform(at).Coordinates);
    }

    /// <summary>Breadth-first route over the rails (N/S/E/W) to <paramref name="goal"/>.</summary>
    private static void PathTo(KamertonSzkComponent comp, Vector2i goal)
    {
        comp.Path.Clear();
        if (!comp.Rails.ContainsKey(goal))
            return;

        var from = new Dictionary<Vector2i, Vector2i> { [comp.CarriageTile] = comp.CarriageTile };
        var queue = new Queue<Vector2i>();
        queue.Enqueue(comp.CarriageTile);
        while (queue.TryDequeue(out var tile))
        {
            if (tile == goal)
                break;

            foreach (var off in Cardinals)
            {
                var next = tile + off;
                if (!comp.Rails.ContainsKey(next) || from.ContainsKey(next))
                    continue;

                from[next] = tile;
                queue.Enqueue(next);
            }
        }

        if (!from.ContainsKey(goal))
            return;

        for (var t = goal; t != comp.CarriageTile; t = from[t])
        {
            comp.Path.Insert(0, t);
        }
    }

    private Vector2i? FindArray(Entity<KamertonReactorCoreComponent> core, Func<Entity<KamertonFuelArrayComponent>, bool> pred)
    {
        foreach (var array in Arrays(core.Comp))
        {
            if (pred(array) && TileOf(core.Comp, array) is { } tile)
                return tile;
        }

        return null;
    }

    private string? FreeSlot(Entity<KamertonFuelArrayComponent> array)
    {
        for (var i = 1; i <= array.Comp.Slots; i++)
        {
            var slot = KamertonReactorSystem.SlotId(i);
            if (_slots.GetItemOrNull(array, slot) == null)
                return slot;
        }

        return null;
    }

    private string? SpentSlot(Entity<KamertonFuelArrayComponent> array)
    {
        for (var i = 1; i <= array.Comp.Slots; i++)
        {
            var slot = KamertonReactorSystem.SlotId(i);
            if (_slots.GetItemOrNull(array, slot) is { } item && TryComp<KamertonCellComponent>(item, out var cell) && !cell.Charged)
                return slot;
        }

        return null;
    }

    private Entity<KamertonFuelStorageComponent>? Storage(KamertonReactorCoreComponent core)
    {
        return core.Storage is { } uid && TryComp<KamertonFuelStorageComponent>(uid, out var comp) ? (uid, comp) : null;
    }

    private IEnumerable<Entity<KamertonFuelArrayComponent>> Arrays(KamertonReactorCoreComponent core)
    {
        foreach (var uid in core.StableArrays.Concat(core.UnstableArrays))
        {
            if (TryComp<KamertonFuelArrayComponent>(uid, out var a))
                yield return (uid, a);
        }
    }

    /// <summary>The job the storage and arrays call for right now: spent cells out first, then fresh ones in.</summary>
    public KamertonSzkCommand? PickTask(Entity<KamertonReactorCoreComponent> core)
    {
        if (Storage(core.Comp) is not { } storage)
            return null;

        var info = BuildInfo(core.Comp, storage);
        if (info.Spent > 0)
            return KamertonSzkCommand.Unload;

        if (info.FreeStable > 0 && info.StorageStable > 0 || info.FreeUnstable > 0 && info.StorageUnstable > 0)
            return KamertonSzkCommand.Load;

        return null;
    }

    public void Command(Entity<KamertonSzkComponent> szk, KamertonSzkCommand command)
    {
        var comp = szk.Comp;
        switch (command)
        {
            case KamertonSzkCommand.Load:
            case KamertonSzkCommand.Unload:
                comp.State = KamertonSzkState.Working;
                comp.Task = command;
                break;
            case KamertonSzkCommand.Stop:
                comp.State = KamertonSzkState.Parking;
                comp.Task = null;
                comp.Auto = false;
                comp.Path.Clear();
                break;
            case KamertonSzkCommand.ToggleAuto:
                comp.Auto = !comp.Auto;
                break;
        }
    }

    private void OnSzkExamined(Entity<KamertonSzkComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString(Deployed(ent.Comp) ? "kamerton-szk-examine-deployed" : "kamerton-szk-examine-folded",
            ("auto", ent.Comp.Auto ? 1 : 0)));
    }

    #endregion

    #region Console

    public KamertonSzkInfo BuildInfo(KamertonReactorCoreComponent core)
    {
        return BuildInfo(core, Storage(core));
    }

    private KamertonSzkInfo BuildInfo(KamertonReactorCoreComponent core, Entity<KamertonFuelStorageComponent>? storage)
    {
        var info = new KamertonSzkInfo();
        if (core.Szk is { } szkUid && TryComp<KamertonSzkComponent>(szkUid, out var szk))
        {
            info.HasSzk = true;
            info.Deployed = Deployed(szk);
            info.State = szk.State;
            info.Task = szk.Task;
            info.Auto = szk.Auto;
            info.Holding = Look((szkUid, szk));
            var coreTile = TileOf(core, core.Owner) ?? Vector2i.Zero;
            info.CarriageX = szk.CarriageTile.X - coreTile.X;
            info.CarriageY = szk.CarriageTile.Y - coreTile.Y;
        }

        if (storage is { } s)
        {
            info.HasStorage = true;
            info.StorageStable = CountStored(s, KamertonCellKind.Stable);
            info.StorageUnstable = CountStored(s, KamertonCellKind.Unstable);
            info.StorageCapacity = s.Comp.Capacity;
        }

        foreach (var array in Arrays(core))
        {
            for (var i = 1; i <= array.Comp.Slots; i++)
            {
                var item = _slots.GetItemOrNull(array, KamertonReactorSystem.SlotId(i));
                if (item == null)
                {
                    if (array.Comp.Kind == KamertonCellKind.Stable)
                        info.FreeStable++;
                    else
                        info.FreeUnstable++;
                }
                else if (TryComp<KamertonCellComponent>(item, out var cell) && !cell.Charged)
                {
                    info.Spent++;
                }
            }
        }

        return info;
    }

    private void OnCommand(Entity<KamertonControlModuleComponent> ent, ref KamertonSzkCommandMessage args)
    {
        if (_reactor.ConsoleCore(ent) is not { } core)
            return;

        if (core.Comp.Szk is not { } szkUid || !TryComp<KamertonSzkComponent>(szkUid, out var szk))
        {
            _popup.PopupEntity(Loc.GetString("kamerton-szk-missing"), ent, args.Actor);
            return;
        }

        Command((szkUid, szk), args.Command);
        _reactor.UpdateUi(core);
    }

    #endregion
}

using System.Numerics;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Server.Construction;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton fuel storage and the RDZT maintenance drone. The drone never goes inside the reactor: it docks at
/// the fuel storage (a shell part) and moves cells through the hull to and from any array of its reactor
/// group, one cell per <see cref="KamertonDroneComponent.ActionInterval"/>. Spent cells are dropped at its
/// feet, outside the reactor next to the storage, ready to be taken to the converter.
/// </summary>
public sealed class KamertonDroneSystem : EntitySystem
{
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly KamertonReactorSystem _reactor = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    /// <summary>Close enough to a walk goal, tiles.</summary>
    private const float GoalTolerance = 0.3f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<KamertonFuelStorageComponent, ComponentStartup>(OnStorageStartup);
        SubscribeLocalEvent<KamertonFuelStorageComponent, InteractUsingEvent>(OnStorageInteractUsing,
            after: new[] { typeof(FlatpackSystem) });
        SubscribeLocalEvent<KamertonFuelStorageComponent, EntInsertedIntoContainerMessage>((uid, comp, _) => UpdateStorageVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonFuelStorageComponent, EntRemovedFromContainerMessage>((uid, comp, _) => UpdateStorageVisuals((uid, comp)));
        SubscribeLocalEvent<KamertonFuelStorageComponent, ExaminedEvent>(OnStorageExamined);

        SubscribeLocalEvent<KamertonDroneSpawnerComponent, InteractUsingEvent>(OnSpawnerInteractUsing,
            after: new[] { typeof(FlatpackSystem) });
        SubscribeLocalEvent<KamertonDroneComponent, ExaminedEvent>(OnDroneExamined);
        SubscribeLocalEvent<KamertonControlModuleComponent, KamertonDroneCommandMessage>(OnCommand);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<KamertonDroneComponent>();
        while (query.MoveNext(out var uid, out var drone))
        {
            Think((uid, drone), frameTime);
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

    #region Drone

    /// <summary>A core flatpack unpacked (the flatpack system queued it for deletion): the core comes with its drone.</summary>
    private void OnSpawnerInteractUsing(Entity<KamertonDroneSpawnerComponent> ent, ref InteractUsingEvent args)
    {
        if (!EntityManager.IsQueuedForDeletion(ent))
            return;

        foreach (var core in _lookup.GetEntitiesInRange<KamertonReactorCoreComponent>(Transform(ent).Coordinates, 0.75f))
        {
            if (core.Comp.Drone != null)
                continue;

            SpawnDrone(core, Transform(args.User).Coordinates, ent.Comp.Drone.Id);
            break;
        }
    }

    public EntityUid SpawnDrone(Entity<KamertonReactorCoreComponent> core, Robust.Shared.Map.EntityCoordinates coords, string proto = "KamertonDroneRDZT")
    {
        var uid = Spawn(proto, coords);
        Link(uid, core);
        return uid;
    }

    public void Link(EntityUid drone, Entity<KamertonReactorCoreComponent> core)
    {
        var comp = EnsureComp<KamertonDroneComponent>(drone);
        comp.Core = core;
        core.Comp.Drone = drone;
    }

    private Entity<KamertonFuelStorageComponent>? Storage(KamertonReactorCoreComponent core)
    {
        return core.Storage is { } uid && TryComp<KamertonFuelStorageComponent>(uid, out var comp) ? (uid, comp) : null;
    }

    private IEnumerable<Entity<KamertonFuelArrayComponent>> Arrays(KamertonReactorCoreComponent core)
    {
        foreach (var uid in core.StableArrays)
        {
            if (TryComp<KamertonFuelArrayComponent>(uid, out var a))
                yield return (uid, a);
        }

        foreach (var uid in core.UnstableArrays)
        {
            if (TryComp<KamertonFuelArrayComponent>(uid, out var a))
                yield return (uid, a);
        }
    }

    /// <summary>The job the storage and arrays call for right now: spent cells out first, then fresh ones in.</summary>
    public KamertonDroneCommand? PickTask(KamertonReactorCoreComponent core)
    {
        if (Storage(core) is not { } storage)
            return null;

        var info = BuildInfo(core, storage);
        if (info.Spent > 0)
            return KamertonDroneCommand.Unload;

        if (info.FreeStable > 0 && info.StorageStable > 0 || info.FreeUnstable > 0 && info.StorageUnstable > 0)
            return KamertonDroneCommand.Load;

        return null;
    }

    public void Command(Entity<KamertonDroneComponent> drone, KamertonDroneCommand command)
    {
        var comp = drone.Comp;
        switch (command)
        {
            case KamertonDroneCommand.Load:
            case KamertonDroneCommand.Unload:
                comp.State = KamertonDroneState.Working;
                comp.Task = command;
                comp.Accumulator = 0f;
                break;
            case KamertonDroneCommand.Return:
                comp.State = KamertonDroneState.Returning;
                comp.Task = null;
                comp.AutoRefuel = false;
                break;
            case KamertonDroneCommand.ToggleAuto:
                comp.AutoRefuel = !comp.AutoRefuel;
                break;
        }
    }

    /// <summary>One brain tick: leash, then the state's goal, then one walking step.</summary>
    public void Think(Entity<KamertonDroneComponent> drone, float dt)
    {
        var comp = drone.Comp;
        if (comp.Core is not { } coreUid || !TryComp<KamertonReactorCoreComponent>(coreUid, out var core))
        {
            comp.State = KamertonDroneState.Idle;
            comp.Task = null;
            Walk(drone, null);
            return;
        }

        var pos = _transform.GetWorldPosition(drone);
        var corePos = _transform.GetWorldPosition(coreUid);
        var fromCore = (pos - corePos).Length();

        if (fromCore > comp.LeashRadius && comp.State != KamertonDroneState.Returning)
        {
            comp.State = KamertonDroneState.Returning;
            comp.Task = null;
        }

        switch (comp.State)
        {
            case KamertonDroneState.Returning:
                if (fromCore <= comp.WanderRadius * 0.5f)
                {
                    comp.State = KamertonDroneState.Idle;
                    comp.Goal = null;
                    comp.WanderTimer = 0f;
                }
                else
                {
                    comp.Goal = corePos;
                }

                break;

            case KamertonDroneState.Idle:
                if (comp.AutoRefuel && PickTask(core) is { } task)
                {
                    Command(drone, task);
                    break;
                }

                comp.WanderTimer -= dt;
                if (comp.WanderTimer <= 0f)
                {
                    comp.WanderTimer = _random.NextFloat(4f, 8f);
                    comp.Goal = corePos + _random.NextAngle().ToVec() * _random.NextFloat(2f, comp.WanderRadius);
                }

                break;

            case KamertonDroneState.Working:
                if (Storage(core) is not { } storage || comp.Task is not { } job)
                {
                    comp.State = KamertonDroneState.Idle;
                    comp.Task = null;
                    break;
                }

                var storagePos = _transform.GetWorldPosition(storage);
                if ((pos - storagePos).Length() > comp.DockRange)
                {
                    comp.Goal = storagePos;
                    break;
                }

                comp.Goal = null;
                comp.Accumulator += dt;
                if (comp.Accumulator < comp.ActionInterval)
                    break;

                comp.Accumulator = 0f;
                var done = job == KamertonDroneCommand.Load ? LoadOne(drone, core, storage) : UnloadOne(drone, core);
                if (done)
                    break;

                // Job finished: take the next one in auto mode, otherwise go idle.
                comp.Task = comp.AutoRefuel ? PickTask(core) : null;
                if (comp.Task == null)
                    comp.State = KamertonDroneState.Idle;
                break;
        }

        Walk(drone, comp.Goal);
    }

    /// <summary>Moves one charged cell from the storage into a free array slot.</summary>
    public bool LoadOne(EntityUid drone, KamertonReactorCoreComponent core, Entity<KamertonFuelStorageComponent> storage)
    {
        foreach (var array in Arrays(core))
        {
            var container = GetStorage(storage, array.Comp.Kind);
            if (container.ContainedEntities.Count == 0)
                continue;

            for (var i = 1; i <= array.Comp.Slots; i++)
            {
                var slot = KamertonReactorSystem.SlotId(i);
                if (_slots.GetItemOrNull(array, slot) != null)
                    continue;

                var cell = container.ContainedEntities[0];
                _containers.Remove(cell, container);
                if (_slots.TryInsert(array, slot, cell, null, excludeUserAudio: true))
                    return true;

                _containers.Insert(cell, container);
            }
        }

        return false;
    }

    /// <summary>Pulls one spent cell out of an array and drops it at the drone's feet.</summary>
    public bool UnloadOne(EntityUid drone, KamertonReactorCoreComponent core)
    {
        foreach (var array in Arrays(core))
        {
            for (var i = 1; i <= array.Comp.Slots; i++)
            {
                var slot = KamertonReactorSystem.SlotId(i);
                if (_slots.GetItemOrNull(array, slot) is not { } item
                    || !TryComp<KamertonCellComponent>(item, out var cell) || cell.Charged)
                    continue;

                if (!_slots.TryEject(array, slot, null, out var ejected, excludeUserAudio: true))
                    continue;

                _transform.SetCoordinates(ejected.Value, Transform(drone).Coordinates);
                _transform.AttachToGridOrMap(ejected.Value);
                return true;
            }
        }

        return false;
    }

    private void Walk(Entity<KamertonDroneComponent> drone, Vector2? goal)
    {
        var velocity = Vector2.Zero;
        if (goal is { } target)
        {
            var delta = target - _transform.GetWorldPosition(drone);
            if (delta.Length() > GoalTolerance)
            {
                velocity = Vector2.Normalize(delta) * drone.Comp.Speed;
                _transform.SetWorldRotation(drone, velocity.ToWorldAngle());
            }
            else if (drone.Comp.State == KamertonDroneState.Idle)
            {
                drone.Comp.Goal = null;
            }
        }

        _physics.SetLinearVelocity(drone, velocity);
        _appearance.SetData(drone, KamertonDroneVisuals.Moving, velocity != Vector2.Zero);
    }

    private void OnDroneExamined(Entity<KamertonDroneComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("kamerton-drone-examine", ("state", StateName(ent.Comp.State)),
            ("auto", ent.Comp.AutoRefuel ? 1 : 0)));
    }

    private string StateName(KamertonDroneState state)
    {
        return Loc.GetString(state switch
        {
            KamertonDroneState.Working => "kamerton-drone-state-working",
            KamertonDroneState.Returning => "kamerton-drone-state-returning",
            _ => "kamerton-drone-state-idle",
        });
    }

    #endregion

    #region Console

    public KamertonDroneInfo BuildInfo(KamertonReactorCoreComponent core)
    {
        return BuildInfo(core, Storage(core));
    }

    private KamertonDroneInfo BuildInfo(KamertonReactorCoreComponent core, Entity<KamertonFuelStorageComponent>? storage)
    {
        var info = new KamertonDroneInfo();
        if (core.Drone is { } droneUid && TryComp<KamertonDroneComponent>(droneUid, out var drone))
        {
            info.HasDrone = true;
            info.State = drone.State;
            info.Task = drone.Task;
            info.AutoRefuel = drone.AutoRefuel;
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

    private void OnCommand(Entity<KamertonControlModuleComponent> ent, ref KamertonDroneCommandMessage args)
    {
        if (_reactor.ConsoleCore(ent) is not { } core)
            return;

        if (core.Comp.Drone is not { } droneUid || !TryComp<KamertonDroneComponent>(droneUid, out var drone))
        {
            _popup.PopupEntity(Loc.GetString("kamerton-drone-missing"), ent, args.Actor);
            return;
        }

        Command((droneUid, drone), args.Command);
        _reactor.UpdateUi(core);
    }

    #endregion
}

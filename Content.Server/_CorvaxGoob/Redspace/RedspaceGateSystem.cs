using System.Numerics;
using Content.Server.Administration.Logs;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Labels.EntitySystems;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Content.Shared.Teleportation.Components;
using Content.Shared.Teleportation.Systems;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Redspace;

public sealed class RedspaceGateSystem : EntitySystem
{
    [Dependency] private readonly IAdminLogManager _adminLog = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IMapManager _mapManager = default!;
    [Dependency] private readonly ItemSlotsSystem _itemSlots = default!;
    [Dependency] private readonly LabelSystem _label = default!;
    [Dependency] private readonly LinkedEntitySystem _linked = default!;
    [Dependency] private readonly PeriodicCrackleSystem _crackle = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedMapSystem _map = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RedspaceGateComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<RedspaceGateComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<RedspaceGateComponent, InteractUsingEvent>(OnInteractUsing);
        SubscribeLocalEvent<RedspaceGateComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<RedspaceGateComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<RedspaceGateComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<RedspaceGateComponent, BoundUIOpenedEvent>(OnUiOpened);
        SubscribeLocalEvent<RedspaceGateComponent, RedspaceGateOpenPortalMessage>(OnOpenMessage);
        SubscribeLocalEvent<RedspaceGateComponent, RedspaceGateClosePortalMessage>(OnCloseMessage);
        SubscribeLocalEvent<RedspaceGateComponent, RedspaceGateEjectDiskMessage>(OnEjectMessage);

        SubscribeLocalEvent<RedspaceGatePortalComponent, ComponentShutdown>(OnExitPortalShutdown);
        SubscribeLocalEvent<RedspaceGateDiskComponent, ExaminedEvent>(OnDiskExamined);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<RedspaceGateComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Portal == null || now < comp.NextConsume)
                continue;

            if (TryConsumeFuel(comp, out var burn))
            {
                comp.NextConsume = now + burn;
                UpdateAppearance(uid, comp);
                UpdateUi(uid, comp);
                continue;
            }

            _popup.PopupEntity(Loc.GetString("redspace-gate-fuel-depleted"), uid, PopupType.MediumCaution);
            ClosePortal((uid, comp));
        }
    }

    #region Events

    private void OnStartup(Entity<RedspaceGateComponent> ent, ref ComponentStartup args)
    {
        UpdateAppearance(ent, ent.Comp);
    }

    private void OnShutdown(Entity<RedspaceGateComponent> ent, ref ComponentShutdown args)
    {
        // Gate destroyed / deleted: the exit portal goes with it.
        ClosePortal(ent, playSound: false);
    }

    private void OnExitPortalShutdown(Entity<RedspaceGatePortalComponent> ent, ref ComponentShutdown args)
    {
        // Exit portal removed by something else (admin delete, its map deleted...): close the gate side.
        if (ent.Comp.Gate is not { } gate
            || TerminatingOrDeleted(gate)
            || !TryComp<RedspaceGateComponent>(gate, out var gateComp)
            || gateComp.Portal != ent.Owner)
            return;

        gateComp.Portal = null;
        ClosePortal((gate, gateComp));
    }

    private void OnInteractUsing(Entity<RedspaceGateComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<StackComponent>(args.Used, out var stack))
            return;

        if (!IsFuel(ent.Comp, stack.StackTypeId, out _))
            return;

        args.Handled = true;
        TryInsertFuel(ent, args.Used, args.User);
    }

    private void OnExamined(Entity<RedspaceGateComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        args.PushMarkup(Loc.GetString("redspace-gate-examine-fuel",
            ("bluespace", ent.Comp.Bluespace),
            ("bluespaceCapacity", ent.Comp.BluespaceCapacity),
            ("redspace", ent.Comp.Redspace),
            ("redspaceCapacity", ent.Comp.RedspaceCapacity)));
    }

    private void OnDiskExamined(Entity<RedspaceGateDiskComponent> ent, ref ExaminedEvent args)
    {
        var map = ent.Comp.Map;
        var mapName = map != null && !TerminatingOrDeleted(map.Value)
            ? MetaData(map.Value).EntityName
            : Loc.GetString("redspace-gate-window-map-unknown");
        var mapId = map != null && TryComp<MapComponent>(map.Value, out var mapComp) ? mapComp.MapId.ToString() : "?";

        args.PushMarkup(Loc.GetString("redspace-gate-disk-examine",
            ("x", FormatCoord(ent.Comp.Position.X)),
            ("y", FormatCoord(ent.Comp.Position.Y)),
            ("map", mapName),
            ("mapId", mapId)));
    }

    public static string FormatCoord(float value)
    {
        return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnContainerChanged<T>(Entity<RedspaceGateComponent> ent, ref T args) where T : ContainerModifiedMessage
    {
        if (args.Container.ID == ent.Comp.DiskSlotId)
            UpdateUi(ent, ent.Comp);
    }

    private void OnUiOpened(Entity<RedspaceGateComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent, ent.Comp);
    }

    private void OnOpenMessage(Entity<RedspaceGateComponent> ent, ref RedspaceGateOpenPortalMessage args)
    {
        if (TryOpenPortal(ent, new Vector2(args.X, args.Y), args.Actor, out var failure))
            return;

        if (failure != null)
            _popup.PopupEntity(Loc.GetString(failure), ent, args.Actor, PopupType.SmallCaution);
        _audio.PlayPvs(ent.Comp.DenySound, ent);
    }

    private void OnCloseMessage(Entity<RedspaceGateComponent> ent, ref RedspaceGateClosePortalMessage args)
    {
        if (ent.Comp.Portal == null)
            return;

        _adminLog.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(args.Actor):player} closed {ToPrettyString(ent):gate}");
        ClosePortal(ent);
    }

    private void OnEjectMessage(Entity<RedspaceGateComponent> ent, ref RedspaceGateEjectDiskMessage args)
    {
        if (_itemSlots.TryGetSlot(ent, ent.Comp.DiskSlotId, out var slot))
            _itemSlots.TryEjectToHands(ent, slot, args.Actor, true);
    }

    #endregion

    #region Fuel

    public bool HasFuel(RedspaceGateComponent comp)
    {
        return comp.Bluespace > 0 || comp.Redspace > 0;
    }

    /// <summary>Socketed crystals drawn in the bluespace rack (one per <see cref="RedspaceGateComponent.BluespacePerSlot"/>).</summary>
    public int GetBluespaceLevel(RedspaceGateComponent comp)
    {
        return comp.BluespacePerSlot <= 0 ? 0 : Math.Clamp(comp.Bluespace, 0, comp.BluespaceCapacity) / comp.BluespacePerSlot;
    }

    /// <summary>Socketed crimson crystals drawn on the frame (one per <see cref="RedspaceGateComponent.RedspacePerSocket"/>).</summary>
    public int GetRedspaceLevel(RedspaceGateComponent comp)
    {
        return comp.RedspacePerSocket <= 0 ? 0 : Math.Clamp(comp.Redspace, 0, comp.RedspaceCapacity) / comp.RedspacePerSocket;
    }

    /// <summary>
    /// Whether a stack type is accepted as fuel. <paramref name="redspace"/> tells which kind.
    /// </summary>
    public bool IsFuel(RedspaceGateComponent comp, string stackType, out bool redspace)
    {
        redspace = comp.RedspaceStacks.Contains(stackType);
        return redspace || comp.BluespaceStacks.Contains(stackType);
    }

    /// <summary>
    /// Moves as many crystals from the stack as fit: redspace into the gate itself, bluespace into its storage rack.
    /// Returns how many were inserted.
    /// </summary>
    public int TryInsertFuel(Entity<RedspaceGateComponent> ent, EntityUid stackUid, EntityUid? user)
    {
        var comp = ent.Comp;
        if (!TryComp<StackComponent>(stackUid, out var stack) || !IsFuel(comp, stack.StackTypeId, out var redspace))
            return 0;

        var capacity = redspace ? comp.RedspaceCapacity : comp.BluespaceCapacity;
        var stored = redspace ? comp.Redspace : comp.Bluespace;
        var amount = Math.Min(stack.Count, capacity - stored);

        if (amount <= 0)
        {
            if (user != null)
                _popup.PopupEntity(Loc.GetString(redspace ? "redspace-gate-fuel-full-redspace" : "redspace-gate-fuel-full-bluespace"), ent, user.Value);
            return 0;
        }

        if (!_stack.Use(stackUid, amount, stack))
            return 0;

        if (redspace)
            comp.Redspace += amount;
        else
            comp.Bluespace += amount;

        if (user != null)
        {
            _popup.PopupEntity(Loc.GetString(redspace ? "redspace-gate-fuel-inserted-redspace" : "redspace-gate-fuel-inserted-bluespace",
                ("amount", amount),
                ("count", redspace ? comp.Redspace : comp.Bluespace),
                ("capacity", capacity)), ent, user.Value);
        }

        _audio.PlayPvs(comp.FuelSound, ent);
        UpdateAppearance(ent, comp);
        UpdateUi(ent, comp);
        return amount;
    }

    /// <summary>
    /// Burns one crystal, bluespace first. Returns how long that crystal lasts.
    /// </summary>
    private bool TryConsumeFuel(RedspaceGateComponent comp, out TimeSpan burnTime)
    {
        if (comp.Bluespace > 0)
        {
            comp.Bluespace--;
            burnTime = comp.BluespaceBurnTime;
            return true;
        }

        if (comp.Redspace > 0)
        {
            comp.Redspace--;
            burnTime = comp.RedspaceBurnTime;
            return true;
        }

        burnTime = TimeSpan.Zero;
        return false;
    }

    #endregion

    #region Portal

    /// <summary>
    /// Resolves the map the gate targets: the inserted disk's map, or the gate's own map.
    /// </summary>
    public bool TryGetTargetMap(EntityUid gate, RedspaceGateComponent comp, out EntityUid mapUid, out string? failure)
    {
        mapUid = EntityUid.Invalid;
        failure = null;

        if (_itemSlots.GetItemOrNull(gate, comp.DiskSlotId) is { } disk)
        {
            // A disk printed by ftldiskgate carries its own map; any other disk is an ftldisk map override.
            EntityUid? diskDest = TryComp<RedspaceGateDiskComponent>(disk, out var gateDisk) && gateDisk.Map != null
                ? gateDisk.Map
                : CompOrNull<ShuttleDestinationCoordinatesComponent>(disk)?.Destination;

            if (diskDest is not { } dest
                || TerminatingOrDeleted(dest))
            {
                failure = "redspace-gate-fail-no-map";
                return false;
            }

            if (!HasComp<MapComponent>(dest))
            {
                if (Transform(dest).MapUid is not { } destMap)
                {
                    failure = "redspace-gate-fail-no-map";
                    return false;
                }
                dest = destMap;
            }

            mapUid = dest;
        }
        else
        {
            if (Transform(gate).MapUid is not { } own)
            {
                failure = "redspace-gate-fail-no-map";
                return false;
            }
            mapUid = own;
        }

        if (!TryComp<MapComponent>(mapUid, out var mapComp) || !mapComp.MapInitialized || mapComp.MapPaused)
        {
            failure = "redspace-gate-fail-map-unavailable";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates the target, burns the first crystal and opens a linked portal pair.
    /// <paramref name="failure"/> is a locale id on failure.
    /// </summary>
    public bool TryOpenPortal(Entity<RedspaceGateComponent> ent, Vector2 target, EntityUid? user, out string? failure)
    {
        var (uid, comp) = ent;
        failure = null;

        if (comp.Portal != null)
        {
            failure = "redspace-gate-fail-already-open";
            return false;
        }

        // A disk printed by ftldiskgate pins the exit portal to its stored coordinates.
        if (GetDiskTarget(uid, comp) is { } diskTarget)
            target = diskTarget;

        if (!float.IsFinite(target.X) || !float.IsFinite(target.Y)
            || Math.Abs(target.X) > comp.MaxCoordinate || Math.Abs(target.Y) > comp.MaxCoordinate)
        {
            failure = "redspace-gate-fail-invalid-coordinates";
            return false;
        }

        comp.LastTarget = target;

        if (!TryGetTargetMap(uid, comp, out var mapUid, out failure))
            return false;

        var mapId = Comp<MapComponent>(mapUid).MapId;
        var coords = new MapCoordinates(target, mapId);
        var gatePos = _transform.GetMapCoordinates(uid);

        if (gatePos.MapId == mapId && (gatePos.Position - target).Length() < comp.MinDistance)
        {
            failure = "redspace-gate-fail-too-close";
            return false;
        }

        // Open space is allowed (EVA destinations); a tile blocked by walls/machines is not.
        EntityUid? targetGrid = null;
        if (_mapManager.TryFindGridAt(coords, out var gridUid, out var grid))
        {
            var indices = _map.TileIndicesFor(gridUid, grid, coords);
            if (_turf.IsTileBlocked(gridUid, indices, CollisionGroup.Impassable, grid))
            {
                failure = "redspace-gate-fail-obstructed";
                return false;
            }

            targetGrid = gridUid;
        }

        if (!TryConsumeFuel(comp, out var burn))
        {
            failure = "redspace-gate-fail-no-fuel";
            return false;
        }

        var portal = SpawnUpright(comp.PortalPrototype, coords, targetGrid);
        var portalMarker = EnsureComp<RedspaceGatePortalComponent>(portal);
        portalMarker.Gate = uid;

        var exitPortal = EnsureComp<PortalComponent>(portal);
        exitPortal.CanTeleportToOtherMaps = true;
        exitPortal.RandomTeleport = false;
        Dirty(portal, exitPortal);

        var gatePortal = EnsureComp<PortalComponent>(uid);
        gatePortal.CanTeleportToOtherMaps = true;
        gatePortal.RandomTeleport = false;
        Dirty(uid, gatePortal);

        _linked.TryLink(uid, portal);

        comp.Portal = portal;
        comp.Target = coords;
        comp.NextConsume = _timing.CurTime + burn;

        _audio.PlayPvs(comp.OpenSound, uid);
        _audio.PlayPvs(comp.OpenSound, portal);

        if (user != null)
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(user.Value):player} opened {ToPrettyString(uid):gate} to {coords}");

        UpdateAppearance(uid, comp);
        UpdateUi(uid, comp);
        return true;
    }

    /// <summary>
    /// Stored coordinates of the inserted ftldiskgate disk, if any.
    /// </summary>
    public Vector2? GetDiskTarget(EntityUid gate, RedspaceGateComponent comp)
    {
        if (_itemSlots.GetItemOrNull(gate, comp.DiskSlotId) is not { } disk
            || !TryComp<RedspaceGateDiskComponent>(disk, out var gateDisk))
            return null;

        return gateDisk.Position;
    }

    /// <summary>
    /// The gate prints a coordinates disk (the ftldisk CoordinatesDisk) that also stores exact coordinates:
    /// with it inserted the gate opens its exit portal right at <paramref name="position"/> on <paramref name="mapUid"/>.
    /// The disk drops out in front of (below) the gate, or onto the gate's tile when that is blocked.
    /// </summary>
    public EntityUid PrintCoordinatesDisk(Entity<RedspaceGateComponent> ent, EntityUid mapUid, Vector2 position, EntityUid? user = null)
    {
        var (uid, comp) = ent;
        var xform = Transform(uid);
        var dropCoords = xform.Coordinates;

        if (xform.GridUid is { } gridUid && TryComp<MapGridComponent>(gridUid, out var grid))
        {
            var front = _map.TileIndicesFor(gridUid, grid, xform.Coordinates) + new Vector2i(0, -1);
            if (_map.TryGetTileRef(gridUid, grid, front, out var tile)
                && !tile.Tile.IsEmpty
                && !_turf.IsTileBlocked(gridUid, front, CollisionGroup.Impassable, grid))
                dropCoords = _map.GridTileToLocal(gridUid, grid, front);
        }
        else
        {
            dropCoords = dropCoords.Offset(new Vector2(0f, -1f));
        }

        var disk = Spawn(comp.DiskPrototype, dropCoords);

        var dest = EnsureComp<ShuttleDestinationCoordinatesComponent>(disk);
        dest.Destination = mapUid;
        Dirty(disk, dest);

        var gateDisk = EnsureComp<RedspaceGateDiskComponent>(disk);
        gateDisk.Map = mapUid;
        gateDisk.Position = position;

        _label.Label(disk, $"{MetaData(mapUid).EntityName} ({FormatCoord(position.X)}, {FormatCoord(position.Y)})");

        _audio.PlayPvs(comp.PrintSound, uid);
        _crackle.TryCrackle(uid);
        _popup.PopupEntity(Loc.GetString("redspace-gate-disk-printed"), uid, PopupType.Medium);

        if (user != null)
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"{ToPrettyString(user.Value):player} printed {ToPrettyString(disk):disk} at {ToPrettyString(uid):gate} for {position} on {ToPrettyString(mapUid):map}");
        else
            _adminLog.Add(LogType.Action, LogImpact.Medium,
                $"Server printed {ToPrettyString(disk):disk} at {ToPrettyString(uid):gate} for {position} on {ToPrettyString(mapUid):map}");

        return disk;
    }

    /// <summary>
    /// Spawns the exit portal square to whatever it stands on. Spawning at map coordinates parents an unanchored
    /// entity to the map with zero world rotation; grid traversal then moves it onto the grid keeping that world
    /// rotation, i.e. a local rotation of minus the grid's angle. The eye of anyone on that grid is aligned to the
    /// grid, so the portal showed up tilted by the grid's rotation. Parent it to the grid directly with zero local
    /// rotation instead (the sprite is also noRot, so it is drawn upright for every eye angle).
    /// </summary>
    private EntityUid SpawnUpright(EntProtoId proto, MapCoordinates coords, EntityUid? gridUid)
    {
        var portal = gridUid is { } grid
            ? Spawn(proto, _transform.ToCoordinates(grid, coords))
            : Spawn(proto, coords);

        _transform.SetLocalRotation(portal, Angle.Zero);
        return portal;
    }

    /// <summary>
    /// Closes the portal pair and deletes the exit portal.
    /// </summary>
    public void ClosePortal(Entity<RedspaceGateComponent> ent, bool playSound = true)
    {
        var (uid, comp) = ent;
        var portal = comp.Portal;
        comp.Portal = null;
        comp.Target = null;

        if (portal != null && !TerminatingOrDeleted(portal.Value))
        {
            if (!TerminatingOrDeleted(uid))
                _linked.TryUnlink(uid, portal.Value);

            if (playSound)
                _audio.PlayPvs(comp.CloseSound, portal.Value);
            QueueDel(portal.Value);
        }

        if (TerminatingOrDeleted(uid))
            return;

        RemComp<PortalComponent>(uid);

        if (playSound && portal != null)
            _audio.PlayPvs(comp.CloseSound, uid);

        UpdateAppearance(uid, comp);
        UpdateUi(uid, comp);
    }

    #endregion

    private void UpdateAppearance(EntityUid uid, RedspaceGateComponent comp)
    {
        var open = comp.Portal != null;
        _appearance.SetData(uid, RedspaceGateVisuals.Active, open);
        var fueled = HasFuel(comp);
        _appearance.SetData(uid, RedspaceGateVisuals.Fueled, fueled);
        _appearance.SetData(uid, RedspaceGateVisuals.BluespaceLevel, GetBluespaceLevel(comp));
        _appearance.SetData(uid, RedspaceGateVisuals.RedspaceLevel, GetRedspaceLevel(comp));
        _light.SetEnabled(uid, open);
        // Crackles like a crystal only while it holds crystals; while the portal is open, around the ring
        // and more often (the overlay state follows RedspaceGateVisuals.Active via GenericVisualizer).
        _crackle.SetEnabled(uid, fueled || open);
        if (open)
            _crackle.SetInterval(uid, comp.OpenCrackleMin, comp.OpenCrackleMax);
        else
            _crackle.SetInterval(uid, comp.IdleCrackleMin, comp.IdleCrackleMax);
    }

    public RedspaceGateBoundUserInterfaceState GetUiState(EntityUid uid, RedspaceGateComponent comp)
    {
        var disk = _itemSlots.GetItemOrNull(uid, comp.DiskSlotId);
        var mapValid = TryGetTargetMap(uid, comp, out var mapUid, out _);
        var mapName = mapValid ? MetaData(mapUid).EntityName : Loc.GetString("redspace-gate-window-map-unknown");
        var gatePos = _transform.GetMapCoordinates(uid).Position;

        return new RedspaceGateBoundUserInterfaceState
        {
            Open = comp.Portal != null,
            GatePosition = gatePos,
            TargetMapName = mapName,
            DiskInserted = disk != null,
            DiskValid = disk == null || mapValid,
            DiskTarget = GetDiskTarget(uid, comp),
            Target = comp.Target?.Position ?? comp.LastTarget,
            Bluespace = comp.Bluespace,
            Redspace = comp.Redspace,
            BluespaceCapacity = comp.BluespaceCapacity,
            RedspaceCapacity = comp.RedspaceCapacity,
        };
    }

    private void UpdateUi(EntityUid uid, RedspaceGateComponent comp)
    {
        if (TerminatingOrDeleted(uid) || !_ui.HasUi(uid, RedspaceGateUiKey.Key))
            return;

        _ui.SetUiState(uid, RedspaceGateUiKey.Key, GetUiState(uid, comp));
    }
}

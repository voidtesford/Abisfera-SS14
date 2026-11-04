using System.Numerics;
using Content.Server.Audio;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Inventory.Events;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.Redspace.Physics;

/// <summary>One thing suppressing redspace: where it is and how far it reaches.</summary>
public readonly record struct RedspaceSuppressor(EntityUid Uid, Vector2 Position, float Radius, bool Collar);

/// <summary>
/// Everything that switches redspace off: suppressor collars, «маяк подавитель» beacons and the area around an open
/// Euclidean gate and its exit portal. A position is suppressed when any suppressor covers it.
/// </summary>
public sealed class RedspaceSuppressionSystem : EntitySystem
{
    [Dependency] private readonly AmbientSoundSystem _ambient = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly StackSystem _stack = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RedspaceCollarComponent, ComponentStartup>(OnCollarStartup);
        SubscribeLocalEvent<RedspaceCollarComponent, InteractUsingEvent>(OnCollarInteractUsing);
        SubscribeLocalEvent<RedspaceCollarComponent, ExaminedEvent>(OnCollarExamined);
        SubscribeLocalEvent<RedspaceCollarComponent, GotEquippedEvent>(OnCollarEquipped);
        SubscribeLocalEvent<RedspaceCollarComponent, GotUnequippedEvent>(OnCollarUnequipped);

        SubscribeLocalEvent<RedspaceBeaconComponent, ComponentStartup>(OnBeaconStartup);
        SubscribeLocalEvent<RedspaceBeaconComponent, InteractUsingEvent>(OnBeaconInteractUsing);
        SubscribeLocalEvent<RedspaceBeaconComponent, ExaminedEvent>(OnBeaconExamined);
        SubscribeLocalEvent<RedspaceBeaconComponent, AnchorStateChangedEvent>(OnBeaconAnchor);
    }

    #region Queries

    /// <summary>Collects every active suppressor on <paramref name="mapId"/>. Cheap: these are a handful of entities.</summary>
    public void GetSuppressors(MapId mapId, float gateRadius, List<RedspaceSuppressor> result)
    {
        result.Clear();

        var query = EntityQueryEnumerator<RedspaceSuppressorComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var sup, out var xform))
        {
            if (!sup.Active || xform.MapID != mapId)
                continue;

            result.Add(new RedspaceSuppressor(uid, _transform.GetWorldPosition(xform), sup.Radius, HasComp<RedspaceCollarComponent>(uid)));
        }

        if (gateRadius <= 0f)
            return;

        // The exit portal of an open gate.
        var portals = EntityQueryEnumerator<RedspaceGatePortalComponent, TransformComponent>();
        while (portals.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.MapID == mapId)
                result.Add(new RedspaceSuppressor(uid, _transform.GetWorldPosition(xform), gateRadius, false));
        }

        // The gate itself while its portal is open (it is the entry side).
        var gates = EntityQueryEnumerator<RedspaceGateComponent, TransformComponent>();
        while (gates.MoveNext(out var uid, out var gate, out var xform))
        {
            if (gate.Portal != null && xform.MapID == mapId)
                result.Add(new RedspaceSuppressor(uid, _transform.GetWorldPosition(xform), gateRadius, false));
        }
    }

    /// <summary>Whether <paramref name="pos"/> is inside any suppressor of <paramref name="suppressors"/>.</summary>
    public static bool IsSuppressed(List<RedspaceSuppressor> suppressors, Vector2 pos, out EntityUid? by, bool ignoreCollars = false)
    {
        foreach (var sup in suppressors)
        {
            if (ignoreCollars && sup.Collar)
                continue;

            if ((sup.Position - pos).LengthSquared() <= sup.Radius * sup.Radius)
            {
                by = sup.Uid;
                return true;
            }
        }

        by = null;
        return false;
    }

    #endregion

    #region Collar

    private void OnCollarStartup(Entity<RedspaceCollarComponent> ent, ref ComponentStartup args)
    {
        UpdateCollar(ent);
    }

    public bool CollarActive(RedspaceCollarComponent comp)
    {
        return comp.Wearer != null && (comp.Remaining > 0f || comp.Crystals > 0);
    }

    /// <summary>Seconds of suppression left, counting crystals in the slot.</summary>
    public float CollarCharge(RedspaceCollarComponent comp)
    {
        return Math.Max(0f, comp.Remaining) + comp.Crystals * comp.CrystalTime;
    }

    private void UpdateCollar(Entity<RedspaceCollarComponent> ent)
    {
        var sup = EnsureComp<RedspaceSuppressorComponent>(ent);
        sup.Active = CollarActive(ent.Comp);
        _appearance.SetData(ent, RedspaceSuppressorVisuals.Active, ent.Comp.Remaining > 0f || ent.Comp.Crystals > 0);
    }

    private void OnCollarEquipped(Entity<RedspaceCollarComponent> ent, ref GotEquippedEvent args)
    {
        ent.Comp.Wearer = args.Slot == ent.Comp.Slot ? args.Equipee : null;
        UpdateCollar(ent);
    }

    private void OnCollarUnequipped(Entity<RedspaceCollarComponent> ent, ref GotUnequippedEvent args)
    {
        ent.Comp.Wearer = null;
        UpdateCollar(ent);
    }

    private void OnCollarInteractUsing(Entity<RedspaceCollarComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<StackComponent>(args.Used, out var stack) || stack.StackTypeId != ent.Comp.CrystalStack)
            return;

        args.Handled = true;
        TryInsertCrystal(ent, args.Used, args.User);
    }

    /// <summary>Puts one crystal from the stack into the collar's slot.</summary>
    public bool TryInsertCrystal(Entity<RedspaceCollarComponent> ent, EntityUid stackUid, EntityUid? user)
    {
        if (ent.Comp.Crystals >= ent.Comp.CrystalCapacity)
        {
            if (user != null)
                _popup.PopupEntity(Loc.GetString("redspace-collar-full"), ent, user.Value);
            return false;
        }

        if (!_stack.Use(stackUid, 1))
            return false;

        ent.Comp.Crystals++;
        _audio.PlayPvs(ent.Comp.InsertSound, ent);
        if (user != null)
            _popup.PopupEntity(Loc.GetString("redspace-collar-inserted"), ent, user.Value);
        UpdateCollar(ent);
        return true;
    }

    private void OnCollarExamined(Entity<RedspaceCollarComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var charge = CollarCharge(ent.Comp);
        args.PushMarkup(charge <= 0f
            ? Loc.GetString("redspace-collar-examine-empty")
            : Loc.GetString("redspace-collar-examine",
                ("minutes", (int) Math.Ceiling(charge / 60f)),
                ("crystals", ent.Comp.Crystals),
                ("capacity", ent.Comp.CrystalCapacity)));
    }

    /// <summary>
    /// Burns collar charge on one redspace map. A collar drains only while its wearer is not already
    /// covered by a beacon or a gate (collars don't cover each other for this check).
    /// </summary>
    public void UpdateCollars(MapId mapId, List<RedspaceSuppressor> suppressors, float dt)
    {
        var query = EntityQueryEnumerator<RedspaceCollarComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var collar, out var xform))
        {
            if (collar.Wearer == null || xform.MapID != mapId)
                continue;

            if (IsSuppressed(suppressors, _transform.GetWorldPosition(xform), out _, ignoreCollars: true))
                continue;

            DrainCollar((uid, collar), dt);
        }
    }

    public void DrainCollar(Entity<RedspaceCollarComponent> ent, float dt)
    {
        var comp = ent.Comp;
        var wasActive = comp.Remaining > 0f || comp.Crystals > 0;
        while (dt > 0f)
        {
            if (comp.Remaining <= 0f)
            {
                if (comp.Crystals <= 0)
                    break;

                comp.Crystals--;
                comp.Remaining += comp.CrystalTime;
            }

            var burn = Math.Min(dt, comp.Remaining);
            comp.Remaining -= burn;
            dt -= burn;
        }

        if (comp.Remaining < 0f)
            comp.Remaining = 0f;

        if (wasActive && comp.Remaining <= 0f && comp.Crystals <= 0)
        {
            _audio.PlayPvs(comp.DepletedSound, ent);
            if (comp.Wearer != null)
                _popup.PopupEntity(Loc.GetString("redspace-collar-depleted"), ent, comp.Wearer.Value, PopupType.MediumCaution);
        }

        UpdateCollar(ent);
    }

    #endregion

    #region Beacon

    private void OnBeaconStartup(Entity<RedspaceBeaconComponent> ent, ref ComponentStartup args)
    {
        UpdateBeacon(ent);
    }

    public bool BeaconActive(Entity<RedspaceBeaconComponent> ent)
    {
        return Transform(ent).Anchored && ent.Comp.Remaining > 0f;
    }

    /// <summary>Seconds of work left, counting stored cells.</summary>
    public float BeaconFuel(RedspaceBeaconComponent comp)
    {
        return Math.Max(0f, comp.Remaining) + comp.Cells * comp.CellTime;
    }

    /// <summary>Cells in the beacon, counting the one burning.</summary>
    public int BeaconCellCount(RedspaceBeaconComponent comp)
    {
        return comp.Cells + (comp.Remaining > 0f ? 1 : 0);
    }

    private void OnBeaconAnchor(Entity<RedspaceBeaconComponent> ent, ref AnchorStateChangedEvent args)
    {
        UpdateBeacon(ent);
    }

    private void OnBeaconInteractUsing(Entity<RedspaceBeaconComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !HasComp<RedspaceFuelCellComponent>(args.Used))
            return;

        args.Handled = true;
        TryInsertCell(ent, args.Used, args.User);
    }

    /// <summary>Loads a fuel cell (the item is used up).</summary>
    public bool TryInsertCell(Entity<RedspaceBeaconComponent> ent, EntityUid cell, EntityUid? user)
    {
        if (BeaconCellCount(ent.Comp) >= ent.Comp.MaxCells)
        {
            if (user != null)
                _popup.PopupEntity(Loc.GetString("redspace-beacon-full", ("max", ent.Comp.MaxCells)), ent, user.Value);
            return false;
        }

        QueueDel(cell);
        ent.Comp.Cells++;
        _audio.PlayPvs(ent.Comp.InsertSound, ent);
        if (user != null)
        {
            _popup.PopupEntity(Loc.GetString("redspace-beacon-inserted",
                ("count", BeaconCellCount(ent.Comp)), ("max", ent.Comp.MaxCells)), ent, user.Value);
        }

        UpdateBeacon(ent);
        return true;
    }

    private void OnBeaconExamined(Entity<RedspaceBeaconComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var fuel = BeaconFuel(ent.Comp);
        args.PushMarkup(fuel <= 0f
            ? Loc.GetString("redspace-beacon-examine-empty")
            : Loc.GetString("redspace-beacon-examine",
                ("minutes", (int) Math.Ceiling(fuel / 60f)),
                ("cells", BeaconCellCount(ent.Comp)),
                ("max", ent.Comp.MaxCells)));

        if (!Transform(ent).Anchored)
            args.PushMarkup(Loc.GetString("redspace-beacon-examine-unanchored"));
    }

    /// <summary>Starts a new cell if needed and refreshes the suppressor and visuals.</summary>
    private void UpdateBeacon(Entity<RedspaceBeaconComponent> ent)
    {
        var comp = ent.Comp;
        var anchored = Transform(ent).Anchored;
        if (anchored && comp.Remaining <= 0f && comp.Cells > 0)
        {
            comp.Cells--;
            comp.Remaining += comp.CellTime;
        }

        var active = anchored && comp.Remaining > 0f;
        var sup = EnsureComp<RedspaceSuppressorComponent>(ent);
        if (sup.Active == active && _appearance.TryGetData<bool>(ent, RedspaceSuppressorVisuals.Active, out var shown) && shown == active)
            return;

        sup.Active = active;
        _appearance.SetData(ent, RedspaceSuppressorVisuals.Active, active);
        _ambient.SetAmbience(ent, active);
        _light.SetEnabled(ent, active);
    }

    /// <summary>Burns fuel of every working beacon.</summary>
    public void UpdateBeacons(float dt)
    {
        var query = EntityQueryEnumerator<RedspaceBeaconComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var beacon, out var xform))
        {
            if (!xform.Anchored)
                continue;

            var left = dt;
            while (left > 0f)
            {
                if (beacon.Remaining <= 0f)
                {
                    if (beacon.Cells <= 0)
                        break;

                    beacon.Cells--;
                    beacon.Remaining += beacon.CellTime;
                }

                var burn = Math.Min(left, beacon.Remaining);
                beacon.Remaining -= burn;
                left -= burn;
            }

            if (beacon.Remaining <= 0f && beacon.Cells <= 0)
            {
                beacon.Remaining = 0f;
                if (TryComp<RedspaceSuppressorComponent>(uid, out var sup) && sup.Active)
                    _audio.PlayPvs(beacon.PowerDownSound, uid);
            }

            UpdateBeacon((uid, beacon));
        }
    }

    #endregion
}

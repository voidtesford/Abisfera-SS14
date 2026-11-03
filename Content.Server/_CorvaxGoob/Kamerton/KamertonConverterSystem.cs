using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Materials;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Materials;
using Content.Shared.Popups;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Server._CorvaxGoob.Kamerton;

public sealed class KamertonConverterSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly ItemSlotsSystem _slots = default!;
    [Dependency] private readonly KamertonCellSystem _cells = default!;
    [Dependency] private readonly MaterialStorageSystem _materials = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedPointLightSystem _light = default!;
    [Dependency] private readonly PowerReceiverSystem _power = default!;
    [Dependency] private readonly StackSystem _stack = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<KamertonConverterComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<KamertonConverterComponent, InteractUsingEvent>(OnInteractUsing,
            before: new[] { typeof(SharedMaterialStorageSystem) });
        SubscribeLocalEvent<KamertonConverterComponent, GetVerbsEvent<AlternativeVerb>>(OnGetVerbs);
        SubscribeLocalEvent<KamertonConverterComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<KamertonConverterComponent, EntInsertedIntoContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<KamertonConverterComponent, EntRemovedFromContainerMessage>(OnContainerChanged);
        SubscribeLocalEvent<KamertonConverterComponent, MaterialAmountChangedEvent>(OnMaterialsChanged);
        SubscribeLocalEvent<KamertonConverterComponent, BoundUIOpenedEvent>(OnUiOpened);

        SubscribeLocalEvent<KamertonConverterComponent, KamertonConverterStartMessage>((u, c, a) => TryStart((u, c), a.Actor));
        SubscribeLocalEvent<KamertonConverterComponent, KamertonConverterChargeCellMessage>((u, c, a) => TryChargeCell((u, c), a.Actor));
        SubscribeLocalEvent<KamertonConverterComponent, KamertonConverterPrintCrystalMessage>((u, c, a) => TryPrintCrystal((u, c), a.Actor));
        SubscribeLocalEvent<KamertonConverterComponent, KamertonConverterEjectMessage>((u, c, a) => Eject((u, c), a.Actor));
    }

    private void OnMapInit(Entity<KamertonConverterComponent> ent, ref MapInitEvent args)
    {
        UpdateAppearance(ent);
    }

    #region Crystals

    private void OnInteractUsing(Entity<KamertonConverterComponent> ent, ref InteractUsingEvent args)
    {
        if (args.Handled || !TryComp<StackComponent>(args.Used, out var stack))
            return;

        var isBluespace = stack.StackTypeId == ent.Comp.BluespaceStack;
        var isRedspace = stack.StackTypeId == ent.Comp.RedspaceStack;
        if (!isBluespace && !isRedspace)
            return;

        args.Handled = true;
        TryInsertCrystals(ent, args.Used, args.User);
    }

    /// <summary>Loads as many crystals from <paramref name="used"/> as fit. Returns the number loaded.</summary>
    public int TryInsertCrystals(Entity<KamertonConverterComponent> ent, EntityUid used, EntityUid? user)
    {
        if (!TryComp<StackComponent>(used, out var stack))
            return 0;

        var comp = ent.Comp;
        var isRedspace = stack.StackTypeId == comp.RedspaceStack;
        if (comp.Work != KamertonConverterWork.Idle)
        {
            Popup(ent, user, "kamerton-converter-busy");
            return 0;
        }

        if (isRedspace && comp.Mode != KamertonConverterMode.Destroy)
        {
            Popup(ent, user, "kamerton-converter-redspace-needs-destroy");
            return 0;
        }

        var space = comp.MaxCrystals - comp.Bluespace - comp.Redspace;
        var amount = Math.Min(space, stack.Count);
        if (amount <= 0)
        {
            Popup(ent, user, "kamerton-converter-full");
            return 0;
        }

        _stack.Use(used, amount, stack);
        if (isRedspace)
            comp.Redspace += amount;
        else
            comp.Bluespace += amount;

        Popup(ent, user, "kamerton-converter-inserted", ("count", amount));
        Changed(ent);
        return amount;
    }

    public void Eject(Entity<KamertonConverterComponent> ent, EntityUid? user)
    {
        var comp = ent.Comp;
        if (comp.Work != KamertonConverterWork.Idle)
        {
            Popup(ent, user, "kamerton-converter-busy");
            return;
        }

        var coords = Transform(ent).Coordinates;
        if (comp.Bluespace > 0)
            _stack.SpawnMultiple(comp.BluespaceCrystal, comp.Bluespace, coords);
        if (comp.Redspace > 0)
            _stack.SpawnMultiple(comp.RedspaceCrystal, comp.Redspace, coords);
        comp.Bluespace = 0;
        comp.Redspace = 0;

        if (_slots.TryGetSlot(ent, comp.CellSlot, out var slot) && slot.HasItem)
            _slots.TryEjectToHands(ent, slot, user, true);

        Changed(ent);
    }

    #endregion

    #region Mode verb

    private void OnGetVerbs(Entity<KamertonConverterComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract)
            return;

        foreach (var mode in new[] { KamertonConverterMode.Create, KamertonConverterMode.Destroy })
        {
            var target = mode;
            var user = args.User;
            args.Verbs.Add(new AlternativeVerb
            {
                Text = Loc.GetString(mode == KamertonConverterMode.Create
                    ? "kamerton-converter-verb-create"
                    : "kamerton-converter-verb-destroy"),
                Category = VerbCategory.SelectType,
                Icon = new SpriteSpecifier.Texture(new ResPath(mode == KamertonConverterMode.Create
                    ? "/Textures/Interface/VerbIcons/rotate_cw.svg.192dpi.png"
                    : "/Textures/Interface/VerbIcons/rotate_ccw.svg.192dpi.png")),
                Disabled = ent.Comp.Mode == mode,
                Act = () => SetMode(ent, target, user),
            });
        }
    }

    /// <summary>Switches Create/Destroy. Refuses while working, and keeps redspace out of Create mode.</summary>
    public bool SetMode(Entity<KamertonConverterComponent> ent, KamertonConverterMode mode, EntityUid? user)
    {
        var comp = ent.Comp;
        if (comp.Mode == mode)
            return true;

        if (comp.Work != KamertonConverterWork.Idle)
        {
            Popup(ent, user, "kamerton-converter-busy");
            return false;
        }

        if (mode == KamertonConverterMode.Create && comp.Redspace > 0)
        {
            Popup(ent, user, "kamerton-converter-eject-redspace-first");
            return false;
        }

        comp.Mode = mode;
        Popup(ent, user, mode == KamertonConverterMode.Create
            ? "kamerton-converter-mode-create"
            : "kamerton-converter-mode-destroy");
        Changed(ent);
        return true;
    }

    #endregion

    #region Start / finish

    /// <summary>The Start button: Create mode makes a redspace crystal from 10 bluespace, Destroy grinds everything loaded.</summary>
    public bool TryStart(Entity<KamertonConverterComponent> ent, EntityUid? user)
    {
        var comp = ent.Comp;
        if (comp.Work != KamertonConverterWork.Idle)
        {
            Popup(ent, user, "kamerton-converter-busy");
            return false;
        }

        if (comp.Mode == KamertonConverterMode.Create)
        {
            if (comp.Bluespace < comp.MaxCrystals)
            {
                Deny(ent, user, "kamerton-converter-need-crystals", ("count", comp.MaxCrystals));
                return false;
            }

            BeginWork(ent, KamertonConverterWork.Creating, comp.CreateTime);
            return true;
        }

        if (comp.Bluespace + comp.Redspace <= 0)
        {
            Deny(ent, user, "kamerton-converter-empty");
            return false;
        }

        BeginWork(ent, KamertonConverterWork.Grinding, comp.GrindTime);
        return true;
    }

    private void BeginWork(Entity<KamertonConverterComponent> ent, KamertonConverterWork work, TimeSpan duration)
    {
        ent.Comp.Work = work;
        ent.Comp.WorkEnd = _timing.CurTime + duration;
        _audio.PlayPvs(ent.Comp.WorkSound, ent);
        Changed(ent);
    }

    /// <summary>Finishes the current job right away (used by Update and tests).</summary>
    public void FinishWork(Entity<KamertonConverterComponent> ent)
    {
        var comp = ent.Comp;
        var coords = Transform(ent).Coordinates;
        switch (comp.Work)
        {
            case KamertonConverterWork.Creating:
                comp.Bluespace = Math.Max(0, comp.Bluespace - comp.MaxCrystals);
                Spawn(comp.RedspaceCrystal, coords);
                break;
            case KamertonConverterWork.Grinding:
                comp.StableEnergy += comp.Bluespace * comp.StablePerBluespace;
                comp.UnstableEnergy += comp.Redspace * comp.UnstablePerRedspace;
                comp.Bluespace = 0;
                comp.Redspace = 0;
                break;
            case KamertonConverterWork.Printing:
                Spawn(comp.BluespaceCrystal, coords);
                break;
            default:
                return;
        }

        comp.Work = KamertonConverterWork.Idle;
        _audio.PlayPvs(comp.DoneSound, ent);
        Changed(ent);
    }

    #endregion

    #region Cells

    public bool TryChargeCell(Entity<KamertonConverterComponent> ent, EntityUid? user)
    {
        var comp = ent.Comp;
        if (_slots.GetItemOrNull(ent, comp.CellSlot) is not { } cellUid
            || !TryComp<KamertonCellComponent>(cellUid, out var cell))
        {
            Deny(ent, user, "kamerton-converter-no-cell");
            return false;
        }

        if (cell.Charged)
        {
            Deny(ent, user, "kamerton-converter-cell-already-charged");
            return false;
        }

        var stable = cell.Kind == KamertonCellKind.Stable;
        var have = stable ? comp.StableEnergy : comp.UnstableEnergy;
        if (have < comp.ChargeCost)
        {
            Deny(ent, user, stable ? "kamerton-converter-not-enough-stable" : "kamerton-converter-not-enough-unstable",
                ("need", comp.ChargeCost), ("have", have));
            return false;
        }

        if (stable)
            comp.StableEnergy -= comp.ChargeCost;
        else
            comp.UnstableEnergy -= comp.ChargeCost;

        _cells.SwapInSlot(ent, comp.CellSlot, (cellUid, cell));
        _audio.PlayPvs(comp.DoneSound, ent);
        Popup(ent, user, "kamerton-converter-cell-charged");
        Changed(ent);
        return true;
    }

    #endregion

    #region Printing

    public bool TryPrintCrystal(Entity<KamertonConverterComponent> ent, EntityUid? user)
    {
        var comp = ent.Comp;
        if (comp.Work != KamertonConverterWork.Idle)
        {
            Popup(ent, user, "kamerton-converter-busy");
            return false;
        }

        if (comp.Power < comp.PrintPower)
        {
            Deny(ent, user, "kamerton-converter-not-enough-power", ("need", comp.PrintPower), ("have", MathF.Floor(comp.Power)));
            return false;
        }

        var sheet = SheetVolume;
        var cost = comp.PrintSheets * sheet;
        if (_materials.GetMaterialAmount(ent, comp.Plasma) < cost
            || _materials.GetMaterialAmount(ent, comp.Uranium) < cost)
        {
            Deny(ent, user, "kamerton-converter-not-enough-materials", ("count", comp.PrintSheets));
            return false;
        }

        _materials.TryChangeMaterialAmount(ent, comp.Plasma, -cost);
        _materials.TryChangeMaterialAmount(ent, comp.Uranium, -cost);
        comp.Power -= comp.PrintPower;
        BeginWork(ent, KamertonConverterWork.Printing, comp.PrintTime);
        return true;
    }

    /// <summary>Material units in one sheet (sheets carry 100 units of their material).</summary>
    public const int SheetVolume = 100;

    /// <summary>Charges the internal battery for <paramref name="dt"/> seconds if the machine is powered.</summary>
    public void ChargeBattery(Entity<KamertonConverterComponent> ent, float dt)
    {
        if (!_power.IsPowered(ent))
            return;

        ent.Comp.Power = Math.Min(ent.Comp.MaxPower, ent.Comp.Power + ent.Comp.ChargeRate * dt);
    }

    #endregion

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<KamertonConverterComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            var ent = (uid, comp);
            var wasFull = comp.Power >= comp.MaxPower;
            ChargeBattery(ent, frameTime);

            if (comp.Work != KamertonConverterWork.Idle && now >= comp.WorkEnd)
                FinishWork(ent);

            comp.UiAccumulator += frameTime;
            if (comp.UiAccumulator >= 1f && !wasFull)
            {
                comp.UiAccumulator = 0f;
                UpdateUi(ent);
            }
        }
    }

    private void OnExamined(Entity<KamertonConverterComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var c = ent.Comp;
        args.PushMarkup(Loc.GetString(c.Mode == KamertonConverterMode.Create
            ? "kamerton-converter-examine-mode-create"
            : "kamerton-converter-examine-mode-destroy"));
        args.PushMarkup(Loc.GetString("kamerton-converter-examine-crystals",
            ("bluespace", c.Bluespace), ("redspace", c.Redspace), ("max", c.MaxCrystals)));
        args.PushMarkup(Loc.GetString("kamerton-converter-examine-energy",
            ("stable", c.StableEnergy), ("unstable", c.UnstableEnergy)));
    }

    private void OnContainerChanged(EntityUid uid, KamertonConverterComponent comp, ContainerModifiedMessage args)
    {
        if (args.Container.ID == comp.CellSlot)
            UpdateUi((uid, comp));
    }

    private void OnMaterialsChanged(Entity<KamertonConverterComponent> ent, ref MaterialAmountChangedEvent args)
    {
        UpdateUi(ent);
    }

    private void OnUiOpened(Entity<KamertonConverterComponent> ent, ref BoundUIOpenedEvent args)
    {
        UpdateUi(ent);
    }

    private void Changed(Entity<KamertonConverterComponent> ent)
    {
        UpdateAppearance(ent);
        UpdateUi(ent);
    }

    private void UpdateAppearance(Entity<KamertonConverterComponent> ent)
    {
        _appearance.SetData(ent, KamertonConverterVisuals.Fill, Math.Clamp(ent.Comp.Bluespace + ent.Comp.Redspace, 0, 10));
        _appearance.SetData(ent, KamertonConverterVisuals.Work, ent.Comp.Work);
        _appearance.SetData(ent, KamertonConverterVisuals.Mode, ent.Comp.Mode);
        // Crawling red lightning lights the room while it works.
        _light.SetEnabled(ent, ent.Comp.Work != KamertonConverterWork.Idle);
    }

    public void UpdateUi(Entity<KamertonConverterComponent> ent)
    {
        if (!_ui.IsUiOpen(ent.Owner, KamertonConverterUiKey.Key))
            return;

        var c = ent.Comp;
        string? cellName = null;
        var charged = false;
        var stable = true;
        if (_slots.GetItemOrNull(ent, c.CellSlot) is { } cellUid)
        {
            cellName = Name(cellUid);
            if (TryComp<KamertonCellComponent>(cellUid, out var cell))
            {
                charged = cell.Charged;
                stable = cell.Kind == KamertonCellKind.Stable;
            }
        }

        _ui.SetUiState(ent.Owner, KamertonConverterUiKey.Key, new KamertonConverterBoundUserInterfaceState(
            c.Mode, c.Work, c.Bluespace, c.Redspace, c.MaxCrystals,
            c.StableEnergy, c.UnstableEnergy, c.ChargeCost,
            c.Power, c.MaxPower, c.PrintPower,
            _materials.GetMaterialAmount(ent, c.Plasma) / SheetVolume,
            _materials.GetMaterialAmount(ent, c.Uranium) / SheetVolume,
            c.PrintSheets, cellName, charged, stable));
    }

    private void Popup(EntityUid uid, EntityUid? user, string loc, params (string, object)[] args)
    {
        if (user is { } u)
            _popup.PopupEntity(Loc.GetString(loc, args), uid, u, PopupType.Small);
        else
            _popup.PopupEntity(Loc.GetString(loc, args), uid, PopupType.Small);
    }

    private void Deny(Entity<KamertonConverterComponent> ent, EntityUid? user, string loc, params (string, object)[] args)
    {
        _audio.PlayPvs(ent.Comp.DenySound, ent);
        if (user is { } u)
            _popup.PopupEntity(Loc.GetString(loc, args), ent, u, PopupType.SmallCaution);
        else
            _popup.PopupEntity(Loc.GetString(loc, args), ent, PopupType.SmallCaution);
    }
}

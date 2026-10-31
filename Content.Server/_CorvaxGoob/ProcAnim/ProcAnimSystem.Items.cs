using System.Linq;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Hands.Components;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Tag;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Audio;
using Robust.Shared.Containers;

namespace Content.Server._CorvaxGoob.ProcAnim;

/// <summary>
/// Real items in scenes: <see cref="ProcAnimActor.Items"/> (bound on start, drawn / returned on time, props drawn with
/// their sprite) and <see cref="ProcAnimActor.StowHands"/> (the pre-beat that puts the hands' contents away).
/// </summary>
public sealed partial class ProcAnimSystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly IComponentFactory _factory = default!;

    private static readonly ProcAnimStow DefaultStow = new();

    /// <summary>True when <paramref name="uid"/> matches <paramref name="match"/>.</summary>
    public bool Matches(EntityUid uid, ProcAnimItemMatch match)
    {
        if (match.Prototypes.Count > 0 && MetaData(uid).EntityPrototype is { } proto)
        {
            foreach (var id in match.Prototypes)
            {
                if (proto.ID == id.Id || _proto.EnumerateParents<Robust.Shared.Prototypes.EntityPrototype>(proto.ID).Any(p => p.ID == id.Id))
                    return true;
            }
        }

        foreach (var tag in match.Tags)
        {
            if (_tag.HasTag(uid, tag))
                return true;
        }

        foreach (var name in match.Components)
        {
            if (_factory.TryGetRegistration(name, out var reg) && HasComp(uid, reg.Type))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The entity <paramref name="item"/> binds to on <paramref name="actor"/> (hands, then worn items, then storages worn,
    /// nested ones included), with where it was found.
    /// </summary>
    public EntityUid? FindItem(EntityUid actor, ProcAnimItem item, IReadOnlyCollection<EntityUid>? exclude = null)
        => FindItem(actor, item, exclude, out _);

    private EntityUid? FindItem(EntityUid actor, ProcAnimItem item, IReadOnlyCollection<EntityUid>? exclude,
        out ProcAnimBoundItem origin)
    {
        origin = new ProcAnimBoundItem { Index = item.Index };
        bool Ok(EntityUid e) => !HasComp<VirtualItemComponent>(e) && exclude?.Contains(e) != true && Matches(e, item.Match);

        var from = item.From.Count > 0 ? item.From : null;
        if (from == null || from.Contains("hands"))
        {
            foreach (var held in _hands.EnumerateHeld(actor))
            {
                if (!Ok(held))
                    continue;
                origin.Item = held;
                origin.FromHand = true;
                return held;
            }
        }

        if (!_inventory.TryGetContainerSlotEnumerator(actor, out var slots))
            return null;

        // Worn first (e.g. the gun on the suit storage), then what is inside the storages.
        var worn = new List<(EntityUid Item, string Slot)>();
        while (slots.NextItem(out var wornItem, out var slot))
        {
            if (from == null || from.Contains(slot.Name))
                worn.Add((wornItem, slot.Name));
        }

        foreach (var (wornItem, slot) in worn)
        {
            if (!Ok(wornItem))
                continue;
            origin.Item = wornItem;
            origin.Slot = slot;
            return wornItem;
        }

        foreach (var (wornItem, _) in worn)
        {
            if (FindStored(wornItem, Ok, 0) is not { } stored)
                continue;
            origin.Item = stored;
            if (_container.TryGetContainingContainer((stored, null, null), out var container))
            {
                origin.ContainerOwner = container.Owner;
                origin.ContainerId = container.ID;
            }
            return stored;
        }

        return null;
    }

    private EntityUid? FindStored(EntityUid storage, Func<EntityUid, bool> ok, int depth)
    {
        if (depth > 3 || !TryComp<StorageComponent>(storage, out var comp))
            return null;

        foreach (var inner in comp.Container.ContainedEntities)
        {
            if (ok(inner))
                return inner;
            if (FindStored(inner, ok, depth + 1) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="actor"/> carries every required item of role <paramref name="role"/> in <paramref name="scene"/>
    /// (e.g. to only pick a finisher variant the attacker has the gear for).
    /// </summary>
    public bool HasRequiredItems(string scene, int role, EntityUid actor)
    {
        if (!_proto.TryIndex<ProcAnimScenePrototype>(scene, out var proto) || role < 0 || role >= proto.Actors.Count)
            return false;

        var used = new List<EntityUid>();
        foreach (var item in proto.Actors[role].Items)
        {
            if (FindItem(actor, item, used) is { } found)
                used.Add(found);
            else if (item.Required)
                return false;
        }

        return true;
    }

    /// <summary>A planned pre-beat reach: put <see cref="Item"/> away from <see cref="Hand"/>, or draw it into it.</summary>
    private sealed class HandOp
    {
        public EntityUid Item;
        public string Hand = "";
        public bool Draw;
        public ProcAnimBoundItem? Bound;
    }

    private readonly Dictionary<EntityUid, List<HandOp>> _handOps = new();

    /// <summary>
    /// Binds the items of every actor and plans the stow / draw reaches. Fails (changing nothing) when a required item
    /// is missing.
    /// </summary>
    private bool PlanItems(ProcAnimScenePrototype proto, IReadOnlyList<EntityUid> actors,
        out List<EntityUid> items, out List<ProcAnimBoundItem>[] bound, out List<HandOp>[] ops, out bool[] heldAfter)
    {
        items = Enumerable.Repeat(EntityUid.Invalid, proto.Items.Count).ToList();
        bound = new List<ProcAnimBoundItem>[actors.Count];
        ops = new List<HandOp>[actors.Count];
        heldAfter = new bool[actors.Count];
        var used = new List<EntityUid>();
        for (var i = 0; i < actors.Count; i++)
        {
            var uid = actors[i];
            var tr = proto.Actors[i];
            bound[i] = new List<ProcAnimBoundItem>();
            ops[i] = new List<HandOp>();
            foreach (var item in tr.Items)
            {
                if (FindItem(uid, item, used, out var origin) is { } found)
                {
                    used.Add(found);
                    items[item.Index] = found;
                    bound[i].Add(origin);
                }
                else if (item.Required)
                {
                    return false;
                }
            }

            // Hand contents after the pre-beat (hand id -> item), to plan which hand each draw uses.
            var hands = new Dictionary<string, EntityUid?>();
            foreach (var hand in _hands.EnumerateHands(uid))
                hands[hand] = _hands.GetHeldItem(uid, hand);

            if (tr.StowHands is { } stow)
            {
                foreach (var (hand, held) in hands.ToList())
                {
                    if (held is not { } h || HasComp<VirtualItemComponent>(h))
                        continue;
                    var b = bound[i].FirstOrDefault(x => x.Item == h);
                    // Kept: matched by keep, or bound and used from the start (it already is where it must be).
                    if (stow.Keep.Any(k => Matches(h, k)) || b != null && tr.Items.First(x => x.Index == b.Index).Draw == null)
                        continue;
                    ops[i].Add(new HandOp { Item = h, Hand = hand });
                    hands[hand] = null;
                    // A wielded item's virtual copies free up too.
                    foreach (var (other, virt) in hands.ToList())
                    {
                        if (virt is { } v && TryComp<VirtualItemComponent>(v, out var virtComp) && virtComp.BlockingEntity == h)
                            hands[other] = null;
                    }
                }
            }

            foreach (var b in bound[i])
            {
                var item = tr.Items.First(x => x.Index == b.Index);
                if (item.Draw != null || b.FromHand)
                    continue;
                var free = hands.FirstOrDefault(x => x.Value == null).Key;
                if (free == null)
                {
                    // Unwield to free a hand.
                    free = hands.FirstOrDefault(x => x.Value is { } v && HasComp<VirtualItemComponent>(v)).Key;
                    if (free == null)
                        continue;
                }
                ops[i].Add(new HandOp { Item = b.Item, Hand = free, Draw = true, Bound = b });
                hands[free] = b.Item;
            }

            heldAfter[i] = hands.Values.Any(v => v is { } e && !HasComp<VirtualItemComponent>(e));
        }

        return true;
    }

    private bool IsRightHand(EntityUid uid, string hand)
        => _hands.TryGetHand(uid, hand, out var h) && h.Value.Location == HandLocation.Right;

    private void SetupItems(EntityUid uid, ProcAnimComponent comp, List<EntityUid> items, List<ProcAnimBoundItem> bound,
        List<HandOp> ops)
    {
        comp.Items = items.ToList();
        comp.Bound = bound;
        comp.Stowed.Clear();
        comp.StowRight = ops.Select(o => IsRightHand(uid, o.Hand)).ToList();
        comp.NextStow = 0;
        _handOps[uid] = ops;
    }

    /// <summary>Runs the pre-beat reaches that are due and the timed draws / returns.</summary>
    private void UpdateItems(EntityUid uid, ProcAnimComponent comp, ProcAnimScenePrototype proto, float real, float anim)
    {
        var tr = proto.Actors[comp.Actor];
        var stow = tr.StowHands ?? DefaultStow;
        if (_handOps.TryGetValue(uid, out var ops))
        {
            var pre = real + comp.Intro + comp.PreBeat;
            while (comp.NextStow < ops.Count && (pre >= comp.NextStow * stow.Duration + stow.Grab || real >= -comp.Intro))
            {
                var op = ops[comp.NextStow++];
                if (op.Draw)
                    Draw(uid, tr, op.Bound!, op.Hand);
                else
                    StowHeld(uid, comp, stow, op.Item, op.Hand);
                if (stow.Sound != null)
                    _audio.PlayPvs(stow.Sound, uid, AudioParams.Default.WithVolume(-4f));
            }
        }

        if (real < -comp.Intro)
            return;

        foreach (var b in comp.Bound)
        {
            var item = tr.Items.First(x => x.Index == b.Index);
            if (!b.Drawn && !b.Returned && item.Draw is { } draw && anim >= draw)
                Draw(uid, tr, b, null);
            else if (b.Drawn && !b.Returned && item.Return is { } ret && anim >= ret)
                Return(uid, tr, b);
        }
    }

    private void Draw(EntityUid uid, ProcAnimActor tr, ProcAnimBoundItem b, string? preferred)
    {
        var item = tr.Items.First(x => x.Index == b.Index);
        b.Drawn = true;
        if (TerminatingOrDeleted(b.Item))
            return;

        // Taken from where it is now (it may have been stowed by the pre-beat since it was found).
        RecordOrigin(uid, b);
        if (preferred == null)
            preferred = PreferredHand(uid, tr, item);
        if (!TakeIntoHand(uid, b.Item, preferred, b.Unwielded))
            return;

        if (item.Wield && TryComp<WieldableComponent>(b.Item, out var wield) && !wield.Wielded)
            _wieldable.TryWield(b.Item, wield, uid, showMessage: false);
    }

    /// <summary>The hand a prop of the item is drawn on (near / far arm on the side the actor faces), if any is free.</summary>
    private string? PreferredHand(EntityUid uid, ProcAnimActor tr, ProcAnimItem item)
    {
        if (!TryComp<ProcAnimComponent>(uid, out var comp))
            return null;
        var prop = Proto(comp)?.Actors.SelectMany(a => a.Props).FirstOrDefault(p => p.ItemIndex == item.Index);
        if (prop == null)
            return null;
        var far = prop.Part is ProcAnimBone.ArmFar or ProcAnimBone.ArmFarLow or ProcAnimBone.ArmFarHand;
        var near = prop.Part is ProcAnimBone.ArmNear or ProcAnimBone.ArmNearLow or ProcAnimBone.ArmNearHand;
        if (!far && !near)
            return null;
        var screen = (-comp.View).RotateVec(comp.Forward);
        var sx = screen.X < -0.3f ? -1f : 1f;
        var facingRight = tr.Facing >= 0f ? sx > 0 : sx < 0;
        var wantRight = near == facingRight;
        return _hands.EnumerateHands(uid).FirstOrDefault(h => IsRightHand(uid, h) == wantRight);
    }

    private ProcAnimScenePrototype? Proto(ProcAnimComponent comp)
        => _proto.TryIndex(comp.Scene, out var proto) ? proto : null;

    private void RecordOrigin(EntityUid uid, ProcAnimBoundItem b)
    {
        b.Slot = null;
        b.ContainerOwner = null;
        b.ContainerId = null;
        b.FromHand = _hands.IsHolding(uid, b.Item);
        if (b.FromHand || !_container.TryGetContainingContainer((b.Item, null, null), out var container))
            return;
        if (container.Owner == uid && _inventory.HasSlot(uid, container.ID))
            b.Slot = container.ID;
        else
        {
            b.ContainerOwner = container.Owner;
            b.ContainerId = container.ID;
        }
    }

    private bool TakeIntoHand(EntityUid uid, EntityUid item, string? preferred, List<EntityUid>? unwielded)
    {
        if (_hands.IsHolding(uid, item))
            return true;

        ItemOps = true;
        try
        {
            string? hand = preferred != null && _hands.HandIsEmpty(uid, preferred) ? preferred : null;
            if (hand == null && preferred != null && _hands.GetHeldItem(uid, preferred) is { } blocking
                && TryComp<VirtualItemComponent>(blocking, out var virt))
            {
                // The wanted hand only holds the other end of a wielded item: let go of that end.
                Unwield(uid, virt.BlockingEntity, unwielded);
                hand = _hands.HandIsEmpty(uid, preferred) ? preferred : null;
            }

            if (hand == null && !_hands.TryGetEmptyHand(uid, out hand))
            {
                foreach (var held in _hands.EnumerateHeld(uid).ToList())
                    Unwield(uid, held, unwielded);
                if (!_hands.TryGetEmptyHand(uid, out hand))
                    return false;
            }

            if (_container.TryGetContainingContainer((item, null, null), out var slot) && slot.Owner == uid
                && _inventory.HasSlot(uid, slot.ID)
                && !_inventory.TryUnequip(uid, uid, slot.ID, silent: true, force: true))
                return false;

            return _hands.TryPickup(uid, item, hand, checkActionBlocker: false, animate: false);
        }
        finally
        {
            ItemOps = false;
        }
    }

    private void Unwield(EntityUid uid, EntityUid item, List<EntityUid>? unwielded)
    {
        if (!TryComp<WieldableComponent>(item, out var wield) || !wield.Wielded)
            return;
        if (_wieldable.TryUnwield(item, wield, uid, force: true))
            unwielded?.Add(item);
    }

    private void Return(EntityUid uid, ProcAnimActor tr, ProcAnimBoundItem b)
    {
        b.Returned = true;
        if (TerminatingOrDeleted(b.Item) || !_hands.IsHolding(uid, b.Item))
            return;

        ItemOps = true;
        try
        {
            var back = b.FromHand;
            if (!back && b.Slot != null)
                back = _inventory.TryEquip(uid, uid, b.Item, b.Slot, silent: true, force: true);
            if (!back && b.ContainerOwner is { } owner && Exists(owner) && b.ContainerId != null)
            {
                if (TryComp<StorageComponent>(owner, out var storage) && storage.Container.ID == b.ContainerId)
                    back = _storage.Insert(owner, b.Item, out _, storageComp: storage, playSound: false);
                else if (_container.TryGetContainer(owner, b.ContainerId, out var container))
                    back = _container.Insert(b.Item, container);
            }

            // Somewhere sensible, else it stays in hand.
            if (!back)
                TryStow(uid, tr.StowHands ?? DefaultStow, b.Item);
        }
        finally
        {
            ItemOps = false;
        }

        foreach (var w in b.Unwielded)
        {
            if (!TerminatingOrDeleted(w) && _hands.IsHolding(uid, w) && TryComp<WieldableComponent>(w, out var wield) && !wield.Wielded)
                _wieldable.TryWield(w, wield, uid, showMessage: false);
        }
        b.Unwielded.Clear();
    }

    private void StowHeld(EntityUid uid, ProcAnimComponent comp, ProcAnimStow stow, EntityUid item, string hand)
    {
        if (TerminatingOrDeleted(item) || !_hands.IsHolding(uid, item))
            return;

        var wasActive = _toggle.IsActivated(item);
        ItemOps = true;
        try
        {
            if (TryComp<WieldableComponent>(item, out var wield) && wield.Wielded)
                _wieldable.TryUnwield(item, wield, uid, force: true);
            if (wasActive)
                _toggle.TryDeactivate(item, uid, predicted: false);

            if (!TryStow(uid, stow, item))
                _hands.TryDrop(uid, item, checkActionBlocker: false); // Last resort: at the feet.
        }
        finally
        {
            ItemOps = false;
        }

        comp.Stowed.Add(new ProcAnimStowedItem { Item = item, WasActive = wasActive, Hand = hand });
    }

    /// <summary>Puts a held item away following <paramref name="stow"/>'s rules / slots; false if nothing took it.</summary>
    private bool TryStow(EntityUid uid, ProcAnimStow stow, EntityUid item)
    {
        var slots = stow.Rules.FirstOrDefault(r => Matches(item, r.Match))?.Slots ?? stow.Slots;
        foreach (var slot in slots)
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out var worn))
            {
                if (TryComp<StorageComponent>(worn, out var storage)
                    && _storage.Insert(worn.Value, item, out _, storageComp: storage, playSound: false))
                    return true;
                continue;
            }

            if (_inventory.HasSlot(uid, slot) && _inventory.TryEquip(uid, uid, item, slot, silent: true))
                return true;
        }

        return false;
    }

    /// <summary>End of the scene: bound items not back yet go back, stowed ones return to the hands (when restoring).</summary>
    private void FinishItems(EntityUid uid, ProcAnimComponent comp, ProcAnimActor? tr)
    {
        _handOps.Remove(uid);
        if (tr == null || TerminatingOrDeleted(uid))
            return;

        foreach (var b in comp.Bound)
        {
            if (b.Drawn && !b.Returned)
                Return(uid, tr, b);
        }

        if (tr.StowHands is not { Restore: true })
            return;

        foreach (var s in comp.Stowed)
        {
            if (TerminatingOrDeleted(s.Item) || _hands.IsHolding(uid, s.Item))
                continue;
            // Still on him (or dropped right at his feet).
            if (!IsCarriedBy(s.Item, uid) && !(Transform(s.Item).ParentUid == Transform(uid).ParentUid
                                               && _transform.InRange(Transform(s.Item).Coordinates, Transform(uid).Coordinates, 1.5f)))
                continue;
            if (TakeIntoHand(uid, s.Item, s.Hand, null) && s.WasActive)
                _toggle.TryActivate(s.Item, uid, predicted: false);
        }
    }

    private bool IsCarriedBy(EntityUid item, EntityUid uid)
    {
        var parent = Transform(item).ParentUid;
        for (var i = 0; i < 8 && parent.IsValid(); i++)
        {
            if (parent == uid)
                return true;
            parent = Transform(parent).ParentUid;
        }

        return false;
    }
}

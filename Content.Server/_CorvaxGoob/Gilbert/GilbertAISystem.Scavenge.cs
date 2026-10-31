using System.Linq;
using Content.Shared._Goobstation.Heretic.Components;
using Content.Server.NPC.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Cuffs.Components;
using Content.Shared.Explosion.Components;
using Content.Shared.Humanoid;
using Content.Shared.Medical.Healing;
using Content.Shared.Storage;
using Content.Shared.Strip.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Whitelist;
using Content.Shared.Wieldable.Components;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.DoAfter;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Robust.Shared.Audio;
using Robust.Shared.Containers;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// Scavenging: 8-gauge shells he sees go into his not-full shell boxes; grenades (any kind), cuffs and meds
/// are picked up. Cuffed / crit / dead people next to him are searched: a cuffed one is knocked to the floor, frisked
/// (do-after), then looted through the normal strip do-afters, backpack included; 2 minutes cooldown per person.
/// </summary>
public sealed partial class GilbertAISystem
{
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    public bool IsCuffed(EntityUid uid)
    {
        return TryComp<CuffableComponent>(uid, out var cuffable) && cuffable.CuffedHandCount > 0;
    }

    private static readonly string[] LootSlots = { "pocket1", "pocket2", "suitstorage", "belt" };
    private const string BackSlot = "back";
    private static readonly SoundSpecifier FriskKnockdownSound = new SoundPathSpecifier("/Audio/Effects/thudswoosh.ogg");

    private bool TryStartScavenge(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.Task != GilbertTask.None || now < comp.NextScavenge)
            return false;

        comp.NextScavenge = now + comp.ScavengeInterval;
        PruneScavengeIgnore(comp, now);

        if (TryFindLoot(ent, xform, now, out var victim))
        {
            comp.LootSlot = null;
            comp.LootItem = null;
            comp.LootBag = null;
            comp.FriskDone = false;
            // Item = victim too: the generic task code aborts when the item disappears.
            StartTask(ent, GilbertTask.Loot, victim, victim, comp.FriskTaskTimeout);
            return true;
        }

        if (TryFindPickup(ent, xform, out var pickup))
        {
            StartTask(ent, GilbertTask.Pickup, pickup, null, TimeSpan.FromSeconds(15));
            return true;
        }

        return false;
    }

    private void PruneScavengeIgnore(GilbertAIComponent comp, TimeSpan now)
    {
        if (comp.ScavengeIgnore.Count == 0)
            return;

        foreach (var (uid, until) in comp.ScavengeIgnore.ToList())
        {
            if (until < now || TerminatingOrDeleted(uid))
                comp.ScavengeIgnore.Remove(uid);
        }
    }

    private void IgnoreForScavenge(GilbertAIComponent comp, EntityUid item)
    {
        comp.ScavengeIgnore[item] = _timing.CurTime + comp.ScavengeIgnoreTime;
    }

    #region What he wants

    /// <summary>Is this item worth taking at all (see <see cref="LootPriority"/>).</summary>
    public bool IsWantedLoot(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        return LootPriority(ent, item) > 0;
    }

    /// <summary>
    /// v9 whitelist with priorities (0 = leave it): his own weapons 100, 8-gauge shells for a box with room 80,
    /// shell boxes 70, medicine he'd actually use 60, first-aid kits holding such medicine 55, grenades 50, cuffs 40.
    /// Anything else - cable coils, tools, trash, guns that aren't his - stays where it is.
    /// </summary>
    public int LootPriority(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        if (TerminatingOrDeleted(item) || ent.Comp.ScavengeIgnore.ContainsKey(item))
            return 0;

        if (ent.Comp.OwnGear.ContainsKey(item))
            return IsOnHim(ent, item) ? 0 : 100;

        if (HasComp<GunComponent>(item))
            return 0;

        if (IsHeavyShell(item))
            return TryFindBoxWithRoom(ent, item, out _) ? 80 : 0;

        if (IsShotgunBox(item))
            return 70;

        if (IsMedicalLoot(ent.Comp, item, ent))
            return 60;

        if (IsMedkitWithMeds(ent, item))
            return 55;

        if (IsGrenade(item))
            return HasComp<ActiveTimerTriggerComponent>(item) ? 0 : 50;

        if (HasComp<HandcuffComponent>(item))
            return CountStored(ent, e => HasComp<HandcuffComponent>(e)) < ent.Comp.MaxCuffs ? 40 : 0;

        return 0;
    }

    public bool IsHeavyShell(EntityUid item)
    {
        return TryComp<CartridgeAmmoComponent>(item, out var cartridge) && !cartridge.Spent
               && _tag.HasTag(item, "ShellShotgunHeavy");
    }

    /// <summary>One of his own shell boxes that accepts this shell and isn't full.</summary>
    public bool TryFindBoxWithRoom(EntityUid uid, EntityUid shell, out EntityUid box)
    {
        return TryFindStored(uid, e =>
                !HasComp<GunComponent>(e)
                && TryComp<BallisticAmmoProviderComponent>(e, out var b)
                && MetaData(e).EntityPrototype?.ID.StartsWith("MagazineBoxShotgun") == true
                && b.Count < b.Capacity
                && _whitelist.IsWhitelistPassOrNull(b.Whitelist, shell),
            out box);
    }

    /// <summary>v9: consumable medicine that treats something a body like his can have (not cable coils...).</summary>
    private bool IsMedicalLoot(GilbertAIComponent comp, EntityUid item, EntityUid uid)
    {
        return IsConsumableHealItem(comp, item) && IsUsefulMedicine(uid, item);
    }

    private int CountStored(EntityUid uid, Func<EntityUid, bool> predicate)
    {
        var count = 0;
        foreach (var item in EnumerateStored(uid))
        {
            if (predicate(item))
                count++;
        }

        return count;
    }

    private bool CanStow(EntityUid uid, EntityUid item)
    {
        foreach (var slot in new[] { "back", "belt" })
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out var storage)
                && TryComp<StorageComponent>(storage, out var storageComp)
                && _storage.CanInsert(storage.Value, item, out _, storageComp))
                return true;
        }

        return false;
    }

    #endregion

    #region Finding

    private bool TryFindPickup(Entity<GilbertAIComponent> ent, TransformComponent xform, out EntityUid found)
    {
        found = default;
        var best = float.MaxValue;
        foreach (var item in _lookup.GetEntitiesInRange(xform.Coordinates, ent.Comp.ScavengeRange, LookupFlags.Dynamic | LookupFlags.Sundries))
        {
            if (item == ent.Owner || Transform(item).Anchored || _container.IsEntityOrParentInContainer(item))
                continue;

            if (!IsWantedLoot(ent, item))
                continue;

            if (!IsHeavyShell(item) && !CanStow(ent, item) && !CanStowMedicine(ent, item)
                && !IsMedkitWithMeds(ent, item) && !ent.Comp.OwnGear.ContainsKey(item))
                continue;

            var dist = DistanceTo(ent, item);
            if (dist >= best || !CanSee(ent, item, ent.Comp, xform))
                continue;

            best = dist;
            found = item;
        }

        return found != default;
    }

    /// <summary>Only people who can't resist: cuffed, in crit or dead.</summary>
    public bool IsLootable(EntityUid uid, EntityUid victim)
    {
        if (victim == uid || HasComp<GilbertAIComponent>(victim) || !HasComp<StrippableComponent>(victim))
            return false;

        var cuffed = TryComp<CuffableComponent>(victim, out var cuffable) && cuffable.CuffedHandCount > 0;
        return cuffed || _mobState.IsIncapacitated(victim);
    }

    /// <summary>
    /// A visible cuffed / crit / dead person who has something he wants on them (hands, pockets, suit storage,
    /// belt or in the backpack) and hasn't been searched in the last <see cref="GilbertAIComponent.FriskCooldown"/>.
    /// </summary>
    private bool TryFindLoot(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, out EntityUid victim)
    {
        victim = default;
        PruneFriskCooldowns(ent.Comp, now);

        foreach (var other in _lookup.GetEntitiesInRange<HumanoidAppearanceComponent>(xform.Coordinates, ent.Comp.ScavengeRange))
        {
            if (!IsLootable(ent, other) || IsFriskOnCooldown(ent.Comp, other, now) || !CanSee(ent, other, ent.Comp, xform))
                continue;

            if (TryFindLootSlot(ent, other, out _, out _, out _) || TryFindLootBag(ent, other, out _))
            {
                victim = other;
                return true;
            }
        }

        return false;
    }

    public bool IsFriskOnCooldown(GilbertAIComponent comp, EntityUid victim, TimeSpan now)
    {
        return comp.FriskCooldowns.TryGetValue(victim, out var until) && now < until;
    }

    private void PruneFriskCooldowns(GilbertAIComponent comp, TimeSpan now)
    {
        if (comp.FriskCooldowns.Count == 0)
            return;

        foreach (var (uid, until) in comp.FriskCooldowns.ToList())
        {
            if (now >= until || TerminatingOrDeleted(uid))
                comp.FriskCooldowns.Remove(uid);
        }
    }

    /// <summary>The backpack on their back, if it holds anything he wants.</summary>
    private bool TryFindLootBag(Entity<GilbertAIComponent> ent, EntityUid victim, out EntityUid bag)
    {
        if (_inventory.TryGetSlotEntity(victim, BackSlot, out var worn) && !HasComp<StripMenuInvisibleComponent>(worn)
            && !ent.Comp.ScavengeIgnore.ContainsKey(worn.Value) && TryFindWantedInBag(ent, worn.Value, out _))
        {
            bag = worn.Value;
            return true;
        }

        bag = default;
        return false;
    }

    /// <summary>Something he wants in this bag (top level of its storage).</summary>
    private bool TryFindWantedInBag(Entity<GilbertAIComponent> ent, EntityUid bag, out EntityUid item)
    {
        item = default;
        var best = 0;
        if (TryComp<StorageComponent>(bag, out var storage))
        {
            foreach (var stored in storage.Container.ContainedEntities)
            {
                var priority = LootPriority(ent, stored);
                if (priority > best)
                {
                    best = priority;
                    item = stored;
                }
            }
        }

        return best > 0;
    }

    /// <summary>v9: the most wanted thing in their hands / pockets / suit storage / belt (own weapons first).</summary>
    private bool TryFindLootSlot(Entity<GilbertAIComponent> ent, EntityUid victim, out EntityUid item, out string slot, out bool isHand)
    {
        item = default;
        slot = string.Empty;
        isHand = false;
        var best = 0;

        foreach (var hand in _hands.EnumerateHands(victim))
        {
            if (_hands.GetHeldItem(victim, hand) is { } held && LootPriority(ent, held) is var priority && priority > best)
            {
                best = priority;
                item = held;
                slot = hand;
                isHand = true;
            }
        }

        foreach (var invSlot in LootSlots)
        {
            if (_inventory.TryGetSlotEntity(victim, invSlot, out var worn) && !HasComp<StripMenuInvisibleComponent>(worn)
                && LootPriority(ent, worn.Value) is var priority && priority > best)
            {
                best = priority;
                item = worn.Value;
                slot = invSlot;
                isHand = false;
            }
        }

        return best > 0;
    }

    #endregion

    #region Tasks

    private void UpdatePickup(Entity<GilbertAIComponent> ent, EntityUid item, TimeSpan now)
    {
        var comp = ent.Comp;
        switch (comp.TaskStep)
        {
            case 0:
                if (_container.IsEntityOrParentInContainer(item) || !IsWantedLoot(ent, item))
                {
                    comp.TaskItem = null;
                    FinishTask(ent);
                    return;
                }

                if (DistanceTo(ent, item) > 1.2f || !_interaction.InRangeUnobstructed(ent.Owner, item, 1.5f))
                {
                    if (TryComp<NPCSteeringComponent>(ent, out var steering) && steering.Status == SteeringStatus.NoPath)
                    {
                        IgnoreForScavenge(comp, item);
                        comp.TaskItem = null;
                        FinishTask(ent);
                        return;
                    }

                    RegisterMove(ent, Transform(item).Coordinates, 0.8f);
                    return;
                }

                _steering.Unregister(ent);
                if (!TakeIntoFreeHand(ent, item))
                {
                    IgnoreForScavenge(comp, item);
                    comp.TaskItem = null;
                    FinishTask(ent);
                    return;
                }

                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                return;

            case 1:
                PutAwayLoot(ent, item);
                return;
        }
    }

    /// <summary>
    /// Searching a person, steps:
    /// 0 - walk up; a cuffed person still on their feet gets knocked to the floor first;
    /// 1 - the visible frisk do-after;
    /// 2 - waiting for it;
    /// 3 - pick the next thing: an item from their dropped backpack, a wanted item from hands / pockets / suit
    ///     storage / belt (normal strip do-after), or the backpack itself (stripped, then dropped at his feet);
    /// 4 - waiting for the strip do-after.
    /// The person can't be searched again for <see cref="GilbertAIComponent.FriskCooldown"/> once the frisk began.
    /// </summary>
    private void UpdateLoot(Entity<GilbertAIComponent> ent, EntityUid victim, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskTarget != victim || TerminatingOrDeleted(victim))
        {
            AbortTask(ent);
            return;
        }

        var inReach = DistanceTo(ent, victim) <= comp.LootRange && _interaction.InRangeUnobstructed(ent.Owner, victim, comp.LootRange);

        switch (comp.TaskStep)
        {
            case 0:
                if (!IsLootable(ent, victim))
                {
                    EndFrisk(ent);
                    return;
                }

                if (!inReach)
                {
                    if (TryComp<NPCSteeringComponent>(ent, out var steering) && steering.Status == SteeringStatus.NoPath)
                    {
                        // Can't get there: try again some other time.
                        comp.FriskCooldowns[victim] = now + TimeSpan.FromSeconds(30);
                        EndFrisk(ent);
                        return;
                    }

                    RegisterMove(ent, Transform(victim).Coordinates, 1f);
                    return;
                }

                _steering.Unregister(ent);
                FaceTowards(ent, victim);
                comp.TaskStep = 1;
                if (KnockDownForSearch(ent, victim, true))
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.6);
                return;

            case 1:
            {
                if (!inReach)
                {
                    comp.TaskStep = 0;
                    return;
                }

                FaceTowards(ent, victim);
                comp.FriskCooldowns[victim] = now + comp.FriskCooldown;
                comp.FriskDone = false;
                var args = new DoAfterArgs(EntityManager, ent, comp.FriskTime, new GilbertFriskDoAfterEvent(), ent, target: victim)
                {
                    BreakOnMove = true,
                    NeedHand = false,
                    DistanceThreshold = comp.LootRange + 0.5f,
                };

                if (!_doAfter.TryStartDoAfter(args))
                {
                    EndFrisk(ent);
                    return;
                }

                _popup.PopupEntity(Loc.GetString("gilbert-frisk-start",
                        ("user", Identity.Entity(ent, EntityManager)), ("target", Identity.Entity(victim, EntityManager))),
                    victim, PopupType.Medium);
                comp.TaskStep = 2;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                return;
            }

            case 2:
                if (HasActiveDoAfter(ent))
                    return;

                if (!comp.FriskDone)
                {
                    // Interrupted (they got dragged away...): the cooldown still applies.
                    EndFrisk(ent);
                    return;
                }

                comp.TaskStep = 3;
                return;

            case 3:
                if (!inReach)
                {
                    EndFrisk(ent);
                    return;
                }

                FaceTowards(ent, victim);

                // Their backpack is on the floor next to him: take the wanted things out one by one.
                if (comp.LootBag is { } bag && !TerminatingOrDeleted(bag) && !_container.IsEntityOrParentInContainer(bag)
                    && DistanceTo(ent, bag) <= 1.5f)
                {
                    if (TryFindWantedInBag(ent, bag, out var fromBag))
                    {
                        if (TakeIntoFreeHand(ent, fromBag))
                            StashLoot(ent, fromBag);
                        else
                            IgnoreForScavenge(comp, fromBag);

                        comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                        return;
                    }

                    // Emptied of everything useful: it stays on the floor.
                    IgnoreForScavenge(comp, bag);
                    comp.LootBag = null;
                }

                if (!IsLootable(ent, victim))
                {
                    EndFrisk(ent);
                    return;
                }

                // Getting up while being searched: back on the floor.
                KnockDownForSearch(ent, victim, false);

                string slot;
                bool isHand;
                EntityUid item;
                if (TryFindLootSlot(ent, victim, out item, out slot, out isHand))
                {
                }
                else if (TryFindLootBag(ent, victim, out item))
                {
                    slot = BackSlot;
                    isHand = false;
                }
                else
                {
                    EndFrisk(ent);
                    return;
                }

                // The stripped item lands in his active hand: it has to be empty.
                if (GetGun(ent, out var gun, out _) && TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
                    _wieldable.TryUnwield(gun, wield, ent, force: true);

                if (!_hands.TryGetEmptyHand(ent.Owner, out var hand))
                {
                    EndFrisk(ent);
                    return;
                }

                _hands.SetActiveHand(ent.Owner, hand);
                comp.LootItem = item;
                comp.LootSlot = slot;
                comp.LootIsHand = isHand;

                // Same message the strip menu sends: normal strip do-after, popups and logs.
                var msg = new StrippingSlotButtonPressed(slot, isHand)
                {
                    Actor = ent.Owner,
                    UiKey = StrippingUiKey.Key,
                };
                RaiseLocalEvent(victim, (object) msg, true);

                comp.TaskStep = 4;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                return;

            case 4:
                if (HasActiveDoAfter(ent))
                    return;

                comp.TaskStep = 3;
                if (comp.LootItem is not { } taken || TerminatingOrDeleted(taken))
                    return;

                comp.LootItem = null;
                if (!_hands.IsHolding(ent.Owner, taken))
                {
                    // Interrupted or refused: don't keep trying the same thing.
                    IgnoreForScavenge(comp, taken);
                    return;
                }

                if (comp.LootSlot == BackSlot)
                {
                    // The backpack goes on the floor at his feet; he empties it there.
                    _hands.TryDrop(ent.Owner, taken);
                    comp.LootBag = taken;
                    return;
                }

                StashLoot(ent, taken);
                return;
        }
    }

    /// <summary>
    /// A cuffed person still on their feet is knocked to the floor (on the first approach with a popup and a thud).
    /// Returns true if they were knocked down.
    /// </summary>
    private bool KnockDownForSearch(Entity<GilbertAIComponent> ent, EntityUid victim, bool announce)
    {
        if (!IsCuffed(victim) || _standing.IsDown(victim))
            return false;

        if (!_stun.TryKnockdown(victim, ent.Comp.FriskKnockdownTime, true))
            return false;

        FaceTowards(ent, victim);
        _audio.PlayPvs(FriskKnockdownSound, victim);
        if (announce)
        {
            _popup.PopupEntity(Loc.GetString("gilbert-frisk-knockdown",
                    ("user", Identity.Entity(ent, EntityManager)), ("target", Identity.Entity(victim, EntityManager))),
                victim, PopupType.MediumCaution);
        }

        return true;
    }

    private void OnFriskDoAfter(Entity<GilbertAIComponent> ent, ref GilbertFriskDoAfterEvent args)
    {
        if (args.Cancelled || args.Handled)
            return;

        args.Handled = true;
        ent.Comp.FriskDone = true;
    }

    /// <summary>Shells into a box with room, everything else into his bag (or at his feet); the search goes on.</summary>
    private void StashLoot(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        if (!_hands.IsHolding(ent.Owner, item))
            return;

        StoreLoot(ent, item);
    }

    /// <summary>
    /// v9: where a looted thing goes. His own weapon: back to its home slot. Shells: into a box. Medicine: into one of
    /// his first-aid kits if there's room. A first-aid kit: its useful medicine goes into his kits, then the kit
    /// itself into the bag if it still holds something useful (otherwise it is dropped). The rest: the bag.
    /// </summary>
    private void StoreLoot(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        if (ent.Comp.OwnGear.ContainsKey(item))
        {
            ReturnGear(ent, item);
            ent.Comp.Recovered++;
            return;
        }

        if (IsHeavyShell(item) && TryFindBoxWithRoom(ent, item, out var box))
            _interaction.InteractUsing(ent, item, box, Transform(box).Coordinates, checkCanInteract: false, checkCanUse: false);

        if (_hands.IsHolding(ent.Owner, item) && IsUsefulMedicine(ent, item))
            TryPutInMedkit(ent, item);

        if (_hands.IsHolding(ent.Owner, item) && IsMedkit(item) && TryComp<StorageComponent>(item, out var kit))
        {
            foreach (var med in kit.Container.ContainedEntities.ToList())
            {
                if (IsMedicalLoot(ent.Comp, med, ent))
                    TryPutInMedkit(ent, med);
            }

            if (!IsMedkitWithMeds(ent, item))
            {
                _hands.TryDrop(ent.Owner, item);
                IgnoreForScavenge(ent.Comp, item);
                return;
            }
        }

        if (_hands.IsHolding(ent.Owner, item))
            StowOrDrop(ent, item);

        if (!_container.IsEntityOrParentInContainer(item))
            IgnoreForScavenge(ent.Comp, item);
    }

    /// <summary>His first-aid kits (the combat ones first).</summary>
    private IEnumerable<EntityUid> OwnMedkits(EntityUid uid)
    {
        return EnumerateStored(uid).Where(IsMedkit)
            .OrderByDescending(e => MetaData(e).EntityPrototype?.ID.StartsWith("MedkitCombat") == true);
    }

    /// <summary>v9: into one of his first-aid kits, if one has room.</summary>
    public bool TryPutInMedkit(EntityUid uid, EntityUid item)
    {
        foreach (var kit in OwnMedkits(uid).ToList())
        {
            if (kit == item || !TryComp<StorageComponent>(kit, out var storage) || !_storage.CanInsert(kit, item, out _, storage))
                continue;

            if (_storage.Insert(kit, item, out _, user: uid, storageComp: storage, playSound: false))
                return true;
        }

        return false;
    }

    private bool CanStowMedicine(EntityUid uid, EntityUid item)
    {
        if (!TryComp<GilbertAIComponent>(uid, out var comp) || !IsMedicalLoot(comp, item, uid))
            return false;

        foreach (var kit in OwnMedkits(uid))
        {
            if (TryComp<StorageComponent>(kit, out var storage) && _storage.CanInsert(kit, item, out _, storage))
                return true;
        }

        return false;
    }

    private void EndFrisk(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.LootItem is { } held && _hands.IsHolding(ent.Owner, held))
            StowOrDrop(ent, held);

        comp.LootItem = null;
        comp.LootBag = null;
        comp.LootSlot = null;
        comp.FriskDone = false;
        comp.TaskItem = null;
        FinishTask(ent);
        if (GetGun(ent, out var gun, out _))
            EnsureWielded(ent, gun);
    }

    /// <summary>Shells into a box with room, everything else into the bag; back to holding the shotgun.</summary>
    private void PutAwayLoot(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        var comp = ent.Comp;
        if (_hands.IsHolding(ent.Owner, item))
            StoreLoot(ent, item);

        comp.TaskItem = null;
        FinishTask(ent);
        if (GetGun(ent, out var gun, out _))
            EnsureWielded(ent, gun);
    }

    #endregion
}

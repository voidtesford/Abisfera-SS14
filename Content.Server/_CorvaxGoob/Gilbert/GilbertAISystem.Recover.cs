using System.Linq;
using Content.Server.NPC.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Storage;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v9: his own weapons. The shotgun, truncheon and shield he spawned with are remembered (by entity, together with
/// where each one was: the shotgun in his hands, the truncheon / shield in the webbing or the bag). One that ends up
/// lying around (disarmed, dropped) and that he sees is walked to, picked up and put back where it belongs. This
/// beats looting and patrols; in a fight only when it is right next to him or the enemy is out of sight.
/// </summary>
public sealed partial class GilbertAISystem
{
    public const string GearHomeHand = "hand";

    /// <summary>Remembers his spawned weapons once his starting gear is on.</summary>
    private void RecordOwnGear(Entity<GilbertAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.OwnGearRecorded)
            return;

        if (FindShotgun(ent) is { } gun)
            comp.OwnGear[gun] = HomeOf(ent, gun);

        if (TryFindStored(ent, IsTruncheon, out var truncheon))
            comp.OwnGear[truncheon] = HomeOf(ent, truncheon);
        else if (_hands.EnumerateHeld(ent.Owner).FirstOrDefault(IsTruncheon) is { Valid: true } heldTruncheon)
            comp.OwnGear[heldTruncheon] = GearHomeHand;

        if (TryFindStored(ent, IsShield, out var shield))
            comp.OwnGear[shield] = HomeOf(ent, shield);
        else if (_hands.EnumerateHeld(ent.Owner).FirstOrDefault(IsShield) is { Valid: true } heldShield)
            comp.OwnGear[heldShield] = GearHomeHand;

        // Starting gear is equipped at map init, but be patient with odd spawns (admin-spawned, no gear yet).
        comp.OwnGearRecorded = comp.OwnGear.Count > 0;
    }

    /// <summary>"hand", or the inventory slot ("belt", "back", "suitstorage"...) whose item holds it.</summary>
    private string HomeOf(EntityUid uid, EntityUid item)
    {
        if (_hands.IsHolding(uid, item))
            return GearHomeHand;

        var current = item;
        for (var i = 0; i < 6; i++)
        {
            if (!_container.TryGetContainingContainer((current, null, null), out var container))
                break;

            if (container.Owner == uid)
                return _inventory.HasSlot(uid, container.ID) ? container.ID : GearHomeHand;

            current = container.Owner;
        }

        return "back";
    }

    /// <summary>Is the item on him (hands, worn, in any of his bags)?</summary>
    public bool IsOnHim(EntityUid uid, EntityUid item)
    {
        var current = item;
        for (var i = 0; i < 8; i++)
        {
            if (!_container.TryGetContainingContainer((current, null, null), out var container))
                return false;

            if (container.Owner == uid)
                return true;

            current = container.Owner;
        }

        return false;
    }

    /// <summary>
    /// v9: where to go for a lost item: the item itself on the floor, or the bag / box / first-aid kit (any storage
    /// item, not a locker and not anybody's inventory) it was put in that lies on the floor.
    /// </summary>
    public bool TryGetGearHolder(EntityUid uid, EntityUid item, out EntityUid holder)
    {
        holder = item;
        for (var i = 0; i < 6; i++)
        {
            if (!_container.TryGetContainingContainer((holder, null, null), out var container))
                return !Transform(holder).Anchored;

            var owner = container.Owner;
            if (owner == uid || !HasComp<StorageComponent>(owner) || HasComp<MobStateComponent>(owner))
                return false;

            holder = owner;
        }

        return false;
    }

    /// <summary>His own lost weapon he can see lying around (the closest one), if any.</summary>
    public bool TryFindLostGear(Entity<GilbertAIComponent> ent, TransformComponent xform, float range, out EntityUid found)
    {
        found = default;
        var comp = ent.Comp;
        if (comp.OwnGear.Count == 0)
            return false;

        var now = _timing.CurTime;
        var best = float.MaxValue;
        foreach (var item in comp.OwnGear.Keys.ToList())
        {
            if (TerminatingOrDeleted(item))
            {
                comp.OwnGear.Remove(item);
                continue;
            }

            if (comp.RecoverIgnore.TryGetValue(item, out var until))
            {
                if (until > now)
                    continue;
                comp.RecoverIgnore.Remove(item);
            }

            // On him, or in someone's hands / a locker: nothing to pick up. v9: in a bag / box lying around: fine.
            if (!TryGetGearHolder(ent, item, out var holder))
                continue;

            var itemXform = Transform(holder);
            if (itemXform.MapID != xform.MapID)
                continue;

            var dist = DistanceTo(ent, holder);
            if (dist > range || dist >= best || !CanSee(ent, holder, comp, xform))
                continue;

            best = dist;
            found = item;
        }

        return found != default;
    }

    /// <summary>
    /// Goes for a lost weapon of his if he sees one. <paramref name="combat"/>: only one within
    /// <see cref="GilbertAIComponent.RecoverCombatRange"/>, unless the enemy hasn't been seen for a second.
    /// </summary>
    private bool TryStartRecover(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now, bool combat = false)
    {
        var comp = ent.Comp;
        if (comp.Task != GilbertTask.None)
            return false;

        RecordOwnGear(ent);
        var range = combat && now - comp.LastSeenTarget < TimeSpan.FromSeconds(1) ? comp.RecoverCombatRange : comp.RecoverRange;
        if (!TryFindLostGear(ent, xform, range, out var item))
            return false;

        StartTask(ent, GilbertTask.Recover, item, null, TimeSpan.FromSeconds(20));
        return true;
    }

    private void UpdateRecover(Entity<GilbertAIComponent> ent, EntityUid item, TimeSpan now)
    {
        var comp = ent.Comp;
        switch (comp.TaskStep)
        {
            case 0:
                if (IsOnHim(ent, item))
                {
                    // Already his again (picked up some other way): just put it back.
                    comp.TaskStep = 1;
                    return;
                }

                if (!TryGetGearHolder(ent, item, out var holder))
                {
                    // Someone else grabbed it.
                    comp.TaskItem = null;
                    FinishTask(ent);
                    return;
                }

                if (DistanceTo(ent, holder) > 1.2f || !_interaction.InRangeUnobstructed(ent.Owner, holder, 1.5f))
                {
                    if (TryComp<NPCSteeringComponent>(ent, out var steering) && steering.Status == SteeringStatus.NoPath)
                    {
                        comp.RecoverIgnore[item] = now + TimeSpan.FromSeconds(30);
                        _steering.Unregister(ent);
                        comp.TaskItem = null;
                        FinishTask(ent);
                        return;
                    }

                    RegisterMove(ent, Transform(holder).Coordinates, 0.8f);
                    return;
                }

                _steering.Unregister(ent);
                if (!TakeIntoFreeHand(ent, item))
                {
                    comp.RecoverIgnore[item] = now + TimeSpan.FromSeconds(30);
                    comp.TaskItem = null;
                    FinishTask(ent);
                    return;
                }

                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                return;

            case 1:
                ReturnGear(ent, item);
                comp.Recovered++;
                comp.TaskItem = null;
                FinishTask(ent);
                return;
        }
    }

    /// <summary>
    /// Puts one of his weapons back: the shotgun into his hands (wielded) or onto the vest if he fights with the
    /// truncheon right now; the truncheon / shield into the webbing or bag they came from (or kept in hand in melee).
    /// </summary>
    private void ReturnGear(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        PutGearBack(ent, item);
        // v10.1 (t292u): picking it up made its hand the active one; in a fight the weapon goes back there.
        if (ent.Comp.State is GilbertAIState.Combat or GilbertAIState.Search)
            SelectBestWeapon(ent);
    }

    private void PutGearBack(Entity<GilbertAIComponent> ent, EntityUid item)
    {
        var comp = ent.Comp;
        var home = comp.OwnGear.TryGetValue(item, out var h) ? h : "back";
        var melee = comp.WeaponMode == GilbertWeaponMode.Melee && comp.State is GilbertAIState.Combat or GilbertAIState.Search;

        if (HasComp<GunComponent>(item))
        {
            if (!melee && TakeIntoFreeHand(ent, item))
            {
                EnsureWielded(ent, item);
                return;
            }

            if (_hands.IsHolding(ent.Owner, item) || TakeIntoFreeHand(ent, item))
            {
                if (_inventory.TryEquip(ent, item, SuitStorageSlot, silent: true, force: true))
                    return;
            }

            if (!IsOnHim(ent, item))
                comp.RecoverIgnore[item] = _timing.CurTime + TimeSpan.FromSeconds(60);
            return;
        }

        // Truncheon / shield in a melee fight: keep it in hand.
        if (melee && (IsTruncheon(item) || IsShield(item)))
        {
            if (TakeIntoFreeHand(ent, item))
            {
                if (IsShield(item))
                    _toggle.TryActivate(item, ent, predicted: false);
                if (IsTruncheon(item))
                    _hands.TrySelect(ent, item);
                return;
            }
        }

        if (!_hands.IsHolding(ent.Owner, item) && !TakeIntoFreeHand(ent, item))
            return;

        if (IsShield(item))
            _toggle.TryDeactivate(item, ent, predicted: false);

        var placed = false;
        if (home != GearHomeHand && _inventory.TryGetSlotEntity(ent, home, out var storage)
            && TryComp<StorageComponent>(storage, out var storageComp))
        {
            placed = _storage.Insert(storage.Value, item, out _, user: ent, storageComp: storageComp, playSound: false);
        }

        if (!placed)
            StowOrDrop(ent, item);

        // No room for it anywhere: don't keep walking back to it.
        if (!IsOnHim(ent, item))
            comp.RecoverIgnore[item] = _timing.CurTime + TimeSpan.FromSeconds(60);

        if (GetGun(ent, out var gun, out _))
            EnsureWielded(ent, gun);
    }
}

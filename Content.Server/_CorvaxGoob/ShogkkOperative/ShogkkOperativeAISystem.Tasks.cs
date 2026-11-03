using System.Linq;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Medical.Healing;
using Content.Shared.Storage;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

public sealed partial class ShogkkOperativeAISystem
{
    #region Tasks

    private void UpdateTask(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now, EntityUid? seen)
    {
        var comp = ent.Comp;
        if (now >= comp.TaskTimeout)
        {
            if (comp.Task == ShogkkOperativeTask.PickupWeapon && comp.TaskItem is { } unreached)
                comp.PickupBlacklist[unreached] = now + TimeSpan.FromSeconds(30);

            AbortTask(ent);
            return;
        }

        // Patching up is dropped the moment an enemy shows up; a reload is finished (it's what he needs to fight).
        if (comp.Task == ShogkkOperativeTask.Heal && seen != null)
        {
            AbortTask(ent);
            EnterCombat(ent, seen.Value);
            return;
        }

        if (seen != null && comp.State != ShogkkOperativeAIState.Combat)
            EnterCombat(ent, seen.Value);

        if (seen != null)
        {
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = Transform(seen.Value).Coordinates;
            _rotate.TryFaceCoordinates(ent, _transform.GetWorldPosition(seen.Value));
        }

        if (now < comp.TaskNextStep)
            return;

        switch (comp.Task)
        {
            case ShogkkOperativeTask.Reload:
                UpdateReload(ent, now);
                break;
            case ShogkkOperativeTask.Heal:
                UpdateHeal(ent, now);
                break;
            case ShogkkOperativeTask.PickupWeapon:
                UpdatePickup(ent, now);
                break;
        }
    }

    private void AbortTask(Entity<ShogkkOperativeAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.Task == ShogkkOperativeTask.None)
            return;

        CancelDoAfters(ent);
        if (comp.TaskItem is { } item && !TerminatingOrDeleted(item) && _hands.IsHolding(ent.Owner, item))
            StowOrDrop(ent, item);

        var wasHealing = comp.Task == ShogkkOperativeTask.Heal;
        comp.Task = ShogkkOperativeTask.None;
        comp.TaskItem = null;
        comp.TaskStep = 0;

        if (comp.TaskGun is { } gun && !TerminatingOrDeleted(gun) && _hands.IsHolding(ent.Owner, gun))
            EnsureWielded(ent, gun);
        else if (comp.Gun is { } main && !TerminatingOrDeleted(main) && _hands.IsHolding(ent.Owner, main))
            EnsureWielded(ent, main);

        comp.TaskGun = null;
        if (wasHealing && comp.State == ShogkkOperativeAIState.Heal)
            SetState(ent, ShogkkOperativeAIState.Patrol);
    }

    private void CancelDoAfters(EntityUid uid)
    {
        if (!TryComp<DoAfterComponent>(uid, out var doAfter))
            return;

        foreach (var (id, da) in doAfter.DoAfters.ToList())
        {
            if (!da.Cancelled && !da.Completed)
                _doAfter.Cancel(uid, id, doAfter);
        }
    }

    #endregion

    #region Healing

    private bool TryStartHealing(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.Task != ShogkkOperativeTask.None || now < comp.NextHealAttempt || now - comp.LastDamaged < comp.HealSafeDelay)
            return false;

        if (!TryComp<DamageableComponent>(ent, out var damageable) || damageable.TotalDamage < comp.HealThreshold)
            return false;

        comp.HealBlacklist.RemoveWhere(e => TerminatingOrDeleted(e));
        if (!TryPickHealingItem(ent, damageable, out var item))
        {
            // Out of medicine: check again later in case he picks some up.
            comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
            return false;
        }

        StopCombatComponents(ent);
        _steering.Unregister(ent);
        SetState(ent, ShogkkOperativeAIState.Heal);
        comp.Task = ShogkkOperativeTask.Heal;
        comp.TaskStep = 0;
        comp.TaskItem = item;
        comp.TaskCounter = 0;
        comp.TaskGun = TryGetHeldGun(ent, out var gun) ? gun : null;
        comp.TaskNextStep = now;
        comp.TaskTimeout = now + TimeSpan.FromSeconds(45);
        return true;
    }

    /// <summary>The first-aid item (bag, webbing, inside the medkit) that would heal the most of what he has.</summary>
    public bool TryPickHealingItem(Entity<ShogkkOperativeAIComponent> ent, DamageableComponent damageable, out EntityUid item)
    {
        item = default;
        var bestScore = 0f;
        foreach (var candidate in EnumerateStored(ent))
        {
            if (ent.Comp.HealBlacklist.Contains(candidate) || !TryComp<HealingComponent>(candidate, out var healing))
                continue;

            if (healing.DamageContainers is { } containers && damageable.DamageContainerID is { } own && !containers.Contains(own))
                continue;

            var score = 0f;
            foreach (var (type, amount) in healing.Damage.DamageDict)
            {
                if (amount >= FixedPoint2.Zero || !damageable.Damage.DamageDict.TryGetValue(type, out var have) || have <= FixedPoint2.Zero)
                    continue;

                score += FixedPoint2.Min(have, -amount).Float();
            }

            if (score <= bestScore)
                continue;

            bestScore = score;
            item = candidate;
        }

        return bestScore > 0f;
    }

    private void UpdateHeal(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        var damage = CompOrNull<DamageableComponent>(ent)?.TotalDamage ?? FixedPoint2.Zero;
        switch (comp.TaskStep)
        {
            case 0:
                if (comp.TaskItem is not { } item || TerminatingOrDeleted(item))
                {
                    FinishHealing(ent, now);
                    return;
                }

                if (comp.TaskGun is { } gun)
                    Unwield(ent, gun);

                if (!TakeIntoFreeHand(ent, item))
                {
                    comp.NextHealAttempt = now + TimeSpan.FromSeconds(30);
                    FinishHealing(ent, now);
                    return;
                }

                // Used on himself like a player would (the normal self-heal do-after).
                comp.TaskMark = now;
                comp.TaskDamageBefore = damage.Float();
                _interaction.UseInHandInteraction(ent, item, checkCanInteract: false, checkUseDelay: false);
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.5);
                return;

            case 1:
                if (HasActiveDoAfter(ent))
                    return;

                if (comp.TaskItem is { } used && !TerminatingOrDeleted(used))
                {
                    if (damage.Float() >= comp.TaskDamageBefore)
                        comp.HealBlacklist.Add(used);

                    if (_hands.IsHolding(ent.Owner, used))
                        StowOrDrop(ent, used);
                }

                comp.TaskCounter++;
                if (damage >= comp.HealThreshold / 2 && comp.TaskCounter < 10 && TryComp<DamageableComponent>(ent, out var dmg)
                    && TryPickHealingItem(ent, dmg, out var next))
                {
                    comp.TaskItem = next;
                    comp.TaskStep = 0;
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                    return;
                }

                FinishHealing(ent, now);
                return;
        }
    }

    private void FinishHealing(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        comp.Task = ShogkkOperativeTask.None;
        comp.TaskItem = null;
        comp.NextHealAttempt = now + TimeSpan.FromSeconds(5);
        if (comp.TaskGun is { } gun && !TerminatingOrDeleted(gun) && _hands.IsHolding(ent.Owner, gun))
            EnsureWielded(ent, gun);

        comp.TaskGun = null;
        SetState(ent, ShogkkOperativeAIState.Patrol);
    }

    #endregion

    #region Inventory

    private bool TakeIntoFreeHand(EntityUid uid, EntityUid item)
    {
        if (_hands.IsHolding(uid, item))
        {
            _hands.TrySelect(uid, item);
            return true;
        }

        if (!_hands.TryGetEmptyHand(uid, out var hand))
        {
            if (TryGetHeldGun(uid, out var gun))
                Unwield(uid, gun);

            if (!_hands.TryGetEmptyHand(uid, out hand))
                return false;
        }

        // Pockets are inventory slots: unequip properly first.
        if (_container.TryGetContainingContainer((item, null, null), out var slotContainer) && slotContainer.Owner == uid
            && _inventory.HasSlot(uid, slotContainer.ID))
        {
            if (!_inventory.TryUnequip(uid, uid, slotContainer.ID, silent: true, force: true))
                return false;
        }

        if (!_hands.TryPickup(uid, item, hand, checkActionBlocker: false, animate: false))
            return false;

        _hands.SetActiveHand(uid, hand);
        return true;
    }

    private static readonly string[] StowSlots = { "back", "belt" };

    private void StowOrDrop(EntityUid uid, EntityUid item)
    {
        foreach (var slot in StowSlots)
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out var storage) && TryComp<StorageComponent>(storage, out var storageComp)
                && _storage.Insert(storage.Value, item, out _, user: uid, storageComp: storageComp, playSound: false))
                return;
        }

        _hands.TryDrop(uid, item);
    }

    private static readonly string[] StoredSlots = { "belt", "pocket1", "pocket2", "back" };

    /// <summary>Everything in his webbing, pockets and bag, including the inside of containers there (the medkit).</summary>
    public IEnumerable<EntityUid> EnumerateStored(EntityUid uid)
    {
        foreach (var slot in StoredSlots)
        {
            if (!_inventory.TryGetSlotEntity(uid, slot, out var storage))
                continue;

            if (slot.StartsWith("pocket"))
                yield return storage.Value;

            foreach (var item in EnumerateStorageRecursive(storage.Value, 0))
                yield return item;
        }
    }

    private IEnumerable<EntityUid> EnumerateStorageRecursive(EntityUid storage, int depth)
    {
        if (depth > 2 || !TryComp<StorageComponent>(storage, out var comp))
            yield break;

        foreach (var item in comp.Container.ContainedEntities.ToList())
        {
            yield return item;
            foreach (var inner in EnumerateStorageRecursive(item, depth + 1))
                yield return inner;
        }
    }

    #endregion
}

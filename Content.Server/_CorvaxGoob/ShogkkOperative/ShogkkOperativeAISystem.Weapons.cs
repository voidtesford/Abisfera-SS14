using System.Linq;
using Content.Shared.Item;
using Content.Shared.Storage;
using Content.Shared.Verbs;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wieldable.Components;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// Weapon handling like a player's: racking the bolt / pumping the shotgun when the gun needs it (the "use in hand"
/// key, or the Rack / Cycle verb), picking his own gun back up when it was knocked out of his hands, and grabbing any
/// weapon he sees on the floor when he has none.
/// </summary>
public sealed partial class ShogkkOperativeAISystem
{
    [Dependency] private readonly SharedVerbSystem _verbs = default!;

    #region Rack

    private bool IsSpent(EntityUid round)
    {
        return TryComp<CartridgeAmmoComponent>(round, out var cart) && cart.Spent;
    }

    /// <summary>
    /// True when a hand action (rack the bolt / close it / pump) is what stands between this gun and the next shot:
    /// the bolt is open with a round to chamber, the chamber is empty with a loaded magazine, or a spent case / shell
    /// sits in the chamber.
    /// </summary>
    public bool NeedsRack(EntityUid gun)
    {
        if (TryComp<ChamberMagazineAmmoProviderComponent>(gun, out var chamber))
        {
            var chambered = _gun.GetChamberEntity(gun);
            var chamberLive = chambered is { } round && !IsSpent(round);
            var magLive = _gun.GetMagazineEntity(gun) is { } mag && AmmoIn(mag) > 0;
            if (chamber.BoltClosed == false)
                return chamberLive || magLive;

            if (chambered != null && !chamberLive)
                return true;

            return chambered == null && magLive;
        }

        if (TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic))
            return ballistic.Cycleable && !ballistic.AutoCycle && ChamberedIsSpent(ballistic);

        return false;
    }

    /// <summary>
    /// Racks / pumps the gun in his hands the way a player does: two-handed guns wielded first (in one hand the "use"
    /// key would only wield it), then "use in hand"; the Rack / Cycle verb when that key would do something else.
    /// Rate-limited by <see cref="ShogkkOperativeAIComponent.RackDelay"/>.
    /// </summary>
    private bool TryRack(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextRack || !_hands.IsHolding(ent.Owner, gun))
            return false;

        comp.NextRack = now + comp.RackDelay;
        _hands.TrySelect(ent, gun);

        var useKeyRacks = true;
        if (TryComp<WieldableComponent>(gun, out var wield))
        {
            if (!wield.Wielded)
                _wieldable.TryWield(gun, wield, ent, showMessage: false);

            useKeyRacks = wield.Wielded && !wield.UnwieldOnUse;
        }

        if (!useKeyRacks || !_interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false))
            RackByVerb(ent, gun);

        comp.Racks++;
        if (NeedsRack(gun))
            comp.FailedRacks++;
        else
            comp.FailedRacks = 0;

        return true;
    }

    private static readonly string[] RackVerbs = { "gun-chamber-rack", "gun-ballistic-cycle" };

    private void RackByVerb(EntityUid user, EntityUid gun)
    {
        var texts = RackVerbs.Select(id => Loc.GetString(id)).ToHashSet();
        var verbs = _verbs.GetLocalVerbs(gun, user, new List<Type> { typeof(ActivationVerb), typeof(Verb) }, force: true);
        foreach (var verb in verbs)
        {
            if (verb.Disabled || !texts.Contains(verb.Text))
                continue;

            _verbs.ExecuteVerb(verb, user, gun, forced: true);
            return;
        }
    }

    /// <summary>Racks the gun if it needs it and he hasn't given up on it; true when he spent this moment on it.</summary>
    private bool RackIfNeeded(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun, TimeSpan now)
    {
        if (!NeedsRack(gun))
        {
            ent.Comp.FailedRacks = 0;
            return false;
        }

        if (ent.Comp.FailedRacks >= 4)
            return false;

        TryRack(ent, gun, now);
        return true;
    }

    #endregion

    #region Armament

    /// <summary>A gun he can fire now or after a reload from what he carries.</summary>
    private bool IsUsableGun(EntityUid uid, EntityUid gun)
    {
        return HasComp<GunComponent>(gun) && (LoadedAmmo(gun) > 0 || TryFindAmmoFor(uid, gun, out _));
    }

    /// <summary>Guns in his hands, on the vest's suit storage and in his webbing / pockets / bag.</summary>
    private IEnumerable<EntityUid> EnumerateOwnGuns(EntityUid uid)
    {
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (HasComp<GunComponent>(held))
                yield return held;
        }

        if (_inventory.TryGetSlotEntity(uid, SuitStorageSlot, out var suit) && HasComp<GunComponent>(suit))
            yield return suit.Value;

        foreach (var item in EnumerateStored(uid))
        {
            if (HasComp<GunComponent>(item))
                yield return item;
        }
    }

    private EntityUid? FindUsableGun(EntityUid uid, EntityUid? preferred)
    {
        if (preferred is { } main && !TerminatingOrDeleted(main) && EnumerateOwnGuns(uid).Contains(main) && IsUsableGun(uid, main))
            return main;

        foreach (var gun in EnumerateOwnGuns(uid).ToList())
        {
            if (IsUsableGun(uid, gun))
                return gun;
        }

        return null;
    }

    /// <summary>
    /// Knife in hand (or nothing) but a gun he can use on him: knife away, gun in both hands, back to shooting.
    /// </summary>
    private bool TryRearm(Entity<ShogkkOperativeAIComponent> ent)
    {
        var comp = ent.Comp;
        if (FindUsableGun(ent, comp.Gun) is not { } gun)
            return false;

        if (!_hands.IsHolding(ent.Owner, gun))
        {
            foreach (var held in _hands.EnumerateHeld(ent.Owner).ToList())
            {
                if (!HasComp<GunComponent>(held))
                    StowOrDrop(ent, held);
            }

            if (!TakeIntoFreeHand(ent, gun))
                return false;
        }

        ArmWithGun(ent, gun);
        return true;
    }

    private void ArmWithGun(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun)
    {
        var comp = ent.Comp;
        comp.Gun = gun;
        comp.OutOfAmmo = false;
        comp.FailedRacks = 0;
        comp.WeaponMode = ShogkkOperativeWeaponMode.Ranged;
        RemComp<Content.Server.NPC.Components.NPCMeleeCombatComponent>(ent);
        EnsureWielded(ent, gun);
    }

    private bool HasMeleeWeapon(EntityUid uid) => FindKnife(uid) != null;

    /// <summary>
    /// Between fights and moves: racks a gun that needs it, takes up a usable gun he carries instead of the knife, and
    /// goes for a weapon on the floor (his own dropped gun; anything when he has no usable gun). True when this think
    /// went on that.
    /// </summary>
    private bool UpdateArmament(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (now < comp.NextWeaponCheck)
            return false;

        comp.NextWeaponCheck = now + comp.WeaponCheckInterval;

        // Outside the fight a gun that needs racking gets racked straight away (in a fight UpdateCombat does it).
        if (comp.State != ShogkkOperativeAIState.Combat && TryGetHeldGun(ent, out var held) && RackIfNeeded(ent, held, now))
            return false;

        if ((comp.WeaponMode == ShogkkOperativeWeaponMode.Melee || !TryGetHeldGun(ent, out _)) && TryRearm(ent))
            return false;

        if (FindFloorWeapon(ent, xform, now) is { } item)
        {
            StartPickup(ent, item, now);
            return true;
        }

        return false;
    }

    #endregion

    #region Pickup

    private bool OnTheFloor(EntityUid item, TransformComponent itemXform, TransformComponent xform)
    {
        return !TerminatingOrDeleted(item) && itemXform.MapID == xform.MapID && !itemXform.Anchored
               && !_container.IsEntityOrParentInContainer(item, xform: itemXform);
    }

    /// <summary>
    /// The weapon on the floor to go for, or null: his own gun if it lies in view and is worth it; otherwise, when he
    /// has no usable gun, the best one in view: a gun he can shoot (loaded, or ammo for it on him), else (no weapon at
    /// all) the hardest-hitting melee weapon, else an empty gun to club with.
    /// </summary>
    private EntityUid? FindFloorWeapon(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        foreach (var (blocked, until) in comp.PickupBlacklist.ToList())
        {
            if (until <= now || TerminatingOrDeleted(blocked))
                comp.PickupBlacklist.Remove(blocked);
        }

        var hasUsableGun = FindUsableGun(ent, comp.Gun) != null;
        var hasMelee = HasMeleeWeapon(ent);

        if (comp.Gun is { } own && !comp.PickupBlacklist.ContainsKey(own) && !TerminatingOrDeleted(own)
            && OnTheFloor(own, Transform(own), xform) && CanSee(ent, own, comp, xform)
            && (IsUsableGun(ent, own) || !hasUsableGun && !hasMelee))
        {
            return own;
        }

        if (hasUsableGun)
            return null;

        EntityUid? best = null;
        var bestScore = float.MinValue;
        var pos = _transform.GetWorldPosition(xform);
        foreach (var item in _lookup.GetEntitiesInRange<ItemComponent>(xform.Coordinates, comp.VisionRange))
        {
            if (comp.PickupBlacklist.ContainsKey(item) || HasComp<StorageComponent>(item))
                continue;

            var isGun = HasComp<GunComponent>(item);
            if (!isGun && !TryComp<MeleeWeaponComponent>(item, out _))
                continue;

            var itemXform = Transform(item);
            if (!OnTheFloor(item, itemXform, xform))
                continue;

            float score;
            if (isGun && IsUsableGun(ent, item))
                score = 3000f;
            else if (hasMelee)
                continue; // He has a knife: only a gun he can shoot is worth the detour.
            else if (!isGun && TryComp<MeleeWeaponComponent>(item, out var melee))
                score = 2000f + Math.Min(melee.Damage.GetTotal().Float(), 100f);
            else
                score = 1000f;

            score -= (_transform.GetWorldPosition(itemXform) - pos).Length();
            if (score <= bestScore || !CanSee(ent, item, comp, xform))
                continue;

            bestScore = score;
            best = item;
        }

        return best;
    }

    private void StartPickup(Entity<ShogkkOperativeAIComponent> ent, EntityUid item, TimeSpan now)
    {
        var comp = ent.Comp;
        StopCombatComponents(ent);
        comp.Task = ShogkkOperativeTask.PickupWeapon;
        comp.TaskStep = 0;
        comp.TaskItem = item;
        comp.TaskGun = null;
        comp.TaskNextStep = now;
        comp.TaskTimeout = now + comp.PickupTimeout;
        comp.ProgressTime = now;
        comp.ProgressPosition = _transform.GetWorldPosition(ent);
        SteerTo(ent, Transform(item).Coordinates, 0.6f);
    }

    private void UpdatePickup(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        var xform = Transform(ent);
        if (comp.TaskItem is not { } item || TerminatingOrDeleted(item) || !OnTheFloor(item, Transform(item), xform))
        {
            // Gone, or someone else took it.
            FinishPickupTask(ent);
            return;
        }

        if (_interaction.InRangeUnobstructed(ent.Owner, item, 1.3f))
        {
            _steering.Unregister(ent);
            PickUp(ent, item);
            FinishPickupTask(ent);
            return;
        }

        var coords = Transform(item).Coordinates;
        if (ReachedOrStuck(ent, xform, coords, 0.6f, now) && !_interaction.InRangeUnobstructed(ent.Owner, item, 1.3f))
        {
            comp.PickupBlacklist[item] = now + TimeSpan.FromSeconds(30);
            FinishPickupTask(ent);
            return;
        }

        SteerTo(ent, coords, 0.6f);
        comp.TaskNextStep = now + TimeSpan.FromSeconds(0.2);
    }

    private void PickUp(Entity<ShogkkOperativeAIComponent> ent, EntityUid item)
    {
        var comp = ent.Comp;
        var isGun = HasComp<GunComponent>(item);

        // Hands free for it: a knife goes back in the webbing / bag; a gun he can't use goes on the vest or in the bag.
        foreach (var held in _hands.EnumerateHeld(ent.Owner).ToList())
        {
            if (HasComp<GunComponent>(held))
            {
                if (!isGun && IsUsableGun(ent, held))
                    continue;

                Unwield(ent, held);
                if (!_inventory.TryEquip(ent, held, SuitStorageSlot, silent: true, force: true))
                    StowOrDrop(ent, held);
            }
            else if (isGun || HasComp<MeleeWeaponComponent>(held))
            {
                StowOrDrop(ent, held);
            }
        }

        if (!TakeIntoFreeHand(ent, item))
        {
            comp.PickupBlacklist[item] = _timing.CurTime + TimeSpan.FromSeconds(30);
            return;
        }

        comp.Pickups++;
        if (isGun)
        {
            ArmWithGun(ent, item);
            comp.NextRack = TimeSpan.Zero;
            RackIfNeeded(ent, item, _timing.CurTime);
        }
        else
        {
            comp.WeaponMode = ShogkkOperativeWeaponMode.Melee;
            _hands.TrySelect(ent, item);
        }
    }

    private void FinishPickupTask(Entity<ShogkkOperativeAIComponent> ent)
    {
        var comp = ent.Comp;
        comp.Task = ShogkkOperativeTask.None;
        comp.TaskItem = null;
        comp.TaskStep = 0;
        comp.TaskGun = null;
        comp.NextWeaponCheck = TimeSpan.Zero;
    }

    #endregion
}

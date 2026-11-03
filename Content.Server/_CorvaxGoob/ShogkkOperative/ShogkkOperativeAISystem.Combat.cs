using Content.Shared._CorvaxGoob.ShogkkOperative;
using System.Numerics;
using Robust.Shared.Prototypes;
using System.Linq;
using Content.Server.NPC.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Movement.Components;
using Content.Shared.Storage;
using Content.Shared.Tag;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

public sealed partial class ShogkkOperativeAISystem
{
    private const string MagazineSlot = "gun_magazine";
    private const string ChamberSlot = "gun_chamber";
    private const string SuitStorageSlot = "suitstorage";

    #region Combat

    private void UpdateCombat(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.Target is not { } tgt || !IsEnemy(tgt))
        {
            comp.Target = null;
            StopCombatComponents(ent);
            if (comp.LastKnownPosition is { } last)
                EnterSearch(ent, last, now);
            else
                SetState(ent, ShogkkOperativeAIState.Patrol);
            return;
        }

        var visible = CanSee(ent, tgt, comp, xform);
        var tgtCoords = Transform(tgt).Coordinates;
        if (visible)
        {
            comp.LastSeenTarget = now;
            comp.LastKnownPosition = tgtCoords;
            comp.KnownEnemies[tgt] = new ShogkkKnownEnemy(tgtCoords, now);
        }
        else if (now - comp.LastSeenTarget > comp.LoseTargetDelay)
        {
            StopCombatComponents(ent);
            EnterSearch(ent, comp.LastKnownPosition, now);
            return;
        }

        var dist = DistanceTo(ent, tgt);
        if (visible)
        {
            // Keeps facing the enemy while moving (strafing / backing off), like a player would.
            EnsureComp<NoRotateOnMoveComponent>(ent);
            _rotate.TryFaceCoordinates(ent, _transform.GetWorldPosition(tgt));
        }

        if (comp.WeaponMode == ShogkkOperativeWeaponMode.Ranged)
        {
            if (!TryGetHeldGun(ent, out var gun))
            {
                SwitchToMelee(ent);
            }
            else
            {
                // Pump-action: rack the spent shell out.
                if (TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic) && ChamberedIsSpent(ballistic))
                    _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);

                if (LoadedAmmo(gun) == 0)
                {
                    if (TryFindAmmoFor(ent, gun, out var ammo))
                    {
                        StartReload(ent, gun, ammo, now);
                        return;
                    }

                    comp.OutOfAmmo = true;
                    SwitchToMelee(ent);
                }
            }
        }

        if (comp.WeaponMode == ShogkkOperativeWeaponMode.Melee)
        {
            RemComp<NPCRangedCombatComponent>(ent);
            var melee = EnsureComp<NPCMeleeCombatComponent>(ent);
            melee.Target = tgt;
            SteerTo(ent, visible ? tgtCoords : comp.LastKnownPosition ?? tgtCoords, 0.9f);
            return;
        }

        RemComp<NPCMeleeCombatComponent>(ent);
        if (TryGetHeldGun(ent, out var held))
            EnsureWielded(ent, held);

        // An ally in the line of fire: hold fire and sidestep.
        if (visible && AllyInLineOfFire(ent, tgt, xform, out var sidestep))
        {
            RemComp<NPCRangedCombatComponent>(ent);
            SteerTo(ent, sidestep, 0.3f);
            return;
        }

        var ranged = EnsureComp<NPCRangedCombatComponent>(ent);
        ranged.Target = tgt;

        // Seen through glass is no shot: go round through the door. In view with a clear line: stand and shoot.
        var clearShot = visible && _interaction.InRangeUnobstructed(ent.Owner, tgt, dist + 0.5f);
        if (clearShot && dist <= comp.RangedKeepDistance)
            _steering.Unregister(ent);
        else if (visible && !clearShot)
            SteerTo(ent, tgtCoords, 1.2f);
        else
            SteerTo(ent, comp.LastKnownPosition ?? tgtCoords, comp.RangedKeepDistance - 0.5f);
    }

    /// <summary>
    /// True when another operative stands between him and the target, inside the spread cone of the gun (at least a
    /// body width). <paramref name="sidestep"/> is a spot 1.5 tiles to the side away from that ally.
    /// </summary>
    private bool AllyInLineOfFire(Entity<ShogkkOperativeAIComponent> ent, EntityUid target, TransformComponent xform, out EntityCoordinates sidestep)
    {
        sidestep = default;
        var from = _transform.GetWorldPosition(xform);
        var toTarget = _transform.GetWorldPosition(target) - from;
        var dist = toTarget.Length();
        if (dist < 0.01f)
            return false;

        var dir = toTarget / dist;
        var spread = TryGetHeldGun(ent, out var gun) && TryComp<GunComponent>(gun, out var gunComp)
            ? (float) gunComp.MaxAngle.Theta / 2f
            : 0f;

        foreach (var ally in _lookup.GetEntitiesInRange<ShogkkOperativeComponent>(xform.Coordinates, dist + 0.5f))
        {
            if (ally.Owner == ent.Owner || _mobState.IsDead(ally))
                continue;

            var rel = _transform.GetWorldPosition(ally) - from;
            var along = Vector2.Dot(rel, dir);
            if (along <= 0.2f || along >= dist + 0.3f)
                continue;

            var across = dir.X * rel.Y - dir.Y * rel.X;
            var halfWidth = 0.55f + along * MathF.Tan(spread);
            if (MathF.Abs(across) > halfWidth)
                continue;

            var side = across >= 0 ? -1f : 1f;
            var perp = new Vector2(-dir.Y, dir.X) * side * 1.5f;
            sidestep = _transform.ToCoordinates(new MapCoordinates(from + perp, xform.MapID));
            return true;
        }

        return false;
    }

    #endregion

    #region Weapons

    private bool TryGetHeldGun(EntityUid uid, out EntityUid gun)
    {
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (HasComp<GunComponent>(held))
            {
                gun = held;
                return true;
            }
        }

        gun = default;
        return false;
    }

    private void EnsureWielded(EntityUid uid, EntityUid gun)
    {
        if (TryComp<WieldableComponent>(gun, out var wield) && !wield.Wielded)
            _wieldable.TryWield(gun, wield, uid, showMessage: false);

        _hands.TrySelect(uid, gun);
    }

    private void Unwield(EntityUid uid, EntityUid gun)
    {
        if (TryComp<WieldableComponent>(gun, out var wield) && wield.Wielded)
            _wieldable.TryUnwield(gun, wield, uid, force: true);
    }

    private bool ChamberedIsSpent(BallisticAmmoProviderComponent ballistic)
    {
        if (ballistic.Entities.Count == 0)
            return false;

        return TryComp<CartridgeAmmoComponent>(ballistic.Entities[^1], out var cart) && cart.Spent;
    }

    public int CountLiveShells(BallisticAmmoProviderComponent ballistic)
    {
        var count = ballistic.UnspawnedCount;
        foreach (var ent in ballistic.Entities)
        {
            if (!TryComp<CartridgeAmmoComponent>(ent, out var cart) || !cart.Spent)
                count++;
        }

        return count;
    }

    /// <summary>Rounds he can still fire from this gun without reloading.</summary>
    public int LoadedAmmo(EntityUid gun)
    {
        if (TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic))
            return CountLiveShells(ballistic);

        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(gun, ref ev);
        return ev.Count;
    }

    private int AmmoIn(EntityUid item)
    {
        if (TryComp<BallisticAmmoProviderComponent>(item, out var ballistic))
            return CountLiveShells(ballistic);

        var ev = new GetAmmoCountEvent();
        RaiseLocalEvent(item, ref ev);
        return ev.Count;
    }

    /// <summary>
    /// A spare magazine that fits the gun's magazine slot, or (pump shotgun) a shell box of the same shells, with
    /// rounds in it, in his bag / webbing / pockets / hands.
    /// </summary>
    public bool TryFindAmmoFor(EntityUid uid, EntityUid gun, out EntityUid ammo)
    {
        ammo = default;
        var magazineGun = _itemSlots.TryGetSlot(gun, MagazineSlot, out var magSlot);
        TryComp<BallisticAmmoProviderComponent>(gun, out var gunBallistic);
        var best = 0;

        foreach (var item in EnumerateStored(uid).Concat(_hands.EnumerateHeld(uid)))
        {
            if (item == gun || HasComp<GunComponent>(item))
                continue;

            var fits = false;
            if (magazineGun)
                fits = HasComp<BallisticAmmoProviderComponent>(item) && _itemSlots.CanInsert(gun, item, uid, magSlot!);
            else if (gunBallistic != null && TryComp<BallisticAmmoProviderComponent>(item, out var box) && box.MayTransfer)
                fits = box.Proto == gunBallistic.Proto || box.Entities.Count > 0 && RoundFits(gunBallistic, box.Entities[0]);

            if (!fits)
                continue;

            var count = AmmoIn(item);
            if (count <= best)
                continue;

            best = count;
            ammo = item;
        }

        return best > 0;
    }

    private bool RoundFits(BallisticAmmoProviderComponent gun, EntityUid round)
    {
        return gun.Whitelist == null || _whitelist.IsValid(gun.Whitelist, round);
    }

    private void SwitchToMelee(Entity<ShogkkOperativeAIComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.WeaponMode == ShogkkOperativeWeaponMode.Melee)
            return;

        comp.WeaponMode = ShogkkOperativeWeaponMode.Melee;
        RemComp<NPCRangedCombatComponent>(ent);

        // The empty gun goes on the vest's suit storage, in the bag, or on the floor.
        foreach (var held in _hands.EnumerateHeld(ent.Owner).ToList())
        {
            if (!HasComp<GunComponent>(held))
                continue;

            Unwield(ent, held);
            if (!_inventory.TryEquip(ent, held, SuitStorageSlot, silent: true, force: true))
                StowOrDrop(ent, held);
        }

        if (FindKnife(ent) is { } knife && TakeIntoFreeHand(ent, knife))
            _hands.TrySelect(ent, knife);
    }

    private static readonly ProtoId<TagPrototype> KnifeTag = "Knife";

    private EntityUid? FindKnife(EntityUid uid)
    {
        EntityUid? fallback = null;
        foreach (var item in _hands.EnumerateHeld(uid).Concat(EnumerateStored(uid)))
        {
            if (!HasComp<MeleeWeaponComponent>(item) || HasComp<GunComponent>(item) || HasComp<StorageComponent>(item))
                continue;

            if (_tag.HasTag(item, KnifeTag))
                return item;

            fallback ??= item;
        }

        return fallback;
    }

    #endregion

    #region Reload

    private void StartReload(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun, EntityUid ammo, TimeSpan now)
    {
        var comp = ent.Comp;
        StopCombatComponents(ent);
        _steering.Unregister(ent);
        comp.Task = ShogkkOperativeTask.Reload;
        comp.TaskStep = 0;
        comp.TaskItem = ammo;
        comp.TaskGun = gun;
        comp.TaskNextStep = now;
        comp.TaskTimeout = now + TimeSpan.FromSeconds(20);
    }

    private void UpdateReload(Entity<ShogkkOperativeAIComponent> ent, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.TaskGun is not { } gun || comp.TaskItem is not { } ammo || TerminatingOrDeleted(gun) || TerminatingOrDeleted(ammo)
            || !_hands.IsHolding(ent.Owner, gun))
        {
            AbortTask(ent);
            return;
        }

        if (_itemSlots.TryGetSlot(gun, MagazineSlot, out _))
            UpdateMagazineReload(ent, gun, ammo, now);
        else
            UpdateBoxReload(ent, gun, ammo, now);
    }

    /// <summary>Rifle / SMG: pull the empty magazine (drop it), take a full one from the bag, seat it, chamber.</summary>
    private void UpdateMagazineReload(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun, EntityUid mag, TimeSpan now)
    {
        var comp = ent.Comp;
        switch (comp.TaskStep)
        {
            case 0:
                Unwield(ent, gun);
                if (_itemSlots.TryEject(gun, MagazineSlot, ent, out var old))
                {
                    // A half-used one goes back in the bag, an empty one stays on the floor.
                    if (AmmoIn(old.Value) > 0)
                        StowOrDrop(ent, old.Value);
                }

                if (!TakeIntoFreeHand(ent, mag))
                {
                    AbortTask(ent);
                    return;
                }

                comp.TaskStep = 1;
                comp.TaskNextStep = now + comp.MagazineSwapTime;
                return;

            case 1:
                if (!_itemSlots.TryInsert(gun, MagazineSlot, mag, ent))
                {
                    StowOrDrop(ent, mag);
                    AbortTask(ent);
                    return;
                }

                // Chamber a round from the new magazine.
                if (_gun.GetChamberEntity(gun) == null)
                {
                    _hands.TrySelect(ent, gun);
                    _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);
                }

                FinishReload(ent, gun);
                return;
        }
    }

    /// <summary>Pump shotgun: spent shell out, then the shell box on the gun (the normal continuous-fill do-after).</summary>
    private void UpdateBoxReload(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun, EntityUid box, TimeSpan now)
    {
        var comp = ent.Comp;
        if (!TryComp<BallisticAmmoProviderComponent>(gun, out var ballistic))
        {
            AbortTask(ent);
            return;
        }

        switch (comp.TaskStep)
        {
            case 0:
                if (ballistic.Entities.Count > 0 && ChamberedIsSpent(ballistic))
                {
                    _hands.TrySelect(ent, gun);
                    _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.3);
                    return;
                }

                Unwield(ent, gun);
                if (!TakeIntoFreeHand(ent, box))
                {
                    AbortTask(ent);
                    return;
                }

                comp.TaskCounter = CountLiveShells(ballistic);
                _interaction.InteractUsing(ent, box, gun, Transform(gun).Coordinates, checkCanInteract: false);
                comp.TaskMark = now;
                comp.TaskStep = 1;
                comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
                return;

            case 1:
            {
                var live = CountLiveShells(ballistic);
                if (live > comp.TaskCounter)
                {
                    comp.TaskCounter = live;
                    comp.TaskMark = now;
                }

                if (HasActiveDoAfter(ent) && now - comp.TaskMark <= TimeSpan.FromSeconds(3))
                {
                    comp.TaskNextStep = now + TimeSpan.FromSeconds(0.25);
                    return;
                }

                CancelDoAfters(ent);
                if (AmmoIn(box) > 0)
                    StowOrDrop(ent, box);
                else
                    _hands.TryDrop(ent.Owner, box);

                if (live == 0)
                {
                    AbortTask(ent);
                    return;
                }

                // Chamber the first shell.
                _hands.TrySelect(ent, gun);
                _interaction.UseInHandInteraction(ent, gun, checkCanInteract: false, checkUseDelay: false);
                FinishReload(ent, gun);
                return;
            }
        }
    }

    private void FinishReload(Entity<ShogkkOperativeAIComponent> ent, EntityUid gun)
    {
        ent.Comp.Reloads++;
        ent.Comp.Task = ShogkkOperativeTask.None;
        ent.Comp.TaskItem = null;
        ent.Comp.TaskGun = null;
        EnsureWielded(ent, gun);
    }

    #endregion
}

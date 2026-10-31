using System.Linq;
using Content.Server.NPC.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>
/// v10.1 (t292u): he must never stand next to a visible enemy without fighting. The NPC combat components attack with
/// whatever is in the ACTIVE hand; putting recovered gear back (the shield / shotgun picked up into a free hand, the
/// shotgun hung on the vest) could leave a non-weapon or an empty hand active, and the melee branch never re-selected
/// the truncheon. <see cref="SelectBestWeapon"/> fixes that after every recovery and every melee think, and the
/// watchdog below catches anything else: a hostile in reach and in plain view, no shot / swing for
/// <see cref="GilbertAIComponent.AttackWatchdogDelay"/>, no task, no cutscene → combat state reset, best weapon
/// re-equipped into the active hand, fight resumed.
/// </summary>
public sealed partial class GilbertAISystem
{
    /// <summary>Puts the weapon of the current mode into the active hand (truncheon in melee, the wielded shotgun in ranged).</summary>
    private void SelectBestWeapon(Entity<GilbertAIComponent> ent)
    {
        if (ent.Comp.WeaponMode == GilbertWeaponMode.Ranged)
        {
            if (GetGun(ent, out var gun, out _))
                EnsureWielded(ent, gun);
            return;
        }

        var truncheon = _hands.EnumerateHeld(ent.Owner).FirstOrDefault(IsTruncheon);
        if (truncheon == default && TryFindStored(ent, e => IsTruncheon(e), out var stored) && TakeIntoFreeHand(ent, stored))
            truncheon = stored;

        if (truncheon != default && _hands.IsHolding(ent.Owner, truncheon)
            && _hands.GetActiveItem(ent.Owner) != truncheon)
        {
            _hands.TrySelect(ent, truncheon);
        }
    }

    /// <summary>Changes whenever any of his weapons (or his fists) fires / swings: their cooldown timestamps.</summary>
    private long AttackSignature(EntityUid uid)
    {
        long sig = 0;
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (TryComp<GunComponent>(held, out var gun))
                sig += gun.NextFire.Ticks;
            if (TryComp<MeleeWeaponComponent>(held, out var melee))
                sig += melee.NextAttack.Ticks;
        }

        if (TryComp<MeleeWeaponComponent>(uid, out var fist))
            sig += fist.NextAttack.Ticks;
        return sig;
    }

    /// <summary>Should he be attacking right now: in combat, nothing else to do, the target hostile, in reach and in view.</summary>
    private bool ShouldBeAttacking(Entity<GilbertAIComponent> ent, TransformComponent xform)
    {
        var comp = ent.Comp;
        if (comp.State != GilbertAIState.Combat || comp.Task != GilbertTask.None || comp.FinisherVictim != null
            || comp.Target is not { } tgt || TerminatingOrDeleted(tgt) || !IsHostile(ent, tgt))
            return false;

        // Holding fire on purpose (gun cooling down / locked): not a stall.
        if (comp.WeaponMode == GilbertWeaponMode.Ranged && GetGun(ent, out var gun, out _)
            && TryComp<GunComponent>(gun, out var gunComp) && gunComp.NextFire > _timing.CurTime + comp.AttackWatchdogDelay)
            return false;

        var dist = DistanceTo(ent, tgt);
        var reach = comp.WeaponMode == GilbertWeaponMode.Melee ? 1.4f : comp.RangedKeepDistance + 0.5f;
        return dist <= reach && CanSee(ent, tgt, comp, xform) && _interaction.InRangeUnobstructed(ent.Owner, tgt, dist + 0.5f);
    }

    private void UpdateAttackWatchdog(Entity<GilbertAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        var sig = AttackSignature(ent);
        if (sig != comp.AttackSignature || !ShouldBeAttacking(ent, xform))
        {
            comp.AttackSignature = sig;
            comp.AttackStallSince = now;
            return;
        }

        if (now - comp.AttackStallSince < comp.AttackWatchdogDelay)
            return;

        // Stalled: drop the NPC combat state, pick the weapon for the range again and put it in the active hand.
        comp.AttackStallSince = now;
        comp.AttackWatchdogResets++;
        StopCombatComponents(ent);
        _steering.Unregister(ent);

        var tgt = comp.Target!.Value;
        var wantRanged = DistanceTo(ent, tgt) > comp.MeleeSwitchRange && HasAnyShotgunAmmo(ent);
        comp.WeaponMode = wantRanged ? GilbertWeaponMode.Melee : GilbertWeaponMode.Ranged; // force a real switch
        if (wantRanged)
            SwitchToRanged(ent);
        else
            SwitchToMelee(ent);
        comp.NextWeaponSwitch = now;
        SelectBestWeapon(ent);
        comp.NextThink = now;
        Log.Debug($"Gilbert {ToPrettyString(ent)}: no attack on {ToPrettyString(tgt)} for {comp.AttackWatchdogDelay.TotalSeconds:0.#} s, combat reset ({comp.WeaponMode}).");
    }
}

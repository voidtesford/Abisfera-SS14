// CorvaxGoob-ProperKungFu: passive bonuses of the "Proper Kung Fu" martial art.

using Content.Goobstation.Shared.MartialArts.Components;
using Content.Goobstation.Shared.Sprinting;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Movement.Systems;
using Content.Shared.Weapons.Melee.Events;

namespace Content.Goobstation.Shared.MartialArts;

public partial class SharedMartialArtsSystem
{
    private void InitializeProperKungFu()
    {
        SubscribeLocalEvent<GrantProperKungFuComponent, Content.Shared.Interaction.Events.UseInHandEvent>(OnGrantCQCUse);
        SubscribeLocalEvent<ProperKungFuComponent, GetMeleeAttackRateEvent>(OnProperKungFuAttackRate);
        SubscribeLocalEvent<ProperKungFuComponent, RefreshMovementSpeedModifiersEvent>(OnProperKungFuMoveSpeed);
    }

    /// <summary>
    /// Faster fists. The event is raised on the weapon, so this only fires for unarmed attacks (weapon == user).
    /// </summary>
    private void OnProperKungFuAttackRate(Entity<ProperKungFuComponent> ent, ref GetMeleeAttackRateEvent args)
    {
        if (args.Weapon != args.User || !ent.Comp.HasPassives)
            return;

        args.Multipliers *= ent.Comp.UnarmedAttackRateMultiplier;
    }

    /// <summary>
    /// Faster sprint (Goob sprint mechanic). Walking and normal movement stay the same.
    /// </summary>
    private void OnProperKungFuMoveSpeed(Entity<ProperKungFuComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        if (ent.Comp.LifeStage > ComponentLifeStage.Running
            || !ent.Comp.HasPassives
            || !TryComp<SprinterComponent>(ent, out var sprinter)
            || !sprinter.IsSprinting)
            return;

        args.ModifySpeed(ent.Comp.SprintSpeedMultiplier);
    }
}

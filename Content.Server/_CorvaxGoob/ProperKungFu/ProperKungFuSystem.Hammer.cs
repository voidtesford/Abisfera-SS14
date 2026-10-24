using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Common.Weapons;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Part;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Hammer" (Молот): a sweeping downward claw strike into the head.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly Content.Shared.StatusEffectNew.StatusEffectsSystem _statusNew = default!;
    [Dependency] private readonly StandingStateSystem _standing = default!;

    private const string HammerMove = "Hammer";

    private void OnHammer(Entity<ProperKungFuComponent> ent, ref HammerActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        var target = args.Target;

        if (user == target
            || !HasComp<MobStateComponent>(target)
            || HasComp<WolfDancingComponent>(user)
            || HasComp<ClawWhirlwindedComponent>(target)
            || HasComp<ClawWhirlwindedComponent>(user))
            return;

        if (!TryComp<MartialArtsKnowledgeComponent>(user, out var knowledge)
            || knowledge.MartialArtsForm != MartialArtsForms.ProperKungFu
            || knowledge.Blocked)
            return;

        var userCoords = _transform.GetMapCoordinates(user);
        var targetCoords = _transform.GetMapCoordinates(target);
        if (userCoords.MapId != targetCoords.MapId)
            return;

        // Same reach as the user's fists; out of reach nothing happens and the cooldown isn't spent.
        if (!TryComp<MeleeWeaponComponent>(user, out var melee))
            return;

        var rangeEv = new GetLightAttackRangeEvent(target, user, melee.Range);
        RaiseLocalEvent(user, ref rangeEv);
        if (!_melee.InRange(user, target, rangeEv.Range, null))
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-hammer-too-far"), user, user, PopupType.SmallCaution);
            return;
        }

        args.Handled = true;

        var delta = targetCoords.Position - userCoords.Position;
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        // Priority: crit > Wolf Grip synergy > Wolf Dance mark > lying > base hit.
        if (_mobState.IsCritical(target))
        {
            // The skull breaks, but the head stays on.
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            RemComp<ProperKungFuWolfGripMarkComponent>(target);
            RemComp<ProperKungFuWolfDanceMarkComponent>(target);
            DealHammerHeadDamage(user, target, args.Damage);
            InflictHammerTrauma(target, args, breakSkull: true);
            FinishHammer(user, target, "proper-kung-fu-hammer-crit-user", "proper-kung-fu-hammer-crit-target", args.HitSound);
            return;
        }

        if (TryConsumeWolfGripMark(user, target))
        {
            // From the grip the claws sweep up instead of down.
            SpawnHammerEffect(args.GripImpactEffect, targetCoords);
            DoHammerGripSynergy(user, target, direction, args);
            FinishHammer(user, target, "proper-kung-fu-hammer-grip-user", "proper-kung-fu-hammer-grip-target", args.HitSound);
            return;
        }

        // Wolf Dance variant: wins over the lying branch, since the dance itself knocks the target down.
        if (TryConsumeWolfDanceMark(user, target))
        {
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            DoHammerDanceVariant(user, target, direction, args);
            FinishHammer(user, target, "proper-kung-fu-hammer-dance-user", "proper-kung-fu-hammer-dance-target", args.HitSound);
            return;
        }

        if (_standing.IsDown(target) || HasComp<KnockedDownComponent>(target))
        {
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            RemComp<ProperKungFuWolfGripMarkComponent>(target);
            DealHammerHeadDamage(user, target, args.LyingDamage);
            InflictHammerTrauma(target, args);
            // After the damage, so the hit itself doesn't wake it up again.
            _statusNew.TryAddStatusEffectDuration(target, args.SleepStatusEffect, args.SleepTime);
            FinishHammer(user, target, "proper-kung-fu-hammer-sleep-user", "proper-kung-fu-hammer-sleep-target", args.HitSound);
            return;
        }

        SpawnHammerEffect(args.ImpactEffect, targetCoords);
        DealHammerHeadDamage(user, target, args.Damage);
        InflictHammerTrauma(target, args);
        _stun.TryKnockdown(target, args.KnockdownTime, true);
        FinishHammer(user, target, "proper-kung-fu-hammer-user", "proper-kung-fu-hammer-target", args.HitSound);
    }

    private void SpawnHammerEffect(Robust.Shared.Prototypes.EntProtoId? effect, Robust.Shared.Map.MapCoordinates coords)
    {
        if (effect is { } proto)
            Spawn(proto, coords);
    }

    private void FinishHammer(EntityUid user, EntityUid target, string userLoc, string targetLoc, Robust.Shared.Audio.SoundSpecifier? sound)
    {
        _audio.PlayPvs(sound, target);
        _popup.PopupEntity(Loc.GetString(userLoc, ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString(targetLoc, ("user", user)), target, target, PopupType.LargeCaution);

        // +5 ult charge.
        var hitEv = new ProperKungFuSpecialHitEvent(target, HammerMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    /// <summary>
    /// Hammer head damage, dealt in small hits: Shitmed only rolls a random dismemberment (and bone/organ traumas)
    /// when a wound grows by more than 10 at once, so the head can't randomly come off from the Hammer.
    /// </summary>
    private void DealHammerHeadDamage(EntityUid user, EntityUid target, DamageSpecifier damage)
    {
        DealChunkedDamage(user, target, damage, TargetBodyPart.Head);
    }

    /// <summary>
    /// Eyes and brain lose part of their integrity; the skull is only broken in the crit variant.
    /// </summary>
    private void InflictHammerTrauma(EntityUid target, HammerActionEvent args, bool breakSkull = false)
    {
        var eyes = args.EyeDamageFraction;
        var brain = args.BrainDamageFraction;
        InflictPartTrauma(target, TargetBodyPart.Head, "Blunt",
            (organ, organComp) =>
            {
                if (HasComp<EyesComponent>(organ))
                    return organComp.IntegrityCap * eyes;
                if (HasComp<BrainComponent>(organ))
                    return organComp.IntegrityCap * brain;
                return null;
            },
            breakBones: breakSkull);
    }

    /// <summary>
    /// Hammer on a Wolf-Grip-marked target: let go, 20 Blunt to the head (no knockdown), the user backflips away
    /// and for a moment a Wolf Dance on this target is an instant straight dash.
    /// </summary>
    private void DoHammerGripSynergy(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        DealHammerHeadDamage(user, target, args.GripDamage);
        InflictHammerTrauma(target, args);

        // Backflip: the user flies back away from the target (walls stop it).
        StartKnockback(user, -direction, args.BackflipDistance, args.BackflipDuration, dust: false);
        RaiseNetworkEvent(new ProperKungFuBackflipEvent(GetNetEntity(user), (float) args.BackflipDuration.TotalSeconds, -direction),
            Filter.Pvs(user, entityManager: EntityManager));

        var mark = EnsureComp<ProperKungFuHammerMarkComponent>(target);
        mark.Marker = user;
        mark.ExpiresAt = _timing.CurTime + args.DanceWindow;
    }

    private bool TryConsumeHammerMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuHammerMarkComponent>(target, out var mark)
            || mark.Marker != user
            || _timing.CurTime >= mark.ExpiresAt)
            return false;

        RemComp(target, mark);
        return true;
    }

    private bool TryConsumeWolfDanceMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuWolfDanceMarkComponent>(target, out var mark))
            return false;

        var valid = mark.Marker == user && _timing.CurTime < mark.ExpiresAt;
        if (valid)
            RemComp(target, mark);
        return valid;
    }

    /// <summary>
    /// Hammer on a Wolf-Dance-marked target: 20 Blunt to the body, an exact 5 tile knockback, landing knocked down.
    /// No eye/brain trauma and no sleep.
    /// </summary>
    private void DoHammerDanceVariant(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        _damageable.TryChangeDamage(target, args.DanceDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);

        var slide = TimeSpan.FromSeconds(args.DanceKnockbackDistance / MathF.Max(0.1f, args.DanceKnockbackSpeed));
        StartKnockback(target, direction, args.DanceKnockbackDistance, slide, dust: true);

        // Stays down as from the dance (a dance knockdown never has more left than this), lands on the floor.
        _stun.TryKnockdown(target, slide + args.DanceKnockdownTime, true);
    }

    private void UpdateHammerMarks(TimeSpan now)
    {
        var danceMarks = EntityQueryEnumerator<ProperKungFuWolfDanceMarkComponent>();
        while (danceMarks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }

        var marks = EntityQueryEnumerator<ProperKungFuHammerMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }
}

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
    [Dependency] private readonly BloodstreamSystem _bloodstream = default!;

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

        // Priority: crit > lying > Wolf Grip synergy > base hit.
        if (_mobState.IsCritical(target))
            SpawnHammerEffect(args.ImpactEffect, targetCoords);

        if (_mobState.IsCritical(target) && TryTearHeadApart(user, target, direction, args))
        {
            RemComp<ProperKungFuWolfGripMarkComponent>(target);
            FinishHammer(user, target, "proper-kung-fu-hammer-gib-user", "proper-kung-fu-hammer-gib-target", args.HitSound);
            return;
        }

        if (_standing.IsDown(target) || HasComp<KnockedDownComponent>(target))
        {
            SpawnHammerEffect(args.ImpactEffect, targetCoords);
            RemComp<ProperKungFuWolfGripMarkComponent>(target);
            _damageable.TryChangeDamage(target, args.LyingDamage, origin: user, targetPart: TargetBodyPart.Head, canMiss: false);
            InflictHammerTrauma(target, args);
            // After the damage, so the hit itself doesn't wake it up again.
            _statusNew.TryAddStatusEffectDuration(target, args.SleepStatusEffect, args.SleepTime);
            FinishHammer(user, target, "proper-kung-fu-hammer-sleep-user", "proper-kung-fu-hammer-sleep-target", args.HitSound);
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

        SpawnHammerEffect(args.ImpactEffect, targetCoords);
        _damageable.TryChangeDamage(target, args.Damage, origin: user, targetPart: TargetBodyPart.Head, canMiss: false);
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
    /// Eyes and brain lose part of their integrity; the skull itself is not broken by the Hammer.
    /// </summary>
    private void InflictHammerTrauma(EntityUid target, HammerActionEvent args)
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
            breakBones: false);
    }

    /// <summary>
    /// Shitmed: rips the head off the body and gibs it (organs and gore fly out), with a pool of blood.
    /// </summary>
    private bool TryTearHeadApart(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        var head = _body.GetBodyChildrenOfType(target, BodyPartType.Head).FirstOrDefault();
        if (head == default
            || !TryComp<WoundableComponent>(head.Id, out var woundable)
            || woundable.ParentWoundable is not { } parent
            || !woundable.CanRemove)
            return false;

        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        _wound.AmputateWoundable(parent, head.Id, woundable);

        // Detached now: blow it apart.
        if (!TerminatingOrDeleted(head.Id) && TryComp<BodyPartComponent>(head.Id, out var part) && part.Body == null)
        {
            _transform.SetCoordinates(head.Id, Transform(target).Coordinates);
            _body.GibPart(head.Id, part, splatDirection: direction, splatModifier: 2f, splatCone: Angle.FromDegrees(120), gibSoundOverride: args.GibSound);
        }

        if (args.GibBloodSpill > 0)
            _bloodstream.TryModifyBloodLevel(target, -args.GibBloodSpill);

        return true;
    }

    /// <summary>
    /// Hammer on a Wolf-Grip-marked target: let go, 20 Blunt to the head (no knockdown), the user backflips away
    /// and for a moment a Wolf Dance on this target is an instant straight dash.
    /// </summary>
    private void DoHammerGripSynergy(EntityUid user, EntityUid target, Vector2 direction, HammerActionEvent args)
    {
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);

        _damageable.TryChangeDamage(target, args.GripDamage, origin: user, targetPart: TargetBodyPart.Head, canMiss: false);
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

    private void UpdateHammerMarks(TimeSpan now)
    {
        var marks = EntityQueryEnumerator<ProperKungFuHammerMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }
}

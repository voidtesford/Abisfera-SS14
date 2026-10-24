using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Common.Weapons;
using Content.Goobstation.Maths.FixedPoint;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Body.Components;
using Content.Server.Weapons.Melee;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Medical.Surgery.Traumas;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Weapons.Melee;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Claw Whirlwind" (Водоворот когтей).
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly TraumaSystem _trauma = default!;
    [Dependency] private readonly WoundSystem _wound = default!;
    [Dependency] private readonly MeleeWeaponSystem _melee = default!;

    private const string ClawWhirlwindMove = "ClawWhirlwind";

    private void OnClawWhirlwind(Entity<ProperKungFuComponent> ent, ref ClawWhirlwindActionEvent args)
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

        // Same reach as the user's fists, checked the same way a light attack is.
        if (!TryComp<MeleeWeaponComponent>(user, out var melee))
            return;

        var rangeEv = new GetLightAttackRangeEvent(target, user, melee.Range);
        RaiseLocalEvent(user, ref rangeEv);
        if (!_melee.InRange(user, target, rangeEv.Range, null))
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-whirlwind-too-far"), user, user, PopupType.SmallCaution);
            return;
        }

        args.Handled = true;

        // Nobody keeps dragging the target around while it flies over the user.
        if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(target, pullable, user, true);
        if (TryComp<PullerComponent>(target, out var puller) && puller.Pulling is { } pulled
            && TryComp<PullableComponent>(pulled, out var pulledComp))
            _pulling.TryStopPull(pulled, pulledComp, target, true);

        RemComp<ProperKungFuSlidingComponent>(target);
        _physics.SetLinearVelocity(target, Vector2.Zero);

        var delta = targetCoords.Position - userCoords.Position;
        var toTarget = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();

        // Over the user to the opposite side; a wall right behind the user shortens the throw.
        var landing = userCoords.Position - toTarget * args.FlipDistance;
        if (IsPathBlocked(target, userCoords.MapId, userCoords.Position, landing, out var free))
            landing = free;

        var now = _timing.CurTime;
        var total = args.FlipDuration + args.AirDuration + args.LandHold;
        var held = EnsureComp<ClawWhirlwindedComponent>(target);
        held.User = user;
        held.Event = args;
        held.Map = userCoords.MapId;
        held.Phase = ClawWhirlwindPhase.Flip;
        held.PhaseStart = now;
        held.From = targetCoords.Position;
        held.To = landing;
        held.ReleaseAt = now + total;
        held.Landed = false;

        // Standing stun for the whole move: no attacking or using things while being thrown around.
        _stun.TryStun(target, total, true);

        // The user turns around with the throw.
        _transform.SetWorldRotation(user, (-toTarget).ToWorldAngle());

        SendWhirlwindAnimation(target, ClawWhirlwindPhase.Flip, (float) args.FlipDuration.TotalSeconds, landing - targetCoords.Position, 0.9f, 1f);
        _audio.PlayPvs(args.FlipSound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-whirlwind-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-whirlwind-target", ("user", user)), target, target, PopupType.MediumCaution);
    }

    private void UpdateWhirlwind(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ClawWhirlwindedComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var held, out var xform))
        {
            if (held.Event is not { } args
                || xform.MapID != held.Map
                || _container.IsEntityInContainer(uid))
            {
                EndWhirlwind(uid, landed: false);
                continue;
            }

            switch (held.Phase)
            {
                case ClawWhirlwindPhase.Flip:
                {
                    var duration = args.FlipDuration.TotalSeconds;
                    var t = duration <= 0 ? 1f : (float) Math.Clamp((now - held.PhaseStart).TotalSeconds / duration, 0, 1);
                    // Smooth arc: slow at both ends, fast over the user's head.
                    var eased = t * t * (3f - 2f * t);
                    _transform.SetWorldPosition(uid, Vector2.Lerp(held.From, held.To, eased));

                    if (t < 1f)
                        break;

                    // The thrower got taken out mid-throw: the target just drops.
                    if (TerminatingOrDeleted(held.User) || _mobState.IsIncapacitated(held.User))
                    {
                        Land(uid, held, args);
                        break;
                    }

                    DoKnee(held.User, uid, held, args);
                    held.Phase = ClawWhirlwindPhase.Air;
                    held.PhaseStart = now;
                    SendWhirlwindAnimation(uid, ClawWhirlwindPhase.Air, (float) args.AirDuration.TotalSeconds,
                        Vector2.Zero, args.LaunchHeight, args.Spins);
                    break;
                }
                case ClawWhirlwindPhase.Air:
                    if (now - held.PhaseStart >= args.AirDuration)
                        Land(uid, held, args);
                    break;
                case ClawWhirlwindPhase.Land:
                    if (now >= held.ReleaseAt)
                        RemCompDeferred(uid, held);
                    break;
            }
        }

        var marks = EntityQueryEnumerator<ProperKungFuWhirlwindMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }

    /// <summary>
    /// Knee into the jaw: head damage, broken skull, brain trauma, claw burst and the Wolf Dance mark.
    /// </summary>
    private void DoKnee(EntityUid user, EntityUid target, ClawWhirlwindedComponent held, ClawWhirlwindActionEvent args)
    {
        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(user);
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        // No shield rule here: the target is already in the air.
        _damageable.TryChangeDamage(target, args.Damage, origin: user, targetPart: TargetBodyPart.Head, canMiss: false);
        InflictHeadTrauma(target, args.BrainTraumaSeverity);

        if (args.ImpactEffect is { } effect)
        {
            // The burst points from the knee up through the jaw (the sprite is drawn pointing north).
            var burst = Spawn(effect, _transform.GetMapCoordinates(target));
            _transform.SetWorldRotation(burst, direction.ToWorldAngle() + Math.PI);
        }

        var mark = EnsureComp<ProperKungFuWhirlwindMarkComponent>(target);
        mark.Marker = user;
        mark.ExpiresAt = _timing.CurTime + args.MarkDuration;

        _audio.PlayPvs(args.KneeSound, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-whirlwind-knee-target"), target, target, PopupType.LargeCaution);

        // +5 ult charge.
        var hitEv = new ProperKungFuSpecialHitEvent(target, ClawWhirlwindMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    /// <summary>
    /// Shitmed: breaks the head's bone (skull) and damages the brain, the same way wound traumas do it.
    /// </summary>
    private void InflictHeadTrauma(EntityUid target, FixedPoint2 brainSeverity)
    {
        if (!TryComp<BodyComponent>(target, out var body))
            return;

        var (partType, symmetry) = _body.ConvertTargetBodyPart(TargetBodyPart.Head);
        var part = _body.GetBodyChildrenOfType(target, partType, body, symmetry).FirstOrDefault();
        var head = part.Id;
        if (!head.IsValid() || !TryComp<WoundableComponent>(head, out var woundable))
            return;

        // Traumas need an inflicter wound: reuse (or open) the Blunt wound on the head, like dismemberment does.
        Entity<TraumaInflicterComponent>? inflicter = null;
        if (_wound.TryInduceWound(head, "Blunt", 0f, out var wound, woundable))
            inflicter = (wound.Value.Owner, EnsureComp<TraumaInflicterComponent>(wound.Value.Owner));

        if (woundable.Bone.ContainedEntities.FirstOrDefault() is { } bone
            && bone.IsValid()
            && TryComp<BoneComponent>(bone, out var boneComp)
            && boneComp.BoneSeverity != BoneSeverity.Broken)
        {
            // Removing all the integrity left makes it Broken.
            if (inflicter is { } inf)
                _trauma.ApplyBoneTrauma(bone, (head, woundable), inf, boneComp.BoneIntegrity, boneComp);
            else
                _trauma.ApplyDamageToBone(bone, boneComp.BoneIntegrity, boneComp);
        }

        if (inflicter is not { } brainInflicter)
            return;

        foreach (var (organ, organComp) in _body.GetPartOrgans(head, part.Component))
        {
            if (!HasComp<BrainComponent>(organ))
                continue;

            var trauma = _trauma.AddTrauma(organ, (head, woundable), brainInflicter, TraumaType.OrganDamage, brainSeverity);
            if (!trauma.IsValid())
                break;
            if (!_trauma.TryCreateOrganDamageModifier(organ, brainSeverity, trauma, "WoundableDamage", organComp))
                _trauma.TryChangeOrganDamageModifier(organ, brainSeverity, trauma, "WoundableDamage", organComp);
            break;
        }
    }

    private void Land(EntityUid target, ClawWhirlwindedComponent held, ClawWhirlwindActionEvent args)
    {
        held.Phase = ClawWhirlwindPhase.Land;
        held.Landed = true;
        SendWhirlwindAnimation(target, ClawWhirlwindPhase.Land, 0f, Vector2.Zero, 0f, 0f);
        _stun.TryKnockdown(target, args.KnockdownTime, true);
        _audio.PlayPvs(args.LandSound, target);
    }

    /// <summary>
    /// Releases the target right away (interrupted, or a Wolf Dance kick takes over).
    /// </summary>
    private void EndWhirlwind(EntityUid target, bool landed)
    {
        if (!TryComp<ClawWhirlwindedComponent>(target, out var held))
            return;

        if (!held.Landed && !landed)
            SendWhirlwindAnimation(target, ClawWhirlwindPhase.Land, 0f, Vector2.Zero, 0f, 0f);

        RemComp(target, held);
    }

    private void SendWhirlwindAnimation(EntityUid target, ClawWhirlwindPhase phase, float duration, Vector2 direction, float height, float spins)
    {
        var ev = new ClawWhirlwindAnimationEvent(GetNetEntity(target), phase, duration, direction, height, spins);
        RaiseNetworkEvent(ev, Filter.Pvs(target, entityManager: EntityManager));
    }
}

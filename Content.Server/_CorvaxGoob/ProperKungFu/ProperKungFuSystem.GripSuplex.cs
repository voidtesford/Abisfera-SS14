using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Wolf Grip finisher: the held target is lifted over the user's back and driven head first into the floor
/// behind them (a bridge suplex). Breaks the skull, Blunt to the head and the torso, knockdown, and the Wolf Grip
/// mark is renewed on landing so the Whirlwind/Hammer follow-ups still work.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    private void StartGripSuplex(Entity<ProperKungFuComponent> ent, EntityUid target, ProperKungFuWolfGripPerformedEvent args)
    {
        var suplex = EnsureComp<ProperKungFuGripSuplexComponent>(target);
        suplex.User = ent;
        suplex.Map = _transform.GetMapId(target);
        suplex.Event = args;
        suplex.StartTime = _timing.CurTime + args.FinisherDelay;
        suplex.Lifted = false;

        // Locked until it hits the floor; the landing knockdown takes over from there.
        _stun.TryStun(target, args.FinisherDelay + args.FinisherDuration + TimeSpan.FromSeconds(0.2), true);
    }

    private void UpdateGripSuplexes(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuGripSuplexComponent>();
        while (query.MoveNext(out var uid, out var suplex))
        {
            UpdateGripSuplex(uid, suplex, now);
        }
    }

    private void UpdateGripSuplex(EntityUid target, ProperKungFuGripSuplexComponent suplex, TimeSpan now)
    {
        var user = suplex.User;
        if (suplex.Event is not { } args
            || TerminatingOrDeleted(user)
            || _transform.GetMapId(user) != suplex.Map
            || _transform.GetMapId(target) != suplex.Map
            || _container.IsEntityInContainer(target)
            || _container.IsEntityInContainer(user)
            || _mobState.IsIncapacitated(user))
        {
            RemCompDeferred(target, suplex);
            return;
        }

        if (now < suplex.StartTime)
            return;

        if (!suplex.Lifted)
        {
            // Let go of the grab and throw it up over the shoulder.
            if (TryComp<PullableComponent>(target, out var pullable) && pullable.BeingPulled)
                _pulling.TryStopPull(target, pullable, user, true);

            var userPos = _transform.GetWorldPosition(user);
            var from = _transform.GetWorldPosition(target);
            var delta = from - userPos;
            var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : _transform.GetWorldRotation(user).ToWorldVec();
            var to = userPos - dir * args.FinisherBehindDistance;
            if (IsPathBlocked(user, suplex.Map, userPos, to, out var free))
                to = free;

            suplex.From = from;
            suplex.To = to;
            suplex.Lifted = true;

            RemComp<ProperKungFuSlidingComponent>(target);
            _transform.SetWorldRotation(user, dir.ToWorldAngle());
            RaiseNetworkEvent(new ProperKungFuGripSuplexAnimationEvent(GetNetEntity(target),
                    (float) args.FinisherDuration.TotalSeconds,
                    args.FinisherHeight,
                    -dir),
                Filter.Pvs(target, entityManager: EntityManager));
            _audio.PlayPvs(args.FinisherLiftSound, target);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-finisher-user", ("target", target)), user, user, PopupType.Large);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-grip-finisher-target", ("user", user)), target, target, PopupType.LargeCaution);
        }

        var duration = args.FinisherDuration.TotalSeconds;
        var t = duration <= 0 ? 1f : (float) Math.Clamp((now - suplex.StartTime).TotalSeconds / duration, 0, 1);
        _physics.SetLinearVelocity(target, Vector2.Zero);
        _transform.SetWorldPosition(target, Vector2.Lerp(suplex.From, suplex.To, t));

        if (t < 1f)
            return;

        LandGripSuplex(user, target, args);
        RemCompDeferred(target, suplex);
    }

    private void LandGripSuplex(EntityUid user, EntityUid target, ProperKungFuWolfGripPerformedEvent args)
    {
        HealFinisherUser(user);

        // Head first: the skull breaks, then the head and the torso take the impact.
        InflictPartTrauma(target, TargetBodyPart.Head, "Blunt", (_, _) => null, breakBones: true);
        DealChunkedDamage(user, target, args.FinisherHeadDamage, TargetBodyPart.Head);
        DealChunkedDamage(user, target, args.FinisherTorsoDamage, TargetBodyPart.Chest);

        _stun.TryKnockdown(target, args.FinisherKnockdownTime, true);

        var coords = _transform.GetMapCoordinates(target);
        foreach (var effect in args.FinisherImpactEffects)
        {
            Spawn(effect, coords);
        }

        SpawnSlamFlourish(coords, ripple: true);
        KickFinisherCamera(user, coords.Position - _transform.GetWorldPosition(user), 0.45f);

        _audio.PlayPvs(args.FinisherSound, target);

        // The mark is renewed so the Whirlwind/Hammer follow-ups on a Wolf-Gripped target are still possible.
        var mark = EnsureComp<ProperKungFuWolfGripMarkComponent>(target);
        mark.Marker = user;
        mark.ExpiresAt = _timing.CurTime + args.MarkDuration;
    }
}

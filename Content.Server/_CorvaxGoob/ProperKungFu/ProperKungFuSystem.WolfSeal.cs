using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Popups;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Claw Mastery skill "Wolf Seal" (Волчья печать): dash in, uppercut, a black-and-white apex, a two-handed palm
/// thrust and a release that throws the target 5 tiles. The target can't breathe for 20 s (cleared on death) and is
/// knocked down, stunned and disoriented for 5 s. No limit per ultimate, only the 40 s cooldown.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    private static readonly EntProtoId SealImpactProto = "ProperKungFuSealImpact";
    private static readonly EntProtoId SealShockwaveProto = "ProperKungFuSealShockwave";
    private static readonly EntProtoId SealDustProto = "ProperKungFuSealDust";
    private static readonly EntProtoId SealSpeedLinesProto = "ProperKungFuSealSpeedLines";

    /// <summary>Closer than this the user strikes from where they stand; further they blink in first.</summary>
    private const float SealReach = 1.2f;

    public static readonly TimeSpan SealApexDelay = TimeSpan.FromSeconds(0.25);
    public static readonly TimeSpan SealApexLength = TimeSpan.FromSeconds(0.4);
    public static readonly TimeSpan SealPalmGap = TimeSpan.FromSeconds(0.1);
    public static readonly TimeSpan SealReleaseDelay = TimeSpan.FromSeconds(0.1);
    public static readonly TimeSpan SealFlightTime = TimeSpan.FromSeconds(0.5);

    private void InitializeWolfSeal()
    {
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuWolfSealActionEvent>(OnWolfSeal);
    }

    private void OnWolfSeal(Entity<ProperKungFuComponent> ent, ref ProperKungFuWolfSealActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        var target = args.Target;
        if (!ent.Comp.UltimateActive
            || HasComp<ProperKungFuCloneComponent>(user)
            || _mobState.IsIncapacitated(user)
            || HasComp<ProperKungFuSealComponent>(user)
            || IsUltimateFriend(user, target)
            || !HasComp<Content.Shared.Mobs.Components.MobStateComponent>(target)
            || _mobState.IsDead(target))
            return;

        var map = _transform.GetMapId(user);
        if (_transform.GetMapId(target) != map)
            return;

        var userPos = _transform.GetWorldPosition(user);
        var targetPos = _transform.GetWorldPosition(target);
        if ((targetPos - userPos).Length() > args.MaxRange)
            return;

        args.Handled = true;
        var dir = DirectionTo(user, target, _transform.GetWorldRotation(user).ToWorldVec());

        // Beyond arm's reach: a blink in, leaving afterimages.
        if ((targetPos - userPos).Length() > SealReach)
        {
            var spot = targetPos - dir * 0.8f;
            if (IsPathBlocked(user, map, userPos, spot, out var free))
                spot = free;
            for (var i = 0; i < 3; i++)
            {
                SpawnAfterimage(user, map, Vector2.Lerp(userPos, spot, i / 3f), DashAfterimageEffect);
            }

            _transform.SetWorldPosition(user, spot);
        }

        RemComp<ProperKungFuSlidingComponent>(user);
        RemComp<ProperKungFuSlidingComponent>(target);
        _transform.SetWorldRotation(user, dir.ToWorldAngle());
        _transform.SetWorldRotation(target, (-dir).ToWorldAngle());

        // Both held in place (the victim hangs at the top of the uppercut) until the release.
        var hold = (float) (SealApexDelay + SealApexLength + SealPalmGap + SealReleaseDelay).TotalSeconds + 0.3f;
        Freeze(target, hold, null);
        Freeze(user, hold, null);

        // The uppercut: 10 Blunt, the victim pops up.
        DealChunkedDamage(user, target, args.UppercutDamage, TargetBodyPart.Chest);
        SendPose(target, ProperKungFuPose.Jump, 0.9f, 0.45f, 0f);
        Spawn(PalmFlashEffect, _transform.GetMapCoordinates(target));
        _audio.PlayPvs(args.UppercutSound, target);

        var now = _timing.CurTime;
        var comp = EnsureComp<ProperKungFuSealComponent>(user);
        comp.Event = args;
        comp.Victim = target;
        comp.Map = map;
        comp.Direction = dir;
        comp.Stage = 0;
        comp.Trails = 0;
        comp.NextStep = now + SealApexDelay;
        comp.EndBy = now + TimeSpan.FromSeconds(4);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-seal-user", ("target", target)), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-seal-target", ("user", user)), target, target, PopupType.LargeCaution);
    }

    private void UpdateWolfSeals(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuSealComponent>();
        while (query.MoveNext(out var user, out var comp))
        {
            var victim = comp.Victim;
            if (comp.Event is not { } args
                || now >= comp.EndBy
                || TerminatingOrDeleted(victim)
                || _transform.GetMapId(victim) != comp.Map)
            {
                EndWolfSeal(user, comp);
                continue;
            }

            if (now < comp.NextStep)
                continue;

            var palmsDone = comp.Stage - 1;
            if (comp.Stage == 0)
            {
                // The apex: black, white silhouettes, horizontal speed lines.
                var mid = (_transform.GetWorldPosition(user) + _transform.GetWorldPosition(victim)) / 2f;
                SpawnTintZone(new MapCoordinates(mid, comp.Map), ProperKungFuTintMode.Silhouette, 3f, SealApexLength,
                    fadeIn: 0.03f, fadeOut: 0.06f);
                var lines = Spawn(SealSpeedLinesProto, new MapCoordinates(mid, comp.Map));
                _transform.SetWorldRotation(lines, comp.Direction.ToWorldAngle());
                comp.Stage = 1;
                comp.NextStep = now + SealApexLength;
            }
            else if (palmsDone < args.PalmHits)
            {
                // The two-handed palm thrust: 10 Blunt per palm into the chest, jagged white impact flashes.
                DealChunkedDamage(user, victim, args.PalmDamage, TargetBodyPart.Chest);
                var at = _transform.GetMapCoordinates(victim);
                var impact = Spawn(SealImpactProto, at);
                _transform.SetWorldRotation(impact, (comp.Direction.ToWorldAngle() + Math.PI * 0.25 * palmsDone));
                _audio.PlayPvs(args.PalmSound, victim);
                comp.Stage++;
                comp.NextStep = now + (palmsDone + 1 < args.PalmHits ? SealPalmGap : SealReleaseDelay);
            }
            else if (comp.Trails == 0)
            {
                ReleaseWolfSeal(user, victim, comp, args);
                comp.Trails = 1;
                comp.NextStep = now + TimeSpan.FromSeconds(0.06);
            }
            else if (comp.Trails < 8)
            {
                // Afterimages behind the flying victim.
                SpawnAfterimage(victim, comp.Map, _transform.GetWorldPosition(victim), TimeStopVictimAfterimage);
                comp.Trails++;
                comp.NextStep = now + TimeSpan.FromSeconds(0.06);
            }
            else
            {
                EndWolfSeal(user, comp);
            }
        }
    }

    private void ReleaseWolfSeal(EntityUid user, EntityUid victim, ProperKungFuSealComponent comp, ProperKungFuWolfSealActionEvent args)
    {
        Unfreeze(user);
        Unfreeze(victim);

        var at = _transform.GetMapCoordinates(victim);
        Spawn(SealShockwaveProto, at);
        Spawn(SealDustProto, at);
        _audio.PlayPvs(args.ReleaseSound, victim);

        StartKnockback(victim, comp.Direction, args.KnockbackDistance, SealFlightTime, true);
        SendPose(victim, ProperKungFuPose.Launch, (float) SealFlightTime.TotalSeconds, 0.3f, 1f);

        // 20 s without breath (cleared on death, never shortening a longer block) and a strong 5 s daze.
        BlockBreathing(victim, args.BreathBlockTime);
        if (HasComp<KravMagaBlockedBreathingComponent>(victim))
        {
            var breath = EnsureComp<ProperKungFuDeathPunchBreathComponent>(victim);
            var until = _timing.CurTime + args.BreathBlockTime;
            if (breath.ExpiresAt < until)
                breath.ExpiresAt = until;
        }

        _stun.TryParalyze(victim, args.DazeTime, true);
        ApplyDisorientation(victim, args.DisorientEffect, args.DazeTime);

        // The user sinks into a kneeling recovery.
        SendPose(user, ProperKungFuPose.Crouch, 0.8f, 0f, 0f);
    }

    private void EndWolfSeal(EntityUid user, ProperKungFuSealComponent comp)
    {
        Unfreeze(user);
        if (!TerminatingOrDeleted(comp.Victim))
            Unfreeze(comp.Victim);
        RemCompDeferred(user, comp);
    }
}

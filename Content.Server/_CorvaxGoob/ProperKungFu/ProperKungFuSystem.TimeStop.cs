using System.Numerics;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Goobstation.Wizard.FadingTimedDespawn;
using Content.Shared._Goobstation.Wizard.TimeStop;
using Content.Shared.Ghost;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Spawners;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// v13 time stop of the melee kunai finisher (一寸死拳): a 20-tile circle around the victim where everything but the
/// user is held with the Goob <see cref="FrozenComponent"/> (the wizard time stop's freeze), drawn grey on clients
/// (<see cref="ProperKungFuTintZoneComponent"/>). The punch lands 3 s in, the victim flies 3 tiles inside the
/// stopped time, and the other 17 tiles of knockback follow when time starts again at 5 s.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    public const float TimeStopRadius = 20f;
    public static readonly TimeSpan TimeStopDuration = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan TimeStopPunchAt = TimeSpan.FromSeconds(3);
    public const float TimeStopFlightDistance = 3f;
    public const float TimeStopAfterKnockback = DeathPunchKnockback - TimeStopFlightDistance;
    private static readonly TimeSpan TimeStopFlightTime = TimeSpan.FromSeconds(0.6);
    private static readonly TimeSpan TimeStopAfterKnockbackTime = TimeSpan.FromSeconds(1.0);
    private static readonly TimeSpan TimeStopResumeStun = TimeSpan.FromSeconds(1.8);
    private static readonly TimeSpan TimeStopScanInterval = TimeSpan.FromSeconds(0.1);
    private static readonly TimeSpan TimeStopTrailInterval = TimeSpan.FromSeconds(0.08);

    private static readonly EntProtoId TimeStopZoneProto = "ProperKungFuTimeStopZone";
    private static readonly EntProtoId TimeStopKanji = "ProperKungFuTimeStopKanji";
    private static readonly EntProtoId TimeStopShockwave = "ProperKungFuShockwaveEffect";
    private static readonly EntProtoId TimeStopVictimAfterimage = "ProperKungFuTimeStopAfterimage";
    private static readonly SoundSpecifier TimeStopSound = new SoundPathSpecifier("/Audio/_Goobstation/Wizard/timeparadox2.ogg");

    private void InitializeTimeStop()
    {
        SubscribeLocalEvent<ProperKungFuTimeStopZoneComponent, ComponentShutdown>(OnTimeStopZoneShutdown);
    }

    /// <summary>
    /// Starts a time stop circle at <paramref name="center"/>; only <paramref name="user"/> keeps moving.
    /// </summary>
    public EntityUid StartTimeStop(EntityUid user, MapCoordinates center, TimeSpan duration, float radius = TimeStopRadius)
    {
        var now = _timing.CurTime;
        var zone = Spawn(TimeStopZoneProto, center);
        var comp = EnsureComp<ProperKungFuTimeStopZoneComponent>(zone);
        comp.User = user;
        comp.Radius = radius;
        comp.End = now + duration;
        comp.NextScan = now;

        var tint = EnsureComp<ProperKungFuTintZoneComponent>(zone);
        tint.Mode = ProperKungFuTintMode.TimeStop;
        tint.Radius = radius;
        tint.Start = now;
        tint.End = comp.End;
        Dirty(zone, tint);

        EnsureComp<TimedDespawnComponent>(zone).Lifetime = (float) duration.TotalSeconds + 1f;
        _audio.PlayPvs(TimeStopSound, zone);
        ScanTimeStop(zone, comp, now);
        return zone;
    }

    private void UpdateTimeStops(TimeSpan now)
    {
        var zones = EntityQueryEnumerator<ProperKungFuTimeStopZoneComponent>();
        while (zones.MoveNext(out var zone, out var comp))
        {
            if (now >= comp.End)
            {
                EndTimeStop(zone);
                continue;
            }

            if (now < comp.NextScan)
                continue;

            comp.NextScan = now + TimeStopScanInterval;
            ScanTimeStop(zone, comp, now);
        }

        // Anything still marked by a zone that is gone moves again.
        var stopped = EntityQueryEnumerator<ProperKungFuTimeStoppedComponent>();
        while (stopped.MoveNext(out var uid, out var marker))
        {
            if (marker.Zone is { } zone && !HasComp<ProperKungFuTimeStopZoneComponent>(zone))
                Unfreeze(uid);
        }
    }

    private void ScanTimeStop(EntityUid zone, ProperKungFuTimeStopZoneComponent comp, TimeSpan now)
    {
        var center = _transform.GetMapCoordinates(zone);
        var left = (float) (comp.End - now).TotalSeconds;
        foreach (var uid in _lookup.GetEntitiesInRange(center, comp.Radius, LookupFlags.Dynamic))
        {
            if (uid == comp.User
                || uid == zone
                || HasComp<FrozenComponent>(uid)
                || HasComp<GhostComponent>(uid)
                || _container.IsEntityInContainer(uid))
                continue;

            if (!HasComp<MobStateComponent>(uid)
                && !HasComp<ProjectileComponent>(uid)
                && !HasComp<ThrownItemComponent>(uid)
                && !(TryComp<PhysicsComponent>(uid, out var body) && body.LinearVelocity.LengthSquared() > 0.0001f))
                continue;

            Freeze(uid, left + 1f, zone);
        }
    }

    /// <summary>
    /// Holds <paramref name="uid"/> with the Goob freeze for up to <paramref name="seconds"/>; its despawn and
    /// landing timers wait as long as it is held (the freeze gives that time back when it ends early).
    /// </summary>
    private void Freeze(EntityUid uid, float seconds, EntityUid? zone)
    {
        if (HasComp<FrozenComponent>(uid))
            return;

        EnsureComp<FrozenComponent>(uid).FreezeTime = seconds;
        if (TryComp<TimedDespawnComponent>(uid, out var despawn))
            despawn.Lifetime += seconds;

        if (TryComp<FadingTimedDespawnComponent>(uid, out var fading) && !fading.FadeOutStarted)
            fading.Lifetime += seconds;

        if (TryComp<ThrownItemComponent>(uid, out var thrown) && thrown.LandTime != null)
        {
            thrown.LandTime = thrown.LandTime.Value + TimeSpan.FromSeconds(seconds);
            thrown.Animate = false;
            Dirty(uid, thrown);
        }

        EnsureComp<ProperKungFuTimeStoppedComponent>(uid).Zone = zone;
    }

    private void Unfreeze(EntityUid uid)
    {
        if (!HasComp<ProperKungFuTimeStoppedComponent>(uid))
            return;

        RemComp<ProperKungFuTimeStoppedComponent>(uid);
        RemComp<FrozenComponent>(uid);
    }

    public void EndTimeStop(EntityUid zone)
    {
        if (TerminatingOrDeleted(zone))
            return;

        RemComp<ProperKungFuTimeStopZoneComponent>(zone);
        QueueDel(zone);
    }

    private void OnTimeStopZoneShutdown(Entity<ProperKungFuTimeStopZoneComponent> ent, ref ComponentShutdown args)
    {
        var stopped = EntityQueryEnumerator<ProperKungFuTimeStoppedComponent>();
        var release = new List<EntityUid>();
        while (stopped.MoveNext(out var uid, out var marker))
        {
            if (marker.Zone == ent.Owner)
                release.Add(uid);
        }

        foreach (var uid in release)
        {
            Unfreeze(uid);
        }
    }

    // ---- Melee kunai finisher ----

    /// <summary>
    /// The punch kunai arrived: the user dashes in front of the victim and time stops around it.
    /// </summary>
    private void StartDeathPunchTimeStop(EntityUid user, EntityUid victim, ProperKungFuKunaiFinisherComponent finisher)
    {
        var map = finisher.Map;
        var now = _timing.CurTime;
        var userPos = _transform.GetWorldPosition(user);
        var victimPos = _transform.GetWorldPosition(victim);
        var delta = victimPos - userPos;
        var dir = delta.LengthSquared() > 0.0001f ? Vector2.Normalize(delta) : finisher.Direction;
        finisher.DashDirection = dir;

        // Dash right in front of the victim, red silhouettes left on the way.
        var spot = victimPos - dir * 0.7f;
        if (IsPathBlocked(user, map, victimPos, spot, out var free))
            spot = free;

        for (var i = 0; i < 3; i++)
        {
            SpawnAfterimage(user, map, Vector2.Lerp(userPos, spot, i / 3f), DashAfterimageEffect);
        }

        RemComp<ProperKungFuSlidingComponent>(user);
        RemComp<ProperKungFuSlidingComponent>(victim);
        _physics.SetLinearVelocity(user, Vector2.Zero);
        _transform.SetWorldPosition(user, spot);
        _transform.SetWorldRotation(user, dir.ToWorldAngle());

        finisher.Zone = StartTimeStop(user, new MapCoordinates(victimPos, map), TimeStopDuration);
        finisher.ZoneEnd = now + TimeStopDuration;

        // 一寸死拳 fades in behind the victim and stays until time moves again.
        var kanjiPos = victimPos + dir * DeathPunchKanjiDistance;
        if (IsPathBlocked(victim, map, victimPos, kanjiPos, out var kanjiFree))
            kanjiPos = kanjiFree;
        Spawn(TimeStopKanji, new MapCoordinates(kanjiPos, map));

        finisher.Phase = 10;
        finisher.NextStep = now + TimeStopPunchAt;
        finisher.EndBy = now + TimeStopDuration + TimeSpan.FromSeconds(3);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-time-stop-user"), user, user, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-time-stop-others"), victim,
            Filter.PvsExcept(user, entityManager: EntityManager), true, PopupType.LargeCaution);
    }

    /// <summary>
    /// Phases 10-12 of the melee kunai finisher (inside the stopped time). False when the phase is not one of them.
    /// </summary>
    private bool UpdateDeathPunchTimeStop(EntityUid user, ProperKungFuKunaiFinisherComponent finisher, TimeSpan now)
    {
        var victim = finisher.Victim;
        var map = finisher.Map;
        switch (finisher.Phase)
        {
            case 10:
            {
                // The punch: flash, ring and residue stay where it landed; no knockback yet.
                DoDeathPunch(user, victim, finisher, knockback: false, kanji: false);
                var from = _transform.GetWorldPosition(victim);
                var to = from + finisher.DashDirection * TimeStopFlightDistance;
                if (IsPathBlocked(victim, map, from, to, out var free))
                    to = free;

                finisher.FlightFrom = from;
                finisher.FlightTo = to;
                finisher.FlightStart = now;
                finisher.NextTrail = now;
                finisher.Trails = 0;
                finisher.Phase = 11;
                finisher.NextStep = now;
                return true;
            }
            case 11:
            {
                // Three tiles of flight through the stopped time, with afterimages, shockwaves and wisps.
                var t = (float) Math.Clamp((now - finisher.FlightStart).TotalSeconds / TimeStopFlightTime.TotalSeconds, 0, 1);
                var eased = 1f - (1f - t) * (1f - t);
                var pos = Vector2.Lerp(finisher.FlightFrom, finisher.FlightTo, eased);
                _transform.SetWorldPosition(victim, pos);

                if (now >= finisher.NextTrail)
                {
                    finisher.NextTrail = now + TimeStopTrailInterval;
                    SpawnAfterimage(victim, map, pos, TimeStopVictimAfterimage);
                    if (finisher.Trails % 3 == 0)
                    {
                        var ring = Spawn(TimeStopShockwave, new MapCoordinates(pos, map));
                        _transform.SetWorldRotation(ring, finisher.DashDirection.ToWorldAngle());
                    }

                    var wisp = new Angle(_instinctRandom.NextFloat(0f, MathHelper.TwoPi)).ToVec() * _instinctRandom.NextFloat(0.2f, 0.6f);
                    Spawn(DeathPunchWisp, new MapCoordinates(pos + wisp, map));
                    finisher.Trails++;
                }

                if (t < 1f)
                    return true;

                finisher.Phase = 12;
                finisher.NextStep = finisher.ZoneEnd;
                return true;
            }
            case 12:
            {
                // Time moves again: the other 17 tiles of the knockback follow at once.
                if (finisher.Zone is { } zone)
                    EndTimeStop(zone);
                finisher.Zone = null;

                RemComp<ProperKungFuSlidingComponent>(victim);
                StartKnockback(victim, finisher.DashDirection, TimeStopAfterKnockback, TimeStopAfterKnockbackTime, dust: true);
                _stun.TryStun(victim, TimeStopResumeStun, true);
                _popup.PopupEntity(Loc.GetString("proper-kung-fu-time-resume"), victim, PopupType.Medium);
                RemCompDeferred(user, finisher);
                return true;
            }
        }

        return false;
    }
}

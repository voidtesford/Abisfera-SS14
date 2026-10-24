using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Wolf Instinct" (Волчье чутьё): the counter stance. The parry itself is predicted by
/// <see cref="SharedWolfInstinctSystem"/>; everything that moves or hurts someone happens here, on the next update
/// (never inside a physics contact callback).
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly SharedStaminaSystem _stamina = default!;
    [Dependency] private readonly SharedWolfInstinctSystem _instinct = default!;
    [Dependency] private readonly IRobustRandom _instinctRandom = default!;

    private const string WolfInstinctMove = "WolfInstinct";

    private void InitializeWolfInstinct()
    {
        SubscribeLocalEvent<ProperKungFuComponent, WolfInstinctActionEvent>(OnWolfInstinct);
        SubscribeLocalEvent<WolfInstinctStanceComponent, WolfInstinctParriedEvent>(OnInstinctParried);
        SubscribeLocalEvent<WolfInstinctStanceComponent, ProjectileReflectAttemptEvent>(OnInstinctProjectile);
        SubscribeLocalEvent<WolfInstinctStanceComponent, HitScanReflectAttemptEvent>(OnInstinctHitscan);
        SubscribeLocalEvent<WolfInstinctStanceComponent, DamageChangedEvent>(OnInstinctDamaged);
        SubscribeLocalEvent<WolfInstinctStanceComponent, ThrowHitByEvent>(OnInstinctThrowHit);
        SubscribeLocalEvent<WolfInstinctStanceComponent, ComponentShutdown>(OnInstinctShutdown);
    }

    private void OnWolfInstinct(Entity<ProperKungFuComponent> ent, ref WolfInstinctActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        if (_mobState.IsIncapacitated(user)
            || !TryComp<MartialArtsKnowledgeComponent>(user, out var knowledge)
            || knowledge.MartialArtsForm != MartialArtsForms.ProperKungFu
            || knowledge.Blocked)
            return;

        args.Handled = true;

        var stance = EnsureComp<WolfInstinctStanceComponent>(user);
        stance.EndTime = _timing.CurTime + args.Duration;
        stance.CancelMeleeDamage = args.CancelMeleeDamage;
        stance.Event = args;
        stance.PendingShooter = null;
        stance.DashAt = null;
        Dirty(user, stance);

        if (stance.Aura is not { } aura || TerminatingOrDeleted(aura))
            stance.Aura = args.AuraEffect is { } auraProto ? SpawnAttachedTo(auraProto, new EntityCoordinates(user, Vector2.Zero)) : null;

        _audio.PlayPvs(args.StanceSound, user);
        _audio.PlayPvs(args.StanceRingSound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-user"), user, user, PopupType.Medium);
    }

    private void OnInstinctShutdown(Entity<WolfInstinctStanceComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.Aura is { } aura && !TerminatingOrDeleted(aura))
            QueueDel(aura);

        ent.Comp.Aura = null;
    }

    private void EndInstinct(EntityUid user)
    {
        RemCompDeferred<WolfInstinctStanceComponent>(user);
    }

    private bool CanCounter(EntityUid user, WolfInstinctStanceComponent stance)
    {
        return stance.Event != null
               && _instinct.IsActive(stance)
               && !_mobState.IsIncapacitated(user);
    }

    // ---- Melee ----

    private void OnInstinctParried(Entity<WolfInstinctStanceComponent> ent, ref WolfInstinctParriedEvent args)
    {
        if (!CanCounter(ent, ent.Comp) || ent.Comp.PendingCounters.Contains(args.Attacker))
            return;

        ent.Comp.PendingCounters.Add(args.Attacker);

        if (!ent.Comp.Event!.AllowMultipleCounters)
            ent.Comp.EndTime = _timing.CurTime; // the parry above still went through, nothing after it
    }

    /// <summary>
    /// Two palms into the attacker's chest: Slash and stamina damage, thrown back on their feet (no knockdown).
    /// </summary>
    private void DoInstinctCounter(EntityUid user, EntityUid attacker, WolfInstinctActionEvent args)
    {
        if (TerminatingOrDeleted(attacker) || _transform.GetMapId(attacker) != _transform.GetMapId(user))
            return;

        var delta = _transform.GetWorldPosition(attacker) - _transform.GetWorldPosition(user);
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        _damageable.TryChangeDamage(attacker, args.CounterDamage, origin: user, targetPart: SkillTargetPart, canMiss: false);
        _stamina.TakeStaminaDamage(attacker, args.CounterStaminaDamage, source: user, immediate: true);

        if (TryComp<PullableComponent>(attacker, out var pullable) && pullable.BeingPulled)
            _pulling.TryStopPull(attacker, pullable, user, true);

        StartKnockback(attacker,
            direction,
            args.CounterKnockbackDistance,
            TimeSpan.FromSeconds(args.CounterKnockbackDistance / MathF.Max(0.1f, args.CounterKnockbackSpeed)),
            dust: true);

        if (args.CounterEffect is { } effect)
        {
            // Bright three-claw slash at a random angle every hit.
            var fx = Spawn(effect, _transform.GetMapCoordinates(attacker));
            _transform.SetWorldRotation(fx, _instinctRandom.NextAngle());
        }

        _audio.PlayPvs(args.CounterSound, attacker);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-counter-user", ("target", attacker)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-counter-target", ("user", user)), attacker, attacker, PopupType.MediumCaution);

        // +5 ult per counter.
        var hitEv = new ProperKungFuSpecialHitEvent(attacker, WolfInstinctMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    // ---- Guns ----

    private void OnInstinctProjectile(Entity<WolfInstinctStanceComponent> ent, ref ProjectileReflectAttemptEvent args)
    {
        if (args.Cancelled
            || args.Component.Shooter is not { } shooter
            || shooter == ent.Owner
            || args.Component.Weapon is not { } weapon
            || !HasComp<GunComponent>(weapon)
            || !CanCounter(ent, ent.Comp))
            return;

        // Confirmed in OnInstinctDamaged once the bullet actually hurts.
        ent.Comp.PendingShooter = shooter;
    }

    private void OnInstinctHitscan(Entity<WolfInstinctStanceComponent> ent, ref HitScanReflectAttemptEvent args)
    {
        if (args.Reflected
            || args.Shooter is not { } shooter
            || shooter == ent.Owner
            || !HasComp<GunComponent>(args.SourceItem)
            || !CanCounter(ent, ent.Comp))
            return;

        ent.Comp.PendingShooter = shooter;
    }

    private void OnInstinctDamaged(EntityUid uid, WolfInstinctStanceComponent comp, DamageChangedEvent args)
    {
        var ent = new Entity<WolfInstinctStanceComponent>(uid, comp);
        if (ent.Comp.PendingShooter is not { } shooter
            || args.Origin != shooter
            || !args.DamageIncreased
            || ent.Comp.DashAt != null)
            return;

        ent.Comp.PendingShooter = null;
        if (CanCounter(ent, ent.Comp))
            ent.Comp.DashAt = shooter;
    }

    private void StartInstinctDash(EntityUid user, EntityUid shooter, WolfInstinctActionEvent args)
    {
        if (TerminatingOrDeleted(shooter))
            return;

        var userCoords = _transform.GetMapCoordinates(user);
        var shooterCoords = _transform.GetMapCoordinates(shooter);
        if (userCoords.MapId != shooterCoords.MapId)
            return;

        var delta = shooterCoords.Position - userCoords.Position;
        var distance = delta.Length();
        var direction = distance > 0.01f ? delta / distance : _transform.GetWorldRotation(user).ToWorldVec();

        // Too far: dash the full range in that direction and miss.
        var length = distance <= args.DashRange
            ? MathF.Max(0f, distance - args.DashStopDistance)
            : args.DashRange;

        RemComp<ProperKungFuSlidingComponent>(user);
        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        var now = _timing.CurTime;
        var dash = EnsureComp<ProperKungFuInstinctDashComponent>(user);
        dash.Shooter = shooter;
        dash.Map = userCoords.MapId;
        dash.From = userCoords.Position;
        dash.To = userCoords.Position + direction * length;
        dash.StartTime = now;
        dash.EndTime = now + TimeSpan.FromSeconds(length / MathF.Max(1f, args.DashSpeed));
        dash.Event = args;
        dash.NextAfterimage = now;

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-dash-user", ("target", shooter)), user, user, PopupType.Medium);
    }

    private void UpdateInstinctDashes(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuInstinctDashComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var dash, out var xform))
        {
            if (xform.MapID != dash.Map || _container.IsEntityInContainer(uid) || _mobState.IsIncapacitated(uid))
            {
                RemCompDeferred(uid, dash);
                continue;
            }

            var total = (dash.EndTime - dash.StartTime).TotalSeconds;
            var t = total <= 0 ? 1f : (float) Math.Clamp((now - dash.StartTime).TotalSeconds / total, 0, 1);
            var current = _transform.GetWorldPosition(xform);
            var desired = Vector2.Lerp(dash.From, dash.To, t);
            var blocked = IsPathBlocked(uid, dash.Map, current, desired, out var free);
            _transform.SetWorldPosition(uid, blocked ? free : desired);

            if (dash.Event?.AfterimageEffect is { } afterimage && now >= dash.NextAfterimage)
            {
                SpawnAfterimage(uid, dash.Map, blocked ? free : desired, afterimage);
                dash.NextAfterimage = now + TimeSpan.FromSeconds(0.04);
            }

            if (t < 1f && !blocked)
                continue;

            RemCompDeferred(uid, dash);
            if (dash.Event is { } args)
                FinishInstinctDash(uid, dash.Shooter, args);
        }
    }

    /// <summary>
    /// End of the dash: a shooter within reach is floored, loses the guns in their hands and is taken into a hard grab.
    /// No damage.
    /// </summary>
    private void FinishInstinctDash(EntityUid user, EntityUid shooter, WolfInstinctActionEvent args)
    {
        if (TerminatingOrDeleted(shooter)
            || _transform.GetMapId(shooter) != _transform.GetMapId(user)
            || Vector2.Distance(_transform.GetWorldPosition(user), _transform.GetWorldPosition(shooter)) > args.DashReach)
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-dash-miss"), user, user, PopupType.SmallCaution);
            return;
        }

        var delta = _transform.GetWorldPosition(shooter) - _transform.GetWorldPosition(user);
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(user).ToWorldVec();

        // Knock the guns out of the hands, to the side.
        var side = new Vector2(-direction.Y, direction.X);
        foreach (var held in _hands.EnumerateHeld(shooter))
        {
            if (!HasComp<GunComponent>(held) || !_hands.TryDrop(shooter, held, checkActionBlocker: false))
                continue;

            _throwing.TryThrow(held, (direction + side) * args.GunThrowDistance * 0.7f, 8f, user,
                pushbackRatio: 0f, recoil: false, playSound: false);
        }

        _stun.TryKnockdown(shooter, args.ShooterKnockdownTime, true);
        TakeIntoHardGrab(user, shooter, args.GrabHoldDistance);

        _audio.PlayPvs(args.DashHitSound, shooter);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-catch-user", ("target", shooter)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-catch-target", ("user", user)), shooter, shooter, PopupType.LargeCaution);

        var hitEv = new ProperKungFuSpecialHitEvent(shooter, WolfInstinctMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    // ---- Thrown objects ----

    private void OnInstinctThrowHit(EntityUid uid, WolfInstinctStanceComponent comp, ThrowHitByEvent args)
    {
        var ent = new Entity<WolfInstinctStanceComponent>(uid, comp);
        if (args.Handled
            || HasComp<MobStateComponent>(args.Thrown)
            || !CanCounter(ent, ent.Comp))
            return;

        var stance = ent.Comp;
        if (stance.Event!.BlockThrownDamage)
            args.Handled = true;

        if (args.Component.Thrower is not { } thrower
            || thrower == ent.Owner
            || stance.PendingReflects.Exists(r => r.Item == args.Thrown))
            return;

        var speed = TryComp<PhysicsComponent>(args.Thrown, out var physics) ? physics.LinearVelocity.Length() : 0f;
        stance.PendingReflects.Add((args.Thrown, thrower, speed));
    }

    private void ReflectThrown(EntityUid user, EntityUid item, EntityUid thrower, float speed, WolfInstinctActionEvent args)
    {
        if (TerminatingOrDeleted(item)
            || TerminatingOrDeleted(thrower)
            || _container.IsEntityInContainer(item)
            || _transform.GetMapId(item) != _transform.GetMapId(thrower))
            return;

        var delta = _transform.GetWorldPosition(thrower) - _transform.GetWorldPosition(item);
        var distance = delta.Length();
        if (distance < 0.01f)
            return;

        if (speed < 0.5f)
            speed = args.FallbackThrowSpeed;

        // Back along the line to the thrower, a bit past them so it still flies when it arrives.
        _physics.SetLinearVelocity(item, Vector2.Zero);
        _physics.SetAngularVelocity(item, 0f);
        _throwing.TryThrow(item,
            delta / distance * (distance + 1f),
            speed * args.ReflectSpeedMultiplier,
            user,
            pushbackRatio: 0f,
            recoil: false,
            playSound: false);

        _audio.PlayPvs(args.ReflectSound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-instinct-reflect-user", ("item", item)), user, user, PopupType.Small);
    }

    // ---- Update ----

    private void UpdateWolfInstinct(TimeSpan now)
    {
        var query = EntityQueryEnumerator<WolfInstinctStanceComponent>();
        while (query.MoveNext(out var uid, out var stance))
        {
            // The damage of a bullet always follows its attempt event right away: anything left over was blocked.
            stance.PendingShooter = null;

            if (stance.Event is not { } args)
            {
                EndInstinct(uid);
                continue;
            }

            if (stance.PendingCounters.Count > 0)
            {
                var counters = stance.PendingCounters.ToArray();
                stance.PendingCounters.Clear();
                foreach (var attacker in counters)
                {
                    DoInstinctCounter(uid, attacker, args);
                }
            }

            if (stance.PendingReflects.Count > 0)
            {
                var reflects = stance.PendingReflects.ToArray();
                stance.PendingReflects.Clear();
                foreach (var (item, thrower, speed) in reflects)
                {
                    ReflectThrown(uid, item, thrower, speed, args);
                }
            }

            if (stance.DashAt is { } shooter)
            {
                stance.DashAt = null;
                StartInstinctDash(uid, shooter, args);
                if (args.GunEndsStance)
                {
                    EndInstinct(uid);
                    continue;
                }
            }

            if (now >= stance.EndTime || _mobState.IsIncapacitated(uid))
                EndInstinct(uid);
        }

        UpdateInstinctDashes(now);
    }
}

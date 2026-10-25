using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Goobstation.Wizard.Projectiles;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions;
using Content.Shared.Blocking;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Movement.Systems;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Server.Audio;
using Robust.Shared.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Events;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Server logic of the "Proper Kung Fu" martial art: grants its actions and performs the special moves.
/// </summary>
public sealed partial class ProperKungFuSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;

    /// <summary>
    /// Where the Blunt of every special move except Hammer lands. Only Hammer targets the head.
    /// </summary>
    private const TargetBodyPart SkillTargetPart = TargetBodyPart.Chest;

    private const string WolfDanceMove = "WolfDance";
    private const string ClawSeriesMove = "ClawSeries";

    /// <summary>
    /// Claw Series knockback (tiles) and how long its mark enables the Wolf Dance synergy.
    /// </summary>
    private const float ClawSeriesKnockback = 3f;
    private static readonly TimeSpan ClawMarkDuration = TimeSpan.FromSeconds(2);

    // Claw Series visuals (white-red finisher flash and dust).
    private static readonly EntProtoId PalmFlashEffect = "ProperKungFuPalmFlash";
    private static readonly EntProtoId DustPuffWhite = "ProperKungFuDustPuffWhite";
    private static readonly EntProtoId DustPuffRed = "ProperKungFuDustPuffRed";
    private static readonly TimeSpan SlideDuration = TimeSpan.FromSeconds(0.4);
    private static readonly TimeSpan DustPuffInterval = TimeSpan.FromSeconds(0.06);

    /// <summary>
    /// Distance kept from walls while moving, roughly a mob's collision radius.
    /// </summary>
    private const float BodyClearance = 0.4f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProperKungFuComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ProperKungFuComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ProperKungFuComponent, WolfDanceActionEvent>(OnWolfDance);
        SubscribeLocalEvent<WolfDancingComponent, PreventCollideEvent>(OnDancingPreventCollide);
        SubscribeLocalEvent<ProperKungFuDanceFinisherComponent, PreventCollideEvent>(OnDanceFinisherPreventCollide);
        SubscribeLocalEvent<ProperKungFuHammerFinisherComponent, PreventCollideEvent>(OnHammerFinisherPreventCollide);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuSpecialHitEvent>(OnSpecialHit);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuClawSeriesPerformedEvent>(OnClawSeries);
        SubscribeLocalEvent<ProperKungFuComponent, ClawWhirlwindActionEvent>(OnClawWhirlwind);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuWolfGripPerformedEvent>(OnWolfGrip);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuPerfectFlowPerformedEvent>(OnPerfectFlow);
        SubscribeLocalEvent<ProperKungFuComponent, HammerActionEvent>(OnHammer);
        InitializeWolfInstinct();
        InitializePassives();
        InitializeWolfPack();
        InitializeKunai();
        InitializeKunaiFinishers();
        InitializeJoinPack();
        InitializeUltimate();
    }

    private void OnStartup(Entity<ProperKungFuComponent> ent, ref ComponentStartup args)
    {
        foreach (var proto in ent.Comp.BaseActions)
        {
            if (_actions.AddAction(ent, proto) is { } action)
                ent.Comp.ActionEntities.Add(action);
        }

        // Passive +stamina (zero for a pupil). Attack rate and sprint speed are applied by SharedMartialArtsSystem.ProperKungFu.
        if (ent.Comp.HasPassives && TryComp<StaminaComponent>(ent, out var stamina))
        {
            stamina.CritThreshold += ent.Comp.BonusStamina;
            ent.Comp.AppliedBonusStamina = ent.Comp.BonusStamina;
            Dirty(ent.Owner, stamina);
        }

        // Fist hits draw red wolf claw scratches instead of the normal fist sprite.
        if (TryComp<MeleeWeaponComponent>(ent, out var melee))
        {
            ent.Comp.OriginalAnimation = melee.Animation;
            ent.Comp.OriginalWideAnimation = melee.WideAnimation;
            ent.Comp.OriginalMissAnimation = melee.MissAnimation;
            melee.Animation = ent.Comp.ClawAnimation;
            melee.WideAnimation = ent.Comp.ClawAnimation;
            melee.MissAnimation = ent.Comp.ClawAnimation;
            Dirty(ent.Owner, melee);
        }

        _movementSpeed.RefreshMovementSpeedModifiers(ent);
        Dirty(ent);
    }

    private void OnShutdown(Entity<ProperKungFuComponent> ent, ref ComponentShutdown args)
    {
        foreach (var action in ent.Comp.ActionEntities)
        {
            _actions.RemoveAction(ent.Owner, action);
        }

        ent.Comp.ActionEntities.Clear();

        foreach (var action in ent.Comp.UltimateActionEntities)
        {
            _actions.RemoveAction(ent.Owner, action);
        }

        ent.Comp.UltimateActionEntities.Clear();

        if (TerminatingOrDeleted(ent))
            return;

        if (ent.Comp.AppliedBonusStamina != 0f && TryComp<StaminaComponent>(ent, out var stamina))
        {
            stamina.CritThreshold -= ent.Comp.AppliedBonusStamina;
            Dirty(ent.Owner, stamina);
        }

        ent.Comp.AppliedBonusStamina = 0f;

        // Give the normal fist animations back (only the ones we still own).
        if (TryComp<MeleeWeaponComponent>(ent, out var melee))
        {
            if (ent.Comp.OriginalAnimation is { } anim && melee.Animation == ent.Comp.ClawAnimation)
                melee.Animation = anim;
            if (ent.Comp.OriginalWideAnimation is { } wide && melee.WideAnimation == ent.Comp.ClawAnimation)
                melee.WideAnimation = wide;
            if (ent.Comp.OriginalMissAnimation is { } miss && melee.MissAnimation == ent.Comp.ClawAnimation)
                melee.MissAnimation = miss;
            Dirty(ent.Owner, melee);
        }

        // The component is still attached during shutdown, so the speed refresh is skipped for it.
        _movementSpeed.RefreshMovementSpeedModifiers(ent);
    }

    private void OnSpecialHit(Entity<ProperKungFuComponent> ent, ref ProperKungFuSpecialHitEvent args)
    {
        // TODO ProperKungFu ultimate: this only fills the meter; activation on crit is not implemented yet.
        if (args.Blocked || ent.Comp.UltimateActive)
            return;

        AddUltimateCharge(ent, ent.Comp.ChargePerSpecialHit);
    }

    /// <summary>
    /// "Claw Series": the third fist hit in a row adds 10 Blunt, throws the target 3 tiles back,
    /// stuns it for 2 s and marks it for the Wolf Dance synergy.
    /// </summary>
    private void OnClawSeries(Entity<ProperKungFuComponent> ent, ref ProperKungFuClawSeriesPerformedEvent args)
    {
        if (!TryComp<CanPerformComboComponent>(ent, out var combo)
            || combo.CurrentTarget is not { } target
            || TerminatingOrDeleted(target)
            || !_proto.TryIndex(combo.BeingPerformed, out var proto)
            || !IsKungFuComboForm(ent, proto.MartialArtsForm)
            || !TryComp<MartialArtsKnowledgeComponent>(ent, out var knowledge)
            || knowledge.MartialArtsForm != proto.MartialArtsForm
            || knowledge.Blocked)
            return;

        // Start the next series from scratch instead of chaining hit 4, 5... into new combos.
        combo.LastAttacks.Clear();
        Dirty(ent.Owner, combo);

        if (!proto.CanDoWhileProne && TryComp<StandingStateComponent>(ent, out var standing)
            && standing.CurrentState != StandingState.Standing)
            return;

        // Only Hammer goes for the head: the combo bonus always lands on the torso.
        var targetPart = SkillTargetPart;

        // Finisher threshold is measured before the combo bonus lands.
        var preDamage = TryComp<DamageableComponent>(target, out var preDamageable) ? preDamageable.TotalDamage.Float() : 0f;
        var scale = EffectScale(ent);
        var finisher = !ent.Comp.Pupil && ent.Comp.ClawSeriesFinisherThreshold > 0f && preDamage >= ent.Comp.ClawSeriesFinisherThreshold
                       && !_mobState.IsDead(target)
                       && !HasComp<ProperKungFuSeriesFinisherComponent>(ent);

        var damage = new DamageSpecifier();
        damage.DamageDict.Add(proto.DamageType, proto.ExtraDamage);
        DealKungFuDamage(target, damage, origin: ent, targetPart: targetPart);

        // Standing stun: StunnedComponent only, no knockdown and nothing is dropped.
        _stun.TryStun(target, TimeSpan.FromSeconds(proto.ParalyzeTime * scale), true);
        Spawn(PalmFlashEffect, _transform.GetMapCoordinates(target));

        if (TryComp<PullableComponent>(target, out var pullable))
            _pulling.TryStopPull(target, pullable, ent, true);

        var delta = _transform.GetWorldPosition(target) - _transform.GetWorldPosition(ent);
        var direction = delta.LengthSquared() > 0.0001f
            ? Vector2.Normalize(delta)
            : _transform.GetWorldRotation(ent).ToWorldVec();

        // Slides back exactly 3 tiles on its feet, kicking up dust; walls stop it.
        StartKnockback(target, direction, ClawSeriesKnockback * scale, SlideDuration, dust: true);

        // The Wolf Dance synergy mark; a pupil has no synergies.
        if (!ent.Comp.Pupil)
        {
            var mark = EnsureComp<ProperKungFuClawMarkComponent>(target);
            mark.Marker = ent;
            mark.ExpiresAt = _timing.CurTime + ClawMarkDuration;
        }

        _audio.PlayPvs(new SoundPathSpecifier("/Audio/Weapons/genhit3.ogg"), target);

        if (finisher && !TerminatingOrDeleted(target))
        {
            StartClawSeriesFinisher(ent, target, direction);
        }
        else
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-user", ("target", target)), ent, ent, PopupType.Medium);
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-claw-series-target", ("user", ent.Owner)), target, target, PopupType.MediumCaution);
        }

        if (!ent.Comp.UltimateActive)
            AddUltimateCharge(ent, ent.Comp.ChargePerCombo);
    }

    /// <summary>
    /// The Proper Kung Fu combo forms: the full art and the pupil one.
    /// </summary>
    private static bool IsKungFuComboForm(Entity<ProperKungFuComponent> ent, MartialArtsForms form)
    {
        return ent.Comp.Pupil ? form == MartialArtsForms.ProperKungFuPupil : form == MartialArtsForms.ProperKungFu;
    }

    /// <summary>
    /// Scale of the non-damage effects of a combo: 1 for the art, halved for a pupil.
    /// </summary>
    private static float EffectScale(Entity<ProperKungFuComponent> ent)
    {
        return ent.Comp.Pupil ? ent.Comp.PupilEffectMultiplier : 1f;
    }

    public void AddUltimateCharge(Entity<ProperKungFuComponent> ent, float amount)
    {
        // Wolf Pack clones and pupils never charge anything.
        if (ent.Comp.Pupil || HasComp<ProperKungFuCloneComponent>(ent))
            return;

        var newCharge = Math.Clamp(ent.Comp.UltimateCharge + amount, 0f, ent.Comp.MaxUltimateCharge);
        if (MathHelper.CloseTo(newCharge, ent.Comp.UltimateCharge))
            return;

        ent.Comp.UltimateCharge = newCharge;
        Dirty(ent);
    }

    private void OnWolfDance(Entity<ProperKungFuComponent> ent, ref WolfDanceActionEvent args)
    {
        if (args.Handled)
            return;

        var user = args.Performer;
        var target = args.Target;

        if (user == target || !HasComp<MobStateComponent>(target) || HasComp<WolfDancingComponent>(user))
            return;

        if (!TryComp<MartialArtsKnowledgeComponent>(user, out var knowledge)
            || knowledge.MartialArtsForm != MartialArtsForms.ProperKungFu
            || knowledge.Blocked)
            return;

        var userCoords = _transform.GetMapCoordinates(user);
        var targetCoords = _transform.GetMapCoordinates(target);
        if (userCoords.MapId != targetCoords.MapId)
            return;

        var delta = targetCoords.Position - userCoords.Position;
        var distance = delta.Length();
        if (distance > args.MaxRange)
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-too-far"), user, user, PopupType.SmallCaution);
            return;
        }

        args.Handled = true;

        var direction = distance > 0.01f
            ? delta / distance
            : _transform.GetWorldRotation(user).ToWorldVec();

        _transform.SetWorldRotation(user, direction.ToWorldAngle());

        // Right after Claw Whirlwind: a faster flying kick that hits harder. The mark is consumed now,
        // so it can't run out mid-flight.
        var whirlwind = TryConsumeWhirlwindMark(user, target);

        // Right after the Hammer's Wolf Grip synergy: an instant flying kick with the normal kick effects.
        var hammer = !whirlwind && TryConsumeHammerMark(user, target);
        if (hammer)
            RemComp<ProperKungFuSlidingComponent>(user); // cut the backflip short

        // CorvaxGoob-ProperKungFu-Start: every variant is the same fast dash through the target and the user never
        // falls; the synergies (Claw Series / Claw Whirlwind / Hammer grip) only change what the hit does to the victim.
        var kickVariant = whirlwind || hammer || HasFreshClawMark(user, target);
        var duration = whirlwind ? args.WhirlwindDashDuration
            : hammer ? args.HammerDashDuration
            : kickVariant ? args.DashDuration
            : args.PassThroughDashDuration;
        StartPassThrough(user, target, userCoords, direction, distance, args, kickVariant, whirlwind, duration);
    }
    // CorvaxGoob-ProperKungFu-End

    private bool TryConsumeWhirlwindMark(EntityUid user, EntityUid target)
    {
        if (!TryComp<ProperKungFuWhirlwindMarkComponent>(target, out var mark)
            || mark.Marker != user
            || _timing.CurTime >= mark.ExpiresAt)
            return false;

        RemComp(target, mark);
        return true;
    }

    /// <summary>
    /// Raycasts for walls/windows/doors between two points. <paramref name="free"/> is the furthest safe point.
    /// </summary>
    /// <summary>
    /// Pushes <paramref name="target"/> <paramref name="distance"/> tiles along <paramref name="direction"/>
    /// over <paramref name="duration"/>, decelerating; see the sliding part of <see cref="Update"/>.
    /// </summary>
    private void StartKnockback(EntityUid target, Vector2 direction, float distance, TimeSpan duration, bool dust)
    {
        var now = _timing.CurTime;
        var from = _transform.GetWorldPosition(target);
        var sliding = EnsureComp<ProperKungFuSlidingComponent>(target);
        sliding.Map = _transform.GetMapId(target);
        sliding.From = from;
        sliding.To = from + direction * distance;
        sliding.StartTime = now;
        sliding.EndTime = now + duration;
        sliding.Dust = dust;
        sliding.NextPuff = now;
        sliding.Puffs = 0;
        _physics.SetLinearVelocity(target, Vector2.Zero);
    }

    private bool IsPathBlocked(EntityUid user, MapId map, Vector2 from, Vector2 to, out Vector2 free)
    {
        free = to;
        var delta = to - from;
        var length = delta.Length();
        if (length < 0.01f)
            return false;

        var dir = delta / length;
        // A little extra length so the user's body (radius ~0.35) never ends up inside a wall.
        var ray = new CollisionRay(from, dir, (int) CollisionGroup.Impassable);
        foreach (var hit in _physics.IntersectRay(map, ray, length + BodyClearance, user, returnOnFirstHit: true))
        {
            free = from + dir * MathF.Max(0f, hit.Distance - BodyClearance);
            return true;
        }

        return false;
    }

    private void StartPhase(EntityUid user, WolfDancingComponent dancing, WolfDancePhase phase, Vector2 from, Vector2 to, TimeSpan duration)
    {
        dancing.Phase = phase;
        dancing.PhaseStart = _timing.CurTime;
        dancing.PhaseDuration = duration;
        dancing.PhaseFrom = from;
        dancing.PhaseTo = to;
        dancing.NextAfterimage = _timing.CurTime;

        var dir = to - from;
        if (dir.LengthSquared() > 0.0001f)
            _transform.SetWorldRotation(user, (Vector2.Normalize(dir)).ToWorldAngle());

        SendAnimation(user, phase, (float) duration.TotalSeconds, dir);
    }

    private void SendAnimation(EntityUid user, WolfDancePhase phase, float duration, Vector2 direction)
    {
        var ev = new WolfDanceAnimationEvent(GetNetEntity(user), phase, duration, direction);
        RaiseNetworkEvent(ev, Filter.Pvs(user, entityManager: EntityManager));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<WolfDancingComponent>();
        while (query.MoveNext(out var uid, out var dancing))
        {
            UpdateDance(uid, dancing, now);
        }

        var slides = EntityQueryEnumerator<ProperKungFuSlidingComponent, TransformComponent>();
        while (slides.MoveNext(out var uid, out var sliding, out var xform))
        {
            if (xform.MapID != sliding.Map || _container.IsEntityInContainer(uid))
            {
                RemCompDeferred(uid, sliding);
                continue;
            }

            var total = (sliding.EndTime - sliding.StartTime).TotalSeconds;
            var t = total <= 0 ? 1f : (float) Math.Clamp((now - sliding.StartTime).TotalSeconds / total, 0, 1);
            var eased = 1f - (1f - t) * (1f - t); // fast start, skids to a stop
            var current = _transform.GetWorldPosition(xform);
            var desired = Vector2.Lerp(sliding.From, sliding.To, eased);
            var blocked = IsPathBlocked(uid, sliding.Map, current, desired, out var free);
            _transform.SetWorldPosition(uid, blocked ? free : desired);

            if (t >= 1f || blocked)
            {
                RemCompDeferred(uid, sliding);
                continue;
            }

            if (!sliding.Dust || now < sliding.NextPuff)
                continue;

            Spawn(sliding.Puffs % 2 == 0 ? DustPuffWhite : DustPuffRed, xform.Coordinates);
            sliding.Puffs++;
            sliding.NextPuff = now + DustPuffInterval;
        }

        UpdateWolfDanceTosses(now);
        UpdateDanceFinishers(now);
        UpdateWhirlwind(now);
        UpdateWolfGripMarks(now);
        UpdateHammerMarks(now);
        UpdateHammerFinishers(now);
        UpdateBleedBoosts();
        UpdateWolfInstinct(now);
        UpdatePerfectFlow(now);
        UpdateClawSeriesFinishers(now);
        UpdateGripSuplexes(now);
        UpdateWolfPack(now);
        UpdateKunai(now);
        UpdateKunaiFinishers(now);
        UpdateBloodlust(now);
        UpdateUltimate(now);

        var marks = EntityQueryEnumerator<ProperKungFuClawMarkComponent>();
        while (marks.MoveNext(out var uid, out var mark))
        {
            if (now >= mark.ExpiresAt)
                RemCompDeferred(uid, mark);
        }
    }

    private void UpdateDance(EntityUid user, WolfDancingComponent dancing, TimeSpan now)
    {
        if (dancing.Event is not { } args)
        {
            StopDance(user, false);
            return;
        }

        // Interrupted: stunned, knocked down, crit/dead, put into a container, or moved to another map.
        if (_mobState.IsIncapacitated(user)
            || HasComp<StunnedComponent>(user)
            || HasComp<KnockedDownComponent>(user)
            || _container.IsEntityInContainer(user)
            || _transform.GetMapId(user) != dancing.Map)
        {
            StopDance(user, false);
            return;
        }

        var target = dancing.Target;
        var targetValid = !TerminatingOrDeleted(target) && _transform.GetMapId(target) == dancing.Map;

        if (dancing.PassThrough)
        {
            UpdatePassThrough(user, dancing, args, now, targetValid);
            return;
        }

        // CorvaxGoob-ProperKungFu-Start: the old flying kick is gone, every variant is the dash through the target.
        StopDance(user, false);
    }
    // CorvaxGoob-ProperKungFu-End

    private void StopDance(EntityUid user, bool kicking)
    {
        RemComp<WolfDancingComponent>(user);

        if (!kicking)
            SendAnimation(user, WolfDancePhase.Cancel, 0f, Vector2.Zero);
    }

    /// <summary>
    /// The synergy Wolf Dance hit, landed as the user dashes through the target (no kick pose, the user stays up).
    /// </summary>
    private bool DoKickHit(EntityUid user, EntityUid target, WolfDanceActionEvent args, bool whirlwind, Vector2 direction)
    {
        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, _transform.GetMapCoordinates(target));

        if (TryKnockShieldAway(user, target, direction, args))
        {
            var blockedEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, true);
            RaiseLocalEvent(user, ref blockedEv);
            return false;
        }

        // Claw Whirlwind variant (takes priority over the Claw Series mark, which is consumed as well).
        if (whirlwind)
        {
            RemComp<ProperKungFuClawMarkComponent>(target);
            DoWhirlwindKick(user, target, direction, args);
            return true;
        }

        // Claw Series synergy: a fresh mark from this user turns the kick into a harder finisher without the stun.
        var synergy = TryComp<ProperKungFuClawMarkComponent>(target, out var mark)
            && mark.Marker == user
            && _timing.CurTime < mark.ExpiresAt;

        if (mark != null)
            RemComp(target, mark);

        _audio.PlayPvs(args.HitSound, target);
        DealKungFuDamage(target,
            synergy ? args.SynergyDamage : args.Damage,
            origin: user,
            targetPart: SkillTargetPart,
            canMiss: false);

        if (synergy)
        {
            // Falls, but can still act (no StunnedComponent).
            _stun.TryKnockdown(target, args.SynergyKnockdownTime, true);
        }
        else
        {
            // Paralyze = stun + knockdown, so the target falls and stays down for the duration.
            _stun.TryParalyze(target, args.StunTime, true);
        }

        // Fly backwards, away from the user.
        // Kinematic, so the distance is exact (a thrown mob kept sliding far past it) and walls stop it.
        var knockback = synergy ? args.SynergyKnockbackDistance : args.KnockbackDistance;
        StartKnockback(target,
            direction,
            knockback,
            TimeSpan.FromSeconds(knockback / MathF.Max(0.1f, args.KnockbackSpeed)),
            dust: false);

        MarkWolfDance(user, target);

        var hitUserLoc = synergy ? "proper-kung-fu-wolf-dance-synergy-user" : "proper-kung-fu-wolf-dance-hit-user";
        var hitTargetLoc = synergy ? "proper-kung-fu-wolf-dance-synergy-target" : "proper-kung-fu-wolf-dance-hit-target";
        _popup.PopupEntity(Loc.GetString(hitUserLoc, ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString(hitTargetLoc, ("user", user)), target, target, PopupType.MediumCaution);

        var hitEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, false);
        RaiseLocalEvent(user, ref hitEv);
        return true;
    }

    /// <summary>
    /// Wolf Dance right after Claw Whirlwind: 15 Blunt + 15 Slash, the target flies 5 tiles and falls.
    /// The user stays on their feet.
    /// </summary>
    private void DoWhirlwindKick(EntityUid user, EntityUid target, Vector2 direction, WolfDanceActionEvent args)
    {
        // The target may still be spinning in the air: the kick takes over.
        if (HasComp<ClawWhirlwindedComponent>(target))
            EndWhirlwind(target, landed: false);

        _audio.PlayPvs(args.HitSound, target);
        DealKungFuDamage(target,
            args.WhirlwindSynergyDamage,
            origin: user,
            targetPart: SkillTargetPart,
            canMiss: false);

        _stun.TryKnockdown(target, args.WhirlwindKnockdownTime, true);
        StartKnockback(target,
            direction,
            args.WhirlwindKnockbackDistance,
            TimeSpan.FromSeconds(args.WhirlwindKnockbackDistance / MathF.Max(0.1f, args.KnockbackSpeed)),
            dust: false);

        MarkWolfDance(user, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-whirlwind-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-whirlwind-target", ("user", user)), target, target, PopupType.MediumCaution);

        var hitEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, false);
        RaiseLocalEvent(user, ref hitEv);
    }

    /// <summary>
    /// If the target holds an active-capable shield (anything with <see cref="BlockingComponent"/>) the kick is
    /// fully blocked, but the shield is knocked out of their hands.
    /// </summary>
    private bool TryKnockShieldAway(EntityUid user, EntityUid target, Vector2 direction, WolfDanceActionEvent args)
    {
        EntityUid? shield = null;
        foreach (var held in _hands.EnumerateHeld(target))
        {
            if (!HasComp<BlockingComponent>(held))
                continue;

            shield = held;
            break;
        }

        if (shield is not { } shieldUid)
            return false;

        if (_hands.TryDrop(target, shieldUid, checkActionBlocker: false))
        {
            _throwing.TryThrow(shieldUid,
                direction * 1.5f,
                args.ShieldThrowSpeed,
                user,
                pushbackRatio: 0f,
                recoil: false,
                playSound: false);
        }

        _audio.PlayPvs(args.BlockedSound, target);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-blocked-user", ("target", target), ("shield", shieldUid)), user, user, PopupType.MediumCaution);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-blocked-target", ("user", user), ("shield", shieldUid)), target, target, PopupType.MediumCaution);
        return true;
    }

    private EntityUid SpawnAfterimage(EntityUid user, MapId map, Vector2 pos, EntProtoId proto)
    {
        var effect = Spawn(proto, new MapCoordinates(pos, map));
        if (!TryComp<TrailComponent>(effect, out var trail))
            return effect;

        trail.RenderedEntity = user;
        trail.SpawnPosition = pos;
        Dirty(effect, trail);
        return effect;
    }
}

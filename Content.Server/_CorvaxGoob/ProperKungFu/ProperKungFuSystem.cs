using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Server.Popups;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Goobstation.Wizard.Projectiles;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions;
using Content.Shared.Blocking;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Popups;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Server logic of the "Proper Kung Fu" martial art: grants its actions and performs the special moves.
/// </summary>
public sealed class ProperKungFuSystem : EntitySystem
{
    [Dependency] private readonly SharedActionsSystem _actions = default!;
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly PopupSystem _popup = default!;
    [Dependency] private readonly SharedStunSystem _stun = default!;
    [Dependency] private readonly ThrowingSystem _throwing = default!;
    [Dependency] private readonly TransformSystem _transform = default!;

    private const string WolfDanceMove = "WolfDance";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProperKungFuComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<ProperKungFuComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<ProperKungFuComponent, WolfDanceActionEvent>(OnWolfDance);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuSpecialHitEvent>(OnSpecialHit);
    }

    private void OnStartup(Entity<ProperKungFuComponent> ent, ref ComponentStartup args)
    {
        foreach (var proto in ent.Comp.BaseActions)
        {
            if (_actions.AddAction(ent, proto) is { } action)
                ent.Comp.ActionEntities.Add(action);
        }

        Dirty(ent);
    }

    private void OnShutdown(Entity<ProperKungFuComponent> ent, ref ComponentShutdown args)
    {
        foreach (var action in ent.Comp.ActionEntities)
        {
            _actions.RemoveAction(ent.Owner, action);
        }

        ent.Comp.ActionEntities.Clear();
    }

    private void OnSpecialHit(Entity<ProperKungFuComponent> ent, ref ProperKungFuSpecialHitEvent args)
    {
        // TODO ProperKungFu ultimate: this only fills the meter; activation on crit is not implemented yet.
        if (args.Blocked || ent.Comp.UltimateActive)
            return;

        AddUltimateCharge(ent, ent.Comp.ChargePerSpecialHit);
    }

    public void AddUltimateCharge(Entity<ProperKungFuComponent> ent, float amount)
    {
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

        if (user == target || !HasComp<MobStateComponent>(target))
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

        var direction = distance > 0.01f
            ? delta / distance
            : _transform.GetWorldRotation(user).ToWorldVec();

        // Dash: stop right in front of the target, on the user's side.
        var dashLength = MathF.Max(0f, distance - args.StopDistance);
        var start = userCoords.Position;
        var landing = start + direction * dashLength;

        if (dashLength > 0.1f)
        {
            SpawnAfterimages(user, userCoords.MapId, start, landing, args);
            _transform.SetWorldPosition(user, landing);
        }

        _transform.SetWorldRotation(user, direction.ToWorldAngle());
        _audio.PlayPvs(args.DashSound, user);

        args.Handled = true;

        // Roundhouse kick.
        if (args.ShockwaveEffect is { } shockwave)
            Spawn(shockwave, targetCoords);

        if (TryKnockShieldAway(user, target, direction, args))
        {
            var blockedEv = new ProperKungFuSpecialHitEvent(target, WolfDanceMove, true);
            RaiseLocalEvent(user, ref blockedEv);
            return;
        }

        _audio.PlayPvs(args.HitSound, target);
        _damageable.TryChangeDamage(target,
            args.Damage,
            origin: user,
            targetPart: TargetBodyPart.Head,
            canMiss: false);

        // Paralyze = stun + knockdown, so the target falls and stays down for the duration.
        _stun.TryParalyze(target, args.StunTime, true);

        // Fly backwards, away from the user.
        _throwing.TryThrow(target,
            direction * args.KnockbackDistance,
            args.KnockbackSpeed,
            user,
            pushbackRatio: 0f,
            compensateFriction: true,
            recoil: false,
            animated: false,
            playSound: false,
            doSpin: false);

        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-hit-user", ("target", target)), user, user, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-dance-hit-target", ("user", user)), target, target, PopupType.MediumCaution);

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

    private void SpawnAfterimages(EntityUid user, MapId map, Vector2 start, Vector2 end, WolfDanceActionEvent args)
    {
        if (args.AfterimageEffect is not { } proto || args.Afterimages <= 0)
            return;

        for (var i = 0; i < args.Afterimages; i++)
        {
            var t = (float) i / args.Afterimages;
            var pos = Vector2.Lerp(start, end, t);
            var effect = Spawn(proto, new MapCoordinates(pos, map));

            if (!TryComp<TrailComponent>(effect, out var trail))
                continue;

            trail.RenderedEntity = user;
            trail.SpawnPosition = pos;
            Dirty(effect, trail);
        }
    }
}

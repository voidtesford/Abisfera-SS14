using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Goobstation.Shared.Religion.Nullrod.Components;
using Content.Server.Administration.Systems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Interaction.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Popups;
using Robust.Shared.Audio;
using Robust.Shared.Map;
using Robust.Shared.Player;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// The ultimate: the bar fills from fighting (special hits, combos and damage taken). With a full bar the master
/// either starts the mode with the "Ultimate" action, or falls into crit and, 5 seconds later, gets up fully healed in it.
/// For 4 minutes the skills (not the combos) are replaced by the ultimate-only skills, then everything goes back.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly RejuvenateSystem _rejuvenate = default!;

    public const string UltimateAction = "ActionProperKungFuUltimate";

    private const float UltimateCloneSearchRange = 10f;
    public const string UltimateShadowFaction = "ProperKungFuShadow";
    private static readonly TimeSpan PackCallDuration = TimeSpan.FromSeconds(2);
    private static readonly SoundSpecifier UltimateStartSound = new SoundPathSpecifier("/Audio/Magic/forcewall.ogg");

    private void InitializeUltimate()
    {
        SubscribeLocalEvent<ProperKungFuComponent, DamageChangedEvent>(OnUltimateDamaged);
        SubscribeLocalEvent<ProperKungFuComponent, MobStateChangedEvent>(OnUltimateMobState);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuUltimateActionEvent>(OnUltimateAction);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuUltimateClonesActionEvent>(OnUltimateClones);
        SubscribeLocalEvent<ProperKungFuComponent, ProperKungFuSummonPackActionEvent>(OnSummonPack);
    }

    private void OnUltimateDamaged(Entity<ProperKungFuComponent> ent, ref DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta is not { } delta || ent.Comp.UltimateActive || !ent.Comp.HasPassives)
            return;

        var taken = delta.GetTotal().Float();
        if (taken > 0f)
            AddUltimateCharge(ent, taken * ent.Comp.ChargePerDamage);
    }

    private void OnUltimateMobState(Entity<ProperKungFuComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
        {
            ent.Comp.UltimatePendingAt = null;
            return;
        }

        if (args.NewMobState != MobState.Critical
            || !ent.Comp.HasPassives
            || ent.Comp.UltimateActive
            || ent.Comp.UltimatePendingAt != null
            || ent.Comp.UltimateCharge < ent.Comp.MaxUltimateCharge)
            return;

        ent.Comp.UltimatePendingAt = _timing.CurTime + ent.Comp.UltimateActivationDelay;
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-pending"), ent, ent, PopupType.Large);
    }

    private void OnUltimateAction(Entity<ProperKungFuComponent> ent, ref ProperKungFuUltimateActionEvent args)
    {
        if (args.Handled || !ent.Comp.HasPassives)
            return;

        if (ent.Comp.UltimateActive)
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-already"), ent, ent);
            return;
        }

        if (ent.Comp.UltimateCharge < ent.Comp.MaxUltimateCharge)
        {
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-not-ready",
                ("percent", (int) MathF.Floor(ent.Comp.UltimateCharge / ent.Comp.MaxUltimateCharge * 100f))), ent, ent);
            return;
        }

        StartUltimate(ent);
        args.Handled = true;
    }

    public void StartUltimate(Entity<ProperKungFuComponent> ent)
    {
        var comp = ent.Comp;
        if (comp.UltimateActive)
            return;

        var now = _timing.CurTime;
        comp.UltimatePendingAt = null;
        comp.UltimateActive = true;
        comp.UltimateCharge = 0f;
        comp.UltimateEndTime = now + comp.UltimateDuration;
        comp.UltimateClonesUsed = false;
        comp.UltimateJudgmentUsed = false;
        comp.UltimateGazeUsed = false;

        // The combos shorten to 2 inputs for the mode.
        SwapComboList(ent, comp.UltimateCombos);

        // Skills out, ultimate skills in.
        foreach (var action in comp.ActionEntities)
        {
            _actions.RemoveAction(ent.Owner, action);
        }

        comp.ActionEntities.Clear();
        foreach (var proto in comp.UltimateActions)
        {
            if (_actions.AddAction(ent, proto) is { } action)
                comp.UltimateActionEntities.Add(action);
        }

        Dirty(ent);
        SpawnSkillFlashes(ent);
        SpawnUltimateMist(ent);
        _audio.PlayPvs(UltimateStartSound, ent);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-start-user"), ent, ent, PopupType.Large);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-start-others", ("user", ent.Owner)), ent,
            Filter.PvsExcept(ent, entityManager: EntityManager), true, PopupType.LargeCaution);
    }

    public void EndUltimate(Entity<ProperKungFuComponent> ent)
    {
        var comp = ent.Comp;
        if (!comp.UltimateActive)
            return;

        comp.UltimateActive = false;
        comp.UltimateEndTime = null;
        comp.UltimateClonesUsed = false;
        comp.UltimateJudgmentUsed = false;
        comp.UltimateGazeUsed = false;
        RemComp<ProperKungFuUltimateSpawnerComponent>(ent);
        SwapComboList(ent, comp.RegularCombos);
        EndJudgment(ent.Owner);
        EndGazeStance(ent.Owner);

        foreach (var action in comp.UltimateActionEntities)
        {
            _actions.RemoveAction(ent.Owner, action);
        }

        comp.UltimateActionEntities.Clear();
        foreach (var proto in comp.BaseActions)
        {
            if (_actions.AddAction(ent, proto) is { } action)
                comp.ActionEntities.Add(action);
        }

        // The shadows live 60 s each, but never outlast the mode.
        var clones = EntityQueryEnumerator<ProperKungFuCloneComponent>();
        while (clones.MoveNext(out var uid, out var clone))
        {
            if (clone.Ultimate && clone.Summoner == ent.Owner)
                VanishClone(uid, clone);
        }

        Dirty(ent);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-end"), ent, ent, PopupType.Medium);
    }

    /// <summary>
    /// Replaces the allowed combos with the given comboList and clears the attack history, so nothing half-typed
    /// carries over between the regular and the Claw Mastery sequences.
    /// </summary>
    private void SwapComboList(EntityUid uid, string listId)
    {
        if (!TryComp<CanPerformComboComponent>(uid, out var combo)
            || !_proto.TryIndex<ComboListPrototype>(listId, out var list))
            return;

        combo.AllowedCombos.Clear();
        foreach (var id in list.Combos)
        {
            if (_proto.TryIndex(id, out var proto))
                combo.AllowedCombos.Add(proto);
        }

        combo.LastAttacks.Clear();
        Dirty(uid, combo);
    }

    private void UpdateUltimate(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.UltimatePendingAt is { } pending && now >= pending)
            {
                comp.UltimatePendingAt = null;
                if (!_mobState.IsDead(uid) && !comp.UltimateActive)
                {
                    // Back on the feet, fully healed, and straight into the mode.
                    _rejuvenate.PerformRejuvenate(uid);
                    _standing.Stand(uid);
                    StartUltimate((uid, comp));
                }
            }

            if (comp.UltimateActive && comp.UltimateEndTime is { } end && now >= end)
                EndUltimate((uid, comp));
        }

        var spawners = EntityQueryEnumerator<ProperKungFuUltimateSpawnerComponent, ProperKungFuComponent>();
        while (spawners.MoveNext(out var uid, out var spawner, out var comp))
        {
            if (!comp.UltimateActive || spawner.Remaining <= 0)
            {
                RemCompDeferred(uid, spawner);
                continue;
            }

            if (now < spawner.NextBatch)
                continue;

            // First wave: one at a time every 0.5 s. Later waves: PerSecond at once, every second.
            spawner.NextBatch = now + (spawner.OneByOne ? spawner.Args.FirstInterval : TimeSpan.FromSeconds(1));
            var batch = spawner.OneByOne ? 1 : Math.Min(spawner.Remaining, Math.Max(1, spawner.Args.PerSecond));
            SpawnUltimateBatch(uid, comp, spawner, batch);
            spawner.Remaining -= batch;
        }
    }

    private void OnUltimateClones(Entity<ProperKungFuComponent> ent, ref ProperKungFuUltimateClonesActionEvent args)
    {
        if (args.Handled || !ent.Comp.UltimateActive)
            return;

        // First use in the mode: the full twenty. Every later use: a smaller wave of the same shadows.
        var first = !ent.Comp.UltimateClonesUsed;
        var count = first ? args.Count : args.ReuseCount;
        ent.Comp.UltimateClonesUsed = true;

        var spawner = EnsureComp<ProperKungFuUltimateSpawnerComponent>(ent);
        if (spawner.Remaining <= 0)
        {
            spawner.Spawned = 0;
            spawner.NextBatch = _timing.CurTime;
            spawner.OneByOne = first;
        }

        spawner.Args = args;
        spawner.Remaining += Math.Max(0, count);

        _audio.PlayPvs(args.Sound, ent);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-ultimate-clones-user", ("count", count)), ent, ent, PopupType.Medium);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-pack-others", ("user", ent.Owner)), ent,
            Filter.PvsExcept(ent, entityManager: EntityManager), true, PopupType.MediumCaution);
        args.Handled = true;
    }

    private void SpawnUltimateBatch(EntityUid user, ProperKungFuComponent comp, ProperKungFuUltimateSpawnerComponent spawner, int batch)
    {
        var args = spawner.Args;
        var xform = Transform(user);
        var map = xform.MapID;
        var origin = _transform.GetWorldPosition(xform);
        var leader = EnsureComp<ProperKungFuPackLeaderComponent>(user);
        var now = _timing.CurTime;

        for (var i = 0; i < batch; i++)
        {
            var angle = new Angle(MathF.Tau * (spawner.Spawned % 10) / 10f + _instinctRandom.NextFloat(-0.2f, 0.2f));
            var spot = origin + angle.ToVec() * args.SpawnRadius;
            if (IsPathBlocked(user, map, origin, spot, out var free))
                spot = free;

            var coords = new MapCoordinates(spot, map);
            var clone = SpawnCloneBody(user, coords, args.CloneHealth);

            // No Proper Kung Fu at all: no skills, combos, buffs or passives.
            var cloneComp = EnsureComp<ProperKungFuCloneComponent>(clone);
            cloneComp.Summoner = user;
            cloneComp.Ultimate = true;

            // Hostile to everyone (not the user's factions), sparing only the summoner and the Pack Dogs.
            _faction.ClearFactions(clone, false);
            _faction.AddFaction(clone, UltimateShadowFaction);
            SyncShadowExceptions(clone, cloneComp);
            // 60 s of life each; EndUltimate still clears them early if the mode ends first.
            cloneComp.ExpiresAt = now + args.CloneLifetime;
            cloneComp.DamageModifier = args.DamageModifier;
            cloneComp.VanishEffect = args.VanishEffect;
            cloneComp.NextSkill = TimeSpan.MaxValue;

            if (args.Weapons.Count > 0)
                GiveUltimateCloneWeapon(clone, args.Weapons[_instinctRandom.Next(args.Weapons.Count)], cloneComp.ExpiresAt);

            cloneComp.Target = FindUltimateCloneTarget(clone, Transform(clone));
            StartCloneBrain(clone, cloneComp);
            Spawn(args.SpawnEffect, coords);
            leader.Clones.Add(clone);
            spawner.Spawned++;
        }
    }

    private void GiveUltimateCloneWeapon(EntityUid clone, Robust.Shared.Prototypes.EntProtoId? proto, TimeSpan expiresAt)
    {
        if (proto is not { } weaponProto)
            return;

        var weapon = Spawn(weaponProto, _transform.GetMapCoordinates(clone));

        // A null rod would punish its untrained holder; a kunai needs its summoner or it fades.
        RemComp<NullrodComponent>(weapon);
        if (TryComp<ProperKungFuKunaiComponent>(weapon, out var kunai))
        {
            kunai.Summoner = clone;
            kunai.ExpiresAt = expiresAt;
        }

        EnsureComp<UnremoveableComponent>(weapon).DeleteOnDrop = true;
        if (!_hands.TryPickupAnyHand(clone, weapon, checkActionBlocker: false))
            QueueDel(weapon);
    }

    /// <summary>
    /// The nearest living mob within 10 tiles that the shadow may attack, conscious ones first.
    /// </summary>
    private EntityUid? FindUltimateCloneTarget(EntityUid clone, TransformComponent xform)
    {
        var pos = _transform.GetWorldPosition(xform);
        EntityUid? best = null;
        var bestScore = float.MaxValue;
        foreach (var mob in _lookup.GetEntitiesInRange<MobStateComponent>(_transform.GetMapCoordinates(clone, xform), UltimateCloneSearchRange))
        {
            var uid = mob.Owner;
            if (uid == clone || _mobState.IsDead(uid, mob.Comp) || IsCloneFriend(clone, uid))
                continue;

            var score = (_transform.GetWorldPosition(uid) - pos).LengthSquared();
            if (_mobState.IsCritical(uid, mob.Comp))
                score += 10000f;

            if (score >= bestScore)
                continue;

            bestScore = score;
            best = uid;
        }

        return best;
    }

    /// <summary>
    /// Living, conscious, uncuffed Pack Dogs on the user's map that are not inside anything, nearest first.
    /// </summary>
    public List<EntityUid> GetSummonablePupils(EntityUid user, int max)
    {
        var userXform = Transform(user);
        var origin = _transform.GetWorldPosition(userXform);
        var found = new List<(EntityUid Uid, float Distance)>();
        var query = EntityQueryEnumerator<ProperKungFuComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var kungFu, out var xform))
        {
            if (uid == user
                || !kungFu.Pupil
                || xform.MapID != userXform.MapID
                || !_mobState.IsAlive(uid)
                || _container.IsEntityInContainer(uid)
                || TryComp<CuffableComponent>(uid, out var cuffs) && cuffs.CuffedHandCount > 0)
                continue;

            found.Add((uid, (_transform.GetWorldPosition(xform) - origin).LengthSquared()));
        }

        return found.OrderBy(p => p.Distance).Take(Math.Max(0, max)).Select(p => p.Uid).ToList();
    }

    private void OnSummonPack(Entity<ProperKungFuComponent> ent, ref ProperKungFuSummonPackActionEvent args)
    {
        if (args.Handled || !ent.Comp.UltimateActive)
            return;

        var user = ent.Owner;
        var pupils = GetSummonablePupils(user, args.MaxPupils);
        if (pupils.Count == 0)
        {
            // No cooldown wasted when nobody can come.
            _popup.PopupEntity(Loc.GetString("proper-kung-fu-summon-pack-none"), user, user);
            return;
        }

        var xform = Transform(user);
        var map = xform.MapID;
        var origin = _transform.GetWorldPosition(xform);
        var facing = _transform.GetWorldRotation(xform);
        for (var i = 0; i < pupils.Count; i++)
        {
            var pupil = pupils[i];
            Spawn(args.SmokeEffect, _transform.GetMapCoordinates(pupil));

            if (TryComp<PullableComponent>(pupil, out var pullable) && pullable.BeingPulled)
                _pulling.TryStopPull(pupil, pullable, ignoreGrab: true);
            if (TryComp<PullerComponent>(pupil, out var puller) && puller.Pulling is { } pulled
                && TryComp<PullableComponent>(pulled, out var pulledComp))
                _pulling.TryStopPull(pulled, pulledComp, ignoreGrab: true);

            // One on each side of the user (more go around).
            var side = facing + Angle.FromDegrees(i % 2 == 0 ? 90 : -90) + Angle.FromDegrees(30 * (i / 2));
            var spot = origin + side.ToWorldVec() * args.Radius;
            if (IsPathBlocked(user, map, origin, spot, out var free))
                spot = free;

            RemComp<ProperKungFuSlidingComponent>(pupil);
            _physics.SetLinearVelocity(pupil, Vector2.Zero);
            _transform.SetMapCoordinates(pupil, new MapCoordinates(spot, map));
            _transform.SetWorldRotation(pupil, facing);
            Spawn(args.SmokeEffect, new MapCoordinates(spot, map));
            Spawn(args.ArriveEffect, new MapCoordinates(spot, map));

            // Only the summoned dog sees the alpha's call.
            RaiseNetworkEvent(new ProperKungFuPackSummonedEvent((float) PackCallDuration.TotalSeconds), Filter.Entities(pupil));
        }

        _audio.PlayPvs(args.Sound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-summon-pack-user", ("count", pupils.Count)), user, user, PopupType.Medium);
        args.Handled = true;
    }
}

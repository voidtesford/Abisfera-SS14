using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Goobstation.Shared.MartialArts;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.Systems;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Access.Components;
using Content.Shared.Actions.Components;
using Content.Shared.Armor;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Explosion.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Interaction.Components;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Systems;
using Content.Shared.PDA;
using Content.Shared.Popups;
using Content.Shared.Weapons.Melee.Events;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Reflect;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Wolf Pack": short-lived, cosmetically dressed copies of the user that fight with Proper Kung Fu.
/// The brain is a tiny HTN (walk to the target and punch it) plus this system picking the target every
/// quarter second and firing Wolf Dance, Claw Whirlwind, Hammer and Wolf Instinct when they are ready.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly MetaDataSystem _metaData = default!;
    [Dependency] private readonly MobThresholdSystem _thresholds = default!;
    [Dependency] private readonly NpcFactionSystem _faction = default!;
    [Dependency] private readonly NPCSystem _npc = default!;
    [Dependency] private readonly SharedMartialArtsSystem _martialArts = default!;

    public const string WolfPackAction = "ActionProperKungFuWolfPack";
    public const string KunaiAction = "ActionProperKungFuKunai";
    public const string JoinPackAction = "ActionProperKungFuJoinPack";

    private const string CloneHtnCompound = "ProperKungFuCloneCompound";
    private const string FallbackCloneSpecies = "MobHuman";
    private static readonly TimeSpan CloneThinkInterval = TimeSpan.FromSeconds(0.25);
    /// <summary>
    /// Internal delay before every clone skill (on top of the skill's own cooldown), also counted from the spawn.
    /// </summary>
    private static readonly TimeSpan CloneSkillGap = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Slots whose items are never copied onto a clone: identity, pockets and suit storage only ever hold loot.
    /// </summary>
    private static readonly HashSet<string> CloneSkippedSlots = new() { "id", "pocket1", "pocket2", "suitstorage" };

    private DamageModifierSet? _cloneHalfDamage;
    private float _cloneHalfDamageFactor = -1f;

    private void InitializeWolfPack()
    {
        SubscribeLocalEvent<ProperKungFuComponent, WolfPackActionEvent>(OnWolfPack);
        SubscribeLocalEvent<ProperKungFuCloneComponent, MobStateChangedEvent>(OnCloneMobState);
        SubscribeLocalEvent<ProperKungFuCloneComponent, AttackAttemptEvent>(OnCloneAttackAttempt);
        SubscribeLocalEvent<ProperKungFuCloneComponent, AttackedEvent>(OnCloneAttacked);
        SubscribeLocalEvent<ProperKungFuCloneComponent, ComponentShutdown>(OnCloneShutdown);
        SubscribeLocalEvent<ProperKungFuCloneComponent, MeleeHitEvent>(OnCloneMeleeHit);
        SubscribeLocalEvent<ProperKungFuPackLeaderComponent, AttackedEvent>(OnLeaderAttacked);
        SubscribeLocalEvent<ProperKungFuPackLeaderComponent, DamageChangedEvent>(OnLeaderDamaged);
    }

    private void OnWolfPack(Entity<ProperKungFuComponent> ent, ref WolfPackActionEvent args)
    {
        if (args.Handled || HasComp<ProperKungFuCloneComponent>(ent))
            return;

        var user = args.Performer;
        var target = args.Target;
        if (target == user
            || IsPackMember(user, target)
            || !HasComp<MobStateComponent>(target)
            || _mobState.IsDead(target))
            return;

        var xform = Transform(user);
        var map = xform.MapID;
        var origin = _transform.GetWorldPosition(xform);
        var count = _instinctRandom.Next(Math.Max(1, args.MinClones), Math.Max(args.MinClones, args.MaxClones) + 1);
        var startAngle = _instinctRandom.NextFloat(0f, MathF.Tau);

        var leader = EnsureComp<ProperKungFuPackLeaderComponent>(user);
        for (var i = 0; i < count; i++)
        {
            var dir = new Angle(startAngle + MathF.Tau * i / count).ToVec();
            var spot = origin + dir * args.SpawnRadius;
            if (IsPathBlocked(user, map, origin, spot, out var free))
                spot = free;

            var clone = SpawnClone(user, new MapCoordinates(spot, map), args, target);
            leader.Clones.Add(clone);
        }

        _audio.PlayPvs(args.Sound, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-pack-user"), user, user);
        _popup.PopupEntity(Loc.GetString("proper-kung-fu-wolf-pack-others", ("user", user)), user,
            Robust.Shared.Player.Filter.PvsExcept(user, entityManager: EntityManager), true, PopupType.MediumCaution);
        args.Handled = true;
    }

    private EntityUid SpawnClone(EntityUid user, MapCoordinates coords, WolfPackActionEvent args, EntityUid target)
    {
        var clone = SpawnCloneBody(user, coords, args.CloneHealth);

        // Proper Kung Fu without the summoning skills: a clone never calls more clones or kunai,
        // and has none of the master's buffs or passives.
        var kungFu = Factory.GetComponent<ProperKungFuComponent>();
        kungFu.Clone = true;
        kungFu.BaseActions.RemoveAll(a => a == WolfPackAction || a == KunaiAction || a == JoinPackAction || a == UltimateAction);
        AddComp(clone, kungFu);
        _martialArts.GrantProperKungFu(clone);

        var cloneComp = EnsureComp<ProperKungFuCloneComponent>(clone);
        cloneComp.Summoner = user;
        cloneComp.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(args.Lifetime);
        cloneComp.DamageModifier = args.DamageModifier;
        cloneComp.VanishEffect = args.VanishEffect;
        cloneComp.Target = target;
        cloneComp.NextSkill = _timing.CurTime + CloneSkillGap;

        StartCloneBrain(clone, cloneComp);
        Spawn(args.SpawnEffect, coords);
        return clone;
    }

    private void StartCloneBrain(EntityUid clone, ProperKungFuCloneComponent cloneComp)
    {
        var htn = EnsureComp<HTNComponent>(clone);
        htn.RootTask = new HTNCompoundTask { Task = CloneHtnCompound };
        htn.Blackboard.SetValue(NPCBlackboard.Owner, clone);
        UpdateCloneBlackboard(clone, cloneComp);
        _npc.WakeNPC(clone, htn);
    }

    /// <summary>
    /// A body that looks like the user: same species and appearance, name, cosmetic copies of the clothes,
    /// the user's factions, and the given total health.
    /// </summary>
    private EntityUid SpawnCloneBody(EntityUid user, MapCoordinates coords, float health)
    {
        EntProtoId species = FallbackCloneSpecies;
        if (TryComp<HumanoidAppearanceComponent>(user, out var humanoid)
            && _proto.TryIndex<SpeciesPrototype>(humanoid.Species, out var speciesProto))
            species = speciesProto.Prototype;

        var clone = Spawn(species, coords);
        if (humanoid != null)
            _humanoid.CloneAppearance(user, clone, humanoid);

        _metaData.SetEntityName(clone, Name(user));
        _transform.SetWorldRotation(clone, _transform.GetWorldRotation(user));

        CopyCosmeticClothing(user, clone);
        ScaleCloneHealth(clone, health);

        if (TryComp<NpcFactionMemberComponent>(user, out var factions))
        {
            _faction.ClearFactions(clone, false);
            _faction.AddFactions(clone, new HashSet<ProtoId<Content.Shared.NPC.Prototypes.NpcFactionPrototype>>(factions.Factions));
        }

        return clone;
    }

    /// <summary>
    /// Dresses the clone in fresh copies of what the user wears, stripped of every function: no armor, reflection,
    /// access or storage, nothing that can be taken off, and all of it goes away with the clone.
    /// </summary>
    private void CopyCosmeticClothing(EntityUid user, EntityUid clone)
    {
        var worn = new List<(string Slot, string Proto)>();
        var slots = _inventory.GetSlotEnumerator(user);
        while (slots.MoveNext(out var slot))
        {
            if (slot.ContainedEntity is not { } item
                || CloneSkippedSlots.Contains(slot.ID)
                || HasComp<GunComponent>(item)
                || HasComp<IdCardComponent>(item)
                || HasComp<PdaComponent>(item)
                || MetaData(item).EntityPrototype is not { } proto)
                continue;

            worn.Add((slot.ID, proto.ID));
        }

        // Whatever the species prototype spawned with (nothing for a plain mob) is replaced.
        foreach (var (slot, proto) in worn)
        {
            if (_inventory.TryGetSlotEntity(clone, slot, out var existing))
                QueueDel(existing);

            if (!_inventory.SpawnItemInSlot(clone, slot, proto, silent: true, force: true)
                || !_inventory.TryGetSlotEntity(clone, slot, out var copy))
                continue;

            MakeCosmetic(copy.Value);
        }
    }

    private void MakeCosmetic(EntityUid item)
    {
        if (TryComp<ContainerManagerComponent>(item, out var containers))
        {
            foreach (var container in _container.GetAllContainers(item, containers))
            {
                foreach (var inside in container.ContainedEntities.ToArray())
                {
                    QueueDel(inside);
                }
            }
        }

        RemComp<ArmorComponent>(item);
        RemComp<ExplosionResistanceComponent>(item);
        RemComp<ReflectComponent>(item);
        RemComp<AccessComponent>(item);
        EnsureComp<UnremoveableComponent>(item).DeleteOnDrop = true;
    }

    private void ScaleCloneHealth(EntityUid clone, float health)
    {
        if (!TryComp<MobThresholdsComponent>(clone, out var thresholds) || health <= 0f)
            return;

        if (!_thresholds.TryGetThresholdForState(clone, MobState.Dead, out var dead, thresholds) || dead <= FixedPoint2.Zero)
            return;

        var scale = health / dead.Value.Float();
        foreach (var (value, state) in new SortedDictionary<FixedPoint2, MobState>(thresholds.Thresholds))
        {
            _thresholds.SetMobStateThreshold(clone, value * scale, state, thresholds);
        }
    }

    private bool IsPackMember(EntityUid summoner, EntityUid uid)
    {
        return TryComp<ProperKungFuCloneComponent>(uid, out var clone) && clone.Summoner == summoner;
    }

    /// <summary>
    /// True when a clone (or anything else) must never hurt <paramref name="target"/>: its own summoner or a clone of the same pack.
    /// </summary>
    private bool IsCloneFriend(EntityUid attacker, EntityUid target)
    {
        if (!TryComp<ProperKungFuCloneComponent>(attacker, out var clone))
            return false;

        if (target == clone.Summoner || target == attacker || IsPackMember(clone.Summoner, target))
            return true;

        // The ultimate's clones spare the Pack Dogs.
        return clone.Ultimate && TryComp<ProperKungFuComponent>(target, out var pupil) && pupil.Pupil;
    }

    private void OnCloneAttackAttempt(EntityUid uid, ProperKungFuCloneComponent comp, AttackAttemptEvent args)
    {
        if (args.Target is { } target && IsCloneFriend(uid, target))
            args.Cancel();
    }

    /// <summary>
    /// Wolf Instinct when the clone is attacked, if it is ready.
    /// </summary>
    private void OnCloneAttacked(EntityUid uid, ProperKungFuCloneComponent comp, AttackedEvent args)
    {
        if (comp.Vanished || comp.Ultimate || IsCloneFriend(uid, args.User) || _timing.CurTime < comp.NextSkill)
            return;

        if (TryCloneSkill(uid, "ActionProperKungFuWolfInstinct", null))
            comp.NextSkill = _timing.CurTime + CloneSkillGap;
    }

    private void OnLeaderAttacked(EntityUid uid, ProperKungFuPackLeaderComponent comp, AttackedEvent args)
    {
        SetPackTarget(uid, comp, args.User);
    }

    private void OnLeaderDamaged(EntityUid uid, ProperKungFuPackLeaderComponent comp, DamageChangedEvent args)
    {
        if (args.DamageIncreased && args.Origin is { } origin)
            SetPackTarget(uid, comp, origin);
    }

    /// <summary>
    /// Called when the summoner hits someone (fists or a skill): the pack switches to that victim.
    /// </summary>
    private void OnLeaderHit(EntityUid leader, EntityUid victim)
    {
        if (TryComp<ProperKungFuPackLeaderComponent>(leader, out var comp))
            SetPackTarget(leader, comp, victim);
    }

    public void SetPackTarget(EntityUid leader, ProperKungFuPackLeaderComponent comp, EntityUid target)
    {
        if (target == leader
            || IsPackMember(leader, target)
            || !HasComp<MobStateComponent>(target)
            || _mobState.IsDead(target))
            return;

        foreach (var clone in comp.Clones)
        {
            if (!TryComp<ProperKungFuCloneComponent>(clone, out var cloneComp) || cloneComp.Vanished)
                continue;

            cloneComp.Target = target;
            UpdateCloneBlackboard(clone, cloneComp);
        }
    }

    private void UpdateCloneBlackboard(EntityUid clone, ProperKungFuCloneComponent comp)
    {
        if (!TryComp<HTNComponent>(clone, out var htn))
            return;

        if (comp.Target is { } target && !TerminatingOrDeleted(target))
        {
            htn.Blackboard.SetValue("Target", target);
            htn.Blackboard.SetValue("TargetCoordinates", Transform(target).Coordinates);
        }
        else
        {
            htn.Blackboard.Remove<EntityUid>("Target");
            htn.Blackboard.Remove<EntityCoordinates>("TargetCoordinates");
        }
    }

    private void OnCloneMobState(EntityUid uid, ProperKungFuCloneComponent comp, MobStateChangedEvent args)
    {
        if (args.NewMobState == MobState.Dead)
            VanishClone(uid, comp);
    }

    private void OnCloneShutdown(EntityUid uid, ProperKungFuCloneComponent comp, ComponentShutdown args)
    {
        if (!TryComp<ProperKungFuPackLeaderComponent>(comp.Summoner, out var leader))
            return;

        leader.Clones.Remove(uid);
        if (leader.Clones.Count == 0)
            RemCompDeferred(comp.Summoner, leader);
    }

    public void VanishClone(EntityUid uid, ProperKungFuCloneComponent comp)
    {
        if (comp.Vanished)
            return;

        comp.Vanished = true;
        if (TryComp<HTNComponent>(uid, out var htn))
            _npc.SleepNPC(uid, htn);

        Spawn(comp.VanishEffect, _transform.GetMapCoordinates(uid));
        QueueDel(uid);
    }

    /// <summary>
    /// Clones deal only a part of every damage: fists through a melee modifier set, skills through DealKungFuDamage.
    /// </summary>
    private DamageModifierSet GetCloneDamageSet(float factor)
    {
        if (_cloneHalfDamage != null && MathHelper.CloseTo(_cloneHalfDamageFactor, factor))
            return _cloneHalfDamage;

        var set = new DamageModifierSet();
        foreach (var type in _proto.EnumeratePrototypes<DamageTypePrototype>())
        {
            set.Coefficients[type.ID] = factor;
        }

        _cloneHalfDamage = set;
        _cloneHalfDamageFactor = factor;
        return set;
    }

    private void OnCloneMeleeHit(EntityUid uid, ProperKungFuCloneComponent clone, MeleeHitEvent args)
    {
        if (args.User != uid || !args.IsHit)
            return;

        args.ModifiersList.Add(GetCloneDamageSet(clone.DamageModifier));
    }

    private void UpdateWolfPack(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuCloneComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var clone, out var xform))
        {
            if (clone.Vanished)
                continue;

            if (now >= clone.ExpiresAt || TerminatingOrDeleted(clone.Summoner) || _mobState.IsDead(uid))
            {
                VanishClone(uid, clone);
                continue;
            }

            if (now < clone.NextThink)
                continue;

            clone.NextThink = now + CloneThinkInterval;

            if (clone.Target is { } current
                && (TerminatingOrDeleted(current)
                    || _mobState.IsDead(current)
                    || Transform(current).MapID != xform.MapID
                    || IsCloneFriend(uid, current)))
                clone.Target = null;

            if (clone.Ultimate)
            {
                clone.Target ??= FindUltimateCloneTarget(uid, xform);
                UpdateCloneBlackboard(uid, clone);
                continue;
            }

            UpdateCloneBlackboard(uid, clone);

            if (clone.Target is not { } target || now < clone.NextSkill || _mobState.IsIncapacitated(uid))
                continue;

            var distance = (_transform.GetWorldPosition(target) - _transform.GetWorldPosition(xform)).Length();
            var used = distance switch
            {
                <= 1.5f => TryCloneSkill(uid, "ActionProperKungFuHammer", target)
                           || TryCloneSkill(uid, "ActionProperKungFuClawWhirlwind", target),
                > 2f and <= 5f => TryCloneSkill(uid, "ActionProperKungFuWolfDance", target),
                _ => false,
            };

            if (used)
                clone.NextSkill = now + CloneSkillGap;
        }
    }

    /// <summary>
    /// Fires one of the clone's own Proper Kung Fu actions if it is off cooldown. Returns true when it went off.
    /// </summary>
    private bool TryCloneSkill(EntityUid clone, string actionProto, EntityUid? target)
    {
        if (!TryComp<ProperKungFuComponent>(clone, out var kungFu))
            return false;

        var now = _timing.CurTime;
        foreach (var action in kungFu.ActionEntities)
        {
            if (MetaData(action).EntityPrototype?.ID != actionProto || !TryComp<ActionComponent>(action, out var comp))
                continue;

            if (_actions.IsCooldownActive(comp, now))
                return false;

            if (target is { } t)
                _actions.SetEventTarget(action, t);

            _actions.PerformAction(clone, (action, comp), predicted: false);
            return _actions.IsCooldownActive(comp, _timing.CurTime);
        }

        return false;
    }
}

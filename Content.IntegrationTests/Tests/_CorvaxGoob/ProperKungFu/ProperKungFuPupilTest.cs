using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Projectiles;
using Content.Shared.StatusEffect;
using Content.Shared.Traits;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// v11: Join the Pack, the pupil form, the master's passives, the info window and the "Pack Hound" trait.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuComponent))]
public sealed class ProperKungFuPupilTest
{
    /// <summary>
    /// Join the Pack teaches the pupil form after the do-after. The pupil has the three trimmed combos only:
    /// same damage, halved stun and slide, no mark, no finisher, torso-only Perfect Flow, and none of the
    /// master's passives (attack rate, stamina, gunshot reduction, bleed boost) and no gun ban.
    /// </summary>
    [Test]
    public async Task ProperKungFuPupilFormTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var statusOld = entMan.System<StatusEffectsSystem>();
        var melee = entMan.System<SharedMeleeWeaponSystem>();

        EntityUid master = default;
        EntityUid pupil = default;
        EntityUid plain = default;
        EntityUid seriesTarget = default;
        EntityUid flowTarget = default;
        EntityUid masterTarget = default;
        Vector2 seriesStart = default;
        var seriesBefore = 0f;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 12; x++)
            for (var y = -3; y <= 12; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            master = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            pupil = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            plain = SpawnMob(entMan, new EntityCoordinates(map.Grid, 10.5f, 10.5f));
            seriesTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 4.5f));
            flowTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 7.5f));
            masterTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 10.5f));

            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(master, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(master));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(master);
            var join = kungFu.ActionEntities.First(a => actions.GetEvent(a) is JoinPackActionEvent);
            Entity<ActionComponent> action = (join, entMan.GetComponent<ActionComponent>(join));
            Assert.That(actions.SetEventTarget(action, pupil));
            actions.PerformAction(master, action, predicted: false);
        });

        await server.WaitRunTicks(timing.TickRate * 5);
        await server.WaitAssertion(() =>
            Assert.That(entMan.HasComponent<ProperKungFuComponent>(pupil), Is.False, "Still learning after 5 s"));

        await server.WaitRunTicks(timing.TickRate * 6);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<ProperKungFuComponent>(pupil, out var kungFu), "Taught after 10 s");
            Assert.That(kungFu!.Pupil);
            Assert.That(kungFu.ActionEntities, Is.Empty, "A pupil has no skills");
            Assert.That(entMan.GetComponent<MartialArtsKnowledgeComponent>(pupil).MartialArtsForm,
                Is.EqualTo(MartialArtsForms.ProperKungFuPupil));

            // No passives: attack rate and stamina are those of a plain human.
            Assert.That(melee.GetAttackRate(pupil, pupil), Is.EqualTo(melee.GetAttackRate(plain, plain)).Within(0.001f), "No attack rate bonus");
            Assert.That(melee.GetAttackRate(master, master), Is.GreaterThan(melee.GetAttackRate(plain, plain)), "The master hits faster");
            Assert.That(entMan.GetComponent<StaminaComponent>(pupil).CritThreshold,
                Is.EqualTo(entMan.GetComponent<StaminaComponent>(plain).CritThreshold).Within(0.01f), "No stamina bonus");

            // Guns: the master can't shoot, the pupil can.
            var gun = entMan.SpawnEntity("WeaponPistolMk58", entMan.GetComponent<TransformComponent>(pupil).Coordinates);
            var gunComp = entMan.GetComponent<GunComponent>(gun);
            var masterShot = new ShotAttemptedEvent { User = master, Used = (gun, gunComp) };
            entMan.EventBus.RaiseLocalEvent(master, ref masterShot);
            Assert.That(masterShot.Cancelled, "The master can't use guns");
            var pupilShot = new ShotAttemptedEvent { User = pupil, Used = (gun, gunComp) };
            entMan.EventBus.RaiseLocalEvent(pupil, ref pupilShot);
            Assert.That(pupilShot.Cancelled, Is.False, "A pupil can use guns");

            // Gunshot reduction: the master takes 60% of what the pupil takes.
            var taken = new Dictionary<EntityUid, float>();
            foreach (var who in new[] { pupil, master })
            {
                var reflect = new ProjectileReflectAttemptEvent(gun, new ProjectileComponent(), false);
                entMan.EventBus.RaiseLocalEvent(who, ref reflect);
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Piercing", 10);
                var dealt = damageSys.TryChangeDamage(who, dmg, targetPart: TargetBodyPart.Chest, canMiss: false);
                taken[who] = dealt?.GetTotal().Float() ?? 0f;
            }

            TestContext.WriteLine($"Gunshot: pupil {taken[pupil]}, master {taken[master]}");
            Assert.That(taken[pupil], Is.GreaterThan(0f));
            Assert.That(taken[master] / taken[pupil], Is.EqualTo(0.6f).Within(0.02f), "Only the master's gunshot damage is reduced");

            // 80 pre-damage: a master would do the Claw Series finisher, a pupil never does.
            var parts = new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.Groin };
            for (var p = 0; p < parts.Length; p++)
            {
                var pre = new DamageSpecifier();
                pre.DamageDict.Add("Blunt", p == 2 ? 28 : 26);
                damageSys.TryChangeDamage(seriesTarget, pre, targetPart: parts[p], canMiss: false);
            }

            xformSys.SetWorldPosition(pupil, new Vector2(0.5f, 4.5f));
            seriesStart = xformSys.GetWorldPosition(seriesTarget);
            seriesBefore = Blunt(entMan.GetComponent<DamageableComponent>(seriesTarget));
            Assert.That(entMan.GetComponent<DamageableComponent>(seriesTarget).TotalDamage.Float(), Is.GreaterThanOrEqualTo(80f));
            combat.SetInCombatMode(pupil, true);
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(pupil, new ComboAttackPerformedEvent(pupil, seriesTarget, pupil, ComboAttackType.Harm));
            }

            Assert.That(entMan.HasComponent<ProperKungFuBleedBoostComponent>(seriesTarget), Is.False, "No bleed boost from a pupil");
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(seriesTarget)) - seriesBefore, Is.EqualTo(10f).Within(0.5f),
                "Pupil Claw Series: the same 10 Blunt");
            Assert.That(entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(pupil), Is.False, "No finisher for a pupil");
            Assert.That(statusOld.TryGetTime(seriesTarget, "Stun", out var stun), "Stunned");
            var left = (stun!.Value.Item2 - timing.CurTime).TotalSeconds;
            Assert.That(left, Is.InRange(0.7, 1.05), $"Halved stun (1 s), left {left}");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(seriesTarget), Is.False, "No synergy mark");
            Assert.That(entMan.HasComponent<ProperKungFuBleedBoostComponent>(seriesTarget), Is.False, "No bleed boost from a pupil");
        });

        await server.WaitRunTicks(timing.TickRate);

        await server.WaitPost(() =>
        {
            var moved = xformSys.GetWorldPosition(seriesTarget).X - seriesStart.X;
            Assert.That(moved, Is.EqualTo(1.5f).Within(0.3f), $"Halved slide, moved {moved}");

            // Pupil Perfect Flow: 10 Blunt on the torso only, nothing on the arms, no disorientation.
            xformSys.SetWorldPosition(pupil, new Vector2(0.5f, 7.5f));
            foreach (var type in new[] { ComboAttackType.Disarm, ComboAttackType.Harm, ComboAttackType.Disarm, ComboAttackType.Harm })
            {
                entMan.EventBus.RaiseLocalEvent(pupil, new ComboAttackPerformedEvent(pupil, flowTarget, pupil, type));
            }

            // The master's Claw Series marks for the bleed boost.
            xformSys.SetWorldPosition(master, new Vector2(0.5f, 10.5f));
            combat.SetInCombatMode(master, true);
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(master, new ComboAttackPerformedEvent(master, masterTarget, master, ComboAttackType.Harm));
            }

            // The boost only lives for the tick of the hit.
            Assert.That(entMan.HasComponent<ProperKungFuBleedBoostComponent>(masterTarget), "The master's hits boost bleeding");
        });

        await server.WaitRunTicks(timing.TickRate);

        await server.WaitAssertion(() =>
        {
            var damage = entMan.GetComponent<DamageableComponent>(flowTarget);
            Assert.That(Blunt(damage), Is.EqualTo(10f).Within(0.5f), "Pupil Perfect Flow: 10 Blunt in total");
            Assert.That(PartDamage(entMan, flowTarget, BodyPartType.Chest, "Blunt"), Is.EqualTo(10f).Within(0.5f), "On the torso");
            Assert.That(PartDamage(entMan, flowTarget, BodyPartType.Arm, "Blunt"), Is.LessThanOrEqualTo(0f), "Nothing on the arms");
            Assert.That(entMan.HasComponent<ProperKungFuDisorientedComponent>(flowTarget), Is.False, "No disorientation");

        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A master's finishers never fire on a dead target.
    /// </summary>
    [Test]
    public async Task ProperKungFuNoFinisherOnDeadTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var mobState = entMan.System<Content.Shared.Mobs.Systems.MobStateSystem>();

        EntityUid master = default;
        EntityUid target = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 8; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            master = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(master, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(master));

            mobState.ChangeMobState(target, Content.Shared.Mobs.MobState.Dead);
            // 80+ damage too, so only the death keeps the finisher away.
            foreach (var part in new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.Groin })
            {
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Blunt", 30);
                damageSys.TryChangeDamage(target, dmg, targetPart: part, canMiss: false);
            }
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            Assert.That(mobState.IsDead(target), "The target is dead");
            combat.SetInCombatMode(master, true);
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(master, new ComboAttackPerformedEvent(master, target, master, ComboAttackType.Harm));
            }
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
            Assert.That(entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(master), Is.False, "No finisher on the dead"));

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The info window lists everything for the master and only the trimmed combos for a pupil; the thresholds
    /// shown match the real ones and every name and description is localized.
    /// </summary>
    [Test]
    public async Task ProperKungFuInfoTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var loc = server.ResolveDependency<ILocalizationManager>();
        var actions = entMan.System<ActionsSystem>();
        var hands = entMan.System<SharedHandsSystem>();

        var owner = ProperKungFuInfo.GetEntries(false);
        var pupil = ProperKungFuInfo.GetEntries(true);

        Assert.Multiple(() =>
        {
            Assert.That(owner.Count(e => e.Kind == ProperKungFuInfo.Kind.Combo), Is.EqualTo(3));
            Assert.That(owner.Count(e => e.Kind == ProperKungFuInfo.Kind.Skill), Is.EqualTo(7));
            Assert.That(owner.Count(e => e.Kind == ProperKungFuInfo.Kind.Synergy), Is.EqualTo(5));
            Assert.That(owner.Count(e => e.Kind == ProperKungFuInfo.Kind.Finisher), Is.EqualTo(7));
            Assert.That(owner.Where(e => e.Kind == ProperKungFuInfo.Kind.Finisher).All(e => e.Threshold is > 0f));

            Assert.That(pupil.Count(e => e.Kind == ProperKungFuInfo.Kind.Combo), Is.EqualTo(3));
            Assert.That(pupil.Any(e => e.Kind is ProperKungFuInfo.Kind.Skill or ProperKungFuInfo.Kind.Synergy or ProperKungFuInfo.Kind.Finisher),
                Is.False, "A pupil sees no skills, synergies or finishers");
            Assert.That(pupil.Count(e => e.Kind == ProperKungFuInfo.Kind.Passive), Is.EqualTo(1), "Only the limits note");

            foreach (var entry in owner.Concat(pupil))
            {
                Assert.That(loc.HasString(entry.Name), $"Missing {entry.Name}");
                Assert.That(loc.HasString(entry.Description), $"Missing {entry.Description}");
            }

            foreach (var kind in new[] { ProperKungFuInfo.Kind.Combo, ProperKungFuInfo.Kind.Skill, ProperKungFuInfo.Kind.Passive,
                         ProperKungFuInfo.Kind.Synergy, ProperKungFuInfo.Kind.Finisher })
            {
                Assert.That(loc.HasString(ProperKungFuInfo.SectionName(kind)), $"Missing section {kind}");
            }

            foreach (var type in new[] { ComboAttackType.Harm, ComboAttackType.Disarm, ComboAttackType.Grab })
            {
                Assert.That(loc.HasString($"proper-kung-fu-info-intent-{type.ToString().ToLowerInvariant()}"), $"Missing intent {type}");
            }

            // Combo inputs match the combo prototypes.
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuClawSeries").AttackTypes, Is.EqualTo(ProperKungFuInfo.ClawSeriesInputs));
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuWolfGrip").AttackTypes, Is.EqualTo(ProperKungFuInfo.WolfGripInputs));
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuPerfectFlow").AttackTypes, Is.EqualTo(ProperKungFuInfo.PerfectFlowInputs));
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuPupilClawSeries").AttackTypes, Is.EqualTo(ProperKungFuInfo.ClawSeriesInputs));
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuPupilWolfGrip").AttackTypes, Is.EqualTo(ProperKungFuInfo.WolfGripInputs));
            Assert.That(protoMan.Index<ComboPrototype>("ProperKungFuPupilPerfectFlow").AttackTypes, Is.EqualTo(ProperKungFuInfo.PerfectFlowInputs));

            // Combo thresholds match the prototypes.
            Assert.That(((ProperKungFuWolfGripPerformedEvent) protoMan.Index<ComboPrototype>("ProperKungFuWolfGrip").ResultEvent!).FinisherThreshold,
                Is.EqualTo(ProperKungFuInfo.WolfGripFinisherThreshold));
            Assert.That(((ProperKungFuPerfectFlowPerformedEvent) protoMan.Index<ComboPrototype>("ProperKungFuPerfectFlow").ResultEvent!).FinisherThreshold,
                Is.EqualTo(ProperKungFuInfo.PerfectFlowFinisherThreshold));
        });

        // Skill thresholds and the Claw Series one match the real actions and the component.
        EntityUid master = default;
        await server.WaitPost(() =>
        {
            master = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(master, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(master));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(master);
            Assert.That(kungFu.ClawSeriesFinisherThreshold, Is.EqualTo(ProperKungFuInfo.ClawSeriesFinisherThreshold));
            var seen = new HashSet<string>();
            foreach (var action in kungFu.ActionEntities)
            {
                switch (actions.GetEvent(action))
                {
                    case WolfDanceActionEvent ev:
                        Assert.That(ev.FinisherThreshold, Is.EqualTo(ProperKungFuInfo.WolfDanceFinisherThreshold));
                        seen.Add("dance");
                        break;
                    case ClawWhirlwindActionEvent ev:
                        Assert.That(ev.FinisherThreshold, Is.EqualTo(ProperKungFuInfo.ClawWhirlwindFinisherThreshold));
                        seen.Add("whirlwind");
                        break;
                    case HammerActionEvent ev:
                        Assert.That(ev.FinisherThreshold, Is.EqualTo(ProperKungFuInfo.HammerFinisherThreshold));
                        seen.Add("hammer");
                        break;
                    case WolfInstinctActionEvent ev:
                        Assert.That(ev.FinisherThreshold, Is.EqualTo(ProperKungFuInfo.WolfInstinctFinisherThreshold));
                        seen.Add("instinct");
                        break;
                }
            }

            Assert.That(seen, Has.Count.EqualTo(4), "All four finisher skills are checked");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The "Pack Hound" trait: locked with zero time on its tracker, unlocked with any time on it, and its component
    /// grants the pupil form on spawn and then removes itself.
    /// </summary>
    [Test]
    public async Task ProperKungFuPackHoundTraitTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var loc = server.ResolveDependency<ILocalizationManager>();

        await server.WaitAssertion(() =>
        {
            Assert.That(protoMan.HasIndex<PlayTimeTrackerPrototype>(ProperKungFuPackHound.Tracker));
            var trait = protoMan.Index<TraitPrototype>(ProperKungFuPackHound.Trait);
            Assert.That(trait.Requirements, Is.Not.Null.And.Not.Empty);
            Assert.That(loc.HasString(trait.Name));
            Assert.That(loc.HasString("trait-proper-kung-fu-pack-hound-locked"));
            Assert.That(loc.HasString("trait-locked-suffix"));

            var locked = new Dictionary<string, TimeSpan>();
            Assert.That(ProperKungFuPackHound.TraitRequirementsMet(trait, entMan, protoMan, null, locked, out var reason), Is.False,
                "Locked for a player never taught");
            Assert.That(reason, Is.Not.Null);

            var zero = new Dictionary<string, TimeSpan> { [ProperKungFuPackHound.Tracker] = TimeSpan.Zero };
            Assert.That(ProperKungFuPackHound.TraitRequirementsMet(trait, entMan, protoMan, null, zero, out _), Is.False,
                "Zero time is still locked");

            var unlocked = new Dictionary<string, TimeSpan> { [ProperKungFuPackHound.Tracker] = ProperKungFuPackHound.UnlockTime };
            Assert.That(ProperKungFuPackHound.TraitRequirementsMet(trait, entMan, protoMan, null, unlocked, out _), Is.True,
                "Unlocked once taught");

            var inverted = new PlayTimeTrackerRequirement
            {
                Tracker = ProperKungFuPackHound.Tracker,
                Reason = "trait-proper-kung-fu-pack-hound-locked",
                Inverted = true,
            };
            Assert.That(inverted.Check(entMan, protoMan, null, locked, out _), Is.True, "Inverted requirement");
            Assert.That(inverted.Check(entMan, protoMan, null, unlocked, out _), Is.False, "Inverted requirement");
        });

        EntityUid hound = default;
        await server.WaitPost(() =>
        {
            hound = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.AddComponent<ProperKungFuPackHoundComponent>(hound);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<ProperKungFuComponent>(hound, out var kungFu), "The trait grants the art");
            Assert.That(kungFu!.Pupil, "The pupil form");
            Assert.That(entMan.GetComponent<MartialArtsKnowledgeComponent>(hound).MartialArtsForm, Is.EqualTo(MartialArtsForms.ProperKungFuPupil));
            Assert.That(entMan.HasComponent<ProperKungFuPackHoundComponent>(hound), Is.False, "The trait component removes itself");
            // No player attached: nothing to unlock.
            Assert.That(entMan.System<ProperKungFuSystem>().UnlockPackHound(hound), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid SpawnMob(IEntityManager entMan, EntityCoordinates coords)
    {
        var mob = entMan.SpawnEntity("MobHuman", coords);
        entMan.RemoveComponent<BarotraumaComponent>(mob);
        entMan.RemoveComponent<TemperatureComponent>(mob);
        return mob;
    }

    private static float Blunt(DamageableComponent damage)
    {
        return damage.Damage.DamageDict.TryGetValue("Blunt", out var blunt) ? blunt.Float() : 0f;
    }

    private static float PartDamage(IEntityManager entMan, EntityUid target, BodyPartType type, string damageType)
    {
        var total = 0f;
        var found = false;
        foreach (var part in entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, type))
        {
            found = true;
            if (entMan.GetComponent<DamageableComponent>(part.Id).Damage.DamageDict.TryGetValue(damageType, out var value))
                total += value.Float();
        }

        return found ? total : -1f;
    }
}

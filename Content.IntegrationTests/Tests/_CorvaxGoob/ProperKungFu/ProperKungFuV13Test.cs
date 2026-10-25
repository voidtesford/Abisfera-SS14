using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Actions.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Mobs.Systems;
using Content.Shared.StatusEffectNew;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// Proper Kung Fu v13: Wolf Seal, the Claw Mastery combo set and attack rate, the fan kunai, the Wolf Gaze miss rule,
/// Wolf Judgment and the v13 locale.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuSystem))]
public sealed class ProperKungFuV13Test
{
    /// <summary>
    /// Wolf Seal from 2.5 tiles: a dash, 30 Blunt to the chest (10 + 2 x 10), about 5 tiles of knockback, no breathing
    /// for 20 s (cleared on death), knocked down, stunned and disoriented; usable again in the same mode.
    /// </summary>
    [Test]
    public async Task WolfSealTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = entMan.System<ActionsSystem>();
        var xform = entMan.System<SharedTransformSystem>();
        var status = entMan.System<StatusEffectsSystem>();
        var mobState = entMan.System<MobStateSystem>();

        EntityUid user = default;
        EntityUid victim = default;
        EntityUid second = default;
        var victimStart = Vector2.Zero;

        await server.WaitPost(() =>
        {
            Floor(entMan, tileDefs, map, 12);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnBreathingMob(entMan, new EntityCoordinates(map.Grid, 3.0f, 0.5f));
            second = SpawnBreathingMob(entMan, new EntityCoordinates(map.Grid, 0.5f, -1.5f));
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            StartUltimate(entMan, user);
            var seal = FindAction(entMan, user, "ActionProperKungFuWolfSeal");
            victimStart = xform.GetWorldPosition(victim);
            Assert.That(actions.SetEventTarget(seal, victim));
            actions.PerformAction(user, seal, predicted: false);
            Assert.That(entMan.HasComponent<ProperKungFuSealComponent>(user), "Wolf Seal started");
            Assert.That((xform.GetWorldPosition(user) - victimStart).Length(), Is.LessThan(1.2f), "Dashed in from 2.5 tiles");
            Assert.That(seal.Comp.Cooldown, Is.Not.Null);
            Assert.That((seal.Comp.Cooldown!.Value.End - timing.CurTime).TotalSeconds, Is.EqualTo(40).Within(0.2), "40 s cooldown");
        });

        await server.WaitRunTicks(timing.TickRate * 2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuSealComponent>(user), Is.False, "Wolf Seal over");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Blunt"), Is.EqualTo(30f).Within(0.5f), "Uppercut + two palms to the chest");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Head, "Blunt"), Is.LessThanOrEqualTo(0f), "Nothing to the head");
            var flown = (xform.GetWorldPosition(victim) - victimStart).Length();
            Assert.That(flown, Is.InRange(3.5f, 6.5f), "Thrown about 5 tiles");

            Assert.That(entMan.TryGetComponent<KravMagaBlockedBreathingComponent>(victim, out var blocked), "Can't breathe");
            Assert.That((blocked!.BlockedTime - timing.CurTime).TotalSeconds, Is.InRange(16.5, 20.1), "About 20 s without breath");
            Assert.That(entMan.HasComponent<ProperKungFuDeathPunchBreathComponent>(victim), "Breath block cleared on death");

            Assert.That(entMan.HasComponent<StunnedComponent>(victim), "Stunned");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Knocked down");
            Assert.That(status.HasStatusEffect(victim, "StatusEffectProperKungFuDisoriented"), "Disoriented");
            Assert.That(entMan.HasComponent<Content.Shared._Goobstation.Wizard.TimeStop.FrozenComponent>(user), Is.False);
            Assert.That(entMan.HasComponent<ProperKungFuTimeStoppedComponent>(user), Is.False, "User free again");

            // Not limited per mode: off cooldown, it works again on someone else.
            var seal = FindAction(entMan, user, "ActionProperKungFuWolfSeal");
            actions.ClearCooldown(seal.Owner);
            Assert.That(actions.SetEventTarget(seal, second));
            actions.PerformAction(user, seal, predicted: false);
            Assert.That(entMan.HasComponent<ProperKungFuSealComponent>(user), "Used twice in one mode");

            mobState.ChangeMobState(victim, Content.Shared.Mobs.MobState.Dead);
        });

        await server.WaitRunTicks(timing.TickRate * 2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KravMagaBlockedBreathingComponent>(victim), Is.False, "Death clears the breath block");
            Assert.That(PartDamage(entMan, second, BodyPartType.Chest, "Blunt"), Is.EqualTo(30f).Within(0.5f), "Second seal landed");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The Claw Mastery combo set: two inputs each (the last two of the full sequence, as in the info menu), no
    /// sequence a suffix of another, swapped in at the start of the mode and back at its end; fists 2.5x instead of 1.5x.
    /// </summary>
    [Test]
    public async Task UltimateCombosAndAttackRateTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var melee = entMan.System<SharedMeleeWeaponSystem>();

        EntityUid user = default;
        float baseRate = 0f;
        await server.WaitPost(() =>
        {
            Floor(entMan, tileDefs, map, 4);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
        });
        await server.WaitRunTicks(5);

        var regular = protoMan.Index<ComboListPrototype>("ProperKungFuMoves").Combos.Select(c => c.Id).ToHashSet();
        var ult = protoMan.Index<ComboListPrototype>("ProperKungFuUltimateMoves").Combos.Select(c => c.Id).ToList();

        await server.WaitAssertion(() =>
        {
            var expected = new Dictionary<string, (string Full, ComboAttackType[] Inputs)>
            {
                ["ProperKungFuUltClawSeries"] = ("ProperKungFuClawSeries", ProperKungFuInfo.UltClawSeriesInputs),
                ["ProperKungFuUltWolfGrip"] = ("ProperKungFuWolfGrip", ProperKungFuInfo.UltWolfGripInputs),
                ["ProperKungFuUltPerfectFlow"] = ("ProperKungFuPerfectFlow", ProperKungFuInfo.UltPerfectFlowInputs),
            };
            Assert.That(ult, Is.EquivalentTo(expected.Keys));

            foreach (var (id, (full, inputs)) in expected)
            {
                var combo = protoMan.Index<ComboPrototype>(id);
                var fullCombo = protoMan.Index<ComboPrototype>(full);
                Assert.That(combo.AttackTypes, Is.EqualTo(inputs), $"{id} matches the info menu");
                Assert.That(combo.AttackTypes, Has.Count.EqualTo(2), $"{id}: two inputs");
                Assert.That(combo.AttackTypes, Is.EqualTo(fullCombo.AttackTypes.Skip(fullCombo.AttackTypes.Count - 2)), $"{id} keeps the last inputs");
                Assert.That(combo.ResultEvent?.GetType(), Is.EqualTo(fullCombo.ResultEvent?.GetType()), $"{id} does the same thing");
                Assert.That(combo.MartialArtsForm, Is.EqualTo(MartialArtsForms.ProperKungFu));
            }

            // Combos match as a suffix of the input history: no sequence may end with another one.
            foreach (var a in ult)
            foreach (var b in ult)
            {
                if (a == b)
                    continue;
                var sa = protoMan.Index<ComboPrototype>(a).AttackTypes;
                var sb = protoMan.Index<ComboPrototype>(b).AttackTypes;
                Assert.That(sa.SequenceEqual(sb), Is.False, $"{a} and {b} differ");
            }

            var combos = entMan.GetComponent<CanPerformComboComponent>(user);
            Assert.That(combos.AllowedCombos.Select(c => c.ID).ToHashSet(), Is.EquivalentTo(regular), "Full combos outside the mode");
            baseRate = melee.GetAttackRate(user, user);

            StartUltimate(entMan, user);
            Assert.That(combos.AllowedCombos.Select(c => c.ID), Is.EquivalentTo(ult), "Short combos in the mode");
            Assert.That(melee.GetAttackRate(user, user) / baseRate, Is.EqualTo(2.5f / 1.5f).Within(0.01f), "2.5x replaces 1.5x");

            entMan.GetComponent<ProperKungFuComponent>(user).UltimateEndTime = timing.CurTime;
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.UltimateActive, Is.False, "Mode over");
            var combos = entMan.GetComponent<CanPerformComboComponent>(user);
            Assert.That(combos.AllowedCombos.Select(c => c.ID).ToHashSet(), Is.EquivalentTo(regular), "Full combos back");
            Assert.That(melee.GetAttackRate(user, user), Is.EqualTo(baseRate).Within(0.001f), "1.5x again");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The fan kunai: thrown, it becomes five kunai at -30, -15, 0, +15 and +30 degrees; the user is not teleported,
    /// no finisher starts, and every kunai is gone after landing (no pickups).
    /// </summary>
    [Test]
    public async Task FanKunaiTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = entMan.System<ActionsSystem>();
        var xform = entMan.System<SharedTransformSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var serverHands = entMan.System<Content.Server.Hands.Systems.HandsSystem>();

        EntityUid user = default;
        var userStart = Vector2.Zero;
        await server.WaitPost(() =>
        {
            Floor(entMan, tileDefs, map, 14);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            foreach (var held in hands.EnumerateHeld(user).ToList())
            {
                entMan.DeleteEntity(held);
            }

            StartUltimate(entMan, user);
            var fan = FindAction(entMan, user, "ActionProperKungFuFanKunai");
            actions.PerformAction(user, fan, predicted: false);
            Assert.That(fan.Comp.Cooldown, Is.Not.Null);
            Assert.That((fan.Comp.Cooldown!.Value.End - timing.CurTime).TotalSeconds, Is.EqualTo(10).Within(0.2), "10 s cooldown");

            var kunai = hands.EnumerateHeld(user).Single(e => entMan.HasComponent<ProperKungFuFanKunaiComponent>(e));
            Assert.That(hands.TrySelect(user, kunai));
            userStart = xform.GetWorldPosition(user);
            Assert.That(serverHands.ThrowHeldItem(user, new EntityCoordinates(map.Grid, 0.5f, 8.5f)), "Thrown");
            Assert.That(FanKunai(entMan, user).Count, Is.EqualTo(5), "Split into five");
        });

        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var angles = FanKunai(entMan, user)
                .Select(k => (xform.GetWorldPosition(k) - userStart))
                .Select(d => Angle.FromWorldVec(d).Degrees - Angle.FromWorldVec(new Vector2(0f, 1f)).Degrees)
                .Select(a => (a + 540) % 360 - 180)
                .OrderBy(a => a)
                .ToList();
            Assert.That(angles, Has.Count.EqualTo(5));
            var want = new[] { -30.0, -15.0, 0.0, 15.0, 30.0 };
            for (var i = 0; i < 5; i++)
            {
                Assert.That(angles[i], Is.EqualTo(want[i]).Within(4.0), $"Kunai {i} in the fan");
            }
        });

        await server.WaitRunTicks(timing.TickRate * 3);

        await server.WaitAssertion(() =>
        {
            Assert.That(FanKunai(entMan, user), Is.Empty, "All gone after landing");
            // The airless test map has no gravity, so the throw's recoil may push the user backwards (-Y);
            // a teleport would put them forward (+Y), where the kunai went.
            Assert.That(xform.GetWorldPosition(user).Y - userStart.Y, Is.LessThan(0.2f), "No teleport");
            Assert.That(entMan.HasComponent<ProperKungFuKunaiFinisherComponent>(user), Is.False, "No finisher");
            var pickups = entMan.EntityQuery<ProperKungFuKunaiComponent>().Count();
            Assert.That(pickups, Is.Zero, "No kunai left lying around");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Wolf Gaze nobody attacks: the stance fades after 3 s, the use is not spent and the cooldown becomes 2 minutes.
    /// </summary>
    [Test]
    public async Task WolfGazeMissTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = entMan.System<ActionsSystem>();

        EntityUid user = default;
        await server.WaitPost(() =>
        {
            Floor(entMan, tileDefs, map, 4);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            StartUltimate(entMan, user);
            var gaze = FindAction(entMan, user, "ActionProperKungFuWolfGaze");
            actions.PerformAction(user, gaze, predicted: false);
            Assert.That(entMan.HasComponent<ProperKungFuGazeStanceComponent>(user), "In the stance");
        });

        await server.WaitRunTicks(timing.TickRate * 3 + 10);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuGazeStanceComponent>(user), Is.False, "Stance over");
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.UltimateGazeUsed, Is.False, "Not spent");
            var gaze = FindAction(entMan, user, "ActionProperKungFuWolfGaze");
            Assert.That(gaze.Comp.Cooldown, Is.Not.Null);
            Assert.That((gaze.Comp.Cooldown!.Value.End - timing.CurTime).TotalSeconds, Is.InRange(115.0, 120.1), "2 minute cooldown");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Wolf Judgment: the claw cross hits a target in the area (never the user), and the full combo happens once
    /// per mode (+120 Slash to the chest); the second use is only the cross.
    /// </summary>
    [Test]
    public async Task WolfJudgmentTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = entMan.System<ActionsSystem>();

        EntityUid user = default;
        EntityUid victim = default;
        EntityUid other = default;
        await server.WaitPost(() =>
        {
            Floor(entMan, tileDefs, map, 30);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 3.0f));
            other = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, -3.0f));
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            StartUltimate(entMan, user);
            var judgment = FindAction(entMan, user, "ActionProperKungFuWolfJudgment");
            Assert.That(actions.SetEventTarget(judgment, victim));
            actions.PerformAction(user, judgment, predicted: false);
            Assert.That((judgment.Comp.Cooldown!.Value.End - timing.CurTime).TotalSeconds, Is.EqualTo(60).Within(0.2), "60 s cooldown");
        });

        await server.WaitRunTicks(timing.TickRate * 12);

        float first = 0f;
        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.UltimateJudgmentUsed, "Full combo spent");
            first = entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float();
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Slash"), Is.GreaterThanOrEqualTo(150f), "Cross + 120 to the chest");
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.Zero, "Never the user");

            // Second use: only the cross on the other mob.
            var judgment = FindAction(entMan, user, "ActionProperKungFuWolfJudgment");
            actions.ClearCooldown(judgment.Owner);
            Assert.That(actions.SetEventTarget(judgment, other));
            actions.PerformAction(user, judgment, predicted: false);
        });

        await server.WaitRunTicks(timing.TickRate * 6);

        await server.WaitAssertion(() =>
        {
            Assert.That(PartDamage(entMan, other, BodyPartType.Chest, "Slash"), Is.EqualTo(40f).Within(1f), "Only the cross, 2 x 20");
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.Zero, "Never the user");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Every v13 info, popup and prototype locale key exists in both languages.
    /// </summary>
    [Test]
    public async Task V13LocaleTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var res = server.ResolveDependency<IResourceManager>();

        var keys = new[]
        {
            "proper-kung-fu-wolf-seal-user", "proper-kung-fu-wolf-seal-target",
            "proper-kung-fu-info-wolf-seal-name", "proper-kung-fu-info-wolf-seal-desc",
            "proper-kung-fu-info-fan-kunai-name", "proper-kung-fu-info-fan-kunai-desc",
            "proper-kung-fu-info-ult-claw-series-name", "proper-kung-fu-info-ult-claw-series-desc",
            "proper-kung-fu-info-ult-wolf-grip-name", "proper-kung-fu-info-ult-wolf-grip-desc",
            "proper-kung-fu-info-ult-perfect-flow-name", "proper-kung-fu-info-ult-perfect-flow-desc",
        };

        await server.WaitAssertion(() =>
        {
            foreach (var culture in new[] { "en-US", "ru-RU" })
            {
                var text = res.ContentFileReadAllText(new ResPath($"/Locale/{culture}/_CorvaxGoob/martial-arts/proper-kung-fu.ftl"))
                    + res.ContentFileReadAllText(new ResPath($"/Locale/{culture}/_CorvaxGoob/martial-arts/proper-kung-fu-info.ftl"));
                foreach (var key in keys)
                {
                    Assert.That(text, Does.Contain($"\n{key} = "), $"{culture}: {key}");
                }
            }

            var ru = res.ContentFileReadAllText(new ResPath("/Locale/ru-RU/ss14-ru/prototypes/_corvaxgoob/actions/proper_kung_fu.ftl"));
            Assert.That(ru, Does.Contain("ent-ActionProperKungFuWolfSeal = "));
            Assert.That(ru, Does.Contain("ent-ActionProperKungFuFanKunai = "));

            var info = ProperKungFuInfo.GetEntries(false).Select(e => e.Name).ToList();
            foreach (var key in new[] { "wolf-seal", "fan-kunai", "ult-claw-series", "ult-wolf-grip", "ult-perfect-flow" })
            {
                Assert.That(info, Does.Contain($"proper-kung-fu-info-{key}-name"), $"Info menu shows {key}");
            }
        });

        await pair.CleanReturnAsync();
    }

    private static List<EntityUid> FanKunai(IEntityManager entMan, EntityUid user)
    {
        var list = new List<EntityUid>();
        var query = entMan.EntityQueryEnumerator<ProperKungFuFanKunaiComponent, ProperKungFuKunaiComponent>();
        while (query.MoveNext(out var uid, out _, out var kunai))
        {
            if (kunai.Summoner == user && !entMan.GetComponent<MetaDataComponent>(uid).EntityDeleted)
                list.Add(uid);
        }

        return list;
    }

    private static void StartUltimate(IEntityManager entMan, EntityUid user)
    {
        var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
        kungFu.UltimateCharge = kungFu.MaxUltimateCharge;
        entMan.System<ActionsSystem>().PerformAction(user, FindAction(entMan, user, "ActionProperKungFuUltimate"), predicted: false);
        Assert.That(kungFu.UltimateActive, "Claw Mastery on");
    }

    private static void Floor(IEntityManager entMan, ITileDefinitionManager tileDefs, Content.IntegrationTests.Pair.TestMapData map, int half)
    {
        var mapSys = entMan.System<SharedMapSystem>();
        var tile = new Tile(tileDefs["Plating"].TileId);
        for (var x = -half; x <= half; x++)
        for (var y = -half; y <= half; y++)
        {
            mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
        }
    }

    private static float PartDamage(IEntityManager entMan, EntityUid target, BodyPartType type, string damageType)
    {
        var part = entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, type).FirstOrDefault();
        if (part == default)
            return -1f;

        return entMan.GetComponent<DamageableComponent>(part.Id).Damage.DamageDict.TryGetValue(damageType, out var value)
            ? value.Float()
            : 0f;
    }

    private static Entity<ActionComponent> FindAction(IEntityManager entMan, EntityUid user, string proto)
    {
        var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
        foreach (var uid in kungFu.ActionEntities.Concat(kungFu.UltimateActionEntities))
        {
            if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == proto)
                return (uid, entMan.GetComponent<ActionComponent>(uid));
        }

        Assert.Fail($"{proto} not granted");
        return default;
    }

    private static EntityUid Learn(IEntityManager entMan, EntityCoordinates coords)
    {
        var user = SpawnMob(entMan, coords);
        var book = entMan.SpawnEntity("ProperKungFuManual", coords);
        Assert.That(entMan.System<SharedHandsSystem>().TryPickupAnyHand(user, book), Is.True);
        entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        return user;
    }

    private static EntityUid SpawnMob(IEntityManager entMan, EntityCoordinates coords)
    {
        var mob = SpawnBreathingMob(entMan, coords);
        entMan.RemoveComponent<RespiratorComponent>(mob);
        return mob;
    }

    /// <summary>
    /// Keeps the respirator (Wolf Seal's breath block needs one); no barotrauma or cold on the airless test map.
    /// </summary>
    private static EntityUid SpawnBreathingMob(IEntityManager entMan, EntityCoordinates coords)
    {
        var mob = entMan.SpawnEntity("MobHuman", coords);
        entMan.RemoveComponent<BarotraumaComponent>(mob);
        entMan.RemoveComponent<TemperatureComponent>(mob);
        return mob;
    }
}

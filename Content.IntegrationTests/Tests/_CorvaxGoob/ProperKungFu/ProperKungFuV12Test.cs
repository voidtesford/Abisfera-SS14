using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Shared.MartialArts;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions.Components;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// Proper Kung Fu v12: the Hammer finisher cross, the ultimate mode, Twenty Shadows and Summon the Dogs.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuComponent))]
public sealed class ProperKungFuV12Test
{
    [Test]
    public async Task HammerFinisherCrossTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var inventory = entMan.System<InventorySystem>();

        EntityUid user = default;
        EntityUid target = default;
        var userHurt = 0f;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 15; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(user);
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));

            var armor = entMan.SpawnEntity("ClothingOuterArmorBasic", new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            Assert.That(inventory.TryEquip(target, armor, "outerClothing", silent: true, force: true), "Armor on");

            // 80 pre-damage on the legs and groin, so the torso starts clean.
            var parts = new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.Groin };
            foreach (var (part, amount) in parts.Zip(new[] { 26f, 26f, 28f }))
            {
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Blunt", amount);
                damageSys.TryChangeDamage(target, dmg, ignoreResistances: true, targetPart: part, canMiss: false);
            }
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.GreaterThanOrEqualTo(80f));
            Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Slash"), Is.LessThanOrEqualTo(0f), "Clean torso");

            var hammer = FindAction(entMan, user, "ActionProperKungFuHammer");
            Assert.That(actions.SetEventTarget(hammer, target));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(entMan.HasComponent<ProperKungFuHammerFinisherComponent>(user), "Finisher started");
        });

        // Spin at 1.35 s; the cross must not be there yet.
        await server.WaitRunTicks((int) (timing.TickRate * 1.2));
        await server.WaitAssertion(() =>
            Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Slash"), Is.LessThanOrEqualTo(0f), "No cross before the spin"));

        await server.WaitRunTicks(timing.TickRate * 3);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuHammerFinisherComponent>(user), Is.False, "Finisher over");
            Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Slash"), Is.EqualTo(90f).Within(0.5f),
                "90 Slash to the torso through the armor");

            var distance = (xformSys.GetWorldPosition(user) - xformSys.GetWorldPosition(target)).Length();
            Assert.That(distance, Is.LessThan(2f), "The user dashed to the landed victim");

            // Bloodlust: the victim carries the Blood Harvest mark for 30 s.
            Assert.That(entMan.TryGetComponent<ProperKungFuBloodHarvestComponent>(target, out var mark), "Blood Harvest mark");
            Assert.That(mark!.Harvester, Is.EqualTo(user));
            Assert.That((mark.ExpiresAt - timing.CurTime).TotalSeconds, Is.InRange(25, 30.1), "30 s mark");

            var hurt = new DamageSpecifier();
            hurt.DamageDict.Add("Blunt", 40);
            damageSys.TryChangeDamage(user, hurt, ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
            userHurt = entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float();
        });

        // The slashed victim bleeds: each bleed tick heals the user for 5 while within 10 tiles.
        await server.WaitRunTicks(timing.TickRate * 6);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.LessThan(userHurt - 4f), "Bloodlust heals"));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UltimateModeAndShadowsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var kungFuSys = entMan.System<ProperKungFuSystem>();

        EntityUid user = default;
        var startedAt = TimeSpan.Zero;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -8; x <= 8; x++)
            for (var y = -8; y <= 8; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.ActionEntities, Has.Count.EqualTo(8));
            var ult = FindAction(entMan, user, "ActionProperKungFuUltimate");

            actions.PerformAction(user, ult, predicted: false);
            Assert.That(kungFu.UltimateActive, Is.False, "Not with an empty bar");

            kungFu.UltimateCharge = kungFu.MaxUltimateCharge;
            actions.PerformAction(user, ult, predicted: false);
            Assert.That(kungFu.UltimateActive, "Mode on");
            Assert.That(kungFu.UltimateCharge, Is.EqualTo(0f), "Bar spent");
            Assert.That(kungFu.ActionEntities, Is.Empty, "Skills out");
            Assert.That(kungFu.UltimateActionEntities, Has.Count.EqualTo(6), "Ultimate skills in");
            Assert.That(kungFu.UltimateEndTime!.Value - timing.CurTime, Is.EqualTo(TimeSpan.FromMinutes(4)).Within(TimeSpan.FromSeconds(0.2)));

            var shadows = FindAction(entMan, user, "ActionProperKungFuUltimateClones");
            actions.PerformAction(user, shadows, predicted: false);
            Assert.That(shadows.Comp.Cooldown, Is.Not.Null);
            startedAt = timing.CurTime;
        });

        // One every half second on the first use: 20 after ~10 s. The 5-health shadows can die on the airless test map, so count every one ever seen.
        var seen = new Dictionary<EntityUid, TimeSpan>();
        async Task Watch(int ticks)
        {
            for (var t = 0; t < ticks; t++)
            {
                await server.WaitRunTicks(1);
                await server.WaitPost(() =>
                {
                    foreach (var (uid, clone) in Shadows(entMan, user))
                    {
                        seen.TryAdd(uid, clone.ExpiresAt);
                        Assert.That(clone.NextSkill, Is.EqualTo(TimeSpan.MaxValue), "No skills");
                    }
                });
            }
        }

        await Watch(timing.TickRate * 2);
        Assert.That(seen.Count, Is.InRange(3, 6), "One every half second, not all at once");

        await Watch(timing.TickRate * 9);
        Assert.That(seen, Has.Count.EqualTo(20), "Twenty shadows");
        foreach (var expires in seen.Values)
        {
            Assert.That((expires - startedAt).TotalSeconds, Is.InRange(59.5, 70.5), "60 s of life");
        }

        await server.WaitAssertion(() =>
        {
            var action = FindAction(entMan, user, "ActionProperKungFuUltimateClones");
            Assert.That(action.Comp.UseDelay, Is.EqualTo(TimeSpan.FromSeconds(60)), "60 s cooldown");
            actions.ClearCooldown(action.Owner);
            actions.PerformAction(user, action, predicted: false);
        });

        await Watch(timing.TickRate * 2);
        Assert.That(seen, Has.Count.EqualTo(25), "A re-use adds 5");
        await server.WaitPost(() => kungFuSys.EndUltimate((user, entMan.GetComponent<ProperKungFuComponent>(user))));

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.UltimateActive, Is.False);
            Assert.That(kungFu.UltimateActionEntities, Is.Empty);
            Assert.That(kungFu.ActionEntities, Has.Count.EqualTo(8), "Skills back");
            Assert.That(Shadows(entMan, user).Count(s => !s.Clone.Vanished), Is.Zero, "All shadows gone with the mode");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SummonTheDogsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var kungFuSys = entMan.System<ProperKungFuSystem>();
        var martial = entMan.System<SharedMartialArtsSystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid user = default;
        var pupils = new EntityUid[4];

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 15; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            kungFuSys.StartUltimate((user, kungFu));
            var summon = FindAction(entMan, user, "ActionProperKungFuSummonPack");
            Assert.That(summon.Comp.UseDelay, Is.EqualTo(TimeSpan.FromSeconds(30)), "30 s cooldown");
            actions.PerformAction(user, summon, predicted: false);
            Assert.That(summon.Comp.Cooldown, Is.Null, "Nobody to call: the skill is not spent");

            // Distances 3 (dead), 6, 9 and 12 tiles.
            for (var i = 0; i < pupils.Length; i++)
            {
                pupils[i] = SpawnMob(entMan, new EntityCoordinates(map.Grid, 3.5f + 3f * i, 0.5f));
                Assert.That(martial.GrantProperKungFuPupil(pupils[i]));
            }

            var kill = new DamageSpecifier();
            kill.DamageDict.Add("Blunt", 300);
            damageSys.TryChangeDamage(pupils[0], kill, ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(kungFuSys.GetSummonablePupils(user, 2), Is.EqualTo(new[] { pupils[1], pupils[2] }), "Nearest two living");

            var summon = FindAction(entMan, user, "ActionProperKungFuSummonPack");
            actions.PerformAction(user, summon, predicted: false);
            Assert.That(summon.Comp.Cooldown, Is.Not.Null);

            var origin = xformSys.GetWorldPosition(user);
            Assert.That((xformSys.GetWorldPosition(pupils[1]) - origin).Length(), Is.LessThan(2f), "First dog at the side");
            Assert.That((xformSys.GetWorldPosition(pupils[2]) - origin).Length(), Is.LessThan(2f), "Second dog at the side");
            Assert.That((xformSys.GetWorldPosition(pupils[3]) - origin).Length(), Is.GreaterThan(10f), "Only two come");
            Assert.That((xformSys.GetWorldPosition(pupils[0]) - origin).Length(), Is.GreaterThan(2.5f), "The dead stay");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task V12DataTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var loc = server.ResolveDependency<ILocalizationManager>();

        await server.WaitAssertion(() =>
        {
            Assert.That(new ProperKungFuComponent { Clone = true }.HasPassives, Is.False, "Wolf Pack clones have no passives");
            Assert.That(new ProperKungFuComponent { Pupil = true }.HasPassives, Is.False);
            Assert.That(new ProperKungFuComponent().HasPassives);
            Assert.That(new ProperKungFuCloneComponent().DamageModifier, Is.EqualTo(0.25f), "Quarter damage");
            Assert.That(ProperKungFuInfo.KunaiFinisherThreshold, Is.EqualTo(80f));
            Assert.That(ProperKungFuSystem.KunaiThrowSpeedMultiplier, Is.EqualTo(2f), "Kunai fly twice as fast");
            Assert.That(ProperKungFuSystem.StrongTorsoSlashCoefficient, Is.EqualTo(0.7f), "Torso armor Slash <= 0.7 saves the limbs");

            foreach (var key in new[]
                     {
                         "proper-kung-fu-kunai-finisher-user", "proper-kung-fu-kunai-death-punch-user",
                         "proper-kung-fu-ultimate-start-user", "proper-kung-fu-ultimate-end",
                         "proper-kung-fu-summon-pack-none", "proper-kung-fu-summon-pack-call",
                         "proper-kung-fu-hammer-finisher-cross-user", "proper-kung-fu-hammer-finisher-cross-target",
                         "proper-kung-fu-info-finisher-kunai-name", "proper-kung-fu-info-passive-bloodlust-name",
                         "proper-kung-fu-info-summon-pack-name", "proper-kung-fu-info-ultimate-clones-name",
                     })
            {
                Assert.That(loc.HasString(key), key);
            }
        });

        await pair.CleanReturnAsync();
    }

    private static List<(EntityUid Uid, ProperKungFuCloneComponent Clone)> Shadows(IEntityManager entMan, EntityUid user)
    {
        var list = new List<(EntityUid, ProperKungFuCloneComponent)>();
        var query = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent>();
        while (query.MoveNext(out var uid, out var clone))
        {
            if (clone.Ultimate && clone.Summoner == user && !entMan.IsQueuedForDeletion(uid))
                list.Add((uid, clone));
        }

        return list;
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
        var mob = entMan.SpawnEntity("MobHuman", coords);
        entMan.RemoveComponent<BarotraumaComponent>(mob);
        entMan.RemoveComponent<TemperatureComponent>(mob);
        return mob;
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
}

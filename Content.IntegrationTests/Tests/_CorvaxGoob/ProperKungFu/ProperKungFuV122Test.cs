using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Shared.MartialArts;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.NPC.HTN;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Actions.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.CombatMode;
using Content.Shared.Maps;
using Content.Shared.StatusEffect;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee;
using Content.Server.Weapons.Melee;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// Proper Kung Fu v12.2: the clone HTN never crashes the server (KeyNotFoundException 'TargetCoordinates' in
/// MoveToOperator.Startup), the ultimate's shadows are hostile to everything but their summoner and the Pack Dogs,
/// and Wolf Pack clones have no finishers.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuCloneTargetOperator))]
public sealed class ProperKungFuV122Test
{
    /// <summary>
    /// Wolf Pack clones and ultimate shadows fight next to hostile dummies while the NPC system runs for several
    /// seconds: forced pack target changes every half second, one target killed, one deleted mid-plan, and clone
    /// lifetimes running out. Any exception thrown by an HTN operator fails the server tick and so this test.
    /// </summary>
    [Test]
    public async Task CloneHtnStressTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var mobState = entMan.System<MobStateSystem>();
        var kungFuSys = entMan.System<ProperKungFuSystem>();

        EntityUid user = default;
        EntityUid dummyA = default;
        EntityUid dummyB = default;
        EntityUid dummyC = default;
        var packClones = new List<EntityUid>();
        var hammerWorked = false;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -10; x <= 10; x++)
            for (var y = -10; y <= 10; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(user);
            dummyA = SpawnMob(entMan, new EntityCoordinates(map.Grid, 3.5f, 0.5f));
            dummyB = SpawnMob(entMan, new EntityCoordinates(map.Grid, -3.5f, 2.5f));
            dummyC = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, -4.5f));
            PreDamage(damageSys, dummyA);
        });

        await server.WaitRunTicks(5);

        // Wolf Pack on the 85-damage dummy, and one clone's Hammer straight away: abilities work, no finisher.
        await server.WaitAssertion(() =>
        {
            var pack = FindAction(entMan, user, ProperKungFuSystem.WolfPackAction);
            Assert.That(actions.SetEventTarget(pack, dummyA));
            actions.PerformAction(user, pack, predicted: false);

            Assert.That(entMan.TryGetComponent<ProperKungFuPackLeaderComponent>(user, out var leader), "Pack summoned");
            packClones.AddRange(leader!.Clones);
            Assert.That(packClones, Is.Not.Empty);

            var clone = packClones[0];
            Assert.That(kungFuSys.IsWolfPackClone(clone));
            xformSys.SetCoordinates(clone, new EntityCoordinates(map.Grid, 2.5f, 0.5f));
            var before = entMan.GetComponent<DamageableComponent>(dummyA).TotalDamage.Float();
            Assert.That(before, Is.GreaterThanOrEqualTo(80f), "Over the finisher threshold");

            var hammer = FindAction(entMan, clone, "ActionProperKungFuHammer");
            Assert.That(actions.SetEventTarget(hammer, dummyA));
            actions.PerformAction(clone, hammer, predicted: false);
            hammerWorked = entMan.GetComponent<DamageableComponent>(dummyA).TotalDamage.Float() > before;
            Assert.That(entMan.HasComponent<ProperKungFuHammerFinisherComponent>(clone), Is.False,
                "A Wolf Pack clone's Hammer on an 85-damage target is a plain Hammer, not the finisher");
        });

        Assert.That(hammerWorked, "The clone's Hammer still hits (reduced damage)");

        // The ultimate's shadows join in.
        await server.WaitPost(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            kungFuSys.StartUltimate((user, kungFu));
            var shadows = FindAction(entMan, user, "ActionProperKungFuUltimateClones");
            actions.PerformAction(user, shadows, predicted: false);
        });

        var finisherOnClone = new List<string>();
        var maxShadows = 0;
        var step = timing.TickRate / 2;

        // 10 s in half-second steps.
        for (var i = 0; i < 20; i++)
        {
            await server.WaitRunTicks(step);
            var index = i;
            await server.WaitPost(() =>
            {
                var leaderExists = entMan.TryGetComponent<ProperKungFuPackLeaderComponent>(user, out var leader);
                var alive = new[] { dummyA, dummyB, dummyC }
                    .Where(d => entMan.EntityExists(d) && !entMan.IsQueuedForDeletion(d) && !mobState.IsDead(d))
                    .ToList();

                // Forced target changes, back and forth between whoever is still standing.
                if (leaderExists && alive.Count > 0)
                    kungFuSys.SetPackTarget(user, leader!, alive[index % alive.Count]);

                switch (index)
                {
                    case 4:
                        // The pack's target dies.
                        var kill = new DamageSpecifier();
                        kill.DamageDict.Add("Blunt", 400);
                        damageSys.TryChangeDamage(dummyB, kill, ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
                        break;
                    case 7:
                        // The pack's target is deleted outright, mid-plan.
                        entMan.DeleteEntity(dummyA);
                        break;
                    case 11:
                        // Half the clones' lifetimes run out (pack and shadows).
                        var n = 0;
                        var expire = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent>();
                        while (expire.MoveNext(out _, out var c))
                        {
                            if (n++ % 2 == 0)
                                c.ExpiresAt = timing.CurTime;
                        }

                        break;
                    case 15:
                        // Everything left expires; the one-by-one shadow wave (0.5 s each) is stopped first.
                        entMan.RemoveComponent<ProperKungFuUltimateSpawnerComponent>(user);
                        var all = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent>();
                        while (all.MoveNext(out _, out var c))
                        {
                            c.ExpiresAt = timing.CurTime;
                        }

                        break;
                }

                var shadows = 0;
                var query = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent, HTNComponent>();
                while (query.MoveNext(out var uid, out var clone, out _))
                {
                    if (clone.Ultimate)
                        shadows++;

                    foreach (var comp in entMan.GetComponents(uid))
                    {
                        var name = comp.GetType().Name;
                        if (name.Contains("Finisher") && name.StartsWith("ProperKungFu"))
                            finisherOnClone.Add(name);
                    }
                }

                maxShadows = Math.Max(maxShadows, shadows);
            });
        }

        await server.WaitRunTicks(timing.TickRate);

        await server.WaitAssertion(() =>
        {
            Assert.That(maxShadows, Is.GreaterThan(0), "Shadows took part");
            Assert.That(finisherOnClone, Is.Empty, "No clone ever started a finisher: " + string.Join(", ", finisherOnClone));
            Assert.That(entMan.Deleted(dummyA), "The deleted target is gone");
            Assert.That(mobState.IsDead(dummyB), "The killed target stayed dead");

            var left = 0;
            var query = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent>();
            while (query.MoveNext(out var uid, out _))
            {
                if (!entMan.IsQueuedForDeletion(uid))
                    left++;
            }

            Assert.That(left, Is.Zero, "Every clone vanished when its lifetime ran out");
            Assert.That(entMan.HasComponent<ProperKungFuPackLeaderComponent>(user), Is.False, "The empty pack is cleaned up");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The Twenty Shadows are hostile to every faction (including the summoner's own crew), spare only the summoner
    /// and the Pack Dogs (also a Pack Dog taught after they appeared), and go for nearby targets on their own.
    /// </summary>
    [Test]
    public async Task ShadowsHostileToAllTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var faction = entMan.System<NpcFactionSystem>();
        var martial = entMan.System<SharedMartialArtsSystem>();
        var kungFuSys = entMan.System<ProperKungFuSystem>();

        EntityUid user = default;
        EntityUid pupil = default;
        EntityUid laterPupil = default;
        EntityUid crewmate = default;
        EntityUid mouse = default;
        var laterPupilDamage = 0f;

        // Every faction in the game, not just the ones known when this was written.
        var shadowFaction = protoMan.Index<NpcFactionPrototype>(ProperKungFuSystem.UltimateShadowFaction);
        var notHostile = protoMan.EnumeratePrototypes<NpcFactionPrototype>()
            .Where(f => f.ID != shadowFaction.ID && !shadowFaction.Hostile.Contains(f.ID))
            .Select(f => f.ID)
            .ToList();
        Assert.That(notHostile, Is.Empty, "The shadow faction is hostile to every other faction");
        Assert.That(shadowFaction.Friendly, Is.Empty, "and friendly to none");

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -10; x <= 10; x++)
            for (var y = -10; y <= 10; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(user);
            pupil = SpawnMob(entMan, new EntityCoordinates(map.Grid, -2.5f, 0.5f));
            Assert.That(martial.GrantProperKungFuPupil(pupil));
            laterPupil = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, -2.5f));
            crewmate = SpawnMob(entMan, new EntityCoordinates(map.Grid, 4.5f, 0.5f));
            mouse = entMan.SpawnEntity("MobMouse", new EntityCoordinates(map.Grid, 0.5f, 4.5f));

            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            kungFuSys.StartUltimate((user, kungFu));
            actions.PerformAction(user, FindAction(entMan, user, "ActionProperKungFuUltimateClones"), predicted: false);
        });

        await server.WaitRunTicks(timing.TickRate * 2);

        var shadows = new List<EntityUid>();
        await server.WaitAssertion(() =>
        {
            var query = entMan.EntityQueryEnumerator<ProperKungFuCloneComponent>();
            while (query.MoveNext(out var uid, out var clone))
            {
                if (clone.Ultimate && !entMan.IsQueuedForDeletion(uid))
                    shadows.Add(uid);
            }

            Assert.That(shadows, Is.Not.Empty, "Shadows spawned");
            foreach (var shadow in shadows)
            {
                Assert.That(faction.IsMember(shadow, ProperKungFuSystem.UltimateShadowFaction), "In the shadow faction");
                Assert.That(faction.IsMemberOfAny(shadow, protoMan.EnumeratePrototypes<NpcFactionPrototype>()
                    .Where(f => f.ID != shadowFaction.ID).Select(f => new ProtoId<NpcFactionPrototype>(f.ID))), Is.False,
                    "In no other faction (not the summoner's crew)");
                Assert.That(faction.IsIgnored(shadow, user), "Spares the summoner");
                Assert.That(faction.IsIgnored(shadow, pupil), "Spares the Pack Dog");

                var hostiles = faction.GetNearbyHostiles(shadow, 12f).ToList();
                Assert.That(hostiles, Does.Contain(crewmate), "Hostile to the summoner's own crew");
                Assert.That(hostiles, Does.Not.Contain(user));
                Assert.That(hostiles, Does.Not.Contain(pupil));
            }

            // A Pack Dog taught after the shadows appeared (it may already have been hit as a stranger).
            Assert.That(martial.GrantProperKungFuPupil(laterPupil));
        });

        // One think (0.25 s) plus a swing already in flight.
        await server.WaitRunTicks(timing.TickRate);
        await server.WaitPost(() =>
            laterPupilDamage = entMan.GetComponent<DamageableComponent>(laterPupil).TotalDamage.Float());
        await server.WaitRunTicks(timing.TickRate * 4);

        await server.WaitAssertion(() =>
        {
            foreach (var shadow in shadows.Where(s => entMan.EntityExists(s) && !entMan.IsQueuedForDeletion(s)))
            {
                Assert.That(faction.IsIgnored(shadow, laterPupil), "Spares the later Pack Dog too");
            }

            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.Zero, "Summoner untouched");
            Assert.That(entMan.GetComponent<DamageableComponent>(pupil).TotalDamage.Float(), Is.Zero, "Pack Dog untouched");
            Assert.That(entMan.GetComponent<DamageableComponent>(laterPupil).TotalDamage.Float(), Is.EqualTo(laterPupilDamage),
                "Once taught, the later Pack Dog is left alone");
            Assert.That(entMan.GetComponent<DamageableComponent>(crewmate).TotalDamage.Float(), Is.GreaterThan(0f),
                "Nobody ordered it, yet the summoner's crewmate was attacked");
            Assert.That(!entMan.EntityExists(mouse) || entMan.GetComponent<DamageableComponent>(mouse).TotalDamage.Float() > 0f,
                "The mouse was attacked too");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Any kunai hit stuns for 2 s: a melee hit by anyone holding one (also an untrained mob, as the shadows are),
    /// and a thrown one. With the melee finisher's own 3 s stun the longer one wins, nothing accumulates.
    /// </summary>
    [Test]
    public async Task KunaiHitStunTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var mapSys = entMan.System<SharedMapSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var melee = entMan.System<MeleeWeaponSystem>();
        var throwing = entMan.System<ThrowingSystem>();
        var status = entMan.System<StatusEffectsSystem>();
        var combat = entMan.System<SharedCombatModeSystem>();

        EntityUid user = default;
        EntityUid stranger = default;
        EntityUid victimPlain = default;
        EntityUid victimFinisher = default;
        EntityUid victimThrown = default;
        EntityUid kunaiStranger = default;
        EntityUid kunaiUser = default;

        double Remaining(EntityUid uid)
        {
#pragma warning disable CS0618
            return status.TryGetTime(uid, "Stun", out var time) ? (time.Value.Item2 - timing.CurTime).TotalSeconds : 0;
#pragma warning restore CS0618
        }

        EntityUid MakeKunai(EntityUid holder)
        {
            var kunai = entMan.SpawnEntity("ProperKungFuKunai", entMan.GetComponent<TransformComponent>(holder).Coordinates);
            var comp = entMan.GetComponent<ProperKungFuKunaiComponent>(kunai);
            comp.Summoner = holder;
            comp.ExpiresAt = TimeSpan.FromHours(1);
            Assert.That(hands.TryPickupAnyHand(holder, kunai), "Kunai in hand");
            return kunai;
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -10; x <= 10; x++)
            for (var y = -10; y <= 10; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(user);
            stranger = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 6.5f));
            victimPlain = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.3f, 6.5f));
            victimFinisher = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.3f, 0.5f));
            victimThrown = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, -4.5f));
            PreDamage(damageSys, victimFinisher);
            kunaiStranger = MakeKunai(stranger);
            combat.SetInCombatMode(stranger, true);
            combat.SetInCombatMode(user, true);
        });

        await server.WaitRunTicks(5);

        // Melee, wielded by someone untrained: no stun at all (v13: only thrown kunai stun), no finisher.
        await server.WaitAssertion(() =>
        {
            Assert.That(Remaining(victimPlain), Is.Zero);
            var weapon = entMan.GetComponent<MeleeWeaponComponent>(kunaiStranger);
            weapon.NextAttack = TimeSpan.Zero; // Skip the swing cooldown from picking it up.
            Assert.That(melee.AttemptLightAttack(stranger, kunaiStranger, weapon, victimPlain), "Swung");
            Assert.That(entMan.GetComponent<DamageableComponent>(victimPlain).TotalDamage.Float(), Is.GreaterThan(0f), "The swing landed");
            Assert.That(entMan.HasComponent<StunnedComponent>(victimPlain), Is.False, "A melee kunai hit never stuns");
            Assert.That(Remaining(victimPlain), Is.Zero, "No melee kunai stun");
            Assert.That(entMan.HasComponent<ProperKungFuKunaiFinisherComponent>(stranger), Is.False);
        });

        // Melee finisher: its own 3 s stun wins over the 2 s, no 5 s pile-up.
        await server.WaitPost(() => kunaiUser = MakeKunai(user));
        await server.WaitAssertion(() =>
        {
            var weapon = entMan.GetComponent<MeleeWeaponComponent>(kunaiUser);
            weapon.NextAttack = TimeSpan.Zero;
            Assert.That(melee.AttemptLightAttack(user, kunaiUser, weapon, victimFinisher), "Swung");
            Assert.That(entMan.HasComponent<ProperKungFuKunaiFinisherComponent>(user), "The melee kunai finisher started");
            Assert.That(Remaining(victimFinisher), Is.EqualTo(3).Within(0.05), "The finisher's 3 s stun, not 2 + 3");
        });

        // Thrown at an unhurt victim: plain 2 s stun (after the melee finisher and its time stop are over).
        await server.WaitRunTicks(timing.TickRate * 13);
        await server.WaitPost(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuKunaiFinisherComponent>(user), Is.False, "Finisher over");
            var thrown = entMan.SpawnEntity("ProperKungFuKunai", new EntityCoordinates(map.Grid, 0.5f, -2.5f));
            var comp = entMan.GetComponent<ProperKungFuKunaiComponent>(thrown);
            comp.Summoner = stranger;
            comp.ExpiresAt = TimeSpan.FromHours(1);
            throwing.TryThrow(thrown, new Vector2(0f, -3f), 10f, stranger, pushbackRatio: 0f, recoil: false, playSound: false);
        });

        var thrownStun = 0.0;
        for (var i = 0; i < 30 && thrownStun <= 0; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() => thrownStun = Remaining(victimThrown));
        }

        Assert.That(thrownStun, Is.GreaterThan(1.8).And.LessThanOrEqualTo(ProperKungFuSystem.KunaiHitStun.TotalSeconds + 0.05),
            "A thrown kunai hit stuns for 2 s");

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The death punch impact (flash, shockwave ring, residual glow and crackling wisps) is spawned at the impact
    /// point and cleans itself up after about a second; the kanji dissolves instead of vanishing.
    /// </summary>
    [Test]
    public async Task DeathPunchImpactTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var kungFuSys = entMan.System<ProperKungFuSystem>();

        var kanji = protoMan.Index<EntityPrototype>("ProperKungFuOneInchKanji");
        Assert.That(kanji.Components.ContainsKey("ProperKungFuDissolve"), "The kanji dissolves");
        Assert.That(kanji.Components.ContainsKey("FadingTimedDespawn"), Is.False);

        var ids = new[] { "ProperKungFuDeathPunchFlash", "ProperKungFuDeathPunchRing", "ProperKungFuDeathPunchResidue", "ProperKungFuDeathPunchWisp" };
        int Count(string id)
        {
            var n = 0;
            var query = entMan.EntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID == id && !entMan.IsQueuedForDeletion(uid))
                    n++;
            }

            return n;
        }

        await server.WaitAssertion(() =>
        {
            kungFuSys.SpawnDeathPunchImpact(map.MapId, new Vector2(0.5f, 0.5f));
            Assert.That(Count(ids[0]), Is.EqualTo(1), "Flash");
            Assert.That(Count(ids[1]), Is.EqualTo(1), "Shockwave ring");
            Assert.That(Count(ids[2]), Is.EqualTo(1), "Residual glow");
            Assert.That(Count(ids[3]), Is.EqualTo(ProperKungFuSystem.DeathPunchWispCount), "Crackling wisps");
        });

        await server.WaitRunTicks((int) (timing.TickRate * 1.6f));

        await server.WaitAssertion(() =>
        {
            foreach (var id in ids)
            {
                Assert.That(Count(id), Is.Zero, $"{id} gone after its fade");
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Technique names are only ever shown to whoever performed them: the victim's and bystanders' lines never
    /// name the move, and Proper Kung Fu never uses the stock martial arts combo announcement.
    /// </summary>
    [Test]
    public async Task TechniqueNamesPrivateTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var res = server.ResolveDependency<IResourceManager>();

        var names = new Dictionary<string, string[]>
        {
            ["en-US"] = new[]
            {
                "wolf dance", "claw series", "claw whirlwind", "wolf grip", "cross slash", "hammer", "wolf instinct",
                "perfect flow", "wolf pack", "twenty shadows", "one-inch", "death punch", "counter", "finisher", "ultimate", "seal", "fan kunai", "claw mastery",
            },
            ["ru-RU"] = new[]
            {
                "волч", "серию когтей", "серия когтей", "вихр", "крестовый", "молот", "инстинкт", "идеальн", "поток",
                "двадцать теней", "одного дюйма", "смертельн", "контратак", "добивани", "ульт", "печат", "веерн", "мастерств",
            },
        };

        await server.WaitAssertion(() =>
        {
            foreach (var (culture, words) in names)
            {
                var text = res.ContentFileReadAllText(new ResPath($"/Locale/{culture}/_CorvaxGoob/martial-arts/proper-kung-fu.ftl"));
                var checkedLines = 0;
                foreach (var line in text.Split('\n'))
                {
                    var eq = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (eq < 0)
                        continue;

                    var key = line[..eq].Trim();
                    if (!key.EndsWith("-target") && !key.EndsWith("-others") && !key.EndsWith("-neck"))
                        continue;

                    checkedLines++;
                    var value = line[(eq + 3)..].ToLowerInvariant();
                    foreach (var word in words)
                    {
                        Assert.That(value, Does.Not.Contain(word), $"{culture} {key} names a technique to someone else");
                    }
                }

                Assert.That(checkedLines, Is.GreaterThan(30), $"{culture}: victim and bystander lines found");
            }
        });

        await pair.CleanReturnAsync();
    }

    private static void PreDamage(DamageableSystem damageSys, EntityUid target)
    {
        foreach (var (part, amount) in new[] { (TargetBodyPart.LeftLeg, 28f), (TargetBodyPart.RightLeg, 28f), (TargetBodyPart.Groin, 29f) })
        {
            var dmg = new DamageSpecifier();
            dmg.DamageDict.Add("Blunt", amount);
            damageSys.TryChangeDamage(target, dmg, ignoreResistances: true, targetPart: part, canMiss: false);
        }
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
        entMan.RemoveComponent<RespiratorComponent>(mob);
        return mob;
    }
}

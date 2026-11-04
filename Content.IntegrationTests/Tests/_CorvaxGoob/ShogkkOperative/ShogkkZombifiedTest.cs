using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.ShogkkOperative;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ShogkkOperative;

/// <summary>
/// Ordinary SHOGKK zombified: prototypes and job gear, pale amber-red eyes, no guns, neutrality with the operatives,
/// hostility to humans and Gilbert, wandering, hordes and the groan that calls the others to the hunt.
/// </summary>
[TestFixture]
public sealed class ShogkkZombifiedTest
{
    private const string Victim = "MobHuman";

    private static readonly string[] Variants =
    {
        "MobShogkkZombified", "MobShogkkZombifiedAssistant", "MobShogkkZombifiedEngineer", "MobShogkkZombifiedScientist",
        "MobShogkkZombifiedMedic", "MobShogkkZombifiedSecurity", "MobShogkkZombifiedCargo", "MobShogkkZombifiedChef",
        "MobShogkkZombifiedBotanist", "MobShogkkZombifiedJanitor", "MobShogkkZombifiedService",
    };

    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  parent: MobShogkkZombifiedSecurity
  id: TestMobShogkkZombifiedSecurityTool
  components:
  - type: ShogkkZombifiedGear
    toolChance: 1
    jobs:
    - id: security
      gear:
      - ShogkkZombifiedGearSecurity
      tools:
      - Stunbaton
";

    #region Helpers

    private static async Task FillFloor(TestPair pair, TestMapData map, int minX, int maxX, int minY, int maxY)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
                tiles.Add((new Vector2i(x, y), map.Tile.Tile));
            mapSys.SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.Server.WaitRunTicks((int) Math.Ceiling(seconds * rate) + 1);
    }

    private static FixedPoint2 Total(IEntityManager entMan, EntityUid target)
    {
        return entMan.TryGetComponent<DamageableComponent>(target, out var dmg) ? dmg.TotalDamage : FixedPoint2.Zero;
    }

    /// <summary>Damage other than asphyxiation (the test maps have no air).</summary>
    private static float Hits(IEntityManager entMan, EntityUid target)
    {
        return entMan.GetComponent<DamageableComponent>(target).Damage.DamageDict
            .Where(kv => kv.Key != "Asphyxiation").Sum(kv => kv.Value.Float());
    }

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static ShogkkZombifiedAIComponent ZAI(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<ShogkkZombifiedAIComponent>(uid);

    private static string? Proto(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;

    /// <summary>Everything inside him, at any depth (hands, slots, bags).</summary>
    private static IEnumerable<EntityUid> Contents(IEntityManager entMan, EntityUid uid)
    {
        if (!entMan.TryGetComponent<ContainerManagerComponent>(uid, out var manager))
            yield break;

        foreach (var container in manager.Containers.Values)
        foreach (var item in container.ContainedEntities)
        {
            yield return item;
            foreach (var inner in Contents(entMan, item))
                yield return inner;
        }
    }

    private static string? Slot(IEntityManager entMan, EntityUid uid, string slot)
    {
        return entMan.System<InventorySystem>().TryGetSlotEntity(uid, slot, out var item) ? Proto(entMan, item.Value) : null;
    }

    private static EntityUid SpawnZombified(IEntityManager entMan, TestMapData map, Vector2 at, string proto = "MobShogkkZombifiedAssistant")
    {
        var uid = entMan.SpawnEntity(proto, map.GridCoords.Offset(at));
        Sturdy(entMan, uid);
        return uid;
    }

    /// <summary>He stands still (no wandering, no horde) until something to chase turns up.</summary>
    private static void Hold(IEntityManager entMan, EntityUid uid)
    {
        var comp = ZAI(entMan, uid);
        comp.PauseUntil = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        comp.NextHordeCheck = comp.PauseUntil;
    }

    #endregion

    /// <summary>
    /// Every variant: the zombified base (no breathing / pressure / cold), the marker, his AI, the faction, pale
    /// amber-red eyes and light, a two-word name, the job outfit, no ghost role, no radio, no gun anywhere on him.
    /// </summary>
    [Test]
    public async Task PrototypesAndJobGear()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -12, 12, -4, 4);
        var entMan = server.EntMan;
        var protos = server.ResolveDependency<IPrototypeManager>();
        var eyes = Color.FromHex("#ff9a7a");

        var spawned = new List<(string Proto, EntityUid Uid)>();
        await server.WaitPost(() =>
        {
            var x = -11.5f;
            foreach (var proto in Variants)
            {
                spawned.Add((proto, SpawnZombified(entMan, map, new Vector2(x, 0.5f), proto)));
                x += 2f;
            }
        });
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            foreach (var (proto, uid) in spawned)
            {
                Assert.That(protos.EnumerateAllParents<EntityPrototype>(proto, true).Select(p => p.id),
                    Does.Contain("BaseMobShogkkZombified"), $"{proto}: on the zombified base");
                Assert.That(entMan.HasComponent<ShogkkZombifiedComponent>(uid), proto);
                Assert.That(entMan.HasComponent<ShogkkZombifiedAIComponent>(uid), proto);
                Assert.That(entMan.HasComponent<ShogkkOperativeComponent>(uid), Is.False, proto);
                Assert.That(entMan.HasComponent<Content.Shared._Shitmed.Body.Components.BreathingImmunityComponent>(uid), $"{proto}: no breathing");
                Assert.That(entMan.HasComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(uid), proto);
                Assert.That(entMan.HasComponent<GhostRoleComponent>(uid), Is.False, $"{proto}: no ghost role");
                Assert.That(entMan.GetComponent<Content.Shared.NPC.Components.NpcFactionMemberComponent>(uid).Factions
                    .Select(f => f.Id), Is.EquivalentTo(new[] { "ShogkkZombified" }), proto);

                var humanoid = entMan.GetComponent<HumanoidAppearanceComponent>(uid);
                Assert.That(humanoid.EyeColor, Is.EqualTo(eyes), $"{proto}: pale amber-red eyes");
                var light = entMan.GetComponent<PointLightComponent>(uid);
                Assert.That(light.Color, Is.EqualTo(eyes), $"{proto}: eye light colour");
                Assert.That(light.Energy, Is.LessThan(0.7f), $"{proto}: dimmer than the operatives' glow (0.7)");
                Assert.That(light.Radius, Is.LessThan(1.6f), $"{proto}: smaller than the operatives' glow (1.6)");
                Assert.That(eyes.R, Is.EqualTo(1f), "red-dominant");
                Assert.That(eyes.R > eyes.G && eyes.G > eyes.B, "an amber-red hue (red > green > blue)");

                var gear = entMan.GetComponent<ShogkkZombifiedGearComponent>(uid);
                var name = entMan.GetComponent<MetaDataComponent>(uid).EntityName;
                Assert.That(name.Split(' '), Has.Length.EqualTo(2), $"{proto}: first + last name, got '{name}'");
                Assert.That(name, Is.EqualTo(gear.FullName));

                var all = Contents(entMan, uid).ToList();
                Assert.That(all.Where(entMan.HasComponent<GunComponent>), Is.Empty, $"{proto}: no gun");
                Assert.That(all.Select(e => Proto(entMan, e) ?? ""), Has.None.Contains("Radio").And.None.Contains("Dogtag"), $"{proto}: no radio, no dog tag");

                var jumpsuit = Slot(entMan, uid, "jumpsuit");
                var outer = Slot(entMan, uid, "outerClothing");
                var head = Slot(entMan, uid, "head");
                TestContext.Out.WriteLine($"[zomb] {proto}: {name} ({humanoid.Sex}), job {gear.Job}, outfit {gear.Outfit}, jumpsuit {jumpsuit}, outer {outer}, head {head}, tool {(gear.Tool is { } t ? Proto(entMan, t) : "-")}");
                Assert.That(gear.Job, Is.Not.Null, proto);
                Assert.That(jumpsuit, Is.Not.Null, $"{proto}: job jumpsuit");
                Assert.That(Slot(entMan, uid, "shoes"), Is.Not.Null, $"{proto}: shoes");
                switch (proto)
                {
                    case "MobShogkkZombifiedEngineer":
                        Assert.That(jumpsuit, Is.EqualTo("ClothingUniformJumpsuitEngineering"));
                        Assert.That(head == "ClothingHeadHatHardhatYellow" || outer == "ClothingOuterVestHazard", "hard hat or hi-vis vest");
                        break;
                    case "MobShogkkZombifiedSecurity":
                        Assert.That(jumpsuit, Is.EqualTo("ClothingUniformJumpsuitSec"));
                        Assert.That(outer, Is.EqualTo("ClothingOuterArmorBasic"), "armour vest");
                        break;
                    case "MobShogkkZombifiedScientist":
                        Assert.That(jumpsuit, Is.EqualTo("ClothingUniformJumpsuitScientist"));
                        Assert.That(outer, Does.StartWith("ClothingOuterCoat"), "lab coat");
                        break;
                    case "MobShogkkZombifiedMedic":
                        Assert.That(jumpsuit, Is.AnyOf("ClothingUniformJumpsuitMedicalDoctor", "ClothingUniformJumpsuitParamedic"));
                        break;
                    case "MobShogkkZombifiedCargo":
                        Assert.That(jumpsuit, Is.AnyOf("ClothingUniformJumpsuitCargo", "ClothingUniformJumpsuitSalvageSpecialist"));
                        break;
                    case "MobShogkkZombifiedService":
                        Assert.That(gear.Job, Is.AnyOf("chef", "botanist", "janitor"));
                        break;
                }

                if (gear.Tool is { } tool)
                {
                    Assert.That(entMan.System<SharedHandsSystem>().IsHolding(uid, tool), $"{proto}: the tool is in his hand");
                    Assert.That(entMan.HasComponent<MeleeWeaponComponent>(tool), $"{proto}: and he can hit with it");
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The job tool: in his active hand, and NPC melee hits the victim with it (a stun baton, switched off).</summary>
    [Test]
    public async Task JobToolInHandIsUsed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -8, 8, -8, 8);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;

        EntityUid z = default, human = default;
        await server.WaitPost(() =>
        {
            z = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f), "TestMobShogkkZombifiedSecurityTool");
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var gear = entMan.GetComponent<ShogkkZombifiedGearComponent>(z);
            Assert.That(gear.Tool, Is.Not.Null);
            Assert.That(Proto(entMan, gear.Tool!.Value), Is.EqualTo("Stunbaton"));
            Assert.That(entMan.System<SharedHandsSystem>().GetActiveItem(z), Is.EqualTo(gear.Tool), "the baton is the active item");
            var dmg = entMan.GetComponent<DamageableComponent>(human).Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}");
            TestContext.Out.WriteLine($"[zomb] baton hits: {string.Join(" ", dmg)}");
            Assert.That(Hits(entMan, human), Is.GreaterThan(0f), "beats the human");
            Assert.That(Contents(entMan, z).Where(entMan.HasComponent<GunComponent>), Is.Empty);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Zombified and operatives leave each other alone (factions, AIs, no hits) even while both go for a human.</summary>
    [Test]
    public async Task NeutralToOperatives()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -12, 12, -12, 12);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;
        var zai = server.System<ShogkkZombifiedAISystem>();
        var opAi = server.System<ShogkkOperativeAISystem>();
        var faction = server.System<NpcFactionSystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid z = default, op = default, human = default;
        await server.WaitPost(() =>
        {
            z = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f));
            Hold(entMan, z); // no wandering off the grid before the human turns up
            op = entMan.SpawnEntity("MobShogkkOperativeAkm", map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            Sturdy(entMan, op);
            var opComp = entMan.GetComponent<ShogkkOperativeAIComponent>(op);
            opComp.PauseUntil = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
            opComp.NextReport = opComp.PauseUntil;
        });
        await RunSeconds(pair, 4);
        await server.WaitAssertion(() =>
        {
            Assert.That(zai.IsVictim(op), Is.False, "an operative is no victim");
            Assert.That(opAi.IsEnemy(z), Is.False, "a zombified is no enemy");
            Assert.That(faction.IsEntityFriendly(z, op) && faction.IsEntityFriendly(op, z), "friendly factions both ways");
            Assert.That(protos.Index<NpcFactionPrototype>("ShogkkZombified").Hostile, Does.Not.Contain((ProtoId<NpcFactionPrototype>) "ShogkkOperative"));
            Assert.That(protos.Index<NpcFactionPrototype>("ShogkkOperative").Hostile, Does.Not.Contain((ProtoId<NpcFactionPrototype>) "ShogkkZombified"));
            Assert.That(ZAI(entMan, z).Target, Is.Null);
            Assert.That(entMan.GetComponent<ShogkkOperativeAIComponent>(op).Target, Is.Null);
            Assert.That(Hits(entMan, z) + Hits(entMan, op), Is.EqualTo(0f));
        });

        // Hitting each other by accident (a stray bullet) starts no fight either way.
        await server.WaitPost(() =>
        {
            var damage = new DamageSpecifier(protos.Index<Content.Shared.Damage.Prototypes.DamageTypePrototype>("Blunt"), FixedPoint2.New(5));
            entMan.System<DamageableSystem>().TryChangeDamage(z, damage, origin: op);
            entMan.System<DamageableSystem>().TryChangeDamage(op, damage, origin: z);
        });
        await RunSeconds(pair, 1);
        await server.WaitAssertion(() =>
        {
            Assert.That(ZAI(entMan, z).Target, Is.Null, "he doesn't turn on the operative");
            Assert.That(entMan.GetComponent<ShogkkOperativeAIComponent>(op).Target, Is.Null, "nor the operative on him");
        });

        // A human: both go for him, the operative never shoots the zombified.
        var zHits = 0f;
        await server.WaitAssertion(() => zHits = Hits(entMan, z));
        await server.WaitPost(() =>
        {
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -3.5f)));
            Sturdy(entMan, human);
        });
        for (var i = 0; i < 8; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitAssertion(() =>
            {
                var c = ZAI(entMan, z);
                var dz = entMan.GetComponent<DamageableComponent>(z).Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}");
                TestContext.Out.WriteLine($"[zomb] t{i}: z {c.State}->{c.Target} at {server.System<TransformSystem>().GetWorldPosition(z)}, dmg {string.Join(" ", dz)}; op -> {entMan.GetComponent<ShogkkOperativeAIComponent>(op).Target} at {server.System<TransformSystem>().GetWorldPosition(op)}, human {server.System<TransformSystem>().GetWorldPosition(human)}");
            });
        }
        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[zomb] with operative: human {Total(entMan, human)}, zombified hits {Hits(entMan, z) - zHits}");
            Assert.That(ZAI(entMan, z).Target, Is.EqualTo(human));
            Assert.That(entMan.GetComponent<ShogkkOperativeAIComponent>(op).Target, Is.EqualTo(human));
            Assert.That(Hits(entMan, z) - zHits, Is.EqualTo(0f), "no friendly fire on the zombified");
            Assert.That(Hits(entMan, human), Is.GreaterThan(0f));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Goes for a human on sight and beats him; Gilbert and the zombified are mutual enemies (no surrender demand).</summary>
    [Test]
    public async Task HostileToHumansAndGilbert()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -12, 12, -12, 12);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;
        var zai = server.System<ShogkkZombifiedAISystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid z = default, human = default;
        await server.WaitPost(() =>
        {
            z = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f));
            Hold(entMan, z);
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -2.5f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var comp = ZAI(entMan, z);
            Assert.That(zai.IsVictim(human));
            Assert.That(comp.Target, Is.EqualTo(human), "goes for the human");
            Assert.That(comp.State, Is.EqualTo(ShogkkZombifiedAIState.Chase));
            Assert.That(Hits(entMan, human), Is.GreaterThan(0f), "and beats him");
            Assert.That(Contents(entMan, z).Where(entMan.HasComponent<GunComponent>), Is.Empty);
            entMan.DeleteEntity(human);
        });
        await RunSeconds(pair, 1);

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert", map.GridCoords.Offset(new Vector2(0.5f, 4.5f)));
            Sturdy(entMan, gilbert);
        });
        await RunSeconds(pair, 4);
        await server.WaitAssertion(() =>
        {
            Assert.That(zai.IsVictim(gilbert), "Gilbert is a victim to them");
            Assert.That(protos.Index<NpcFactionPrototype>("ShogkkZombified").Hostile, Does.Contain((ProtoId<NpcFactionPrototype>) "Gilbert"));
            Assert.That(protos.Index<NpcFactionPrototype>("Gilbert").Hostile, Does.Contain((ProtoId<NpcFactionPrototype>) "ShogkkZombified"));
            var g = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[zomb] gilbert {g.State} -> {g.Target}, zombified -> {ZAI(entMan, z).Target}");
            Assert.That(ZAI(entMan, z).Target, Is.EqualTo(gilbert), "the zombified goes for Gilbert");
            Assert.That(g.Target, Is.EqualTo(z), "Gilbert goes for the zombified");
            Assert.That(g.State, Is.EqualTo(GilbertAIState.Combat), "without a surrender demand");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Alone on a big floor he roams: walks off to random spots well away from where he started.</summary>
    [Test]
    public async Task Wanders()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -20, 20, -20, 20);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;
        var xf = server.System<TransformSystem>();

        EntityUid z = default;
        var start = Vector2.Zero;
        await server.WaitPost(() =>
        {
            z = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f));
            start = xf.GetWorldPosition(z);
        });

        var maxDist = 0f;
        var destinations = new HashSet<EntityCoordinates>();
        for (var i = 0; i < 10; i++)
        {
            await RunSeconds(pair, 2);
            await server.WaitAssertion(() =>
            {
                maxDist = MathF.Max(maxDist, (xf.GetWorldPosition(z) - start).Length());
                if (ZAI(entMan, z).Destination is { } d)
                    destinations.Add(d);
            });
        }

        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[zomb] wandered up to {maxDist:F1} tiles, {destinations.Count} destinations, state {ZAI(entMan, z).State}");
            Assert.That(maxDist, Is.GreaterThan(5f), "walked well away from the spawn");
            Assert.That(destinations, Is.Not.Empty);
            Assert.That(ZAI(entMan, z).State, Is.EqualTo(ShogkkZombifiedAIState.Wander));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Zombified who see each other band into one horde with one leader and keep near him as he roams.</summary>
    [Test]
    public async Task HordeForms()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -20, 20, -20, 20);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;
        var zai = server.System<ShogkkZombifiedAISystem>();
        var xf = server.System<TransformSystem>();

        var zs = new List<EntityUid>();
        EntityUid far = default;
        await server.WaitPost(() =>
        {
            zs.Add(SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f)));
            zs.Add(SpawnZombified(entMan, map, new Vector2(2.5f, 0.5f)));
            zs.Add(SpawnZombified(entMan, map, new Vector2(1.5f, 2.5f)));
            far = SpawnZombified(entMan, map, new Vector2(-19.5f, -19.5f));
            Hold(entMan, far);
        });
        await RunSeconds(pair, 4);
        EntityUid leader = default;
        await server.WaitAssertion(() =>
        {
            var roots = zs.Select(zai.HordeRoot).Distinct().ToList();
            TestContext.Out.WriteLine($"[zomb] horde roots: {string.Join(",", roots)} of {string.Join(",", zs)}");
            Assert.That(roots, Has.Count.EqualTo(1), "one horde");
            leader = roots[0];
            Assert.That(leader, Is.EqualTo(zs.Min()), "led by the lowest uid");
            Assert.That(zai.HordeRoot(far), Is.EqualTo(far), "the far one is not in it");
        });

        // The leader roams; the followers keep up with him.
        await RunSeconds(pair, 15);
        await server.WaitAssertion(() =>
        {
            var lp = xf.GetWorldPosition(leader);
            var dists = zs.Where(z => z != leader).Select(z => (xf.GetWorldPosition(z) - lp).Length()).ToList();
            TestContext.Out.WriteLine($"[zomb] followers at {string.Join(", ", dists.Select(d => d.ToString("F1")))} from the leader");
            Assert.That(zs.Select(zai.HordeRoot).Distinct().Count(), Is.EqualTo(1), "still one horde");
            Assert.That(dists, Has.All.LessThan(8f), "followers stay near the leader");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The one who sees the victim groans: a zombified in earshot (15 tiles) who can't see the victim joins the hunt and
    /// comes; one further away does not hear it.
    /// </summary>
    [Test]
    public async Task GroanCallsTheOthers()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -6, 6, -6, 34);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;
        var xf = server.System<TransformSystem>();

        EntityUid spotter = default, near = default, far = default, human = default;
        await server.WaitPost(() =>
        {
            spotter = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f));
            near = SpawnZombified(entMan, map, new Vector2(0.5f, 12.5f));
            far = SpawnZombified(entMan, map, new Vector2(0.5f, 32.5f));
            foreach (var z in new[] { spotter, near, far })
                Hold(entMan, z);
        });
        await RunSeconds(pair, 1);

        await server.WaitPost(() =>
        {
            // In reach of the spotter only (the near one is ~15 tiles from him, out of his sight).
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -2.5f)));
            Sturdy(entMan, human);
        });
        await server.WaitAssertion(() =>
        {
            var zai = server.System<ShogkkZombifiedAISystem>();
            Assert.That(zai.CanSee(spotter, human), "the spotter sees the victim");
            Assert.That(zai.CanSee(near, human), Is.False, "the near one can't see the victim");
            Assert.That(zai.CanSee(far, human), Is.False);
        });
        await RunSeconds(pair, 1.5);
        var nearStart = 0f;
        await server.WaitAssertion(() =>
        {
            var s = ZAI(entMan, spotter);
            var n = ZAI(entMan, near);
            var f = ZAI(entMan, far);
            TestContext.Out.WriteLine($"[zomb] spotter {s.State}->{s.Target}, near {n.State}->{n.Target}, far {f.State}->{f.Target}");
            Assert.That(s.Target, Is.EqualTo(human));
            Assert.That(s.NextGroan, Is.GreaterThan(TimeSpan.Zero), "the spotter groaned");
            Assert.That(n.Target, Is.EqualTo(human), "the near one heard the groan");
            Assert.That(n.State, Is.AnyOf(ShogkkZombifiedAIState.Hunt, ShogkkZombifiedAIState.Chase));
            Assert.That(f.Target, Is.Null, "the far one heard nothing");
            nearStart = (xf.GetWorldPosition(near) - xf.GetWorldPosition(human)).Length();
        });

        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var d = (xf.GetWorldPosition(near) - xf.GetWorldPosition(human)).Length();
            TestContext.Out.WriteLine($"[zomb] near one: {nearStart:F1} -> {d:F1} tiles from the victim");
            Assert.That(d, Is.LessThan(nearStart - 4f), "and comes to the hunt");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A gun on the floor next to him: never picked up, not even in a fight.</summary>
    [Test]
    public async Task NeverTakesGuns()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -8, 8, -8, 8);
        await RunSeconds(pair, 2);
        var entMan = server.EntMan;

        EntityUid z = default, gun = default, human = default;
        await server.WaitPost(() =>
        {
            z = SpawnZombified(entMan, map, new Vector2(0.5f, 0.5f), "MobShogkkZombifiedSecurity");
            gun = entMan.SpawnEntity("WeaponRifleAk", map.GridCoords.Offset(new Vector2(0.5f, -0.5f)));
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -4.5f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 8);
        await server.WaitAssertion(() =>
        {
            Assert.That(ZAI(entMan, z).Target, Is.EqualTo(human));
            Assert.That(Contents(entMan, z), Does.Not.Contain(gun), "the gun stays on the floor");
            Assert.That(Contents(entMan, z).Where(entMan.HasComponent<GunComponent>), Is.Empty, "no gun on him at all");
            Assert.That(entMan.System<SharedContainerSystem>().IsEntityInContainer(gun), Is.False);
        });

        await pair.CleanReturnAsync();
    }
}

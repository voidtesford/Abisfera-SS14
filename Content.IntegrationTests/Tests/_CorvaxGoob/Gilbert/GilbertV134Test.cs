using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.ProcAnim;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Mobs.Systems;
using Content.Shared.Throwing;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Physics.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v13.4: phase 2 targets only humanoids (not the pAI / DT-2 sword / borgs / pets), the leap victim stays where the
/// scene lands it, the levitation drifts at enemies, and the two new rage finishers (head torn off; heart ripped out,
/// head crushed) picked at random with the tear in half.
/// </summary>
[TestFixture]
public sealed class GilbertV134Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";

    private static async Task FillFloor(TestPair pair, TestMapData map, int radius)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            for (var x = -radius; x <= radius; x++)
            for (var y = -radius; y <= radius; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
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

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static void Quiet(IEntityManager entMan, EntityUid gilbert)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert).NextAny = now + TimeSpan.FromHours(1);
        entMan.GetComponent<GilbertAIComponent>(gilbert).NextRageFinisherTry = now + TimeSpan.FromHours(1);
    }

    /// <summary>Gilbert in the rage phase, abilities kept from starting by themselves. <paramref name="before"/> runs before the burst.</summary>
    private static async Task<EntityUid> SpawnRaging(TestPair pair, TestMapData map, Vector2 at, Action<EntityUid>? before = null)
    {
        var entMan = pair.Server.EntMan;
        var gilbert = EntityUid.Invalid;
        await pair.Server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(at));
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            comp.TransitionDuration = TimeSpan.FromSeconds(1.0);
            comp.BurstAt = TimeSpan.FromSeconds(0.6);
            comp.RageDuration = TimeSpan.FromSeconds(300);
            Quiet(entMan, gilbert);
            before?.Invoke(gilbert);
        });
        await pair.Server.WaitRunTicks(3);
        await pair.Server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.3);
        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            Quiet(entMan, gilbert);
        });
        return gilbert;
    }

    /// <summary>
    /// Item 1: the root cause was "anything with a MobStateComponent is a rage target"; the pAI and the DT-2 sword are
    /// items with a MobState (for their ghost role), borgs and pets are mobs. Only a free-standing humanoid is an enemy,
    /// and the burst spares the items (it may still singe a free-standing pet / borg).
    /// </summary>
    [Test]
    public async Task RageTargetsOnlyHumanoids()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var rage = server.System<GilbertRageAbilitiesSystem>();

        EntityUid human = default, pai = default, sword = default, borg = default, corgi = default;
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f), _ =>
        {
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            Sturdy(entMan, human);
            pai = entMan.SpawnEntity("PersonalAI", map.GridCoords.Offset(new Vector2(1.5f, 1.5f)));
            sword = entMan.SpawnEntity("Justice", map.GridCoords.Offset(new Vector2(1.5f, -0.5f)));
            borg = entMan.SpawnEntity("BorgChassisGeneric", map.GridCoords.Offset(new Vector2(-1.5f, 0.5f)));
            corgi = entMan.SpawnEntity("MobCorgi", map.GridCoords.Offset(new Vector2(0.5f, 2.5f)));
        });

        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[v13.4] burst: human {Total(entMan, human)} pai {Total(entMan, pai)} sword {Total(entMan, sword)} borg {Total(entMan, borg)} corgi {Total(entMan, corgi)}");
            Assert.That(rage.IsRageEnemy(gilbert, human), "a humanoid is an enemy");
            Assert.That(rage.IsRageEnemy(gilbert, pai), Is.False, "pAI");
            Assert.That(rage.IsRageEnemy(gilbert, sword), Is.False, "DT-2 sword");
            Assert.That(rage.IsRageEnemy(gilbert, borg), Is.False, "borg");
            Assert.That(rage.IsRageEnemy(gilbert, corgi), Is.False, "pet");
            Assert.That(rage.IsRageEnemy(gilbert, gilbert), Is.False, "himself");
            Assert.That(rage.IsRageCollateral(gilbert, pai), Is.False, "the pAI is never hurt");
            Assert.That(rage.IsRageCollateral(gilbert, sword), Is.False, "the sword is never hurt");
            Assert.That(Total(entMan, pai), Is.EqualTo(FixedPoint2.Zero), "the burst spared the pAI");
            Assert.That(Total(entMan, sword), Is.EqualTo(FixedPoint2.Zero), "the burst spared the sword");
            Assert.That(Total(entMan, human), Is.GreaterThan(FixedPoint2.Zero), "the burst hit the human");
            // His berserk fists still go for a plain NPC critter (v6), never for the pAI / sword / borg ...
            Assert.That(rage.IsRageCritter(gilbert, corgi), "a stray NPC corgi is fair game for his fists");
            Assert.That(rage.IsRageCritter(gilbert, pai), Is.False, "pAI is not a critter");
            Assert.That(rage.IsRageCritter(gilbert, sword), Is.False, "DT-2 is not a critter");
            Assert.That(rage.IsRageCritter(gilbert, borg), Is.False, "a borg is not a critter");
            // ... nor for a pet offered to / taken by a ghost.
            entMan.AddComponent<GhostTakeoverAvailableComponent>(corgi);
            Assert.That(rage.IsRageCritter(gilbert, corgi), Is.False, "a ghost-role pet is spared");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Item 1: the AI's berserk target and the levitation bolts go for the human, not the pAI next to him.</summary>
    [Test]
    public async Task LevitationDriftsAtTheHumanNotThePai()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var rage = server.System<GilbertRageAbilitiesSystem>();
        var xforms = server.System<SharedTransformSystem>();

        EntityUid human = default, pai = default;
        var gilbert = await SpawnRaging(pair, map, new Vector2(-4.5f, 0.5f));
        await server.WaitPost(() =>
        {
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4.5f, 0.5f)));
            Sturdy(entMan, human);
            pai = entMan.SpawnEntity("PersonalAI", map.GridCoords.Offset(new Vector2(-3.5f, 0.5f)));
        });

        var startDist = 0f;
        FixedPoint2 paiBase = default;
        await server.WaitPost(() =>
        {
            startDist = (xforms.GetWorldPosition(human) - xforms.GetWorldPosition(gilbert)).Length();
            paiBase = Total(entMan, pai);
            Assert.That(rage.TryLevitate(gilbert, ignoreCooldowns: true), "levitation started");
        });
        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            var dist = (xforms.GetWorldPosition(human) - xforms.GetWorldPosition(gilbert)).Length();
            TestContext.Out.WriteLine($"[v13.4] drift: {comp.LevitateDrifted:0.00} dist {startDist:0.00} -> {dist:0.00} target {comp.LevitateDriftTarget}");
            Assert.That(comp.LevitateDrifted, Is.GreaterThan(0.5f), "drifted");
            Assert.That(dist, Is.LessThan(startDist - 0.5f), "closer to the human");
            Assert.That(comp.LevitateDriftTarget, Is.EqualTo(human), "after the human, not the pAI");
            Assert.That(Total(entMan, pai) - paiBase, Is.EqualTo(FixedPoint2.Zero), "no bolts at the pAI");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Item 5: the leap victim stays where the scene lands it - no leftover velocity, no follow-up throw.</summary>
    [Test]
    public async Task LeapVictimLandsWhereTheSceneDoes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var rage = server.System<GilbertRageAbilitiesSystem>();
        var xforms = server.System<SharedTransformSystem>();

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            Sturdy(entMan, victim);
        });
        var gilbert = await SpawnRaging(pair, map, new Vector2(-5.5f, 0.5f));
        await server.WaitPost(() => Assert.That(rage.TryLeap(gilbert, victim, ignoreCooldowns: true), "leap started"));

        var played = false;
        for (var i = 0; i < 40 && !played; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() => played = entMan.HasComponent<ProcAnimComponent>(victim));
        }
        Assert.That(played, "the pounce scene played");

        var ended = false;
        for (var i = 0; i < 100 && !ended; i++)
        {
            await RunSeconds(pair, 0.05);
            await server.WaitPost(() => ended = !entMan.HasComponent<ProcAnimComponent>(victim));
        }

        var landed = Vector2.Zero;
        await server.WaitAssertion(() =>
        {
            Assert.That(ended, "scene over");
            landed = xforms.GetWorldPosition(victim);
            var vel = entMan.GetComponent<PhysicsComponent>(victim).LinearVelocity;
            TestContext.Out.WriteLine($"[v13.4] leap end: at {landed} vel {vel}");
            Assert.That(vel.Length(), Is.LessThan(0.05f), "no leftover velocity");
            Assert.That(entMan.HasComponent<ThrownItemComponent>(victim), Is.False, "not thrown on after the scene");
            Assert.That(entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert).LeapThrows, Is.EqualTo(1));
        });
        await RunSeconds(pair, 1.0);
        await server.WaitAssertion(() =>
        {
            var later = xforms.GetWorldPosition(victim);
            TestContext.Out.WriteLine($"[v13.4] leap +1 s: at {later}");
            Assert.That((later - landed).Length(), Is.LessThan(0.6f), "stays (near) where it landed");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Item 6: variants 5 / 6 and their names, and the random pick covers all three rage finishers.</summary>
    [Test]
    public async Task RageFinisherVariantsAndRandomPick()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protos = server.ResolveDependency<IPrototypeManager>();
        await server.WaitAssertion(() =>
        {
            var finisher = server.System<GilbertFinisherSystem>();
            Assert.That(finisher.ResolveVariant("4"), Is.EqualTo(GilbertFinisherSystem.RageFinisher));
            Assert.That(finisher.ResolveVariant("5"), Is.EqualTo(GilbertFinisherSystem.RageNeckFinisher));
            Assert.That(finisher.ResolveVariant("neck"), Is.EqualTo(GilbertFinisherSystem.RageNeckFinisher));
            Assert.That(finisher.ResolveVariant("6"), Is.EqualTo(GilbertFinisherSystem.RageHeartFinisher));
            Assert.That(finisher.ResolveVariant("heart"), Is.EqualTo(GilbertFinisherSystem.RageHeartFinisher));

            var seen = new HashSet<string>();
            var rage = server.System<GilbertRageAbilitiesSystem>();
            for (var i = 0; i < 200; i++)
                seen.Add(rage.PickRageFinisher());
            Assert.That(seen, Is.EquivalentTo(GilbertFinisherSystem.RageFinishers), "all three picked at random");

            foreach (var id in GilbertFinisherSystem.RageFinishers)
            {
                var proto = protos.Index<ProcAnimScenePrototype>(id);
                Assert.That(proto.Actors.Any(a => a.FinalDamage != null
                                                  && a.FinalDamage.DamageDict.TryGetValue("Asphyxiation", out var asphyx)
                                                  && asphyx == FixedPoint2.New(200)), $"{id}: 200 Asphyxiation");
            }
        });
        await pair.CleanReturnAsync();
    }

    private static async Task<EntityUid> RunRageFinisher(TestPair pair, TestMapData map, string id)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        var rage = server.System<GilbertRageAbilitiesSystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.8f, 0.6f)));
            entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(victim);
        });
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));
        await server.WaitAssertion(() =>
        {
            Assert.That(rage.TryRageFinisher(gilbert, victim, out var error, id), $"started ({error})");
            Assert.That(entMan.GetComponent<ProcAnimComponent>(victim).Scene.Id, Is.EqualTo(id));
        });

        var proto = protos.Index<ProcAnimScenePrototype>(id);
        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) + 1.5);
        return victim;
    }

    /// <summary>Item 6, variant 5: the head is twisted and torn off (thrown loose), the victim dies.</summary>
    [Test]
    public async Task RageNeckTearsTheHeadOff()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var victim = await RunRageFinisher(pair, map, GilbertFinisherSystem.RageNeckFinisher);
        var entMan = pair.Server.EntMan;
        var body = pair.Server.System<SharedBodySystem>();
        await pair.Server.WaitAssertion(() =>
        {
            var parts = body.GetBodyChildren(victim).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            TestContext.Out.WriteLine($"[v13.4] neck: parts {string.Join(",", parts)}");
            Assert.That(entMan.HasComponent<ProcAnimComponent>(victim), Is.False, "scene over");
            Assert.That(pair.Server.System<MobStateSystem>().IsDead(victim), "dead");
            Assert.That(parts, Does.Not.Contain(TargetBodyPart.Head), "head torn off");
            Assert.That(parts, Does.Contain(TargetBodyPart.Chest).And.Contain(TargetBodyPart.Groin), "the body stays whole otherwise");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Item 6, variant 6: the heart is ripped out of the body, the head crushed (gibbed), the victim dies.</summary>
    [Test]
    public async Task RageHeartRipsHeartAndPopsHead()
    {
        await using var pair = await PoolManager.GetServerClient();
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = pair.Server.EntMan;
        var body = pair.Server.System<SharedBodySystem>();

        var hadHeart = false;
        // The heart is checked on a fresh human first (the slot id the scene removes).
        await pair.Server.WaitPost(() =>
        {
            var probe = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-6f, -6f)));
            hadHeart = body.GetBodyOrgans(probe).Any(o => o.Component.SlotId == "heart");
            entMan.DeleteEntity(probe);
        });
        Assert.That(hadHeart, "a human has a heart slot");

        var victim = await RunRageFinisher(pair, map, GilbertFinisherSystem.RageHeartFinisher);
        await pair.Server.WaitAssertion(() =>
        {
            var parts = body.GetBodyChildren(victim).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            var organs = body.GetBodyOrgans(victim).Select(o => o.Component.SlotId).ToList();
            TestContext.Out.WriteLine($"[v13.4] heart: parts {string.Join(",", parts)} organs {string.Join(",", organs)}");
            Assert.That(entMan.HasComponent<ProcAnimComponent>(victim), Is.False, "scene over");
            Assert.That(pair.Server.System<MobStateSystem>().IsDead(victim), "dead");
            Assert.That(organs, Does.Not.Contain("heart"), "heart ripped out");
            Assert.That(parts, Does.Not.Contain(TargetBodyPart.Head), "head crushed");
        });
        await pair.CleanReturnAsync();
    }
    /// <summary>
    /// Items 2 / 4b: an overlay spread over several limbs (his neon rage arms) is cut per limb group, never onto a held
    /// item, so each piece follows its bone (before: one static copy over the torso - a second, unmoving shotgun / pair
    /// of arms). Pixels with no limb under them within 3 px go to the torso.
    /// </summary>
    [Test]
    public void OverlaySplitFollowsTheLimbs()
    {
        const int c = 16;
        var images = new byte[ProcAnimPuppet.GroupCount][];
        for (var g = 0; g < images.Length; g++)
            images[g] = new byte[c * c * 4];
        void Fill(ProcAnimPuppetGroup g, int x0, int x1)
        {
            for (var y = 0; y < c; y++)
            for (var x = x0; x < x1; x++)
                images[(int) g][(y * c + x) * 4 + 3] = 255;
        }
        Fill(ProcAnimPuppetGroup.ArmNear, 0, 4);
        Fill(ProcAnimPuppetGroup.HeldNear, 2, 4); // the gun over the hand: never gets the overlay
        Fill(ProcAnimPuppetGroup.ArmFar, 12, 16);
        var result = new ProcAnimPuppetResult { Size = c, Images = images, Pivots = new() };

        var overlay = new byte[c * c * 4];
        for (var y = 0; y < c; y++)
        for (var x = 0; x < c; x++)
            if (x < 4 || x >= 12 || x == 8)
                overlay[(y * c + x) * 4 + 3] = 200;

        var parts = ProcAnimPuppet.SplitByGroup(result, overlay, c);
        int Count(ProcAnimPuppetGroup g) => parts[(int) g] is { } p ? Enumerable.Range(0, c * c).Count(i => p[i * 4 + 3] > 0) : 0;
        TestContext.Out.WriteLine($"[v13.4] split: near {Count(ProcAnimPuppetGroup.ArmNear)} far {Count(ProcAnimPuppetGroup.ArmFar)} torso {Count(ProcAnimPuppetGroup.Torso)} held {Count(ProcAnimPuppetGroup.HeldNear)}");
        Assert.That(Count(ProcAnimPuppetGroup.ArmNear), Is.EqualTo(4 * c), "near arm piece");
        Assert.That(Count(ProcAnimPuppetGroup.ArmFar), Is.EqualTo(4 * c), "far arm piece");
        Assert.That(Count(ProcAnimPuppetGroup.HeldNear), Is.Zero, "nothing on the held item");
        Assert.That(Count(ProcAnimPuppetGroup.Torso), Is.EqualTo(c), "the stray column goes to the torso");
    }
}

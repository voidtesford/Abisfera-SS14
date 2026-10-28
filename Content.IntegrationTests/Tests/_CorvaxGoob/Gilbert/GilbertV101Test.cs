using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert.Finisher;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Console;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v10.1: finishers on victims already in crit (AI and command), the final asphyxiation damage, the attack watchdog
/// after recovering his gear, the puppet texture colour space.
/// </summary>
[TestFixture]
public sealed class GilbertV101Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";

    private static readonly string[] Variants =
    {
        GilbertFinisherSystem.DefaultFinisher, "GilbertFinisherBarrel", "GilbertFinisherGroundPound",
    };

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
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }

    private static EntityCoordinates At(TestMapData map, float x, float y) => map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f));

    private static void NoAir(IEntityManager entMan, EntityUid uid) =>
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(uid);

    private static void HoldFire(IEntityManager entMan, IGameTiming timing, EntityUid gilbert)
    {
        var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
        comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
        foreach (var held in entMan.System<SharedHandsSystem>().EnumerateHeld(gilbert))
        {
            if (entMan.TryGetComponent<GunComponent>(held, out var gun))
                gun.NextFire = timing.CurTime + TimeSpan.FromHours(1);
        }
    }

    /// <summary>Blunt on the chest until past the crit threshold (by <paramref name="over"/>).</summary>
    private static void Crit(IEntityManager entMan, EntityUid victim, float over = 5f)
    {
        var ai = entMan.System<GilbertAISystem>();
        var dmg = entMan.System<DamageableSystem>();
        for (var i = 0; i < 30 && ai.CritMargin(victim) is { } m && m > -over; i++)
        {
            var spec = new DamageSpecifier();
            spec.DamageDict["Blunt"] = FixedPoint2.New(Math.Min(10f, m.Float() + over));
            dmg.TryChangeDamage(victim, spec, ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
        }
    }

    private static MobState State(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<MobStateComponent>(uid).CurrentState;

    private static FixedPoint2 Asphyxiation(IEntityManager entMan, EntityUid uid) =>
        entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict.GetValueOrDefault("Asphyxiation");

    /// <summary>His shot at 3 tiles put the target into crit: he still runs up and finishes them.</summary>
    [Test]
    public async Task AiFinishesCritVictimAfterShot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var sys = server.System<GilbertFinisherSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            v = entMan.SpawnEntity(Victim, At(map, 3, 0));
            NoAir(entMan, v);
        });
        await RunSeconds(pair, 0.5);

        await server.WaitPost(() =>
        {
            HoldFire(entMan, timing, g);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            ai.ForceCombat(g, v);
            ai.NoteOwnShot((g, comp));
            Assert.That(comp.FinisherShot, Is.Not.Null, "shot at 3 tiles remembered");
            // The pellets land: crit.
            Crit(entMan, v);
            Assert.That(State(entMan, v), Is.EqualTo(MobState.Critical));
            Assert.That(ai.FinisherEligible((g, comp), v, timing.CurTime), "crit is eligible");
        });

        var started = false;
        for (var i = 0; i < 24 && !started; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() => started = sys.IsPlaying(g));
        }

        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[v10.1] crit victim: started={started} variant={entMan.GetComponent<GilbertAIComponent>(g).LastFinisher} state={State(entMan, v)}");
            Assert.That(started, "finisher started on the crit victim");
            Assert.That(State(entMan, v), Is.EqualTo(MobState.Critical), "held in crit, not dead yet");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>No shot at all: someone he fought lies in crit 3 tiles away - he walks up and finishes them.</summary>
    [Test]
    public async Task AiSeeksCritEnemy()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var sys = server.System<GilbertFinisherSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid g = default, v = default, bystander = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            v = entMan.SpawnEntity(Victim, At(map, 3, 0));
            bystander = entMan.SpawnEntity(Victim, At(map, -2, 0));
            NoAir(entMan, v);
            NoAir(entMan, bystander);
            entMan.GetComponent<GilbertAIComponent>(g).FinisherClearRadius = 0f;
        });
        await RunSeconds(pair, 0.5);

        await server.WaitPost(() =>
        {
            HoldFire(entMan, timing, g);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            ai.ForceCombat(g, v);
            ai.NoteEnemySeen(comp, v, timing.CurTime);
            Crit(entMan, v);
            Crit(entMan, bystander); // never fought: left alone
            comp.FinisherShot = null;
        });

        var started = false;
        for (var i = 0; i < 32 && !started; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() => started = sys.IsPlaying(g));
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(started, "walked up to the crit enemy and started a finisher");
            Assert.That(entMan.GetComponent<GilbertFinisherComponent>(g).Partner, Is.EqualTo(v), "on the enemy, not the bystander");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The admin command on a victim in crit: held in crit through the cutscene, then dead with 200 asphyxiation.</summary>
    [Test]
    public async Task CommandOnCritVictim()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var console = server.ResolveDependency<IConsoleHost>();
        var timing = server.ResolveDependency<IGameTiming>();
        var sys = server.System<GilbertFinisherSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<GilbertFinisherPrototype>(Variants[0]);

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            HoldFire(entMan, timing, g);
            v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            NoAir(entMan, v);
            Crit(entMan, v);
            Assert.That(State(entMan, v), Is.EqualTo(MobState.Critical));
        });
        await RunSeconds(pair, 0.3);

        await server.WaitPost(() => console.ExecuteCommand($"gilbertfinisher {entMan.GetNetEntity(g)} {entMan.GetNetEntity(v)} 1"));
        await RunSeconds(pair, 0.1);
        await server.WaitAssertion(() => Assert.That(sys.IsPlaying(g) && sys.IsPlaying(v), "cutscene running on a crit victim"));

        var total = GilbertFinisherTimeline.TotalLength(proto) + proto.Intro;
        await RunSeconds(pair, total * 0.6);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(v), "still playing");
            Assert.That(State(entMan, v), Is.EqualTo(MobState.Critical), "held in crit mid-cutscene");
        });

        await RunSeconds(pair, total * 0.4 + 1.5);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(v), Is.False, "ended");
            Assert.That(Asphyxiation(entMan, v), Is.GreaterThanOrEqualTo(FixedPoint2.New(200)), "final asphyxiation");
            Assert.That(State(entMan, v), Is.EqualTo(MobState.Dead));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The final damage is configured on every variant, and skips resistances (bulletproof vest + helmet on).</summary>
    [Test]
    public async Task FinalDamageIgnoresResistances()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var entMan = server.EntMan;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var sys = server.System<GilbertFinisherSystem>();
        var inv = server.System<Content.Shared.Inventory.InventorySystem>();

        foreach (var id in Variants)
        {
            var proto = protoMan.Index<GilbertFinisherPrototype>(id);
            await server.WaitAssertion(() =>
            {
                Assert.That(proto.FinalDamage?.DamageDict.GetValueOrDefault("Asphyxiation"), Is.EqualTo(FixedPoint2.New(200)), $"{id}: finalDamage");
                var v = entMan.SpawnEntity(Victim, At(map, 1, 0));
                NoAir(entMan, v);
                inv.TryEquip(v, entMan.SpawnEntity("ClothingOuterArmorBulletproof", At(map, 1, 0)), "outerClothing", silent: true, force: true);
                inv.TryEquip(v, entMan.SpawnEntity("ClothingHeadHelmetShogkk", At(map, 1, 0)), "head", silent: true, force: true);
                sys.ApplyFinalDamage(v, null, proto);
                Assert.That(Asphyxiation(entMan, v), Is.GreaterThanOrEqualTo(FixedPoint2.New(200)), $"{id}: full amount");
                entMan.DeleteEntity(v);
            });
        }

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// t292u: shotgun and truncheon on the floor mid-fight; he picks both back up and goes on attacking
    /// (the victim keeps taking damage) instead of standing there.
    /// </summary>
    [TestCase(4f)]
    [TestCase(1f)]
    public async Task AttacksAgainAfterRecoveringGear(float range)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var hands = server.System<SharedHandsSystem>();
        var xformSys = server.System<SharedTransformSystem>();
        var containers = server.System<SharedContainerSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var thresholds = server.System<MobThresholdSystem>();

        EntityUid g = default, v = default, gun = default, baton = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            v = entMan.SpawnEntity(Victim, At(map, range, 0));
            NoAir(entMan, v);
            thresholds.SetMobStateThreshold(v, FixedPoint2.New(100000), MobState.Critical);
            thresholds.SetMobStateThreshold(v, FixedPoint2.New(200000), MobState.Dead);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            comp.FinisherEnabled = false;
        });
        await RunSeconds(pair, 1);

        await server.WaitPost(() =>
        {
            ai.ForceCombat(g, v);
            gun = hands.EnumerateHeld(g).First(e => entMan.HasComponent<GunComponent>(e));
            baton = entMan.EntityQuery<MetaDataComponent>(true)
                .First(m => m.EntityPrototype?.ID is "TruncheonShogkk" && containers.IsEntityInContainer(m.Owner)).Owner;
            Assert.That(hands.TryDrop(g, gun, checkActionBlocker: false), "shotgun dropped");
            if (hands.IsHolding(g, baton))
                hands.TryDrop(g, baton, checkActionBlocker: false);
            else
                containers.TryRemoveFromContainer(baton, true);
            xformSys.SetCoordinates(gun, At(map, 1, 1));
            xformSys.SetCoordinates(baton, At(map, -1, 1));
        });

        var recovered = false;
        for (var i = 0; i < 40 && !recovered; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() => recovered = containers.IsEntityInContainer(gun) && containers.IsEntityInContainer(baton)
                && entMan.GetComponent<GilbertAIComponent>(g).Task == GilbertTask.None);
        }

        FixedPoint2 before = default;
        await server.WaitPost(() => before = entMan.GetComponent<DamageableComponent>(v).TotalDamage);
        var hit = false;
        for (var i = 0; i < 12 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() => hit = entMan.GetComponent<DamageableComponent>(v).TotalDamage > before);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            TestContext.Out.WriteLine($"[v10.1] recovered={recovered} hit={hit} state={comp.State} mode={comp.WeaponMode} task={comp.Task} " +
                                      $"held={string.Join(",", hands.EnumerateHeld(g).Select(e => entMan.ToPrettyString(e)))} watchdog={comp.AttackWatchdogResets}");
            Assert.That(recovered, "both picked back up");
            Assert.That(hit, "attacking again within 6 s");
            var active = hands.GetActiveItem(g);
            Assert.That(active == null || entMan.HasComponent<GunComponent>(active.Value)
                        || entMan.GetComponent<MetaDataComponent>(active.Value).EntityPrototype?.ID == "TruncheonShogkk",
                $"weapon in the active hand, not {entMan.ToPrettyString(active)}");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// t292u watchdog: a fight that stalls (here: pacified, so no shot / swing lands) with the enemy in reach and in
    /// view is reset after ~3 s; once he can fight again he does.
    /// </summary>
    [Test]
    public async Task WatchdogResetsStalledFight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var thresholds = server.System<MobThresholdSystem>();

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            v = entMan.SpawnEntity(Victim, At(map, 3, 0));
            NoAir(entMan, v);
            thresholds.SetMobStateThreshold(v, FixedPoint2.New(100000), MobState.Critical);
            thresholds.SetMobStateThreshold(v, FixedPoint2.New(200000), MobState.Dead);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            comp.FinisherEnabled = false;
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() =>
        {
            entMan.AddComponent<PacifiedComponent>(g);
            ai.ForceCombat(g, v);
        });
        await RunSeconds(pair, 4.5);

        FixedPoint2 before = default;
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            TestContext.Out.WriteLine($"[v10.1] stalled: resets={comp.AttackWatchdogResets} state={comp.State} mode={comp.WeaponMode}");
            Assert.That(comp.AttackWatchdogResets, Is.GreaterThanOrEqualTo(1), "stall detected and reset");
            entMan.RemoveComponent<PacifiedComponent>(g);
            before = entMan.GetComponent<DamageableComponent>(v).TotalDamage;
        });

        var hit = false;
        for (var i = 0; i < 10 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() => hit = entMan.GetComponent<DamageableComponent>(v).TotalDamage > before);
        }

        Assert.That(hit, "fighting again after the reset");
        await pair.CleanReturnAsync();
    }

    /// <summary>Puppet segments are uploaded as sRGB like every other sprite texture (linear made them a grey ghost).</summary>
    [Test]
    public void PuppetTexturesAreSrgb()
    {
        var p = Content.Client._CorvaxGoob.Gilbert.Finisher.GilbertFinisherVisualsSystem.PuppetTextureParameters;
        Assert.That(p.Srgb, "sRGB");
        Assert.That(p.SampleParameters.Filter, Is.False, "pixel art: no filtering");
    }
}

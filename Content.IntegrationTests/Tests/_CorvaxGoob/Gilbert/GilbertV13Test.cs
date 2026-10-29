using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server._CorvaxGoob.ProcAnim;
using Content.Server.Chat.Systems;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Chat;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v13: scenes staged side-on before they start, the rage walks round instead of smashing when there is a sane
/// way round, and the phase-2 abilities (leap, red discharge cone, levitation) and the phase-2 finisher.
/// </summary>
[TestFixture]
public sealed partial class GilbertV13Test
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

    private static FixedPoint2 Dmg(IEntityManager entMan, EntityUid target, string type)
    {
        return entMan.GetComponent<DamageableComponent>(target).Damage.DamageDict.GetValueOrDefault(type);
    }

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static async Task<EntityUid> SpawnRaging(TestPair pair, TestMapData map, Vector2 at, bool abilities = false, bool quiet = false)
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
            // The route tests are about walking vs smashing: no rage abilities muddying them.
            if (!abilities)
                entMan.RemoveComponent<GilbertRageAbilitiesComponent>(gilbert);
            else if (quiet)
                Quiet(entMan, gilbert); // the test starts them itself
        });
        await pair.Server.WaitRunTicks(3);
        await pair.Server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.3);
        await pair.Server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage)));
        return gilbert;
    }

    #region Stage 1: staging

    /// <summary>
    /// A 1-tile north-south corridor that opens into a room 3 tiles up: the actors are moved there first, the scene
    /// plays side-on and the victim ends where it was drawn (on Gilbert's row, not 1-1.5 tiles off).
    /// </summary>
    [Test]
    public async Task StagedSideOnFromVerticalCorridor()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);
        var sEnt = server.EntMan;
        var xformSys = sEnt.System<SharedTransformSystem>();

        EntityUid a = default, v = default;
        var walls = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            for (var y = -6; y <= 2; y++)
            {
                walls.Add(sEnt.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, y + 0.5f))));
                walls.Add(sEnt.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(-0.5f, y + 0.5f))));
            }
            a = GilbertTestUtil.SpawnActive(sEnt, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 1.7f)));
        });
        await RunSeconds(pair, 0.3);

        var origin = Vector2.Zero;
        var forward = Vector2.Zero;
        await server.WaitPost(() =>
        {
            var start = xformSys.GetWorldPosition(a);
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err), Is.True, err);
            var comp = sEnt.GetComponent<ProcAnimComponent>(a);
            origin = comp.Origin;
            forward = comp.Forward;
            Assert.That(MathF.Abs(forward.X), Is.GreaterThan(0.99f), "staged side-on");
            Assert.That((origin - start).Length(), Is.InRange(0.5f, 3.05f), "moved out of the corridor");
            Assert.That(origin.Y, Is.GreaterThan(2.9f), "into the room above");
            Assert.That((xformSys.GetWorldPosition(a) - origin).Length(), Is.LessThan(0.6f), "really moved there");
        });

        float length = 0, intro = 0;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        await server.WaitPost(() =>
        {
            var comp = sEnt.GetComponent<ProcAnimComponent>(a);
            intro = comp.Intro;
            length = protoMan.Index<ProcAnimScenePrototype>(comp.Scene).Length;
        });
        await RunSeconds(pair, intro + length + 1.0);
        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<ProcAnimComponent>(v), Is.False, "scene over");
            var pos = xformSys.GetWorldPosition(v);
            // Where the scene ends it (the kick sends it 1.63 forward, 1.2 up-screen): no snap after the scene.
            var drawn = origin + new Vector2(MathF.Sign(forward.X) * 1.63f, 1.2f);
            Assert.That((pos - drawn).Length(), Is.LessThan(0.6f), $"victim where it was drawn ({pos} vs {drawn})");
            foreach (var wall in walls)
                Assert.That(sEnt.Deleted(wall), Is.False);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Enough side room where they stand: nobody is moved.</summary>
    [Test]
    public async Task NoStagingInOpenRoom()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);
        var sEnt = server.EntMan;
        var xformSys = sEnt.System<SharedTransformSystem>();
        EntityUid a = default, v = default;
        await server.WaitPost(() =>
        {
            a = GilbertTestUtil.SpawnActive(sEnt, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 1.7f)));
        });
        await RunSeconds(pair, 0.3);
        await server.WaitPost(() =>
        {
            var start = xformSys.GetWorldPosition(a);
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err), Is.True, err);
            Assert.That((sEnt.GetComponent<ProcAnimComponent>(a).Origin - start).Length(), Is.LessThan(0.01f));
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Stage 2: rage route choice

    [Test]
    public void SmashRouteDecision()
    {
        var comp = new GilbertAIComponent();
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, null, 12f), Is.True, "no way round");
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, 14f, null), Is.False, "can't smash, walk");
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, 14f, 12f), Is.False, "a short detour");
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, 22f, 12f), Is.False, "under 2x");
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, 30f, 12f), Is.True, "2.5x and 18 tiles more");
        Assert.That(GilbertAISystem.DecideSmashRoute(comp, 9f, 4f), Is.False, "2.25x but only 5 tiles more");
    }

    /// <summary>
    /// A wall between him and his victim with a gap 4 tiles up: he walks round through the gap, the wall is
    /// left alone (not a single smash punch).
    /// </summary>
    [Test]
    public async Task RageWalksRoundShortDetour()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;

        var walls = new List<EntityUid>();
        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            for (var y = -6; y <= 3; y++)
                walls.Add(entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, y + 0.5f))));
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4.5f, 0.5f)));
            Sturdy(entMan, victim);
        });
        var gilbert = await SpawnRaging(pair, map, new Vector2(-1.5f, 0.5f));
        // Behind the wall he can't see them: they shout.
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(victim, "сюда!",
            InGameICChatType.Speak, ChatTransmitRange.Normal));

        var hit = false;
        for (var i = 0; i < 40 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() => hit = Dmg(entMan, victim, "Blunt") > FixedPoint2.Zero);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v13] detour: punches={comp.BerserkSmashPunches} checks={comp.BerserkRouteChecks} walk={comp.BerserkWalkCost} smash={comp.BerserkSmashCost} stucks={comp.BerserkWalkStucks}");
            Assert.That(hit, "got to the victim within 20 s");
            Assert.That(comp.BerserkRouteChecks, Is.GreaterThan(0), "the route was weighed");
            Assert.That(comp.BerserkSmashPunches, Is.EqualTo(0), "walked round, smashed nothing");
            foreach (var wall in walls)
            {
                Assert.That(entMan.Deleted(wall), Is.False);
                Assert.That(entMan.GetComponent<DamageableComponent>(wall).TotalDamage, Is.EqualTo(FixedPoint2.Zero), "wall untouched");
            }
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Walled in on every side: no way round, he smashes in.</summary>
    [Test]
    public async Task RageSmashesSealedRoom()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            for (var x = 2; x <= 6; x++)
            for (var y = -2; y <= 2; y++)
                if (x == 2 || x == 6 || y == -2 || y == 2)
                    entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f)));
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4.5f, 0.5f)));
            Sturdy(entMan, victim);
        });
        var gilbert = await SpawnRaging(pair, map, new Vector2(-1.5f, 0.5f));
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(victim, "помогите!",
            InGameICChatType.Speak, ChatTransmitRange.Normal));

        var hit = false;
        for (var i = 0; i < 60 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() => hit = Dmg(entMan, victim, "Blunt") > FixedPoint2.Zero);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v13] sealed: punches={comp.BerserkSmashPunches} checks={comp.BerserkRouteChecks} walk={comp.BerserkWalkCost} smash={comp.BerserkSmashCost}");
            Assert.That(hit, "smashed in and hit them within 30 s");
            Assert.That(comp.BerserkSmashPunches, Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }

    #endregion
}

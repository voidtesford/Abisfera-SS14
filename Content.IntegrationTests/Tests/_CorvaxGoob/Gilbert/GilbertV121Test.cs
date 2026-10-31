using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.ProcAnim;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Gravity;
using Content.Shared.Hands.Components;
using Content.Shared.Humanoid;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v12.1 (real-client diagnosis): the scene is laid out side-on even in narrow rooms, both actors are always drawn as
/// side-view puppets (no north / south originals left under them), re-created layers are re-baked and the in-hand
/// overlays (crackle) are treated as in-hand layers.
/// </summary>
[TestFixture]
public sealed class GilbertV121Test
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
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }

    /// <summary>Walls on the tile columns at +-<paramref name="wallX"/> (a north-south corridor around tile 0).</summary>
    private static async Task<(EntityUid A, EntityUid V)> Corridor(TestPair pair, TestMapData map, int wallX, Vector2 victimAt)
    {
        var sEnt = pair.Server.EntMan;
        EntityUid a = default, v = default;
        await pair.Server.WaitPost(() =>
        {
            for (var y = -7; y <= 7; y++)
            {
                sEnt.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(wallX + 0.5f, y + 0.5f)));
                sEnt.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(-wallX + 0.5f, y + 0.5f)));
            }
            a = GilbertTestUtil.SpawnActive(sEnt, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 0.5f) + victimAt));
        });
        await RunSeconds(pair, 0.3);
        return (a, v);
    }

    [Test]
    public async Task NarrowRoomStillPlaysSideOn()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);
        var sEnt = server.EntMan;
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(GilbertFinisherSystem.DefaultFinisher);

        // 3 tiles wide: ~1.5 tiles free on each side, well below RequiredRoom, the vertical has plenty.
        var (a, v) = await Corridor(pair, map, 2, new Vector2(0, 1.2f));
        await server.WaitPost(() =>
        {
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err), Is.True, err);
            var comp = sEnt.GetComponent<ProcAnimComponent>(a);
            Assert.That(proto.RequiredRoom, Is.GreaterThan(2f));
            Assert.That(MathF.Abs(comp.Forward.X), Is.GreaterThan(0.99f), "side-on (screen-horizontal), not vertical");
            Assert.That(comp.MaxForward, Is.LessThan(1.5f), "the walk-in is clamped to the room instead");
            Assert.That(sEnt.GetComponent<ProcAnimComponent>(v).Forward, Is.EqualTo(comp.Forward));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task VerticalCorridorDrawnSideOnPuppets()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);
        var sEnt = server.EntMan;
        var cEnt = client.EntMan;

        // 1 tile wide: no room beside Gilbert, so the server has to lay it out vertically.
        var (a, v) = await Corridor(pair, map, 1, new Vector2(0, 1.2f));
        await server.WaitPost(() =>
        {
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err), Is.True, err);
            Assert.That(MathF.Abs(sEnt.GetComponent<ProcAnimComponent>(a).Forward.Y), Is.GreaterThan(0.99f));
        });
        var intro = 0f;
        var origin = Vector2.Zero;
        await server.WaitPost(() =>
        {
            intro = sEnt.GetComponent<ProcAnimComponent>(a).Intro;
            origin = sEnt.GetComponent<ProcAnimComponent>(a).Origin;
        });
        await RunSeconds(pair, intro + 0.5);

        await client.WaitAssertion(() =>
        {
            // The layout pivot must reach the client (a server-only origin drew the actors hundreds of tiles away).
            Assert.That(origin, Is.Not.EqualTo(Vector2.Zero));
            Assert.That(cEnt.GetComponent<ProcAnimComponent>(pair.ToClientUid(a)).Origin, Is.EqualTo(origin));
            var vis = cEnt.System<ProcAnimVisualsSystem>();
            foreach (var uid in new[] { pair.ToClientUid(a), pair.ToClientUid(v) })
            {
                var sprite = cEnt.GetComponent<SpriteComponent>(uid);
                Assert.That(sprite.EnableDirectionOverride, Is.True, "profile lock");
                Assert.That(sprite.DirectionOverride, Is.AnyOf(Direction.East, Direction.West), "drawn side-on");
                Assert.That(vis.GetPuppetLayer(uid, ProcAnimPuppetGroup.Torso), Is.Not.Null, "side-view puppet built");
                Assert.That(vis.GetPuppetHiddenLayers(uid), Is.Not.Empty, "originals hidden");
            }

            // The victim stands up-screen of Gilbert in the world, but is drawn beside him.
            var cv = pair.ToClientUid(v);
            var offset = cEnt.GetComponent<SpriteComponent>(cv).Offset;
            Assert.That(offset.X, Is.GreaterThan(0.4f), "drawn to Gilbert's side");
            Assert.That(offset.Y, Is.LessThan(-0.4f), "pulled down to his row");
            Assert.That(offset.Length(), Is.LessThan(3f), "drawn next to the scene, not far away");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RecreatedLayerIsRebakedAndCrackleHidden()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);
        var sEnt = server.EntMan;
        var cEnt = client.EntMan;

        EntityUid a = default, v = default;
        await server.WaitPost(() =>
        {
            a = GilbertTestUtil.SpawnActive(sEnt, Gilbert, map.GridCoords);
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.2f)));
        });
        await RunSeconds(pair, 0.3);
        await server.WaitPost(() => Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err), Is.True, err));
        var intro = 0f;
        await server.WaitPost(() => intro = sEnt.GetComponent<ProcAnimComponent>(a).Intro);
        await RunSeconds(pair, intro + 0.4);

        var ca = pair.ToClientUid(a);
        SpriteComponent.Layer? fresh = null;
        await client.WaitPost(() =>
        {
            var spriteSys = cEnt.System<SpriteSystem>();
            var vis = cEnt.System<ProcAnimVisualsSystem>();
            var sprite = cEnt.GetComponent<SpriteComponent>(ca);
            var ent = (ca, sprite);

            // Held items are on the back now: every layer the hands drew (crackle overlay included) is hidden.
            var hands = cEnt.GetComponent<HandsComponent>(ca);
            foreach (var key in hands.RevealedLayers.Values.SelectMany(k => k))
                if (spriteSys.LayerMapTryGet(ent, key, out var idx, false))
                    Assert.That(sprite[idx].Visible, Is.False, $"in-hand layer {key} hidden after the holster");

            // A system re-creates an original layer (same state, new layer object) under the puppet.
            Assert.That(spriteSys.LayerMapTryGet(ent, HumanoidVisualLayers.Chest, out var chest, false), Is.True);
            var old = (SpriteComponent.Layer) sprite[chest];
            Assert.That(vis.GetPuppetHiddenLayers(ca), Does.Contain(old));
            var rsi = old.ActualRsi;
            var state = old.State;
            spriteSys.RemoveLayer(ent, chest);
            var added = spriteSys.AddBlankLayer(ent, chest);
            if (rsi != null)
                spriteSys.LayerSetRsi(added, rsi, state);
            spriteSys.LayerMapSet(ent, HumanoidVisualLayers.Chest, chest);
            spriteSys.LayerSetVisible(added, true);
            fresh = added;
        });
        await RunSeconds(pair, 0.2);
        await client.WaitAssertion(() =>
        {
            var vis = cEnt.System<ProcAnimVisualsSystem>();
            Assert.That(fresh!.Visible, Is.False, "re-created layer re-baked into the puppet, not drawn as a static original");
            Assert.That(vis.GetPuppetHiddenLayers(ca), Does.Contain(fresh));
            Assert.That(vis.GetPuppetLayer(ca, ProcAnimPuppetGroup.Torso), Is.Not.Null);
        });

        await pair.CleanReturnAsync();
    }
}

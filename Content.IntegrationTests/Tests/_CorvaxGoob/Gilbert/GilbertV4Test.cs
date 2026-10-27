using System.Numerics;
using System.Reflection;
using Content.Client._CorvaxGoob.Gilbert;
using Content.IntegrationTests.Pair;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Server.Gravity;
using Content.Server.Physics.Controllers;
using Content.Shared.Audio;
using Content.Shared.Gravity;
using Content.Shared.Inventory;
using Content.Shared.Maps;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Tag;
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Client.Graphics;
using Robust.Shared.Maths;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v4: heavy Death Squad footsteps and the proximity screen interference.
/// </summary>
[TestFixture]
public sealed class GilbertV4Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";
    private const string HeavySteps = "FootstepHeavySuit";

    private static async Task FillFloor(TestPair pair, TestMapData map, int radius)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            var tile = map.Tile.Tile;
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
                }
            }

            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    /// <summary>
    /// The heavy stomp is on his body (so it beats his boots and the floor), it is the same collection as the
    /// Death Squad magboots, and the mover really picks it for a step, walking and running.
    /// </summary>
    [Test]
    public async Task HeavyFootstepsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var protoMan = server.ProtoMan;

            // Source of the sound: the Death Squad magboots.
            Assert.That(protoMan.Index("ClothingShoesBootsMagDeathsquad")
                    .TryGetComponent<FootstepModifierComponent>(out var dsBoots, entMan.ComponentFactory),
                "death squad boots have a footstep modifier");
            Assert.That(dsBoots!.FootstepSoundCollection, Is.TypeOf<SoundCollectionSpecifier>());
            Assert.That(((SoundCollectionSpecifier) dsBoots.FootstepSoundCollection!).Collection, Is.EqualTo(HeavySteps));

            var gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            Assert.That(entMan.TryGetComponent<FootstepModifierComponent>(gilbert, out var steps), "body footstep modifier");
            var collection = steps!.FootstepSoundCollection as SoundCollectionSpecifier;
            Assert.That(collection?.Collection, Is.EqualTo(HeavySteps));
            Assert.That(server.System<TagSystem>().HasTag(gilbert, "FootstepSound"), "makes footstep sounds");
            Assert.That(server.System<InventorySystem>().TryGetSlotEntity(gilbert, "shoes", out _), "still wears his boots");

            var mover = server.System<MoverController>();
            var method = typeof(SharedMoverController).GetMethod("TryGetSound", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "footstep resolver");

            var input = entMan.GetComponent<InputMoverComponent>(gilbert);
            var mobMover = entMan.GetComponent<MobMoverComponent>(gilbert);
            var xform = entMan.GetComponent<TransformComponent>(gilbert);

            foreach (var buttons in new[] { MoveButtons.None, MoveButtons.Walk })
            {
                input.HeldMoveButtons = buttons;
                mobMover.LastPosition = xform.Coordinates;
                mobMover.StepSoundDistance = 100f;
                var args = new object?[] { false, gilbert, input, mobMover, xform, null, null };
                var played = (bool) method!.Invoke(mover, args)!;
                Assert.That(played, $"a step sound plays ({buttons}, sprinting={input.Sprinting})");
                Assert.That((args[5] as SoundCollectionSpecifier)?.Collection, Is.EqualTo(HeavySteps),
                    $"the step is the heavy stomp ({buttons})");
            }

            entMan.DeleteEntity(gilbert);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The distance curve and the fade are what the spec asks: nothing at 12+, full at 6-, smooth in between (v5: 12 tiles, full at 6).</summary>
    [Test]
    public void InterferenceCurveTest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GilbertProximitySystem.IntensityAt(0f), Is.EqualTo(1f));
            Assert.That(GilbertProximitySystem.IntensityAt(2f), Is.EqualTo(1f));
            Assert.That(GilbertProximitySystem.IntensityAt(5.5f), Is.EqualTo(1f), "max inside 6 tiles");
            Assert.That(GilbertProximitySystem.IntensityAt(6f), Is.EqualTo(1f), "full at 6 tiles");
            Assert.That(GilbertProximitySystem.IntensityAt(6.5f), Is.LessThan(1f).And.GreaterThan(0.9f), "ramps down past 6");
            Assert.That(GilbertProximitySystem.IntensityAt(9f), Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(GilbertProximitySystem.IntensityAt(11.9f), Is.GreaterThan(0f).And.LessThan(0.01f));
            Assert.That(GilbertProximitySystem.IntensityAt(12f), Is.EqualTo(0f));
            Assert.That(GilbertProximitySystem.IntensityAt(15f), Is.EqualTo(0f));
            Assert.That(GilbertProximitySystem.IntensityAt(float.NaN), Is.EqualTo(0f));
        });

        var last = 1f;
        for (var d = 6f; d <= 12f; d += 0.25f)
        {
            var i = GilbertProximitySystem.IntensityAt(d);
            Assert.That(i, Is.LessThanOrEqualTo(last), $"falls off with distance ({d})");
            last = i;
        }

        // Fade in takes FadeInTime, fade out FadeOutTime, and never overshoots.
        var value = 0f;
        var steps = 0;
        while (value < 1f && steps < 1000)
        {
            value = GilbertInterferenceSystem.Step(value, 1f, 1f / 60f);
            steps++;
        }

        Assert.That(steps / 60f, Is.EqualTo(GilbertInterferenceSystem.FadeInTime).Within(0.05f), "fade in time");
        Assert.That(GilbertInterferenceSystem.Step(0.5f, 0f, 1f / 60f), Is.EqualTo(0.5f - 1f / 60f / GilbertInterferenceSystem.FadeOutTime).Within(1e-5f));
        Assert.That(GilbertInterferenceSystem.Step(0.99f, 1f, 1f), Is.EqualTo(1f), "no overshoot up");
        Assert.That(GilbertInterferenceSystem.Step(0.01f, 0f, 1f), Is.EqualTo(0f), "no overshoot down");
        Assert.That(GilbertInterferenceSystem.FadeInTime, Is.GreaterThan(0.3f));
        Assert.That(GilbertInterferenceSystem.FadeOutTime, Is.GreaterThan(0.3f));
    }

    /// <summary>
    /// Server-side check of who is affected: living people near a living Gilbert, scaled by distance;
    /// not Gilbert himself, not ghosts, not other maps, and nobody once he is dead.
    /// </summary>
    [Test]
    public async Task InterferenceTargetsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var otherMap = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var prox = server.System<GilbertProximitySystem>();
            var origin = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));

            var gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, origin);
            var close = entMan.SpawnEntity(Victim, origin.Offset(new Vector2(5f, 0f)));
            var mid = entMan.SpawnEntity(Victim, origin.Offset(new Vector2(0f, 9f)));
            var near11 = entMan.SpawnEntity(Victim, origin.Offset(new Vector2(11f, 0f)));
            var far = entMan.SpawnEntity(Victim, origin.Offset(new Vector2(-13f, 0f)));
            var ghost = entMan.SpawnEntity("MobObserver", origin.Offset(new Vector2(1f, 0f)));
            var elsewhere = entMan.SpawnEntity(Victim, otherMap.GridCoords);

            Assert.Multiple(() =>
            {
                Assert.That(prox.GetIntensity(close), Is.EqualTo(1f), "within 6 tiles: full");
                Assert.That(prox.GetIntensity(mid), Is.EqualTo(0.5f).Within(0.01f), "9 tiles: half");
                Assert.That(prox.GetIntensity(near11), Is.GreaterThan(0f).And.LessThan(0.2f), "11 tiles: faint");
                Assert.That(prox.GetIntensity(far), Is.EqualTo(0f), "13 tiles: none");
                Assert.That(prox.GetIntensity(ghost), Is.EqualTo(0f), "ghosts are not affected");
                Assert.That(prox.CanBeAffected(ghost), Is.False);
                Assert.That(prox.GetIntensity(gilbert), Is.EqualTo(0f), "Gilbert himself is not affected");
                Assert.That(prox.GetIntensity(elsewhere), Is.EqualTo(0f), "other map");
            });

            // A second Gilbert next to the first is not affected either.
            var twin = GilbertTestUtil.SpawnActive(entMan, Gilbert, origin.Offset(new Vector2(1f, 0f)));
            Assert.That(prox.GetIntensity(twin), Is.EqualTo(0f));
            entMan.DeleteEntity(twin);

            // Dead Gilbert: no interference at all.
            server.System<MobStateSystem>().ChangeMobState(gilbert, MobState.Dead);
            Assert.That(prox.GetIntensity(close), Is.EqualTo(0f), "stops when he dies");
            Assert.That(prox.GetIntensity(mid), Is.EqualTo(0f));

            foreach (var e in new[] { gilbert, close, mid, near11, far, ghost, elsewhere })
                entMan.DeleteEntity(e);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Client side: the overlay and its shader are registered, and the local player's interference is computed from
    /// the client's own copy of Gilbert (no networking needed) and drops to zero when he dies.
    /// </summary>
    [Test]
    public async Task InterferenceClientTest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 9);

        Assert.That(client.ResolveDependency<IOverlayManager>().HasOverlay<GilbertInterferenceOverlay>(), "overlay registered");
        Assert.That(client.ProtoMan.HasIndex<ShaderPrototype>(GilbertInterferenceOverlay.Shader), "shader prototype");

        // The engine's own shader parser accepts the source (headless clients never compile it).
        var res = client.ResolveDependency<IResourceManager>();
        await client.WaitAssertion(() =>
        {
            var code = res.ContentFileReadAllText(new ResPath("/Textures/_CorvaxGoob/Mobs/Gilbert/Shaders/gilbert_interference.swsl"));
            var parser = typeof(ShaderPrototype).Assembly.GetType("Robust.Client.Graphics.ShaderParser");
            var parse = parser?.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, new[] { typeof(string), typeof(IResourceManager) });
            Assert.That(parse, Is.Not.Null, "engine shader parser");
            Assert.DoesNotThrow(() => parse!.Invoke(null, new object[] { code, res }), "shader parses");
        });

        EntityUid sGilbert = default;
        EntityUid sPlayer = default;
        await server.WaitPost(() =>
        {
            var origin = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            sGilbert = GilbertTestUtil.SpawnActive(server.EntMan, Gilbert, origin);
            sPlayer = server.EntMan.SpawnEntity(Victim, origin.Offset(new Vector2(8f, 0f))); // on the 6..12 ramp
            server.PlayerMan.SetAttachedEntity(pair.Player!, sPlayer);
        });

        await pair.RunTicksSync(10);

        var cPlayer = client.EntMan.GetEntity(server.EntMan.GetNetEntity(sPlayer));
        var cGilbert = client.EntMan.GetEntity(server.EntMan.GetNetEntity(sGilbert));
        await client.WaitAssertion(() =>
        {
            Assert.That(client.Session?.AttachedEntity, Is.EqualTo(cPlayer), "client attached to the mob");
            Assert.That(client.EntMan.HasComponent<GilbertComponent>(cGilbert), "client knows he is Gilbert");
            var xform = client.System<SharedTransformSystem>();
            var distance = (xform.GetWorldPosition(cGilbert) - xform.GetWorldPosition(cPlayer)).Length();
            var target = client.System<GilbertProximitySystem>().GetIntensity(cPlayer);
            Assert.That(target, Is.EqualTo(GilbertProximitySystem.IntensityAt(distance)).Within(0.001f));
            Assert.That(target, Is.GreaterThan(0f), $"near Gilbert ({distance:F2} tiles)");
        });

        await server.WaitPost(() => server.System<MobStateSystem>().ChangeMobState(sGilbert, MobState.Dead));
        await pair.RunTicksSync(10);

        await client.WaitAssertion(() =>
        {
            Assert.That(client.System<GilbertProximitySystem>().GetIntensity(cPlayer), Is.EqualTo(0f), "gone once he is dead");
        });

        await server.WaitPost(() =>
        {
            server.EntMan.DeleteEntity(sPlayer);
            server.EntMan.DeleteEntity(sGilbert);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Brighter eyes: flickering glow and a soft halo, both unshaded and directional, drawn over the helmet and
    /// gas mask, with a red eye light in front of his face; all of it goes out on death.
    /// </summary>
    [Test]
    public async Task GlowingEyesTest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();

        // The sprite sheet: a flickering glow and a pulsing halo, 4 directions each.
        var res = client.ResolveDependency<IResourceManager>();
        var meta = System.Text.Json.JsonDocument.Parse(
            res.ContentFileReadAllText(new ResPath("/Textures/_CorvaxGoob/Mobs/Gilbert/eyes.rsi/meta.json")));
        foreach (var state in meta.RootElement.GetProperty("states").EnumerateArray())
        {
            Assert.That(state.GetProperty("directions").GetInt32(), Is.EqualTo(4), "directional");
            Assert.That(state.GetProperty("delays")[0].GetArrayLength(), Is.GreaterThan(1), "animated");
        }

        EntityUid sGilbert = default;
        await server.WaitPost(() => sGilbert = GilbertTestUtil.SpawnActive(server.EntMan, Gilbert, map.GridCoords));
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            var light = server.EntMan.GetComponent<Robust.Server.GameObjects.PointLightComponent>(sGilbert);
            Assert.That(light.Enabled, "eye light on");
            Assert.That(light.Color.R, Is.GreaterThan(0.9f));
            Assert.That(light.Color.G, Is.LessThan(0.2f));
            Assert.That(light.Offset.Y, Is.LessThan(0f), "in front of his face");
            Assert.That(light.Radius, Is.GreaterThanOrEqualTo(2f));
        });

        var cGilbert = client.EntMan.GetEntity(server.EntMan.GetNetEntity(sGilbert));
        await client.WaitAssertion(() =>
        {
            var spriteSys = client.System<Robust.Client.GameObjects.SpriteSystem>();
            var sprite = client.EntMan.GetComponent<Robust.Client.GameObjects.SpriteComponent>(cGilbert);
            Entity<Robust.Client.GameObjects.SpriteComponent?> ent = (cGilbert, sprite);
            Assert.That(spriteSys.LayerMapTryGet(ent, GilbertVisualLayers.Eyes, out var eyes, false), "eyes layer");
            Assert.That(spriteSys.LayerMapTryGet(ent, GilbertVisualLayers.EyesHalo, out var halo, false), "halo layer");
            Assert.That(eyes, Is.GreaterThan(halo), "eyes over the halo");
            foreach (var key in new[] { "mask", "head" })
            {
                if (spriteSys.LayerMapTryGet(ent, key, out var slot, false))
                    Assert.That(halo, Is.GreaterThan(slot), $"drawn over the {key} slot");
            }

            Assert.That(spriteSys.TryGetLayer(ent, GilbertVisualLayers.Eyes, out var eyesLayer, false));
            Assert.That(spriteSys.TryGetLayer(ent, GilbertVisualLayers.EyesHalo, out var haloLayer, false));
            Assert.That(eyesLayer!.ShaderPrototype?.Id, Is.EqualTo("unshaded"));
            Assert.That(haloLayer!.ShaderPrototype?.Id, Is.EqualTo("unshaded"));
            Assert.That(eyesLayer.State.Name, Is.EqualTo("glow"));
            Assert.That(haloLayer.State.Name, Is.EqualTo("halo"));
            Assert.That(eyesLayer.Visible && haloLayer.Visible, "glowing while alive");
        });

        await server.WaitPost(() => server.System<MobStateSystem>().ChangeMobState(sGilbert, MobState.Dead));
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(server.EntMan.GetComponent<Robust.Server.GameObjects.PointLightComponent>(sGilbert).Enabled, Is.False, "light out");
        });
        await client.WaitAssertion(() =>
        {
            var spriteSys = client.System<Robust.Client.GameObjects.SpriteSystem>();
            Entity<Robust.Client.GameObjects.SpriteComponent?> ent = (cGilbert, null);
            Assert.That(spriteSys.TryGetLayer(ent, GilbertVisualLayers.Eyes, out var eyesLayer, false));
            Assert.That(spriteSys.TryGetLayer(ent, GilbertVisualLayers.EyesHalo, out var haloLayer, false));
            Assert.That(eyesLayer!.Visible, Is.False, "eyes out on death");
            Assert.That(haloLayer!.Visible, Is.False, "halo out on death");
        });

        await server.WaitPost(() => server.EntMan.DeleteEntity(sGilbert));
        await pair.CleanReturnAsync();
    }
}

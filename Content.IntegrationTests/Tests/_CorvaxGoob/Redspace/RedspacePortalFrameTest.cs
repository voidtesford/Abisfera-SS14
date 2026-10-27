using System.Numerics;
using Content.Server._CorvaxGoob.Redspace;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

/// <summary>
/// The open Euclidean portal (both the exit portal and the gate's ring) has a constant looping crackle around its frame.
/// </summary>
[TestFixture]
[TestOf(typeof(RedspaceGateSystem))]
public sealed class RedspacePortalFrameTest
{
    private const string Rsi = "/Textures/_CorvaxGoob/Effects/redspace_crackle_large.rsi";

    [Test]
    public async Task FrameCrackleLoops()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var xform = entMan.System<SharedTransformSystem>();

        // The frame states are proper loops: 12 evenly timed frames.
        var resCache = client.ResolveDependency<IResourceCache>();
        await client.WaitAssertion(() =>
        {
            var rsi = resCache.GetResource<RSIResource>(new ResPath(Rsi)).RSI;
            foreach (var name in new[] { "portal_frame", "gate_frame" })
            {
                Assert.That(rsi.TryGetState(name, out var state), Is.True, name);
                Assert.That(state!.DelayCount, Is.EqualTo(12), name);
                Assert.That(state.GetDelays(), Is.All.EqualTo(0.08f).Within(1e-4f), name);
            }
        });

        EntityUid gate = default;
        RedspaceGateComponent comp = default!;
        await server.WaitAssertion(() =>
        {
            gate = entMan.SpawnEntity("RedspaceGate", map.GridCoords);
            comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            var red = entMan.SpawnEntity("MaterialRedspaceCrystal1", map.GridCoords);
            stack.SetCount(red, 4);
            sys.TryInsertFuel((gate, comp), red, null);
        });
        await pair.RunTicksSync(5);

        // Closed gate: no ring crackle.
        await client.WaitAssertion(() =>
            Assert.That(FrameLayer(cEntMan, cEntMan.GetEntity(entMan.GetNetEntity(gate)), "gate_frame").Visible, Is.False,
                "closed gate has no ring crackle"));

        EntityUid portal = default;
        await server.WaitAssertion(() =>
        {
            var target = xform.GetMapCoordinates(gate).Position + new Vector2(6f, 0f);
            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out var fail), Is.True, fail);
            portal = comp.Portal!.Value;
        });
        await pair.RunTicksSync(5);

        // Both ends crackle continuously (visible all the time, auto-animated, unshaded), between periodic bursts too.
        for (var i = 0; i < 3; i++)
        {
            await client.WaitAssertion(() =>
            {
                foreach (var (uid, state) in new[] { (portal, "portal_frame"), (gate, "gate_frame") })
                {
                    var layer = FrameLayer(cEntMan, cEntMan.GetEntity(entMan.GetNetEntity(uid)), state);
                    Assert.That(layer.Visible, Is.True, $"{state} visible");
                    Assert.That(layer.AutoAnimated, Is.True, $"{state} animates");
                    Assert.That(layer.ShaderPrototype?.Id, Is.EqualTo("unshaded"), $"{state} unshaded");
                }
            });
            await pair.RunSeconds(1.7f);
        }

        await server.WaitAssertion(() => sys.ClosePortal((gate, comp)));
        await pair.RunTicksSync(5);
        await client.WaitAssertion(() =>
            Assert.That(FrameLayer(cEntMan, cEntMan.GetEntity(entMan.GetNetEntity(gate)), "gate_frame").Visible, Is.False,
                "ring crackle stops when the portal closes"));

        await pair.CleanReturnAsync();
    }

    private static SpriteComponent.Layer FrameLayer(IEntityManager entMan, EntityUid uid, string state)
    {
        var sprite = entMan.GetComponent<SpriteComponent>(uid);
        Assert.That(entMan.System<SpriteSystem>().LayerMapTryGet((uid, sprite), RedspaceGateVisualLayers.PortalFrame, out var index, false),
            Is.True, $"{entMan.ToPrettyString(uid)} has the frame layer");
        var layer = sprite[index];
        Assert.That(layer.RsiState.Name, Is.EqualTo(state));
        return (SpriteComponent.Layer) layer;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.ProcAnim;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Gravity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v13.4.1: the "Barrel" finisher holds the wielded shotgun level in the hands (the in-game wielded M8 frame, not the
/// diagonal inhand one) and drives the muzzle into the victim's mouth; the shot fires there.
/// </summary>
[TestFixture]
public sealed class GilbertV1341Test
{
    // Wielded M8 frame (east, centred, y-up px): muzzle / butt, as drawn in game.
    private static readonly Vector2 Muzzle = new(15f, 0.5f);
    private static readonly Vector2 Butt = new(-7.5f, -1f);
    // The victim's mouth on the side head sprite (Head, rest pose).
    private static readonly Vector2 Mouth = new(-3.5f, 7.5f);

    private const string Scene = "GilbertFinisherBarrel";

    private static Vector2 World(ProcAnimScenePrototype proto, int actor, float t, ProcAnimBone part, Vector2 at,
        IReadOnlyDictionary<ProcAnimBone, Vector2>?[] pivots)
    {
        var move = ProcAnimTimeline.Evaluate(ProcAnimTimeline.Tracks(proto, actor).Move, t);
        var pose = ProcAnimTimeline.Solve(proto, actor, t, pivots[actor], true, i => pivots[i]);
        return new Vector2(move.X, move.Y) * 32f + ProcAnimTimeline.Point(pose, part, at);
    }

    /// <summary>The client's mask-derived joints (forward frame; the scene runs east, so no mirroring).</summary>
    private static Dictionary<ProcAnimBone, Vector2>? Pivots(ProcAnimPuppetResult? bake)
    {
        if (bake == null)
            return null;
        var res = new Dictionary<ProcAnimBone, Vector2>();
        foreach (var (group, p) in bake.Pivots)
            if (Enum.TryParse<ProcAnimBone>(group.ToString(), out var bone))
                res[bone] = p;
        return res;
    }

    [Test]
    public async Task BarrelMuzzleGoesIntoTheMouth()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            for (var x = -6; x <= 6; x++)
            for (var y = -6; y <= 6; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            var gravity = server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Scene);
        var rate = server.ResolveDependency<IGameTiming>().TickRate;

        EntityUid attacker = default, victim = default;
        await server.WaitPost(() =>
        {
            attacker = GilbertTestUtil.SpawnActive(server.EntMan, "MobGilbert", map.GridCoords);
            victim = server.EntMan.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1.3f, 0.2f)));
        });
        await pair.RunTicksSync((int) (0.3f * rate));
        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(attacker, victim, out var err, Scene), $"started ({err})");
        });
        await pair.RunTicksSync((int) ((proto.Intro + 0.6f) * rate));

        // Solve with the joints the client measured on the baked sprites, exactly like the in-game puppet.
        var pivots = new IReadOnlyDictionary<ProcAnimBone, Vector2>?[2];
        await client.WaitAssertion(() =>
        {
            var vis = client.EntMan.System<ProcAnimVisualsSystem>();
            pivots[0] = Pivots(vis.GetPuppetResult(pair.ToClientUid(attacker)));
            pivots[1] = Pivots(vis.GetPuppetResult(pair.ToClientUid(victim)));
        });
        Assert.That(pivots[0], Is.Not.Null, "attacker puppeted");
        Assert.That(pivots[1], Is.Not.Null, "victim puppeted");
        TestContext.Out.WriteLine("[v13.4.1] attacker pivots " + string.Join(" ", pivots[0]!.Select(kv => $"{kv.Key}={kv.Value}")));
        TestContext.Out.WriteLine("[v13.4.1] victim pivots " + string.Join(" ", pivots[1]!.Select(kv => $"{kv.Key}={kv.Value}")));

        foreach (var t in new[] { 3.05f, 3.2f, 3.4f, 3.6f })
        {
            var muzzle = World(proto, 0, t, ProcAnimBone.HeldNear, Muzzle, pivots);
            var butt = World(proto, 0, t, ProcAnimBone.HeldNear, Butt, pivots);
            var mouth = World(proto, 1, t, ProcAnimBone.Head, Mouth, pivots);
            var axis = MathF.Atan2(muzzle.Y - butt.Y, muzzle.X - butt.X) * 180f / MathF.PI;
            TestContext.Out.WriteLine($"[v13.4.1] t={t} muzzle {muzzle} mouth {mouth} dist {(muzzle - mouth).Length():0.0} axis {axis:0}");
            Assert.That((muzzle - mouth).Length(), Is.LessThan(4f), $"muzzle in the mouth at {t}");
            Assert.That(axis, Is.InRange(-110f, -55f), $"barrel points down into the face at {t}");
            Assert.That(butt.Y, Is.GreaterThan(mouth.Y + 12f), "the stock is up in the hands, not lying on the victim");
        }

        // The shot (sparks) comes out of the muzzle, the blood out of the mouth.
        var shot = proto.Events.Where(e => MathF.Abs(e.Time - 3.4f) < 0.001f).ToList();
        Assert.That(shot, Is.Not.Empty);
        Assert.That(shot.Any(e => e.Blood is { From: ProcAnimBone.HeldNear } b && (b.At - Muzzle).Length() < 0.01f), "muzzle flash at the muzzle");
        Assert.That(shot.Any(e => e.Blood is { From: ProcAnimBone.Head } b && (b.At - Mouth).Length() < 0.01f), "blood from the mouth");

        await pair.CleanReturnAsync();
    }
}

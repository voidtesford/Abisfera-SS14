using System.Numerics;
using Content.Client._CorvaxGoob.ProcAnim;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Rotation;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v13.3: a finisher on a downed / crit victim. The victim's sprite was lying (RotationVisuals: 90 / 270 degrees) when
/// the scene started and the client added that angle to every frame, so the whole victim puppet was drawn on its side
/// next to an upright Gilbert, then snapped to the corpse at the end. Mid-scene both actors are upright side views;
/// the corpse lies at the end.
/// </summary>
[TestFixture]
public sealed class GilbertV133Test
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

    private static double Deg(Angle a) => a.Reduced().Degrees;

    /// <summary>Smallest absolute difference of two angles, degrees.</summary>
    private static double Diff(Angle a, Angle b)
    {
        var d = Math.Abs((a - b).Reduced().Degrees) % 360;
        return d > 180 ? 360 - d : d;
    }

    [Test]
    public void PureRotationHelpers()
    {
        Assert.That(ProcAnimVisualsSystem.SceneRotation(0f), Is.EqualTo(Angle.Zero), "upright base, no lying angle added");
        // A tilt wound past a full turn picks the side it shows on screen.
        Assert.That(Diff(ProcAnimVisualsSystem.LyingRotation(-270.4f), Angle.FromDegrees(90)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.LyingRotation(280f), Angle.FromDegrees(270)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.LyingRotation(450f), Angle.FromDegrees(90)), Is.LessThan(1e-3));
        // The settle eases the short way round from the last tilt onto the lying side.
        Assert.That(Diff(ProcAnimVisualsSystem.SettleRotation(Angle.FromDegrees(-80), Angle.FromDegrees(270), 0f), Angle.FromDegrees(-80)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.SettleRotation(Angle.FromDegrees(-80), Angle.FromDegrees(270), 0.5f), Angle.FromDegrees(-85)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.SettleRotation(Angle.FromDegrees(-80), Angle.FromDegrees(270), 1f), Angle.FromDegrees(270)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.SettleRotation(Angle.FromDegrees(10), Angle.FromDegrees(90), 2f), Angle.FromDegrees(90)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.SceneRotation(30f), Angle.FromDegrees(30)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.LyingRotation(80f), Angle.FromDegrees(90)), Is.LessThan(1e-3));
        Assert.That(Diff(ProcAnimVisualsSystem.LyingRotation(-80f), Angle.FromDegrees(270)), Is.LessThan(1e-3));
        var captured = Angle.FromDegrees(37); // a frame of the stand-up tween
        Assert.That(ProcAnimVisualsSystem.RestRotation(RotationState.Horizontal, Angle.Zero, Angle.FromDegrees(90), captured),
            Is.EqualTo(Angle.FromDegrees(90)));
        Assert.That(ProcAnimVisualsSystem.RestRotation(RotationState.Vertical, Angle.Zero, Angle.FromDegrees(90), captured),
            Is.EqualTo(Angle.Zero));
        Assert.That(ProcAnimVisualsSystem.RestRotation(null, Angle.Zero, Angle.FromDegrees(90), captured), Is.EqualTo(captured));
    }

    [TestCase("GilbertFinisherPhase1")]
    [TestCase("GilbertFinisherBarrel")]
    [TestCase("GilbertFinisherGroundPound")]
    [TestCase("GilbertFinisherRage")]
    public async Task CritVictimDrawnUprightThenLies(string scene)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var sEnt = server.EntMan;
        var cEnt = client.EntMan;
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(scene);

        EntityUid a = default, v = default;
        await server.WaitPost(() =>
        {
            a = GilbertTestUtil.SpawnActive(sEnt, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.7f, 0.5f)));
            sEnt.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(v);
        });
        await RunSeconds(pair, 0.3);
        await server.WaitPost(() =>
        {
            Crit(sEnt, v);
            Assert.That(sEnt.GetComponent<MobStateComponent>(v).CurrentState, Is.EqualTo(MobState.Critical));
        });
        await RunSeconds(pair, 1.0);

        // The precondition the bug needs: the victim is drawn lying on the client.
        await client.WaitAssertion(() =>
        {
            var rot = cEnt.GetComponent<SpriteComponent>(pair.ToClientUid(v)).Rotation;
            TestContext.Out.WriteLine($"[v13.3] {scene}: crit victim sprite rotation before = {Deg(rot):F1}");
            Assert.That(Math.Min(Diff(rot, Angle.FromDegrees(90)), Diff(rot, Angle.FromDegrees(270))), Is.LessThan(1),
                "crit victim lies before the finisher");
        });

        var lead = 0f;
        await server.WaitPost(() =>
        {
            Assert.That(server.System<GilbertFinisherSystem>().TryStart(a, v, out var err, scene), Is.True, err);
            var comp = sEnt.GetComponent<ProcAnimComponent>(a);
            lead = comp.Intro + comp.PreBeat;
        });

        // Sample the whole scene: every frame the victim is drawn upright + its scene tilt, like Gilbert.
        var length = ProcAnimTimeline.TotalLength(proto);
        var samples = 0;
        float? victimTilt = null;
        for (var t = 0.4; t < lead + length - 0.3; t += 0.5)
        {
            await RunSeconds(pair, 0.5);
            await client.WaitAssertion(() =>
            {
                var vis = cEnt.System<ProcAnimVisualsSystem>();
                foreach (var uid in new[] { pair.ToClientUid(a), pair.ToClientUid(v) })
                {
                    if (!vis.IsAnimating(uid) || vis.GetLastBodyRot(uid) is not { } body)
                        continue;
                    var rot = cEnt.GetComponent<SpriteComponent>(uid).Rotation;
                    Assert.That(Diff(rot, Angle.FromDegrees(body)), Is.LessThan(0.5),
                        $"{scene} t={t:F1}: {(uid == pair.ToClientUid(v) ? "victim" : "Gilbert")} sprite {Deg(rot):F1} != scene tilt {body:F1} (lying angle added)");
                    if (cEnt.TryGetComponent<AnimationPlayerComponent>(uid, out var player))
                        Assert.That(cEnt.System<AnimationPlayerSystem>().HasRunningAnimation(player, "rotate"), Is.False,
                            "no lie-down / stand-up tween over the scene");
                    samples++;
                    if (uid == pair.ToClientUid(v))
                        victimTilt = body;
                }
            });
        }
        Assert.That(samples, Is.GreaterThan(4), "the victim was sampled mid-scene");

        // The scene end and the settle after it, frame by frame: no jump (the lie-down tween or the laying-down
        // system's facing-based side pick used to flip the corpse right after the scene).
        Angle? prev = null;
        var maxStep = 0.0;
        var settled = false;
        for (var i = 0; i < 60; i++)
        {
            await pair.RunTicksSync(3);
            var done = false;
            await client.WaitAssertion(() =>
            {
                var vis = cEnt.System<ProcAnimVisualsSystem>();
                var cv = pair.ToClientUid(v);
                if (!cEnt.EntityExists(cv) || !cEnt.TryGetComponent<SpriteComponent>(cv, out var sprite))
                {
                    done = true;
                    return;
                }
                if (vis.GetLastBodyRot(cv) is { } body)
                    victimTilt = body;
                var rot = sprite.Rotation;
                if (prev is { } p)
                    maxStep = Math.Max(maxStep, Diff(rot, p));
                prev = rot;
                if (vis.IsSettling(cv))
                    settled = true;
                if (!vis.IsAnimating(cv) && !vis.IsSettling(cv) && settled)
                    done = true;
            });
            if (done)
                break;
        }
        TestContext.Out.WriteLine($"[v13.3] {scene}: largest per-3-tick rotation step around the end = {maxStep:F1}");
        Assert.That(maxStep, Is.LessThan(60), $"{scene}: rotation jump at the end of the scene");

        await RunSeconds(pair, 1.0);
        await server.WaitAssertion(() => Assert.That(sEnt.HasComponent<ProcAnimComponent>(v), Is.False, "scene over"));
        await client.WaitAssertion(() =>
        {
            var cv = pair.ToClientUid(v);
            Assert.That(cEnt.System<ProcAnimVisualsSystem>().IsAnimating(cv), Is.False);
            if (!cEnt.EntityExists(cv) || !cEnt.TryGetComponent<SpriteComponent>(cv, out var sprite))
                return; // torn apart (rage) - the remains are separate entities
            var rot = sprite.Rotation;
            TestContext.Out.WriteLine($"[v13.3] {scene}: corpse sprite rotation after = {Deg(rot):F1} (raw {rot.Degrees:F1}), last victim tilt = {victimTilt:F1}");
            Assert.That(Math.Min(Diff(rot, Angle.FromDegrees(90)), Diff(rot, Angle.FromDegrees(270))), Is.LessThan(1),
                "the corpse lies at the end");
            Assert.That(rot.Degrees, Is.InRange(-1, 361), "the lying angle is left unwound (no full spin on the next tween)");
            // No flip after the settle: the corpse still lies on the side the puppet was last tilted to.
            if (victimTilt is { } tilt && Diff(Angle.FromDegrees(tilt), Angle.Zero) > 45)
                Assert.That(Diff(rot, Angle.FromDegrees(tilt)), Is.LessThan(50),
                    $"corpse {Deg(rot):F1} vs last scene tilt {tilt:F1}: rotation jump at the end");
        });

        await pair.CleanReturnAsync();
    }
}

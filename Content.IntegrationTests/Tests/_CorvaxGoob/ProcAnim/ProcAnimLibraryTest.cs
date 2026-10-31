using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Vector3 = System.Numerics.Vector3;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.ProcAnim;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProcAnim;

/// <summary>Records the ProcAnim server events for <see cref="ProcAnimLibraryTest"/>.</summary>
public sealed class ProcAnimTestRecorderSystem : EntitySystem
{
    public readonly List<(EntityUid Target, int Index, float Time, string? Beat)> Fired = new();
    public readonly List<(EntityUid Uid, int Actor, bool Completed)> Ended = new();

    public override void Initialize()
    {
        SubscribeLocalEvent<MetaDataComponent, ProcAnimEventFiredEvent>(OnFired);
        SubscribeLocalEvent<MetaDataComponent, ProcAnimEndedEvent>(OnEnded);
    }

    private void OnFired(EntityUid uid, MetaDataComponent _, ref ProcAnimEventFiredEvent ev)
    {
        Fired.Add((uid, ev.Index, ev.Event.Time, ev.Event.Beat));
    }

    private void OnEnded(EntityUid uid, MetaDataComponent _, ref ProcAnimEndedEvent ev)
    {
        Ended.Add((uid, ev.Actor, ev.Completed));
    }
}

/// <summary>
/// v11 ProcAnim library: easing / timeline evaluation, 2-bone IK and joint limits, prop attachment, YAML validation,
/// server event order, stop and cleanup - on small test scenes independent of Gilbert.
/// </summary>
[TestFixture]
public sealed class ProcAnimLibraryTest
{
    private const string Punch = "ProcAnimTestPunch";
    private const string Bad = "ProcAnimTestBadScene";

    [TestPrototypes]
    private const string Prototypes = @"
- type: procAnimScene
  id: ProcAnimTestPunch
  length: 1.2
  requiredRoom: 0
  actors:
  - role: puncher
    facing: 1
    move: [{t: 0, x: 0}, {t: 1.2, x: 0}]
    parts:
      Body: {pivot: '0, 0', keys: [{t: 0, rot: 0}]}
      ArmNear: {pivot: '0, 6', keys: [{t: 0, rot: 0}]}
      ArmNearLow: {pivot: '0, 0', keys: [{t: 0, rot: 0}]}
      ArmNearHand: {pivot: '0, -6', keys: [{t: 0, rot: 0}]}
      ArmFar: {pivot: '0, 6', keys: [{t: 0, rot: 0}]}
      ArmFarLow: {pivot: '0, 0', keys: [{t: 0, rot: 0}, {t: 1, rot: 120}]}
      LegNear: {pivot: '0, -6', keys: [{t: 0, rot: 0}]}
      LegNearLow: {pivot: '0, -12', keys: [{t: 0, rot: 40}]}
    joints: {elbow: '-5, 90'}
    ik:
    - limb: ArmNear
      keys: [{t: 0, x: 8, y: 2}]
      weights: [{t: 0, x: 0}, {t: 0.4, x: 1, ease: Linear}, {t: 0.8, x: 1}, {t: 1, x: 0, ease: Linear}]
    props:
    - {sprite: Objects/Weapons/Melee/kitchen_knife.rsi, start: 0.2, end: 0.9, part: ArmNearHand,
       keys: [{t: 0.2, x: 2, y: -1, rot: 90}]}
    expressions:
    - {t: 0, face: neutral}
    - {t: 0.5, face: angry, eyes: narrowed}
  - role: target
    facing: -1
    move: [{t: 0, x: 0.6}, {t: 0.6, x: 0.6}, {t: 0.9, x: 0.8, ease: Out}]
    parts:
      Head: {pivot: '0, 9', keys: [{t: 0, rot: 0}]}
  events:
  - {t: 0.1, target: puncher, beat: wind-up, sound: {path: /Audio/Effects/thudswoosh.ogg}}
  - {t: 0.5, beat: punch, hitStop: 0.05, damage: [{part: Chest, damage: {types: {Blunt: 5}}}]}
  - {t: 0.7, beat: popup, popup: {text: cmd-procanim-stopped}}
  - {t: 1.1, target: puncher, beat: done}

- type: procAnimScene
  id: ProcAnimTestBadScene
  length: 1
  actors:
  - role: a
    move: [{t: 0.5, x: 0}, {t: 0.2, x: 1}]
    ik:
    - limb: Head
    - limb: ArmFar
      relativeTo: nobody.Head
  - role: A
    facing: 2
  events:
  - {t: 0.6, target: ghost}
  - {t: 0.3}
";

    private static ProcAnimKey K(float t, float x, ProcAnimEase ease = ProcAnimEase.InOut) =>
        new() { Time = t, X = x, Ease = ease };

    [Test]
    public void EasingEndpointsAndShapes()
    {
        Assert.Multiple(() =>
        {
            foreach (var ease in Enum.GetValues<ProcAnimEase>())
            {
                Assert.That(ProcAnimTimeline.Ease(ease, 0f), Is.EqualTo(ease == ProcAnimEase.Step ? 0f : 0f).Within(1e-4f), $"{ease}(0)");
                Assert.That(ProcAnimTimeline.Ease(ease, 1f), Is.EqualTo(1f).Within(1e-4f), $"{ease}(1)");
                Assert.That(ProcAnimTimeline.Ease(ease, -1f), Is.EqualTo(ProcAnimTimeline.Ease(ease, 0f)), $"{ease} clamps below");
                Assert.That(ProcAnimTimeline.Ease(ease, 2f), Is.EqualTo(ProcAnimTimeline.Ease(ease, 1f)), $"{ease} clamps above");
            }

            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.Linear, 0.25f), Is.EqualTo(0.25f).Within(1e-5f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.InOut, 0.5f), Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.In, 0.5f), Is.LessThan(0.5f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.Out, 0.5f), Is.GreaterThan(0.5f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.CubicIn, 0.3f), Is.EqualTo(ProcAnimTimeline.Ease(ProcAnimEase.In, 0.3f)), "cubic alias");
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.Step, 0.99f), Is.EqualTo(0f));
            // Back: undershoots at the start (in) / overshoots at the end (out).
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.BackIn, 0.2f), Is.LessThan(0f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.BackOut, 0.8f), Is.GreaterThan(1f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.BackInOut, 0.1f), Is.LessThan(0f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.BackInOut, 0.9f), Is.GreaterThan(1f));
            // Elastic: oscillates past the target.
            var elastic = Enumerable.Range(1, 99).Select(i => ProcAnimTimeline.Ease(ProcAnimEase.ElasticOut, i / 100f)).ToList();
            Assert.That(elastic.Max(), Is.GreaterThan(1.05f));
            Assert.That(elastic.Any(v => v < 1f && v > 0.5f), "comes back below 1");
            Assert.That(Enumerable.Range(1, 99).Min(i => ProcAnimTimeline.Ease(ProcAnimEase.ElasticIn, i / 100f)), Is.LessThan(0f));
            // Hit-stop: frozen for the hold fraction, then snaps in.
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.HitStop, ProcAnimTimeline.HitStopHold * 0.9f), Is.EqualTo(0f));
            Assert.That(ProcAnimTimeline.Ease(ProcAnimEase.HitStop, ProcAnimTimeline.HitStopHold + 0.2f), Is.GreaterThan(0.5f));
        });
    }

    [Test]
    public void TimelineEvaluation()
    {
        var keys = new List<ProcAnimKey> { K(0f, 0f), K(1f, 10f, ProcAnimEase.Linear), K(2f, 10f), K(3f, 0f, ProcAnimEase.Step) };
        Assert.Multiple(() =>
        {
            Assert.That(ProcAnimTimeline.Evaluate(keys, -1f).X, Is.EqualTo(0f), "before the first key: first value");
            Assert.That(ProcAnimTimeline.Evaluate(keys, 0.5f).X, Is.EqualTo(5f).Within(1e-4f), "linear");
            Assert.That(ProcAnimTimeline.Evaluate(keys, 1.5f).X, Is.EqualTo(10f).Within(1e-4f), "hold");
            Assert.That(ProcAnimTimeline.Evaluate(keys, 2.99f).X, Is.EqualTo(10f).Within(1e-4f), "step holds until the key");
            Assert.That(ProcAnimTimeline.Evaluate(keys, 3f).X, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(ProcAnimTimeline.Evaluate(keys, 9f).X, Is.EqualTo(0f), "after the last key: last value");
            Assert.That(ProcAnimTimeline.Evaluate(new List<ProcAnimKey>(), 1f), Is.EqualTo(default(ProcAnimPose)));
            // Smooth (monotone spline) keeps the key values and stays inside them.
            Assert.That(ProcAnimTimeline.Evaluate(keys, 1f, true).X, Is.EqualTo(10f).Within(1e-4f));
            for (var t = 0f; t <= 3f; t += 0.05f)
                Assert.That(ProcAnimTimeline.Evaluate(keys, t, true).X, Is.InRange(-1e-3f, 10.001f), $"smooth overshoot at {t}");
        });
    }

    [Test]
    public async Task IkJointLimitsPropsAndExpressions()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Punch);
        var p = proto.Actor("puncher");
        var hand = p.Parts[ProcAnimBone.ArmNearHand].Pivot;

        Assert.Multiple(() =>
        {
            Assert.That(proto.ActorIndex("TARGET"), Is.EqualTo(1), "roles are case-insensitive");
            Assert.That(ProcAnimTimeline.Facing(proto, 1), Is.EqualTo(-1f));

            // IK weight 0: the keyed pose (arm hanging straight down).
            var rest = ProcAnimTimeline.Solve(proto, 0, 0f);
            Assert.That(ProcAnimTimeline.Point(rest, ProcAnimBone.ArmNearHand, hand).X, Is.EqualTo(0f).Within(1e-3f));

            // Full weight: the hand reaches the target (8, 2) through a 6 + 6 px arm, elbow folded forward.
            var reach = ProcAnimTimeline.Solve(proto, 0, 0.6f);
            var tip = ProcAnimTimeline.Point(reach, ProcAnimBone.ArmNearHand, hand);
            Assert.That(Vector2.Distance(tip, new Vector2(8, 2)), Is.LessThan(0.3f), $"IK tip {tip}");
            var elbow = reach[(int) ProcAnimBone.ArmNearLow].Rot - reach[(int) ProcAnimBone.ArmNear].Rot;
            Assert.That(elbow, Is.InRange(-5f, 90f), "elbow within the actor's limits");
            Assert.That(elbow, Is.GreaterThan(1f), "auto bend: elbow folds towards the facing");
            var noIk = ProcAnimTimeline.Solve(proto, 0, 0.6f, ik: false);
            Assert.That(ProcAnimTimeline.Point(noIk, ProcAnimBone.ArmNearHand, hand).X, Is.EqualTo(0f).Within(1e-3f), "ik: false");

            // Half weight: in between.
            var half = ProcAnimTimeline.Point(ProcAnimTimeline.Solve(proto, 0, 0.2f), ProcAnimBone.ArmNearHand, hand);
            Assert.That(half.X, Is.InRange(0.5f, 7.5f));

            // Joint limits: the far elbow is keyed to 120 but this actor allows 90; the knee keyed forward is clamped.
            var late = ProcAnimTimeline.Solve(proto, 0, 1f);
            Assert.That(late[(int) ProcAnimBone.ArmFarLow].Rot - late[(int) ProcAnimBone.ArmFar].Rot, Is.EqualTo(90f).Within(1e-3f));
            Assert.That(late[(int) ProcAnimBone.LegNearLow].Rot - late[(int) ProcAnimBone.LegNear].Rot, Is.EqualTo(0f).Within(1e-3f),
                "knees never bend forward");
            var pose = new Vector3[16];
            pose[(int) ProcAnimBone.LegNearLow].Z = 40f;
            ProcAnimTimeline.ApplyJointLimits(pose, -1f, true);
            Assert.That(pose[(int) ProcAnimBone.LegNearLow].Z, Is.EqualTo(40f), "mirrored facing: +40 is backwards");
            pose[(int) ProcAnimBone.ArmNearHand] = new Vector3(3, 3, 3);
            ProcAnimTimeline.ApplyJointLimits(pose, 1f, false);
            Assert.That(pose[(int) ProcAnimBone.ArmNearHand], Is.EqualTo(Vector3.Zero), "rigid hand");

            // Prop attached to the hand: follows the hand pose, visible only in its interval.
            var prop = p.Props[0];
            Assert.That(ProcAnimTimeline.PropVisible(p, prop, 0.1f), Is.False);
            Assert.That(ProcAnimTimeline.PropVisible(p, prop, 0.5f), Is.True);
            Assert.That(ProcAnimTimeline.PropVisible(p, prop, 0.9f), Is.False, "end is exclusive");
            var hp = reach[(int) ProcAnimBone.ArmNearHand];
            var pp = ProcAnimTimeline.PropPose(prop, hp, 0.6f);
            var expected = new Vector2(hp.X, hp.Y) + ProcAnimTimeline.Rotate(new Vector2(2, -1), hp.Rot);
            Assert.That(Vector2.Distance(new Vector2(pp.X, pp.Y), expected), Is.LessThan(1e-3f));
            Assert.That(pp.Rot, Is.EqualTo(hp.Rot + 90f).Within(1e-3f));

            // Expression stub layer: step keys.
            Assert.That(ProcAnimTimeline.ExpressionAt(p, 0.2f), Is.EqualTo(new ProcAnimExpression("neutral", null)));
            Assert.That(ProcAnimTimeline.ExpressionAt(p, 0.6f), Is.EqualTo(new ProcAnimExpression("angry", "narrowed")));
            Assert.That(ProcAnimTimeline.ExpressionAt(proto.Actor("target"), 0.6f), Is.EqualTo(default(ProcAnimExpression)));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ValidationOfAllScenes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        Assert.Multiple(() =>
        {
            foreach (var scene in protoMan.EnumeratePrototypes<ProcAnimScenePrototype>())
            {
                if (scene.ID == Bad || scene.ID.EndsWith("BadScene"))
                    continue;
                Assert.That(ProcAnimValidation.Validate(scene), Is.Empty, scene.ID);
            }

            var errors = ProcAnimValidation.Validate(protoMan.Index<ProcAnimScenePrototype>(Bad));
            string[] expected = { "duplicate role", "facing must be", "move: times must be ascending", "ik[0]", "ik[1]",
                "unknown target 'ghost'", "events: times must be ascending" };
            foreach (var e in expected)
                Assert.That(errors.Any(x => x.Contains(e)), $"expected '{e}' in: {string.Join(" | ", errors)}");
        });
        await pair.CleanReturnAsync();
    }

    private static async Task<TestMapData> Floor(TestPair pair)
    {
        var map = await pair.CreateTestMap();
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            for (var x = -4; x <= 4; x++)
            for (var y = -4; y <= 4; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
        return map;
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }

    [Test]
    public async Task EventOrderStopAndCleanup()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await Floor(pair);
        var ent = server.EntMan;
        var sys = server.System<ProcAnimSystem>();
        var rec = server.System<ProcAnimTestRecorderSystem>();
        var blocker = server.System<ActionBlockerSystem>();
        var console = server.ResolveDependency<IConsoleHost>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Punch);

        EntityUid a = default, b = default, c = default;
        await server.WaitPost(() =>
        {
            a = ent.SpawnEntity("MobHuman", map.GridCoords);
            b = ent.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1, 0)));
            c = ent.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0, 2)));
        });
        await RunSeconds(pair, 0.2);

        await server.WaitPost(() =>
        {
            Assert.That(sys.TryPlay(Punch, new[] { a }, out var e1), Is.False);
            Assert.That(e1, Is.EqualTo("procanim-error-actor-count"));
            Assert.That(sys.TryPlay(Punch, new[] { a, a }, out var e2), Is.False);
            Assert.That(e2, Is.EqualTo("procanim-error-same"));
            Assert.That(sys.TryPlay("NoSuchScene", new[] { a, b }, out var e3), Is.False);
            Assert.That(e3, Is.EqualTo("procanim-error-no-proto"));
            Assert.That(sys.TryPlay(Punch, new[] { a, b }, out var err), Is.True, err);
            Assert.That(sys.TryPlay(Punch, new[] { c, b }, out var e4), Is.False);
            Assert.That(e4, Is.EqualTo("procanim-error-busy"));
            var comp = ent.GetComponent<ProcAnimComponent>(b);
            Assert.That(comp.Actor, Is.EqualTo(1));
            Assert.That(comp.Actors, Is.EqualTo(new List<EntityUid> { a, b }));
            Assert.That(comp.Partner, Is.EqualTo(a));
        });
        var before = server.ResolveDependency<IEntityManager>().GetComponent<DamageableComponent>(b).TotalDamage;
        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) + 0.3);

        await server.WaitAssertion(() =>
        {
            // Every event once, in timeline order, on its target actor.
            Assert.That(rec.Fired.Select(f => f.Index), Is.EqualTo(Enumerable.Range(0, proto.Events.Count)));
            Assert.That(rec.Fired.Select(f => f.Time), Is.Ordered);
            Assert.That(rec.Fired.Select(f => f.Target), Is.EqualTo(new[] { a, b, b, a }));
            Assert.That(ent.GetComponent<DamageableComponent>(b).TotalDamage, Is.GreaterThan(before), "punch damage");
            Assert.That(rec.Ended.OrderBy(x => x.Actor), Is.EqualTo(new[] { (a, 0, true), (b, 1, true) }));
            Assert.That(ent.HasComponent<ProcAnimComponent>(a) || ent.HasComponent<ProcAnimComponent>(b), Is.False, "cleaned up");
            Assert.That(blocker.CanMove(a) && blocker.CanMove(b), "unlocked");
        });

        // Stopped half-way (by either actor): both released, not completed, later events never fire.
        rec.Fired.Clear();
        rec.Ended.Clear();
        await server.WaitPost(() => Assert.That(sys.TryPlay(Punch, new[] { a, b }, out var err), Is.True, err));
        await RunSeconds(pair, 0.3);
        await server.WaitPost(() => sys.Stop(b));
        await RunSeconds(pair, 1.2);
        await server.WaitAssertion(() =>
        {
            Assert.That(rec.Fired.Select(f => f.Beat), Is.EqualTo(new[] { "wind-up" }));
            Assert.That(rec.Ended.OrderBy(x => x.Actor), Is.EqualTo(new[] { (a, 0, false), (b, 1, false) }));
            Assert.That(ent.HasComponent<ProcAnimComponent>(a) || ent.HasComponent<ProcAnimComponent>(b), Is.False);
            Assert.That(blocker.CanMove(a) && blocker.CanMove(b));
        });

        // Admin command: play, stop all; a deleted actor ends the scene for the other one.
        await server.WaitPost(() =>
            console.ExecuteCommand($"procanim play {Punch} {ent.GetNetEntity(a)} {ent.GetNetEntity(c)}"));
        await RunSeconds(pair, 0.1);
        await server.WaitPost(() =>
        {
            Assert.That(ent.HasComponent<ProcAnimComponent>(a) && ent.HasComponent<ProcAnimComponent>(c), "command started it");
            console.ExecuteCommand("procanim stop");
        });
        await RunSeconds(pair, 0.1);
        await server.WaitPost(() =>
        {
            Assert.That(ent.HasComponent<ProcAnimComponent>(a) || ent.HasComponent<ProcAnimComponent>(c), Is.False, "stop all");
            Assert.That(sys.TryPlay(Punch, new[] { a, c }, out var err), Is.True, err);
            ent.DeleteEntity(c);
        });
        await RunSeconds(pair, 0.2);
        await server.WaitAssertion(() =>
        {
            Assert.That(ent.HasComponent<ProcAnimComponent>(a), Is.False, "partner deleted -> released");
            Assert.That(blocker.CanMove(a));
            Assert.That(sys.StopAll(), Is.EqualTo(0));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The layout is screen-relative: on a grid rotated by 90° / 45° (camera follows the grid) the forward axis, the
    /// sprite facing on screen and the actor positions in the grid's (= screen's) frame match the unrotated run.
    /// </summary>
    [Test]
    public async Task RotatedGridLayout()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var ent = server.EntMan;
        var sys = server.System<ProcAnimSystem>();
        var xformSys = server.System<SharedTransformSystem>();
        const string scene = "GilbertFinisherPhase1";
        var samples = new[] { 0.0, 0.8, 1.6, 2.5, 4.0 };

        async Task<List<(Vector2 Fwd, Vector2 PosA, Vector2 PosB, Direction DirA, Direction DirB)>> Run(double degrees)
        {
            var map = await Floor(pair);
            var angle = Angle.FromDegrees(degrees);
            EntityUid a = default, b = default;
            await server.WaitPost(() =>
            {
                xformSys.SetWorldRotation(map.Grid.Owner, angle);
                a = ent.SpawnEntity("MobHuman", map.GridCoords);
                b = ent.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(1, 0)));
            });
            await RunSeconds(pair, 0.2);

            var result = new List<(Vector2, Vector2, Vector2, Direction, Direction)>();
            Vector2 origin = default;
            var elapsed = 0.0;
            foreach (var at in samples)
            {
                if (at > elapsed)
                    await RunSeconds(pair, at - elapsed);
                elapsed = at;
                await server.WaitPost(() =>
                {
                    if (at == 0.0)
                    {
                        origin = xformSys.GetWorldPosition(a);
                        Assert.That(sys.TryPlay(scene, new[] { a, b }, out var err), Is.True, err);
                        var comp = ent.GetComponent<ProcAnimComponent>(a);
                        Assert.That((comp.View - angle).Reduced().Degrees, Is.EqualTo(0).Within(1e-3), $"view on {degrees}°");
                    }

                    // World -> screen (the eye of a player on this grid is rotated by -angle).
                    var inv = -angle;
                    var fwd = inv.RotateVec(ent.GetComponent<ProcAnimComponent>(a).Forward);
                    var pa = inv.RotateVec(xformSys.GetWorldPosition(a) - origin);
                    var pb = inv.RotateVec(xformSys.GetWorldPosition(b) - origin);
                    var da = (xformSys.GetWorldRotation(a) + inv).GetCardinalDir();
                    var db = (xformSys.GetWorldRotation(b) + inv).GetCardinalDir();
                    result.Add((fwd, pa, pb, da, db));
                });
            }

            await server.WaitPost(() => sys.StopAll());
            await RunSeconds(pair, 0.1);
            return result;
        }

        var flat = await Run(0);
        Assert.That(flat[0].Fwd.X, Is.EqualTo(1f).Within(1e-4), "side view on the flat grid");
        Assert.That(flat[0].DirA, Is.EqualTo(Direction.East));
        Assert.That(flat[0].DirB, Is.EqualTo(Direction.West));
        foreach (var degrees in new[] { 90.0, 45.0 })
        {
            var rotated = await Run(degrees);
            for (var i = 0; i < samples.Length; i++)
            {
                var (f, r) = (flat[i], rotated[i]);
                var where = $"{degrees}° at {samples[i]}s";
                Assert.That(Vector2.Distance(f.Fwd, r.Fwd), Is.LessThan(1e-3f), "forward " + where);
                Assert.That(Vector2.Distance(f.PosA, r.PosA), Is.LessThan(2e-3f), $"attacker {f.PosA} vs {r.PosA} " + where);
                Assert.That(Vector2.Distance(f.PosB, r.PosB), Is.LessThan(2e-3f), $"victim {f.PosB} vs {r.PosB} " + where);
                Assert.That(r.DirA, Is.EqualTo(f.DirA), "attacker direction " + where);
                Assert.That(r.DirB, Is.EqualTo(f.DirB), "victim direction " + where);
            }
        }

        await pair.CleanReturnAsync();
    }
}

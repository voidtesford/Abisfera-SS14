using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared._CorvaxGoob.ProcAnim;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProcAnim;

/// <summary>
/// v12 hit effects (spatter, recoil, bruise, drips, pool, dust, shake, stagger), face expressions and smooth view
/// flips: the deterministic maths shared by the client overlay and the preview, validation, and the Gilbert data.
/// </summary>
[TestFixture]
public sealed class ProcAnimFxTest
{
    private const string Scene = "ProcAnimFxTestScene";
    private const string Bad = "ProcAnimFxTestBadScene";
    private static readonly string[] Finishers = { "GilbertFinisherPhase1", "GilbertFinisherBarrel", "GilbertFinisherGroundPound" };

    [TestPrototypes]
    private const string Prototypes = @"
- type: procAnimScene
  id: ProcAnimFxTestScene
  length: 1.5
  requiredRoom: 0
  actors:
  - role: hitter
    facing: 1
    move: [{t: 0, x: 0}]
    face: {glow: true}
    expressions:
    - {t: 0, face: neutral}
    - {t: 0.2, face: angry}
  - role: target
    facing: -1
    move: [{t: 0, x: 0.6}]
    parts:
      Body: {pivot: '0, 0', keys: [{t: 0, rot: 0}]}
      Head: {pivot: '0, 9', keys: [{t: 0, rot: 0}]}
    views:
    - {start: 0.8, end: 1.2, facing: South}
    expressions:
    - {t: 0, face: neutral}
    - {t: 0.3, face: pain, eyes: closed}
    - {t: 0.5, face: dazed}
    - {t: 1.3, face: dead}
  events:
  - {t: 0.3, target: target, hitStop: 0.05, effects: [
      {type: Spatter, part: Head, at: '0, 9', dir: 0, spread: 40, count: 12, floor: -14},
      {type: Recoil, part: Head, dir: 0, strength: 1, duration: 0.2},
      {type: Shake, strength: 1, duration: 0.2},
      {type: Bruise, part: Head, at: '-2, 9'}]}
  - {t: 0.6, target: target, effects: [
      {type: Spatter, part: Head, at: '0, 9', dir: 180, spread: 40, count: 12},
      {type: Stagger, duration: 0.4},
      {type: Pool, part: Body, at: '0, 0', size: 1.5},
      {type: Dust, part: Body, at: '0, 0', count: 8},
      {type: Drips, part: Head, at: '-3, 8', duration: 1}]}

- type: procAnimScene
  id: ProcAnimFxTestBadScene
  length: 1
  requiredRoom: 0
  actors:
  - role: a
    move: [{t: 0, x: 0}]
    expressions:
    - {t: 0, face: smirk}
    - {t: 0.5, eyes: wink}
  events:
  - {t: 0.2, target: a, effects: [
      {type: Spatter, count: -3, spread: 400},
      {type: Pool, size: 0},
      {type: Dust, strength: -1},
      {type: Recoil, duration: 0}]}
";

    private static Vector2 Mean(List<ProcAnimFx.Dot> dots) =>
        dots.Aggregate(Vector2.Zero, (s, d) => s + d.Position) / dots.Count;

    [Test]
    public async Task ParticlesDeterministicAndDirectional()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Scene);
        var right = proto.Events[0].Effects[0];
        var left = proto.Events[1].Effects[0];
        var o = new Vector2(0, 9);
        Assert.Multiple(() =>
        {
            var a = ProcAnimFx.Particles(right, 7, o, 0.08f, Color.Red);
            var b = ProcAnimFx.Particles(right, 7, o, 0.08f, Color.Red);
            Assert.That(a, Is.EqualTo(b), "same seed and age -> same particles");
            Assert.That(ProcAnimFx.Particles(right, 8, o, 0.08f, Color.Red), Is.Not.EqualTo(a), "the seed varies them");
            Assert.That(a, Is.Not.Empty);
            Assert.That(Mean(a).X, Is.GreaterThan(o.X + 1f), "dir 0 sprays to +x");
            Assert.That(Mean(ProcAnimFx.Particles(left, 7, o, 0.08f, Color.Red)).X, Is.LessThan(o.X - 1f), "dir 180 sprays to -x");
            Assert.That(ProcAnimFx.Particles(right, 7, o, -0.1f, Color.Red), Is.Empty, "nothing before the hit");

            // After ~1.5 s every droplet has landed as a stain on the floor and stays coloured like the blood.
            var landed = ProcAnimFx.Particles(right, 7, o, 1.5f, Color.Red);
            Assert.That(landed, Has.Count.EqualTo(12));
            Assert.That(landed.All(d => MathF.Abs(d.Position.Y - right.Floor) <= 2.01f), "stains lie on the floor");
            Assert.That(landed.All(d => d.Color.R > d.Color.G && d.Color.A > 0.3f), "blood coloured, visible");
            Assert.That(ProcAnimFx.Particles(right, 7, o, ProcAnimFx.DurationOf(right) + 0.1f, Color.Red), Is.Empty, "fades out");

            // Pool grows, dust stays on the floor and fades, drips fall.
            var pool = proto.Events[1].Effects[2];
            var po = ProcAnimFx.Origin(pool, ProcAnimTimeline.Solve(proto, 1, 0.6f));
            Assert.That(po.Y, Is.EqualTo(pool.Floor), "pool on the floor");
            float Area(List<ProcAnimFx.Dot> d) => d.Sum(x => x.Size.X * x.Size.Y * x.Color.A);
            Assert.That(Area(ProcAnimFx.Particles(pool, 1, po, 3f, Color.Red)),
                Is.GreaterThan(Area(ProcAnimFx.Particles(pool, 1, po, 0.3f, Color.Red))), "pool spreads");
            var dust = proto.Events[1].Effects[3];
            var dusty = ProcAnimFx.Particles(dust, 1, new Vector2(0, -14), 0.2f, Color.Red);
            Assert.That(dusty, Is.Not.Empty);
            Assert.That(dusty.All(d => d.Position.Y >= -16f), "dust rises from the floor");
            Assert.That(ProcAnimFx.Particles(dust, 1, new Vector2(0, -14), ProcAnimFx.DurationOf(dust) + 0.1f, Color.Red), Is.Empty);
            Assert.That(ProcAnimFx.ColorOf(dust, Color.Red), Is.Not.EqualTo(Color.Red), "dust is not blood coloured");
            Assert.That(ProcAnimFx.ColorOf(pool, Color.Blue), Is.EqualTo(Color.Blue), "pools use the blood colour");
            Assert.That(ProcAnimFx.Anchored(ProcAnimEffectType.Spatter) && !ProcAnimFx.Anchored(ProcAnimEffectType.Bruise));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PoseLayersAndShakeStayInTheirWindow()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Scene);
        const int head = (int) ProcAnimBone.Head;
        const int body = (int) ProcAnimBone.Body;
        Assert.Multiple(() =>
        {
            var rest = ProcAnimTimeline.Solve(proto, 1, 0.25f);
            var hit = ProcAnimTimeline.Solve(proto, 1, 0.36f);
            var after = ProcAnimTimeline.Solve(proto, 1, 0.55f);
            // The head's visual centre (above the neck pivot) is pushed along dir 0 (+x).
            Vector2 HeadAt(ProcAnimPose[] p) => ProcAnimTimeline.Point(p, ProcAnimBone.Head, new Vector2(0, 12));
            Assert.That(HeadAt(hit).X, Is.GreaterThan(HeadAt(rest).X + 1f), "recoil pushes the head along dir 0");
            Assert.That(after[head].X, Is.EqualTo(rest[head].X).Within(1e-3f), "recoil is gone after its duration");
            Assert.That(after[body].Rot, Is.EqualTo(rest[body].Rot).Within(1e-3f));

            // Stagger sways the body over its window and settles.
            var sway = Enumerable.Range(1, 30).Select(i => ProcAnimTimeline.Solve(proto, 1, 0.6f + i * 0.4f / 31f)[body].Rot).ToList();
            Assert.That(sway.Max() - sway.Min(), Is.GreaterThan(0.5f), "stagger sways");
            Assert.That(ProcAnimTimeline.Solve(proto, 1, 1.05f)[body].Rot, Is.EqualTo(rest[body].Rot).Within(1e-3f), "stagger settles");
            Assert.That(ProcAnimTimeline.Solve(proto, 0, 0.36f)[head].X, Is.EqualTo(ProcAnimTimeline.Solve(proto, 0, 0.25f)[head].X).Within(1e-4f),
                "only the event target is moved");

            // No popping: the recoil layer is continuous.
            var prev = ProcAnimTimeline.Solve(proto, 1, 0.3f)[head];
            for (var i = 1; i <= 300; i++)
            {
                var cur = ProcAnimTimeline.Solve(proto, 1, 0.3f + i * 0.001f)[head];
                Assert.That(MathF.Abs(cur.X - prev.X), Is.LessThan(0.3f), $"recoil jump at {0.3f + i * 0.001f}");
                prev = cur;
            }

            var hitReal = ProcAnimTimeline.RealTime(proto, 0.3f);
            Assert.That(ProcAnimFx.Shake(proto, 1, hitReal - 0.01f), Is.EqualTo(Vector2.Zero), "no shake before the hit");
            Assert.That(Enumerable.Range(1, 10).Any(i => ProcAnimFx.Shake(proto, 1, hitReal + i * 0.015f).Length() > 0.1f), "shakes");
            Assert.That(ProcAnimFx.Shake(proto, 1, hitReal + 0.21f), Is.EqualTo(Vector2.Zero), "no shake after its duration");
            Assert.That(ProcAnimFx.Shake(proto, 0, hitReal + 0.02f), Is.EqualTo(Vector2.Zero), "the hitter does not shake");
            Assert.That(ProcAnimFx.RecoilEnvelope(0f, 0.3f), Is.EqualTo(0f));
            Assert.That(ProcAnimFx.RecoilEnvelope(0.3f, 0.3f), Is.EqualTo(0f));
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public void SoftJointLimitsAndSmoothIkWeight()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ProcAnimTimeline.SoftClamp(30f, 0f, 90f), Is.EqualTo(30f), "identity inside");
            Assert.That(ProcAnimTimeline.SoftClamp(200f, 0f, 90f), Is.EqualTo(90f), "exact limit far out");
            Assert.That(ProcAnimTimeline.SoftClamp(-50f, 0f, 90f), Is.EqualTo(0f));
            var prev = ProcAnimTimeline.SoftClamp(70f, 0f, 90f);
            var prevSlope = 1f;
            for (var i = 1; i <= 400; i++)
            {
                var x = 70f + i * 0.1f;
                var y = ProcAnimTimeline.SoftClamp(x, 0f, 90f);
                var slope = (y - prev) / 0.1f;
                Assert.That(y, Is.LessThanOrEqualTo(90f));
                Assert.That(slope, Is.InRange(-1e-3f, 1.001f), "monotonic, never faster than the input");
                Assert.That(MathF.Abs(slope - prevSlope), Is.LessThan(0.05f), $"no slope jump (pop) at {x}");
                prev = y;
                prevSlope = slope;
            }

            var track = new ProcAnimIkTrack
            {
                Weights = new() { new ProcAnimKey { Time = 0, X = 0 }, new ProcAnimKey { Time = 1, X = 1, Ease = ProcAnimEase.Linear } },
            };
            Assert.That(ProcAnimTimeline.IkWeight(track, 0.5f), Is.EqualTo(0.5f).Within(1e-4f));
            Assert.That(ProcAnimTimeline.IkWeight(track, 0.05f), Is.LessThan(0.05f), "eases in");
            Assert.That(ProcAnimTimeline.IkWeight(track, 0.95f), Is.GreaterThan(0.95f), "eases out");
            Assert.That(ProcAnimTimeline.IkWeight(track, 1f), Is.EqualTo(1f).Within(1e-5f));
        });
    }

    [Test]
    public async Task FacesAndViewFlips()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Scene);
        var target = proto.Actors[1];
        var hitter = proto.Actors[0];
        var pose = ProcAnimTimeline.Solve(proto, 1, 0.1f);
        Assert.Multiple(() =>
        {
            var neutral = ProcAnimFx.FaceDots(target.Face, new ProcAnimExpression("neutral", "open"), pose, -1f, false);
            var pain = ProcAnimFx.FaceDots(target.Face, ProcAnimTimeline.ExpressionAt(target, 0.4f), pose, -1f, false);
            var dead = ProcAnimFx.FaceDots(target.Face, ProcAnimTimeline.ExpressionAt(target, 1.4f), pose, -1f, false);
            var front = ProcAnimFx.FaceDots(target.Face, new ProcAnimExpression("dead", null), pose, -1f, true);
            Assert.That(ProcAnimTimeline.ExpressionAt(target, 0.4f), Is.EqualTo(new ProcAnimExpression("pain", "closed")));
            Assert.That(neutral, Is.Empty, "neutral + open draws nothing over the sprite");
            Assert.That(pain, Is.Not.Empty);
            Assert.That(dead, Has.Count.GreaterThanOrEqualTo(5), "X eye");
            Assert.That(front.Count, Is.EqualTo(2 * dead.Count - 1), "front view: two eyes, one mouth");
            var hx = ProcAnimTimeline.Point(pose, ProcAnimBone.Head, new Vector2(0, 9)).X;
            Assert.That(pain.All(d => d.Position.X < hx), "facing -x: the face is drawn on the -x side of the head");
            var mirrored = ProcAnimFx.FaceDots(target.Face, new ProcAnimExpression("pain", "closed"), pose, 1f, false);
            Assert.That(mirrored.Select(d => d.Position.X - hx).Sum(), Is.EqualTo(-pain.Select(d => d.Position.X - hx).Sum()).Within(1e-3f),
                "the face mirrors with the facing");

            var hp = ProcAnimTimeline.Solve(proto, 0, 0.5f);
            var angry = ProcAnimFx.FaceDots(hitter.Face, ProcAnimTimeline.ExpressionAt(hitter, 0.5f), hp, 1f, false);
            Assert.That(hitter.Face.Glow);
            Assert.That(angry.Any(d => d.Color.R > 0.9f && d.Color.G < 0.4f), "glowing eyes flare red when angry");
            Assert.That(ProcAnimFx.FaceDots(new ProcAnimFace { Disabled = true }, new ProcAnimExpression("pain", "closed"), pose, 1f, false), Is.Empty);

            Assert.That(ProcAnimFx.ViewFlipScale(target, 0.5f), Is.EqualTo(1f));
            Assert.That(ProcAnimFx.ViewFlipScale(target, 1f), Is.EqualTo(1f));
            Assert.That(ProcAnimFx.ViewFlipScale(target, 0.8f), Is.LessThan(0.5f), "squeezed at the view edge");
            Assert.That(ProcAnimFx.ViewFlipScale(target, 0.77f), Is.InRange(0.3f, 1f));
            Assert.That(ProcAnimFx.ViewFlipScale(hitter, 0.8f), Is.EqualTo(1f), "no views -> no flip");
            var prev = ProcAnimFx.ViewFlipScale(target, 0.7f);
            for (var i = 1; i <= 100; i++)
            {
                var cur = ProcAnimFx.ViewFlipScale(target, 0.7f + i * 0.001f);
                Assert.That(MathF.Abs(cur - prev), Is.LessThan(0.05f), "smooth flip");
                prev = cur;
            }
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ValidationOfEffectsAndFaces()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var errors = ProcAnimValidation.Validate(protoMan.Index<ProcAnimScenePrototype>(Bad));
        Assert.Multiple(() =>
        {
            Assert.That(ProcAnimValidation.Validate(protoMan.Index<ProcAnimScenePrototype>(Scene)), Is.Empty);
            string[] expected = { "unknown face 'smirk'", "unknown eyes 'wink'", "events[0].effects[0] (Spatter", "count", "spread",
                "events[0].effects[1] (Pool", "size", "events[0].effects[2] (Dust", "strength", "events[0].effects[3] (Recoil", "duration" };
            foreach (var e in expected)
                Assert.That(errors.Any(x => x.Contains(e)), $"expected an error containing '{e}', got:\n{string.Join("\n", errors)}");
        });
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GilbertFinishersHaveHitEffectsAndFaces()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var all = new HashSet<ProcAnimEffectType>();
        Assert.Multiple(() =>
        {
            foreach (var id in Finishers)
            {
                var scene = protoMan.Index<ProcAnimScenePrototype>(id);
                Assert.That(ProcAnimValidation.Validate(scene), Is.Empty, id);
                var types = scene.Events.SelectMany(e => e.Effects.Select(f => f.Type)).ToHashSet();
                all.UnionWith(types);
                foreach (var need in new[] { ProcAnimEffectType.Spatter, ProcAnimEffectType.Recoil, ProcAnimEffectType.Bruise,
                             ProcAnimEffectType.Dust })
                    Assert.That(types, Does.Contain(need), $"{id}: {need}");
                Assert.That(types.Contains(ProcAnimEffectType.Pool) || types.Contains(ProcAnimEffectType.Drips), $"{id}: bleeding aftermath");
                Assert.That(scene.Events.Where(e => e.Effects.Count > 0).All(e => e.TargetIndex >= 0), $"{id}: effects need a target");
                // Hits that spray blood are heavy: they keep a hit-stop or a shake.
                Assert.That(scene.Events.Where(e => e.Effects.Any(f => f.Type == ProcAnimEffectType.Spatter))
                    .All(e => e.HitStop > 0f || e.Shake > 0f || e.Effects.Any(f => f.Type == ProcAnimEffectType.Shake)), $"{id}: heavy hits");
                Assert.That(scene.Actors.Any(a => a.Face.Glow && a.Expressions.Any(k => k.Face == "angry")), $"{id}: Gilbert gets angry");
                Assert.That(scene.Actors.Any(a => !a.Face.Glow && a.Expressions.Any(k => k.Face == "pain")), $"{id}: the victim hurts");
                Assert.That(scene.Actors.Any(a => a.Expressions.Any(k => k.Face == "dazed")), $"{id}: dazed");
            }

            Assert.That(all, Is.SupersetOf(Enum.GetValues<ProcAnimEffectType>()), "every effect type is used");
            Assert.That(protoMan.Index<ProcAnimScenePrototype>("GilbertFinisherBarrel").Actors
                .Any(a => a.Expressions.Any(k => k.Face == "dead")), "barrel victim dies");
        });
        await pair.CleanReturnAsync();
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Systems;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v13.2: V3 ends with a neck break instead of the head shot - standing behind the victim Gilbert grabs the handle of
/// the baton rammed in its mouth and twists it until the head turns round (the head is drawn mirrored from then on,
/// <see cref="ProcAnimActor.Flips"/>), pulls the baton out and kicks the turned face, sending the corpse sliding off.
/// </summary>
[TestFixture]
public sealed class GilbertV132Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";
    private const string GroundPound = "GilbertFinisherGroundPound";

    [Test]
    public async Task NeckBreakScene()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(GroundPound);
        var attacker = proto.Actor("attacker");
        var victim = proto.Actor("victim");
        var snap = proto.Events.Single(e => e.Kill);
        var kick = proto.Events.Last(e => e.Damage.Count > 0);

        Assert.Multiple(() =>
        {
            // The head turns round at the snap and stays turned.
            var flip = victim.Flips.Single();
            Assert.That(flip.Parts, Is.EquivalentTo(new[] { ProcAnimBone.Head }));
            Assert.That(flip.Time, Is.EqualTo(snap.Time).Within(0.05f), "the face turns round as the neck snaps");
            Assert.That(flip.Until, Is.Null, "stays turned");
            Assert.That(ProcAnimTimeline.FlipScale(victim, ProcAnimBone.Head, snap.Time - 0.5f), Is.EqualTo(1f));
            Assert.That(System.MathF.Abs(ProcAnimTimeline.FlipScale(victim, ProcAnimBone.Head, flip.Time)), Is.EqualTo(ProcAnimTimeline.MinFlipScale).Within(1e-4f), "edge-on mid-turn, never a zero scale");
            Assert.That(ProcAnimTimeline.FlipScale(victim, ProcAnimBone.Head, snap.Time + 0.5f), Is.EqualTo(-1f));
            Assert.That(ProcAnimTimeline.FlipScale(victim, ProcAnimBone.Body, snap.Time + 0.5f), Is.EqualTo(1f), "only the head");
            Assert.That(ProcAnimTimeline.FlipScale(attacker, ProcAnimBone.Head, snap.Time + 0.5f), Is.EqualTo(1f));

            // A mirrored part mirrors its props: offset x and rotation negated.
            var lodged = victim.Props.Single(p => p.Part == ProcAnimBone.Head);
            var part = new ProcAnimPose(1f, 2f, 0f);
            var t = snap.Time - 0.2f;
            var plain = ProcAnimTimeline.PropPose(lodged, part, t, 1f, lodged.ItemRotation);
            var turned = ProcAnimTimeline.PropPose(lodged, part, t, -1f, lodged.ItemRotation);
            Assert.That(turned.X - part.X, Is.EqualTo(-(plain.X - part.X)).Within(1e-4f));
            Assert.That(turned.Y, Is.EqualTo(plain.Y).Within(1e-4f));
            Assert.That(turned.Rot, Is.EqualTo(-plain.Rot).Within(1e-4f));

            // The baton stays in the mouth through the twist and passes straight into his hand as he pulls it out.
            Assert.That(lodged.Start, Is.LessThan(snap.Time));
            Assert.That(lodged.End, Is.GreaterThan(snap.Time).And.LessThan(kick.Time));
            var pulled = attacker.Props.Single(p => p.Start > snap.Time);
            Assert.That(pulled.Start, Is.EqualTo(lodged.End).Within(1e-3f), "no gap / double baton at the pull-out");
            Assert.That(pulled.Part, Is.EqualTo(ProcAnimBone.ArmFarHand));
            Assert.That(attacker.Props.All(p => p.End <= lodged.Start + 1e-3f || p.Start >= lodged.End - 1e-3f),
                "his hand holds no second baton while one is in the mouth");

            // His far hand is on the handle (IK to the victim's head) during the twist, the near one on the skull.
            Assert.That(attacker.Ik.Any(k => k.Limb == ProcAnimBone.ArmFar && k.RelativeTo == "victim.Head"
                                             && ProcAnimTimeline.IkWeight(k, snap.Time - 0.1f) > 0.9f), "far hand on the handle");
            Assert.That(attacker.Ik.Any(k => k.Limb == ProcAnimBone.ArmNear && k.RelativeTo == "victim.Head"
                                             && ProcAnimTimeline.IkWeight(k, snap.Time) > 0.9f), "near hand on the skull");
            // The kick: his near foot on the turned face at the hit.
            Assert.That(attacker.Ik.Any(k => k.Limb == ProcAnimBone.LegNear && k.RelativeTo == "victim.Head"
                                             && ProcAnimTimeline.IkWeight(k, kick.Time) > 0.9f), "the foot lands on the face");

            // The neck break: an audible crack, a hit effect, the victim dies; no shot, no decapitation.
            Assert.That(snap.Sound, Is.Not.Null);
            Assert.That(snap.Flash || snap.Effects.Count > 0, "hit effect");
            Assert.That(snap.Amputate, Is.Empty);
            Assert.That(proto.Events.SelectMany(e => e.Amputate), Has.No.Member(TargetBodyPart.Head));
            Assert.That(proto.Events.Any(e => e.Unholster), Is.False, "the shotgun is not brought back");
            Assert.That(attacker.UnholsterAt, Is.Null);
            Assert.That(victim.FinalDamage?.DamageDict.GetValueOrDefault("Asphyxiation").Float(), Is.EqualTo(200f), "final asphyxiation kept");
            // Same budget as the old head shot (Head 60 + Chest 40), now all Blunt: the snap 60 into the head, the kick's 40
            // into the torso (a second head hit on a head at its integrity limit rolls a random dismemberment).
            var late = proto.Events.Where(e => e.Time >= snap.Time).SelectMany(e => e.Damage).ToList();
            Assert.That(late.All(h => h.IgnoreArmor));
            Assert.That(snap.Damage.Single().Part, Is.EqualTo(TargetBodyPart.Head));
            Assert.That(snap.Damage.Single().Damage.DamageDict.GetValueOrDefault("Blunt").Float(), Is.EqualTo(60f), "the neck break");
            Assert.That(kick.Damage.Single().Part, Is.EqualTo(TargetBodyPart.Chest));
            Assert.That(late.Sum(h => h.Damage.DamageDict.GetValueOrDefault("Blunt").Float()), Is.EqualTo(100f), "neck + kick = the old 100");
            Assert.That(late.Sum(h => h.Damage.DamageDict.GetValueOrDefault("Piercing").Float()), Is.EqualTo(0f));

            // The kick throws the corpse: a flight section starting at the kick, moving away from Gilbert.
            Assert.That(victim.Flight, Is.Not.Null);
            Assert.That(victim.Flight!.Start, Is.EqualTo(kick.Time).Within(0.02f));
            Assert.That(victim.Flight.End, Is.GreaterThan(victim.Flight.Start + 0.2f));
            var x0 = ProcAnimTimeline.Evaluate(victim.Move, victim.Flight.Start).X;
            var x1 = ProcAnimTimeline.Evaluate(victim.Move, victim.Flight.End).X;
            Assert.That(x1 - x0, Is.GreaterThan(0.6f), "real knockback");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Played for real: the victim dies with its head still on, without its arms, thrown back by the kick.</summary>
    [Test]
    public async Task NeckBreakPlaysOnServer()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var sys = server.System<GilbertFinisherSystem>();
        var body = server.System<SharedBodySystem>();
        var xforms = server.System<SharedTransformSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(GroundPound);
        await server.WaitPost(() =>
        {
            var mapSys = server.System<SharedMapSystem>();
            for (var x = -8; x <= 8; x++)
            for (var y = -8; y <= 8; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            var gravity = entMan.EnsureComponent<GravityComponent>(map.Grid);
            server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            entMan.GetComponent<GilbertAIComponent>(g).NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            var q = entMan.EntityQueryEnumerator<GunComponent>();
            while (q.MoveNext(out _, out var gun))
                gun.NextFire = timing.CurTime + TimeSpan.FromHours(1);
            v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(v);
        });
        await RunSeconds(pair, 0.3);
        await server.WaitAssertion(() => Assert.That(sys.TryStart(g, v, out var err, GroundPound), err));

        // Right after the snap, before the kick: dead, head on, still next to him.
        var snap = proto.Events.Single(e => e.Kill).Time;
        var kick = proto.Events.Last(e => e.Damage.Count > 0).Time;
        var t1 = proto.Intro + ProcAnimTimeline.RealTime(proto, (snap + kick) / 2f);
        await RunSeconds(pair, t1);
        var gap0 = 0f;
        var at0 = Vector2.Zero;
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(v));
            var parts = body.GetBodyChildren(v).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            Assert.That(parts, Does.Contain(TargetBodyPart.Head), "the neck is broken, the head stays on");
            Assert.That(entMan.GetComponent<MobStateComponent>(v).CurrentState, Is.EqualTo(MobState.Dead), "the snap kills");
            gap0 = (xforms.GetWorldPosition(v) - xforms.GetWorldPosition(g)).Length();
            at0 = xforms.GetWorldPosition(v);
        });

        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) + proto.Intro + 1.5 - t1);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g) || sys.IsPlaying(v), Is.False, "ended");
            var parts = body.GetBodyChildren(v).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            var slid = (xforms.GetWorldPosition(v) - at0).Length();
            TestContext.Out.WriteLine($"[v13.2] parts {string.Join(",", parts)}, gap before the kick {gap0:F2}, corpse thrown {slid:F2}");
            Assert.That(slid, Is.GreaterThan(0.6f), "the kick really throws the corpse back");
            Assert.That(parts, Does.Contain(TargetBodyPart.Head));
            Assert.That(parts, Does.Not.Contain(TargetBodyPart.LeftArm).And.Not.Contain(TargetBodyPart.RightArm));
            Assert.That(entMan.GetComponent<MobStateComponent>(v).CurrentState, Is.EqualTo(MobState.Dead));
        });

        await pair.CleanReturnAsync();
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }
}

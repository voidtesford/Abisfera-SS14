using System.Collections.Generic;
using Content.Shared.Gravity;
using Content.Server.Gravity;
using Content.Server.NPC.HTN;
using Content.Server._CorvaxGoob.Gilbert;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.Gilbert.Finisher;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Shared._CorvaxGoob.Gilbert.Finisher;
using Content.Shared.ActionBlocker;
using Content.Shared.Humanoid;
using Content.Shared.Standing;
using Robust.Client.GameObjects;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// Phase-1 finisher cutscene (t265u): the <c>gilbertfinisher</c> command runs, locks both participants, moves them along
/// the timeline, animates the client sprite and restores everything (no stuck offsets / locks) without crashing.
/// </summary>
[TestFixture]
public sealed class GilbertFinisherTest
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

            // Gravity, so the weightless float animation does not move sprite offsets around.
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }

    [Test]
    public async Task PrototypeIsSane()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>()
            .Index<GilbertFinisherPrototype>(GilbertFinisherSystem.DefaultFinisher);

        Assert.Multiple(() =>
        {
            Assert.That(proto.Length, Is.InRange(10f, 12.5f), "10-12.5 s cutscene");
            Assert.That(GilbertFinisherTimeline.TotalLength(proto), Is.GreaterThan(proto.Length), "hit-stops lengthen real time");
            Assert.That(proto.Events.Any(e => e.Blood != null), "blood bursts");
            Assert.That(proto.Events.Any(e => e.Decal != null), "floor decal");
            Assert.That(proto.Attacker.HolsterHeldItems, Is.True, "shotgun slung on the back");
            // Holster intro (anim time -Intro..0): the swing ends, then the item is drawn on the back; it has its sounds.
            Assert.That(proto.Intro, Is.InRange(0.5f, 0.7f), "holster beat 0.5-0.7 s");
            Assert.That(proto.Attacker.HolsterAt, Is.Not.Null.And.InRange(-proto.Intro, 0f));
            Assert.That(GilbertFinisherTimeline.HeldInHands(proto.Attacker, -proto.Intro), Is.True);
            Assert.That(GilbertFinisherTimeline.HeldInHands(proto.Attacker, 0f), Is.False);
            Assert.That(proto.Events.Count(e => e.Time < 0f && e.Time >= -proto.Intro && e.Sound != null), Is.GreaterThanOrEqualTo(2), "swing + sling sounds");
            Assert.That(GilbertFinisherTimeline.AnimTime(proto, -0.3f), Is.EqualTo(-0.3f), "no hit-stop warp in the intro");
            Assert.That(GilbertFinisherTimeline.RealTime(proto, -0.3f), Is.EqualTo(-0.3f));
            Assert.That(proto.Attacker.Parts.ContainsKey(GilbertFinisherPart.HeldNear), "held item animated in the hand");
            // Arms and legs have two segments (upper arm, forearm + hand; thigh, shin + shoe), so no hand / foot tracks.
            foreach (var p in new[] { GilbertFinisherPart.ArmNearHand, GilbertFinisherPart.ArmFarHand, GilbertFinisherPart.LegNearFoot, GilbertFinisherPart.LegFarFoot })
                Assert.That(proto.Attacker.Parts.ContainsKey(p) || proto.Victim.Parts.ContainsKey(p), Is.False, $"{p} not keyed");
            Assert.That(proto.Events.Select(e => e.Time), Is.Ordered, "events must be sorted");
            Assert.That(proto.Events.Select(e => e.Beat).Where(b => b != null),
                Is.SupersetOf(new[] { "grabs the head", "knee", "grabs the shoulders", "headbutt", "falls on the back", "stomp",
                    "stomp 2", "stomp 3", "stomp 4", "stomp 5", "stomp 6", "temple kick", "flies", "lands", "walks off", "end" }));
            Assert.That(proto.Events.Count(e => e.Beat != null && e.Beat.StartsWith("stomp")), Is.EqualTo(6), "stomp + 5 more");

            foreach (var role in new[] { GilbertFinisherRole.Attacker, GilbertFinisherRole.Victim })
            {
                var tracks = GilbertFinisherTimeline.Tracks(proto, role);
                Assert.That(tracks.Move.Select(k => k.Time), Is.Ordered, $"{role} move keys");
                foreach (var (part, track) in tracks.Parts)
                    Assert.That(track.Keys.Select(k => k.Time), Is.Ordered, $"{role} {part} keys");
            }

            // Victim ends lying (tumbled half a turn by the temple kick) off to the side; attacker walks off past it.
            var victimEnd = GilbertFinisherTimeline.Evaluate(proto.Victim.Parts[GilbertFinisherPart.Body].Keys, proto.Length);
            Assert.That(victimEnd.Rot, Is.EqualTo(-270f));
            var aEnd = GilbertFinisherTimeline.Evaluate(proto.Attacker.Move, proto.Length).X;
            var vEndPose = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, proto.Length);
            Assert.That(aEnd, Is.GreaterThan(vEndPose.X + 0.6f));
            Assert.That(vEndPose.Y, Is.InRange(0.5f, 1.5f), "kicked aside");
            Assert.That(proto.RequiredRoom, Is.GreaterThanOrEqualTo(aEnd));

            // Kick flight: 1-2 tiles, declared so the server can shorten it.
            Assert.That(proto.Victim.Flight, Is.Not.Null);
            var fl = proto.Victim.Flight!;
            var p0 = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, fl.Start);
            var p1 = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, fl.End);
            Assert.That(new Vector2(p1.X - p0.X, p1.Y - p0.Y).Length(), Is.InRange(1f, 2f));
            Assert.That(proto.Events.Any(e => e.Beat == "temple kick" && Math.Abs(e.Time - fl.Start) < 0.01f));
            // Temple kick with the back to the camera, then turned back for the walk-off.
            Assert.That(GilbertFinisherTimeline.ViewAt(proto.Attacker, fl.Start), Is.EqualTo(Direction.North));
            Assert.That(GilbertFinisherTimeline.ViewAt(proto.Attacker, 1f), Is.Null);
            Assert.That(GilbertFinisherTimeline.ViewAt(proto.Attacker, proto.Events.First(e => e.Beat == "walks off").Time), Is.Null);
            // Kicked away up-screen (behind Gilbert, who stands below the victim's head).
            Assert.That(p1.Y - p0.Y, Is.GreaterThan(Math.Abs(p1.X - p0.X)));
            Assert.That(GilbertFinisherTimeline.Evaluate(proto.Attacker.Move, fl.Start).Y, Is.LessThan(p0.Y - 0.1f));

            // Concussion: dazed (with stars) after the knee, recovered before the shoulder grab.
            var grab = proto.Events.First(e => e.Beat == "grabs the shoulders").Time;
            var knee = proto.Events.First(e => e.Beat == "knee").Time;
            Assert.That(GilbertFinisherTimeline.DazeWeight(proto.Victim, knee + 0.6f), Is.GreaterThan(0.6f));
            Assert.That(GilbertFinisherTimeline.Stars(proto.Victim, knee + 0.6f), Is.Not.Empty);
            Assert.That(GilbertFinisherTimeline.DazeWeight(proto.Victim, grab), Is.EqualTo(0f));
            Assert.That(GilbertFinisherTimeline.Stars(proto.Victim, grab), Is.Empty);

            // Smooth spline passes through the keys.
            foreach (var (part, track) in proto.Victim.Parts)
            foreach (var k in track.Keys.Where(k => track.Keys.Count(o => o.Time == k.Time) == 1))
                Assert.That(GilbertFinisherTimeline.Evaluate(track.Keys, k.Time, true).Rot, Is.EqualTo(k.Rot).Within(0.01f), $"{part} @ {k.Time}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CommandRunsLocksAndRestores()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);

        var sEnt = server.EntMan;
        var blocker = server.System<ActionBlockerSystem>();
        var xformSys = server.System<SharedTransformSystem>();
        var standing = server.System<StandingStateSystem>();
        var console = server.ResolveDependency<IConsoleHost>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<GilbertFinisherPrototype>(GilbertFinisherSystem.DefaultFinisher);

        EntityUid attacker = default, victim = default;
        Vector2 start = default;
        await server.WaitPost(() =>
        {
            var coords = map.GridCoords;
            attacker = GilbertTestUtil.SpawnActive(sEnt, Gilbert, coords);
            victim = sEnt.SpawnEntity(Victim, coords.Offset(new Vector2(1.3f, 0.2f)));
            start = xformSys.GetWorldPosition(attacker);
        });
        await RunSeconds(pair, 0.3);

        // Bad input is refused (the test console turns WriteError into a failure, so check through the system).
        await server.WaitPost(() =>
        {
            var sys = server.System<GilbertFinisherSystem>();
            Assert.That(sys.TryStart(attacker, attacker, out var err), Is.False);
            Assert.That(err, Is.EqualTo("cmd-gilbertfinisher-same"));
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(attacker), Is.False);
        });

        // Baseline of Gilbert's layers (some systems keep non-zero offsets by design, e.g. the stam-crit layer).
        var cEnt0 = client.EntMan;
        var cAttacker0 = pair.ToClientUid(attacker);
        var baseline = new Dictionary<SpriteComponent.Layer, (Vector2 Offset, double Rot, bool Visible)>();
        Vector2 baseSpriteOffset = default;
        await client.WaitPost(() =>
        {
            var sprite = cEnt0.GetComponent<SpriteComponent>(cAttacker0);
            baseSpriteOffset = sprite.Offset;
            foreach (var layer in sprite.AllLayers.Cast<SpriteComponent.Layer>())
                baseline[layer] = (layer.Offset, layer.Rotation.Degrees, layer.Visible);
        });

        await server.WaitPost(() =>
        {
            console.ExecuteCommand($"gilbertfinisher {sEnt.GetNetEntity(attacker)} {sEnt.GetNetEntity(victim)}");
        });
        var sTiming = server.ResolveDependency<IGameTiming>();
        var cmdTime = sTiming.CurTime;
        await RunSeconds(pair, 0.1);

        // Gilbert holds his shotgun: the holster intro plays first and the timeline starts after it.
        var intro = 0f;
        TimeSpan startTime = default;
        await server.WaitAssertion(() =>
        {
            var comp = sEnt.GetComponent<GilbertFinisherComponent>(attacker);
            intro = comp.Intro;
            startTime = comp.StartTime;
            Assert.That(intro, Is.EqualTo(proto.Intro), "held item -> holster intro");
            Assert.That(sEnt.GetComponent<GilbertFinisherComponent>(victim).Intro, Is.EqualTo(intro));
            Assert.That((comp.StartTime - cmdTime).TotalSeconds, Is.EqualTo(intro).Within(0.1), "timeline shifted by the intro");
        });

        // Runs the server/client until the given animation time of the timeline.
        async Task RunToAnim(float anim)
        {
            var real = (float) (sTiming.CurTime - startTime).TotalSeconds;
            await RunSeconds(pair, Math.Max(0f, GilbertFinisherTimeline.RealTime(proto, anim) - real));
        }

        // Mid-swing: the shotgun is drawn in the hand (its own layer), not yet on the back.
        await RunToAnim(-proto.Intro + 0.25f);
        await client.WaitAssertion(() =>
        {
            var vis = client.EntMan.System<GilbertFinisherVisualsSystem>();
            var held = vis.GetPuppetLayer(pair.ToClientUid(attacker), GilbertPuppetGroup.HeldNear);
            var slung = vis.GetPuppetLayer(pair.ToClientUid(attacker), GilbertPuppetGroup.Slung);
            Assert.That(held, Is.Not.Null, "in-hand item baked");
            Assert.That(held!.Visible, Is.True, "still in the hand mid-swing");
            Assert.That(slung, Is.Not.Null, "on-back item baked");
            Assert.That(slung!.Visible, Is.False);
        });
        await RunToAnim(0f);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(attacker), Is.True, "command started the finisher");
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(victim), Is.True);
            Assert.That(blocker.CanMove(attacker), Is.False, "attacker locked");
            Assert.That(blocker.CanMove(victim), Is.False, "victim locked");
            Assert.That(blocker.CanAttack(victim), Is.False);
            var d = xformSys.GetWorldPosition(victim) - xformSys.GetWorldPosition(attacker);
            Assert.That(d.X, Is.EqualTo(proto.VictimStart).Within(0.05f), "victim pulled in front of Gilbert");
            Assert.That(Math.Abs(d.Y), Is.LessThan(0.05f));
        });

        // Mid-cutscene the client animates the sprite layers.
        await RunSeconds(pair, 0.8);
        var cEnt = client.EntMan;
        var cVictim = pair.ToClientUid(victim);
        var cAttacker = pair.ToClientUid(attacker);
        await client.WaitAssertion(() =>
        {
            Assert.That(cEnt.HasComponent<GilbertFinisherComponent>(cVictim), Is.True, "component networked");
            Assert.That(cEnt.System<GilbertFinisherVisualsSystem>().IsAnimating(cAttacker), Is.True);
            var sprite = cEnt.GetComponent<SpriteComponent>(cAttacker);
            var spriteSys = cEnt.System<SpriteSystem>();
            var vis = cEnt.System<GilbertFinisherVisualsSystem>();

            // Side view: the sprite is baked into per-limb puppet layers and the originals are hidden.
            var knee = vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.LegNear);
            Assert.That(knee, Is.Not.Null, "attacker puppeted (side view)");
            Assert.That(spriteSys.LayerMapTryGet((cAttacker, sprite), GilbertFinisherVisualsSystem.PuppetKeyPrefix + "LegNear", out _, false), Is.True);
            Assert.That(Math.Abs(knee!.Rotation.Degrees), Is.GreaterThan(5), "knee leg is raised around t=0.9");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.ArmFar), Is.Not.Null, "solid far arm");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.Torso), Is.Not.Null);
            // Split limbs: the shin keeps the full leg length instead of shrinking.
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.LegNearLow), Is.Not.Null, "lower leg layer");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.ArmNearLow), Is.Not.Null, "forearm layer");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.ArmNearHand), Is.Null, "arms: upper arm + forearm (with hand), no hand layer");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.LegNearFoot), Is.Null, "legs: thigh + shin (with shoe), no foot layer");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.HeldNear)!.Visible, Is.False, "holstered after the intro");
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.Slung)!.Visible, Is.True, "shotgun on the back");

            var hidden = vis.GetPuppetHiddenLayers(cAttacker);
            Assert.That(hidden.Count, Is.GreaterThan(10));
            Assert.That(hidden.All(l => !l.Visible), "baked originals hidden");
            Assert.That(spriteSys.LayerMapTryGet((cAttacker, sprite), HumanoidVisualLayers.Chest, out var chest, false), Is.True);
            Assert.That(hidden, Does.Contain((SpriteComponent.Layer) sprite[chest]));

            // Mask-derived joints: shoulder above the hip, neck above the shoulder; the hidden far arm is synthesized.
            var bake = vis.GetPuppetResult(cAttacker);
            Assert.That(bake, Is.Not.Null);
            Assert.That(bake!.SynthesizedFarArm, Is.True);
            Assert.That(bake.Pivots[GilbertPuppetGroup.ArmNear].Y, Is.GreaterThan(bake.Pivots[GilbertPuppetGroup.LegNear].Y));
            Assert.That(bake.Pivots[GilbertPuppetGroup.Head].Y, Is.GreaterThan(bake.Pivots[GilbertPuppetGroup.ArmNear].Y));
            // Shoulder > elbow > wrist, hip > knee > sole, and the segments cover the whole limb (no lost rows).
            foreach (var (up, low, tip) in new[]
                     {
                         (GilbertPuppetGroup.ArmNear, GilbertPuppetGroup.ArmNearLow, GilbertPuppetGroup.ArmNearHand),
                         (GilbertPuppetGroup.ArmFar, GilbertPuppetGroup.ArmFarLow, GilbertPuppetGroup.ArmFarHand),
                         (GilbertPuppetGroup.LegNear, GilbertPuppetGroup.LegNearLow, GilbertPuppetGroup.LegNearFoot),
                     })
            {
                var leg = up == GilbertPuppetGroup.LegNear;
                Assert.That(bake.Pivots[up].Y, Is.GreaterThan(bake.Pivots[low].Y), $"{low} below {up}");
                Assert.That(bake.Pivots[low].Y, Is.GreaterThan(bake.Pivots[tip].Y), $"{tip} below {low}");
                var seg = bake.Segments[up];
                Assert.That(seg.End - seg.Joint, Is.GreaterThanOrEqualTo(7), $"{up} full length");
                Assert.That(bake.Images[(int) low].Length, Is.GreaterThan(0));
                if (leg)
                {
                    // Two segments, knee half-way: thigh and shin + shoe both get real length.
                    Assert.That(seg.Tip, Is.EqualTo(seg.End), "leg has no separate foot segment");
                    Assert.That(seg.Mid - seg.Joint, Is.GreaterThanOrEqualTo(3), "thigh");
                    Assert.That(seg.End - seg.Mid, Is.GreaterThanOrEqualTo(3), "shin + shoe");
                }
                else
                {
                    // Two rigid segments, elbow at 2/5: the forearm carries the hand (the wrist pivot is only the grip reference).
                    Assert.That(seg.Mid - seg.Joint, Is.GreaterThanOrEqualTo(2), $"{up} upper arm");
                    Assert.That(seg.End - seg.Mid, Is.GreaterThanOrEqualTo(4), $"{up} forearm + hand");
                    Assert.That(bake.Images[(int) tip].Where((b, i) => i % 4 == 3).All(a => a == 0), $"{tip} is empty (no wrist)");
                }
            }

            // The victim is puppeted too.
            Assert.That(vis.GetPuppetLayer(cVictim, GilbertPuppetGroup.Head), Is.Not.Null, "victim puppeted");
        });

        await RunSeconds(pair, 2.0);
        await server.WaitAssertion(() =>
        {
            Assert.That(blocker.CanMove(attacker), Is.False, "still locked at t~3s");
            Assert.That(blocker.CanMove(victim), Is.False);
        });

        // Temple kick: back to the camera (direction override), turned back afterwards.
        var kick = proto.Victim.Flight!.Start;
        await RunToAnim(kick);
        await client.WaitAssertion(() =>
        {
            var sprite = cEnt.GetComponent<SpriteComponent>(cAttacker);
            Assert.That(sprite.EnableDirectionOverride, Is.True, "back view forced");
            Assert.That(sprite.DirectionOverride, Is.EqualTo(Direction.North));
            Assert.That(cEnt.System<GilbertFinisherVisualsSystem>().GetPuppetLayer(cAttacker, GilbertPuppetGroup.LegFar), Is.Not.Null,
                "back view puppeted too");
        });
        await RunToAnim(proto.Events.First(e => e.Beat == "walks off").Time);
        await client.WaitAssertion(() =>
        {
            var sprite = cEnt.GetComponent<SpriteComponent>(cAttacker);
            Assert.That(sprite.EnableDirectionOverride, Is.False, "turned back: override restored");
        });

        // Just before the end (still locked): positions follow the move tracks.
        await RunSeconds(pair, GilbertFinisherTimeline.TotalLength(proto) - 0.35f
                               - (float) (sTiming.CurTime - startTime).TotalSeconds);
        await server.WaitAssertion(() =>
        {
            var comp = sEnt.GetComponent<GilbertFinisherComponent>(attacker);
            var t = (float) (sTiming.CurTime - comp.StartTime).TotalSeconds;
            Assert.That(t, Is.LessThan(GilbertFinisherTimeline.TotalLength(proto)));
            t = GilbertFinisherTimeline.AnimTime(proto, t);
            var aPos = xformSys.GetWorldPosition(attacker) - start;
            var vPos = xformSys.GetWorldPosition(victim) - start;
            var aEnd = GilbertFinisherTimeline.Evaluate(proto.Attacker.Move, t).X;
            var vEnd = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, t).X;
            Assert.That(aPos.X, Is.EqualTo(aEnd).Within(0.1f), $"Gilbert walked forward past the victim (t={t})");
            var vEndY = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, t).Y;
            Assert.That(t, Is.GreaterThan(proto.Victim.Flight!.End), "checked after the kick flight");
            Assert.That(sEnt.GetComponent<GilbertFinisherComponent>(victim).FlightScale, Is.EqualTo(1f), "open floor: full flight");
            Assert.That(vPos.X, Is.EqualTo(vEnd).Within(0.1f), "victim knocked down and kicked aside");
            Assert.That(vPos.Y, Is.EqualTo(vEndY).Within(0.1f));
            Assert.That(aPos.X, Is.GreaterThan(vPos.X + 0.5f));
            Assert.That(aPos.Y, Is.EqualTo(GilbertFinisherTimeline.Evaluate(proto.Attacker.Move, t).Y).Within(0.1f));

            // The finisher is standalone; switch Gilbert's brain off so his AI does not lunge at the
            // victim right after the unlock and mess up the sprite-restore checks below.
            sEnt.RemoveComponent<GilbertAIComponent>(attacker);
            sEnt.RemoveComponent<HTNComponent>(attacker);
        });

        await RunSeconds(pair, 0.5);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(attacker), Is.False, "finished");
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(victim), Is.False);
            Assert.That(blocker.CanMove(attacker), Is.True, "attacker unlocked");
            Assert.That(blocker.CanAttack(attacker), Is.True);
            Assert.That(standing.IsDown(victim), Is.True, "victim left lying");
        });

        await RunSeconds(pair, 0.3);
        await client.WaitAssertion(() =>
        {
            var vis = cEnt.System<GilbertFinisherVisualsSystem>();
            Assert.That(vis.IsAnimating(cAttacker), Is.False);
            Assert.That(vis.IsAnimating(cVictim), Is.False);
            var sprite = cEnt.GetComponent<SpriteComponent>(cAttacker);
            Assert.That((sprite.Offset - baseSpriteOffset).Length(), Is.LessThan(0.001f), "no stuck sprite offset");
            Assert.That(sprite.Rotation.Degrees, Is.EqualTo(0).Within(0.01));
            var checkedLayers = 0;
            foreach (var layer in sprite.AllLayers.Cast<SpriteComponent.Layer>())
            {
                if (!baseline.TryGetValue(layer, out var b))
                {
                    // Added after the start (e.g. stun/status layers); we never touched it, just no rotation.
                    Assert.That(layer.Rotation.Degrees, Is.EqualTo(0).Within(0.01), "new layer rotated");
                    continue;
                }
                checkedLayers++;
                Assert.That(layer.Rotation.Degrees, Is.EqualTo(b.Rot).Within(0.01), "no stuck layer rotation");
                Assert.That((layer.Offset - b.Offset).Length(), Is.LessThan(0.001f), "no stuck layer offset");
                // Shader layers (Gilbert's blinking eye glow) toggle on their own and are never hidden by the puppet.
                if (layer.ShaderPrototype == null && layer.Shader == null)
                    Assert.That(layer.Visible, Is.EqualTo(b.Visible), "layer visibility restored");
            }
            Assert.That(checkedLayers, Is.GreaterThan(20));
            Assert.That(vis.GetPuppetLayer(cAttacker, GilbertPuppetGroup.Torso), Is.Null);
            var spriteSys = cEnt.System<SpriteSystem>();
            foreach (var group in Enum.GetValues<GilbertPuppetGroup>())
                Assert.That(spriteSys.LayerMapTryGet((cAttacker, sprite), GilbertFinisherVisualsSystem.PuppetKeyPrefix + group, out _, false),
                    Is.False, $"puppet layer {group} removed");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A wall beside the victim: the temple-kick flight is shortened so it lands on free floor, not in the wall.</summary>
    [Test]
    public async Task JointLimits()
    {
        await using var pair = await PoolManager.GetServerClient();
        var proto = pair.Server.ResolveDependency<IPrototypeManager>()
            .Index<GilbertFinisherPrototype>(GilbertFinisherSystem.DefaultFinisher);

        Assert.Multiple(() =>
        {
            // Keyed side-view poses: knees fold back only, elbows forward only, hips in range.
            foreach (var role in new[] { GilbertFinisherRole.Attacker, GilbertFinisherRole.Victim })
            {
                var tr = role == GilbertFinisherRole.Attacker ? proto.Attacker : proto.Victim;
                var f = GilbertFinisherTimeline.Facing(role);
                foreach (var (part, min, max) in new[]
                         {
                             (GilbertFinisherPart.LegNearLow, GilbertFinisherTimeline.KneeMin, GilbertFinisherTimeline.KneeMax),
                             (GilbertFinisherPart.LegFarLow, GilbertFinisherTimeline.KneeMin, GilbertFinisherTimeline.KneeMax),
                             (GilbertFinisherPart.ArmNearLow, GilbertFinisherTimeline.ElbowMin, GilbertFinisherTimeline.ElbowMax),
                             (GilbertFinisherPart.ArmFarLow, GilbertFinisherTimeline.ElbowMin, GilbertFinisherTimeline.ElbowMax),
                             (GilbertFinisherPart.LegNear, GilbertFinisherTimeline.HipMin, GilbertFinisherTimeline.HipMax),
                             (GilbertFinisherPart.LegFar, GilbertFinisherTimeline.HipMin, GilbertFinisherTimeline.HipMax),
                             (GilbertFinisherPart.ArmNear, GilbertFinisherTimeline.ShoulderMin, GilbertFinisherTimeline.ShoulderMax),
                             (GilbertFinisherPart.ArmFar, GilbertFinisherTimeline.ShoulderMin, GilbertFinisherTimeline.ShoulderMax),
                         })
                {
                    if (!tr.Parts.TryGetValue(part, out var track))
                        continue;
                    foreach (var k in track.Keys)
                    {
                        if (GilbertFinisherTimeline.ViewAt(tr, k.Time) != null)
                            continue;
                        Assert.That(f * k.Rot, Is.InRange(min - 0.01f, max + 0.01f), $"{role} {part} @ {k.Time}");
                    }
                }
            }

            // Runtime clamp (procedural walk / drag / daze on top of the keys).
            foreach (var f in new[] { 1f, -1f })
            {
                var pose = new System.Numerics.Vector3[GilbertFinisherTimeline.PartCount];
                pose[(int) GilbertFinisherPart.LegNearLow].Z = f * 40f;   // hyperextended / forward-bent knee
                pose[(int) GilbertFinisherPart.LegFarLow].Z = f * -170f;  // folded through the thigh
                pose[(int) GilbertFinisherPart.ArmNearLow].Z = f * -60f;  // elbow bent backwards
                pose[(int) GilbertFinisherPart.LegNear].Z = f * 160f;
                pose[(int) GilbertFinisherPart.ArmNearHand].Z = 120f;
                pose[(int) GilbertFinisherPart.ArmNear].Z = f * -150f;     // flung back past the shoulder limit
                pose[(int) GilbertFinisherPart.LegNearFoot].Z = 30f;
                GilbertFinisherTimeline.ApplyJointLimits(pose, f, true);
                Assert.That(f * pose[(int) GilbertFinisherPart.LegNearLow].Z, Is.EqualTo(GilbertFinisherTimeline.KneeMax));
                Assert.That(f * pose[(int) GilbertFinisherPart.LegFarLow].Z, Is.EqualTo(GilbertFinisherTimeline.KneeMin));
                Assert.That(f * pose[(int) GilbertFinisherPart.ArmNearLow].Z, Is.EqualTo(GilbertFinisherTimeline.ElbowMin));
                Assert.That(f * pose[(int) GilbertFinisherPart.LegNear].Z, Is.EqualTo(GilbertFinisherTimeline.HipMax));
                Assert.That(f * pose[(int) GilbertFinisherPart.ArmNear].Z, Is.EqualTo(GilbertFinisherTimeline.ShoulderMin));
                Assert.That(pose[(int) GilbertFinisherPart.ArmNearHand].Z, Is.EqualTo(0f), "arms have no wrist joint");
                Assert.That(pose[(int) GilbertFinisherPart.LegNearFoot].Z, Is.EqualTo(0f), "legs have no foot joint");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FlightShortenedByWall()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var sEnt = server.EntMan;
        var sys = server.System<GilbertFinisherSystem>();
        var xformSys = server.System<SharedTransformSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<GilbertFinisherPrototype>(GilbertFinisherSystem.DefaultFinisher);

        EntityUid a = default, v = default, wall = default;
        Vector2 start = default;
        await server.WaitPost(() =>
        {
            a = sEnt.SpawnEntity(Victim, map.GridCoords);
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1, 0)));
            // The flight goes up-screen (north, the attacker's left when facing east): wall off the row above.
            for (var x = -1; x <= 6; x++)
                wall = sEnt.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x, 1)));
            start = xformSys.GetWorldPosition(a);
        });
        await RunSeconds(pair, 0.2);
        await server.WaitPost(() => Assert.That(sys.TryStart(a, v, out var err), Is.True, err));

        var fl = proto.Victim.Flight!;
        await RunSeconds(pair, GilbertFinisherTimeline.RealTime(proto, fl.End) + 0.3);
        await server.WaitAssertion(() =>
        {
            var comp = sEnt.GetComponent<GilbertFinisherComponent>(v);
            Assert.That(sEnt.GetComponent<GilbertFinisherComponent>(a).Forward, Is.EqualTo(new Vector2(1, 0)));
            Assert.That(comp.FlightScale, Is.InRange(0f, 0.99f), "flight shortened");
            Assert.That(sEnt.GetComponent<TransformComponent>(wall).Anchored, Is.True);
            var wallFace = xformSys.GetWorldPosition(wall).Y - 0.5f;
            var vPos = xformSys.GetWorldPosition(v);
            var planned = GilbertFinisherTimeline.Evaluate(proto.Victim.Move, fl.End).Y;
            Assert.That(vPos.Y, Is.LessThanOrEqualTo(wallFace - 0.3f), "did not land in the wall");
            Assert.That(vPos.Y - start.Y, Is.GreaterThan(0f), "still knocked to the side");
            Assert.That(start.Y + planned + 0.45f, Is.GreaterThan(wallFace), "the unshortened flight would have landed against / in the wall");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StopKeepDownFalseAndDeletion()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var sEnt = server.EntMan;
        var sys = server.System<GilbertFinisherSystem>();
        var blocker = server.System<ActionBlockerSystem>();
        var standing = server.System<StandingStateSystem>();
        var console = server.ResolveDependency<IConsoleHost>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<GilbertFinisherPrototype>(GilbertFinisherSystem.DefaultFinisher);

        EntityUid a = default, v = default, a2 = default, v2 = default;
        await server.WaitPost(() =>
        {
            a = sEnt.SpawnEntity(Victim, map.GridCoords);
            v = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-1, 0)));
            a2 = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0, 4)));
            v2 = sEnt.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1, 4)));
        });
        await RunSeconds(pair, 0.2);

        // Two plain humans, keepdown=false; stop half-way through; delete a victim mid-cutscene.
        var sTiming = server.ResolveDependency<IGameTiming>();
        await server.WaitPost(() =>
        {
            Assert.That(sys.TryStart(a, v, out var err, keepDown: false), Is.True, err);
            // Empty hands: no holster intro, the timeline starts right away.
            var comp = sEnt.GetComponent<GilbertFinisherComponent>(a);
            Assert.That(comp.Intro, Is.EqualTo(0f), "nothing held -> no holster beat");
            Assert.That(comp.StartTime, Is.EqualTo(sTiming.CurTime));
            Assert.That(comp.NextEvent, Is.EqualTo(proto.Events.FindIndex(e => e.Time >= 0f)), "intro events skipped");
            Assert.That(sys.TryStart(a, v2, out _), Is.False, "busy attacker is refused");
            Assert.That(sys.TryStart(a2, v2, out var err2), Is.True, err2);
        });
        await RunSeconds(pair, 1.5);
        await server.WaitPost(() =>
        {
            console.ExecuteCommand($"gilbertfinisher stop {sEnt.GetNetEntity(v)}");
            sEnt.DeleteEntity(v2);
        });
        await RunSeconds(pair, 0.3);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(a), Is.False);
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(v), Is.False);
            Assert.That(blocker.CanMove(a), Is.True);
            Assert.That(blocker.CanMove(v), Is.True);
            Assert.That(standing.IsDown(v), Is.False, "keepdown=false leaves the victim standing");
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(a2), Is.False, "partner deleted -> attacker released");
            Assert.That(blocker.CanMove(a2), Is.True);
        });

        // Can be replayed after it ended.
        await server.WaitPost(() => Assert.That(sys.TryStart(a, v, out var err, keepDown: false), Is.True, err));
        await RunSeconds(pair, GilbertFinisherTimeline.TotalLength(proto) + 0.3);
        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.HasComponent<GilbertFinisherComponent>(a), Is.False);
            Assert.That(blocker.CanMove(v), Is.True);
        });

        await pair.CleanReturnAsync();
    }
}

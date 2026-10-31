using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.ActionBlocker;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v10: AI-triggered finisher, the barrel (V2) and ground-and-pound (V3) variants, per-part armour-ignoring damage,
/// V3 amputations, admin orders (gilbertgoto / gilberttarget).
/// </summary>
[TestFixture]
public sealed class GilbertV10Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";

    private static readonly string[] Variants =
    {
        GilbertFinisherSystem.DefaultFinisher, "GilbertFinisherBarrel", "GilbertFinisherGroundPound",
    };

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

    private static EntityCoordinates At(TestMapData map, float x, float y) => map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f));

    private static void NoAir(IEntityManager entMan, EntityUid uid) =>
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(uid);

    private static void HoldFire(IEntityManager entMan, IGameTiming timing, EntityUid gilbert)
    {
        var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
        comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
        foreach (var held in entMan.System<SharedHandsSystem>().EnumerateHeld(gilbert))
        {
            if (entMan.TryGetComponent<GunComponent>(held, out var gun))
                gun.NextFire = timing.CurTime + TimeSpan.FromHours(1);
        }
    }

    /// <summary>Damages the victim until <c>crit - total == margin</c>.</summary>
    private static void SetMargin(IEntityManager entMan, EntityUid victim, float margin)
    {
        var ai = entMan.System<GilbertAISystem>();
        var dmg = entMan.System<DamageableSystem>();
        for (var i = 0; i < 20 && ai.CritMargin(victim) is { } m && m > margin + 0.5f; i++)
        {
            var spec = new DamageSpecifier();
            spec.DamageDict["Blunt"] = FixedPoint2.New(Math.Min(10f, m.Float() - margin));
            dmg.TryChangeDamage(victim, spec, ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
        }
    }

    private static Dictionary<TargetBodyPart, FixedPoint2> PartDamage(IEntityManager entMan, EntityUid victim)
    {
        var body = entMan.System<SharedBodySystem>();
        var result = new Dictionary<TargetBodyPart, FixedPoint2>();
        foreach (var (id, part) in body.GetBodyChildren(victim))
        {
            var key = body.GetTargetBodyPart(part);
            if (entMan.TryGetComponent<DamageableComponent>(id, out var d))
                result[key] = result.GetValueOrDefault(key) + d.TotalDamage;
        }
        return result;
    }

    [Test]
    public async Task VariantPrototypesAreSane()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var sys = pair.Server.System<GilbertFinisherSystem>();

        Assert.Multiple(() =>
        {
            Assert.That(GilbertFinisherSystem.Variants, Is.EquivalentTo(Variants));
            Assert.That(sys.ResolveVariant("1"), Is.EqualTo(Variants[0]));
            Assert.That(sys.ResolveVariant("2"), Is.EqualTo(Variants[1]));
            Assert.That(sys.ResolveVariant("3"), Is.EqualTo(Variants[2]));
            Assert.That(sys.ResolveVariant("4"), Is.EqualTo(GilbertFinisherSystem.RageFinisher)); // v13: 4 = the rage finisher
            Assert.That(sys.ResolveVariant("5"), Is.Not.Null); // v13.4: 5 = RageNeck, 6 = RageHeart
            Assert.That(sys.ResolveVariant("7"), Is.Null);

            var expected = new Dictionary<string, (TargetBodyPart, string, float)[]>
            {
                [Variants[0]] = new[] { (TargetBodyPart.Head, "Blunt", 60f) },
                [Variants[1]] = new[] { (TargetBodyPart.Head, "Piercing", 90f) },
                [Variants[2]] = new[]
                {
                    (TargetBodyPart.Head, "Blunt", 60f), (TargetBodyPart.LeftLeg, "Piercing", 30f),
                    (TargetBodyPart.RightLeg, "Piercing", 30f), (TargetBodyPart.Chest, "Blunt", 40f), // v13.2: the kick (its force goes to the torso)
                },
            };

            foreach (var id in Variants)
            {
                var proto = protoMan.Index<ProcAnimScenePrototype>(id);
                Assert.That(proto.Events.Select(e => e.Time), Is.Ordered, $"{id} events sorted");
                foreach (var role in new[] { 0, 1 })
                {
                    var tracks = ProcAnimTimeline.Tracks(proto, role);
                    Assert.That(tracks.Move.Select(k => k.Time), Is.Ordered, $"{id} {role} move keys");
                    foreach (var (part, track) in tracks.Parts)
                        Assert.That(track.Keys.Select(k => k.Time), Is.Ordered, $"{id} {role} {part} keys");
                }

                var hits = proto.Events.SelectMany(e => e.Damage).ToList();
                Assert.That(hits.All(h => h.IgnoreArmor), $"{id}: every finisher hit ignores armour");
                var got = hits.SelectMany(h => h.Damage.DamageDict.Select(kv => (h.Part, kv.Key, kv.Value.Float())))
                    .OrderBy(x => x.Part).ThenBy(x => x.Key).ToList();
                Assert.That(got, Is.EqualTo(expected[id].OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList()), $"{id} damage budget");
                Assert.That(proto.RequiredRoom, Is.GreaterThan(1f), $"{id} room");
                Assert.That(ProcAnimTimeline.TotalLength(proto), Is.GreaterThanOrEqualTo(proto.Length));
            }

            var pound = protoMan.Index<ProcAnimScenePrototype>(Variants[2]);
            // v13.2: no head shot / decapitation any more - only the two arms come off.
            Assert.That(pound.Events.SelectMany(e => e.Amputate),
                Is.EquivalentTo(new[] { TargetBodyPart.LeftArm, TargetBodyPart.RightArm }));
            Assert.That(pound.Actor("victim").HoldAlive, "the victim is held alive until the neck snaps");
            Assert.That(pound.Actor("victim").Hide.Count, Is.EqualTo(2), "only the two torn arms are hidden; the head stays on");
            var legShot = pound.Events.First(e => e.Damage.Count > 0);
            Assert.That(legShot.Damage.Select(d => d.Part), Is.EquivalentTo(new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg }));
            Assert.That(legShot.Time, Is.LessThan(pound.Events.First(e => e.Amputate.Count > 0).Time), "legs shot before the arms come off");
            // The twist breaks the neck (Blunt to the head) and kills; the kick to the face is the last hit.
            var snap = pound.Events.Single(e => e.Kill);
            Assert.That(snap.Amputate, Is.Empty, "the neck break leaves the head on");
            Assert.That(snap.Damage.Select(d => d.Part), Is.EquivalentTo(new[] { TargetBodyPart.Head }));
            Assert.That(snap.Damage.SelectMany(d => d.Damage.DamageDict.Keys), Is.EquivalentTo(new[] { "Blunt" }));
            Assert.That(snap.Sound, Is.Not.Null, "an audible crack");
            Assert.That(pound.Events.Where(e => e.Amputate.Count > 0).All(e => e.Time < snap.Time));
            var kick = pound.Events.Last(e => e.Damage.Count > 0);
            Assert.That(kick.Time, Is.GreaterThan(snap.Time));
            // The kick lands on the face (its blood comes from the head), its force goes to the torso: the head is at its integrity
            // limit after the snap and a second head hit would roll a random dismemberment.
            Assert.That(kick.Blood?.From, Is.EqualTo(ProcAnimBone.Head), "the kick lands on the face");
            Assert.That(kick.Damage.Select(d => d.Part), Is.EquivalentTo(new[] { TargetBodyPart.Chest }), "the kick's force goes to the torso");
            Assert.That(kick.Damage.SelectMany(d => d.Damage.DamageDict.Keys), Is.EquivalentTo(new[] { "Blunt" }));
            // The truncheon prop: drawn and stowed by Gilbert, lodged in the mouth in between.
            Assert.That(pound.Actor("attacker").Props.Count, Is.EqualTo(2));
            Assert.That(pound.Actor("victim").Props.Count, Is.EqualTo(1));
            foreach (var prop in pound.Actor("attacker").Props.Concat(pound.Actor("victim").Props))
            {
                Assert.That(prop.State, Is.EqualTo("truncheon"));
                Assert.That(prop.End, Is.GreaterThan(prop.Start));
                Assert.That(prop.Keys, Is.Not.Empty);
            }
            Assert.That(pound.Actor("attacker").UnholsterAt, Is.Null, "v13.2: no head shot - the shotgun stays on his back");
            Assert.That(pound.Events.Where(e => e.Kill || e.Amputate.Count > 0 || e.Damage.Count > 0).All(e => e.Damage.All(d => d.IgnoreArmor)));
            var barrel = protoMan.Index<ProcAnimScenePrototype>(Variants[1]);
            Assert.That(barrel.Actor("attacker").HolsterHeldItems, Is.False, "the barrel variant keeps the shotgun");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>All variants: joint limits hold outside the turned (side / back) views.</summary>
    [Test]
    public async Task VariantJointLimits()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();
        var limits = new Dictionary<ProcAnimBone, (float Min, float Max)>
        {
            [ProcAnimBone.LegNearLow] = (ProcAnimTimeline.KneeMin, ProcAnimTimeline.KneeMax),
            [ProcAnimBone.LegFarLow] = (ProcAnimTimeline.KneeMin, ProcAnimTimeline.KneeMax),
            [ProcAnimBone.ArmNearLow] = (ProcAnimTimeline.ElbowMin, ProcAnimTimeline.ElbowMax),
            [ProcAnimBone.ArmFarLow] = (ProcAnimTimeline.ElbowMin, ProcAnimTimeline.ElbowMax),
            [ProcAnimBone.LegNear] = (ProcAnimTimeline.HipMin, ProcAnimTimeline.HipMax),
            [ProcAnimBone.LegFar] = (ProcAnimTimeline.HipMin, ProcAnimTimeline.HipMax),
            [ProcAnimBone.ArmNear] = (ProcAnimTimeline.ShoulderMin, ProcAnimTimeline.ShoulderMax),
            [ProcAnimBone.ArmFar] = (ProcAnimTimeline.ShoulderMin, ProcAnimTimeline.ShoulderMax),
        };

        Assert.Multiple(() =>
        {
            foreach (var id in Variants)
            {
                var proto = protoMan.Index<ProcAnimScenePrototype>(id);
                foreach (var (role, track, f) in new[] { ("attacker", proto.Actor("attacker"), 1f), ("victim", proto.Actor("victim"), -1f) })
                foreach (var (part, (min, max)) in limits)
                {
                    if (!track.Parts.TryGetValue(part, out var t))
                        continue;
                    foreach (var k in t.Keys)
                    {
                        if (ProcAnimTimeline.ViewAt(track, k.Time) != null)
                            continue;
                        Assert.That(f * k.Rot, Is.InRange(min - 0.01f, max + 0.01f), $"{id} {role} {part} @ {k.Time}");
                    }
                }
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Each variant runs through the command, damages the victim, restores both and (V3) tears both arms off.</summary>
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    public async Task VariantRunsAndRestores(int variant)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var console = server.ResolveDependency<IConsoleHost>();
        var timing = server.ResolveDependency<IGameTiming>();
        var sys = server.System<GilbertFinisherSystem>();
        var body = server.System<SharedBodySystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(Variants[variant - 1]);

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            HoldFire(entMan, timing, g);
            v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            NoAir(entMan, v);
        });
        await RunSeconds(pair, 0.3);

        await server.WaitPost(() => console.ExecuteCommand($"gilbertfinisher {entMan.GetNetEntity(g)} {entMan.GetNetEntity(v)} {variant}"));
        await RunSeconds(pair, 0.1);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g) && sys.IsPlaying(v), "cutscene running");
            Assert.That(entMan.GetComponent<ProcAnimComponent>(g).Scene.Id, Is.EqualTo(proto.ID));
        });

        var elapsed = 0.0;
        if (variant == 3)
        {
            // Legs shot, both arms torn off, baton rammed in: still alive (not even crit) right before the neck snaps.
            var shot = proto.Events.Single(e => e.Kill).Time;
            elapsed = proto.Intro + ProcAnimTimeline.RealTime(proto, shot - 0.3f);
            await RunSeconds(pair, elapsed);
            await server.WaitAssertion(() =>
            {
                var state = entMan.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(v).CurrentState;
                var parts = body.GetBodyChildren(v).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
                TestContext.Out.WriteLine($"[v10b] before the twist: {state}, damage {entMan.GetComponent<DamageableComponent>(v).TotalDamage}, parts {string.Join(",", parts)}");
                Assert.That(sys.IsPlaying(v), "still in the cutscene");
                Assert.That(state, Is.EqualTo(Content.Shared.Mobs.MobState.Alive), "held alive until the neck snaps");
                Assert.That(parts, Does.Contain(TargetBodyPart.Head));
                Assert.That(parts, Does.Not.Contain(TargetBodyPart.LeftArm).And.Not.Contain(TargetBodyPart.RightArm));
            });
        }

        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) + proto.Intro + 1.5 - elapsed);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g) || sys.IsPlaying(v), Is.False, "restored");
            Assert.That(server.System<ActionBlockerSystem>().CanMove(g), "Gilbert can move again");
            Assert.That(entMan.GetComponent<DamageableComponent>(v).TotalDamage, Is.GreaterThan(FixedPoint2.Zero), "victim hurt");
            var parts = body.GetBodyChildren(v).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            var armsGone = !parts.Contains(TargetBodyPart.LeftArm) && !parts.Contains(TargetBodyPart.RightArm);
            TestContext.Out.WriteLine($"[v10] variant {variant}: damage {entMan.GetComponent<DamageableComponent>(v).TotalDamage}, parts {string.Join(",", parts)}");
            Assert.That(armsGone, Is.EqualTo(variant == 3), "V3 tears both arms off, the others don't");
            // v10.1: every variant ends with the final 200 asphyxiation.
            Assert.That(entMan.GetComponent<DamageableComponent>(v).Damage.DamageDict.GetValueOrDefault("Asphyxiation"),
                Is.GreaterThanOrEqualTo(FixedPoint2.New(200)), "final asphyxiation");
            if (variant == 3)
            {
                Assert.That(parts, Does.Contain(TargetBodyPart.Head), "v13.2: V3 breaks the neck - the head stays on");
                Assert.That(entMan.GetComponent<Content.Shared.Mobs.Components.MobStateComponent>(v).CurrentState, Is.EqualTo(Content.Shared.Mobs.MobState.Dead), "V3 kills");
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Per-part damage of every variant goes through a helmet + bulletproof vest unchanged.</summary>
    [Test]
    public async Task PerPartDamageIgnoresArmor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var entMan = server.EntMan;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var sys = server.System<GilbertFinisherSystem>();
        var inv = server.System<InventorySystem>();

        foreach (var id in Variants)
        {
            var proto = protoMan.Index<ProcAnimScenePrototype>(id);
            await server.WaitAssertion(() =>
            {
                var v = entMan.SpawnEntity(Victim, At(map, 1, 0));
                NoAir(entMan, v);
                var thresholds = entMan.System<MobThresholdSystem>();
                thresholds.SetMobStateThreshold(v, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
                thresholds.SetMobStateThreshold(v, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
                var helmet = entMan.SpawnEntity("ClothingHeadHelmetShogkk", At(map, 1, 0));
                var vest = entMan.SpawnEntity("ClothingOuterArmorBulletproof", At(map, 1, 0));
                Assert.That(inv.TryEquip(v, helmet, "head", silent: true, force: true), "helmet on");
                Assert.That(inv.TryEquip(v, vest, "outerClothing", silent: true, force: true), "vest on");

                var before = PartDamage(entMan, v);
                var total0 = entMan.GetComponent<DamageableComponent>(v).TotalDamage;
                var expected = new Dictionary<TargetBodyPart, FixedPoint2>();
                foreach (var hit in proto.Events.SelectMany(e => e.Damage))
                {
                    sys.ApplyHit(v, v, hit);
                    expected[hit.Part] = expected.GetValueOrDefault(hit.Part) + hit.Damage.GetTotal();
                }

                var after = PartDamage(entMan, v);
                var total = entMan.GetComponent<DamageableComponent>(v).TotalDamage - total0;
                TestContext.Out.WriteLine($"[v10] {id}: " + string.Join(", ", after.Select(kv => $"{kv.Key} +{kv.Value - before.GetValueOrDefault(kv.Key)}")) + $"; body +{total}");
                // A part holds at most its integrity (head 65): what doesn't fit spills into the chest.
                var spill = 0f;
                foreach (var (part, amount) in expected)
                {
                    var got = (after.GetValueOrDefault(part) - before.GetValueOrDefault(part)).Float();
                    if (part == TargetBodyPart.Chest)
                        continue;
                    Assert.That(got, Is.GreaterThanOrEqualTo(Math.Min(amount.Float(), 60f) - 0.05f), $"{id}: {part} took the hit through the armour");
                    Assert.That(got, Is.LessThanOrEqualTo(amount.Float() + 0.05f), $"{id}: {part}");
                    spill += amount.Float() - got;
                }
                var chest = (after.GetValueOrDefault(TargetBodyPart.Chest) - before.GetValueOrDefault(TargetBodyPart.Chest)).Float();
                Assert.That(chest, Is.EqualTo(expected.GetValueOrDefault(TargetBodyPart.Chest).Float() + spill).Within(0.1f), $"{id}: chest = own hit + overflow");
                Assert.That(total.Float(), Is.EqualTo(expected.Values.Sum(x => x.Float())).Within(0.05f), $"{id}: body total = sum of the hits");
                entMan.DeleteEntity(v);
            });
        }

        await pair.CleanReturnAsync();
    }

    /// <summary>His own point-blank shot leaves the target near crit: he runs up and plays a finisher.</summary>
    [Test]
    public async Task AiTriggersFinisherAfterCloseShot()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var sys = server.System<GilbertFinisherSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid g = default, v = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            v = entMan.SpawnEntity(Victim, At(map, 3, 0));
            NoAir(entMan, v);
        });
        await RunSeconds(pair, 0.5);

        await server.WaitPost(() =>
        {
            HoldFire(entMan, timing, g);
            SetMargin(entMan, v, 15);
            ai.ForceCombat(g, v);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            Assert.That(ai.CritMargin(v)!.Value.Float(), Is.InRange(10f, 20f));
            Assert.That(ai.FinisherEligible((g, comp), v, timing.CurTime), "margin 15 is eligible");
            ai.NoteOwnShot((g, comp));
            Assert.That(comp.FinisherShot, Is.Not.Null, "shot at 3 tiles remembered");
        });

        var started = false;
        for (var i = 0; i < 24 && !started; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() => started = sys.IsPlaying(g) || entMan.GetComponent<GilbertAIComponent>(g).LastFinisher != null);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            TestContext.Out.WriteLine($"[v10] AI finisher started={started} variant={comp.LastFinisher}");
            Assert.That(started, "ran up and started the finisher within 6 s");
            Assert.That(Variants, Does.Contain(comp.LastFinisher));
            Assert.That(comp.FinisherCooldowns.ContainsKey(v), "per-victim cooldown set");
            Assert.That(ai.FinisherEligible((g, comp), v, timing.CurTime, checkMargin: false), Is.False, "not twice in a row");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Margin above 20, a shot from beyond 4 tiles and berserk: no finisher.</summary>
    [Test]
    public async Task AiFinisherNegatives()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid g = default, near = default, far = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            near = entMan.SpawnEntity(Victim, At(map, 2, 0));
            far = entMan.SpawnEntity(Victim, At(map, -6, 0));
            NoAir(entMan, near);
            NoAir(entMan, far);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            comp.FinisherClearRadius = 0f; // two potential victims here: the "other hostile near" rule is not under test
        });
        await RunSeconds(pair, 0.5);

        await server.WaitAssertion(() =>
        {
            HoldFire(entMan, timing, g);
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            SetMargin(entMan, near, 30);
            Assert.That(ai.CritMargin(near)!.Value.Float(), Is.GreaterThan(20f));
            Assert.That(ai.FinisherEligible((g, comp), near, timing.CurTime), Is.False, "margin > 20");
            SetMargin(entMan, near, 15);
            Assert.That(ai.FinisherEligible((g, comp), near, timing.CurTime), "margin 15 ok (control)");

            SetMargin(entMan, far, 15);
            ai.ForceCombat(g, far);
            ai.NoteOwnShot((g, comp));
            Assert.That(comp.FinisherShot, Is.Null, "shot from 6 tiles is not a finisher shot");
        });

        await server.WaitPost(() =>
            server.System<GilbertBerserkSystem>().StartBerserk((g, entMan.GetComponent<GilbertBerserkComponent>(g))));
        await RunSeconds(pair, 0.5);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            Assert.That(ai.InBerserk(g), "berserk");
            Assert.That(ai.FinisherEligible((g, comp), near, timing.CurTime), Is.False, "never in berserk");
            comp.FinisherShot = null;
            ai.ForceCombat(g, near);
            ai.NoteOwnShot((g, comp));
            Assert.That(comp.FinisherShot, Is.Null, "berserk shot is not remembered");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>gilbertgoto / gilberttarget set and clear the orders.</summary>
    [Test]
    public async Task OrderCommands()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var console = server.ResolveDependency<IConsoleHost>();
        var xform = server.System<SharedTransformSystem>();

        EntityUid g = default, t = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            t = entMan.SpawnEntity(Victim, At(map, 3, 0));
            NoAir(entMan, t);
        });
        await RunSeconds(pair, 0.2);
        var gn = entMan.GetNetEntity(g);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            var origin = xform.GetWorldPosition(map.Grid.Owner);
            console.ExecuteCommand(FormattableString.Invariant($"gilbertgoto {gn} {origin.X + 4.5f:0.###} {origin.Y + 2.5f:0.###}"));
            Assert.That(comp.GotoOrder, Is.Not.Null);
            var mc = xform.ToMapCoordinates(comp.GotoOrder!.Value);
            Assert.That(mc.Position, Is.EqualTo(origin + new Vector2(4.5f, 2.5f)).Using<Vector2>((a, b) => (a - b).Length() < 0.01f ? 0 : 1));

            console.ExecuteCommand($"gilbertgoto {gn} 1.5 -2 {entMan.GetNetEntity(map.Grid.Owner)}");
            Assert.That(comp.GotoOrder!.Value.EntityId, Is.EqualTo(map.Grid.Owner));
            Assert.That(comp.GotoOrder!.Value.Position, Is.EqualTo(new Vector2(1.5f, -2f)));

            console.ExecuteCommand($"gilbertgoto {gn} clear");
            Assert.That(comp.GotoOrder, Is.Null);

            console.ExecuteCommand($"gilberttarget {gn} {entMan.GetNetEntity(t)}");
            Assert.That(comp.HuntTarget, Is.EqualTo(t));
            console.ExecuteCommand($"gilberttarget {gn} clear");
            Assert.That(comp.HuntTarget, Is.Null);
        });

        await pair.CleanReturnAsync();
    }
}

using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stunnable;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

public sealed partial class GilbertV13Test
{
    #region Stages 3-6: rage abilities, rage finisher

    /// <summary>Raging Gilbert with his abilities, the AI kept from starting any on its own (the test does).</summary>
    private static async Task<EntityUid> SpawnRagingQuiet(TestPair pair, TestMapData map, Vector2 at)
    {
        return await SpawnRaging(pair, map, at, abilities: true, quiet: true);
    }

    private static void Quiet(IEntityManager entMan, EntityUid gilbert)
    {
        var now = IoCManager.Resolve<IGameTiming>().CurTime;
        var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
        comp.NextAny = now + TimeSpan.FromHours(1);
        entMan.GetComponent<GilbertAIComponent>(gilbert).NextRageFinisherTry = now + TimeSpan.FromHours(1);
    }

    private static EntityUid SpawnVictim(IEntityManager entMan, TestMapData map, Vector2 at)
    {
        var v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(at));
        Sturdy(entMan, v);
        return v;
    }

    /// <summary>
    /// Stage 3: wind-up, the dash knocks the victim down and drags it into the pounce scene: chest stomp (10 Shock),
    /// head stomp (10 Shock), grabbed by the arms and thrown (10 Blunt) - really thrown at the end. 60 s cooldown.
    /// </summary>
    [Test]
    public async Task LeapPouncesStompsAndThrows()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var rage = server.System<GilbertRageAbilitiesSystem>();

        EntityUid victim = default;
        await server.WaitPost(() => victim = SpawnVictim(entMan, map, new Vector2(2.5f, 0.5f)));
        var gilbert = await SpawnRagingQuiet(pair, map, new Vector2(-3.5f, 0.5f));

        FixedPoint2 shockBase = default, bluntBase = default, asphyxBase = default;
        var started = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            shockBase = Dmg(entMan, victim, "Shock");
            bluntBase = Dmg(entMan, victim, "Blunt");
            asphyxBase = Dmg(entMan, victim, "Asphyxiation");
            started = timing.CurTime;
            Assert.That(rage.TryLeap(gilbert, victim, ignoreCooldowns: true), "leap started");
            Quiet(entMan, gilbert);
        });

        var played = false;
        var startX = 0f;
        for (var i = 0; i < 30 && !played; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() =>
            {
                played = entMan.TryGetComponent<ProcAnimComponent>(victim, out var anim) && anim.Scene.Id == "GilbertLeapPounce";
                startX = server.System<SharedTransformSystem>().GetWorldPosition(victim).X;
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(played, "the pounce scene played on the victim");
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            Assert.That(comp.LeapHits, Is.EqualTo(1));
            Assert.That(comp.NextLeap - started, Is.InRange(TimeSpan.FromSeconds(59.5), TimeSpan.FromSeconds(60.5)), "60 s cooldown");
        });

        // Read right after the throw: his rage punches (+15 Shock each) are fair game once the victim is loose.
        var thrown = false;
        var trace = "";
        for (var i = 0; i < 80 && !thrown; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() =>
            {
                thrown = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert).LeapThrows > 0
                         && !entMan.HasComponent<ProcAnimComponent>(victim);
                trace += $" {Dmg(entMan, victim, "Shock") - shockBase}{(entMan.HasComponent<ProcAnimComponent>(victim) ? "a" : "")}{entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert).LeapThrows}";
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            TestContext.Out.WriteLine($"[v13] leap: shock={Dmg(entMan, victim, "Shock")} blunt={Dmg(entMan, victim, "Blunt")} throws={comp.LeapThrows}");
            Assert.That(entMan.HasComponent<ProcAnimComponent>(victim), Is.False, "scene over");
            Assert.That(Dmg(entMan, victim, "Shock") - shockBase, Is.EqualTo(FixedPoint2.New(20)), "chest + head stomps " + trace);
            Assert.That(Dmg(entMan, victim, "Blunt") - bluntBase, Is.GreaterThanOrEqualTo(FixedPoint2.New(10)), "the throw");
            Assert.That(comp.LeapThrows, Is.EqualTo(1), "thrown for real");
            Assert.That(entMan.HasComponent<GilbertLeapThrowComponent>(victim), Is.False);
            // v13.1: the ability is not a finisher - no 200 Asphyxiation from the leap. The test map has no air, so the
            // victim's own lungs add ~1/s of background suffocation; anything the scene dealt would be far above that.
            Assert.That(Dmg(entMan, victim, "Asphyxiation") - asphyxBase, Is.LessThan(FixedPoint2.New(5)), "no asphyxiation from the leap");
        });

        // v13.1: thrown ~5 tiles forward from where he let go (the scene's flight plus the real throw).
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
        {
            var x = server.System<SharedTransformSystem>().GetWorldPosition(victim).X;
            TestContext.Out.WriteLine($"[v13.1] leap: victim x {startX:0.00} -> {x:0.00}");
            Assert.That(x - startX, Is.GreaterThan(4.3f), "thrown about 5 tiles forward");
            Assert.That(x, Is.LessThan(10.6f), "still on the floor");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>v13.1: the leap scene itself carries no Asphyxiation (only the rage finisher's final 200 does).</summary>
    [Test]
    public async Task LeapSceneDealsNoAsphyxiation()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protos = pair.Server.ResolveDependency<IPrototypeManager>();
        await pair.Server.WaitAssertion(() =>
        {
            var leap = protos.Index<ProcAnimScenePrototype>("GilbertLeapPounce");
            foreach (var actor in leap.Actors)
                Assert.That(actor.FinalDamage?.DamageDict.ContainsKey("Asphyxiation") ?? false, Is.False, "final damage");
            foreach (var ev in leap.Events)
            foreach (var hit in ev.Damage)
                Assert.That(hit.Damage.DamageDict.ContainsKey("Asphyxiation"), Is.False, $"event {ev.Beat} at {ev.Time}");
            var rage = protos.Index<ProcAnimScenePrototype>(GilbertFinisherSystem.RageFinisher);
            Assert.That(rage.Actors.Any(a => a.FinalDamage != null && a.FinalDamage.DamageDict.TryGetValue("Asphyxiation", out var asphyx) && asphyx == FixedPoint2.New(200)),
                "the rage finisher keeps its 200");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Stage 4: the red cone hits what is in front (15 Shock, 3 s stun, red crackle for 3 s), not what is behind.
    /// 30 s cooldown.
    /// </summary>
    [Test]
    public async Task ConeShocksAndStunsWhatIsInFront()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var rage = server.System<GilbertRageAbilitiesSystem>();

        EntityUid front = default, behind = default;
        await server.WaitPost(() =>
        {
            front = SpawnVictim(entMan, map, new Vector2(5.5f, 0.5f));
            behind = SpawnVictim(entMan, map, new Vector2(-6.5f, 0.5f));
        });
        var gilbert = await SpawnRagingQuiet(pair, map, new Vector2(-2.5f, 0.5f));

        // The rage burst (when he entered phase 2) already crackled them: measure from here.
        FixedPoint2 frontBase = default, behindBase = default;
        var started = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            frontBase = Dmg(entMan, front, "Shock");
            behindBase = Dmg(entMan, behind, "Shock");
            started = timing.CurTime;
            Assert.That(rage.TryCone(gilbert, front, ignoreCooldowns: true), "cone started");
            Quiet(entMan, gilbert);
        });

        await RunSeconds(pair, 1.6);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            TestContext.Out.WriteLine($"[v13] cone: front={Dmg(entMan, front, "Shock")} behind={Dmg(entMan, behind, "Shock")}");
            Assert.That(Dmg(entMan, front, "Shock") - frontBase, Is.EqualTo(FixedPoint2.New(15)), "15 Shock");
            Assert.That(Dmg(entMan, behind, "Shock") - behindBase, Is.EqualTo(FixedPoint2.Zero), "nothing behind him");
            Assert.That(entMan.HasComponent<StunnedComponent>(front), "stunned");
            Assert.That(entMan.HasComponent<GilbertShockedComponent>(front), "crackling");
            Assert.That(comp.NextCone - started, Is.InRange(TimeSpan.FromSeconds(29.5), TimeSpan.FromSeconds(30.5)), "30 s cooldown");
        });

        await RunSeconds(pair, 3.5);
        await server.WaitAssertion(() =>
            Assert.That(entMan.HasComponent<GilbertShockedComponent>(front), Is.False, "the crackle lasts 3 s"));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Stage 5: levitation: 100 bolts in 15 s, 5 Shock each, aimed at the living around him, no stun; he comes
    /// back down after. 75 s cooldown.
    /// </summary>
    [Test]
    public async Task LevitationHundredBoltsNoStun()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var rage = server.System<GilbertRageAbilitiesSystem>();

        EntityUid a = default, b = default;
        await server.WaitPost(() =>
        {
            a = SpawnVictim(entMan, map, new Vector2(3.5f, 0.5f));
            b = SpawnVictim(entMan, map, new Vector2(-1.5f, 4.5f));
        });
        var gilbert = await SpawnRagingQuiet(pair, map, new Vector2(-1.5f, 0.5f));

        FixedPoint2 aBase = default, bBase = default;
        var started = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            aBase = Dmg(entMan, a, "Shock");
            bBase = Dmg(entMan, b, "Shock");
            started = timing.CurTime;
            Assert.That(rage.TryLevitate(gilbert, ignoreCooldowns: true), "levitation started");
            Quiet(entMan, gilbert);
        });

        await RunSeconds(pair, 8);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            Assert.That(comp.Levitating && entMan.HasComponent<GilbertLevitatingComponent>(gilbert), "in the air");
            Assert.That(comp.StrikesDone, Is.InRange(30, 70), "bolts spread over the 15 s");
            Assert.That(entMan.HasComponent<StunnedComponent>(a) || entMan.HasComponent<StunnedComponent>(b), Is.False, "bolts don't stun");
            Assert.That(comp.NextLevitate - started, Is.EqualTo(TimeSpan.FromSeconds(75)), "75 s cooldown");
        });

        // Read the moment he lands: after that his rage punches (+15 Shock) would muddle the bolt count.
        var landed = false;
        var ltrace = "";
        for (var i = 0; i < 120 && !landed; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() =>
            {
                landed = !entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert).Levitating;
                ltrace += $" {Dmg(entMan, a, "Shock") - aBase}/{Dmg(entMan, b, "Shock") - bBase}";
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            var shockA = Dmg(entMan, a, "Shock") - aBase;
            var shockB = Dmg(entMan, b, "Shock") - bBase;
            TestContext.Out.WriteLine($"[v13] levitation: strikes={comp.StrikesDone} a={shockA} b={shockB}");
            Assert.That(comp.StrikesDone, Is.EqualTo(100), "100 bolts");
            Assert.That(comp.Levitating, Is.False, "landed");
            Assert.That(entMan.HasComponent<GilbertLevitatingComponent>(gilbert), Is.False);
            Assert.That(shockA > FixedPoint2.Zero && shockB > FixedPoint2.Zero, "both got hit");
            // Mostly the living: well over half of the 100 bolts hit one of the two (5 Shock a hit, spread over the
            // body; body-part damage caps may clip the total, hence the 250 ceiling on the expectation).
            Assert.That(comp.LevitateLivingHits, Is.GreaterThanOrEqualTo(50), "living targets preferred " + ltrace);
            Assert.That((shockA + shockB).Float(), Is.GreaterThanOrEqualTo(Math.Min(5f * comp.LevitateLivingHits, 250f) - 0.01f), "5 Shock a bolt");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Stage 6: the rage finisher: palm strike, teleport behind, hands into the back, torn in half - the groin (with
    /// the legs) comes off, the victim dies.
    /// </summary>
    [Test]
    public async Task RageFinisherTearsInHalf()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var rage = server.System<GilbertRageAbilitiesSystem>();
        var body = server.System<SharedBodySystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.8f, 0.6f)));
            entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(victim);
        });
        var gilbert = await SpawnRagingQuiet(pair, map, new Vector2(0.5f, 0.5f));

        await server.WaitAssertion(() =>
        {
            Assert.That(server.System<GilbertFinisherSystem>().ResolveVariant("rage"), Is.EqualTo(GilbertFinisherSystem.RageFinisher));
            Assert.That(rage.TryRageFinisher(gilbert, victim, out var error, GilbertFinisherSystem.RageFinisher), $"started ({error})"); // v13.4: forced (else random of 3)
            Assert.That(entMan.GetComponent<ProcAnimComponent>(victim).Scene.Id, Is.EqualTo(GilbertFinisherSystem.RageFinisher));
        });

        var proto = protos.Index<ProcAnimScenePrototype>(GilbertFinisherSystem.RageFinisher);
        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) + 1.5);
        await server.WaitAssertion(() =>
        {
            var parts = body.GetBodyChildren(victim).Select(p => body.GetTargetBodyPart(p.Component)).ToList();
            TestContext.Out.WriteLine($"[v13] rage finisher: parts {string.Join(",", parts)}");
            Assert.That(entMan.HasComponent<ProcAnimComponent>(victim), Is.False, "scene over");
            Assert.That(server.System<MobStateSystem>().IsDead(victim), "dead");
            Assert.That(parts, Does.Not.Contain(TargetBodyPart.Groin), "pelvis torn off");
            Assert.That(parts, Does.Not.Contain(TargetBodyPart.LeftLeg).And.Not.Contain(TargetBodyPart.RightLeg), "legs went with it");
            Assert.That(parts, Does.Contain(TargetBodyPart.Chest).And.Contain(TargetBodyPart.Head), "torso stays");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Admin command: each ability, and the rage finisher through <c>gilbertfinisher ... rage</c>.</summary>
    [Test]
    public async Task AbilityCommands()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var console = server.ResolveDependency<IConsoleHost>();

        EntityUid victim = default;
        await server.WaitPost(() => victim = SpawnVictim(entMan, map, new Vector2(4.5f, 0.5f)));
        var gilbert = await SpawnRagingQuiet(pair, map, new Vector2(-2.5f, 0.5f));
        var g = entMan.GetNetEntity(gilbert);
        var v = entMan.GetNetEntity(victim);

        await server.WaitPost(() => console.ExecuteCommand($"gilbertability {g} cone {v}"));
        await RunSeconds(pair, 1.6);
        await server.WaitPost(() => console.ExecuteCommand($"gilbertability {g} levitate"));
        await RunSeconds(pair, 0.2);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            Assert.That(comp.Cones, Is.EqualTo(1), "cone by command");
            Assert.That(comp.Levitations, Is.EqualTo(1), "levitation by command (ignores cooldowns)");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>The AI uses them by itself: a victim in the open 6 tiles off gets one of the abilities within 5 s.</summary>
    [Test]
    public async Task AIUsesRageAbilities()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;

        EntityUid victim = default;
        await server.WaitPost(() => victim = SpawnVictim(entMan, map, new Vector2(4.5f, 0.5f)));
        var gilbert = await SpawnRaging(pair, map, new Vector2(-1.5f, 0.5f), abilities: true);

        var used = 0;
        for (var i = 0; i < 25 && used == 0; i++)
        {
            await RunSeconds(pair, 0.2);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
                used = comp.Leaps + comp.Cones + comp.Levitations;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertRageAbilitiesComponent>(gilbert);
            TestContext.Out.WriteLine($"[v13] AI: leaps={comp.Leaps} cones={comp.Cones} levitations={comp.Levitations}");
            Assert.That(used, Is.GreaterThan(0), "used an ability on a victim in sight");
        });
        await pair.CleanReturnAsync();
    }

    #endregion
}

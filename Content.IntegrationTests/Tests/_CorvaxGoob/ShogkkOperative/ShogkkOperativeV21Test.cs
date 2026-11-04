using System.Collections.Generic;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;
using Content.Server.Atmos.Components;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared._Shitmed.Body.Components;
using Content.Shared.Alert;
using Content.Shared.Damage;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ShogkkOperative;

/// <summary>
/// SHOGKK operatives, v2.1: every zombified SHOGKK (Gilbert and the operatives, via BaseMobShogkkZombified) doesn't
/// breathe and takes no pressure or cold damage; heat still burns them.
/// </summary>
[TestFixture]
public sealed class ShogkkOperativeV21Test
{
    private static readonly string[] Zombified =
    {
        "MobGilbert", "MobShogkkOperative", "MobShogkkOperativeLecter", "MobShogkkOperativeDrozd",
        "MobShogkkOperativeKammerer", "MobShogkkOperativeAkm",
    };

    private static FixedPoint2 Total(IEntityManager entMan, EntityUid uid) =>
        entMan.TryGetComponent<DamageableComponent>(uid, out var dmg) ? dmg.TotalDamage : FixedPoint2.Zero;

    private static FixedPoint2 Dmg(IEntityManager entMan, EntityUid uid, string type) =>
        entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict.GetValueOrDefault(type);

    private static string Describe(IEntityManager entMan, EntityUid uid)
    {
        var parts = new List<string>();
        foreach (var (type, val) in entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict)
        {
            if (val > FixedPoint2.Zero)
                parts.Add($"{type}={val}");
        }
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    [Test]
    public async Task SharedZombifiedComponents()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            foreach (var id in Zombified)
            {
                var proto = server.ProtoMan.Index<EntityPrototype>(id);
                foreach (var comp in new[] { "BreathingImmunity", "PressureImmunity", "RedspaceImmune", "Temperature" })
                    Assert.That(proto.Components.ContainsKey(comp), $"{id}: {comp}");

                Assert.That(proto.TryGetComponent<TemperatureComponent>(out var temp, factory));
                Assert.That(temp!.ColdDamageThreshold, Is.EqualTo(0f), $"{id}: cold threshold");
                Assert.That(temp.ColdDamage.Empty, $"{id}: no cold damage");
                Assert.That(temp.HeatDamageThreshold, Is.EqualTo(325f), $"{id}: heat threshold kept");
                Assert.That(temp.HeatDamage.DamageDict.GetValueOrDefault("Heat"), Is.EqualTo(FixedPoint2.New(1.5)), $"{id}: heat damage kept");
            }

            var human = server.ProtoMan.Index<EntityPrototype>("MobHuman");
            Assert.That(human.Components.ContainsKey("BreathingImmunity"), Is.False);
            Assert.That(human.Components.ContainsKey("PressureImmunity"), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A minute in open space at ~3 K: the operatives and Gilbert are untouched, a human isn't.</summary>
    [Test]
    public async Task VacuumAndColdMinute()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var temps = server.System<TemperatureSystem>();
        var alerts = server.System<AlertsSystem>();
        var mobState = server.System<MobStateSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        var mobs = new List<EntityUid>();
        EntityUid human = default, gilbert = default;
        var start = new Dictionary<EntityUid, FixedPoint2>();

        await server.WaitPost(() =>
        {
            // Off the grid, far apart: open space, nobody can reach anybody.
            human = entMan.SpawnEntity("MobHuman", new MapCoordinates(new Vector2(0f, 60f), map.MapId));
            gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert",
                new EntityCoordinates(map.MapUid, new Vector2(0f, -60f)));
            mobs.Add(gilbert);
            var x = -100f;
            foreach (var id in Zombified)
            {
                if (id == "MobGilbert")
                    continue;
                var op = entMan.SpawnEntity(id, new MapCoordinates(new Vector2(x, 0f), map.MapId));
                var ai = entMan.GetComponent<Content.Server._CorvaxGoob.ShogkkOperative.ShogkkOperativeAIComponent>(op);
                ai.PauseUntil = timing.CurTime + TimeSpan.FromHours(1);
                mobs.Add(op);
                x += 40f;
            }
            foreach (var m in mobs)
                start[m] = Total(entMan, m);
        });

        for (var s = 0; s < 60; s++)
        {
            await server.WaitPost(() =>
            {
                temps.ForceChangeTemperature(human, 3f);
                foreach (var m in mobs)
                    temps.ForceChangeTemperature(m, 3f);
            });
            await server.WaitRunTicks((int) Math.Ceiling(1.0 * timing.TickRate));
        }

        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[ops-v2.1] human after 60 s at 3 K in vacuum: {Describe(entMan, human)}");
            Assert.That(Total(entMan, human), Is.GreaterThan(FixedPoint2.Zero), "human control hurt");
            Assert.That(Dmg(entMan, human, "Asphyxiation"), Is.GreaterThan(FixedPoint2.Zero), "human suffocates");
            Assert.That(Dmg(entMan, human, "Cold"), Is.GreaterThan(FixedPoint2.Zero), "human freezes");
            Assert.That(alerts.IsShowingAlert(human, "LowPressure"), "human low pressure alert");

            foreach (var m in mobs)
            {
                var id = entMan.GetComponent<MetaDataComponent>(m).EntityPrototype!.ID;
                TestContext.Out.WriteLine($"[ops-v2.1] {id}: {Describe(entMan, m)}");
                Assert.That(Total(entMan, m), Is.EqualTo(start[m]), $"{id}: no damage");
                Assert.That(Dmg(entMan, m, "Asphyxiation"), Is.EqualTo(FixedPoint2.Zero), $"{id}: no asphyxiation");
                Assert.That(Dmg(entMan, m, "Cold"), Is.EqualTo(FixedPoint2.Zero), $"{id}: no cold");
                Assert.That(Dmg(entMan, m, "Blunt"), Is.EqualTo(FixedPoint2.Zero), $"{id}: no barotrauma");
                Assert.That(mobState.IsAlive(m), $"{id}: alive");
                Assert.That(entMan.HasComponent<BreathingImmunityComponent>(m), $"{id}: breathing immune");
                Assert.That(entMan.GetComponent<BarotraumaComponent>(m).HasImmunity, $"{id}: pressure immune");
                foreach (var alert in new[] { "LowOxygen", "LowPressure", "HighPressure" })
                    Assert.That(alerts.IsShowingAlert(m, alert), Is.False, $"{id}: {alert} alert");
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Heat still burns them, as before for Gilbert.</summary>
    [Test]
    public async Task HeatStillBurns()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var temps = server.System<TemperatureSystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        EntityUid gilbert = default, op = default;

        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert", new EntityCoordinates(map.MapUid, new Vector2(0f, -60f)));
            op = entMan.SpawnEntity("MobShogkkOperative", new MapCoordinates(new Vector2(0f, 60f), map.MapId));
            entMan.GetComponent<Content.Server._CorvaxGoob.ShogkkOperative.ShogkkOperativeAIComponent>(op).PauseUntil =
                timing.CurTime + TimeSpan.FromHours(1);
        });

        for (var s = 0; s < 5; s++)
        {
            await server.WaitPost(() =>
            {
                temps.ForceChangeTemperature(gilbert, 500f);
                temps.ForceChangeTemperature(op, 500f);
            });
            await server.WaitRunTicks((int) Math.Ceiling(1.0 * timing.TickRate));
        }

        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[ops-v2.1] 500 K: gilbert {Describe(entMan, gilbert)}, op {Describe(entMan, op)}");
            Assert.That(Dmg(entMan, gilbert, "Heat"), Is.GreaterThan(FixedPoint2.Zero), "Gilbert burns");
            Assert.That(Dmg(entMan, op, "Heat"), Is.GreaterThan(FixedPoint2.Zero), "operative burns");
        });

        await pair.CleanReturnAsync();
    }
}

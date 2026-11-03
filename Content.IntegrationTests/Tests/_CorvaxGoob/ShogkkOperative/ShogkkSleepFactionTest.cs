using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._CorvaxGoob.ShogkkOperative;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared.Bed.Sleep;
using Content.Shared.CCVar;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.SSDIndicator;
using Robust.Shared.Configuration;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ShogkkOperative;

/// <summary>
/// v3.1 fixes: the operatives and the zombified no longer fall asleep on their own (the forced SSD sleep of a body no
/// player controls), and every faction hostile to Gilbert is hostile to them too.
/// </summary>
[TestFixture]
public sealed class ShogkkSleepFactionTest
{
    private const string Operative = "MobShogkkOperative";
    private const string Zombified = "MobShogkkZombifiedAssistant";
    private const string Gilbert = "MobGilbert";
    private const string Control = "MobHuman";

    private static readonly ProtoId<NpcFactionPrototype> GilbertFaction = "Gilbert";
    private static readonly ProtoId<NpcFactionPrototype> OperativeFaction = "ShogkkOperative";
    private static readonly ProtoId<NpcFactionPrototype> ZombifiedFaction = "ShogkkZombified";

    private static readonly string[] Turrets =
    {
        "WeaponTurretNanoTrasen", "WeaponTurretSyndicate", "WeaponTurretHostile", "WeaponTurretAllHostile", "WeaponTurretXeno",
    };

    private static async Task RunSeconds(Pair.TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.Server.WaitRunTicks((int) Math.Ceiling(seconds * rate) + 1);
    }

    private static void Pause(IEntityManager entMan, EntityUid uid)
    {
        var until = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        if (entMan.TryGetComponent<ShogkkOperativeAIComponent>(uid, out var op))
            op.PauseUntil = until;
        if (entMan.TryGetComponent<ShogkkZombifiedAIComponent>(uid, out var z))
        {
            z.PauseUntil = until;
            z.NextHordeCheck = until;
        }
    }

    /// <summary>
    /// The real trigger, sped up: with ic.ssd_sleep_time at 1 s an ordinary uncontrolled human (with SSDIndicator) is
    /// put to sleep (proof that the trigger fires in the test); the operative and the zombified have no SSDIndicator and
    /// stay awake, also well past the delay.
    /// </summary>
    [Test]
    public async Task NoSsdSleep()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var cfg = server.ResolveDependency<IConfigurationManager>();
        var mapA = await pair.CreateTestMap();
        var mapB = await pair.CreateTestMap();

        var oldSleep = false;
        var oldTime = 0f;
        EntityUid op = default, z = default, human = default;
        await server.WaitPost(() =>
        {
            oldSleep = cfg.GetCVar(CCVars.ICSSDSleep);
            oldTime = cfg.GetCVar(CCVars.ICSSDSleepTime);
            cfg.SetCVar(CCVars.ICSSDSleep, true);
            cfg.SetCVar(CCVars.ICSSDSleepTime, 1f);

            op = entMan.SpawnEntity(Operative, mapA.GridCoords);
            z = entMan.SpawnEntity(Zombified, mapA.GridCoords.Offset(new Vector2(1, 0)));
            Pause(entMan, op);
            Pause(entMan, z);
            human = entMan.SpawnEntity(Control, mapB.GridCoords); // another map: nobody shoots him
        });

        await RunSeconds(pair, 3);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<SleepingComponent>(human), "control: an uncontrolled human must SSD-sleep (trigger fires)");
                foreach (var uid in new[] { op, z })
                {
                    var name = entMan.ToPrettyString(uid);
                    Assert.That(entMan.HasComponent<SSDIndicatorComponent>(uid), Is.False, $"{name} still has SSDIndicator");
                    Assert.That(entMan.HasComponent<SleepingComponent>(uid), Is.False, $"{name} fell asleep (SSD timer)");
                }
            });
        });

        // Long after the (shortened) SSD delay: still awake.
        await RunSeconds(pair, 5);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var uid in new[] { op, z })
                    Assert.That(entMan.HasComponent<SleepingComponent>(uid), Is.False, $"{entMan.ToPrettyString(uid)} asleep later");
            });
        });

        await server.WaitPost(() =>
        {
            cfg.SetCVar(CCVars.ICSSDSleep, oldSleep);
            cfg.SetCVar(CCVars.ICSSDSleepTime, oldTime);
            entMan.DeleteEntity(op);
            entMan.DeleteEntity(z);
            entMan.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Every faction hostile to Gilbert is hostile to the operatives and the zombified as well.</summary>
    [Test]
    public async Task FactionsHostileLikeGilbert()
    {
        await using var pair = await PoolManager.GetServerClient();
        var protoMan = pair.Server.ResolveDependency<IPrototypeManager>();

        var checkedCount = 0;
        Assert.Multiple(() =>
        {
            foreach (var faction in protoMan.EnumeratePrototypes<NpcFactionPrototype>())
            {
                if (faction.ID == OperativeFaction || faction.ID == ZombifiedFaction || !faction.Hostile.Contains(GilbertFaction))
                    continue;

                checkedCount++;
                Assert.That(faction.Hostile, Does.Contain(OperativeFaction), $"{faction.ID} is hostile to Gilbert but not to the operatives");
                Assert.That(faction.Hostile, Does.Contain(ZombifiedFaction), $"{faction.ID} is hostile to Gilbert but not to the zombified");
            }

            // Internal relations stay: operatives and zombified neutral to each other, both hostile to Gilbert and back.
            var op = protoMan.Index(OperativeFaction);
            var z = protoMan.Index(ZombifiedFaction);
            var gilbert = protoMan.Index(GilbertFaction);
            Assert.That(op.Hostile, Does.Not.Contain(ZombifiedFaction).And.Not.Contain(OperativeFaction));
            Assert.That(z.Hostile, Does.Not.Contain(OperativeFaction).And.Not.Contain(ZombifiedFaction));
            Assert.That(op.Hostile, Does.Contain(GilbertFaction));
            Assert.That(z.Hostile, Does.Contain(GilbertFaction));
            Assert.That(gilbert.Hostile, Does.Contain(OperativeFaction).And.Contain(ZombifiedFaction));
        });
        Assert.That(checkedCount, Is.GreaterThanOrEqualTo(15), "factions hostile to Gilbert");

        await pair.CleanReturnAsync();
    }

    /// <summary>Live: turrets of different factions see the operative and the zombified as targets, like Gilbert.</summary>
    [Test]
    public async Task TurretsTargetLikeGilbert()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var factions = entMan.System<NpcFactionSystem>();
            var op = entMan.SpawnEntity(Operative, map.GridCoords.Offset(new Vector2(2, 0)));
            var z = entMan.SpawnEntity(Zombified, map.GridCoords.Offset(new Vector2(-2, 0)));
            var gilbert = entMan.SpawnEntity(Gilbert, map.GridCoords.Offset(new Vector2(0, 2)));
            var spawned = new List<EntityUid> { op, z, gilbert };

            Assert.Multiple(() =>
            {
                foreach (var proto in Turrets)
                {
                    var turret = entMan.SpawnEntity(proto, map.GridCoords.Offset(new Vector2(0, -2)));
                    spawned.Add(turret);
                    var hostiles = factions.GetNearbyHostiles(turret, 10f).ToHashSet();
                    Assert.That(hostiles, Does.Contain(gilbert), $"{proto} vs Gilbert");
                    Assert.That(hostiles, Does.Contain(op), $"{proto} vs operative");
                    Assert.That(hostiles, Does.Contain(z), $"{proto} vs zombified");
                }

                var opHostiles = factions.GetNearbyHostiles(op, 10f).ToHashSet();
                var zHostiles = factions.GetNearbyHostiles(z, 10f).ToHashSet();
                Assert.That(opHostiles, Does.Contain(gilbert).And.Not.Contain(z));
                Assert.That(zHostiles, Does.Contain(gilbert).And.Not.Contain(op));
                Assert.That(factions.GetNearbyHostiles(gilbert, 10f), Does.Contain(op).And.Contain(z));
            });

            foreach (var uid in spawned)
                entMan.DeleteEntity(uid);
        });

        await pair.CleanReturnAsync();
    }
}

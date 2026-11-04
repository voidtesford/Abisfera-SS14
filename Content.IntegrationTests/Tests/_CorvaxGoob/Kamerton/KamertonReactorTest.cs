#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Kamerton;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Atmos;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton reactor: assembly through the AME-like <see cref="KamertonNodeGroup"/> (any shape, broken links), synchronizer pairing (50 kW per pair, 4 pairs per synchronizer),
/// HV output through the power unit, heat and cooling, the redspace leak through a broken suppressor ring
/// and the collapse. The reactor is stepped through <see cref="KamertonReactorSystem.Step"/> directly.
/// </summary>
[TestFixture]
[TestOf(typeof(KamertonReactorSystem))]
public sealed class KamertonReactorTest
{
    [TestPrototypes]
    private const string Prototypes = @"
- type: entity
  id: KamertonTestHvConsumer
  components:
  - type: Transform
    anchored: true
  - type: NodeContainer
    nodes:
      input:
        !type:CableDeviceNode
        nodeGroupID: HVPower
  - type: PowerConsumer
    drawRate: 10000000
";

    private const string StableFull = "RedspaceSuperEnergyCell";
    private const string StableEmpty = "RedspaceSuperEnergyCellEmpty";
    private const string UnstableFull = "KamertonUnstableEnergyCell";
    private const string UnstableEmpty = "KamertonUnstableEnergyCellEmpty";

    private static readonly string[] Parts =
    {
        "KamertonReactorCore", "KamertonStableFuelArray", "KamertonUnstableFuelArray", "KamertonSynchronizer",
        "KamertonHeatSink", "KamertonRadiator", "KamertonSuppressor", "KamertonPowerUnit", "KamertonControlModule",
    };

    /// <summary>Standard layout, rows from y = +2 down to y = -2 (S = stable, U = unstable, Y = sync, H = heat sink, R = radiator, C = core).</summary>
    private static readonly string[] Layout =
    {
        "HSYUH",
        "URRRS",
        "YRCRY",
        "SRRRU",
        "HUYSH",
    };

    private sealed class Reactor
    {
        public EntityUid Core;
        public readonly Dictionary<Vector2i, EntityUid> At = new();
        public readonly List<EntityUid> Ring = new();
        public EntityUid Console;
        public EntityUid PowerUnit;
    }

    private static EntityCoordinates Tile(TestMapData map, int x, int y) => new(map.Grid.Owner, x + 0.5f, y + 0.5f);

    private static void Floor(IEntityManager entMan, TestMapData map, int radius)
    {
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++)
            tiles.Add((new Vector2i(x, y), map.Tile.Tile));
        entMan.System<SharedMapSystem>().SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
    }

    /// <summary>Builds the standard layout plus the ring (console at (0,3), power unit at (0,-3)), skipping <paramref name="skipRing"/> tiles.</summary>
    private static Reactor Build(IEntityManager entMan, TestMapData map, bool fillCells, int skipRing = 0, bool ring = true)
    {
        var r = new Reactor();
        var slots = entMan.System<ItemSlotsSystem>();
        for (var row = 0; row < 5; row++)
        for (var col = 0; col < 5; col++)
        {
            var x = col - 2;
            var y = 2 - row;
            var id = Layout[row][col] switch
            {
                'H' => "KamertonHeatSink",
                'S' => "KamertonStableFuelArray",
                'U' => "KamertonUnstableFuelArray",
                'Y' => "KamertonSynchronizer",
                'R' => "KamertonRadiator",
                _ => "KamertonReactorCore",
            };
            var uid = entMan.SpawnEntity(id, Tile(map, x, y));
            r.At[new Vector2i(x, y)] = uid;
            if (id == "KamertonReactorCore")
                r.Core = uid;

            if (!fillCells || id is not ("KamertonStableFuelArray" or "KamertonUnstableFuelArray"))
                continue;

            var cell = id == "KamertonStableFuelArray" ? StableFull : UnstableFull;
            for (var i = 1; i <= 4; i++)
                Assert.That(slots.TryInsert(uid, KamertonReactorSystem.SlotId(i), entMan.SpawnEntity(cell, Tile(map, x, y)), null), Is.True);
        }

        if (!ring)
            return r;

        var skipped = 0;
        for (var x = -3; x <= 3; x++)
        for (var y = -3; y <= 3; y++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != 3)
                continue;
            if (x == 0 && y == 3)
            {
                r.Console = entMan.SpawnEntity("KamertonControlModule", Tile(map, x, y));
                r.Ring.Add(r.Console);
                continue;
            }

            if (x == 0 && y == -3)
            {
                r.PowerUnit = entMan.SpawnEntity("KamertonPowerUnit", Tile(map, x, y));
                r.Ring.Add(r.PowerUnit);
                continue;
            }

            if (skipped < skipRing)
            {
                skipped++;
                continue;
            }

            r.Ring.Add(entMan.SpawnEntity("KamertonSuppressor", Tile(map, x, y)));
        }

        return r;
    }

    private static Entity<KamertonReactorCoreComponent> Core(IEntityManager entMan, Reactor r)
        => (r.Core, entMan.GetComponent<KamertonReactorCoreComponent>(r.Core));

    private static int CountProto(IEntityManager entMan, MapId map, string proto)
    {
        var n = 0;
        var q = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (q.MoveNext(out _, out var meta, out var xform))
        {
            if (xform.MapID == map && meta.EntityPrototype?.ID == proto)
                n++;
        }
        return n;
    }

    [Test]
    public async Task PrototypesFlatpacksAndSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            foreach (var id in Parts)
            {
                var p = proto.Index<EntityPrototype>(id);
                Assert.That(p.Abstract, Is.False, id);
                Assert.That(p.TryGetComponent<KamertonReactorPartComponent>(out _, factory), Is.True, id);
                Assert.That(p.Categories.Any(c => c.ID == "HideSpawnMenu"), Is.False, $"{id} is admin-spawnable");

                var fp = proto.Index<EntityPrototype>(id + "Flatpack");
                Assert.That(fp.TryGetComponent<FlatpackComponent>(out var flat, factory), Is.True, fp.ID);
                Assert.That(flat!.Entity?.Id, Is.EqualTo(id), fp.ID);
                Assert.That(fp.Categories.Any(c => c.ID == "HideSpawnMenu"), Is.False, $"{fp.ID} is admin-spawnable");
            }

            var core = proto.Index<EntityPrototype>("KamertonReactorCore");
            Assert.That(core.TryGetComponent<KamertonReactorCoreComponent>(out var c, factory), Is.True);
            Assert.That(c!.PowerPerPair, Is.EqualTo(50000f));
            Assert.That(c.MaxPairsPerSynchronizer, Is.EqualTo(4));
            Assert.That(c.CollapseTemperature, Is.EqualTo(2000f));
            Assert.That(c.LeakRange, Is.EqualTo(20f));
            Assert.That(c.CollapseRange, Is.EqualTo(120f));
            Assert.That(c.CollapseSingularities, Is.EqualTo(5));
            Assert.That(proto.Index<EntityPrototype>("KamertonStableFuelArray").TryGetComponent<KamertonFuelArrayComponent>(out var a, factory), Is.True);
            Assert.That(a!.CellLifetime, Is.EqualTo(1800f), "30 minutes per cell");
        });

        var res = pair.Client.ResolveDependency<IResourceCache>();
        await pair.Client.WaitAssertion(() =>
        {
            var reactor = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Structures/Power/Generation/kamerton_reactor.rsi")).RSI;
            foreach (var s in new[]
                     {
                         "core", "core_on", "core_heat_1", "core_heat_2", "core_heat_3", "array_stable", "array_unstable",
                         "cells_stable_4", "cells_unstable_4", "sync", "sync_on", "heatsink", "heatsink_on", "radiator",
                         "radiator_heat_3", "suppressor", "suppressor_on", "powerunit", "powerunit_on",
                     })
                Assert.That(reactor.TryGetState(s, out _), Is.True, s);

            var box = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Objects/Devices/kamerton_flatpack.rsi")).RSI;
            foreach (var s in new[] { "base", "icon-core", "icon-stable", "icon-unstable", "icon-sync", "icon-heatsink", "icon-radiator", "icon-suppressor", "icon-powerunit", "icon-console" })
                Assert.That(box.TryGetState(s, out _), Is.True, s);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task AssemblyAndPairing()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: true);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            sys.Scan(core);
            var c = core.Comp;
            Assert.That(c.Synchronizers, Has.Count.EqualTo(4));
            Assert.That(c.StableArrays, Has.Count.EqualTo(4));
            Assert.That(c.UnstableArrays, Has.Count.EqualTo(4));
            Assert.That(c.HeatSinks, Has.Count.EqualTo(4));
            Assert.That(c.Radiators, Has.Count.EqualTo(8));
            Assert.That(c.RingMissing, Is.Zero);
            Assert.That(c.Console, Is.EqualTo(r.Console));
            Assert.That(c.PowerUnit, Is.EqualTo(r.PowerUnit));
            Assert.That(sys.IsAssembled(c) && sys.RingComplete(c), Is.True);

            // Every array is claimed by its orthogonal neighbour synchronizer.
            foreach (var arr in c.StableArrays.Concat(c.UnstableArrays))
            {
                var sync = entMan.GetComponent<KamertonFuelArrayComponent>(arr).Synchronizer;
                Assert.That(sync, Is.Not.Null);
                var d = entMan.GetComponent<TransformComponent>(arr).Coordinates.Position - entMan.GetComponent<TransformComponent>(sync!.Value).Coordinates.Position;
                Assert.That(MathF.Abs(d.X) + MathF.Abs(d.Y), Is.EqualTo(1f).Within(0.01f));
            }

            // Stopped: no power.
            sys.Step(core, 1f);
            Assert.That(c.TotalPower, Is.Zero);

            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            sys.Step(core, 1f);
            Assert.That(c.ActivePairs, Is.EqualTo(16), "4 synchronizers x 4 pairs");
            Assert.That(c.TotalPower, Is.EqualTo(800000f));
            Assert.That(c.SyncPairs.Values, Is.All.EqualTo(4));

            // Take cells out of one synchronizer's arrays: 3 stable + 2 unstable -> 2 pairs (100 kW) on it.
            var slots = entMan.System<ItemSlotsSystem>();
            var north = r.At[new Vector2i(0, 2)];
            var st = r.At[new Vector2i(-1, 2)];
            var un = r.At[new Vector2i(1, 2)];
            Assert.That(entMan.GetComponent<KamertonFuelArrayComponent>(st).Synchronizer, Is.EqualTo(north));
            slots.TryEject(st, KamertonReactorSystem.SlotId(1), null, out _);
            slots.TryEject(un, KamertonReactorSystem.SlotId(1), null, out _);
            slots.TryEject(un, KamertonReactorSystem.SlotId(2), null, out _);
            sys.Step(core, 1f);
            Assert.That(c.SyncPairs[north], Is.EqualTo(2));
            Assert.That(c.TotalPower, Is.EqualTo(14 * 50000f));

            // Cells drain after 30 minutes of work into their empty variants.
            sys.Step(core, 1800f);
            Assert.That(CountProto(entMan, map.MapId, StableEmpty), Is.EqualTo(14), "only cells in active pairs drain");
            Assert.That(CountProto(entMan, map.MapId, StableFull), Is.EqualTo(2), "spare stable cell + the ejected one stay charged");
            Assert.That(CountProto(entMan, map.MapId, UnstableEmpty), Is.EqualTo(14));
            sys.Step(core, 1f);
            Assert.That(c.TotalPower, Is.Zero, "everything spent");

            sys.Stop(core);
            sys.SetTemperature(core, c.AmbientTemperature);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task SynchronizerCapAndLinkedHeatSinks()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        EntityUid core = default, sync = default, sinkLinked = default, sinkLoose = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 4);
            var slots = entMan.System<ItemSlotsSystem>();
            core = entMan.SpawnEntity("KamertonReactorCore", Tile(map, 0, 0));
            sync = entMan.SpawnEntity("KamertonSynchronizer", Tile(map, 1, 1));
            // Two stable + two unstable arrays around one synchronizer: 8 + 8 cells, still only 4 pairs.
            foreach (var (pos, id, cell) in new[]
                     {
                         (new Vector2i(1, 2), "KamertonStableFuelArray", StableFull),
                         (new Vector2i(2, 1), "KamertonStableFuelArray", StableFull),
                         (new Vector2i(0, 1), "KamertonUnstableFuelArray", UnstableFull),
                         (new Vector2i(1, 0), "KamertonUnstableFuelArray", UnstableFull),
                     })
            {
                var arr = entMan.SpawnEntity(id, Tile(map, pos.X, pos.Y));
                for (var i = 1; i <= 4; i++)
                    slots.TryInsert(arr, KamertonReactorSystem.SlotId(i), entMan.SpawnEntity(cell, Tile(map, pos.X, pos.Y)), null);
            }

            // One heat sink linked to the core, one standing apart in its own node group.
            sinkLinked = entMan.SpawnEntity("KamertonHeatSink", Tile(map, -1, 0));
            sinkLoose = entMan.SpawnEntity("KamertonHeatSink", Tile(map, -3, 0));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = (core, entMan.GetComponent<KamertonReactorCoreComponent>(core));
            Assert.That(sys.TryStart(c, out var reason), Is.True, reason);
            sys.Step(c, 1f);
            Assert.That(c.Item2.SyncPairs[sync], Is.EqualTo(4));
            Assert.That(c.Item2.TotalPower, Is.EqualTo(200000f), "200 kW cap per synchronizer");
            Assert.That(c.Item2.HeatSinks, Is.EquivalentTo(new[] { sinkLinked }), "only linked heat sinks count");
            Assert.That(sys.GetGroup(sinkLoose), Is.Not.SameAs(sys.GetGroup(core)));
            Assert.That(c.Item2.RingMissing, Is.EqualTo(18), "every empty tile around the inner parts");
            sys.Stop(c);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PowerUnitFeedsHv()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;
        EntityUid consumer = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: true);
            entMan.SpawnEntity("CableHV", Tile(map, 0, -3));
            entMan.SpawnEntity("CableHV", Tile(map, 0, -4));
            consumer = entMan.SpawnEntity("KamertonTestHvConsumer", Tile(map, 0, -4));
        });
        // The parts link into one node group on the next node update.
        await pair.RunTicksSync(3);

        await server.WaitPost(() =>
        {
            var core = Core(entMan, r);
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            sys.Step(core, 1f);
        });
        // The supplier ramps up (200 kW/s), give it time.
        await pair.RunSeconds(8f);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            Assert.That(core.Comp.TotalPower, Is.EqualTo(800000f));
            var supplier = entMan.GetComponent<PowerSupplierComponent>(r.PowerUnit);
            Assert.That(supplier.MaxSupply, Is.EqualTo(800000f));
            Assert.That(entMan.GetComponent<PowerConsumerComponent>(consumer).ReceivedPower, Is.EqualTo(800000f).Within(1f), "HV gets the reactor output");
            Assert.That(supplier.CurrentSupply, Is.EqualTo(800000f).Within(1f));

            var state = sys.BuildState(core);
            Assert.That(state.Linked && state.Assembled && state.Running && state.PowerUnitLinked, Is.True);
            Assert.That(state.Arrays, Has.Count.EqualTo(8));

            sys.Scram(core);
            Assert.That(sys.TryStart(core, out var reason), Is.False, "SCRAM locks the reactor");
            Assert.That(reason, Is.EqualTo("kamerton-reactor-popup-scram-locked"));
            sys.Step(core, 1f);
            Assert.That(supplier.MaxSupply, Is.Zero);
        });
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<PowerConsumerComponent>(consumer).ReceivedPower, Is.Zero.Within(1f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HeatAndCooling()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var nodes = server.System<NodeContainerSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: true);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            var c = core.Comp;
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);

            // No coolant anywhere: 16 pairs heat the core.
            var t0 = c.Temperature;
            sys.Step(core, 10f);
            var expected = 16 * c.HeatPerPair * 10f / sys.GetHeatCapacity(c);
            Assert.That(c.Temperature - t0, Is.EqualTo(expected).Within(0.5f), "heats up without cooling");
            Assert.That(c.Temperature, Is.GreaterThan(t0 + 50f));

            // Stop at 1200 K and push cold nitrogen into one corner heat sink.
            sys.Stop(core);
            sys.SetTemperature(core, 1200f);
            var sink = r.At[new Vector2i(-2, 2)];
            Assert.That(nodes.TryGetNode(sink, "inlet", out PipeNode? inlet), Is.True);
            Assert.That(nodes.TryGetNode(sink, "outlet", out PipeNode? outlet), Is.True);
            inlet!.Air.AdjustMoles(Gas.Nitrogen, 2000f);
            inlet.Air.Temperature = 80f;

            sys.Step(core, 1f);
            Assert.That(c.Temperature, Is.LessThan(1200f), "cold gas cools the core");
            Assert.That(outlet!.Air.TotalMoles, Is.GreaterThan(0f), "gas moves inlet -> outlet");
            Assert.That(outlet.Air.Temperature, Is.GreaterThan(80f), "the gas carries the heat away");
            var sinkComp = entMan.GetComponent<KamertonHeatSinkComponent>(sink);
            Assert.That(sinkComp.LastHeatRemoved, Is.GreaterThan(0f));

            var before = c.Temperature;
            for (var i = 0; i < 10; i++)
                sys.Step(core, 1f);
            Assert.That(c.Temperature, Is.LessThan(before));
            Assert.That(c.Temperature, Is.GreaterThanOrEqualTo(80f), "never below the coolant");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BrokenRingLeaksRedspace()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;
        EntityUid near = default, far = default, immune = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: true, skipRing: 1);
            near = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 15.5f, 0.5f));
            far = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 25.5f, 0.5f));
            immune = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid.Owner, 10.5f, 0.5f));
            entMan.EnsureComponent<RedspaceImmuneComponent>(immune);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            sys.Scan(core);
            Assert.That(core.Comp.RingMissing, Is.EqualTo(1));
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            float Dmg(EntityUid u) => entMan.GetComponent<DamageableComponent>(u).TotalDamage.Float();

            for (var i = 0; i < 5; i++)
                sys.Step(core, 1f);

            Assert.That(Dmg(near), Is.GreaterThan(0f), "leak hits within 20 tiles");
            Assert.That(Dmg(far), Is.Zero, "nothing beyond 20 tiles");
            Assert.That(Dmg(immune), Is.Zero, "redspace-immune are spared");

            // Close the ring: the leak stops.
            var missing = Enumerable.Range(-3, 7).SelectMany(x => Enumerable.Range(-3, 7).Select(y => new Vector2i(x, y)))
                .First(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)) == 3
                            && !r.Ring.Any(e => entMan.GetComponent<TransformComponent>(e).Coordinates.Position.Floored() == p));
            r.Ring.Add(entMan.SpawnEntity("KamertonSuppressor", Tile(map, missing.X, missing.Y)));
        });

        // The new suppressor joins the node group on the next node update.
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            float Dmg(EntityUid u) => entMan.GetComponent<DamageableComponent>(u).TotalDamage.Float();
            sys.Step(core, 1f);
            Assert.That(core.Comp.RingMissing, Is.Zero);
            var after = Dmg(near);
            for (var i = 0; i < 5; i++)
                sys.Step(core, 1f);
            Assert.That(Dmg(near), Is.EqualTo(after), "complete ring: no leak");
            sys.Stop(core);
            sys.SetTemperature(core, core.Comp.AmbientTemperature);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A 3x2 reactor (not the standard 5x5) walled in by a 5x4 shell, rows from y = +1 down to y = 0:
    /// <c>S Y U</c> over <c>H C R</c>; console at (0,2), power unit at (0,-1).
    /// </summary>
    private static Reactor BuildSmall(IEntityManager entMan, TestMapData map)
    {
        var r = new Reactor();
        var slots = entMan.System<ItemSlotsSystem>();
        foreach (var (pos, id) in new[]
                 {
                     (new Vector2i(-1, 1), "KamertonStableFuelArray"),
                     (new Vector2i(0, 1), "KamertonSynchronizer"),
                     (new Vector2i(1, 1), "KamertonUnstableFuelArray"),
                     (new Vector2i(-1, 0), "KamertonHeatSink"),
                     (new Vector2i(0, 0), "KamertonReactorCore"),
                     (new Vector2i(1, 0), "KamertonRadiator"),
                 })
        {
            var uid = entMan.SpawnEntity(id, Tile(map, pos.X, pos.Y));
            r.At[pos] = uid;
            if (id == "KamertonReactorCore")
                r.Core = uid;

            var cell = id switch
            {
                "KamertonStableFuelArray" => StableFull,
                "KamertonUnstableFuelArray" => UnstableFull,
                _ => null,
            };
            if (cell == null)
                continue;

            for (var i = 1; i <= 4; i++)
                Assert.That(slots.TryInsert(uid, KamertonReactorSystem.SlotId(i), entMan.SpawnEntity(cell, Tile(map, pos.X, pos.Y)), null), Is.True);
        }

        for (var x = -2; x <= 2; x++)
        for (var y = -1; y <= 2; y++)
        {
            if (x is > -2 and < 2 && y is > -1 and < 2)
                continue;

            var id = (x, y) switch
            {
                (0, 2) => "KamertonControlModule",
                (0, -1) => "KamertonPowerUnit",
                _ => "KamertonSuppressor",
            };
            var uid = entMan.SpawnEntity(id, Tile(map, x, y));
            r.At[new Vector2i(x, y)] = uid;
            r.Ring.Add(uid);
            if (id == "KamertonControlModule")
                r.Console = uid;
            else if (id == "KamertonPowerUnit")
                r.PowerUnit = uid;
        }

        return r;
    }

    private static int Links(IEntityManager entMan, EntityUid uid)
    {
        entMan.System<SharedAppearanceSystem>().TryGetData<int>(uid, KamertonReactorVisuals.Links, out var flags);
        return flags;
    }

    [Test]
    public async Task NodeGroupAssemblyAnyShape()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;
        EntityUid stray = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = BuildSmall(entMan, map);
            // A synchronizer two tiles away from the shell is in its own group and never joins.
            stray = entMan.SpawnEntity("KamertonSynchronizer", Tile(map, 4, 0));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            var group = sys.GetGroup(r.Core);
            Assert.That(group, Is.Not.Null);
            Assert.That(group!.MasterCore, Is.EqualTo(r.Core));
            Assert.That(group.Nodes, Has.Count.EqualTo(20), "6 inner parts + 14 shell parts");
            foreach (var uid in r.At.Values)
                Assert.That(sys.GetGroup(uid), Is.SameAs(group));
            Assert.That(sys.GetGroup(stray), Is.Not.SameAs(group));

            sys.Scan(core);
            var c = core.Comp;
            Assert.That(c.Synchronizers, Is.EquivalentTo(new[] { r.At[new Vector2i(0, 1)] }));
            Assert.That(c.StableArrays, Has.Count.EqualTo(1));
            Assert.That(c.UnstableArrays, Has.Count.EqualTo(1));
            Assert.That(c.HeatSinks, Has.Count.EqualTo(1));
            Assert.That(c.Radiators, Has.Count.EqualTo(1));
            Assert.That(c.RingParts, Has.Count.EqualTo(14));
            Assert.That(c.RingMissing, Is.Zero);
            Assert.That(c.Console, Is.EqualTo(r.Console));
            Assert.That(c.PowerUnit, Is.EqualTo(r.PowerUnit));
            Assert.That(sys.IsAssembled(c) && sys.RingComplete(c), Is.True);

            // Link couplings (N=1, S=2, E=4, W=8): the core is linked on all four sides, a shell corner on two.
            Assert.That(Links(entMan, r.Core), Is.EqualTo(15));
            Assert.That(Links(entMan, r.At[new Vector2i(-2, 2)]), Is.EqualTo(2 | 4));
            Assert.That(Links(entMan, stray), Is.Zero);

            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            sys.Step(core, 1f);
            Assert.That(c.ActivePairs, Is.EqualTo(4));
            Assert.That(c.TotalPower, Is.EqualTo(200000f));
            sys.Stop(core);
            sys.SetTemperature(core, c.AmbientTemperature);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BrokenConnection()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = BuildSmall(entMan, map);
        });
        await pair.RunTicksSync(5);

        EntityUid stable = default, sync = default;
        await server.WaitAssertion(() =>
        {
            stable = r.At[new Vector2i(-1, 1)];
            sync = r.At[new Vector2i(0, 1)];
            var core = Core(entMan, r);
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            sys.Step(core, 1f);
            Assert.That(core.Comp.TotalPower, Is.EqualTo(200000f));

            // Unanchoring the stable array cuts it out of the node group even though it still stands there.
            entMan.System<SharedTransformSystem>().Unanchor(stable);
        });
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            Assert.That(sys.GetGroup(stable), Is.Not.SameAs(sys.GetGroup(r.Core)));
            sys.Step(core, 1f);
            var c = core.Comp;
            Assert.That(c.StableArrays, Is.Empty);
            Assert.That(c.RingMissing, Is.EqualTo(1), "the hole the array left");
            Assert.That(c.ActivePairs, Is.Zero);
            Assert.That(c.TotalPower, Is.Zero);
            Assert.That(Links(entMan, stable), Is.Zero);
            Assert.That(Links(entMan, sync), Is.EqualTo(1 | 2 | 4), "the synchronizer lost its west coupling");

            // Cutting the synchronizer out leaves no synchronizers: not assembled, the reactor stops.
            entMan.System<SharedTransformSystem>().Unanchor(sync);
        });
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            sys.Step(core, 1f);
            Assert.That(sys.IsAssembled(core.Comp), Is.False);
            Assert.That(core.Comp.Working, Is.False);
            Assert.That(core.Comp.RingMissing, Is.EqualTo(2));
            sys.SetTemperature(core, core.Comp.AmbientTemperature);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CollapseSpawnsTeslaSingularitiesAndRedspace()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var xforms = server.System<SharedTransformSystem>();
        Reactor r = default!;
        Vector2 corePos = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: true);
        });
        await pair.RunTicksSync(5);

        var spawned = new List<EntityUid>();
        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            corePos = xforms.GetWorldPosition(r.Core);
            Assert.That(entMan.HasComponent<RedspaceMapComponent>(map.MapUid), Is.False);
            sys.SetTemperature(core, 2100f);
            sys.Step(core, 1f);
            Assert.That(core.Comp.Collapsed, Is.True);

            var q = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            var tesla = 0;
            var singularities = 0;
            while (q.MoveNext(out var uid, out var meta, out var xform))
            {
                if (xform.MapID != map.MapId)
                    continue;
                var dist = (xforms.GetWorldPosition(xform) - corePos).Length();
                if (meta.EntityPrototype?.ID == "TeslaEnergyBall")
                {
                    tesla++;
                    spawned.Add(uid);
                    Assert.That(dist, Is.LessThan(1.5f), "tesla ball at the core");
                }
                else if (meta.EntityPrototype?.ID == "Singularity")
                {
                    singularities++;
                    spawned.Add(uid);
                    Assert.That(dist, Is.LessThanOrEqualTo(120.5f), "singularities within 120 tiles");
                }
            }

            Assert.That(tesla, Is.EqualTo(1));
            Assert.That(singularities, Is.EqualTo(5));
            Assert.That(entMan.TryGetComponent<RedspaceMapComponent>(map.MapUid, out var rs), Is.True, "the map becomes redspace");
            Assert.That(rs!.Enabled, Is.True);
            Assert.That(rs.Preset.Id, Is.EqualTo("RedspaceNormal"));
        });
        await pair.RunTicksSync(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(r.Core), Is.True, "the core is destroyed");
            Assert.That(r.At.Values.All(entMan.Deleted), Is.True, "the block is destroyed");
            Assert.That(r.Ring.All(entMan.Deleted), Is.True, "the ring is destroyed");

            foreach (var uid in spawned)
                entMan.DeleteEntity(uid);
            server.System<RedspaceMapSystem>().SetRedspace(map.MapUid, null);
        });

        await pair.CleanReturnAsync();
    }
}

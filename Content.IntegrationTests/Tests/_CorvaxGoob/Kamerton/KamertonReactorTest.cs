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
        "KamertonFuelStorage",
    };

    /// <summary>
    /// Standard layout, rows from y = +3 down to y = -3 (S = stable, U = unstable, Y = sync, H = heat sink,
    /// R = radiator, C = core, Q = suppressor, K = console, P = power unit, F = fuel storage). Two parallel lines on each side:
    /// radiators inside, heat sinks on the edge (their pipes run north-south and leave the reactor).
    /// </summary>
    private static readonly string[] Layout =
    {
        "HQFKQQH",
        "HRSYURH",
        "HRUYSRH",
        "HRRCRRH",
        "HRSYURH",
        "HRUYSRH",
        "HQQPQQH",
    };

    private sealed class Reactor
    {
        public EntityUid Core;
        public readonly Dictionary<Vector2i, EntityUid> At = new();
        public readonly List<EntityUid> Ring = new();
        public EntityUid Console;
        public EntityUid PowerUnit;
        public EntityUid Storage;
        public readonly List<Vector2i> Skipped = new();
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

    /// <summary>
    /// Builds the standard layout; <paramref name="ring"/> = false leaves out the suppressors, console and power
    /// unit, <paramref name="skipRing"/> leaves out that many suppressors.
    /// </summary>
    private static Reactor Build(IEntityManager entMan, TestMapData map, bool fillCells, int skipRing = 0, bool ring = true)
    {
        var r = new Reactor();
        var slots = entMan.System<ItemSlotsSystem>();
        var skipped = 0;
        for (var row = 0; row < 7; row++)
        for (var col = 0; col < 7; col++)
        {
            var x = col - 3;
            var y = 3 - row;
            var ch = Layout[row][col];
            var id = ch switch
            {
                'H' => "KamertonHeatSink",
                'S' => "KamertonStableFuelArray",
                'U' => "KamertonUnstableFuelArray",
                'Y' => "KamertonSynchronizer",
                'R' => "KamertonRadiator",
                'Q' => "KamertonSuppressor",
                'K' => "KamertonControlModule",
                'P' => "KamertonPowerUnit",
                'F' => "KamertonFuelStorage",
                _ => "KamertonReactorCore",
            };
            var isRing = ch is 'Q' or 'K' or 'P' or 'F';
            if (isRing && !ring)
                continue;

            if (ch == 'Q' && skipped < skipRing)
            {
                skipped++;
                r.Skipped.Add(new Vector2i(x, y));
                continue;
            }

            var uid = entMan.SpawnEntity(id, Tile(map, x, y));
            r.At[new Vector2i(x, y)] = uid;
            if (isRing)
                r.Ring.Add(uid);
            if (ch == 'C')
                r.Core = uid;
            else if (ch == 'K')
                r.Console = uid;
            else if (ch == 'P')
                r.PowerUnit = uid;
            else if (ch == 'F')
                r.Storage = uid;

            if (!fillCells || ch is not ('S' or 'U'))
                continue;

            var cell = ch == 'S' ? StableFull : UnstableFull;
            for (var i = 1; i <= 4; i++)
                Assert.That(slots.TryInsert(uid, KamertonReactorSystem.SlotId(i), entMan.SpawnEntity(cell, Tile(map, x, y)), null), Is.True);
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
                         "storage", "storage_stable_5", "storage_unstable_5",
                     })
                Assert.That(reactor.TryGetState(s, out _), Is.True, s);

            var box = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Objects/Devices/kamerton_flatpack.rsi")).RSI;
            foreach (var s in new[] { "base", "icon-core", "icon-stable", "icon-unstable", "icon-sync", "icon-heatsink", "icon-radiator", "icon-suppressor", "icon-powerunit", "icon-console", "icon-storage" })
                Assert.That(box.TryGetState(s, out _), Is.True, s);

            var drone = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Mobs/Silicon/kamerton_drone.rsi")).RSI;
            foreach (var s in new[] { "idle", "walk", "eyes" })
            {
                Assert.That(drone.TryGetState(s, out var st), Is.True, s);
                Assert.That(st!.RsiDirections, Is.EqualTo(Robust.Shared.Graphics.RSI.RsiDirectionType.Dir4), s);
            }
            Assert.That(drone.TryGetState("walk", out var walk) && walk!.DelayCount >= 4, Is.True, "walk animation has 4 frames");
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
            Assert.That(c.HeatSinks, Has.Count.EqualTo(14));
            Assert.That(c.Radiators, Has.Count.EqualTo(12));
            Assert.That(c.LinkedRadiators, Has.Count.EqualTo(12), "every radiator is on a heat line");
            Assert.That(c.LinkedHeatSinks, Has.Count.EqualTo(10), "the 2x5 sinks next to radiators; corner sinks only carry the pipe");
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
            Assert.That(c.Item2.RingMissing, Is.EqualTo(15), "every empty tile around the inner parts (the heat sink is a shell part)");
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
            Assert.That(c.Temperature, Is.GreaterThan(t0 + 40f));

            // Heat-pipe joints: radiator-radiator and radiator-heat sink (N=1, S=2, E=4, W=8).
            var appearance = entMan.System<SharedAppearanceSystem>();
            int HeatLinks(EntityUid u) => appearance.TryGetData<int>(u, KamertonReactorVisuals.HeatLinks, out var f) ? f : 0;
            Assert.That(HeatLinks(r.At[new Vector2i(-2, 0)]), Is.EqualTo(15), "radiator: heat pipes all round");
            Assert.That(Links(entMan, r.At[new Vector2i(-2, 0)]), Is.EqualTo(255), "hull joined to all 8 neighbours");
            Assert.That(HeatLinks(r.At[new Vector2i(-3, 2)]), Is.EqualTo(4), "edge heat sink: heat pipe to its radiator");
            Assert.That(Links(entMan, r.At[new Vector2i(-3, 2)]), Is.EqualTo(1 | 2 | 4 | 16 | 64), "edge of the hull: nothing to the west");
            Assert.That(HeatLinks(r.Core), Is.Zero);

            // Stop at 1200 K and push cold nitrogen into the west heat sink line (pipes run north -> south).
            sys.Stop(core);
            sys.SetTemperature(core, 1200f);
            var sink = r.At[new Vector2i(-3, 2)];
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
    public async Task UnlinkedRadiatorsDoNotDissipate()
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
            Floor(entMan, map, 4);
            // S Y U over H C R: the radiator at (1,0) does not touch the heat sink at (-1,0).
            r = BuildSmall(entMan, map);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            var c = core.Comp;
            sys.Scan(core);
            Assert.That(c.Radiators, Has.Count.EqualTo(1));
            Assert.That(c.LinkedRadiators, Is.Empty, "no heat sink at the end of the radiator line");
            Assert.That(c.LinkedHeatSinks, Is.Empty, "no radiator brings heat to the heat sink");
            Assert.That(sys.GetHeatCapacity(c), Is.EqualTo(c.BaseHeatCapacity + 25000f).Within(1f), "the radiator still buffers heat");

            sys.SetTemperature(core, 1200f);
            var sink = r.At[new Vector2i(-1, 0)];
            Assert.That(nodes.TryGetNode(sink, "inlet", out PipeNode? inlet), Is.True);
            inlet!.Air.AdjustMoles(Gas.Nitrogen, 2000f);
            inlet.Air.Temperature = 80f;

            for (var i = 0; i < 5; i++)
                sys.Step(core, 1f);
            Assert.That(c.Temperature, Is.EqualTo(1200f).Within(0.01f), "unlinked radiators dump nothing");
            Assert.That(entMan.GetComponent<KamertonHeatSinkComponent>(sink).LastHeatRemoved, Is.Zero);
            Assert.That(inlet.Air.Temperature, Is.EqualTo(80f).Within(0.01f), "the gas stays cold");
            sys.SetTemperature(core, c.AmbientTemperature);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EdgeHeatSinksCountAsShell()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        EntityUid core = default, edge = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 4);
            core = entMan.SpawnEntity("KamertonReactorCore", Tile(map, 0, 0));
            for (var x = -1; x <= 1; x++)
            for (var y = -1; y <= 1; y++)
            {
                if (x == 0 && y == 0)
                    continue;
                var uid = entMan.SpawnEntity(x == 1 && y == 0 ? "KamertonHeatSink" : "KamertonSuppressor", Tile(map, x, y));
                if (x == 1 && y == 0)
                    edge = uid;
            }
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = (core, entMan.GetComponent<KamertonReactorCoreComponent>(core));
            sys.Scan(c);
            Assert.That(c.Item2.HeatSinks, Is.EquivalentTo(new[] { edge }));
            Assert.That(c.Item2.RingMissing, Is.Zero, "a heat sink on the edge closes the shell like a suppressor");

            // A radiator in the same spot is an inner part: its outer neighbours must be filled too.
            entMan.DeleteEntity(edge);
            entMan.SpawnEntity("KamertonRadiator", Tile(map, 1, 0));
        });
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var c = (core, entMan.GetComponent<KamertonReactorCoreComponent>(core));
            sys.Scan(c);
            Assert.That(c.Item2.Radiators, Has.Count.EqualTo(1));
            Assert.That(c.Item2.RingMissing, Is.EqualTo(3), "(2,-1), (2,0), (2,1) are open");
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
            var missing = r.Skipped[0];
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

            // Hull neighbours (N=1, S=2, E=4, W=8, NE=16, NW=32, SE=64, SW=128): the core is surrounded, a shell corner has S, E and SE.
            Assert.That(Links(entMan, r.Core), Is.EqualTo(255));
            Assert.That(Links(entMan, r.At[new Vector2i(-2, 2)]), Is.EqualTo(2 | 4 | 64));
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
            Assert.That(Links(entMan, sync), Is.EqualTo(255 & ~8), "the synchronizer's hull opens to the west");

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

    [Test]
    public async Task FuelStorageShellAndCapacity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var drones = server.System<KamertonDroneSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 5);
            r = Build(entMan, map, fillCells: false);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            sys.Scan(c);
            Assert.That(c.Comp.RingMissing, Is.Zero, "the fuel storage is a shell part");
            Assert.That(c.Comp.Storage, Is.EqualTo(r.Storage));

            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            Assert.That(drones.TryStore(st, entMan.SpawnEntity(StableEmpty, Tile(map, 0, 5))), Is.False, "spent cells are not stored");
            for (var i = 0; i < 10; i++)
            {
                Assert.That(drones.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, 5))), Is.True);
                Assert.That(drones.TryStore(st, entMan.SpawnEntity(UnstableFull, Tile(map, 0, 5))), Is.True);
            }
            Assert.That(drones.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, 5))), Is.False, "10 stable max");
            Assert.That(drones.TryStore(st, entMan.SpawnEntity(UnstableFull, Tile(map, 0, 5))), Is.False, "10 unstable max");
            Assert.That(drones.CountStored(st, KamertonCellKind.Stable), Is.EqualTo(10));
            Assert.That(drones.CountStored(st, KamertonCellKind.Unstable), Is.EqualTo(10));

            var info = drones.BuildInfo(c.Comp);
            Assert.That(info.HasStorage && !info.HasDrone, Is.True);
            Assert.That(info.FreeStable, Is.EqualTo(16));
            Assert.That(info.FreeUnstable, Is.EqualTo(16));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DroneRefuelsAndUnloadsSpentCells()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var drones = server.System<KamertonDroneSystem>();
        var slots = server.System<ItemSlotsSystem>();
        Reactor r = default!;
        EntityUid drone = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 5);
            r = Build(entMan, map, fillCells: false);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            sys.Scan(c);
            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            for (var i = 0; i < 3; i++)
                Assert.That(drones.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, -1, 3))), Is.True);

            drone = drones.SpawnDrone(c, Tile(map, -1, 4));
            Assert.That(c.Comp.Drone, Is.EqualTo(drone));
            var d = (drone, entMan.GetComponent<KamertonDroneComponent>(drone));
            Assert.That(d.Item2.Core, Is.EqualTo(r.Core));

            for (var i = 0; i < 6; i++)
                drones.Think(d, 1f);

            Assert.That(drones.CountStored(st, KamertonCellKind.Stable), Is.Zero, "all three cells went into the arrays");
            Assert.That(drones.BuildInfo(c.Comp).FreeStable, Is.EqualTo(13));
            Assert.That(d.Item2.State, Is.EqualTo(KamertonDroneState.Idle));

            // Swap one loaded cell for a spent one: the drone pulls it out and drops it outside.
            var array = c.Comp.StableArrays.First(a => slots.GetItemOrNull(a, KamertonReactorSystem.SlotId(1)) != null);
            slots.TryEject(array, KamertonReactorSystem.SlotId(1), null, out var full);
            entMan.DeleteEntity(full);
            Assert.That(slots.TryInsert(array, KamertonReactorSystem.SlotId(1), entMan.SpawnEntity(StableEmpty, Tile(map, 0, 5)), null), Is.True);
            Assert.That(drones.BuildInfo(c.Comp).Spent, Is.EqualTo(1));
            Assert.That(drones.PickTask(c.Comp), Is.EqualTo(KamertonDroneCommand.Unload));

            for (var i = 0; i < 3; i++)
                drones.Think(d, 1f);

            Assert.That(drones.BuildInfo(c.Comp).Spent, Is.Zero);
            var spent = entMan.EntityQuery<KamertonCellComponent>().Select(x => x.Owner)
                .Where(x => entMan.GetComponent<MetaDataComponent>(x).EntityPrototype?.ID == StableEmpty).ToList();
            Assert.That(spent, Has.Count.EqualTo(1));
            Assert.That(entMan.GetComponent<TransformComponent>(spent[0]).ParentUid, Is.EqualTo(map.Grid.Owner), "dropped on the floor, not in a container");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DroneSpawnerLeashAndReturn()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var drones = server.System<KamertonDroneSystem>();
        var xforms = server.System<SharedTransformSystem>();
        EntityUid core = default, drone = default;

        await server.WaitAssertion(() =>
        {
            var flat = proto.Index<EntityPrototype>("KamertonReactorCoreFlatpack");
            Assert.That(flat.TryGetComponent<KamertonDroneSpawnerComponent>(out var sp, factory), Is.True);
            Assert.That(sp!.Drone.Id, Is.EqualTo("KamertonDroneRDZT"));
            var dp = proto.Index<EntityPrototype>("KamertonDroneRDZT");
            Assert.That(dp.TryGetComponent<KamertonDroneComponent>(out var dc, factory), Is.True);
            Assert.That(dc!.WanderRadius, Is.EqualTo(6f));
            Assert.That(dc.LeashRadius, Is.EqualTo(8f));

            Floor(entMan, map, 14);
            core = entMan.SpawnEntity("KamertonReactorCore", Tile(map, 0, 0));
            drone = drones.SpawnDrone((core, entMan.GetComponent<KamertonReactorCoreComponent>(core)), Tile(map, 12, 0));
            var d = (drone, entMan.GetComponent<KamertonDroneComponent>(drone));
            drones.Think(d, 0.1f);
            Assert.That(d.Item2.State, Is.EqualTo(KamertonDroneState.Returning), "beyond the 8-tile leash");

            xforms.SetCoordinates(drone, Tile(map, 1, 0));
            drones.Think(d, 0.1f);
            Assert.That(d.Item2.State, Is.EqualTo(KamertonDroneState.Idle));

            for (var i = 0; i < 20; i++)
            {
                drones.Think(d, 1f);
                if (d.Item2.Goal is { } g)
                    Assert.That((g - xforms.GetWorldPosition(core)).Length(), Is.LessThanOrEqualTo(6.01f), "wanders within 6 tiles");
            }

            drones.Command(d, KamertonDroneCommand.Return);
            Assert.That(d.Item2.State == KamertonDroneState.Returning && !d.Item2.AutoRefuel, Is.True);
            drones.Command(d, KamertonDroneCommand.ToggleAuto);
            Assert.That(d.Item2.AutoRefuel, Is.True);
        });

        await pair.CleanReturnAsync();
    }
}

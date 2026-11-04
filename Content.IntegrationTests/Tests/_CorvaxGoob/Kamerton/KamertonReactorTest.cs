#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Kamerton;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Shared._CorvaxGoob.Kamerton;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Atmos;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Interaction;
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
        "KamertonFuelStorage", "KamertonSzk",
    };

    /// <summary>
    /// Standard layout, rows from y = +3 down to y = -3 (S = stable, U = unstable, Y = sync, H = heat sink,
    /// R = radiator, C = core, Q = suppressor, K = console, P = power unit, F = fuel storage, Z = SZK). Two parallel lines on each side:
    /// radiators inside, heat sinks on the edge (their pipes run north-south and leave the reactor).
    /// </summary>
    private static readonly string[] Layout =
    {
        "HZFKQQH",
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
        public EntityUid Szk;
        public readonly List<Vector2i> Skipped = new();
    }

    private static EntityCoordinates Tile(TestMapData map, int x, int y) => new(map.Grid.Owner, x + 0.5f, y + 0.5f);

    /// <summary>
    /// Sets the gas on every grid tile around the test reactor. A grid this size gets its own
    /// atmosphere, and its inner tiles start empty, so the map atmosphere alone does not reach the core.
    /// </summary>
    private static void SetAir(IEntityManager entMan, TestMapData map, float nitrogen, float oxygen = 0f)
    {
        var mix = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        mix.AdjustMoles(Gas.Nitrogen, nitrogen);
        mix.AdjustMoles(Gas.Oxygen, oxygen);
        var atmos = entMan.System<AtmosphereSystem>();
        atmos.SetMapAtmosphere(map.MapUid, nitrogen + oxygen <= 0f, nitrogen + oxygen <= 0f ? GasMixture.SpaceGas : mix);
        if (!entMan.TryGetComponent<GridAtmosphereComponent>(map.Grid.Owner, out var gridAtmos))
            return;
        foreach (var tile in gridAtmos.Tiles.Values)
        {
            if (tile.MapAtmosphere || tile.Air == null || tile.Air.Immutable)
                continue;
            tile.Air.CopyFrom(mix);
        }
    }

    private static void Breathable(IEntityManager entMan, TestMapData map)
    {
        SetAir(entMan, map, Atmospherics.NitrogenMolesStandard, Atmospherics.OxygenMolesStandard);
    }

    /// <summary>
    /// Atmos sets up the tiles of a freshly built floor over several ticks, so right after building the core
    /// tile can still have no air of its own. Keeps filling the air until the core really is out of vacuum.
    /// </summary>
    private static async Task WaitForAir(TestPair pair, IEntityManager entMan, TestMapData map, KamertonReactorSystem sys, Reactor r)
    {
        for (var i = 0; i < 60; i++)
        {
            var ok = false;
            await pair.Server.WaitPost(() =>
            {
                Breathable(entMan, map);
                ok = !sys.CoreInVacuum(Core(entMan, r));
            });
            if (ok)
                return;
            await pair.RunTicksSync(1);
        }

        Assert.Fail("the core tile never got air");
    }

    private static void Floor(IEntityManager entMan, TestMapData map, int radius)
    {
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++)
            tiles.Add((new Vector2i(x, y), map.Tile.Tile));
        entMan.System<SharedMapSystem>().SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
        // Breathable air around the test reactor: a core in vacuum does not start.
        var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
        air.AdjustMoles(Gas.Oxygen, Atmospherics.OxygenMolesStandard);
        air.AdjustMoles(Gas.Nitrogen, Atmospherics.NitrogenMolesStandard);
        entMan.System<AtmosphereSystem>().SetMapAtmosphere(map.MapUid, false, air);
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
                'Z' => "KamertonSzk",
                _ => "KamertonReactorCore",
            };
            var isRing = ch is 'Q' or 'K' or 'P' or 'F' or 'Z';
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
            else if (ch == 'Z')
                r.Szk = uid;

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
            Assert.That(c.BaseMaxHeat, Is.EqualTo(500f));
            Assert.That(c.MaxHeatPerRadiator, Is.EqualTo(250f));
            Assert.That(c.MeltdownFraction, Is.EqualTo(0.75f));
            Assert.That(c.PassiveCooling, Is.EqualTo(2f));
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
            foreach (var s in new[] { "base", "icon-core", "icon-stable", "icon-unstable", "icon-sync", "icon-heatsink", "icon-radiator", "icon-suppressor", "icon-powerunit", "icon-console", "icon-storage", "icon-szk" })
                Assert.That(box.TryGetState(s, out _), Is.True, s);

            var szk = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Structures/Power/Generation/kamerton_szk.rsi")).RSI;
            foreach (var s in new[] { "rail_0", "rail_15", "carriage", "carriage_lights", "load_stable", "load_unstable", "load_spent", "fx_pickup", "fx_zap" })
                Assert.That(szk.TryGetState(s, out _), Is.True, s);
            Assert.That(reactor.TryGetState("szk", out _), Is.True);
            var hull = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Structures/Power/Generation/kamerton_hull.rsi")).RSI;
            for (var i = 0; i < 16; i++)
                Assert.That(hull.TryGetState($"sinklink_{i}", out _), Is.True);
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
            Assert.That(c.LinkedHeatSinks, Has.Count.EqualTo(14), "sinks next to radiators and, through the sink chain, the corner sinks");
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

            Breathable(entMan, map);
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
            sys.SetHeat(core, 0f);
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
            Breathable(entMan, map);
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
        await WaitForAir(pair, entMan, map, sys, r);

        await server.WaitPost(() =>
        {
            var core = Core(entMan, r);
            Breathable(entMan, map);
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
            Breathable(entMan, map);
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);

            // No coolant anywhere: 16 pairs heat the core.
            Assert.That(c.Heat, Is.Zero, "starts at ambient");
            Assert.That(sys.GetMaxHeat(c), Is.EqualTo(500f + 250f * c.Radiators.Count));
            sys.Step(core, 10f);
            Assert.That(c.Heat, Is.EqualTo(16 * c.HeatPerPair * 10f).Within(0.5f), "16 pairs at 15 heat each: +240 heat per second without cooling");

            // Heat-pipe joints: radiator-radiator and radiator-heat sink (N=1, S=2, E=4, W=8).
            var appearance = entMan.System<SharedAppearanceSystem>();
            int HeatLinks(EntityUid u) => appearance.TryGetData<int>(u, KamertonReactorVisuals.HeatLinks, out var f) ? f : 0;
            Assert.That(HeatLinks(r.At[new Vector2i(-2, 0)]), Is.EqualTo(15), "radiator: heat pipes all round");
            Assert.That(Links(entMan, r.At[new Vector2i(-2, 0)]), Is.EqualTo(255), "hull joined to all 8 neighbours");
            Assert.That(HeatLinks(r.At[new Vector2i(-3, 2)]) & 15, Is.EqualTo(4), "edge heat sink: heat pipe to its radiator");
            Assert.That(HeatLinks(r.At[new Vector2i(-3, 2)]) >> 4, Is.EqualTo(1 | 2), "chained to the sinks north and south");
            Assert.That(HeatLinks(r.At[new Vector2i(-3, 3)]) >> 4, Is.EqualTo(2), "corner sink: chained to the south only");
            Assert.That(Links(entMan, r.At[new Vector2i(-3, 2)]), Is.EqualTo(1 | 2 | 4 | 16 | 64), "edge of the hull: nothing to the west");
            Assert.That(HeatLinks(r.Core), Is.Zero);

            // Stop at 900 heat and push cold nitrogen into the west heat sink line.
            sys.Stop(core);
            sys.SetHeat(core, 900f);
            var sink = r.At[new Vector2i(-3, 2)];
            Assert.That(nodes.TryGetNode(sink, "pipe", out PipeNode? pipe), Is.True);
            pipe!.Air.AdjustMoles(Gas.Nitrogen, 2000f);
            pipe.Air.Temperature = 80f;

            sys.Step(core, 1f);
            Assert.That(c.Heat, Is.LessThan(900f - c.PassiveCooling), "cold gas cools the core faster than passive cooling");
            Assert.That(pipe.Air.Temperature, Is.GreaterThan(80f), "the gas carries the heat away");
            var sinkComp = entMan.GetComponent<KamertonHeatSinkComponent>(sink);
            Assert.That(sinkComp.LastHeatRemoved, Is.GreaterThan(0f));

            var before = c.Heat;
            for (var i = 0; i < 10; i++)
                sys.Step(core, 1f);
            Assert.That(c.Heat, Is.LessThan(before));
            Assert.That(c.Heat, Is.GreaterThanOrEqualTo(0f), "never below ambient");
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
            Assert.That(sys.GetMaxHeat(c), Is.EqualTo(750f), "the radiator still raises the heat limit");

            sys.SetHeat(core, 300f);
            var sink = r.At[new Vector2i(-1, 0)];
            Assert.That(nodes.TryGetNode(sink, "pipe", out PipeNode? pipe), Is.True);
            pipe!.Air.AdjustMoles(Gas.Nitrogen, 2000f);
            pipe.Air.Temperature = 80f;

            for (var i = 0; i < 5; i++)
                sys.Step(core, 1f);
            Assert.That(c.Heat, Is.EqualTo(300f - 5 * c.PassiveCooling).Within(0.01f), "unlinked: only the passive 2/s cooling");
            Assert.That(entMan.GetComponent<KamertonHeatSinkComponent>(sink).LastHeatRemoved, Is.Zero);
            Assert.That(pipe.Air.Temperature, Is.EqualTo(80f).Within(0.01f), "the gas stays cold");
            sys.SetHeat(core, 0f);
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
            Breathable(entMan, map);
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
            sys.SetHeat(core, 0f);
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

            Breathable(entMan, map);
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            sys.Step(core, 1f);
            Assert.That(c.ActivePairs, Is.EqualTo(4));
            Assert.That(c.TotalPower, Is.EqualTo(200000f));
            sys.Stop(core);
            sys.SetHeat(core, 0f);
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
            Breathable(entMan, map);
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
            sys.SetHeat(core, 0f);
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
            sys.SetHeat(core, sys.GetMaxHeat(core.Comp) + 10f);
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
                if (meta.EntityPrototype?.ID == "KamertonTesla")
                {
                    tesla++;
                    spawned.Add(uid);
                    Assert.That(dist, Is.LessThan(1.5f), "the Kamerton tesla at the core");
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
        var szk = server.System<KamertonSzkSystem>();
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
            Assert.That(c.Comp.RingMissing, Is.Zero, "the fuel storage and the SZK are shell parts");
            Assert.That(c.Comp.Storage, Is.EqualTo(r.Storage));
            Assert.That(c.Comp.Szk, Is.EqualTo(r.Szk));

            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            Assert.That(szk.TryStore(st, entMan.SpawnEntity(StableEmpty, Tile(map, 0, 5))), Is.False, "spent cells are not stored");
            for (var i = 0; i < 10; i++)
            {
                Assert.That(szk.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, 5))), Is.True);
                Assert.That(szk.TryStore(st, entMan.SpawnEntity(UnstableFull, Tile(map, 0, 5))), Is.True);
            }
            Assert.That(szk.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, 5))), Is.False, "10 stable max");
            Assert.That(szk.TryStore(st, entMan.SpawnEntity(UnstableFull, Tile(map, 0, 5))), Is.False, "10 unstable max");
            Assert.That(szk.CountStored(st, KamertonCellKind.Stable), Is.EqualTo(10));
            Assert.That(szk.CountStored(st, KamertonCellKind.Unstable), Is.EqualTo(10));

            var info = szk.BuildInfo(c.Comp);
            Assert.That(info.HasStorage && info.HasSzk, Is.True);
            Assert.That(info.FreeStable, Is.EqualTo(16));
            Assert.That(info.FreeUnstable, Is.EqualTo(16));
        });

        await pair.CleanReturnAsync();
    }

    private static int CountRails(IEntityManager entMan)
        => entMan.EntityQuery<KamertonSzkRailComponent>().Count();

    /// <summary>
    /// The SZK lays a rail lattice over the whole reactor footprint with a carriage on it; unanchoring it
    /// removes the lattice and the carriage, anchoring it back lays them again.
    /// </summary>
    [Test]
    public async Task SzkLatticeAppearsAndDisappears()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var xforms = server.System<SharedTransformSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 5);
            r = Build(entMan, map, fillCells: false);
        });
        await pair.RunSeconds(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountRails(entMan), Is.EqualTo(49), "one rail piece per reactor tile");
            Assert.That(entMan.EntityQuery<KamertonSzkCarriageComponent>().Count(), Is.EqualTo(1));
            var c = Core(entMan, r);
            Assert.That(c.Comp.Footprint, Has.Count.EqualTo(49));
            xforms.Unanchor(r.Szk);
        });
        await pair.RunSeconds(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountRails(entMan), Is.Zero, "no SZK, no lattice");
            Assert.That(entMan.EntityQuery<KamertonSzkCarriageComponent>().Count(), Is.Zero);
            xforms.AnchorEntity(r.Szk);
        });
        await pair.RunSeconds(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(CountRails(entMan), Is.EqualTo(49));
            entMan.DeleteEntity(r.Szk);
        });
        await pair.RunSeconds(2);

        await server.WaitAssertion(() => Assert.That(CountRails(entMan), Is.Zero));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The console's Load button sends the carriage from the storage to the arrays; in auto mode it loads new
    /// cells and takes spent ones out by itself.
    /// </summary>
    [Test]
    public async Task SzkLoadsOnConsoleCommandAndAuto()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var szk = server.System<KamertonSzkSystem>();
        var slots = server.System<ItemSlotsSystem>();
        Reactor r = default!;
        EntityUid user = default;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            r = Build(entMan, map, fillCells: false);
            var z = entMan.GetComponent<KamertonSzkComponent>(r.Szk);
            z.StepInterval = 0.05f;
            z.ActionInterval = 0.1f;
            z.Auto = false;
            user = entMan.SpawnEntity("MobHuman", Tile(map, 0, -6));
        });
        await pair.RunSeconds(1);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            sys.Scan(c);
            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            for (var i = 0; i < 3; i++)
                Assert.That(szk.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, -6))), Is.True);

            entMan.EventBus.RaiseLocalEvent(r.Console, new KamertonSzkCommandMessage(KamertonSzkCommand.Load) { Actor = user });
            Assert.That(entMan.GetComponent<KamertonSzkComponent>(r.Szk).State, Is.EqualTo(KamertonSzkState.Working));
        });
        await pair.RunSeconds(15);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            Assert.That(szk.CountStored(st, KamertonCellKind.Stable), Is.Zero, "the carriage loaded every cell");
            Assert.That(szk.BuildInfo(c.Comp).FreeStable, Is.EqualTo(13));

            // One spent cell in an array and two fresh cells in the storage: auto mode handles both.
            var array = c.Comp.StableArrays.First(a => slots.GetItemOrNull(a, KamertonReactorSystem.SlotId(1)) != null);
            slots.TryEject(array, KamertonReactorSystem.SlotId(1), null, out var full);
            entMan.DeleteEntity(full);
            Assert.That(slots.TryInsert(array, KamertonReactorSystem.SlotId(1), entMan.SpawnEntity(StableEmpty, Tile(map, 0, -6)), null), Is.True);
            for (var i = 0; i < 2; i++)
                Assert.That(szk.TryStore(st, entMan.SpawnEntity(StableFull, Tile(map, 0, -6))), Is.True);
            entMan.EventBus.RaiseLocalEvent(r.Console, new KamertonSzkCommandMessage(KamertonSzkCommand.ToggleAuto) { Actor = user });
            Assert.That(entMan.GetComponent<KamertonSzkComponent>(r.Szk).Auto, Is.True);
        });
        await pair.RunSeconds(25);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            var st = (r.Storage, entMan.GetComponent<KamertonFuelStorageComponent>(r.Storage));
            var info = szk.BuildInfo(c.Comp);
            Assert.That(info.Spent, Is.Zero, "auto mode took the spent cell out");
            Assert.That(szk.CountStored(st, KamertonCellKind.Stable), Is.Zero, "auto mode loaded the new cells");
            Assert.That(info.FreeStable, Is.EqualTo(12), "16 slots: 2 stay loaded, the spent one is out, 2 new ones in");
            var spent = entMan.EntityQuery<KamertonCellComponent>().Select(x => x.Owner)
                .Where(x => entMan.GetComponent<MetaDataComponent>(x).EntityPrototype?.ID == StableEmpty).ToList();
            Assert.That(spent, Has.Count.EqualTo(1));
            Assert.That(entMan.GetComponent<TransformComponent>(spent[0]).ParentUid, Is.EqualTo(map.Grid.Owner), "left on the hatch tile");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Vacuum at the core (below 10 kPa): the reactor refuses to start and stops itself if running, unless it is
    /// in meltdown, where the vacuum changes nothing.
    /// </summary>
    [Test]
    public async Task VacuumAtCoreStopsReactor()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        Reactor r = default!;

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 5);
            r = Build(entMan, map, fillCells: true);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            Breathable(entMan, map);
            Assert.That(sys.TryStart(c, out _), Is.True);
            sys.Step(c, 1f);
            Assert.That(c.Comp.Working && c.Comp.ActivePairs > 0, Is.True);

            SetAir(entMan, map, 0.5f); // about 5 kPa
            sys.Step(c, 1f);
            Assert.That(c.Comp.InVacuum, Is.True);
            Assert.That(c.Comp.Working, Is.False, "auto-stop in vacuum");
            Assert.That(c.Comp.TotalPower, Is.Zero);
            var heat = c.Comp.Heat;
            sys.Step(c, 1f);
            Assert.That(c.Comp.Heat, Is.LessThanOrEqualTo(heat), "no pair heating");
            Assert.That(sys.TryStart(c, out var reason), Is.False);
            Assert.That(reason, Is.EqualTo("kamerton-reactor-popup-vacuum"));

            // In meltdown the vacuum changes nothing.
            Breathable(entMan, map);
            Assert.That(sys.TryStart(c, out _), Is.True);
            sys.SetHeat(c, sys.GetMeltdownHeat(c.Comp) + 50f);
            sys.Step(c, 0.1f);
            SetAir(entMan, map, 0f);
            heat = c.Comp.Heat;
            sys.Step(c, 1f);
            Assert.That(c.Comp.InVacuum && c.Comp.Meltdown, Is.True);
            Assert.That(c.Comp.Working, Is.True, "meltdown runs on in vacuum");
            Assert.That(c.Comp.ActivePairs, Is.GreaterThan(0));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A full reactor (128 heat/s) on cold nitrogen at 1 atm and 150 K: the heat sinks hold the heat.</summary>
    [Test]
    public async Task HeatSinksCarryFullReactor()
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
            Floor(entMan, map, 5);
            r = Build(entMan, map, fillCells: true);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var c = Core(entMan, r);
            Breathable(entMan, map);
            Assert.That(sys.TryStart(c, out _), Is.True);
            sys.Step(c, 0.5f);
            Assert.That(c.Comp.ActivePairs * c.Comp.HeatPerPair, Is.EqualTo(240f));
            Assert.That(c.Comp.LinkedHeatSinks, Has.Count.EqualTo(14), "both sink lines linked as chains");
            // Cold nitrogen, 1 atm and 150 K, in every sink: equilibrium is near heat 1030,
            // well under the meltdown threshold.
            sys.SetHeat(c, 1500f);
            for (var i = 0; i < 60; i++)
            {
                foreach (var sink in c.Comp.LinkedHeatSinks)
                {
                    Assert.That(nodes.TryGetNode(sink, "pipe", out PipeNode? pipe), Is.True);
                    pipe!.Air.Clear();
                    pipe.Air.Temperature = 150f;
                    pipe.Air.AdjustMoles(Gas.Nitrogen, Atmospherics.OneAtmosphere * pipe.Air.Volume / (Atmospherics.R * 150f));
                }
                sys.Step(c, 0.5f);
            }
            Assert.That(c.Comp.Heat, Is.LessThan(1300f), "the sinks remove more than 240 heat/s above heat 1100");
            Assert.That(c.Comp.Heat, Is.GreaterThan(900f), "and less than 240 heat/s below heat 1000");
            Assert.That(c.Comp.Meltdown, Is.False);
            Assert.That(c.Comp.Collapsed, Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MeltdownLocksStopAndThrowsArcs()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = server.System<KamertonReactorSystem>();
        var arcs = server.System<KamertonMeltdownSystem>();
        Reactor r = default!;
        var arcsBefore = 0;

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
            Breathable(entMan, map);
            Assert.That(sys.TryStart(core, out var reason), Is.True, reason);
            Assert.That(sys.GetMaxHeat(c), Is.EqualTo(3500f), "500 + 12 radiators x 250");
            Assert.That(sys.GetMeltdownHeat(c), Is.EqualTo(2625f));

            sys.SetHeat(core, 2000f);
            Assert.That(c.Meltdown, Is.False);
            sys.SetHeat(core, 2700f);
            Assert.That(c.Meltdown, Is.True);
            Assert.That(sys.TryStop(core, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("kamerton-reactor-popup-controls-unresponsive"));
            Assert.That(sys.TryScram(core, out _), Is.False);
            Assert.That(c.Working, Is.True, "no way to stop it in meltdown");
            arcsBefore = arcs.ArcsThrown;
        });

        await pair.RunSeconds(2);

        await server.WaitAssertion(() =>
        {
            var core = Core(entMan, r);
            Assert.That(arcs.ArcsThrown, Is.GreaterThan(arcsBefore), "meltdown throws red-blue arcs");
            var proto = entMan.EntityQuery<MetaDataComponent>().Count(m => m.EntityPrototype?.ID is "KamertonArcCrawlRed" or "KamertonArcCrawlBlue");
            Assert.That(proto, Is.GreaterThan(0));

            sys.SetHeat(core, 1000f);
            Assert.That(core.Comp.Meltdown, Is.False);
            Assert.That(sys.TryStop(core, out _), Is.True);
            sys.SetHeat(core, 100f);
            sys.Step(core, 10f);
            Assert.That(core.Comp.Heat, Is.EqualTo(80f).Within(0.5f), "switched off: passive cooling 2 per second");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Heat sinks are straight pipes: gas crosses a line of three, in either orientation.</summary>
    [Test]
    public async Task GasFlowsThroughHeatSinkLine()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var nodes = server.System<NodeContainerSystem>();
        var xforms = server.System<SharedTransformSystem>();
        var a = new EntityUid[2];
        var b = new EntityUid[2];
        var sinks = new List<EntityUid>[] { new(), new() };

        await server.WaitPost(() =>
        {
            Floor(entMan, map, 6);
            // north-south line at x = -2, east-west line at y = -2
            for (var line = 0; line < 2; line++)
            {
                var rot = line == 0 ? Angle.Zero : Angle.FromDegrees(90);
                EntityCoordinates At(int i) => line == 0 ? Tile(map, -2, i) : Tile(map, i, -2);
                a[line] = entMan.SpawnEntity("GasPipeStraight", At(0));
                xforms.SetLocalRotation(a[line], rot);
                for (var i = 1; i <= 3; i++)
                {
                    var sink = entMan.SpawnEntity("KamertonHeatSink", At(i));
                    xforms.SetLocalRotation(sink, rot);
                    sinks[line].Add(sink);
                }

                b[line] = entMan.SpawnEntity("GasPipeStraight", At(4));
                xforms.SetLocalRotation(b[line], rot);
            }
        });
        await pair.RunTicksSync(10);

        await server.WaitAssertion(() =>
        {
            for (var line = 0; line < 2; line++)
            {
                Assert.That(nodes.TryGetNode(a[line], "pipe", out PipeNode? start), Is.True);
                Assert.That(nodes.TryGetNode(b[line], "pipe", out PipeNode? end), Is.True);
                foreach (var sink in sinks[line])
                {
                    Assert.That(nodes.TryGetNode(sink, "pipe", out PipeNode? mid), Is.True);
                    Assert.That(mid!.NodeGroup, Is.SameAs(start!.NodeGroup), $"line {line}: heat sink joins the pipe net");
                }

                Assert.That(end!.NodeGroup, Is.SameAs(start!.NodeGroup), $"line {line}: gas reaches the far pipe");
                start.Air.AdjustMoles(Gas.Frezon, 100f);
                Assert.That(end.Air.GetMoles(Gas.Frezon), Is.GreaterThan(0f), $"line {line}: frezon is not stuck");
            }
        });

        await pair.CleanReturnAsync();
    }
}

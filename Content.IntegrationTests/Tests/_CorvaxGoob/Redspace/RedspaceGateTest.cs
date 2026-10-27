using System.Collections.Generic;
using System.Linq;
using Robust.Shared.Maths;
using System.Numerics;
using Content.Server._CorvaxGoob.Redspace;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Interaction;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Content.Shared.Teleportation.Components;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

[TestFixture]
[TestOf(typeof(RedspaceGateSystem))]
public sealed class RedspaceGateTest
{
    private const string Gate = "RedspaceGate";
    private const string Flatpack = "RedspaceGateFlatpack";
    private const string Unfolding = "RedspaceGateUnfolding";
    private const string Portal = "RedspacePortal";
    private const string Redspace = "MaterialRedspaceCrystal1";
    private const string Bluespace = "MaterialBSCrystal1";

    private static readonly (string Rsi, string[] States)[] Sprites =
    {
        ("/Textures/_CorvaxGoob/Structures/Machines/redspace_gate.rsi", new[] { "frame", "on", "portal", "rack" }
            .Concat(Enumerable.Range(0, 11).Select(i => $"bs_{i}"))
            .Concat(Enumerable.Range(0, 13).Select(i => $"rs_{i}")).ToArray()),
        ("/Textures/_CorvaxGoob/Structures/Machines/redspace_gate_unfold.rsi", new[] { "unfold" }),
        ("/Textures/_CorvaxGoob/Objects/Devices/redspace_gate_flatpack.rsi", new[] { "icon", "inhand-left", "inhand-right" }),
        ("/Textures/_CorvaxGoob/Effects/redspace_portal.rsi", new[] { "portal" }),
        ("/Textures/_CorvaxGoob/Effects/redspace_crackle_large.rsi", new[] { "portal", "gate", "gate_idle", "portal_frame", "gate_frame" }),
    };

    [Test]
    public async Task PrototypesAndSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var gate = protoMan.Index<EntityPrototype>(Gate);
            Assert.That(gate.TryGetComponent<RedspaceGateComponent>(out var gateComp, factory), Is.True);
            Assert.That(protoMan.HasIndex<EntityPrototype>(gateComp!.PortalPrototype), Is.True);
            foreach (var stack in gateComp.RedspaceStacks.Concat(gateComp.BluespaceStacks))
                Assert.That(protoMan.HasIndex(stack), Is.True, stack.Id);
            Assert.That(gate.TryGetComponent<ItemSlotsComponent>(out var slots, factory), Is.True);
            Assert.That(slots, Is.Not.Null);

            var flatpack = protoMan.Index<EntityPrototype>(Flatpack);
            Assert.That(flatpack.TryGetComponent<RedspaceGateFlatpackComponent>(out var packComp, factory), Is.True);
            Assert.That(packComp!.Unfolding.Id, Is.EqualTo(Unfolding));
            var unfolding = protoMan.Index<EntityPrototype>(Unfolding);
            Assert.That(unfolding.TryGetComponent<RedspaceGateUnfoldingComponent>(out var unfoldComp, factory), Is.True);
            Assert.That(unfoldComp!.Gate.Id, Is.EqualTo(Gate));
            Assert.That(unfoldComp.Duration.TotalSeconds, Is.InRange(5.0, 6.0), "unfold should be long (5-6 s)");
            Assert.That(unfoldComp.ConnectTime, Is.LessThan(unfoldComp.Duration));

            var portal = protoMan.Index<EntityPrototype>(Portal);
            Assert.That(portal.TryGetComponent<PortalComponent>(out _, factory), Is.True);
            Assert.That(portal.TryGetComponent<RedspaceGatePortalComponent>(out _, factory), Is.True);

            // Portal crackles on its own, a bit more often than a crystal; the gate only once fuelled, faster while open.
            Assert.That(portal.TryGetComponent<PeriodicCrackleComponent>(out var portalCrackle, factory), Is.True);
            Assert.That(portalCrackle!.Enabled, Is.True);
            Assert.That(portalCrackle.MinInterval, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(portalCrackle.MaxInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(portalCrackle.Sound, Is.Not.Null);
            Assert.That(gate.TryGetComponent<PeriodicCrackleComponent>(out var gateCrackle, factory), Is.True);
            Assert.That(gateCrackle!.Enabled, Is.False, "an empty gate doesn't crackle");
            Assert.That(gateComp.RedspaceCapacity, Is.EqualTo(24));
            Assert.That(gateComp.BluespaceCapacity, Is.EqualTo(50));
            Assert.That(gateCrackle.MinInterval, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(gateCrackle.MaxInterval, Is.EqualTo(TimeSpan.FromSeconds(9)));

            Assert.That(protoMan.HasIndex<EntityPrototype>(Redspace), Is.True);
            Assert.That(protoMan.HasIndex<EntityPrototype>(Bluespace), Is.True);
            Assert.That(protoMan.HasIndex<EntityPrototype>("Multitool"), Is.True);
            Assert.That(protoMan.HasIndex<EntityPrototype>("CoordinatesDisk"), Is.True);
        });

        var resCache = pair.Client.ResolveDependency<IResourceCache>();
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var (rsi, states) in Sprites)
            {
                var res = resCache.GetResource<RSIResource>(new ResPath(rsi)).RSI;
                foreach (var state in states)
                    Assert.That(res.TryGetState(state, out _), Is.True, $"{rsi}:{state}");
            }

            var unfold = resCache.GetResource<RSIResource>(new ResPath(Sprites[1].Rsi)).RSI;
            unfold.TryGetState("unfold", out var unfoldState);
            Assert.That(unfoldState!.DelayCount, Is.GreaterThanOrEqualTo(20), "unfold should be a smooth 20+ frame animation");
            // Everything up to the final held frame matches the server-side duration; the last frame holds
            // long enough that the one-shot never loops back before the gate replaces it.
            var delays = unfoldState.GetDelays();
            Assert.That(delays.Take(delays.Length - 1).Sum(), Is.EqualTo(5.4f).Within(0.01f));
            Assert.That(delays[^1], Is.GreaterThan(10f));

            var large = resCache.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Effects/redspace_crackle_large.rsi")).RSI;
            Assert.That(large.TryGetState("gate_idle", out var idle), Is.True);
            Assert.That(idle!.DelayCount, Is.EqualTo(8));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MultitoolUnfold()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid pack = default;
        EntityUid unfolding = default;
        var start = TimeSpan.Zero;
        await server.WaitAssertion(() =>
        {
            pack = entMan.SpawnEntity(Flatpack, map.GridCoords);
            var user = entMan.SpawnEntity(null, map.GridCoords);
            var steel = entMan.SpawnEntity("SheetSteel1", map.GridCoords);
            var tool = entMan.SpawnEntity("Multitool", map.GridCoords);

            // Not a pulsing tool: nothing happens.
            entMan.EventBus.RaiseLocalEvent(pack, new InteractUsingEvent(user, steel, pack, map.GridCoords));
            Assert.That(entMan.EntityQuery<RedspaceGateUnfoldingComponent>().Count(), Is.EqualTo(0));

            entMan.EventBus.RaiseLocalEvent(pack, new InteractUsingEvent(user, tool, pack, map.GridCoords));
            var all = new List<(EntityUid Uid, RedspaceGateUnfoldingComponent Comp)>();
            var query = entMan.EntityQueryEnumerator<RedspaceGateUnfoldingComponent>();
            while (query.MoveNext(out var qUid, out var qComp))
                all.Add((qUid, qComp));
            Assert.That(all, Has.Count.EqualTo(1));
            unfolding = all[0].Uid;
            start = all[0].Comp.Start;
            Assert.That(start, Is.EqualTo(timing.CurTime), "animation starts now (frame 0)");
            Assert.That(entMan.GetComponent<TransformComponent>(unfolding).Anchored, Is.True);
            Assert.That(entMan.EntityQuery<RedspaceGateComponent>().Count(), Is.EqualTo(0));
        });

        // The flatpack is gone right away; the client has the stand-in with the visible one-shot unfold layer.
        await pair.RunTicksSync(5);
        await server.WaitAssertion(() => Assert.That(entMan.Deleted(pack), Is.True));
        await client.WaitAssertion(() =>
        {
            var cUnfolding = cEntMan.GetEntity(entMan.GetNetEntity(unfolding));
            Assert.That(cEntMan.TryGetComponent<RedspaceGateUnfoldingComponent>(cUnfolding, out var cComp), Is.True);
            Assert.That(cComp!.Start, Is.EqualTo(start), "client got the start time to sync the animation");
            var sprite = cEntMan.GetComponent<SpriteComponent>(cUnfolding);
            var spriteSys = cEntMan.System<SpriteSystem>();
            Assert.That(spriteSys.LayerMapTryGet((cUnfolding, sprite), RedspaceGateUnfoldingLayers.Unfold, out var layer, false), Is.True);
            Assert.That(sprite[layer].Visible, Is.True);
            Assert.That(sprite[layer].RsiState.Name, Is.EqualTo("unfold"));
        });

        // The stand-in stays for the whole animation; no gate yet just before the end.
        foreach (var t in new[] { 1.5f, 3.5f })
        {
            await pair.RunSeconds(t - (float) (timing.CurTime - start).TotalSeconds);
            await server.WaitAssertion(() =>
            {
                Assert.That(entMan.EntityExists(unfolding), Is.True, $"still unfolding at {t}s");
                Assert.That(entMan.EntityQuery<RedspaceGateComponent>().Count(), Is.EqualTo(0));
            });
        }

        await pair.RunSeconds(5.2f - (float) (timing.CurTime - start).TotalSeconds);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityExists(unfolding), Is.True, "still unfolding at 5.2s");
            Assert.That(entMan.GetComponent<RedspaceGateUnfoldingComponent>(unfolding).Connected, Is.True, "pillars connected");
            Assert.That(entMan.EntityQuery<RedspaceGateComponent>().Count(), Is.EqualTo(0));
        });

        await pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(unfolding), Is.True);
            Assert.That(entMan.EntityQuery<RedspaceGateComponent>().Count(), Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid Stack(IEntityManager entMan, StackSystem stack, string proto, int count, EntityCoordinates coords)
    {
        var uid = entMan.SpawnEntity(proto, coords);
        stack.SetCount(uid, count);
        return uid;
    }

    [Test]
    public async Task FuelPoolsAndLevels()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var appearance = entMan.System<SharedAppearanceSystem>();

        int Level(EntityUid uid, RedspaceGateVisuals key)
        {
            Assert.That(appearance.TryGetData<int>(uid, key, out var level), Is.True, key.ToString());
            return level;
        }

        await server.WaitAssertion(() =>
        {
            var gate = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            Assert.That(Level(gate, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(0));
            Assert.That(Level(gate, RedspaceGateVisuals.RedspaceLevel), Is.EqualTo(0));

            // Redspace goes into the gate itself: 24 max, one socketed crystal per 2.
            var red = Stack(entMan, stack, Redspace, 1, map.GridCoords);
            Assert.That(sys.TryInsertFuel((gate, comp), red, null), Is.EqualTo(1));
            Assert.That(Level(gate, RedspaceGateVisuals.RedspaceLevel), Is.EqualTo(0), "1 rs = 0 sockets");
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 1, map.GridCoords), null);
            Assert.That(Level(gate, RedspaceGateVisuals.RedspaceLevel), Is.EqualTo(1), "2 rs = 1 socket");
            for (var i = 0; i < 2; i++)
                sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 10, map.GridCoords), null);
            Assert.That(comp.Redspace, Is.EqualTo(22));
            var extra = Stack(entMan, stack, Redspace, 5, map.GridCoords);
            Assert.That(sys.TryInsertFuel((gate, comp), extra, null), Is.EqualTo(2));
            Assert.That(entMan.GetComponent<StackComponent>(extra).Count, Is.EqualTo(3));
            Assert.That(comp.Redspace, Is.EqualTo(24));
            Assert.That(Level(gate, RedspaceGateVisuals.RedspaceLevel), Is.EqualTo(12), "24 rs = 12 sockets");
            Assert.That(sys.TryInsertFuel((gate, comp), extra, null), Is.EqualTo(0), "gate full");
            Assert.That(entMan.GetComponent<StackComponent>(extra).Count, Is.EqualTo(3));

            // ...while bluespace goes into the separate rack: a full gate still takes 50 bluespace.
            Assert.That(Level(gate, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(0));
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 4, map.GridCoords), null);
            Assert.That(comp.Bluespace, Is.EqualTo(4));
            Assert.That(Level(gate, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(0), "4 bs = 0 slots");
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 1, map.GridCoords), null);
            Assert.That(Level(gate, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(1), "5 bs = 1 slot");
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 30, map.GridCoords), null);
            var blue = Stack(entMan, stack, Bluespace, 30, map.GridCoords);
            Assert.That(sys.TryInsertFuel((gate, comp), blue, null), Is.EqualTo(15));
            Assert.That(entMan.GetComponent<StackComponent>(blue).Count, Is.EqualTo(15));
            Assert.That(comp.Bluespace, Is.EqualTo(50));
            Assert.That(comp.Redspace, Is.EqualTo(24));
            Assert.That(Level(gate, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(10), "50 bs = 10 slots");
            Assert.That(sys.TryInsertFuel((gate, comp), blue, null), Is.EqualTo(0), "rack full");

            // UI shows both pools separately.
            var state = sys.GetUiState(gate, comp);
            Assert.That(state.Bluespace, Is.EqualTo(50));
            Assert.That(state.BluespaceCapacity, Is.EqualTo(50));
            Assert.That(state.Redspace, Is.EqualTo(24));
            Assert.That(state.RedspaceCapacity, Is.EqualTo(24));

            // An empty rack doesn't limit redspace either.
            var gate2 = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp2 = entMan.GetComponent<RedspaceGateComponent>(gate2);
            sys.TryInsertFuel((gate2, comp2), Stack(entMan, stack, Bluespace, 9, map.GridCoords), null);
            Assert.That(Level(gate2, RedspaceGateVisuals.BluespaceLevel), Is.EqualTo(1), "9 bs = 1 slot");
            for (var i = 0; i < 3; i++)
                sys.TryInsertFuel((gate2, comp2), Stack(entMan, stack, Redspace, 10, map.GridCoords), null);
            Assert.That(comp2.Redspace, Is.EqualTo(24));
            Assert.That(comp2.Bluespace, Is.EqualTo(9));

            // Non-fuel stacks are ignored.
            Assert.That(sys.TryInsertFuel((gate2, comp2), entMan.SpawnEntity("SheetSteel1", map.GridCoords), null), Is.EqualTo(0));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task OpenConsumeAndCloseOnEmpty()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();

        EntityUid gate = default;
        RedspaceGateComponent comp = default!;
        EntityUid portal = default;
        var target = new Vector2(10.5f, 10.5f); // open space on the same map

        await server.WaitAssertion(() =>
        {
            gate = entMan.SpawnEntity(Gate, map.GridCoords);
            comp = entMan.GetComponent<RedspaceGateComponent>(gate);

            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out var fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-no-fuel"));

            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 1, map.GridCoords), null);
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 2, map.GridCoords), null);

            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out fail), Is.True, fail);
            portal = comp.Portal!.Value;

            // First unit burned on opening, bluespace first, 1 minute.
            Assert.That(comp.Bluespace, Is.EqualTo(1));
            Assert.That(comp.Redspace, Is.EqualTo(1));
            Assert.That(comp.NextConsume - timing.CurTime, Is.EqualTo(TimeSpan.FromMinutes(1)));

            // Two-way link, exit at the typed coordinates on the gate's map.
            var xform = entMan.System<SharedTransformSystem>();
            var pos = xform.GetMapCoordinates(portal);
            Assert.That(pos.MapId, Is.EqualTo(map.MapId));
            Assert.That(pos.Position, Is.EqualTo(target));
            Assert.That(entMan.HasComponent<PortalComponent>(gate), Is.True);
            Assert.That(entMan.GetComponent<LinkedEntityComponent>(gate).LinkedEntities, Does.Contain(portal));
            Assert.That(entMan.GetComponent<LinkedEntityComponent>(portal).LinkedEntities, Does.Contain(gate));

            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-already-open"));

            comp.NextConsume = timing.CurTime;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            // Second bluespace burned before any redspace.
            Assert.That(comp.Bluespace, Is.EqualTo(0));
            Assert.That(comp.Redspace, Is.EqualTo(1));
            Assert.That(comp.Portal, Is.EqualTo(portal));
            comp.NextConsume = timing.CurTime;
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.Redspace, Is.EqualTo(0));
            Assert.That(comp.NextConsume - timing.CurTime, Is.GreaterThan(TimeSpan.FromMinutes(9.9)));
            Assert.That(comp.Portal, Is.EqualTo(portal));
            comp.NextConsume = timing.CurTime;
        });

        // Out of fuel: closes and the exit portal is removed.
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(comp.Portal, Is.Null);
            Assert.That(entMan.Deleted(portal), Is.True);
            Assert.That(entMan.HasComponent<PortalComponent>(gate), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Rejections()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var tileDef = server.ResolveDependency<ITileDefinitionManager>();

        await server.WaitAssertion(() =>
        {
            var gate = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 5, map.GridCoords), null);

            string fail;
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(float.NaN, 0), null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-invalid-coordinates"));
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(1e9f, 0), null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-invalid-coordinates"));
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(1f, 0.5f), null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-too-close"));

            // Wall on the target tile.
            var plating = new Tile(tileDef["Plating"].TileId);
            mapSys.SetTile(map.Grid, new Vector2i(4, 0), plating);
            var wallCoords = mapSys.GridTileToLocal(map.Grid, map.Grid.Comp, new Vector2i(4, 0));
            entMan.SpawnEntity("WallSolid", wallCoords);
            var wallWorld = entMan.System<SharedTransformSystem>().ToMapCoordinates(wallCoords).Position;
            Assert.That(sys.TryOpenPortal((gate, comp), wallWorld, null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-obstructed"));

            // Disk pointing at a deleted map.
            var disk = entMan.SpawnEntity("CoordinatesDisk", map.GridCoords);
            var gone = mapSys.CreateMap(out _);
            entMan.GetComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination = gone;
            Assert.That(slots.TryInsert(gate, comp.DiskSlotId, disk, null), Is.True);
            entMan.DeleteEntity(gone);
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(10, 10), null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-no-map"));

            // Paused map.
            var paused = mapSys.CreateMap(out var pausedId);
            mapSys.SetPaused(pausedId, true);
            entMan.GetComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination = paused;
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(10, 10), null, out fail), Is.False);
            Assert.That(fail, Is.EqualTo("redspace-gate-fail-map-unavailable"));

            // No fuel was burned by any of the failures.
            Assert.That(comp.Bluespace, Is.EqualTo(5));
            Assert.That(comp.Portal, Is.Null);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DiskOverrideAndDestruction()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var map2 = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var xform = entMan.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            var gate = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 2, map.GridCoords), null);

            // Same as the ftldisk command: a CoordinatesDisk whose destination is a map.
            var disk = entMan.SpawnEntity("CoordinatesDisk", map.GridCoords);
            entMan.GetComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination = map2.MapUid;
            Assert.That(slots.TryInsert(gate, comp.DiskSlotId, disk, null), Is.True);

            // Also accepted: a grid destination resolves to its map. Target is (0.5, 0.5) on map2,
            // i.e. the same position as the gate, but on another map so not "too close".
            var target = new Vector2(0.5f, 0.5f);
            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out var fail), Is.True, fail);
            var portal = comp.Portal!.Value;
            var pos = xform.GetMapCoordinates(portal);
            Assert.That(pos.MapId, Is.EqualTo(map2.MapId));
            Assert.That(pos.Position, Is.EqualTo(target));
            Assert.That(comp.Redspace, Is.EqualTo(1));
            Assert.That(entMan.GetComponent<PortalComponent>(portal).CanTeleportToOtherMaps, Is.True);

            // Deleting the exit portal closes the gate side.
            entMan.DeleteEntity(portal);
            Assert.That(comp.Portal, Is.Null);
            Assert.That(entMan.HasComponent<PortalComponent>(gate), Is.False);

            // Destroying the gate removes the open portal.
            Assert.That(sys.TryOpenPortal((gate, comp), target, null, out fail), Is.True, fail);
            portal = comp.Portal!.Value;
            entMan.DeleteEntity(gate);
        });

        await pair.RunTicksSync(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityQuery<RedspaceGatePortalComponent>().Count(), Is.EqualTo(0));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The exit portal on a rotated grid used to keep zero world rotation (it was spawned at map coordinates), so
    /// through the grid-aligned eye of anyone on that grid it showed up tilted by the grid's angle.
    /// </summary>
    [Test]
    public async Task PortalSpawnsUpright()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var map2 = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var xform = entMan.System<SharedTransformSystem>();
        var gridAngle = Angle.FromDegrees(35);

        await server.WaitAssertion(() =>
        {
            xform.SetLocalRotation(map2.Grid, gridAngle);

            var gate = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 4, map.GridCoords), null);
            var disk = entMan.SpawnEntity("CoordinatesDisk", map.GridCoords);
            entMan.GetComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination = map2.MapUid;
            Assert.That(slots.TryInsert(gate, comp.DiskSlotId, disk, null), Is.True);

            // Onto the rotated grid: square to the grid (and so to the eye of anyone standing on it).
            var onGrid = xform.ToMapCoordinates(map2.GridCoords).Position;
            Assert.That(sys.TryOpenPortal((gate, comp), onGrid, null, out var fail), Is.True, fail);
            var portal = comp.Portal!.Value;
            var portalXform = entMan.GetComponent<TransformComponent>(portal);
            Assert.That(portalXform.ParentUid, Is.EqualTo(map2.Grid.Owner));
            Assert.That(portalXform.LocalRotation.Theta, Is.EqualTo(0).Within(1e-4));
            Assert.That(xform.GetWorldRotation(portal).Theta, Is.EqualTo(gridAngle.Theta).Within(1e-4));
            Assert.That(xform.GetMapCoordinates(portal).Position.X, Is.EqualTo(onGrid.X).Within(1e-3));
            Assert.That(xform.GetMapCoordinates(portal).Position.Y, Is.EqualTo(onGrid.Y).Within(1e-3));
            sys.ClosePortal((gate, comp));

            // Open space: parented to the map, world rotation 0.
            var inSpace = new Vector2(60.5f, 60.5f);
            Assert.That(sys.TryOpenPortal((gate, comp), inSpace, null, out fail), Is.True, fail);
            portal = comp.Portal!.Value;
            Assert.That(entMan.GetComponent<TransformComponent>(portal).ParentUid, Is.EqualTo(map2.MapUid));
            Assert.That(xform.GetWorldRotation(portal).Theta, Is.EqualTo(0).Within(1e-4));
        });

        // And the sprite never turns with the eye.
        var clientProto = pair.Client.ResolveDependency<IPrototypeManager>();
        var clientFactory = pair.Client.ResolveDependency<IComponentFactory>();
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var id in new[] { Portal, Gate })
            {
                Assert.That(clientProto.Index<EntityPrototype>(id).TryGetComponent<SpriteComponent>(out var sprite, clientFactory), Is.True, id);
                Assert.That(sprite!.NoRotation, Is.True, $"{id} sprite should be noRot");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PortalAndGateCrackle()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var stack = entMan.System<StackSystem>();
        var sys = entMan.System<RedspaceGateSystem>();
        var crackleSys = entMan.System<PeriodicCrackleSystem>();

        EntityUid gate = default;
        RedspaceGateComponent comp = default!;
        EntityUid portal = default;
        PeriodicCrackleComponent portalCrackle = default!;
        PeriodicCrackleComponent gateCrackle = default!;
        var firstPortal = TimeSpan.Zero;
        var firstGate = TimeSpan.Zero;

        await server.WaitAssertion(() =>
        {
            gate = entMan.SpawnEntity(Gate, map.GridCoords);
            comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            gateCrackle = entMan.GetComponent<PeriodicCrackleComponent>(gate);
            // No crystals: no crackle.
            Assert.That(gateCrackle.Enabled, Is.False, "an empty gate doesn't crackle");
            firstGate = gateCrackle.NextCrackle;
        });

        await pair.RunSeconds(10f);
        await server.WaitAssertion(() =>
        {
            Assert.That(gateCrackle.Enabled, Is.False);
            Assert.That(gateCrackle.Crackling, Is.False);
            Assert.That(gateCrackle.NextCrackle, Is.EqualTo(firstGate), "an empty gate never crackled");

            // Fuelled (redspace in the gate): idle crackle along the frame, 4-9 s.
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Redspace, 2, map.GridCoords), null);
            Assert.That(gateCrackle.Enabled, Is.True, "a fuelled gate crackles periodically");
            Assert.That(gateCrackle.MinInterval, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(gateCrackle.MaxInterval, Is.EqualTo(TimeSpan.FromSeconds(9)));
            Assert.That(gateCrackle.NextCrackle - timing.CurTime, Is.InRange(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9)));
            firstGate = gateCrackle.NextCrackle;
        });

        // Idle crackle actually fires within 4-9 s.
        await pair.RunSeconds(9.5f);
        await server.WaitAssertion(() =>
        {
            Assert.That(gateCrackle.NextCrackle, Is.GreaterThan(firstGate), "fuelled idle gate crackled");

            // (10.5, 10.5) is open space: no grid there.
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(10.5f, 10.5f), null, out var fail), Is.True, fail);
            portal = comp.Portal!.Value;
            Assert.That(entMan.GetComponent<TransformComponent>(portal).GridUid, Is.Null);

            portalCrackle = entMan.GetComponent<PeriodicCrackleComponent>(portal);
            var now = timing.CurTime;
            Assert.That(portalCrackle.NextCrackle - now, Is.InRange(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
            // Open: stronger and more often, around the ring.
            Assert.That(gateCrackle.Enabled, Is.True);
            Assert.That(gateCrackle.MinInterval, Is.EqualTo(TimeSpan.FromSeconds(2)));
            Assert.That(gateCrackle.MaxInterval, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(gateCrackle.NextCrackle - now, Is.LessThanOrEqualTo(TimeSpan.FromSeconds(5)));
            firstPortal = portalCrackle.NextCrackle;
            firstGate = gateCrackle.NextCrackle;
        });

        // Both have crackled within their 2-5 s interval, the portal while floating in space.
        await pair.RunSeconds(5.5f);
        await server.WaitAssertion(() =>
        {
            Assert.That(portalCrackle.NextCrackle, Is.GreaterThan(firstPortal));
            Assert.That(gateCrackle.NextCrackle, Is.GreaterThan(firstGate));

            // Forced crackle shows the overlay and it hides again after its duration.
            Assert.That(crackleSys.TryCrackle(portal, portalCrackle), Is.True);
            Assert.That(portalCrackle.Crackling, Is.True);
            Assert.That(crackleSys.TryCrackle(gate, gateCrackle), Is.True);

            // Closing with a crystal still stored goes back to the idle interval and keeps crackling.
            Assert.That(comp.Redspace, Is.EqualTo(1));
            sys.ClosePortal((gate, comp));
            Assert.That(gateCrackle.Enabled, Is.True);
            Assert.That(gateCrackle.MinInterval, Is.EqualTo(TimeSpan.FromSeconds(4)));
            Assert.That(gateCrackle.MaxInterval, Is.EqualTo(TimeSpan.FromSeconds(9)));

            // Burn the last crystal: while open it still crackles (boosted); once closed with nothing left, it stops.
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(10.5f, 10.5f), null, out var fail), Is.True, fail);
            Assert.That(comp.Redspace, Is.EqualTo(0));
            Assert.That(gateCrackle.Enabled, Is.True, "open portal keeps the ring crackling");
            sys.ClosePortal((gate, comp));
            Assert.That(gateCrackle.Enabled, Is.False, "out of crystals: no crackle");
            Assert.That(gateCrackle.Crackling, Is.False);
        });

        await pair.RunSeconds(6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(portal), Is.True);
            Assert.That(gateCrackle.Enabled, Is.False);
            Assert.That(gateCrackle.Crackling, Is.False);

            // Refuelling with bluespace (rack) turns it back on.
            sys.TryInsertFuel((gate, comp), Stack(entMan, stack, Bluespace, 1, map.GridCoords), null);
            Assert.That(gateCrackle.Enabled, Is.True);
        });

        await pair.CleanReturnAsync();
    }
}

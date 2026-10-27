using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Server._CorvaxGoob.Redspace;
using Content.Server._CorvaxGoob.Redspace.Commands;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Examine;
using Content.Shared.Labels.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Stacks;
using Robust.Server.Console;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Player;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

/// <summary>
/// ftldiskgate: a Euclidean gate prints a coordinates disk (the ftldisk CoordinatesDisk) with stored coordinates;
/// with it inserted the exit portal opens exactly at those coordinates.
/// </summary>
[TestFixture]
[TestOf(typeof(FTLDiskGateCommand))]
public sealed class RedspaceGateDiskCommandTest
{
    private const string Gate = "RedspaceGate";
    private const string Redspace = "MaterialRedspaceCrystal1";

    private sealed class CaptureShell(IConsoleHost host) : IConsoleShell
    {
        public readonly List<string> Lines = new();
        public readonly List<string> Errors = new();
        public IConsoleHost ConsoleHost { get; } = host;
        public bool IsLocal => true;
        public bool IsServer => true;
        public ICommonSession? Player => null;
        public void ExecuteCommand(string command) => ConsoleHost.ExecuteCommand(command);
        public void RemoteExecuteCommand(string command) { }
        public void WriteLine(string text) => Lines.Add(text);
        public void WriteLine(FormattedMessage message) => Lines.Add(message.ToString());
        public void WriteError(string text) => Errors.Add(text);
        public void Clear() { }
    }

    private static CaptureShell Run(IServerConsoleHost host, string args)
    {
        var shell = new CaptureShell(host);
        var cmd = host.AvailableCommands["ftldiskgate"];
        var argv = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        cmd.Execute(shell, args, argv);
        return shell;
    }

    private static List<EntityUid> GateDisks(IEntityManager entMan)
    {
        return entMan.EntityQuery<RedspaceGateDiskComponent>().Select(c => c.Owner).ToList();
    }

    [Test]
    public async Task PrintsDiskAndPinsPortal()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var map2 = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var host = server.ResolveDependency<IServerConsoleHost>();
        var sys = entMan.System<RedspaceGateSystem>();
        var slots = entMan.System<ItemSlotsSystem>();
        var stack = entMan.System<StackSystem>();
        var xform = entMan.System<SharedTransformSystem>();
        var examine = entMan.System<ExamineSystemShared>();

        await server.WaitAssertion(() =>
        {
            var gate = entMan.SpawnEntity(Gate, map.GridCoords);
            var comp = entMan.GetComponent<RedspaceGateComponent>(gate);
            var netGate = entMan.GetNetEntity(gate);
            var mapSys = entMan.System<SharedMapSystem>();
            var gateTile = mapSys.TileIndicesFor(map.Grid, xform.GetMapCoordinates(gate));
            mapSys.SetTile(map.Grid, gateTile + new Vector2i(0, -1), map.Tile.Tile);

            // "x y" form, gate as NetEntity.
            var shell = Run(host, $"{netGate} {map2.MapId} 12.5 -3");
            Assert.That(shell.Errors, Is.Empty, string.Join("\n", shell.Errors));
            Assert.That(shell.Lines, Has.Count.EqualTo(1));
            var disks = GateDisks(entMan);
            Assert.That(disks, Has.Count.EqualTo(1));
            var disk = disks[0];

            // Same disk as ftldisk, pointing at the map, plus the stored coordinates.
            Assert.That(entMan.GetComponent<MetaDataComponent>(disk).EntityPrototype?.ID, Is.EqualTo("CoordinatesDisk"));
            Assert.That(entMan.GetComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination, Is.EqualTo(map2.MapUid));
            var gateDisk = entMan.GetComponent<RedspaceGateDiskComponent>(disk);
            Assert.That(gateDisk.Map, Is.EqualTo(map2.MapUid));
            Assert.That(gateDisk.Position, Is.EqualTo(new Vector2(12.5f, -3f)));
            Assert.That(entMan.HasComponent<FTLDestinationComponent>(map2.MapUid), Is.True, "same as ftldisk: map becomes an FTL destination");
            Assert.That(entMan.GetComponent<LabelComponent>(disk).CurrentLabel, Does.Contain("12.5, -3"));

            // Printed out of the gate: lying in the world right next to it.
            Assert.That(entMan.GetComponent<TransformComponent>(disk).ParentUid, Is.EqualTo(map.Grid.Owner));
            var gatePos = xform.GetWorldPosition(gate);
            var diskPos = xform.GetWorldPosition(disk);
            Assert.That((gatePos - diskPos).Length(), Is.LessThanOrEqualTo(1.01f));
            Assert.That(mapSys.TileIndicesFor(map.Grid, xform.GetMapCoordinates(disk)), Is.EqualTo(gateTile + new Vector2i(0, -1)),
                "the disk drops out in front of (below) the gate");

            // Examine shows the coordinates.
            var text = examine.GetExamineText(disk, gate).ToString();
            Assert.That(text, Does.Contain("12.5").And.Contain("-3"), text);

            // Activation: whatever is typed, the portal opens at the stored coordinates on the stored map.
            var fuel = entMan.SpawnEntity(Redspace, map.GridCoords);
            stack.SetCount(fuel, 2);
            sys.TryInsertFuel((gate, comp), fuel, null);
            Assert.That(slots.TryInsert(gate, comp.DiskSlotId, disk, null), Is.True);
            Assert.That(sys.GetUiState(gate, comp).DiskTarget, Is.EqualTo(new Vector2(12.5f, -3f)));
            Assert.That(sys.TryOpenPortal((gate, comp), new Vector2(0.5f, 0.5f), null, out var fail), Is.True, fail);
            var portal = comp.Portal!.Value;
            var pos = xform.GetMapCoordinates(portal);
            Assert.That(pos.MapId, Is.EqualTo(map2.MapId));
            Assert.That(pos.Position, Is.EqualTo(new Vector2(12.5f, -3f)));
            Assert.That(comp.Target!.Value.Position, Is.EqualTo(new Vector2(12.5f, -3f)));
            // Gate side keeps its behaviour: linked portal, open ring crackle.
            Assert.That(entMan.HasComponent<Content.Shared.Teleportation.Components.PortalComponent>(gate), Is.True);
            Assert.That(entMan.GetComponent<PeriodicCrackleComponent>(gate).Enabled, Is.True);
            sys.ClosePortal((gate, comp));

            // Removing the disk returns to typed coordinates.
            slots.TryEject(gate, comp.DiskSlotId, null, out _);
            Assert.That(sys.GetUiState(gate, comp).DiskTarget, Is.Null);

            // "x,y" form, gate as server EntityUid.
            shell = Run(host, $"{gate} {map.MapId} -7,4.25");
            Assert.That(shell.Errors, Is.Empty, string.Join("\n", shell.Errors));
            disks = GateDisks(entMan);
            Assert.That(disks, Has.Count.EqualTo(2));
            var second = disks.Single(d => d != disk);
            Assert.That(entMan.GetComponent<RedspaceGateDiskComponent>(second).Position, Is.EqualTo(new Vector2(-7f, 4.25f)));
            Assert.That(entMan.GetComponent<RedspaceGateDiskComponent>(second).Map, Is.EqualTo(map.MapUid));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BadArguments()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var host = server.ResolveDependency<IServerConsoleHost>();
        var mapSys = entMan.System<SharedMapSystem>();

        await server.WaitAssertion(() =>
        {
            var gate = entMan.GetNetEntity(entMan.SpawnEntity(Gate, map.GridCoords));
            var notGate = entMan.GetNetEntity(entMan.SpawnEntity("CoordinatesDisk", map.GridCoords));
            var paused = mapSys.CreateMap(out var pausedId);
            mapSys.SetPaused(paused, true);

            foreach (var args in new[]
                     {
                         $"{gate} {map.MapId}",               // too few
                         $"{gate} {map.MapId} 1 2 3",         // too many
                         $"{notGate} {map.MapId} 1 2",        // not a gate
                         $"999999 {map.MapId} 1 2",           // no such entity
                         $"{gate} 9999 1 2",                  // no such map
                         $"{gate} {pausedId} 1 2",            // paused map
                         $"{gate} {map.MapId} a b",           // not numbers
                         $"{gate} {map.MapId} 1,2,3",         // three numbers
                         $"{gate} {map.MapId} NaN 2",         // not finite
                         $"{gate} {map.MapId} 1e9 2",         // out of range
                     })
            {
                var shell = Run(host, args);
                Assert.That(shell.Errors, Has.Count.EqualTo(1), args);
                Assert.That(shell.Lines, Is.Empty, args);
            }

            Assert.That(GateDisks(entMan), Is.Empty);
            entMan.DeleteEntity(paused);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public void ParsesCoordinateForms()
    {
        Assert.That(FTLDiskGateCommand.TryParseCoordinates(new[] { "1.5", "-2" }, out var p), Is.True);
        Assert.That(p, Is.EqualTo(new Vector2(1.5f, -2f)));
        Assert.That(FTLDiskGateCommand.TryParseCoordinates(new[] { "1.5,-2" }, out p), Is.True);
        Assert.That(p, Is.EqualTo(new Vector2(1.5f, -2f)));
        Assert.That(FTLDiskGateCommand.TryParseCoordinates(new[] { "1.5,", "-2" }, out p), Is.True);
        Assert.That(p, Is.EqualTo(new Vector2(1.5f, -2f)));
        Assert.That(FTLDiskGateCommand.TryParseCoordinates(new[] { "1" }, out _), Is.False);
        Assert.That(FTLDiskGateCommand.TryParseCoordinates(new[] { "x", "y" }, out _), Is.False);
    }
}

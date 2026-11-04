using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.Research;
using Content.Server._CorvaxGoob.Kamerton;
using Content.Server._CorvaxGoob.Shuttles;
using Content.Server.Research.Systems;
using Content.Server.Shuttles.Systems;
using Content.Shared._CorvaxGoob.Research;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Research.Components;
using Content.Shared.Research.Prototypes;
using Content.Shared.Shuttles.Components;
using Robust.Server.GameObjects;
using Robust.Server.Console;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Kamerton;

/// <summary>
/// Stage 11: the kamertontech event switch (persistent, hides the technology from consoles)
/// and ftldisk with an exact console position and facing.
/// </summary>
[TestFixture]
[TestOf(typeof(KamertonTechLockSystem))]
public sealed class KamertonStage11Test
{
    [Test]
    public async Task KamertonTechCommandAndPersistence()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var lockSys = server.System<KamertonTechLockSystem>();
        var console = server.ResolveDependency<IServerConsoleHost>();
        var res = server.ResolveDependency<IResourceManager>();

        await server.WaitPost(() =>
        {
            if (res.UserData.Exists(KamertonTechLockSystem.StatePath))
                res.UserData.Delete(KamertonTechLockSystem.StatePath);
            lockSys.Reload();
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(lockSys.IsOpen, Is.False, "closed by default");
            Assert.That(lockSys.IsHidden("KamertonTech"), Is.True);
            Assert.That(lockSys.IsHidden("BluespaceMining"), Is.False, "other technologies are untouched");
        });

        await server.WaitPost(() => console.ExecuteCommand("kamertontech open"));
        await server.WaitAssertion(() =>
        {
            Assert.That(lockSys.IsOpen, Is.True);
            Assert.That(res.UserData.ReadAllText(KamertonTechLockSystem.StatePath), Does.Contain("open: true"));
            // A restart reads the file again.
            lockSys.Reload();
            Assert.That(lockSys.IsOpen, Is.True, "stays open after reloading from disk");
            Assert.That(lockSys.IsHidden("KamertonTech"), Is.False);
        });

        await server.WaitPost(() => console.ExecuteCommand("kamertontech status"));
        await server.WaitPost(() => console.ExecuteCommand("kamertontech закрыть"));
        await server.WaitAssertion(() =>
        {
            Assert.That(lockSys.IsOpen, Is.False);
            Assert.That(res.UserData.ReadAllText(KamertonTechLockSystem.StatePath), Does.Contain("open: false"));
            lockSys.Reload();
            Assert.That(lockSys.IsOpen, Is.False, "stays closed after reloading from disk");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KamertonTechHiddenFromConsoleWhenClosed()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var lockSys = server.System<KamertonTechLockSystem>();
        var research = server.System<ResearchSystem>();
        var ui = server.System<UserInterfaceSystem>();
        var protos = server.ProtoMan;
        EntityUid rnd = default, consoleUid = default;

        await server.WaitPost(() =>
        {
            lockSys.SetOpen(false);
            rnd = entMan.SpawnEntity("ResearchAndDevelopmentServer", map.GridCoords);
            consoleUid = entMan.SpawnEntity("ComputerResearchAndDevelopment", map.GridCoords.Offset(new Vector2(1, 0)));
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            research.RegisterClient(consoleUid, rnd);
            research.AddTechnology(rnd, "BluespaceMining");
            research.ModifyServerPoints(rnd, 1_000_000);
            var kamerton = protos.Index<TechnologyPrototype>("KamertonTech");

            research.RefreshAllConsoles();
            Assert.That(ui.TryGetUiState<ResearchConsoleBoundInterfaceState>(consoleUid, ResearchConsoleUiKey.Key, out var state), Is.True);
            Assert.That(state!.Researches.ContainsKey("KamertonTech"), Is.False, "not shown at all while closed");
            Assert.That(state.Researches.ContainsKey("BluespaceMining"), Is.True);
            Assert.That(research.CanServerUnlockTechnology(consoleUid, kamerton), Is.False, "cannot be unlocked while closed");
            var ev = new TechnologyHiddenEvent(kamerton);
            entMan.EventBus.RaiseEvent(EventSource.Local, ref ev);
            Assert.That(ev.Hidden, Is.True, "kept off random technology disks");

            // Opening updates the console live.
            lockSys.SetOpen(true);
            Assert.That(ui.TryGetUiState(consoleUid, ResearchConsoleUiKey.Key, out state), Is.True);
            Assert.That(state!.Researches["KamertonTech"], Is.EqualTo(ResearchAvailability.Available));
            Assert.That(research.CanServerUnlockTechnology(consoleUid, kamerton), Is.True);

            // Researched, then closed: hidden again, recipes stay.
            research.AddTechnology(rnd, kamerton);
            lockSys.SetOpen(false);
            Assert.That(ui.TryGetUiState(consoleUid, ResearchConsoleUiKey.Key, out state), Is.True);
            Assert.That(state!.Researches.ContainsKey("KamertonTech"), Is.False);
            var db = entMan.GetComponent<TechnologyDatabaseComponent>(rnd);
            Assert.That(db.UnlockedRecipes.Select(r => r.Id), Does.Contain("KamertonReactorCoreFlatpackBoard"));

            entMan.DeleteEntity(consoleUid);
            entMan.DeleteEntity(rnd);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase("north", Direction.North)]
    [TestCase("восток", Direction.East)]
    [TestCase("ю", Direction.South)]
    [TestCase("West", Direction.West)]
    public async Task FtlDiskPutsConsoleAtTarget(string dirText, Direction expected)
    {
        Assert.That(FTLDiskExactTargetSystem.TryParseDirection(dirText, out var dir), Is.True);
        Assert.That(dir, Is.EqualTo(expected));

        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var target = await pair.CreateTestMap();
        var xforms = server.System<SharedTransformSystem>();
        var exact = server.System<FTLDiskExactTargetSystem>();
        var consoles = server.System<ShuttleConsoleSystem>();
        var slots = server.System<ItemSlotsSystem>();
        EntityUid console = default, disk = default;
        var point = new Vector2(10.5f, -3.5f);

        await server.WaitPost(() =>
        {
            var mapSys = entMan.System<SharedMapSystem>();
            mapSys.SetTile(map.Grid, new Vector2i(3, 1), map.Tile.Tile);
            console = entMan.SpawnEntity("ComputerShuttle", mapSys.GridTileToLocal(map.Grid, map.Grid, new Vector2i(3, 1)));
            xforms.SetLocalRotation(console, Direction.East.ToAngle());
            disk = entMan.SpawnEntity("CoordinatesDisk", map.GridCoords);
            entMan.EnsureComponent<ShuttleDestinationCoordinatesComponent>(disk).Destination = target.MapUid;
            exact.SetTarget(disk, target.MapUid, point, dir);
            Assert.That(slots.TryInsert(console, SharedShuttleConsoleComponent.DiskSlotName, disk, null), Is.True);
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(consoles.TryExactDiskArrival(console, console, target.MapId, out var coords, out var angle), Is.True);
            Assert.That(consoles.TryExactDiskArrival(console, console, map.MapId, out _, out _), Is.False, "only for the disk's map");

            // What FTL arrival does with a position target.
            xforms.SetCoordinates(map.Grid, entMan.GetComponent<TransformComponent>(map.Grid), coords, rotation: angle);

            var (pos, rot) = xforms.GetWorldPositionRotation(console);
            Assert.That(entMan.GetComponent<TransformComponent>(console).MapUid, Is.EqualTo(target.MapUid));
            Assert.That(Vector2.Distance(pos, point), Is.LessThan(0.01f), "the console stands on the target");
            Assert.That(rot.GetCardinalDir(), Is.EqualTo(expected), "the console faces the direction");
            Assert.That(entMan.GetComponent<TransformComponent>(console).LocalRotation.GetCardinalDir(), Is.EqualTo(Direction.East), "still anchored the same way on its grid");
        });

        await pair.CleanReturnAsync();
    }
}

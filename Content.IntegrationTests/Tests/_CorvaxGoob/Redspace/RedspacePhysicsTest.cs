using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Redspace;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server.Stack;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Damage;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stacks;
using Robust.Client.ResourceManagement;
using Robust.Server.Console;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

/// <summary>
/// Redspace map physics: presets, organism damage rate, and the suppressors (collar, beacon, gate portal), plus the
/// admin commands. Simulated time goes through <see cref="RedspaceMapSystem.Step"/> inside one server block, so no
/// other system (atmos, respiration) ticks in between.
/// </summary>
[TestFixture]
[TestOf(typeof(RedspaceMapSystem))]
public sealed class RedspacePhysicsTest
{
    private const string Mob = "MobHuman";
    private const string Collar = "ClothingNeckRedspaceSuppressorCollar";
    private const string Beacon = "RedspaceSuppressorBeacon";
    private const string Flatpack = "RedspaceSuppressorBeaconFlatpack";
    private const string Cell = "RedspaceSuperEnergyCell";
    private const string Crystal = "MaterialBSCrystal1";

    /// <summary>Everything but the organism off, so the tests are deterministic.</summary>
    private static readonly Dictionary<string, string> OnlyOrganism = new()
    {
        ["inertiaEnabled"] = "false", ["currentEnabled"] = "false", ["wavesEnabled"] = "false",
        ["weightlessEnabled"] = "false", ["blinkEnabled"] = "false", ["anomaliesEnabled"] = "false",
        ["echoEnabled"] = "false", ["hallucinationsEnabled"] = "false", ["dischargesEnabled"] = "false",
        ["lightFlickerEnabled"] = "false", ["radioStaticEnabled"] = "false", ["ambientEnabled"] = "false",
        ["jitterChance"] = "0", ["vomitChancePerMinute"] = "0",
        ["anomalyEventsEnabled"] = "false", ["mutationsEnabled"] = "false", ["privateHallucinationsEnabled"] = "false",
        ["itemDecayEnabled"] = "false", ["visualsEnabled"] = "false",
    };

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

    private static CaptureShell Run(IServerConsoleHost host, string command, string args)
    {
        var shell = new CaptureShell(host);
        var argv = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        host.AvailableCommands[command].Execute(shell, args, argv);
        return shell;
    }

    private static bool Affected(IEntityManager entMan, EntityUid uid) => entMan.HasComponent<RedspaceAffectedComponent>(uid);

    [Test]
    public async Task PresetsPrototypesAndSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();
        var sys = server.System<RedspaceMapSystem>();

        await server.WaitAssertion(() =>
        {
            float Dps(string id) => sys.DamagePerSecond(proto.Index<RedspacePresetPrototype>(id).Settings).GetTotal().Float();
            Assert.That(Dps("RedspaceNormal"), Is.EqualTo(200f / 300f).Within(0.01f), "normal zone: 200 dmg in ~5 min");
            Assert.That(Dps("RedspaceCalm"), Is.LessThan(Dps("RedspaceNormal")));
            Assert.That(Dps("RedspaceStorm"), Is.GreaterThan(Dps("RedspaceNormal")));

            var normal = proto.Index<RedspacePresetPrototype>("RedspaceNormal").Settings;
            Assert.That(normal.GateSuppressionRadius, Is.EqualTo(10f));
            Assert.That(normal.WrapEnabled, Is.False);
            Assert.That(proto.HasIndex<EntityPrototype>(normal.DischargeEffect), Is.True);
            Assert.That(proto.HasIndex<EntityPrototype>(RedspaceMapSystem.EchoPrototype), Is.True);
            Assert.That(proto.HasIndex<EntityPrototype>(RedspaceMapSystem.AnomalyPrototype), Is.True);
            Assert.That(proto.HasIndex<SoundCollectionPrototype>("RedspaceWhispers"), Is.True);
            Assert.That(RedspaceMapSystem.SettingNames, Does.Contain("cellularPerSecond"));

            var collar = proto.Index<EntityPrototype>(Collar);
            Assert.That(collar.TryGetComponent<RedspaceCollarComponent>(out var collarComp, factory), Is.True);
            Assert.That(collarComp!.CrystalTime, Is.EqualTo(1800f), "1 crystal = 30 min");
            Assert.That(collar.TryGetComponent<RedspaceSuppressorComponent>(out var collarSup, factory), Is.True);
            Assert.That(collarSup!.Radius, Is.InRange(1.42f, 1.99f), "wearer + 1 tile (diagonals too)");

            var beacon = proto.Index<EntityPrototype>(Beacon);
            Assert.That(beacon.TryGetComponent<RedspaceBeaconComponent>(out var beaconComp, factory), Is.True);
            Assert.That(beaconComp!.CellTime, Is.EqualTo(1800f), "1 cell = 30 min");
            Assert.That(beaconComp.MaxCells, Is.EqualTo(3));
            Assert.That(beacon.TryGetComponent<RedspaceSuppressorComponent>(out var beaconSup, factory), Is.True);
            Assert.That(beaconSup!.Radius, Is.EqualTo(80f));

            Assert.That(proto.Index<EntityPrototype>(Flatpack).TryGetComponent<Content.Shared.Construction.Components.FlatpackComponent>(out var pack, factory), Is.True);
            Assert.That(pack!.Entity?.Id, Is.EqualTo(Beacon));
            Assert.That(proto.Index<EntityPrototype>(Cell).TryGetComponent<RedspaceFuelCellComponent>(out _, factory), Is.True);

            // Spawn-only, but in the spawn menu.
            foreach (var id in new[] { Collar, Beacon, Flatpack, Cell })
                Assert.That(proto.Index<EntityPrototype>(id).HideSpawnMenu, Is.False, id);
        });

        var res = pair.Client.ResolveDependency<IResourceCache>();
        await pair.Client.WaitAssertion(() =>
        {
            foreach (var (rsi, states) in new (string, string[])[]
                     {
                         ("/Textures/_CorvaxGoob/Clothing/Neck/Misc/redspace_suppressor_collar.rsi", new[] { "icon", "icon-glow", "equipped-NECK", "inhand-left", "inhand-right" }),
                         ("/Textures/_CorvaxGoob/Structures/Machines/redspace_suppressor_beacon.rsi", new[] { "base", "on", "off" }),
                         ("/Textures/_CorvaxGoob/Objects/Devices/redspace_suppressor_beacon_flatpack.rsi", new[] { "icon" }),
                         ("/Textures/_CorvaxGoob/Objects/Power/redspace_super_cell.rsi", new[] { "icon", "glow", "inhand-left", "inhand-right" }),
                     })
            {
                var r = res.GetResource<RSIResource>(new ResPath(rsi)).RSI;
                foreach (var state in states)
                    Assert.That(r.TryGetState(state, out _), Is.True, $"{rsi}:{state}");
            }

            var beaconRsi = res.GetResource<RSIResource>(new ResPath("/Textures/_CorvaxGoob/Structures/Machines/redspace_suppressor_beacon.rsi")).RSI;
            Assert.That(beaconRsi.Size.X, Is.EqualTo(64), "beacon is 64x64 like the station anchor");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task UnprotectedDeathInAboutFiveMinutes()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();
        var mobState = server.System<MobStateSystem>();
        var thresholds = server.System<MobThresholdSystem>();

        await server.WaitAssertion(() =>
        {
            sys.SetRedspace(map.MapUid, "RedspaceNormal", OnlyOrganism);
            var player = entMan.SpawnEntity(Mob, map.MapCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            // An NPC mob next to the player is affected too.
            var npc = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(5f, 0f), map.MapId));

            Assert.That(thresholds.TryGetThresholdForState(player, MobState.Dead, out var dead), Is.True);
            Assert.That(dead!.Value.Float(), Is.EqualTo(200f), "a human dies at 200 total damage");

            var deathTime = -1;
            for (var t = 1; t <= 400 && deathTime < 0; t++)
            {
                sys.Step(1f);
                if (t == 240)
                    Assert.That(mobState.IsDead(player), Is.False, "still alive after 4 min");
                if (mobState.IsDead(player))
                    deathTime = t;
            }

            Assert.That(deathTime, Is.InRange(270, 330), "dead in about 5 minutes");
            Assert.That(mobState.IsDead(npc), Is.True, "nearby NPC mobs die the same way");

            var damage = entMan.GetComponent<DamageableComponent>(player).Damage.DamageDict;
            Assert.That(damage.GetValueOrDefault("Cellular"), Is.GreaterThan(FixedPoint2.Zero));
            Assert.That(damage.GetValueOrDefault("Shock"), Is.GreaterThan(FixedPoint2.Zero));
            Assert.That(damage.GetValueOrDefault("Cellular"), Is.GreaterThan(damage.GetValueOrDefault("Shock") * 2));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task EffectsAndOverrides()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();

        await server.WaitAssertion(() =>
        {
            sys.SetRedspace(map.MapUid, "RedspaceStorm", null);
            Assert.That(entMan.HasComponent<RedspaceMapComponent>(map.MapUid), Is.True);
            sys.SetRedspace(map.MapUid, "RedspaceNormal", new Dictionary<string, string>
            {
                ["currentRandomDirection"] = "false", ["currentDirection"] = "0", ["currentTurnRate"] = "0",
                ["wavesEnabled"] = "false", ["weightlessEnabled"] = "false", ["blinkEnabled"] = "false",
                ["dischargesEnabled"] = "false",
            });
            var comp = entMan.GetComponent<RedspaceMapComponent>(map.MapUid);
            Assert.That(comp.Settings.WavesEnabled, Is.False, "override applied");
            Assert.That(comp.Settings.CellularPerSecond, Is.EqualTo(0.5f), "rest from the preset");

            var player = entMan.SpawnEntity(Mob, map.MapCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            var far = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(40f, 0f), map.MapId));
            sys.Step(1f);

            Assert.That(Affected(entMan, player), Is.True);
            Assert.That(Affected(entMan, far), Is.False, "mobs far from every player are not scanned");
            var affected = entMan.GetComponent<RedspaceAffectedComponent>(player);
            Assert.That(affected.Friction, Is.EqualTo(0.35f).Within(0.001f), "low friction");
            Assert.That(affected.DoAfter, Is.EqualTo(1.5f).Within(0.001f), "slow DoAfters");
            Assert.That(affected.Drift.Length(), Is.EqualTo(1.2f).Within(0.05f), "current");

            // Turning the map off clears every effect.
            sys.SetRedspace(map.MapUid, null);
            sys.Step(1f);
            Assert.That(Affected(entMan, player), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task CollarCoversWearerAndOneTile()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();
        var sup = server.System<RedspaceSuppressionSystem>();
        var inventory = server.System<InventorySystem>();
        var stack = server.System<StackSystem>();

        await server.WaitAssertion(() =>
        {
            sys.SetRedspace(map.MapUid, "RedspaceNormal", OnlyOrganism);
            var player = entMan.SpawnEntity(Mob, map.MapCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            var near = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(1f, 1f), map.MapId));
            var far = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(2.2f, 0f), map.MapId));

            var collarUid = entMan.SpawnEntity(Collar, map.MapCoords);
            var collar = new Entity<RedspaceCollarComponent>(collarUid, entMan.GetComponent<RedspaceCollarComponent>(collarUid));
            var crystals = entMan.SpawnEntity(Crystal, map.MapCoords);
            stack.SetCount(crystals, 5);

            Assert.That(sup.TryInsertCrystal(collar, crystals, null), Is.True);
            Assert.That(sup.TryInsertCrystal(collar, crystals, null), Is.False, "slot holds 1 crystal");
            Assert.That(entMan.GetComponent<StackComponent>(crystals).Count, Is.EqualTo(4));

            // Not worn: no protection, no drain.
            sys.Step(60f);
            Assert.That(Affected(entMan, player), Is.True);
            Assert.That(collar.Comp.Crystals, Is.EqualTo(1));
            Assert.That(sup.CollarCharge(collar.Comp), Is.EqualTo(1800f));

            Assert.That(inventory.TryEquip(player, collarUid, "neck", silent: true, force: true), Is.True);
            sys.Step(1f);
            Assert.That(Affected(entMan, player), Is.False, "wearer covered");
            Assert.That(Affected(entMan, near), Is.False, "1 tile (diagonal) covered");
            Assert.That(Affected(entMan, far), Is.True, "2 tiles away is not");
            Assert.That(collar.Comp.Crystals, Is.EqualTo(0), "crystal pulled from the slot and burning");

            // A spare goes in while one burns.
            Assert.That(sup.TryInsertCrystal(collar, crystals, null), Is.True);
            Assert.That(sup.CollarCharge(collar.Comp), Is.EqualTo(3600f - 1f).Within(0.01f));

            // 30 min per crystal: the first runs out and the spare starts exactly then.
            for (var i = 0; i < 1798; i++)
                sys.Step(1f);
            Assert.That(collar.Comp.Crystals, Is.EqualTo(1), "first crystal still burning at 29:59");
            sys.Step(1f);
            sys.Step(1f);
            Assert.That(collar.Comp.Crystals, Is.EqualTo(0), "spare crystal taken after 30 min");
            Assert.That(Affected(entMan, player), Is.False);

            for (var i = 0; i < 1800; i++)
                sys.Step(1f);
            Assert.That(sup.CollarCharge(collar.Comp), Is.EqualTo(0f));
            Assert.That(Affected(entMan, player), Is.True, "out of crystals: exposed again");
            Assert.That(Affected(entMan, near), Is.True);

            // Off redspace (map turned off) a charged collar doesn't drain.
            Assert.That(sup.TryInsertCrystal(collar, crystals, null), Is.True);
            sys.SetRedspace(map.MapUid, null);
            sys.Step(600f);
            Assert.That(collar.Comp.Crystals, Is.EqualTo(1));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BeaconCoversEightyTilesWhileAnchoredAndFuelled()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();
        var sup = server.System<RedspaceSuppressionSystem>();
        var xformSys = server.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            sys.SetRedspace(map.MapUid, "RedspaceNormal", OnlyOrganism);
            var beaconUid = entMan.SpawnEntity(Beacon, map.GridCoords);
            var beacon = new Entity<RedspaceBeaconComponent>(beaconUid, entMan.GetComponent<RedspaceBeaconComponent>(beaconUid));
            Assert.That(entMan.GetComponent<TransformComponent>(beaconUid).Anchored, Is.True);

            var player = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(79f, 0f), map.MapId));
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            var outside = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(81f, 0f), map.MapId));

            // Empty: nothing.
            sys.Step(1f);
            Assert.That(sup.BeaconActive(beacon), Is.False);
            Assert.That(Affected(entMan, player), Is.True, "empty beacon does nothing");

            var cells = Enumerable.Range(0, 4).Select(_ => entMan.SpawnEntity(Cell, map.MapCoords)).ToList();
            for (var i = 0; i < 3; i++)
                Assert.That(sup.TryInsertCell(beacon, cells[i], null), Is.True);
            Assert.That(sup.TryInsertCell(beacon, cells[3], null), Is.False, "3 cells max");
            Assert.That(entMan.Deleted(cells[3]) || !entMan.IsQueuedForDeletion(cells[3]), Is.True);
            Assert.That(sup.BeaconFuel(beacon.Comp), Is.EqualTo(3 * 1800f));
            Assert.That(sup.BeaconCellCount(beacon.Comp), Is.EqualTo(3));

            sys.Step(1f);
            Assert.That(sup.BeaconActive(beacon), Is.True);
            Assert.That(Affected(entMan, player), Is.False, "79 tiles: covered");
            Assert.That(Affected(entMan, outside), Is.True, "81 tiles: not covered");

            // Unanchored: idle, and the fuel is kept.
            xformSys.Unanchor(beaconUid);
            var fuel = sup.BeaconFuel(beacon.Comp);
            sys.Step(100f);
            Assert.That(sup.BeaconActive(beacon), Is.False);
            Assert.That(Affected(entMan, player), Is.True, "unanchored beacon does nothing");
            Assert.That(sup.BeaconFuel(beacon.Comp), Is.EqualTo(fuel), "no burn while unanchored");

            xformSys.AnchorEntity(beaconUid);
            Assert.That(entMan.GetComponent<TransformComponent>(beaconUid).Anchored, Is.True);

            // 30 min per cell.
            sys.Step(1f);
            var start = sup.BeaconFuel(beacon.Comp);
            for (var i = 0; i < 30; i++)
                sys.Step(60f);
            Assert.That(sup.BeaconFuel(beacon.Comp), Is.EqualTo(start - 1800f).Within(0.5f));
            Assert.That(sup.BeaconCellCount(beacon.Comp), Is.EqualTo(2), "one cell per 30 min");

            for (var i = 0; i < 60; i++)
                sys.Step(60f);
            sys.Step(5f);
            Assert.That(sup.BeaconFuel(beacon.Comp), Is.EqualTo(0f));
            Assert.That(sup.BeaconActive(beacon), Is.False);
            var playerPos = server.System<SharedTransformSystem>().GetMapCoordinates(player);
            Assert.That(sys.IsSuppressed(playerPos, out _), Is.False, "out of fuel: exposed again");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task GatePortalCoversTenTiles()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();

        await server.WaitAssertion(() =>
        {
            sys.SetRedspace(map.MapUid, "RedspaceNormal", OnlyOrganism);
            var portal = entMan.SpawnEntity(null, map.MapCoords);
            entMan.AddComponent<RedspaceGatePortalComponent>(portal);

            var player = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(9.5f, 0f), map.MapId));
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            var outside = entMan.SpawnEntity(Mob, new MapCoordinates(new Vector2(10.5f, 0f), map.MapId));

            sys.Step(1f);
            Assert.That(Affected(entMan, player), Is.False, "9.5 tiles from the portal: covered");
            Assert.That(Affected(entMan, outside), Is.True, "10.5 tiles: not covered");
            Assert.That(sys.IsSuppressed(new MapCoordinates(new Vector2(0f, 9.9f), map.MapId), out var by), Is.True);
            Assert.That(by, Is.EqualTo(portal));

            entMan.DeleteEntity(portal);
            sys.Step(1f);
            Assert.That(Affected(entMan, player), Is.True, "portal closed");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Commands()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var host = server.ResolveDependency<IServerConsoleHost>();
        var id = (int) map.MapId;

        await server.WaitAssertion(() =>
        {
            Assert.That(host.AvailableCommands.ContainsKey("redspacemap"), Is.True);
            Assert.That(host.AvailableCommands.ContainsKey("redspacestatus"), Is.True);

            var usage = Run(host, "redspacemap", "");
            Assert.That(usage.Errors, Has.Count.EqualTo(1));
            Assert.That(usage.Lines.Single(), Does.Contain("RedspaceCalm").And.Contain("RedspaceNormal").And.Contain("RedspaceStorm"));

            Assert.That(Run(host, "redspacemap", "99999 storm").Errors, Has.Count.EqualTo(1), "no such map");
            Assert.That(Run(host, "redspacemap", $"{id} nope").Errors, Has.Count.EqualTo(1), "no such preset");
            Assert.That(Run(host, "redspacemap", $"{id} storm bogus=1").Errors, Has.Count.EqualTo(1), "bad override");
            Assert.That(Run(host, "redspacemap", $"{id} storm cellularPerSecond=abc").Errors, Has.Count.EqualTo(1));
            Assert.That(entMan.HasComponent<RedspaceMapComponent>(map.MapUid), Is.False);

            var on = Run(host, "redspacemap", $"{id} storm cellularPerSecond=2 wrapEnabled=true wrapMin=-50,-50");
            Assert.That(on.Errors, Is.Empty, string.Join("\n", on.Errors));
            var comp = entMan.GetComponent<RedspaceMapComponent>(map.MapUid);
            Assert.That(comp.Preset.Id, Is.EqualTo("RedspaceStorm"));
            Assert.That(comp.Settings.CellularPerSecond, Is.EqualTo(2f));
            Assert.That(comp.Settings.WrapEnabled, Is.True);
            Assert.That(comp.Settings.WrapMin, Is.EqualTo(new Vector2(-50f, -50f)));

            var state = Run(host, "redspacemap", $"{id}");
            Assert.That(state.Lines.Single(), Does.Contain("RedspaceStorm").And.Contain("cellularPerSecond=2"));

            Assert.That(Run(host, "redspacestatus", $"{id} 3 3").Lines[0], Is.EqualTo(Loc("cmd-redspacestatus-exposed", id, 3, 3)));
            var portal = entMan.SpawnEntity(null, map.MapCoords);
            entMan.AddComponent<RedspaceGatePortalComponent>(portal);
            Assert.That(Run(host, "redspacestatus", $"{id} 3 3").Lines[0], Does.Contain(portal.ToString()));
            Assert.That(Run(host, "redspacestatus", "").Errors, Has.Count.EqualTo(1), "no player: needs coordinates");

            Assert.That(Run(host, "redspacemap", $"{id} off").Errors, Is.Empty);
            Assert.That(entMan.HasComponent<RedspaceMapComponent>(map.MapUid), Is.False);
            Assert.That(Run(host, "redspacestatus", $"{id} 3 3").Lines.Single(), Is.EqualTo(Loc("cmd-redspacestatus-not-redspace", id, 3, 3)));
            Assert.That(Run(host, "redspacemap", $"{id}").Lines.Single(), Is.EqualTo(Robust.Shared.Localization.Loc.GetString("cmd-redspacemap-state-off", ("map", id))));
        });

        await pair.CleanReturnAsync();
    }

    private static string Loc(string key, int map, float x, float y)
    {
        return Robust.Shared.Localization.Loc.GetString(key, ("where", $"{map} ({x:0.#}, {y:0.#})"));
    }
}

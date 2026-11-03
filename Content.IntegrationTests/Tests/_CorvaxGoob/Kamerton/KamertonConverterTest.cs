#nullable enable
using System.Linq;
using Content.Server._CorvaxGoob.Kamerton;
using Content.Server.Construction.Components;
using Content.Server.Materials;
using Content.Shared._CorvaxGoob.Kamerton;
using System.Collections.Generic;
using Content.Shared.Construction.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Lathe;
using Content.Shared.Lathe.Prototypes;
using Content.Shared.Research.Prototypes;
using Content.Shared.Power.EntitySystems;
using Content.Shared.Stacks;
using Content.Shared.Verbs;
using Robust.Client.ResourceManagement;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton crystal converter: modes and the verb switch, 10 bluespace -> 1 redspace, grinding into stable/unstable
/// energy, charging cells for 100 energy, printing a bluespace crystal from battery power + plasma/uranium sheets.
/// Jobs are finished through <see cref="KamertonConverterSystem.FinishWork"/> so no real time has to pass.
/// </summary>
[TestFixture]
[TestOf(typeof(KamertonConverterSystem))]
public sealed class KamertonConverterTest
{
    private const string Converter = "KamertonConverter";
    private const string Bluespace = "MaterialBSCrystal1";
    private const string Redspace = "MaterialRedspaceCrystal1";
    private const string StableFull = "RedspaceSuperEnergyCell";
    private const string StableEmpty = "RedspaceSuperEnergyCellEmpty";
    private const string UnstableFull = "KamertonUnstableEnergyCell";
    private const string UnstableEmpty = "KamertonUnstableEnergyCellEmpty";

    private static int Count(IEntityManager entMan, MapId map, string proto)
    {
        var total = 0;
        var q = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (q.MoveNext(out var uid, out var meta, out var xform))
        {
            if (xform.MapID != map || meta.EntityPrototype?.ID != proto)
                continue;
            total += entMan.TryGetComponent<StackComponent>(uid, out var s) ? s.Count : 1;
        }
        return total;
    }

    private static EntityUid Stack(IEntityManager entMan, string proto, int count, EntityCoordinates at)
    {
        var uid = entMan.SpawnEntity(proto, at);
        entMan.System<SharedStackSystem>().SetCount(uid, count);
        return uid;
    }

    [Test]
    public async Task PrototypesAndSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var proto = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var conv = proto.Index<EntityPrototype>(Converter);
            Assert.That(conv.TryGetComponent<KamertonConverterComponent>(out var c, factory), Is.True);
            Assert.That(c!.MaxCrystals, Is.EqualTo(10));
            Assert.That(c.StablePerBluespace, Is.EqualTo(10));
            Assert.That(c.UnstablePerRedspace, Is.EqualTo(100));
            Assert.That(c.ChargeCost, Is.EqualTo(100));
            Assert.That(c.PrintPower, Is.EqualTo(20000f));
            Assert.That(c.MaxPower, Is.EqualTo(40000f));
            Assert.That(c.ChargeRate, Is.EqualTo(100f));

            // Board: printed at the science circuit imprinter for 5 steel, 10 gold, 5 silver sheets.
            var recipe = proto.Index<LatheRecipePrototype>("KamertonConverterCircuitboard");
            Assert.That(recipe.Result?.Id, Is.EqualTo("KamertonConverterCircuitboard"));
            Assert.That(recipe.Materials.ToDictionary(kv => kv.Key.Id, kv => kv.Value),
                Is.EquivalentTo(new Dictionary<string, int> { ["Steel"] = 500, ["Gold"] = 1000, ["Silver"] = 500 }));
            Assert.That(proto.Index<LatheRecipePackPrototype>("KamertonBoards").Recipes,
                Does.Contain(new ProtoId<LatheRecipePrototype>("KamertonConverterCircuitboard")));
            Assert.That(proto.Index<EntityPrototype>("CircuitImprinter").TryGetComponent<LatheComponent>(out var imprinter, factory), Is.True);
            Assert.That(imprinter!.DynamicPacks, Does.Contain(new ProtoId<LatheRecipePackPrototype>("KamertonBoards")));
            Assert.That(proto.Index<TechnologyPrototype>("BluespaceMining").RecipeUnlocks,
                Does.Contain(new ProtoId<LatheRecipePrototype>("KamertonConverterCircuitboard")));

            // Board requirements: 3 manipulators + 3 matter bins + 3 capacitors (one merged stock part here) + 1 plasma glass.
            Assert.That(proto.Index<EntityPrototype>("KamertonConverterCircuitboard").TryGetComponent<MachineBoardComponent>(out var board, factory), Is.True);
            Assert.That(board!.Prototype.Id, Is.EqualTo(Converter));
            Assert.That(board.StackRequirements.ToDictionary(kv => kv.Key.Id, kv => kv.Value),
                Is.EquivalentTo(new Dictionary<string, int> { ["Manipulator"] = 9, ["PlasmaGlass"] = 1 }));
            Assert.That(board.TagRequirements, Is.Empty);
            Assert.That(board.ComponentRequirements, Is.Empty);
            Assert.That(conv.TryGetComponent<MachineComponent>(out var machine, factory), Is.True);
            Assert.That(machine!.Board?.Id, Is.EqualTo("KamertonConverterCircuitboard"));

            foreach (var (full, empty) in new[] { (StableFull, StableEmpty), (UnstableFull, UnstableEmpty) })
            {
                Assert.That(proto.Index<EntityPrototype>(full).TryGetComponent<KamertonCellComponent>(out var f, factory), Is.True, full);
                Assert.That(f!.Charged, Is.True);
                Assert.That(f.Swap.Id, Is.EqualTo(empty));
                Assert.That(proto.Index<EntityPrototype>(empty).TryGetComponent<KamertonCellComponent>(out var e, factory), Is.True, empty);
                Assert.That(e!.Charged, Is.False);
                Assert.That(e.Swap.Id, Is.EqualTo(full));
                Assert.That(e.Kind, Is.EqualTo(f.Kind));
            }
            foreach (var id in new[] { Converter, StableEmpty, UnstableFull, UnstableEmpty })
                Assert.That(proto.Index<EntityPrototype>(id).HideSpawnMenu, Is.False, id);
        });

        var res = pair.Client.ResolveDependency<IResourceCache>();
        await pair.Client.WaitAssertion(() =>
        {
            var states = new[] { "base", "screen_create", "screen_destroy", "working_create", "working_destroy", "working_print", "powered", "panel_open" }
                .Concat(Enumerable.Range(0, 11).Select(i => $"fill_{i}"));
            foreach (var (rsi, list) in new (string, IEnumerable<string>)[]
                     {
                         ("/Textures/_CorvaxGoob/Structures/Machines/kamerton_converter.rsi", states),
                         ("/Textures/_CorvaxGoob/Objects/Power/kamerton_unstable_cell.rsi", new[] { "icon", "glow", "empty", "inhand-left", "inhand-right" }),
                         ("/Textures/_CorvaxGoob/Objects/Power/redspace_super_cell.rsi", new[] { "icon", "glow", "empty" }),
                     })
            {
                var r = res.GetResource<RSIResource>(new ResPath(rsi)).RSI;
                foreach (var s in list)
                    Assert.That(r.TryGetState(s, out _), Is.True, $"{rsi}:{s}");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ModesCreateAndGrind()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<KamertonConverterSystem>();
        var verbs = server.System<SharedVerbSystem>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(Converter, map.GridCoords);
            var ent = (uid, entMan.GetComponent<KamertonConverterComponent>(uid));
            var user = entMan.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(ent.Item2.Mode, Is.EqualTo(KamertonConverterMode.Create), "starts in create mode");

            // Create: 9 bluespace is not enough, 10 makes exactly one redspace crystal.
            var bs = Stack(entMan, Bluespace, 9, map.GridCoords);
            Assert.That(sys.TryInsertCrystals(ent, bs, null), Is.EqualTo(9));
            Assert.That(sys.TryStart(ent, null), Is.False, "needs 10 bluespace");
            var bs2 = Stack(entMan, Bluespace, 5, map.GridCoords);
            Assert.That(sys.TryInsertCrystals(ent, bs2, null), Is.EqualTo(1), "capacity is 10");
            Assert.That(entMan.GetComponent<StackComponent>(bs2).Count, Is.EqualTo(4));
            Assert.That(sys.TryStart(ent, null), Is.True);
            Assert.That(ent.Item2.Work, Is.EqualTo(KamertonConverterWork.Creating));
            Assert.That(sys.SetMode(ent, KamertonConverterMode.Destroy, null), Is.False, "no mode switch while busy");
            sys.FinishWork(ent);
            Assert.That(ent.Item2.Bluespace, Is.EqualTo(0));
            Assert.That(Count(entMan, map.MapId, Redspace), Is.EqualTo(1), "10 bluespace -> 1 redspace");

            // Redspace is refused in create mode.
            var rs = Stack(entMan, Redspace, 2, map.GridCoords);
            Assert.That(sys.TryInsertCrystals(ent, rs, null), Is.EqualTo(0));

            // Switch to destroy mode through the alt verb.
            var alt = verbs.GetLocalVerbs(uid, user, typeof(AlternativeVerb), force: true).OfType<AlternativeVerb>().ToList();
            Assert.That(alt.Count, Is.EqualTo(2), "two mode verbs");
            var destroyVerb = alt.Single(v => !v.Disabled);
            verbs.ExecuteVerb(destroyVerb, user, uid, forced: true);
            Assert.That(ent.Item2.Mode, Is.EqualTo(KamertonConverterMode.Destroy));

            // Grind: 4 bluespace -> 40 stable, 2 redspace -> 200 unstable.
            Assert.That(sys.TryInsertCrystals(ent, bs2, null), Is.EqualTo(4));
            Assert.That(sys.TryInsertCrystals(ent, rs, null), Is.EqualTo(2));
            Assert.That(sys.SetMode(ent, KamertonConverterMode.Create, null), Is.False, "redspace loaded keeps destroy mode");
            Assert.That(sys.TryStart(ent, null), Is.True);
            Assert.That(ent.Item2.Work, Is.EqualTo(KamertonConverterWork.Grinding));
            sys.FinishWork(ent);
            Assert.That(ent.Item2.StableEnergy, Is.EqualTo(40));
            Assert.That(ent.Item2.UnstableEnergy, Is.EqualTo(200));
            Assert.That(ent.Item2.Bluespace + ent.Item2.Redspace, Is.EqualTo(0));
            Assert.That(sys.TryStart(ent, null), Is.False, "nothing to grind");

            Assert.That(sys.SetMode(ent, KamertonConverterMode.Create, null), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ChargingCells()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<KamertonConverterSystem>();
        var slots = server.System<ItemSlotsSystem>();

        await server.WaitAssertion(() =>
        {
            var uid = entMan.SpawnEntity(Converter, map.GridCoords);
            var comp = entMan.GetComponent<KamertonConverterComponent>(uid);
            var ent = (uid, comp);

            // Machine frame flow: a spawned converter carries its board and the default parts.
            var machine = entMan.GetComponent<MachineComponent>(uid);
            Assert.That(machine.BoardContainer.ContainedEntities.Select(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID),
                Is.EquivalentTo(new[] { "KamertonConverterCircuitboard" }));
            var parts = new Dictionary<string, int>();
            foreach (var part in machine.PartContainer.ContainedEntities)
            {
                var st = entMan.GetComponent<StackComponent>(part);
                parts[st.StackTypeId] = parts.GetValueOrDefault(st.StackTypeId) + st.Count;
            }
            Assert.That(parts, Is.EquivalentTo(new Dictionary<string, int> { ["Manipulator"] = 9, ["PlasmaGlass"] = 1 }));

            string? InSlot() => slots.GetItemOrNull(uid, comp.CellSlot) is { } c ? entMan.GetComponent<MetaDataComponent>(c).EntityPrototype?.ID : null;

            Assert.That(sys.TryChargeCell(ent, null), Is.False, "no cell");

            var stable = entMan.SpawnEntity(StableEmpty, map.GridCoords);
            Assert.That(slots.TryInsert(uid, comp.CellSlot, stable, null), Is.True);
            comp.StableEnergy = 99;
            Assert.That(sys.TryChargeCell(ent, null), Is.False, "99 < 100");
            Assert.That(InSlot(), Is.EqualTo(StableEmpty));
            comp.StableEnergy = 150;
            comp.UnstableEnergy = 1000;
            Assert.That(sys.TryChargeCell(ent, null), Is.True);
            Assert.That(InSlot(), Is.EqualTo(StableFull), "stable energy charges the super cell");
            Assert.That(comp.StableEnergy, Is.EqualTo(50));
            Assert.That(comp.UnstableEnergy, Is.EqualTo(1000), "unstable untouched");
            Assert.That(sys.TryChargeCell(ent, null), Is.False, "already charged");

            // Swap to an empty unstable cell.
            var old = slots.GetItemOrNull(uid, comp.CellSlot)!.Value;
            entMan.DeleteEntity(old);
            var unstable = entMan.SpawnEntity(UnstableEmpty, map.GridCoords);
            Assert.That(slots.TryInsert(uid, comp.CellSlot, unstable, null), Is.True);
            comp.UnstableEnergy = 50;
            Assert.That(sys.TryChargeCell(ent, null), Is.False, "unstable below 100");
            comp.UnstableEnergy = 100;
            Assert.That(sys.TryChargeCell(ent, null), Is.True);
            Assert.That(InSlot(), Is.EqualTo(UnstableFull), "unstable energy charges the unstable cell");
            Assert.That(comp.UnstableEnergy, Is.EqualTo(0));
            Assert.That(comp.StableEnergy, Is.EqualTo(50));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task BatteryAndPrinting()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<KamertonConverterSystem>();
        var mats = server.System<MaterialStorageSystem>();
        var power = server.System<SharedPowerReceiverSystem>();
        EntityUid uid = default;

        await server.WaitAssertion(() =>
        {
            uid = entMan.SpawnEntity(Converter, map.GridCoords);
            var comp = entMan.GetComponent<KamertonConverterComponent>(uid);
            sys.ChargeBattery((uid, comp), 10f);
            Assert.That(comp.Power, Is.EqualTo(0f), "no charging without power");
            power.SetNeedsPower(uid, false);
        });
        await pair.RunTicksSync(5);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<KamertonConverterComponent>(uid);
            var ent = (uid, comp);
            comp.Power = 0f;
            sys.ChargeBattery(ent, 10f);
            Assert.That(comp.Power, Is.EqualTo(1000f).Within(0.01f), "100 per second");
            sys.ChargeBattery(ent, 1000f);
            Assert.That(comp.Power, Is.EqualTo(40000f), "capped at 40000");

            // Sheets only: ore and glass are refused, plasma/uranium sheets are accepted.
            var user = entMan.SpawnEntity("MobHuman", map.GridCoords);
            Assert.That(mats.TryInsertMaterialEntity(user, entMan.SpawnEntity("PlasmaOre1", map.GridCoords), uid), Is.False, "no ore");
            Assert.That(mats.TryInsertMaterialEntity(user, entMan.SpawnEntity("SheetGlass1", map.GridCoords), uid), Is.False, "only plasma/uranium");

            comp.Power = 19999f;
            Assert.That(sys.TryPrintCrystal(ent, null), Is.False, "needs 20000 power");
            comp.Power = 40000f;
            Assert.That(sys.TryPrintCrystal(ent, null), Is.False, "needs sheets");

            Assert.That(mats.TryInsertMaterialEntity(user, Stack(entMan, "SheetPlasma1", comp.PrintSheets, map.GridCoords), uid), Is.True);
            Assert.That(sys.TryPrintCrystal(ent, null), Is.False, "needs uranium too");
            Assert.That(mats.TryInsertMaterialEntity(user, Stack(entMan, "SheetUranium1", comp.PrintSheets, map.GridCoords), uid), Is.True);
            Assert.That(mats.GetMaterialAmount(uid, "Plasma"), Is.EqualTo(comp.PrintSheets * 100));

            Assert.That(sys.TryPrintCrystal(ent, null), Is.True);
            Assert.That(comp.Power, Is.EqualTo(20000f), "20000 spent");
            Assert.That(mats.GetMaterialAmount(uid, "Plasma"), Is.EqualTo(0));
            Assert.That(mats.GetMaterialAmount(uid, "Uranium"), Is.EqualTo(0));
            Assert.That(comp.Work, Is.EqualTo(KamertonConverterWork.Printing));
            sys.FinishWork(ent);
            Assert.That(Count(entMan, map.MapId, Bluespace), Is.EqualTo(1), "one bluespace crystal printed");
        });

        await pair.CleanReturnAsync();
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Redspace.Physics;
using Content.Server._CorvaxGoob.ShogkkOperative;
using Content.Server.Gravity;
using Content.Server.Weapons.Ranged.Systems;
using Content.Shared._CorvaxGoob.Redspace.Physics;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Wieldable.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ShogkkOperative;

/// <summary>
/// SHOGKK operatives, v2: racking the bolt / pumping like a player, picking their own gun back up, grabbing a weapon
/// from the floor when unarmed, and immunity (with Gilbert) to the redspace map physics.
/// </summary>
[TestFixture]
public sealed class ShogkkOperativeV2Test
{
    private const string Victim = "MobHuman";

    /// <summary>Everything but the organism off (as in RedspacePhysicsTest).</summary>
    private static readonly Dictionary<string, string> OnlyOrganism = new()
    {
        ["inertiaEnabled"] = "false", ["currentEnabled"] = "false", ["wavesEnabled"] = "false",
        ["weightlessEnabled"] = "false", ["blinkEnabled"] = "false", ["anomaliesEnabled"] = "false",
        ["echoEnabled"] = "false", ["hallucinationsEnabled"] = "false", ["dischargesEnabled"] = "false",
        ["lightFlickerEnabled"] = "false", ["radioStaticEnabled"] = "false", ["ambientEnabled"] = "false",
        ["jitterChance"] = "0", ["vomitChancePerMinute"] = "0",
    };

    #region Helpers

    private static async Task FillFloor(TestPair pair, TestMapData map, int minX, int maxX, int minY, int maxY)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = minX; x <= maxX; x++)
            for (var y = minY; y <= maxY; y++)
                tiles.Add((new Vector2i(x, y), map.Tile.Tile));
            mapSys.SetTiles(map.Grid.Owner, map.Grid.Comp, tiles);
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.Server.WaitRunTicks((int) Math.Ceiling(seconds * rate) + 1);
    }

    private static FixedPoint2 Total(IEntityManager entMan, EntityUid target)
    {
        return entMan.TryGetComponent<DamageableComponent>(target, out var dmg) ? dmg.TotalDamage : FixedPoint2.Zero;
    }

    /// <summary>Damage other than asphyxiation (the test maps have no air).</summary>
    private static float Hits(IEntityManager entMan, EntityUid target)
    {
        return entMan.GetComponent<DamageableComponent>(target).Damage.DamageDict
            .Where(kv => kv.Key != "Asphyxiation").Sum(kv => kv.Value.Float());
    }

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static string? Proto(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;

    private static EntityUid HeldGun(IEntityManager entMan, EntityUid mob)
    {
        var hands = entMan.System<SharedHandsSystem>();
        return hands.EnumerateHeld(mob).FirstOrDefault(entMan.HasComponent<GunComponent>);
    }

    private static ShogkkOperativeAIComponent AI(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<ShogkkOperativeAIComponent>(uid);

    private static EntityUid SpawnStill(IEntityManager entMan, TestMapData map, Vector2 at, string proto)
    {
        var uid = entMan.SpawnEntity(proto, map.GridCoords.Offset(at));
        Sturdy(entMan, uid);
        var comp = AI(entMan, uid);
        comp.PauseUntil = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        comp.NextReport = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        return uid;
    }

    /// <summary>Every gun, knife / melee weapon and spare ammo on him, gone.</summary>
    private static void Disarm(IEntityManager entMan, EntityUid op)
    {
        var hands = entMan.System<SharedHandsSystem>();
        var ai = entMan.System<ShogkkOperativeAISystem>();
        foreach (var held in hands.EnumerateHeld(op).ToList())
            entMan.DeleteEntity(held);
        foreach (var item in ai.EnumerateStored(op).ToList())
        {
            if (entMan.HasComponent<GunComponent>(item) || entMan.HasComponent<MeleeWeaponComponent>(item)
                || entMan.HasComponent<BallisticAmmoProviderComponent>(item))
                entMan.DeleteEntity(item);
        }
        if (entMan.System<InventorySystem>().TryGetSlotEntity(op, "suitstorage", out var suit))
            entMan.DeleteEntity(suit);
    }

    #endregion

    /// <summary>
    /// The fork's rifles / SMGs come with the bolt open and the pump gun leaves the spent shell in: the operative racks
    /// the bolt and pumps between shots like a player, and keeps shooting.
    /// </summary>
    [Test]
    public async Task RacksAndPumpsInAFight()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -30, 30, -6, 6);
        var entMan = server.EntMan;
        var ai = server.System<ShogkkOperativeAISystem>();

        var variants = new[] { "MobShogkkOperativeDrozd", "MobShogkkOperativeLecter", "MobShogkkOperativeKammerer" };
        var ops = new List<EntityUid>();
        var humans = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            for (var i = 0; i < variants.Length; i++)
                ops.Add(SpawnStill(entMan, map, new Vector2(-24.5f + i * 24, 0.5f), variants[i]));
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 2; i++)
            {
                var gun = HeldGun(entMan, ops[i]);
                Assert.That(entMan.GetComponent<ChamberMagazineAmmoProviderComponent>(gun).BoltClosed, Is.False,
                    $"{variants[i]}: issued with the bolt open (the fork's default)");
                Assert.That(ai.NeedsRack(gun), Is.True);
            }

            foreach (var op in ops)
            {
                var pos = entMan.GetComponent<TransformComponent>(op).Coordinates.Offset(new Vector2(0f, -2f));
                var human = entMan.SpawnEntity(Victim, pos);
                Sturdy(entMan, human);
                humans.Add(human);
            }
        });

        var boltClosedSeen = new bool[ops.Count];
        for (var step = 0; step < 16; step++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() =>
            {
                for (var i = 0; i < ops.Count; i++)
                {
                    if (AI(entMan, ops[i]).Gun is { } g
                        && entMan.TryGetComponent<ChamberMagazineAmmoProviderComponent>(g, out var c) && c.BoltClosed == true)
                        boltClosedSeen[i] = true;
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < ops.Count; i++)
            {
                var comp = AI(entMan, ops[i]);
                // The SMG can run dry in 8 s (then it's the knife), so look at the gun he was issued.
                var gun = comp.Gun!.Value;
                var heldNames = string.Join(", ", server.System<SharedHandsSystem>().EnumerateHeld(ops[i]).Select(e => Proto(entMan, e)));
                TestContext.Out.WriteLine($"[ops-v2] {variants[i]}: reloads {comp.Reloads}, racks {comp.Racks}, failed {comp.FailedRacks}, loaded {ai.LoadedAmmo(gun)}, holding [{heldNames}], mode {comp.WeaponMode}, human hit {Hits(entMan, humans[i])}");
                Assert.That(comp.Racks, Is.GreaterThanOrEqualTo(1), $"{variants[i]}: racked");
                Assert.That(comp.FailedRacks, Is.LessThan(4), $"{variants[i]}: racking works");
                Assert.That(comp.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Ranged), $"{variants[i]}: still on the gun (spare magazines)");
                Assert.That(Hits(entMan, humans[i]), Is.GreaterThan(0f), $"{variants[i]}: and shoots");
                if (entMan.HasComponent<ChamberMagazineAmmoProviderComponent>(gun))
                    Assert.That(boltClosedSeen[i], $"{variants[i]}: bolt closed by him");
            }

            // Pump: one shot per pump, so several pumps and several shells gone from the 7-shell tube.
            var pump = AI(entMan, ops[2]).Gun!.Value;
            Assert.That(AI(entMan, ops[2]).Racks, Is.GreaterThanOrEqualTo(2), "pumped between shots");
            Assert.That(ai.LoadedAmmo(pump), Is.LessThanOrEqualTo(5), "more than one shell fired");
        });

        // The SMG / rifle run their magazine dry: a fresh magazine from the bag in place of the empty one, racked, firing on.
        await RunSeconds(pair, 7);
        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 2; i++)
            {
                var comp = AI(entMan, ops[i]);
                var gun = comp.Gun!.Value;
                TestContext.Out.WriteLine($"[ops-v2] {variants[i]} later: reloads {comp.Reloads}, racks {comp.Racks}, loaded {ai.LoadedAmmo(gun)}, mode {comp.WeaponMode}");
                Assert.That(comp.Reloads, Is.GreaterThanOrEqualTo(1), $"{variants[i]}: swapped the empty magazine");
                Assert.That(comp.Racks, Is.GreaterThanOrEqualTo(2), $"{variants[i]}: racked after the new magazine");
                Assert.That(comp.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Ranged));
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A fresh magazine in, chamber empty, bolt closed (or bolt open on another gun): racked without a fight.</summary>
    [Test]
    public async Task ChambersAfterMagazineInsert()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -8, 8, -6, 6);
        var entMan = server.EntMan;
        var ai = server.System<ShogkkOperativeAISystem>();
        var slots = server.System<ItemSlotsSystem>();
        var containers = server.System<SharedContainerSystem>();
        var gunSys = server.System<GunSystem>();

        EntityUid rifle = default, smg = default;
        await server.WaitPost(() =>
        {
            rifle = SpawnStill(entMan, map, new Vector2(-4.5f, 0.5f), "MobShogkkOperativeLecter");
            smg = SpawnStill(entMan, map, new Vector2(4.5f, 0.5f), "MobShogkkOperativeDrozd");
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var gun = HeldGun(entMan, rifle);
            Assert.That(slots.TryEject(gun, "gun_magazine", null, out var mag), Is.True);
            if (containers.TryGetContainer(gun, "gun_chamber", out var chamber))
                foreach (var e in chamber.ContainedEntities.ToList())
                    entMan.DeleteEntity(e);
            var chamberComp = entMan.GetComponent<ChamberMagazineAmmoProviderComponent>(gun);
            gunSys.SetBoltClosed(gun, chamberComp, true);
            Assert.That(slots.TryInsert(gun, "gun_magazine", mag!.Value, null), Is.True);
            Assert.That(gunSys.GetChamberEntity(gun), Is.Null, "nothing chambered after the magazine went in");
            Assert.That(ai.NeedsRack(gun), Is.True);
        });

        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
        {
            var gun = HeldGun(entMan, rifle);
            var chambered = gunSys.GetChamberEntity(gun);
            TestContext.Out.WriteLine($"[ops-v2] rifle racks {AI(entMan, rifle).Racks}, chambered {chambered}, smg racks {AI(entMan, smg).Racks}");
            Assert.That(chambered, Is.Not.Null, "a round chambered");
            Assert.That(entMan.GetComponent<CartridgeAmmoComponent>(chambered!.Value).Spent, Is.False);
            Assert.That(ai.NeedsRack(gun), Is.False);
            Assert.That(AI(entMan, rifle).Racks, Is.GreaterThanOrEqualTo(1));

            var smgGun = HeldGun(entMan, smg);
            Assert.That(entMan.GetComponent<ChamberMagazineAmmoProviderComponent>(smgGun).BoltClosed, Is.True, "open bolt closed on a round");
            Assert.That(gunSys.GetChamberEntity(smgGun), Is.Not.Null);
            Assert.That(entMan.TryGetComponent<WieldableComponent>(smgGun, out var wield) && wield.Wielded, "still in both hands");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>The gun knocked out of his hands (on the floor a couple of tiles away): he goes and picks it back up.</summary>
    [Test]
    public async Task PicksUpHisDroppedGun()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -8, 8, -8, 8);
        var entMan = server.EntMan;
        var hands = server.System<SharedHandsSystem>();
        var xforms = server.System<TransformSystem>();

        EntityUid op = default, gun = default;
        await server.WaitPost(() => op = SpawnStill(entMan, map, new Vector2(0.5f, 0.5f), "MobShogkkOperativeAkm"));
        await server.WaitRunTicks(3);
        await server.WaitPost(() =>
        {
            gun = HeldGun(entMan, op);
            Assert.That(AI(entMan, op).Gun, Is.EqualTo(gun));
            if (entMan.TryGetComponent<WieldableComponent>(gun, out var wield) && wield.Wielded)
                server.System<Content.Shared.Wieldable.SharedWieldableSystem>().TryUnwield(gun, wield, op, force: true);
            Assert.That(hands.TryDrop(op, gun, checkActionBlocker: false), Is.True);
            xforms.SetCoordinates(gun, map.GridCoords.Offset(new Vector2(2.5f, -2.5f)));
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => Assert.That(hands.IsHolding(op, gun), Is.False, "dropped"));

        await RunSeconds(pair, 8);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, op);
            TestContext.Out.WriteLine($"[ops-v2] re-pickup: holding {hands.IsHolding(op, gun)}, pickups {comp.Pickups}, task {comp.Task}");
            Assert.That(hands.IsHolding(op, gun), "his gun back in his hands");
            Assert.That(comp.Pickups, Is.GreaterThanOrEqualTo(1));
            Assert.That(comp.Gun, Is.EqualTo(gun));
            Assert.That(comp.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Ranged));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Nothing to fight with: he goes for a weapon he sees on the floor, a loaded gun before a nearer knife, and shoots
    /// with it; with only a knife lying around, the knife.
    /// </summary>
    [Test]
    public async Task UnarmedPicksUpFloorWeapon()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -30, 30, -10, 10);
        var entMan = server.EntMan;
        var hands = server.System<SharedHandsSystem>();

        EntityUid op = default, op2 = default, smg = default, knife = default, knife2 = default;
        await server.WaitPost(() =>
        {
            op = SpawnStill(entMan, map, new Vector2(-15.5f, 0.5f), "MobShogkkOperativeKammerer");
            op2 = SpawnStill(entMan, map, new Vector2(15.5f, 0.5f), "MobShogkkOperativeKammerer");
        });
        await server.WaitRunTicks(3);
        await server.WaitPost(() =>
        {
            Disarm(entMan, op);
            Disarm(entMan, op2);
            knife = entMan.SpawnEntity("CombatKnife", map.GridCoords.Offset(new Vector2(-15.5f, -1.5f)));
            smg = entMan.SpawnEntity("WeaponSubMachineGunDrozd", map.GridCoords.Offset(new Vector2(-13.5f, -3.5f)));
            knife2 = entMan.SpawnEntity("CombatKnife", map.GridCoords.Offset(new Vector2(16.5f, -2.5f)));
        });

        await RunSeconds(pair, 10);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, op);
            var held = hands.EnumerateHeld(op).Select(e => Proto(entMan, e)).ToList();
            TestContext.Out.WriteLine($"[ops-v2] unarmed: holding {string.Join(", ", held)}, mode {comp.WeaponMode}, pickups {comp.Pickups}");
            Assert.That(hands.IsHolding(op, smg), "took the loaded SMG (over the nearer knife)");
            Assert.That(hands.IsHolding(op, knife), Is.False);
            Assert.That(comp.Gun, Is.EqualTo(smg));
            Assert.That(comp.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Ranged));

            var comp2 = AI(entMan, op2);
            Assert.That(hands.IsHolding(op2, knife2), "only a knife around: the knife");
            Assert.That(comp2.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Melee));
        });

        // And uses it.
        EntityUid human = default;
        await server.WaitPost(() =>
        {
            var pos = entMan.GetComponent<TransformComponent>(op).Coordinates;
            human = entMan.SpawnEntity(Victim, pos.Offset(new Vector2(0f, -3f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 8);
        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[ops-v2] with the picked-up SMG: human hit {Hits(entMan, human)}, racks {AI(entMan, op).Racks}");
            Assert.That(Hits(entMan, human), Is.GreaterThan(0f), "shoots with the picked-up gun");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Zombified SHOGKK (Gilbert, the operatives) are untouched by redspace: no organism damage, no effects at all in a
    /// storm; an unprotected human next to them still takes damage.
    /// </summary>
    [Test]
    public async Task RedspaceImmunity()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var sys = server.System<RedspaceMapSystem>();
        var xforms = server.System<TransformSystem>();

        await server.WaitAssertion(() =>
        {
            foreach (var proto in new[] { "MobGilbert", "MobShogkkOperative", "MobShogkkOperativeLecter", "MobShogkkOperativeDrozd", "MobShogkkOperativeKammerer", "MobShogkkOperativeAkm" })
                Assert.That(server.ProtoMan.Index<Robust.Shared.Prototypes.EntityPrototype>(proto).Components.ContainsKey("RedspaceImmune"), proto);
            Assert.That(server.ProtoMan.Index<Robust.Shared.Prototypes.EntityPrototype>(Victim).Components.ContainsKey("RedspaceImmune"), Is.False);

            sys.SetRedspace(map.MapUid, "RedspaceNormal", OnlyOrganism);
            var player = entMan.SpawnEntity(Victim, map.MapCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, player);
            var gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert", map.GridCoords.Offset(new Vector2(3f, 0f)));
            var op = entMan.SpawnEntity("MobShogkkOperative", new MapCoordinates(new Vector2(-3f, 0f), map.MapId));
            var npc = entMan.SpawnEntity(Victim, new MapCoordinates(new Vector2(0f, 3f), map.MapId));
            var g0 = Total(entMan, gilbert);
            var o0 = Total(entMan, op);

            for (var t = 0; t < 60; t++)
                sys.Step(1f);

            TestContext.Out.WriteLine($"[ops-v2] organism 60 s: human {Total(entMan, player)}, npc {Total(entMan, npc)}, gilbert {Total(entMan, gilbert) - g0}, op {Total(entMan, op) - o0}");
            Assert.That(Total(entMan, player), Is.GreaterThan(FixedPoint2.Zero), "unprotected human hurt");
            Assert.That(Total(entMan, npc), Is.GreaterThan(FixedPoint2.Zero), "unprotected NPC human hurt");
            Assert.That(Total(entMan, gilbert), Is.EqualTo(g0), "Gilbert untouched");
            Assert.That(Total(entMan, op), Is.EqualTo(o0), "operative untouched");
            Assert.That(entMan.HasComponent<RedspaceAffectedComponent>(gilbert), Is.False);
            Assert.That(entMan.HasComponent<RedspaceAffectedComponent>(op), Is.False);
            Assert.That(entMan.HasComponent<RedspaceAffectedComponent>(npc), Is.True);

            // A storm with every effect on: still nothing on them, not even a push.
            sys.SetRedspace(map.MapUid, "RedspaceStorm", null);
            var gPos = xforms.GetWorldPosition(gilbert);
            var oPos = xforms.GetWorldPosition(op);
            for (var t = 0; t < 60; t++)
                sys.Step(1f);

            TestContext.Out.WriteLine($"[ops-v2] storm 60 s: human {Total(entMan, player)}, gilbert {Total(entMan, gilbert) - g0}, op {Total(entMan, op) - o0}");
            Assert.That(Total(entMan, gilbert), Is.EqualTo(g0), "storm: Gilbert untouched");
            Assert.That(Total(entMan, op), Is.EqualTo(o0), "storm: operative untouched");
            Assert.That(entMan.HasComponent<RedspaceAffectedComponent>(gilbert), Is.False);
            Assert.That(entMan.HasComponent<RedspaceAffectedComponent>(op), Is.False);
            Assert.That((xforms.GetWorldPosition(gilbert) - gPos).Length(), Is.LessThan(0.01f), "Gilbert not moved");
            Assert.That((xforms.GetWorldPosition(op) - oPos).Length(), Is.LessThan(0.01f), "operative not moved");
        });

        await pair.CleanReturnAsync();
    }
}

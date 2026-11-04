using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.ShogkkOperative;
using Content.Server.Ghost.Roles.Components;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Storage;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.IoC;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ShogkkOperative;

/// <summary>
/// SHOGKK operatives: gear, hostility (with humans and Gilbert, none among themselves), the three radio phrases with
/// their range / conditions / cooldown, and reloading from the bag.
/// </summary>
[TestFixture]
public sealed class ShogkkOperativeTest
{
    private const string Operative = "MobShogkkOperative";
    private const string Victim = "MobHuman";

    private static readonly (string Mob, string Gun, string Ammo)[] Variants =
    {
        ("MobShogkkOperativeLecter", "WeaponRifleLecter", "MagazineRifle"),
        ("MobShogkkOperativeDrozd", "WeaponSubMachineGunDrozd", "MagazinePistolSubMachineGun"),
        ("MobShogkkOperativeKammerer", "WeaponShotgunKammerer", "BoxLethalshot"),
        ("MobShogkkOperativeAkm", "WeaponRifleAk", "MagazineLightRifle"),
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

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static string? Proto(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID;

    private static List<EntityUid> BagContents(IEntityManager entMan, EntityUid mob)
    {
        var inv = entMan.System<InventorySystem>();
        if (!inv.TryGetSlotEntity(mob, "back", out var bag) || !entMan.TryGetComponent<StorageComponent>(bag, out var storage))
            return new();

        return storage.Container.ContainedEntities.ToList();
    }

    private static EntityUid HeldGun(IEntityManager entMan, EntityUid mob)
    {
        var hands = entMan.System<SharedHandsSystem>();
        return hands.EnumerateHeld(mob).FirstOrDefault(entMan.HasComponent<GunComponent>);
    }

    private static ShogkkOperativeAIComponent AI(IEntityManager entMan, EntityUid uid) => entMan.GetComponent<ShogkkOperativeAIComponent>(uid);

    private static int Count(ShogkkOperativeAIComponent comp, ShogkkOperativePhrase phrase) => comp.PhraseCount.GetValueOrDefault(phrase);

    /// <summary>Operatives kept from wandering off (long patrol pause), for the radio scenarios.</summary>
    private static EntityUid SpawnStill(IEntityManager entMan, TestMapData map, Vector2 at, string proto = Operative)
    {
        var uid = entMan.SpawnEntity(proto, map.GridCoords.Offset(at));
        Sturdy(entMan, uid);
        var comp = AI(entMan, uid);
        comp.PauseUntil = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        comp.NextReport = IoCManager.Resolve<IGameTiming>().CurTime + TimeSpan.FromHours(1);
        return uid;
    }

    #endregion

    /// <summary>Clothing, tag with his own name, bag contents and the gun for each variant; the spawner; no ghost role.</summary>
    [Test]
    public async Task SpawnGear()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -6, 6, -6, 6);
        var entMan = server.EntMan;
        var inv = server.System<InventorySystem>();

        var mobs = new List<EntityUid>();
        EntityUid spawner = default;
        await server.WaitPost(() =>
        {
            for (var i = 0; i < Variants.Length; i++)
                mobs.Add(entMan.SpawnEntity(Variants[i].Mob, map.GridCoords.Offset(new Vector2(-4.5f + i * 3, 0.5f))));
            mobs.Add(entMan.SpawnEntity(Operative, map.GridCoords.Offset(new Vector2(0.5f, 3.5f))));
            spawner = entMan.SpawnEntity("SpawnMobShogkkOperative", map.GridCoords.Offset(new Vector2(0.5f, -3.5f)));
            foreach (var mob in mobs)
                AI(entMan, mob).PauseUntil = server.Timing.CurTime + TimeSpan.FromHours(1);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var names = new HashSet<string>();
            for (var i = 0; i < mobs.Count; i++)
            {
                var mob = mobs[i];
                Assert.That(entMan.HasComponent<ShogkkOperativeComponent>(mob));
                Assert.That(entMan.HasComponent<GhostRoleComponent>(mob), Is.False, "no ghost role");

                Assert.That(inv.TryGetSlotEntity(mob, "outerClothing", out var vest) && Proto(entMan, vest!.Value) == "ClothingOuterArmorBasic", "black security vest");
                Assert.That(inv.TryGetSlotEntity(mob, "head", out var helmet) && Proto(entMan, helmet!.Value) == "ClothingHeadHelmetShogkk");
                Assert.That(inv.TryGetSlotEntity(mob, "jumpsuit", out var suit) && Proto(entMan, suit!.Value) == "ClothingUniformJumpsuitShogkk");
                Assert.That(inv.TryGetSlotEntity(mob, "mask", out var mask) && Proto(entMan, mask!.Value) == "ClothingMaskGasSwat");

                Assert.That(inv.TryGetSlotEntity(mob, "pocket1", out var radio) && Proto(entMan, radio!.Value) == "RadioHandheldShogkkOperative");
                Assert.That(entMan.HasComponent<GilbertCrackleComponent>(radio!.Value), Is.False, "radio: no crackle");
                Assert.That(entMan.HasComponent<PointLightComponent>(radio.Value), Is.False, "radio: no glow");

                var name = entMan.GetComponent<MetaDataComponent>(mob).EntityName;
                Assert.That(name.Split(' ').Length, Is.EqualTo(2), $"full name '{name}'");
                names.Add(name);
                Assert.That(inv.TryGetSlotEntity(mob, "neck", out var tag) && Proto(entMan, tag!.Value) == "ClothingNeckDogtagShogkkOperative");
                var desc = entMan.GetComponent<MetaDataComponent>(tag!.Value).EntityDescription;
                Assert.That(desc, Does.StartWith(name + ","), "the tag carries his full name");
                TestContext.Out.WriteLine($"[ops] {name}: tag '{desc}'");

                var gun = HeldGun(entMan, mob);
                Assert.That(gun, Is.Not.EqualTo(EntityUid.Invalid), "a gun in his hands");
                var bag = BagContents(entMan, mob).Select(e => Proto(entMan, e)).ToList();
                Assert.That(bag.Count(p => p == "MedkitFilled"), Is.EqualTo(1), "one first-aid kit");
                Assert.That(bag.Count(p => p == "CombatKnife"), Is.EqualTo(1), "a combat knife");

                var gear = entMan.GetComponent<ShogkkOperativeGearComponent>(mob);
                Assert.That(gear.IssuedWeapon, Is.Not.Null);
                var expected = i < Variants.Length ? Variants[i] : Variants.First(v => v.Gun == Proto(entMan, gun));
                Assert.That(Proto(entMan, gun), Is.EqualTo(expected.Gun));
                Assert.That(bag.Count(p => p == expected.Ammo), Is.EqualTo(2), $"two {expected.Ammo} in the bag");
                Assert.That(server.System<ShogkkOperativeAISystem>().LoadedAmmo(gun), Is.GreaterThan(0), "loaded");
                Assert.That(entMan.GetComponent<NpcFactionMemberComponent>(mob).Factions.Select(f => f.Id), Does.Contain("ShogkkOperative"));
            }

            Assert.That(names.Count, Is.GreaterThan(1), "random names");
            var spawned = entMan.EntityQuery<ShogkkOperativeComponent>().Count();
            Assert.That(spawned, Is.EqualTo(mobs.Count + 1), "the spawner made one operative");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Attacks a human on sight; Gilbert and operatives are mutual enemies; never attacks another operative.</summary>
    [Test]
    public async Task Hostility()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -12, 12, -12, 12);
        var entMan = server.EntMan;
        var ai = server.System<ShogkkOperativeAISystem>();
        var faction = server.System<NpcFactionSystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid op = default, op2 = default, human = default;
        await server.WaitPost(() =>
        {
            op = SpawnStill(entMan, map, new Vector2(0.5f, 0.5f), "MobShogkkOperativeAkm");
            op2 = SpawnStill(entMan, map, new Vector2(1.5f, 0.5f), "MobShogkkOperativeDrozd");
        });

        // Two operatives side by side: friends.
        await RunSeconds(pair, 2);
        await server.WaitAssertion(() =>
        {
            Assert.That(ai.IsEnemy(op2), Is.False);
            Assert.That(faction.IsEntityFriendly(op, op2));
            Assert.That(AI(entMan, op).Target, Is.Null);
            Assert.That(AI(entMan, op2).Target, Is.Null);
            Assert.That(AI(entMan, op).State, Is.Not.EqualTo(ShogkkOperativeAIState.Combat));
            Assert.That(Total(entMan, op) + Total(entMan, op2), Is.EqualTo(FixedPoint2.Zero));
        });

        await server.WaitPost(() =>
        {
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, op);
            Assert.That(comp.Target, Is.EqualTo(human), "goes for the human at once");
            Assert.That(comp.State, Is.EqualTo(ShogkkOperativeAIState.Combat));
            Assert.That(Total(entMan, human), Is.GreaterThan(FixedPoint2.Zero), "and hurts him");
            var d2 = entMan.GetComponent<DamageableComponent>(op2).Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}");
            TestContext.Out.WriteLine($"[ops] human {Total(entMan, human)}, ally {string.Join(" ", d2)}");
            // The test map has no air, so only asphyxiation is expected on him.
            var hits = entMan.GetComponent<DamageableComponent>(op2).Damage.DamageDict
                .Where(kv => kv.Key != "Asphyxiation").Sum(kv => kv.Value.Float());
            Assert.That(hits, Is.EqualTo(0f), "no friendly fire on the ally next to him");
            entMan.DeleteEntity(human);
        });
        await RunSeconds(pair, 1);

        // Gilbert: mutual enemies.
        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(op2);
            gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert", map.GridCoords.Offset(new Vector2(0.5f, 4.5f)));
            Sturdy(entMan, gilbert);
            Sturdy(entMan, op);
        });
        await RunSeconds(pair, 4);
        await server.WaitAssertion(() =>
        {
            Assert.That(ai.IsEnemy(gilbert), "Gilbert is an enemy to them");
            Assert.That(protos.Index<NpcFactionPrototype>("ShogkkOperative").Hostile.Contains("Gilbert") && protos.Index<NpcFactionPrototype>("Gilbert").Hostile.Contains("ShogkkOperative"), "hostile factions both ways");
            Assert.That(AI(entMan, op).Target, Is.EqualTo(gilbert), "the operative goes for Gilbert");
            var g = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[ops] gilbert {g.State} -> {g.Target}, dmg op {Total(entMan, op)} gilbert {Total(entMan, gilbert)}");
            Assert.That(g.Target, Is.EqualTo(op), "Gilbert goes for the operative");
            Assert.That(g.State, Is.EqualTo(GilbertAIState.Combat), "without a surrender demand");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>An ally standing between him and the enemy: he holds fire and steps aside instead of shooting through him.</summary>
    [Test]
    public async Task NoShootingThroughAllies()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -10, 10, -10, 10);
        var entMan = server.EntMan;

        EntityUid op = default, front = default, human = default;
        await server.WaitPost(() =>
        {
            op = SpawnStill(entMan, map, new Vector2(0.5f, 2.5f), "MobShogkkOperativeAkm");
            front = SpawnStill(entMan, map, new Vector2(0.5f, 0.5f), "MobShogkkOperativeKammerer");
            // The ally in front has nothing to shoot with, so only the one behind can hit him.
            var hands = entMan.System<SharedHandsSystem>();
            foreach (var held in hands.EnumerateHeld(front).ToList())
                entMan.DeleteEntity(held);
            foreach (var item in entMan.System<ShogkkOperativeAISystem>().EnumerateStored(front).ToList())
                entMan.DeleteEntity(item);
            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -2.5f)));
            Sturdy(entMan, human);
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() =>
        {
            // Pin the ally in the line (no AI moves, no stepping out of the way).
            entMan.RemoveComponent<ShogkkOperativeAIComponent>(front);
            server.System<TransformSystem>().AnchorEntity(front);
            entMan.GetComponent<ShogkkOperativeAIComponent>(op).Target = human;
        });
        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var hits = entMan.GetComponent<DamageableComponent>(front).Damage.DamageDict
                .Where(kv => kv.Key != "Asphyxiation").Sum(kv => kv.Value.Float());
            var xf = server.System<TransformSystem>();
            TestContext.Out.WriteLine($"[ops] line of fire: ally hits {hits}, human {Total(entMan, human)}, shooter moved to {xf.GetWorldPosition(op)}");
            Assert.That(hits, Is.EqualTo(0f), "never shot the ally in front");
            Assert.That(Total(entMan, human), Is.GreaterThan(FixedPoint2.Zero), "stepped aside and shot the enemy");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// «Вижу противника»: once per new enemy, allies within 40 tiles learn its position and come to check, the one at
    /// 45 does not; the radio cooldown (datafield, 60 s by default) holds back the call about the second enemy.
    /// </summary>
    [Test]
    public async Task RadioEnemySpotted()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -48, 34, -4, 4);
        var entMan = server.EntMan;

        EntityUid a = default, near = default, far = default, enemy = default, enemy2 = default;
        await server.WaitPost(() =>
        {
            a = SpawnStill(entMan, map, new Vector2(0.5f, 0.5f));
            near = SpawnStill(entMan, map, new Vector2(30.5f, 0.5f));
            far = SpawnStill(entMan, map, new Vector2(-44.5f, 0.5f));
        });
        await RunSeconds(pair, 1);
        await server.WaitAssertion(() =>
        {
            Assert.That(AI(entMan, a).RadioCooldown, Is.EqualTo(TimeSpan.FromSeconds(60)), "default cooldown datafield");
            Assert.That(AI(entMan, a).RadioRange, Is.EqualTo(40f));
        });

        await server.WaitPost(() =>
        {
            enemy = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
            Sturdy(entMan, enemy);
        });
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, a);
            Assert.That(Count(comp, ShogkkOperativePhrase.EnemySpotted), Is.EqualTo(1), "one call");
            Assert.That(AI(entMan, near).KnownEnemies.ContainsKey(enemy), "the ally at 30 tiles got the coordinates");
            Assert.That(AI(entMan, near).State, Is.EqualTo(ShogkkOperativeAIState.Assist), "and goes to check");
            Assert.That(AI(entMan, far).KnownEnemies.ContainsKey(enemy), Is.False, "the one at 45 tiles did not");
            Assert.That(AI(entMan, far).State, Is.EqualTo(ShogkkOperativeAIState.Patrol));
            Assert.That(Count(AI(entMan, near), ShogkkOperativePhrase.EnemySpotted), Is.EqualTo(0), "no repeat by the ally");
        });

        // The same enemy again: no new call. A second enemy within the cooldown: no call either.
        await server.WaitPost(() =>
        {
            enemy2 = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-1.5f, 0.5f)));
            Sturdy(entMan, enemy2);
        });
        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.EnemySpotted), Is.EqualTo(1), "cooldown holds"));

        // A short cooldown (the datafield): the second, still unreported enemy is called in.
        await server.WaitPost(() =>
        {
            var comp = AI(entMan, a);
            comp.RadioCooldown = TimeSpan.FromSeconds(1);
            comp.NextPhrase[ShogkkOperativePhrase.EnemySpotted] = server.Timing.CurTime + TimeSpan.FromSeconds(1);
        });
        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, a);
            Assert.That(Count(comp, ShogkkOperativePhrase.EnemySpotted), Is.EqualTo(2), "the new enemy, after the cooldown");
            Assert.That(comp.ReportedEnemies, Does.Contain(enemy2));
        });
        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.EnemySpotted), Is.EqualTo(2), "one call per enemy"));

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// «Нужна помощь»: at 40 HP before crit with nobody within 10 tiles; the nearest allies within 40 come; not with an
    /// ally next to him; not more often than the cooldown.
    /// </summary>
    [Test]
    public async Task RadioNeedHelp()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -48, 34, -4, 4);
        var entMan = server.EntMan;
        var damage = server.System<DamageableSystem>();
        var thresholds = server.System<MobThresholdSystem>();
        var protos = server.ResolveDependency<IPrototypeManager>();

        EntityUid a = default, b = default, c = default, far = default, d = default;
        await server.WaitPost(() =>
        {
            a = entMan.SpawnEntity(Operative, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            AI(entMan, a).PauseUntil = server.Timing.CurTime + TimeSpan.FromHours(1);
            b = SpawnStill(entMan, map, new Vector2(18.5f, 0.5f));
            c = SpawnStill(entMan, map, new Vector2(30.5f, 0.5f));
            far = SpawnStill(entMan, map, new Vector2(-44.5f, 0.5f));
        });
        await RunSeconds(pair, 1);

        // 50 HP left before crit: not yet.
        FixedPoint2 crit = default;
        await server.WaitPost(() =>
        {
            Assert.That(thresholds.TryGetThresholdForState(a, Content.Shared.Mobs.MobState.Critical, out var critN));
            crit = critN!.Value;
            damage.TryChangeDamage(a, new DamageSpecifier(protos.Index<DamageTypePrototype>("Blunt"), crit - 50), true);
        });
        await RunSeconds(pair, 1);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.NeedHelp), Is.EqualTo(0), "50 HP left: no call"));

        await server.WaitPost(() =>
            damage.TryChangeDamage(a, new DamageSpecifier(protos.Index<DamageTypePrototype>("Blunt"), 15), true));
        await RunSeconds(pair, 1);
        float bStart = 0, cStart = 0;
        await server.WaitAssertion(() =>
        {
            TestContext.Out.WriteLine($"[ops] crit {crit}, damage {Total(entMan, a)}");
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.NeedHelp), Is.EqualTo(1), "35 HP left, alone: «Нужна помощь»");
            Assert.That(AI(entMan, b).State, Is.EqualTo(ShogkkOperativeAIState.Assist));
            Assert.That(AI(entMan, b).AssistAlly, Is.EqualTo(a));
            Assert.That(AI(entMan, c).State, Is.EqualTo(ShogkkOperativeAIState.Assist));
            Assert.That(AI(entMan, far).State, Is.EqualTo(ShogkkOperativeAIState.Patrol), "45 tiles: out of range");
            var xf = server.System<TransformSystem>();
            bStart = (xf.GetWorldPosition(b) - xf.GetWorldPosition(a)).Length();
            cStart = (xf.GetWorldPosition(c) - xf.GetWorldPosition(a)).Length();
        });

        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var xf = server.System<TransformSystem>();
            var bNow = (xf.GetWorldPosition(b) - xf.GetWorldPosition(a)).Length();
            var cNow = (xf.GetWorldPosition(c) - xf.GetWorldPosition(a)).Length();
            TestContext.Out.WriteLine($"[ops] responders: b {bStart:F1} -> {bNow:F1}, c {cStart:F1} -> {cNow:F1}");
            Assert.That(bNow, Is.LessThan(bStart - 4), "the ally comes");
            Assert.That(cNow, Is.LessThan(cStart - 4), "the other one too");
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.NeedHelp), Is.EqualTo(1), "cooldown: no repeat");
        });

        // An ally within 10 tiles: no call (cooldown out of the way).
        await server.WaitPost(() =>
        {
            AI(entMan, a).NextPhrase.Clear();
            AI(entMan, a).PhraseCount.Clear();
            d = SpawnStill(entMan, map, new Vector2(4.5f, 0.5f));
        });
        await RunSeconds(pair, 2);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.NeedHelp), Is.EqualTo(0), "an ally next to him: no call"));

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// «Докладываю»: on patrol every ReportInterval (6 min), sharing his position and explored map within 40 tiles; the
    /// cooldown applies on top.
    /// </summary>
    [Test]
    public async Task RadioReport()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -48, 34, -4, 4);
        var entMan = server.EntMan;

        EntityUid a = default, near = default, far = default;
        await server.WaitPost(() =>
        {
            a = SpawnStill(entMan, map, new Vector2(0.5f, 0.5f));
            near = SpawnStill(entMan, map, new Vector2(30.5f, 0.5f));
            far = SpawnStill(entMan, map, new Vector2(-44.5f, 0.5f));
        });
        await RunSeconds(pair, 1);

        (EntityUid, Vector2i) aCell = default;
        await server.WaitPost(() =>
        {
            var comp = AI(entMan, a);
            Assert.That(comp.ReportInterval, Is.EqualTo(TimeSpan.FromMinutes(6)));
            // Fresh from spawn, the first report is 6 minutes away.
            Assert.That(comp.State, Is.EqualTo(ShogkkOperativeAIState.Patrol));
            aCell = server.System<ShogkkOperativeAISystem>().CurrentCell(a, comp, entMan.GetComponent<TransformComponent>(a))!.Value;
            Assert.That(AI(entMan, near).Visited.ContainsKey(aCell), Is.False);
            comp.NextReport = server.Timing.CurTime + TimeSpan.FromSeconds(0.5);
        });
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, a);
            Assert.That(Count(comp, ShogkkOperativePhrase.Report), Is.EqualTo(1), "«Докладываю»");
            var next = comp.NextReport - server.Timing.CurTime;
            Assert.That(next.TotalMinutes, Is.InRange(5.8, 6.0), "next one in 6 minutes");
            Assert.That(AI(entMan, near).AllyPositions.ContainsKey(a), "the ally at 30 tiles knows where he is");
            Assert.That(AI(entMan, near).Visited.ContainsKey(aCell), "and what he explored");
            Assert.That(AI(entMan, far).AllyPositions.ContainsKey(a), Is.False, "the one at 45 tiles does not");
            Assert.That(AI(entMan, far).Visited.ContainsKey(aCell), Is.False);
        });

        // A short interval: still capped by the 60 s cooldown...
        await server.WaitPost(() =>
        {
            var comp = AI(entMan, a);
            comp.ReportInterval = TimeSpan.FromSeconds(1);
            comp.NextReport = server.Timing.CurTime;
        });
        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.Report), Is.EqualTo(1), "the cooldown holds it back"));

        // ...and with a short cooldown it repeats on its interval.
        await server.WaitPost(() =>
        {
            var comp = AI(entMan, a);
            comp.RadioCooldown = TimeSpan.FromSeconds(0.5);
            comp.NextPhrase.Clear();
        });
        await RunSeconds(pair, 3.2);
        await server.WaitAssertion(() =>
            Assert.That(Count(AI(entMan, a), ShogkkOperativePhrase.Report), Is.InRange(3, 5), "every interval"));

        await pair.CleanReturnAsync();
    }

    /// <summary>Empty magazine / empty tube in a fight: reloads from the bag; no ammo left at all: the knife.</summary>
    [Test]
    public async Task ReloadFromBag()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, -12, 12, -12, 12);
        var entMan = server.EntMan;
        var ai = server.System<ShogkkOperativeAISystem>();
        var slots = server.System<ItemSlotsSystem>();
        var containers = server.System<SharedContainerSystem>();

        EntityUid rifle = default, shotgun = default, human = default, human2 = default;
        await server.WaitPost(() =>
        {
            rifle = SpawnStill(entMan, map, new Vector2(-4.5f, 0.5f), "MobShogkkOperativeLecter");
            shotgun = SpawnStill(entMan, map, new Vector2(4.5f, 0.5f), "MobShogkkOperativeKammerer");
        });
        await server.WaitRunTicks(3);

        await server.WaitPost(() =>
        {
            // Rifle: the magazine pulled out and gone, the chamber emptied.
            var gun = HeldGun(entMan, rifle);
            if (slots.TryEject(gun, "gun_magazine", null, out var mag))
                entMan.DeleteEntity(mag.Value);
            if (containers.TryGetContainer(gun, "gun_chamber", out var chamber))
                foreach (var e in chamber.ContainedEntities.ToList())
                    entMan.DeleteEntity(e);

            // Shotgun: empty tube.
            var pump = HeldGun(entMan, shotgun);
            var ballistic = entMan.GetComponent<BallisticAmmoProviderComponent>(pump);
            server.System<Content.Server.Weapons.Ranged.Systems.GunSystem>().SetBallisticUnspawned((pump, ballistic), 0);
            Assert.That(ballistic.Entities, Is.Empty, "a fresh tube holds only unspawned shells");

            Assert.That(ai.LoadedAmmo(gun), Is.EqualTo(0));
            Assert.That(ai.LoadedAmmo(pump), Is.EqualTo(0));

            human = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-4.5f, -1.5f)));
            human2 = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4.5f, -1.5f)));
            Sturdy(entMan, human);
            Sturdy(entMan, human2);
        });

        await RunSeconds(pair, 10);
        await server.WaitAssertion(() =>
        {
            var gun = HeldGun(entMan, rifle);
            var pump = HeldGun(entMan, shotgun);
            TestContext.Out.WriteLine($"[ops] rifle reloads {AI(entMan, rifle).Reloads} ammo {(gun.Valid ? ai.LoadedAmmo(gun) : -1)}, " +
                                      $"shotgun reloads {AI(entMan, shotgun).Reloads} ammo {(pump.Valid ? ai.LoadedAmmo(pump) : -1)}; " +
                                      $"damage {Total(entMan, human)} / {Total(entMan, human2)}");
            Assert.That(AI(entMan, rifle).Reloads, Is.GreaterThanOrEqualTo(1), "rifle reloaded");
            Assert.That(gun.Valid, "the rifle back in his hands");
            // Racking right after the swap, he can empty the new one too and be mid-way through the next swap.
            var midSwap = server.System<SharedHandsSystem>().EnumerateHeld(rifle).Any(e => Proto(entMan, e) == "MagazineRifle");
            Assert.That(slots.GetItemOrNull(gun, "gun_magazine") != null || midSwap, "a magazine in the rifle (or going in)");
            Assert.That(BagContents(entMan, rifle).Count(e => Proto(entMan, e) == "MagazineRifle"), Is.LessThanOrEqualTo(1), "a spare used");
            Assert.That(Total(entMan, human), Is.GreaterThan(FixedPoint2.Zero), "and shoots");

            Assert.That(AI(entMan, shotgun).Reloads, Is.GreaterThanOrEqualTo(1), "shotgun reloaded from the shell box");
            Assert.That(pump.Valid, "the shotgun back in his hands");
            Assert.That(Total(entMan, human2), Is.GreaterThan(FixedPoint2.Zero), "and shoots");
        });

        // Nothing left to load at all: the knife.
        await server.WaitPost(() =>
        {
            var gun = HeldGun(entMan, rifle);
            foreach (var item in ai.EnumerateStored(rifle).Concat(server.System<SharedHandsSystem>().EnumerateHeld(rifle)).ToList())
            {
                if (entMan.HasComponent<BallisticAmmoProviderComponent>(item) && !entMan.HasComponent<GunComponent>(item))
                    entMan.DeleteEntity(item);
            }
            if (slots.TryEject(gun, "gun_magazine", null, out var mag))
                entMan.DeleteEntity(mag.Value);
            if (containers.TryGetContainer(gun, "gun_chamber", out var chamber))
                foreach (var e in chamber.ContainedEntities.ToList())
                    entMan.DeleteEntity(e);
        });
        await RunSeconds(pair, 3);
        await server.WaitAssertion(() =>
        {
            var comp = AI(entMan, rifle);
            var held = server.System<SharedHandsSystem>().EnumerateHeld(rifle).Select(e => Proto(entMan, e)).ToList();
            TestContext.Out.WriteLine($"[ops] out of ammo: mode {comp.WeaponMode}, holding {string.Join(", ", held)}");
            Assert.That(comp.WeaponMode, Is.EqualTo(ShogkkOperativeWeaponMode.Melee));
            Assert.That(held, Does.Contain("CombatKnife"));
        });

        await pair.CleanReturnAsync();
    }
}

using Content.Shared.Mind;
using Content.Shared.Chat;
using Content.Server.Chat.Systems;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Server.Gravity;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Whitelist;
using Robust.Shared.Audio.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;using Content.Shared.Mobs.Systems;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v9: airlocks / patrol / pursuit, own gear, loot whitelist, medicine, contact lines, mental map, berserk audio sync.
/// </summary>
[TestFixture]
public sealed class GilbertV9Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";
    private const string BoxPrefix = "MagazineBoxShotgun";

    private static async Task FillFloor(TestPair pair, TestMapData map, int radius)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            var tile = map.Tile.Tile;
            for (var x = -radius; x <= radius; x++)
            {
                for (var y = -radius; y <= radius; y++)
                {
                    mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
                }
            }

            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.Server.WaitRunTicks((int) Math.Ceiling(seconds * rate) + 1);
    }

    /// <summary>Blunt damage only (a punch): the test map has no air, so total damage also counts suffocation.</summary>
    private static FixedPoint2 Blunt(IEntityManager entMan, EntityUid target)
    {
        return entMan.GetComponent<DamageableComponent>(target).Damage.DamageDict.GetValueOrDefault("Blunt");
    }

    private static void Sturdy(IEntityManager entMan, EntityUid target)
    {
        var thresholds = entMan.System<MobThresholdSystem>();
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
        thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
        // No air on the test map: low pressure would bruise them (Blunt) and look like a punch.
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(target);
    }

    private static EntityUid HeldGun(IEntityManager entMan, EntityUid gilbert)
    {
        var hands = entMan.System<SharedHandsSystem>();
        return hands.EnumerateHeld(gilbert).First(e => entMan.HasComponent<BallisticAmmoProviderComponent>(e) && entMan.HasComponent<GunComponent>(e));
    }

    private static void EmptyGun(IEntityManager entMan, EntityUid gun)
    {
        var ballistic = entMan.GetComponent<BallisticAmmoProviderComponent>(gun);
        // Pump guns keep spent casings in the list, so clear the list itself and delete its shells.
        var list = (List<EntityUid>) typeof(BallisticAmmoProviderComponent)
            .GetField(nameof(BallisticAmmoProviderComponent.Entities))!.GetValue(ballistic)!;
        var shells = list.ToList();
        list.Clear();
        foreach (var shell in shells)
            entMan.DeleteEntity(shell);

        entMan.System<SharedGunSystem>().SetBallisticUnspawned((gun, ballistic), 0);
    }

    private static List<EntityUid> Boxes(IEntityManager entMan, GilbertAISystem ai, EntityUid gilbert)
    {
        return ai.EnumerateStored(gilbert)
            .Where(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID.StartsWith(BoxPrefix) == true)
            .ToList();
    }

    private static EntityUid? Slot(InventorySystem inv, EntityUid uid, string slot)
    {
        return inv.TryGetSlotEntity(uid, slot, out var e) ? e : null;
    }

    private static EntityUid? ContainerOwner(IEntityManager entMan, EntityUid item)
    {
        return entMan.System<SharedContainerSystem>().TryGetContainingContainer((item, null, null), out var c) ? c.Owner : null;
    }
    private static void Box(IEntityManager entMan, TestMapData map, int cx, int cy, string wall = "WallSolid", (int X, int Y)? door = null, string? doorProto = null)
    {
        for (var x = cx - 1; x <= cx + 1; x++)
        {
            for (var y = cy - 1; y <= cy + 1; y++)
            {
                if (x == cx && y == cy)
                    continue;
                var proto = door is { } d && d.X == x && d.Y == y ? doorProto! : wall;
                entMan.SpawnEntity(proto, map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f)));
            }
        }
    }

    private static EntityCoordinates At(TestMapData map, int x, int y)
    {
        return map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f));
    }

    private static void Quiet(IEntityManager entMan, IGameTiming timing, EntityUid gilbert)
    {
        var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
        comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
    }

    #region Airlocks / patrol (t252u)

    /// <summary>Locked in a 1-tile cell with a security airlock: his all-access ID gets him out on patrol.</summary>
    [Test]
    public async Task OpensAirlockWithIdTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, door = default;
        await server.WaitPost(() =>
        {
            for (var x = -1; x <= 1; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if ((x, y) is (0, 0) or (1, 0))
                        continue;
                    entMan.SpawnEntity("WallSolid", At(map, x, y));
                }
            }

            door = entMan.SpawnEntity("AirlockSecurityLocked", At(map, 1, 0));
            entMan.System<Content.Server.Power.EntitySystems.PowerReceiverSystem>().SetNeedsPower(door, false);
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
        });

        var escaped = false;
        for (var i = 0; i < 40 && !escaped; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                var pos = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                escaped = Math.Abs(pos.X - 0.5f) > 2f || Math.Abs(pos.Y - 0.5f) > 2f;
            });
        }

        Assert.That(escaped, "walked out through the security airlock with his ID");
        await pair.CleanReturnAsync();
    }

    /// <summary>Phase 1 patrol: he keeps walking, no standing around.</summary>
    [Test]
    public async Task NoIdlingTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 20);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
        });
        await RunSeconds(pair, 2);

        var moving = 0;
        Vector2 last = default;
        await server.WaitPost(() => last = xformSys.GetWorldPosition(gilbert));
        for (var i = 0; i < 20; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                var now = xformSys.GetWorldPosition(gilbert);
                if ((now - last).Length() > 0.3f)
                    moving++;
                last = now;
            });
        }

        TestContext.Out.WriteLine($"moving seconds: {moving}/20");
        Assert.That(moving, Is.GreaterThanOrEqualTo(14), "patrols without long pauses");
        await pair.CleanReturnAsync();
    }

    /// <summary>A spot he got stuck at is avoided for <see cref="GilbertAIComponent.BlacklistTime"/>, then forgiven.</summary>
    [Test]
    public async Task BlacklistTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.BlacklistTime = TimeSpan.FromSeconds(1);
            ai.BlacklistSpot(comp, At(map, 5, 5));
            Assert.That(ai.IsBlacklisted(comp, At(map, 5, 5)));
            Assert.That(ai.IsBlacklisted(comp, At(map, -5, -5)), Is.False);
        });
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
            Assert.That(ai.IsBlacklisted(entMan.GetComponent<GilbertAIComponent>(gilbert), At(map, 5, 5)), Is.False));
        await pair.CleanReturnAsync();
    }

    /// <summary>Lost sight of someone running east: the pursuit plan starts at the door on their heading.</summary>
    [Test]
    public async Task PursuitPlanPrefersDoorOnHeadingTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, east = default;
        await server.WaitPost(() =>
        {
            east = entMan.SpawnEntity("Airlock", At(map, 6, 0));
            entMan.SpawnEntity("Airlock", At(map, -6, 0));
            entMan.SpawnEntity("Airlock", At(map, 0, 6));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, -2, 0));
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            ai.BuildPursuitPlan((gilbert, comp), At(map, 1, 0), new Vector2(3f, 0f));
            Assert.That(comp.PursuitPlan, Is.Not.Empty);
            var first = xformSys.ToMapCoordinates(comp.PursuitPlan[0]).Position;
            var door = xformSys.GetWorldPosition(east);
            Assert.That((first - door).Length(), Is.LessThan(3f), $"first guess {first} near the east door {door}");
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Own gear, loot, medicine (t253u, t254u)

    /// <summary>Disarmed: the shotgun lands on the floor; he picks it up and has it back in his hands.</summary>
    [Test]
    public async Task RecoversDroppedShotgunTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, gun = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.That(comp.OwnGear.Values, Does.Contain(GilbertAISystem.GearHomeHand));
            gun = HeldGun(entMan, gilbert);
            Assert.That(hands.TryDrop(gilbert, gun, checkActionBlocker: false));
            xformSys.SetCoordinates(gun, At(map, 3, 1));
        });

        var back = false;
        for (var i = 0; i < 20 && !back; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() => back = hands.IsHolding(gilbert, gun));
        }

        // Picked up; the task books it as recovered a moment later, when it puts the gun back where it belongs.
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            Assert.That(back, "shotgun back in his hands");
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).Recovered, Is.GreaterThanOrEqualTo(1));
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>The v9 loot whitelist: no tools or trash, own gear first, then supplies.</summary>
    [Test]
    public async Task LootWhitelistTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var hands = entMan.System<SharedHandsSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0)));
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            var ent = (gilbert, entMan.GetComponent<GilbertAIComponent>(gilbert));
            int P(string proto) => ai.LootPriority(ent, entMan.SpawnEntity(proto, At(map, 4, 4)));
            Assert.Multiple(() =>
            {
                Assert.That(P("CableApcStack"), Is.EqualTo(0), "cable");
                Assert.That(P("Wrench"), Is.EqualTo(0), "tool");
                Assert.That(P("TrashBananaPeel"), Is.EqualTo(0), "trash");
                Assert.That(P("GrenadeShrapnel"), Is.EqualTo(50), "grenade");
                Assert.That(P("MedkitFilled"), Is.EqualTo(55), "first-aid kit with meds");
            });

            var gun = HeldGun(entMan, gilbert);
            Assert.That(ai.LootPriority(ent, gun), Is.EqualTo(0), "own gun on him: nothing to loot");
            hands.TryDrop(gilbert, gun, checkActionBlocker: false);
            Assert.That(ai.LootPriority(ent, gun), Is.EqualTo(100), "own gun on the floor: top priority");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>The reagent table is built from the reagent prototypes (healing, blood, overdose).</summary>
    [Test]
    public async Task ReagentTableTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var ai = server.EntMan.System<GilbertAISystem>();

        await server.WaitAssertion(() =>
        {
            var bic = ai.ReagentInfo("Bicaridine");
            var saline = ai.ReagentInfo("Saline");
            Assert.Multiple(() =>
            {
                Assert.That(bic, Is.Not.Null);
                Assert.That(bic!.HealPerUnit.GetValueOrDefault("Blunt"), Is.GreaterThan(0f), "bicaridine heals blunt");
                Assert.That(bic.Overdose, Is.EqualTo(FixedPoint2.New(15)), "bicaridine overdose");
                Assert.That(saline, Is.Not.Null);
                Assert.That(saline!.BloodPerUnit, Is.GreaterThan(0f), "saline restores blood");
            });
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Medicine by condition: bleeding -> something that stops it; blood loss -> something that restores blood;
    /// 12u bicaridine already in him -> the bicaridine injector would overdose and is never picked.
    /// </summary>
    [Test]
    public async Task TreatmentChoiceTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var blood = entMan.System<Content.Shared.Body.Systems.SharedBloodstreamSystem>();
        var protoMan = server.ProtoMan;
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0)));
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            var gauze = ai.GetTreatment(entMan.SpawnEntity("Gauze", At(map, 3, 3)));
            Assert.That(gauze, Is.Not.Null);
            Assert.That(gauze!.StopsBleeding, Is.GreaterThan(0f), "gauze stops bleeding");
            Assert.That(gauze.Topical, "gauze is topical");

            var injector = entMan.SpawnEntity("BruteAutoInjector", At(map, 3, 3));
            var injection = ai.GetTreatment(injector);
            Assert.That(injection, Is.Not.Null);
            Assert.That(injection!.Dose.ContainsKey("Bicaridine"));
            Assert.That(ai.WouldOverdose(gilbert, injection), Is.False, "clean blood: no overdose");

            var solution = new Content.Shared.Chemistry.Components.Solution("Bicaridine", FixedPoint2.New(12));
            Assert.That(blood.TryAddToChemicals(gilbert, solution));
            Assert.That(ai.InBloodstream(gilbert, "Bicaridine"), Is.GreaterThanOrEqualTo(FixedPoint2.New(11)));
            Assert.That(ai.WouldOverdose(gilbert, injection), "12u + 5u bicaridine is over 15u");

            damageSys.TryChangeDamage(gilbert, new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Blunt"), 60), ignoreResistances: true, canMiss: false);
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var damageable = entMan.GetComponent<DamageableComponent>(gilbert);
            Assert.That(ai.ScoreTreatment(comp, gilbert, injector, damageable), Is.EqualTo(0f), "overdose -> never");
            if (ai.TryPickHealingItem((gilbert, comp), damageable, out var picked) && ai.GetTreatment(picked) is { } t)
                Assert.That(ai.WouldOverdose(gilbert, t), Is.False, "whatever he picks is safe");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Loose meds go into his first-aid kit.</summary>
    [Test]
    public async Task MedsGoIntoMedkitTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0)));
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            var kit = ai.EnumerateStored(gilbert).FirstOrDefault(ai.IsMedkit);
            Assert.That(kit.Valid, "he carries a first-aid kit");
            // Make room: one item out of the kit.
            var inKit = ai.EnumerateStored(gilbert).Where(e => ContainerOwner(entMan, e) == kit).ToList();
            if (inKit.Count > 0)
                entMan.DeleteEntity(inKit[0]);

            var gauze = entMan.SpawnEntity("Gauze", At(map, 1, 0));
            Assert.That(ai.TryPutInMedkit(gilbert, gauze));
            Assert.That(ContainerOwner(entMan, gauze), Is.EqualTo(kit));
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Lines (t255u)

    /// <summary>«Я же говорил, не приближайся» once per contact: again only after a long time out of sight.</summary>
    [Test]
    public async Task ContactLineMemoryTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, victim = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            victim = entMan.SpawnEntity(Victim, At(map, 3, 0));
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var now = timing.CurTime;
            Assert.That(ai.ContactLine(comp, victim, now), Is.EqualTo(1), "first contact");
            ai.NoteEnemySeen(comp, victim, now);
            Assert.That(ai.ContactLine(comp, victim, now + TimeSpan.FromSeconds(3)), Is.Null, "brief loss of sight: no line");
            Assert.That(ai.ContactLine(comp, victim, now + TimeSpan.FromSeconds(20)), Is.Null, "20 s: still remembered");
            Assert.That(ai.ContactLine(comp, victim, now + TimeSpan.FromSeconds(60)), Is.EqualTo(2), "a minute later: 'I told you'");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Re-entering a fight with the same person after a few seconds out of sight doesn't repeat the line.</summary>
    [Test]
    public async Task ReEncounterNoRepeatTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, victim = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            victim = entMan.SpawnEntity(Victim, At(map, 4, 0));
            Sturdy(entMan, victim);
            Quiet(entMan, timing, gilbert);
        });
        await server.WaitRunTicks(3);

        int Lines(GilbertAIComponent c) => c.SaidCounts.GetValueOrDefault("found") + c.SaidCounts.GetValueOrDefault("warned");
        var before = 0;
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            before = Lines(comp);
            ai.EnterCombat((gilbert, comp), victim);
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() => xformSys.SetCoordinates(victim, new EntityCoordinates(map.MapUid, new Vector2(500, 500))));
        await RunSeconds(pair, 6);
        await server.WaitPost(() =>
        {
            xformSys.SetCoordinates(victim, At(map, 3, 0));
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            ai.EnterCombat((gilbert, comp), victim);
        });
        await RunSeconds(pair, 1);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.That(Lines(comp) - before, Is.LessThanOrEqualTo(1), "one contact line for one contact");
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Mental map (t256u)

    /// <summary>Scanning remembers walls, doors and floor; hidden tiles behind walls stay unknown.</summary>
    [Test]
    public async Task MentalMapScanTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, wall = default, door = default;
        await server.WaitPost(() =>
        {
            // A wall line at x = 3 with an airlock at (3, 0).
            for (var y = -5; y <= 5; y++)
            {
                if (y == 0)
                    door = entMan.SpawnEntity("Airlock", At(map, 3, y));
                else if (y == 2)
                    wall = entMan.SpawnEntity("WallSolid", At(map, 3, y));
                else
                    entMan.SpawnEntity("WallSolid", At(map, 3, y));
            }

            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            ai.ScanAround(comp, map.Grid.Owner, map.Grid.Comp, Vector2i.Zero, 8, timing.CurTime);
            Assert.Multiple(() =>
            {
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(1, 1)), Is.EqualTo(GilbertTileKind.Free));
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(3, 2)), Is.EqualTo(GilbertTileKind.Wall));
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(3, 0)), Is.EqualTo(GilbertTileKind.Door));
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(6, 2)), Is.Null, "behind the wall: unseen");
            });

            // Refresh: the wall is gone, the door is bolted.
            entMan.DeleteEntity(wall);
            var doors = entMan.System<Content.Shared.Doors.Systems.SharedDoorSystem>();
            doors.TrySetBoltDown((door, entMan.GetComponent<Content.Shared.Doors.Components.DoorBoltComponent>(door)), true, requirePower: false);
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var changed = comp.MentalMap.TilesChanged;
            ai.ScanAround(comp, map.Grid.Owner, map.Grid.Comp, Vector2i.Zero, 8, timing.CurTime);
            Assert.Multiple(() =>
            {
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(3, 2)), Is.EqualTo(GilbertTileKind.Free), "broken wall");
                Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(3, 0)), Is.EqualTo(GilbertTileKind.DoorLocked), "bolted door");
                Assert.That(comp.MentalMap.TilesChanged, Is.GreaterThanOrEqualTo(changed + 2));
            });
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Two rooms joined by one airlock: the remembered route goes through the door; with the door bolted there
    /// is no known way for him (but the berserk would smash through).
    /// </summary>
    [Test]
    public async Task MentalMapRouteTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, door = default;
        await server.WaitPost(() =>
        {
            // A wall at x = 0 from y = -12 to 12, the door at (0, 8).
            for (var y = -12; y <= 12; y++)
            {
                if (y == 8)
                    door = entMan.SpawnEntity("Airlock", At(map, 0, y));
                else
                    entMan.SpawnEntity("WallSolid", At(map, 0, y));
            }

            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, -6, -6));
            Quiet(entMan, timing, gilbert);
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            // He has walked both rooms before: scan from a few spots on each side.
            foreach (var p in new[] { new Vector2i(-6, -6), new Vector2i(-6, 4), new Vector2i(-2, 8), new Vector2i(2, 8), new Vector2i(6, 4), new Vector2i(6, -6) })
                ai.ScanAround(comp, map.Grid.Owner, map.Grid.Comp, p, 8, timing.CurTime);

            Assert.That(ai.PlanRoute(comp, At(map, -6, -6), At(map, 6, -6), false, out var waypoints, out var length));
            TestContext.Out.WriteLine($"route length {length}, waypoints {waypoints.Count}");
            Assert.That(length, Is.GreaterThan(20f), "around through the door, not through the wall");
            var nearDoor = waypoints.Any(w => (xformSys.ToMapCoordinates(w).Position - xformSys.GetWorldPosition(door)).Length() < 4f);
            Assert.That(nearDoor, "a waypoint at the door");
            Assert.That(xformSys.ToMapCoordinates(waypoints[^1]).Position, Is.EqualTo(xformSys.ToMapCoordinates(At(map, 6, -6)).Position));

            entMan.System<Content.Shared.Doors.Systems.SharedDoorSystem>()
                .TrySetBoltDown((door, entMan.GetComponent<Content.Shared.Doors.Components.DoorBoltComponent>(door)), true, requirePower: false);
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            ai.ScanAround(comp, map.Grid.Owner, map.Grid.Comp, new Vector2i(-2, 8), 8, timing.CurTime);
            Assert.That(ai.PlanRoute(comp, At(map, -6, -6), At(map, 6, -6), false, out _, out _), Is.False, "bolted: no known way");
            Assert.That(ai.PlanRoute(comp, At(map, -6, -6), At(map, 6, -6), true, out _, out _), "berserk smashes the bolted door");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>The trail: chunks he walked count as visited for the trail window, then expire.</summary>
    [Test]
    public async Task TrailExpiresTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.That(comp.TrailMemory, Is.EqualTo(TimeSpan.FromMinutes(5)), "default trail window");
            comp.TrailMemory = TimeSpan.FromSeconds(3);
            Quiet(entMan, timing, gilbert);
        });
        await RunSeconds(pair, 1.5);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.That(comp.MentalMap.Trail, Is.Not.Empty);
            Assert.That(ai.TrailNovelty(comp, map.Grid.Owner, Vector2i.Zero, timing.CurTime), Is.LessThan(0.8f), "just walked here");
            Assert.That(ai.TrailNovelty(comp, map.Grid.Owner, new Vector2i(40, 40), timing.CurTime), Is.EqualTo(1f), "never here");
            Assert.That(comp.MentalMap.Scans, Is.GreaterThan(0), "scans run on their own");
            Assert.That(comp.MentalMap.Scans, Is.LessThanOrEqualTo(3), "about once a second, not per tick");
        });

        await RunSeconds(pair, 6);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var now = timing.CurTime;
            var window = comp.TrailMemory + TimeSpan.FromSeconds(1.1);
            Assert.That(comp.MentalMap.Trail.All(t => now - t.At <= window), "old footsteps expired");
            Assert.That(comp.MentalMap.TrailLast.Values.All(t => now - t <= window), "old trail chunks expired");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Patrol over a small explored station: over 90 s he covers most of it (trail-driven), plans routes over the
    /// remembered map, and the trail is pruned to the window. Dumps the remembered map (preview).
    /// </summary>
    [Test]
    public async Task MentalMapPatrolCoverageTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 16);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            // Outer walls and a cross of inner walls with doorways: four rooms.
            for (var i = -16; i <= 16; i++)
            {
                entMan.SpawnEntity("WallSolid", At(map, i, -16));
                entMan.SpawnEntity("WallSolid", At(map, i, 16));
                if (i != -16 && i != 16)
                {
                    entMan.SpawnEntity("WallSolid", At(map, -16, i));
                    entMan.SpawnEntity("WallSolid", At(map, 16, i));
                }

                if (Math.Abs(i) == 8)
                {
                    var power = entMan.System<Content.Server.Power.EntitySystems.PowerReceiverSystem>();
                    power.SetNeedsPower(entMan.SpawnEntity("Airlock", At(map, i, 0)), false);
                    power.SetNeedsPower(entMan.SpawnEntity("Airlock", At(map, 0, i)), false);
                }
                else if (i != 0 && Math.Abs(i) < 16)
                {
                    entMan.SpawnEntity("WallSolid", At(map, i, 0));
                    entMan.SpawnEntity("WallSolid", At(map, 0, i));
                }
            }

            entMan.SpawnEntity("WallSolid", At(map, 0, 0));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, -8, -8));
            Quiet(entMan, timing, gilbert);
        });

        var rooms = new HashSet<int>();
        var xformSys = entMan.System<SharedTransformSystem>();
        for (var i = 0; i < 90; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                var p = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                rooms.Add((p.X > 0.5f ? 1 : 0) + (p.Y > 0.5f ? 2 : 0));
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var memory = comp.MentalMap.Grids[map.Grid.Owner];
            TestContext.Out.WriteLine($"rooms visited {rooms.Count}, tiles known {memory.Tiles.Count}, routes {comp.RoutesPlanned}, scans {comp.MentalMap.Scans}, trail {comp.MentalMap.Trail.Count}");
            // Preview dump: one row per y, '.' floor, '#' wall, 'D' door, 'X' bolted, ' ' unknown, '*' trail.
            var trail = comp.MentalMap.Trail.Select(t => t.Tile).ToHashSet();
            var dump = new List<string>();
            for (var y = 17; y >= -17; y--)
            {
                var row = new System.Text.StringBuilder("MAP|");
                for (var x = -17; x <= 17; x++)
                {
                    var v = new Vector2i(x, y);
                    row.Append(!memory.Tiles.TryGetValue(v, out var t) ? ' '
                        : trail.Contains(v) ? '*'
                        : t.Kind switch
                        {
                            GilbertTileKind.Free => '.',
                            GilbertTileKind.Wall => '#',
                            GilbertTileKind.Door => 'D',
                            GilbertTileKind.DoorLocked => 'X',
                            GilbertTileKind.Climbable => 'T',
                            _ => '~',
                        });
                }

                TestContext.Out.WriteLine(row.ToString());
                dump.Add(row.ToString());
            }

            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gilbert-v9-map.txt"), dump);

            Assert.That(rooms.Count, Is.GreaterThanOrEqualTo(3), "patrol spreads over the rooms");
            Assert.That(memory.Tiles.Count, Is.GreaterThan(500), "remembers most of the station");
            Assert.That(comp.MentalMap.Scans, Is.InRange(60, 120), "scans throttled to ~1/s");
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Berserk transition / death (t256u)

    [Test]
    public async Task TransitionMatchesClipTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0)));
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.TransitionDuration.TotalSeconds, Is.EqualTo(6.9).Within(0.01), "the transition lasts as long as the 6.9 s clip");
            Assert.That(comp.BurstAt.TotalSeconds, Is.EqualTo(4.75).Within(0.01), "burst on the roar");
            Assert.That(comp.BurstAt, Is.LessThan(comp.TransitionDuration));
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Dying: one «Прости... Прости!» instead of zombie wails.</summary>
    [Test]
    public async Task DeathLineTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;

        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));
        await server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartDying((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 0.2);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.Dying));
            Assert.That(comp.DeathLineSaid, Is.False, "not before the delay");
        });
        await RunSeconds(pair, 0.6);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.DeathLineSaid, "said «Прости... Прости!» while shaking");
            Assert.That(comp.DeathLineSound.ToString(), Does.Contain("gilbert_death_sorry_1"));
        });
        await pair.CleanReturnAsync();
    }

    #region Airlocks in pursuit, prying, NoSlip, tables (t257u–t260u)

    /// <summary>A 1-tile cell around (0, 0) with an airlock at (1, 0), powered without a grid.</summary>
    private static EntityUid Cell(IEntityManager entMan, TestMapData map, string doorProto)
    {
        for (var x = -1; x <= 1; x++)
        {
            for (var y = -1; y <= 1; y++)
            {
                if ((x, y) is (0, 0) or (1, 0))
                    continue;
                entMan.SpawnEntity("WallSolid", At(map, x, y));
            }
        }

        var door = entMan.SpawnEntity(doorProto, At(map, 1, 0));
        entMan.System<Content.Server.Power.EntitySystems.PowerReceiverSystem>().SetNeedsPower(door, false);
        return door;
    }

    private static void DropId(IEntityManager entMan, EntityUid gilbert)
    {
        if (entMan.System<InventorySystem>().TryGetSlotEntity(gilbert, "id", out var id))
            entMan.DeleteEntity(id.Value);
    }

    private static Content.Shared.Doors.Components.DoorState DoorState(IEntityManager entMan, EntityUid door)
    {
        return entMan.GetComponent<Content.Shared.Doors.Components.DoorComponent>(door).State;
    }

    private static bool IsOpen(IEntityManager entMan, EntityUid door)
    {
        return DoorState(entMan, door) is Content.Shared.Doors.Components.DoorState.Open or Content.Shared.Doors.Components.DoorState.Opening;
    }

    private static void Bolt(IEntityManager entMan, EntityUid door)
    {
        entMan.System<Content.Shared.Doors.Systems.SharedDoorSystem>()
            .TrySetBoltDown((door, entMan.GetComponent<Content.Shared.Doors.Components.DoorBoltComponent>(door)), true, requirePower: false);
    }

    /// <summary>t257u: he hears someone behind a closed airlock and goes through it (ID) to check.</summary>
    [Test]
    public async Task HeardBehindAirlockTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, speaker = default, door = default;
        await server.WaitPost(() =>
        {
            door = Cell(entMan, map, "AirlockSecurityLocked");
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            speaker = entMan.SpawnEntity(Victim, At(map, 6, 0));
            Sturdy(entMan, speaker);
        });
        await RunSeconds(pair, 1);
        await server.WaitAssertion(() =>
        {
            entMan.System<ChatSystem>().TrySendInGameICMessage(speaker, "Эй, есть кто?", InGameICChatType.Speak, ChatTransmitRange.Normal);
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).State, Is.EqualTo(GilbertAIState.Investigate), "heard it");
        });

        var through = false;
        var osc = 0;
        for (var i = 0; i < 60 && !through; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() =>
            {
                var pos = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                through = pos.X > 2.2f;
                osc = entMan.GetComponent<GilbertAIComponent>(gilbert).Oscillations;
            });
        }

        TestContext.Out.WriteLine($"[v9] heard behind airlock: through={through} oscillations={osc}");
        Assert.That(through, "went through the closed airlock towards the voice within 15 s");
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// t257u: in combat he sees the target through a glass airlock; his shots can't get through the glass, so he
    /// opens the airlock and closes in instead of standing there.
    /// </summary>
    [Test]
    public async Task SeenThroughGlassAirlockTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, target = default, door = default;
        await server.WaitPost(() =>
        {
            door = Cell(entMan, map, "AirlockSecurityGlassLocked");
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            target = entMan.SpawnEntity(Victim, At(map, 5, 0));
            Sturdy(entMan, target);
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() => ai.ForceCombat(gilbert, target));

        var through = false;
        var osc = 0;
        for (var i = 0; i < 60 && !through; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() =>
            {
                var pos = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                through = pos.X > 2.2f || IsOpen(entMan, door) && pos.X > 1.2f;
                osc = entMan.GetComponent<GilbertAIComponent>(gilbert).Oscillations;
            });
        }

        TestContext.Out.WriteLine($"[v9] seen through glass airlock: through={through} oscillations={osc}");
        Assert.That(through, "opened the glass airlock and went for the target within 15 s");
        await pair.CleanReturnAsync();
    }

    /// <summary>t258u: phase 1 without a fitting ID: pries the powered airlock with bare hands, about 5 s.</summary>
    [Test]
    public async Task HandPryTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, door = default;
        await server.WaitPost(() =>
        {
            door = Cell(entMan, map, "AirlockSecurityLocked");
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            DropId(entMan, gilbert);
            Assert.That(entMan.GetComponent<Content.Shared.Prying.Components.PryingComponent>(gilbert).Force, Is.False,
                "phase 1: bolts still stop him");
        });

        TimeSpan? started = null, opened = null;
        for (var i = 0; i < 300 && opened == null; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() =>
            {
                if (started == null && entMan.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(gilbert, out var st)
                    && st.DoAfterId != null)
                    started = timing.CurTime;
                if (opened == null && IsOpen(entMan, door))
                    opened = timing.CurTime;
            });
        }

        TestContext.Out.WriteLine($"[v9] hand pry: started={started} opened={opened} took={(opened - started)?.TotalSeconds:0.00}s");
        Assert.Multiple(() =>
        {
            Assert.That(started, Is.Not.Null, "started prying");
            Assert.That(opened, Is.Not.Null, "pried it open");
        });
        var took = (opened!.Value - started!.Value).TotalSeconds;
        Assert.That(took, Is.InRange(4.4, 6.5), "about 5 s with bare hands");
        await pair.CleanReturnAsync();
    }

    /// <summary>t258u: phase 1: a bolted airlock can't be pried.</summary>
    [Test]
    public async Task BoltsStopHandPryTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, door = default;
        await server.WaitPost(() =>
        {
            door = Cell(entMan, map, "AirlockSecurityLocked");
            Bolt(entMan, door);
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            DropId(entMan, gilbert);
        });

        var everOpen = false;
        for (var i = 0; i < 40; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() => everOpen |= !entMan.Deleted(door) && IsOpen(entMan, door));
        }

        Assert.That(everOpen, Is.False, "bolts hold against bare hands");
        await pair.CleanReturnAsync();
    }

    /// <summary>t258u: berserk: rips a bolted airlock open at once, like a crowbar.</summary>
    [Test]
    public async Task BerserkRipsBoltedDoorTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid door = default, victim = default;
        await server.WaitPost(() =>
        {
            door = Cell(entMan, map, "AirlockSecurityLocked");
            Bolt(entMan, door);
            victim = entMan.SpawnEntity(Victim, At(map, 6, 0));
            Sturdy(entMan, victim);
        });
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<Content.Shared.Prying.Components.PryingComponent>(gilbert).Force, "berserk: ignores bolts"));
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(victim, "помогите!",
            InGameICChatType.Speak, ChatTransmitRange.Normal));

        var opened = false;
        var out_ = false;
        for (var i = 0; i < 60 && !out_; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() =>
            {
                opened |= entMan.Deleted(door) || IsOpen(entMan, door);
                var pos = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                out_ = pos.X > 2.2f;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v9] berserk bolted door: ripped={comp.BerserkDoorsRipped} opened={opened} out={out_} door={(entMan.Deleted(door) ? "destroyed" : DoorState(entMan, door).ToString())}");
            Assert.That(opened, "the bolted door gave way");
            Assert.That(out_, "and he is through within 15 s");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>t258u: soap and spills don't take him down.</summary>
    [Test]
    public async Task NoSlipTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0)));
        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<Content.Shared.Slippery.NoSlipComponent>(gilbert));
            var ev = new Content.Shared.Slippery.SlipAttemptEvent(null);
            entMan.EventBus.RaiseLocalEvent(gilbert, ev);
            Assert.That(ev.NoSlip, "slip attempts are ignored");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// t259u: tables are passable in his mental map (at an extra cost): a straight route over a row of tables
    /// beats a long detour through the door; and in a dead-end corridor he climbs over a table to get to a voice.
    /// </summary>
    [Test]
    public async Task TableCrossingTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var timing = server.ResolveDependency<IGameTiming>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid planner = default;
        await server.WaitPost(() =>
        {
            // A wall at x = 0 from y = -12 to 12; tables at y = -1..1; the door far away at (0, 10).
            for (var y = -12; y <= 12; y++)
            {
                if (y == 10)
                    entMan.SpawnEntity("Airlock", At(map, 0, y));
                else if (y is >= -1 and <= 1)
                    entMan.SpawnEntity("Table", At(map, 0, y));
                else
                    entMan.SpawnEntity("WallSolid", At(map, 0, y));
            }

            planner = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, -6, -10));
            Quiet(entMan, timing, planner);
        });
        await server.WaitRunTicks(3);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(planner);
            foreach (var p in new[] { new Vector2i(-6, 0), new Vector2i(-6, 10), new Vector2i(-2, 10), new Vector2i(2, 10), new Vector2i(6, 0), new Vector2i(6, 10) })
                ai.ScanAround(comp, map.Grid.Owner, map.Grid.Comp, p, 8, timing.CurTime);
            Assert.That(ai.KnownTile(comp, map.Grid.Owner, new Vector2i(0, 0)), Is.EqualTo(GilbertTileKind.Climbable));
            Assert.That(ai.PlanRoute(comp, At(map, -6, 0), At(map, 6, 0), false, out _, out var length));
            TestContext.Out.WriteLine($"[v9] route over the tables: {length:0.0}");
            Assert.That(length, Is.LessThan(24f), "over the tables, not round through the far door");
            entMan.DeleteEntity(planner);
        });

        // Dead-end corridor: walls above and below, a table across it at x = 4.
        EntityUid gilbert = default, speaker = default;
        await server.WaitPost(() =>
        {
            foreach (var e in entMan.GetEntities().ToList())
            {
                if (entMan.HasComponent<Content.Shared.Climbing.Components.ClimbableComponent>(e)
                    || entMan.TryGetComponent<MetaDataComponent>(e, out var md) && md.EntityPrototype?.ID is "WallSolid" or "Airlock")
                    entMan.DeleteEntity(e);
            }

            for (var x = -2; x <= 10; x++)
            {
                entMan.SpawnEntity("WallSolid", At(map, x, 1));
                entMan.SpawnEntity("WallSolid", At(map, x, -1));
            }

            entMan.SpawnEntity("WallSolid", At(map, -2, 0));
            entMan.SpawnEntity("WallSolid", At(map, 10, 0));
            entMan.SpawnEntity("Table", At(map, 4, 0));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            speaker = entMan.SpawnEntity(Victim, At(map, 8, 0));
            Sturdy(entMan, speaker);
        });
        await RunSeconds(pair, 1);
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(speaker, "Эй, есть кто?",
            InGameICChatType.Speak, ChatTransmitRange.Normal));

        var over = false;
        for (var i = 0; i < 60 && !over; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() =>
            {
                var pos = xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(map.Grid.Owner);
                over = pos.X > 5.2f;
            });
        }

        TestContext.Out.WriteLine($"[v9] climbed over the corridor table: {over}");
        Assert.That(over, "climbed over the table within 15 s");
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// t262u: cuffed-loiterer timings: punch after 15 s near him, fight after 10 s more; "near" is up to 8 tiles
    /// but past 3 only while he sees them, and never through a wall.
    /// </summary>
    [Test]
    public async Task LingerRangeInViewTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, open = default, walled = default, far = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, At(map, 0, 0));
            Quiet(entMan, timing, gilbert);
            open = entMan.SpawnEntity(Victim, At(map, 6, 0));
            for (var x = -1; x <= 1; x++)
                entMan.SpawnEntity("WallSolid", At(map, x, 3));
            walled = entMan.SpawnEntity(Victim, At(map, 0, 6));
            far = entMan.SpawnEntity(Victim, At(map, -9, 0));
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var ent = new Entity<GilbertAIComponent>(gilbert, comp);
            Assert.Multiple(() =>
            {
                Assert.That(comp.LingerPunchTime, Is.EqualTo(TimeSpan.FromSeconds(15)));
                Assert.That(comp.ExpelWatchTime, Is.EqualTo(TimeSpan.FromSeconds(10)));
                Assert.That(comp.LingerRange, Is.EqualTo(8f));
            });

            void Face(EntityUid other)
            {
                xformSys.SetCoordinates(gilbert, At(map, 0, 0));
                var delta = xformSys.GetWorldPosition(other) - xformSys.GetWorldPosition(gilbert);
                xformSys.SetWorldRotation(gilbert, delta.ToWorldAngle());
            }

            Face(open);
            Assert.That(ai.IsNearby(ent, open), "6 tiles away in plain view: near");
            Face(walled);
            Assert.That(ai.IsNearby(ent, walled), Is.False, "6 tiles away behind a wall: not near");
            Face(far); // west: the player 6 tiles east is straight behind his back (vision cone is 180°)
            Assert.That(ai.IsNearby(ent, open), Is.False, "6 tiles away behind his back: not near");
            Assert.That(ai.IsNearby(ent, far), Is.False, "9 tiles: too far");
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    private static async Task<EntityUid> SpawnRaging(TestPair pair, TestMapData map, Vector2 at)
    {
        var entMan = pair.Server.EntMan;
        var gilbert = EntityUid.Invalid;
        await pair.Server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(at));
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            comp.TransitionDuration = TimeSpan.FromSeconds(1.0);
            comp.BurstAt = TimeSpan.FromSeconds(0.6);
            comp.RageDuration = TimeSpan.FromSeconds(300);
        });
        await pair.Server.WaitRunTicks(3);
        await pair.Server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.3);
        return gilbert;
    }

    #endregion
}

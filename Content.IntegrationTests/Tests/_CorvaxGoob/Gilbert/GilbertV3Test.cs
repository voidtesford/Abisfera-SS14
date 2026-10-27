using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Chat.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Gravity;
using Content.Shared.Chat;
using Content.Shared.Cuffs;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Explosion.Components;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.NPC.Components;
using Content.Shared.NPC.Prototypes;
using Content.Shared.NPC.Systems;
using Content.Shared.Standing;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v3 behaviour: stamina, see-through windows, the search procedure (knockdown, backpack, cooldown),
/// expelling lingering cuffed people, ignoring still ones on the floor, threat-ranked targeting and facing.
/// </summary>
[TestFixture]
public sealed class GilbertV3Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";

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

    private static EntityUid SpawnCuffed(IEntityManager entMan, EntityCoordinates coords)
    {
        var victim = entMan.SpawnEntity(Victim, coords);
        var cuffs = entMan.SpawnEntity("Handcuffs", coords);
        Assert.That(entMan.System<SharedCuffableSystem>().TryAddNewCuffs(victim, victim, cuffs), "test setup: cuffed");
        return victim;
    }

    private static float AngleOff(IEntityManager entMan, EntityUid from, EntityUid to)
    {
        var xformSys = entMan.System<SharedTransformSystem>();
        var delta = xformSys.GetWorldPosition(to) - xformSys.GetWorldPosition(from);
        var want = delta.ToWorldAngle();
        var have = xformSys.GetWorldRotation(from);
        return (float) Math.Abs(Angle.ShortestDistance(have, want).Theta);
    }

    /// <summary>300 stamina; windows don't block his sight, walls do.</summary>
    [Test]
    public async Task StaminaAndWindowSightTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        await server.WaitAssertion(() =>
        {
            var gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            var window = entMan.SpawnEntity("ReinforcedWindow", map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
            var wall = entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(2.5f, -1.5f)));
            // The diagonal ray to (2.5, -3.5) crosses the (1.5, -1.5) tile, so wall that one too.
            entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, -1.5f)));
            var behindWindow = SpawnCuffed(entMan, map.GridCoords.Offset(new Vector2(0.5f, -3.5f)));
            var behindWall = SpawnCuffed(entMan, map.GridCoords.Offset(new Vector2(2.5f, -3.5f)));

            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<StaminaComponent>(gilbert).CritThreshold, Is.EqualTo(300f));
                Assert.That(ai.IsSeeThrough(window), "a window is see-through");
                Assert.That(ai.IsSeeThrough(wall), Is.False, "a wall is not");
                Assert.That(ai.CanSee(gilbert, behindWindow), "sees through the window");
                Assert.That(ai.CanSee(gilbert, behindWall), Is.False, "does not see through the wall");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A cuffed person with a grenade in their backpack is walked up to, knocked down, searched (do-after),
    /// the backpack is taken off and the grenade ends up in his storage; the person is on a 2-minute cooldown.
    /// </summary>
    [Test]
    public async Task FriskBackpackTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var standing = entMan.System<StandingStateSystem>();

        EntityUid gilbert = default, victim = default, grenade = default, bag = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            var at = map.GridCoords.Offset(new Vector2(0.5f, -2.5f));
            victim = SpawnCuffed(entMan, at);
            bag = entMan.SpawnEntity("ClothingBackpack", at);
            Assert.That(entMan.System<InventorySystem>().TryEquip(victim, bag, "back", silent: true, force: true));
            grenade = entMan.SpawnEntity("ExGrenade", at);
            Assert.That(entMan.System<Content.Shared.Storage.EntitySystems.SharedStorageSystem>().Insert(bag, grenade, out _, playSound: false));
        });

        var knocked = false;
        var taken = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 160 && !taken; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                knocked |= standing.IsDown(victim);
                taken = ai.EnumerateStored(gilbert).Contains(grenade);
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(knocked, "knocks the cuffed person down before searching");
                Assert.That(taken, "takes the grenade out of their backpack");
                Assert.That(ai.IsFriskOnCooldown(comp, victim, server.Timing.CurTime), "the searched person is on cooldown");
                Assert.That(entMan.System<InventorySystem>().TryGetSlotEntity(victim, "back", out _), Is.False,
                    "the backpack was taken off");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A cuffed person who keeps standing next to him gets punched and told to get out; still there
    /// afterwards, they are fought. (Timers shortened for the test.)
    /// </summary>
    [Test]
    public async Task LingerPunchThenCombatTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, victim = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.LingerPunchTime = TimeSpan.FromSeconds(2);
            comp.ExpelWatchTime = TimeSpan.FromSeconds(3);
            victim = SpawnCuffed(entMan, map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
        });

        var punched = false;
        var facingWhileWaiting = true;
        var combat = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 200 && !combat; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                if (comp.State == GilbertAIState.Expel && comp.ExpelPunched)
                {
                    punched = true;
                    if (AngleOff(entMan, gilbert, victim) > 0.35f)
                        facingWhileWaiting = false;
                }

                combat = comp.State == GilbertAIState.Combat && comp.Target == victim;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(punched, "punches the lingering cuffed person");
                Assert.That(facingWhileWaiting, "faces them while waiting");
                Assert.That(combat, "fights them when they stay");
                Assert.That(comp.ForcedHostile, Does.Contain(victim));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Someone cuffed lying still on the floor is ignored; once they speak the clock runs.</summary>
    [Test]
    public async Task DormantIgnoredUntilSpeaksTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var chat = entMan.System<ChatSystem>();

        EntityUid gilbert = default, victim = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.LingerPunchTime = TimeSpan.FromSeconds(2);
            victim = SpawnCuffed(entMan, map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
            entMan.System<SharedStunSystem>().TryKnockdown(victim, TimeSpan.FromSeconds(60), true);
        });

        var expelled = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 32; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                expelled |= entMan.GetComponent<GilbertAIComponent>(gilbert).State == GilbertAIState.Expel;
            });
        }

        Assert.That(expelled, Is.False, "a still person on the floor is left alone");

        // He may have wandered off meanwhile: bring him back, and the victim keeps talking (a still person
        // stays "active" for a few seconds after each line).
        await server.WaitPost(() =>
            xformSys.SetCoordinates(gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f))));

        for (var i = 0; i < 40 && !expelled; i++)
        {
            if (i % 6 == 0)
            {
                await server.WaitPost(() =>
                    chat.TrySendInGameICMessage(victim, "help", InGameICChatType.Speak, ChatTransmitRange.Normal));
            }

            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                expelled |= entMan.GetComponent<GilbertAIComponent>(gilbert).State == GilbertAIState.Expel;
            });
        }

        Assert.That(expelled, "speaking makes them count as lingering again");
        await pair.CleanReturnAsync();
    }

    /// <summary>An armed enemy further away is a bigger threat than an unarmed one close by; he faces his target.</summary>
    [Test]
    public async Task ArmedThreatPreferredAndFacedTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, near = default, armed = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            near = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-1.5f, -2.5f)));
            armed = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(2.5f, -5.5f)));
            var gun = entMan.SpawnEntity("WeaponPistolViper", map.GridCoords.Offset(new Vector2(2.5f, -5.5f)));
            Assert.That(entMan.System<SharedHandsSystem>().TryPickupAnyHand(armed, gun));
        });

        await server.WaitAssertion(() =>
        {
            var ent = new Entity<GilbertAIComponent>(gilbert, entMan.GetComponent<GilbertAIComponent>(gilbert));
            Assert.That(ai.ThreatScore(ent, armed, server.Timing.CurTime), Is.LessThan(ai.ThreatScore(ent, near, server.Timing.CurTime)));
        });

        var picked = false;
        var faced = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 12 && !(picked && faced); i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                picked |= comp.State == GilbertAIState.Combat && comp.Target == armed;
                if (comp.Target == armed)
                    faced |= AngleOff(entMan, gilbert, armed) < 0.35f;
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(picked, "goes for the armed enemy first");
            Assert.That(faced, "faces the target");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Hostility is mutual: every faction Gilbert fights also fights him, so e.g. a Syndicate footsoldier NPC
    /// picks him up as a hostile.
    /// </summary>
    [Test]
    public async Task FactionsHostileBackTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factions = entMan.System<NpcFactionSystem>();

        await server.WaitAssertion(() =>
        {
            var gilbertFaction = protoMan.Index<NpcFactionPrototype>("Gilbert");
            Assert.Multiple(() =>
            {
                foreach (var other in gilbertFaction.Hostile)
                {
                    Assert.That(protoMan.Index(other).Hostile, Does.Contain(new ProtoId<NpcFactionPrototype>("Gilbert")),
                        $"{other} is hostile to Gilbert");
                }

                // Station fauna: neither side is hostile.
                foreach (var pet in new[] { "PetsNT", "Passive", "SimpleNeutral", "Mouse" })
                {
                    Assert.That(protoMan.Index<NpcFactionPrototype>(pet).Hostile,
                        Does.Not.Contain(new ProtoId<NpcFactionPrototype>("Gilbert")), $"{pet} is not hostile to Gilbert");
                    Assert.That(gilbertFaction.Hostile, Does.Not.Contain(new ProtoId<NpcFactionPrototype>(pet)),
                        $"Gilbert is not hostile to {pet}");
                }
            });

            var gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            var syndie = entMan.SpawnEntity("MobSyndicateFootsoldier", map.GridCoords.Offset(new Vector2(3.5f, 0.5f)));
            Assert.That(factions.IsMember((syndie, null), "Syndicate"), "test setup: a Syndicate NPC");
            Assert.That(factions.GetNearbyHostiles((syndie, null, null), 10f), Does.Contain(gilbert),
                "a Syndicate NPC sees Gilbert as hostile");

            // Station pets (Ian, Runtime, Shiva) and other passive fauna: no hostility either way.
            foreach (var proto in new[] { "MobCorgiIan", "MobCatRuntime", "MobSpiderShiva", "MobMouse" })
            {
                var pet = entMan.SpawnEntity(proto, map.GridCoords.Offset(new Vector2(-2.5f, 0.5f)));
                Assert.That(factions.IsMember((pet, null), "PetsNT") || factions.IsMember((pet, null), "Passive")
                    || factions.IsMember((pet, null), "Mouse") || factions.IsMember((pet, null), "SimpleNeutral"),
                    $"test setup: {proto} is station fauna");
                Assert.That(factions.GetNearbyHostiles((pet, null, null), 10f), Does.Not.Contain(gilbert),
                    $"{proto} does not see Gilbert as hostile");
                Assert.That(factions.GetNearbyHostiles((gilbert, null, null), 10f), Does.Not.Contain(pet),
                    $"Gilbert does not see {proto} as hostile");
            }
        });

        await pair.CleanReturnAsync();
    }

    private static async Task<(bool Fled, float LastDist, bool Detonated)> WatchCharge(TestPair pair, EntityUid gilbert,
        EntityUid charge, float seconds)
    {
        var server = pair.Server;
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var tickRate = (int) server.Timing.TickRate;
        var fled = false;
        var lastDist = 0f;
        var detonated = false;
        for (var i = 0; i < seconds * 4 && !detonated; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                if (entMan.Deleted(charge) || entMan.IsQueuedForDeletion(charge))
                {
                    detonated = true;
                    return;
                }

                fled |= entMan.GetComponent<GilbertAIComponent>(gilbert).FleeingDanger;
                lastDist = (xformSys.GetWorldPosition(gilbert) - xformSys.GetWorldPosition(charge)).Length();
            });
        }

        return (fled, lastDist, detonated);
    }

    /// <summary>He plants C4 on a wall, sets a timer he can make, and is outside the blast when it goes off.</summary>
    [Test]
    public async Task PlantedC4RetreatTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();

        EntityUid gilbert = default, c4 = default;
        var blast = 0f;
        await server.WaitAssertion(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            var wall = entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            // A wall segment to hide behind.
            for (var y = -3; y <= -1; y++)
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(-3.5f, y + 0.5f)));
            c4 = entMan.SpawnEntity("C4", map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            Assert.That(entMan.System<InventorySystem>().TryEquip(gilbert, c4, "pocket2", silent: true, force: true),
                "test setup: C4 in his pocket");
            blast = ai.GetBlastRadius(c4);
            ai.BeginPlantCharge(gilbert, c4, wall);
        });

        // Wait for it to get stuck and armed.
        var tickRate = (int) server.Timing.TickRate;
        var armed = false;
        for (var i = 0; i < 40 && !armed; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() => armed = entMan.HasComponent<ActiveTimerTriggerComponent>(c4));
        }

        Assert.That(armed, "C4 got planted and armed");

        var (fled, lastDist, detonated) = await WatchCharge(pair, gilbert, c4, 40f);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(detonated, "the charge went off");
                Assert.That(fled, "he ran from his own charge");
                Assert.That(lastDist, Is.GreaterThan(blast), "outside the blast radius at detonation");
                Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage.Float(), Is.EqualTo(0f),
                    "unhurt by his own C4");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Someone's primed grenade at his feet: he runs clear of it before it blows; and won't throw at point blank.</summary>
    [Test]
    public async Task PrimedGrenadeFleeTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();

        EntityUid gilbert = default, grenade = default;
        var blast = 0f;
        await server.WaitAssertion(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            var target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
            grenade = entMan.SpawnEntity("ExGrenade", map.GridCoords.Offset(new Vector2(1.0f, 0.5f)));
            blast = ai.GetBlastRadius(grenade);
            Assert.That(ai.GrenadeTooClose((gilbert, entMan.GetComponent<GilbertAIComponent>(gilbert)), grenade, target),
                "a target 2 tiles away is too close to throw at");
            Assert.That(blast, Is.GreaterThan(2f), "frag radius sanity");
            entMan.System<TriggerSystem>().HandleTimerTrigger(grenade, null, 4f, 1f, null, null);
        });

        var (fled, lastDist, detonated) = await WatchCharge(pair, gilbert, grenade, 10f);
        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(detonated, "the grenade went off");
                Assert.That(fled, "he noticed it and ran");
                Assert.That(lastDist, Is.GreaterThan(blast), "outside the blast radius at detonation");
                Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage.Float(), Is.EqualTo(0f),
                    "unhurt");
            });
        });

        await pair.CleanReturnAsync();
    }
}

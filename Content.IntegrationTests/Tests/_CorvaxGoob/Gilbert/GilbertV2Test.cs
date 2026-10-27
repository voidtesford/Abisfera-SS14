using System.Linq;
using System.Numerics;
using Content.Goobstation.Shared.Sprinting;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Chat.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Gravity;
using Content.Shared.Chat;
using Content.Shared.Cuffs.Components;
using Content.Shared.Gravity;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v2 behaviour: hearing, 360° adjacent awareness, loadout counts, heal consumption, arrest escalation, scavenging.
/// </summary>
[TestFixture]
public sealed class GilbertV2Test
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

    /// <summary>Spawns a mob in a closed 3x3 wall cell (centred on a tile) so Gilbert can hear but never see it.</summary>
    private static EntityUid SpawnWalledIn(IEntityManager entMan, TestMapData map, string proto, int tileX, int tileY)
    {
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(tileX + dx + 0.5f, tileY + dy + 0.5f)));
            }
        }

        return entMan.SpawnEntity(proto, map.GridCoords.Offset(new Vector2(tileX + 0.5f, tileY + 0.5f)));
    }

    private static int CountStored(IEntityManager entMan, GilbertAISystem ai, EntityUid gilbert, Func<EntityUid, bool> pred)
    {
        return ai.EnumerateStored(gilbert).Count(pred);
    }

    /// <summary>Speech is heard within 15 tiles, whispers never (v5); hearing turns him and starts an investigation.</summary>
    [Test]
    public async Task HearingSpeechAndWhisperTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.ResolveDependency<IEntityManager>();
        var chat = entMan.System<ChatSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, speaker = default, farSpeaker = default, whisperer = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero); // facing south
            speaker = SpawnWalledIn(entMan, map, Victim, 0, 10);       // behind him, 10 tiles
            farSpeaker = SpawnWalledIn(entMan, map, Victim, 0, -20);   // 20 tiles: out of range
            whisperer = SpawnWalledIn(entMan, map, Victim, 6, 0);      // 6 tiles: too far for a whisper
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            xformSys.SetWorldRotation(gilbert, Angle.Zero);

            var before = comp.HeardCount;
            chat.TrySendInGameICMessage(farSpeaker, "Эй, есть кто?", InGameICChatType.Speak, ChatTransmitRange.Normal);
            Assert.That(comp.HeardCount, Is.EqualTo(before), "speech beyond 15 tiles is not heard");

            chat.TrySendInGameICMessage(whisperer, "тихо...", InGameICChatType.Whisper, ChatTransmitRange.Normal);
            Assert.That(comp.HeardCount, Is.EqualTo(before), "a whisper 6 tiles away is not heard");

            chat.TrySendInGameICMessage(speaker, "Эй, есть кто?", InGameICChatType.Speak, ChatTransmitRange.Normal);
            Assert.Multiple(() =>
            {
                Assert.That(comp.HeardCount, Is.EqualTo(before + 1), "speech 10 tiles away is heard");
                Assert.That(comp.LastNoise, Is.EqualTo(GilbertNoise.Speech));
                Assert.That(comp.State, Is.EqualTo(GilbertAIState.Investigate), "goes to check the sound");
                var forward = xformSys.GetWorldRotation(gilbert).ToWorldVec();
                Assert.That(forward.Y, Is.GreaterThan(0.7f), "turns towards the sound behind him");
            });
        });

        // v5: even a whisper right next to him is ignored (see GilbertV5Test.WhispersIgnoredTest).
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var gpos = xformSys.GetWorldPosition(gilbert);
            var close = entMan.SpawnEntity(Victim, new MapCoordinates(gpos + new Vector2(1.2f, 0f), map.MapId));
            var before = comp.HeardCount;
            chat.TrySendInGameICMessage(close, "тихо...", InGameICChatType.Whisper, ChatTransmitRange.Normal);
            Assert.That(comp.HeardCount, Is.EqualTo(before), "a whisper is never heard");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Screams, gunshots, explosions and sprinting within 15 tiles are heard and investigated.</summary>
    [Test]
    public async Task HearingLoudNoisesTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.ResolveDependency<IEntityManager>();
        var chat = entMan.System<ChatSystem>();
        var explosions = entMan.System<ExplosionSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, screamer = default, farScreamer = default, shooter = default, runner = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            screamer = SpawnWalledIn(entMan, map, Victim, 0, 10);
            farScreamer = SpawnWalledIn(entMan, map, Victim, 0, -20);
            shooter = SpawnWalledIn(entMan, map, Victim, 10, 0);
            runner = SpawnWalledIn(entMan, map, Victim, -10, 0);
        });
        await server.WaitRunTicks(5);

        // Scream.
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var before = comp.HeardCount;
            chat.TryEmoteWithoutChat(farScreamer, "Scream", ignoreActionBlocker: true);
            Assert.That(comp.HeardCount, Is.EqualTo(before), "a scream 20 tiles away is not heard");

            chat.TryEmoteWithoutChat(screamer, "Scream", ignoreActionBlocker: true);
            Assert.Multiple(() =>
            {
                Assert.That(comp.HeardCount, Is.EqualTo(before + 1), "a scream 10 tiles away is heard");
                Assert.That(comp.LastNoise, Is.EqualTo(GilbertNoise.Scream));
                Assert.That(comp.State, Is.EqualTo(GilbertAIState.Investigate));
            });
        });

        // Gunshot (raised the way the gun system raises it after firing).
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var gun = entMan.SpawnEntity("WeaponShotgunCombatShogkk", entMan.GetComponent<TransformComponent>(shooter).Coordinates);
            var before = comp.HeardCount;
            var ev = new GunShotEvent(shooter, new());
            entMan.EventBus.RaiseLocalEvent(gun, ref ev);
            Assert.Multiple(() =>
            {
                Assert.That(comp.HeardCount, Is.EqualTo(before + 1), "a gunshot 10 tiles away is heard");
                Assert.That(comp.LastNoise, Is.EqualTo(GilbertNoise.Gunshot));
                Assert.That(comp.NoiseSource, Is.EqualTo(shooter));
            });
        });

        // Explosion: a small blast 10 tiles away.
        var heardExplosion = false;
        await server.WaitPost(() =>
        {
            var pos = xformSys.ToMapCoordinates(map.GridCoords.Offset(new Vector2(0.5f, -9.5f)));
            explosions.QueueExplosion(pos, "Default", 3f, 1f, 3f, null, canCreateVacuum: false);
        });
        for (var i = 0; i < 30 && !heardExplosion; i++)
        {
            await server.WaitRunTicks(2);
            await server.WaitPost(() =>
                heardExplosion = entMan.GetComponent<GilbertAIComponent>(gilbert).LastNoise == GilbertNoise.Explosion);
        }
        Assert.That(heardExplosion, "an explosion 10 tiles away is heard");

        // Sprinting: the Goob sprint is loud and re-heard while it lasts.
        var sprintHeard = 0;
        await server.WaitPost(() => entMan.EnsureComponent<SprinterComponent>(runner).IsSprinting = true);
        var tickRate = (int) server.Timing.TickRate;
        var lastCount = -1;
        for (var i = 0; i < 12; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() =>
            {
                entMan.GetComponent<SprinterComponent>(runner).IsSprinting = true;
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                if (comp.LastNoise == GilbertNoise.Sprint && comp.HeardCount != lastCount)
                {
                    sprintHeard++;
                    lastCount = comp.HeardCount;
                }
            });
        }
        Assert.That(sprintHeard, Is.GreaterThanOrEqualTo(2), "a sprinting runner 10 tiles away is heard repeatedly");

        await pair.CleanReturnAsync();
    }

    /// <summary>Beyond the forward cone he still notices anyone on his own or an adjacent tile.</summary>
    [Test]
    public async Task AdjacentAwarenessTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, behind = default, behindDiag = default, behindFar = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            behind = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 1.5f)));
            behindDiag = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-0.5f, 1.5f)));
            behindFar = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 3.5f)));
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            xformSys.SetWorldRotation(gilbert, Angle.Zero); // facing south, all three are behind
            Assert.Multiple(() =>
            {
                Assert.That(ai.CanSee(gilbert, behind), "notices someone on the tile right behind him");
                Assert.That(ai.CanSee(gilbert, behindDiag), "notices someone on a diagonal tile behind him");
                Assert.That(ai.CanSee(gilbert, behindFar), Is.False, "3 tiles behind is outside the 360° bubble");
            });
        });

        // And he turns to face them.
        var faced = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 6 && !faced; i++)
        {
            await server.WaitRunTicks(tickRate / 2);
            await server.WaitPost(() =>
            {
                var forward = xformSys.GetWorldRotation(gilbert).ToWorldVec();
                faced = forward.Y > 0.3f;
            });
        }
        Assert.That(faced, "turns to face someone noticed behind him");

        await pair.CleanReturnAsync();
    }

    /// <summary>The belt carries exactly 2 boxes of 8-gauge shells and 6 handcuffs.</summary>
    [Test]
    public async Task LoadoutCountsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var boxes = CountStored(entMan, ai, gilbert,
                e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID == "MagazineBoxShotgunHighCaliber");
            var cuffs = CountStored(entMan, ai, gilbert, e => entMan.HasComponent<HandcuffComponent>(e));
            Assert.Multiple(() =>
            {
                Assert.That(boxes, Is.EqualTo(2), "two boxes of 8-gauge shells");
                Assert.That(cuffs, Is.EqualTo(6), "six handcuffs");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Only items that are used up count as heal items; an item that heals without being consumed is blacklisted.</summary>
    [Test]
    public async Task HealItemsMustBeConsumedTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var ai = entMan.System<GilbertAISystem>();

        await server.WaitAssertion(() =>
        {
            var gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var suture = entMan.SpawnEntity("MedicatedSuture", map.GridCoords);
            var gauze = entMan.SpawnEntity("Gauze", map.GridCoords);
            var crowbar = entMan.SpawnEntity("Crowbar", map.GridCoords);

            Assert.Multiple(() =>
            {
                Assert.That(ai.HealCharges(suture), Is.GreaterThan(0f), "a suture stack has charges");
                Assert.That(ai.IsConsumableHealItem(comp, suture));
                Assert.That(ai.IsConsumableHealItem(comp, gauze));
                Assert.That(ai.HealCharges(crowbar), Is.Null, "a tool is not a consumable heal item");
                Assert.That(ai.IsConsumableHealItem(comp, crowbar), Is.False);
            });

            if (protoMan.HasIndex<EntityPrototype>("MedicalStitches"))
            {
                var stitches = entMan.SpawnEntity("MedicalStitches", map.GridCoords);
                Assert.That(ai.IsConsumableHealItem(comp, stitches), Is.False, "reusable surgical stitches are never used to heal");
            }

            comp.HealBlacklist.Add(gauze);
            Assert.That(ai.IsConsumableHealItem(comp, gauze), Is.False, "blacklisted (not consumed on use) items are skipped");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Walking off after the surrender order gets a warning; walking further after it gets him to engage.</summary>
    [Test]
    public async Task ArrestEscalationTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 16);
        var entMan = server.ResolveDependency<IEntityManager>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, target = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -3.5f)));
        });

        var ordered = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 20 && !ordered; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() => ordered = entMan.GetComponent<GilbertAIComponent>(gilbert).SurrenderOrdered);
        }
        Assert.That(ordered, "orders the unarmed target to surrender");

        // Steps 4 tiles away from where he was told to stop: a warning, not a fight.
        await server.WaitPost(() =>
        {
            var start = xformSys.GetWorldPosition(target);
            xformSys.SetWorldPosition(target, start + new Vector2(0f, -4f));
        });
        var warned = false;
        for (var i = 0; i < 8 && !warned; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() => warned = entMan.GetComponent<GilbertAIComponent>(gilbert).ArrestWarned);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(warned, "warns a target that walks off");
                Assert.That(comp.State, Is.EqualTo(GilbertAIState.Surrender), "a warning first, no shooting yet");
            });
        });

        // Keeps going (6 more tiles from the warning spot): he engages.
        await server.WaitPost(() =>
        {
            var pos = xformSys.GetWorldPosition(target);
            xformSys.SetWorldPosition(target, pos + new Vector2(0f, -6f));
        });
        var combat = false;
        for (var i = 0; i < 8 && !combat; i++)
        {
            await server.WaitRunTicks(tickRate / 4);
            await server.WaitPost(() => combat = entMan.GetComponent<GilbertAIComponent>(gilbert).State == GilbertAIState.Combat);
        }
        Assert.That(combat, "engages a warned target that keeps walking away");

        await pair.CleanReturnAsync();
    }

    /// <summary>Recognises loot and lootable people, and picks up handcuffs lying in front of him.</summary>
    [Test]
    public async Task ScavengeTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var mobState = entMan.System<MobStateSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var ent = new Entity<GilbertAIComponent>(gilbert, entMan.GetComponent<GilbertAIComponent>(gilbert));
            var far = map.GridCoords.Offset(new Vector2(-7.5f, 7.5f));
            var grenade = entMan.SpawnEntity("ExGrenade", far);
            var flashbang = entMan.SpawnEntity("GrenadeFlashBang", far);
            var c4 = entMan.SpawnEntity("C4", far);
            var cuffs = entMan.SpawnEntity("Handcuffs", far);
            var suture = entMan.SpawnEntity("MedicatedSuture", far);
            var crowbar = entMan.SpawnEntity("Crowbar", far);
            var shell = entMan.SpawnEntity("ShellShotgunHighCaliber", far);
            var victim = entMan.SpawnEntity(Victim, far);

            Assert.Multiple(() =>
            {
                Assert.That(ai.IsGrenade(grenade), "explosive grenade");
                Assert.That(ai.IsGrenade(flashbang), "any grenade type counts");
                Assert.That(ai.IsGrenade(c4), Is.False, "C4 is a charge, not a grenade");
                Assert.That(ai.IsWantedLoot(ent, grenade));
                Assert.That(ai.IsWantedLoot(ent, cuffs), "takes cuffs while under the cap");
                Assert.That(ai.IsWantedLoot(ent, suture), "takes consumable meds");
                Assert.That(ai.IsWantedLoot(ent, crowbar), Is.False, "ignores junk");
                Assert.That(ai.IsHeavyShell(shell));
                Assert.That(ai.TryFindBoxWithRoom(gilbert, shell, out _), Is.False, "fresh boxes are full");
                Assert.That(ai.IsWantedLoot(ent, shell), Is.False, "no room for loose shells");
                Assert.That(ai.IsLootable(gilbert, victim), Is.False, "a healthy free person is not looted");
            });

            mobState.ChangeMobState(victim, MobState.Dead);
            Assert.That(ai.IsLootable(gilbert, victim), "a dead body is looted");
            Assert.That(ai.IsLootable(gilbert, gilbert), Is.False);
        });

        // Handcuffs on the floor 2 tiles in front of him get picked up and stowed.
        var cuffsBefore = 0;
        EntityUid floorCuffs = default;
        await server.WaitPost(() =>
        {
            cuffsBefore = CountStored(entMan, ai, gilbert, e => entMan.HasComponent<HandcuffComponent>(e));
            floorCuffs = entMan.SpawnEntity("Handcuffs", map.GridCoords.Offset(new Vector2(0.5f, -1.5f)));
        });

        var stowed = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 20 && !stowed; i++)
        {
            await server.WaitRunTicks(tickRate);
            await server.WaitPost(() => stowed = ai.EnumerateStored(gilbert).Contains(floorCuffs));
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(stowed, "picks up handcuffs and puts them away");
            Assert.That(CountStored(entMan, ai, gilbert, e => entMan.HasComponent<HandcuffComponent>(e)), Is.EqualTo(cuffsBefore + 1));
        });

        await pair.CleanReturnAsync();
    }
}

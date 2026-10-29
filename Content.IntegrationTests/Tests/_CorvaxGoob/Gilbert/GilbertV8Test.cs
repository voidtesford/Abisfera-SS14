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
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v8: phase-1 reload loop, hearing and stalls; berserk vision / wall breaking / chasing; torso and groin integrity.
/// </summary>
[TestFixture]
public sealed class GilbertV8Test
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

    #region Reload

    /// <summary>
    /// The loop: out of shells with the target 6 tiles away, he steps behind cover to reload, loses sight of the
    /// target for longer than LoseTargetDelay, and the old code aborted the fill, stuffed the box into his bag and
    /// started over ("reload" line every few seconds, never a shell in the gun). Now the gun gets loaded, the line
    /// is said once per real reload at most, and the boxes stay in the webbing.
    /// </summary>
    [Test]
    public async Task ReloadBehindCoverNoLoopTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var inv = entMan.System<InventorySystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, target = default, gun = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -5.5f)));
            // Cover to his side.
            for (var x = 1; x <= 4; x++)
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, -1.5f)));
        });
        await server.WaitRunTicks(5);

        var belt = default(EntityUid?);
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.MeleeSwitchRange = 0f;
            comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            comp.LoseTargetDelay = TimeSpan.FromSeconds(1); // makes the old abort certain once he's behind cover
            Sturdy(entMan, target);
            gun = HeldGun(entMan, gilbert);
            EmptyGun(entMan, gun);
            belt = Slot(inv, gilbert, "belt");
            ai.ForceCombat(gilbert, target);
        });

        var maxLive = 0;
        var bagBoxSeen = false;
        for (var i = 0; i < 30; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                var ballistic = entMan.GetComponent<BallisticAmmoProviderComponent>(gun);
                maxLive = Math.Max(maxLive, ai.CountLiveShells(ballistic));
                var dmg = entMan.GetComponent<DamageableComponent>(target);
                if (dmg.TotalDamage > 150)
                    damageSys.SetAllDamage(target, dmg, 0);

                var back = Slot(inv, gilbert, "back");
                foreach (var box in Boxes(entMan, ai, gilbert))
                {
                    if (back != null && ContainerOwner(entMan, box) == back)
                        bagBoxSeen = true;
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var lines = comp.SaidCounts.GetValueOrDefault("reload");
            Assert.Multiple(() =>
            {
                Assert.That(maxLive, Is.GreaterThanOrEqualTo(3), "shells actually went into the gun");
                Assert.That(comp.ReloadsSucceeded, Is.GreaterThanOrEqualTo(1));
                Assert.That(comp.ReloadsStarted, Is.LessThanOrEqualTo(comp.ReloadsSucceeded + 2), "no reload restart loop");
                Assert.That(lines, Is.LessThanOrEqualTo(comp.ReloadsSucceeded + 1), "no reload line spam");
                Assert.That(lines, Is.LessThanOrEqualTo(4));
                Assert.That(bagBoxSeen, Is.False, "the shell boxes never end up in his backpack");
                foreach (var box in Boxes(entMan, ai, gilbert))
                    Assert.That(ContainerOwner(entMan, box), Is.EqualTo(belt), "boxes stay in the webbing");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The shells in his boxes don't fit the gun: the fill bounces every shell back. He gives up quickly, says the
    /// line at most once, doesn't retry in a loop, and fights in melee instead.
    /// </summary>
    [Test]
    public async Task ReloadFailsGracefullyTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, target = default, gun = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -6.5f)));
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.MeleeSwitchRange = 0f;
            comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            Sturdy(entMan, target);
            gun = HeldGun(entMan, gilbert);
            EmptyGun(entMan, gun);
            // Nothing in his boxes passes the gun's whitelist any more.
            // (Whitelist is Access-restricted to the gun system; reflection is fine for a test.)
            typeof(BallisticAmmoProviderComponent).GetField(nameof(BallisticAmmoProviderComponent.Whitelist))!
                .SetValue(entMan.GetComponent<BallisticAmmoProviderComponent>(gun), new EntityWhitelist { Tags = new() { "CartridgeRifle" } });
            ai.ForceCombat(gilbert, target);
        });

        var sawMelee = false;
        for (var i = 0; i < 20; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                var dmg = entMan.GetComponent<DamageableComponent>(target);
                if (dmg.TotalDamage > 150)
                    damageSys.SetAllDamage(target, dmg, 0);
                if (entMan.GetComponent<GilbertAIComponent>(gilbert).WeaponMode == GilbertWeaponMode.Melee)
                    sawMelee = true;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(comp.ReloadsSucceeded, Is.Zero);
                Assert.That(comp.ReloadFailures, Is.GreaterThanOrEqualTo(1), "the failure is noticed");
                Assert.That(comp.ReloadsStarted, Is.LessThanOrEqualTo(4), "no tight retry loop (2 boxes, 8 s cooldown)");
                Assert.That(comp.SaidCounts.GetValueOrDefault("reload"), Is.LessThanOrEqualTo(1), "no reload line spam");
                Assert.That(sawMelee, "falls back to melee");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>A shell box in his pocket is found and used when the webbing is empty.</summary>
    [Test]
    public async Task ReloadFromPocketTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var inv = entMan.System<InventorySystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, target = default, gun = default, pocketBox = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -6.5f)));
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.MeleeSwitchRange = 0f;
            comp.NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
            Sturdy(entMan, target);
            gun = HeldGun(entMan, gilbert);
            EmptyGun(entMan, gun);
            foreach (var box in Boxes(entMan, ai, gilbert))
                entMan.DeleteEntity(box);
            pocketBox = entMan.SpawnEntity("MagazineBoxShotgunHighCaliber", entMan.GetComponent<TransformComponent>(gilbert).Coordinates);
            Assert.That(inv.TryEquip(gilbert, pocketBox, "pocket2", silent: true, force: true), "test setup: box in the pocket");
            ai.ForceCombat(gilbert, target);
        });

        var maxLive = 0;
        for (var i = 0; i < 12; i++)
        {
            await RunSeconds(pair, 1);
            await server.WaitPost(() =>
            {
                maxLive = Math.Max(maxLive, ai.CountLiveShells(entMan.GetComponent<BallisticAmmoProviderComponent>(gun)));
                var dmg = entMan.GetComponent<DamageableComponent>(target);
                if (dmg.TotalDamage > 150)
                    damageSys.SetAllDamage(target, dmg, 0);
            });
        }

        // Let any in-flight reload finish so the box goes home.
        for (var i = 0; i < 6; i++)
        {
            var busy = false;
            await server.WaitPost(() => busy = entMan.GetComponent<GilbertAIComponent>(gilbert).Task == GilbertTask.Reload);
            if (!busy)
                break;
            await RunSeconds(pair, 1);
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(maxLive, Is.GreaterThanOrEqualTo(3), "loaded from the pocketed box");
            if (!entMan.Deleted(pocketBox) && entMan.GetComponent<BallisticAmmoProviderComponent>(pocketBox).Count > 0)
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                var hands = entMan.System<SharedHandsSystem>();
                var mid = comp.Task == GilbertTask.Reload && hands.IsHolding(gilbert, pocketBox);
                Assert.That(comp.Task != GilbertTask.Reload || comp.ReloadBoxSlot == "pocket2", "origin slot recorded");
                TestContext.Out.WriteLine($"[v8] pocket box: pocket2={Slot(inv, gilbert, "pocket2")} task={comp.Task} owner={ContainerOwner(entMan, pocketBox)}");
                Assert.That(mid || Slot(inv, gilbert, "pocket2") == pocketBox, "box went back into the pocket (or is mid-reload)");
            }
        });

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Hearing

    public enum SpeakerKind { Humanoid, Player, HumanoidWhileBusy, Unreachable }

    /// <summary>
    /// Normal local speech 9 tiles away, behind a wall (no line of sight, a path around it): he walks over to the
    /// spot (or runs into the speaker and deals with them). Covers a plain humanoid speaker, a player-session speaker
    /// sending through the chat path with a session, and speech heard while he is busy picking up loot.
    /// </summary>
    [TestCase(SpeakerKind.Humanoid)]
    [TestCase(SpeakerKind.Player)]
    [TestCase(SpeakerKind.HumanoidWhileBusy)]
    [TestCase(SpeakerKind.Unreachable)]
    public async Task HearingGoesToSpeakerTest(SpeakerKind kind)
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var chat = entMan.System<ChatSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, speaker = default;
        await server.WaitPost(() =>
        {
            for (var x = -4; x <= 4; x++)
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, 5.5f)));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero); // facing south, away from the speaker
            speaker = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 9.5f)));
            if (kind == SpeakerKind.Unreachable)
            {
                // Shut in a closed cell: the spot itself has no path, the nearest point outside does.
                for (var x = -1; x <= 1; x++)
                for (var y = -1; y <= 1; y++)
                {
                    if (x != 0 || y != 0)
                        entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, y + 9.5f)));
                }
            }
            if (kind == SpeakerKind.Player)
            {
                var mindSys = entMan.System<SharedMindSystem>();
                var mind = mindSys.CreateMind(pair.Player!.UserId);
                mindSys.TransferTo(mind, speaker);
            }
        });
        await server.WaitRunTicks(5);

        if (kind == SpeakerKind.HumanoidWhileBusy)
        {
            // Busy fetching handcuffs lying a few tiles south when the speech comes.
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                foreach (var cuffs in entMan.System<GilbertAISystem>().EnumerateStored(gilbert)
                             .Where(e => entMan.HasComponent<Content.Shared.Cuffs.Components.HandcuffComponent>(e)).ToList())
                    entMan.DeleteEntity(cuffs);
                entMan.SpawnEntity("Handcuffs", map.GridCoords.Offset(new Vector2(0.5f, -6.5f)));
                comp.NextScavenge = TimeSpan.Zero;
            });
            for (var i = 0; i < 8; i++)
            {
                await server.WaitRunTicks(15);
                var busy = false;
                await server.WaitPost(() => busy = entMan.GetComponent<GilbertAIComponent>(gilbert).Task == GilbertTask.Pickup);
                if (busy)
                    break;
            }
        }

        EntityCoordinates spot = default;
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v8] before speech: state={comp.State} task={comp.Task}");
            spot = entMan.GetComponent<TransformComponent>(speaker).Coordinates;
            if (kind == SpeakerKind.Player)
                chat.TrySendInGameICMessage(speaker, "Эй, кто здесь?", InGameICChatType.Speak, ChatTransmitRange.Normal, player: pair.Player);
            else
                chat.TrySendInGameICMessage(speaker, "Эй, кто здесь?", InGameICChatType.Speak, ChatTransmitRange.Normal);
            Assert.That(comp.LastNoise, Is.EqualTo(GilbertNoise.Speech), "heard the speech");
        });

        var reached = false;
        var investigated = false;
        var start = timing.CurTime;
        for (var i = 0; i < 40 && !reached; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                var dist = (xformSys.GetWorldPosition(gilbert) - xformSys.ToMapCoordinates(spot).Position).Length();
                entMan.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(gilbert, out var steer);
                TestContext.Out.WriteLine($"[v8] t={(timing.CurTime - start).TotalSeconds:0.0} state={comp.State} task={comp.Task} dist={dist:0.0} steer={steer?.Status}");
                investigated |= comp.State == GilbertAIState.Investigate;
                reached = dist < (kind == SpeakerKind.Unreachable ? 3.5f : 3f) || comp.Target == speaker;
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(investigated, "went to check the voice (Investigate), not by chance");
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).InvestigationsStarted, Is.GreaterThanOrEqualTo(1));
            Assert.That(reached, "walked to the speaker's spot within 20 s");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Whispers stay ignored, even right next to him.</summary>
    [Test]
    public async Task HearingWhisperIgnoredTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var chat = entMan.System<ChatSystem>();
        EntityUid gilbert = default, speaker = default;
        await server.WaitPost(() =>
        {
            for (var x = -2; x <= 2; x++)
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, 1.5f)));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            speaker = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 2.5f)));
        });
        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            var before = comp.HeardCount;
            chat.TrySendInGameICMessage(speaker, "тихо...", InGameICChatType.Whisper, ChatTransmitRange.Normal);
            Assert.That(comp.HeardCount, Is.EqualTo(before));
            Assert.That(comp.State, Is.Not.EqualTo(GilbertAIState.Investigate));
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Torso / groin integrity

    /// <summary>
    /// Torso and groin take 500 each (limbs and head stay 250). 400 damage on the chest alone all lands (the old
    /// 250 cap swallowed the rest), and the berserk still starts at exactly 450.
    /// </summary>
    [Test]
    public async Task TorsoGroinIntegrityTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var body = entMan.System<SharedBodySystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var mobState = entMan.System<MobStateSystem>();

        var gilbert = EntityUid.Invalid;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var seen = new HashSet<BodyPartType>();
            foreach (var (part, partComp) in body.GetBodyChildren(gilbert))
            {
                if (!entMan.TryGetComponent<WoundableComponent>(part, out var woundable))
                    continue;

                seen.Add(partComp.PartType);
                var expected = partComp.PartType is BodyPartType.Chest or BodyPartType.Groin ? 500 : 250;
                Assert.That(woundable.IntegrityCap, Is.EqualTo(FixedPoint2.New(expected)), $"{entMan.ToPrettyString(part)} cap");
                Assert.That(woundable.WoundableIntegrity, Is.EqualTo(FixedPoint2.New(expected)), $"{entMan.ToPrettyString(part)} integrity");
            }

            Assert.That(seen, Does.Contain(BodyPartType.Chest).And.Contain(BodyPartType.Groin));
        });

        // 400 on the chest: all of it counts (> the old 250 cap), still phase one.
        await server.WaitPost(() =>
        {
            var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
            for (var i = 0; i < 10; i++)
                damageSys.TryChangeDamage(gilbert, new DamageSpecifier(blunt, 40), ignoreResistances: true, targetPart: TargetBodyPart.Chest, canMiss: false);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var total = entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage;
            Assert.That(total, Is.GreaterThan(FixedPoint2.New(250)), "chest damage isn't capped at 250 any more");
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.None));
        });

        // The hit that crosses 450 starts the berserk and is clamped to exactly 450 (50 HP left), alive.
        await server.WaitPost(() =>
        {
            var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
            for (var i = 0; i < 4; i++)
                damageSys.TryChangeDamage(gilbert, new DamageSpecifier(blunt, 40), ignoreResistances: true, targetPart: TargetBodyPart.Groin, canMiss: false);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.Phase, Is.Not.EqualTo(GilbertBerserkPhase.None), "berserk started");
            Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage, Is.EqualTo(FixedPoint2.New(450)));
            Assert.That(mobState.IsAlive(gilbert));
        });

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Soundtrack / transition audio

    private static List<(EntityUid Uid, AudioComponent Audio)> AudioOn(IEntityManager entMan, EntityUid gilbert)
    {
        var list = new List<(EntityUid, AudioComponent)>();
        var query = entMan.EntityQueryEnumerator<AudioComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var audio, out var xform))
        {
            if (xform.ParentUid == gilbert && !entMan.IsQueuedForDeletion(uid))
                list.Add((uid, audio));
        }

        return list;
    }

    private static string FileOf(AudioComponent audio)
    {
        string file = audio.FileName;
        return file;
    }

    private static bool Playing(IEntityManager entMan, EntityUid gilbert, string file)
    {
        return AudioOn(entMan, gilbert).Any(a => FileOf(a.Audio).EndsWith(file));
    }

    /// <summary>
    /// The looped ambience is silent during the spawn sequence, plays (looped, quiet, ~15 tiles, attached to him)
    /// once he activates, stops the moment the berserk transition starts and never comes back; the transition
    /// plays the growl-into-roar clip once instead of the zombie screams, and the skull shout still follows.
    /// </summary>
    [Test]
    public async Task SoundtrackPhaseOneOnlyTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        const string track = "gilbert_ambience_1.ogg";
        const string transition = "gilbert_berserk_transition_1.ogg";

        var gilbert = EntityUid.Invalid;
        await server.WaitPost(() => gilbert = entMan.SpawnEntity(Gilbert, map.GridCoords));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertComponent>(gilbert).Active, Is.False);
            Assert.That(Playing(entMan, gilbert, track), Is.False, "no soundtrack during the spawn sequence");
        });

        await server.WaitPost(() => entMan.System<GilbertSystem>().FinishSpawnSequence(gilbert));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var tracks = AudioOn(entMan, gilbert).Where(a => FileOf(a.Audio).EndsWith(track)).ToList();
            Assert.That(tracks, Has.Count.EqualTo(1), "one soundtrack once active");
            var p = tracks[0].Audio.Params;
            Assert.That(p.Loop, Is.True);
            Assert.That(p.Volume, Is.InRange(-10f, -8f));
            Assert.That(p.MaxDistance, Is.EqualTo(15f).Within(0.01f));
        });

        await RunSeconds(pair, 2);
        await server.WaitAssertion(() => Assert.That(Playing(entMan, gilbert, track), "still looping in phase one"));

        await server.WaitPost(() =>
        {
            var berserk = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, berserk));
        });
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Assert.That(Playing(entMan, gilbert, track), Is.False, "soundtrack stops at the transition");
            var clips = AudioOn(entMan, gilbert).Where(a => FileOf(a.Audio).EndsWith(transition)).ToList();
            Assert.That(clips, Has.Count.EqualTo(1), "growl/roar clip plays once");
            Assert.That(clips[0].Audio.Params.Loop, Is.False);
        });

        // Through the whole transition: never a zombie scream, never the soundtrack again, then the skull shout.
        var sawScream = false;
        var sawTrack = false;
        var sawShout = false;
        for (var i = 0; i < 24; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitAssertion(() =>
            {
                foreach (var (_, audio) in AudioOn(entMan, gilbert))
                {
                    var f = FileOf(audio).ToLowerInvariant();
                    sawScream |= f.Contains("zombie") || f.Contains("scream");
                    sawTrack |= f.EndsWith(track);
                    sawShout |= f.EndsWith("gilbert_berserk_skull_1.ogg");
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            Assert.That(sawScream, Is.False, "no zombie screams in the transition");
            Assert.That(sawTrack, Is.False, "soundtrack never restarts in berserk");
            Assert.That(sawShout, "skull-crack shout still plays");
            Assert.That(entMan.GetComponent<GilbertComponent>(gilbert).SoundtrackFinished);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Berserk soundtrack: not during the transition nor over the skull shout, playing (15 tiles, attached to
    /// him) in the rage with the phase-1 loop already gone, cut at the dying sequence and gone after death.
    /// </summary>
    [Test]
    public async Task BerserkSoundtrackTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        const string music = "gilbert_berserk_ambience_1.ogg";

        var gilbert = EntityUid.Invalid;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(2);
        await server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));

        // Transition (v9: 6.9 s, the length of the clip) + the shout (music at 10.4 s): none yet at ~9.6 s.
        var early = false;
        for (var i = 0; i < 19; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitAssertion(() => early |= Playing(entMan, gilbert, music));
        }

        await server.WaitAssertion(() => Assert.That(early, Is.False, "no berserk music over the transition and the shout"));
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            var tracks = AudioOn(entMan, gilbert).Where(a => FileOf(a.Audio).EndsWith(music)).ToList();
            Assert.That(tracks, Has.Count.EqualTo(1), "berserk music plays in the rage");
            Assert.That(tracks[0].Audio.Params.MaxDistance, Is.EqualTo(15f).Within(0.01f));
            Assert.That(tracks[0].Audio.Params.Volume, Is.InRange(-8f, -4f));
            Assert.That(Playing(entMan, gilbert, "gilbert_ambience_1.ogg"), Is.False, "phase-1 loop is gone");
        });

        await RunSeconds(pair, 3);
        await server.WaitAssertion(() => Assert.That(Playing(entMan, gilbert, music), "still playing"));

        await server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartDying((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => Assert.That(Playing(entMan, gilbert, music), Is.False, "cut at the dying sequence"));

        var sawMusic = false;
        for (var i = 0; i < 12; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitAssertion(() =>
            {
                if (!entMan.Deleted(gilbert))
                    sawMusic |= Playing(entMan, gilbert, music);
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(sawMusic, Is.False, "never comes back while dying");
            Assert.That(entMan.Deleted(gilbert) || !entMan.System<MobStateSystem>().IsAlive(gilbert), "dead after the sequence");
            var query = entMan.EntityQueryEnumerator<AudioComponent>();
            while (query.MoveNext(out var uid, out var audio))
            {
                if (!entMan.IsQueuedForDeletion(uid))
                    Assert.That(FileOf(audio).EndsWith(music), Is.False, "no berserk music anywhere after death");
            }
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Deleting him (or his death) takes the soundtrack with him.</summary>
    [Test]
    public async Task SoundtrackStopsOnDeleteTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;

        var gilbert = EntityUid.Invalid;
        var audio = EntityUid.Invalid;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            audio = AudioOn(entMan, gilbert).First(a => FileOf(a.Audio).EndsWith("gilbert_ambience_1.ogg")).Uid;
        });

        await server.WaitPost(() => entMan.DeleteEntity(gilbert));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => Assert.That(entMan.Deleted(audio) || entMan.IsQueuedForDeletion(audio)));

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Stalls

    /// <summary>
    /// Profiles a long patrol through four rooms (gaps in the walls) next to a sealed room he can't reach:
    /// the longest stretch he stands still with no reason, the number of path failures and stalls.
    /// </summary>
    [Test]
    public async Task PatrolNoStallsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        await FillFloor(pair, map, 16);

        var gilbert = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            // Cross of walls with 2-tile gaps -> four rooms.
            for (var i = -16; i <= 16; i++)
            {
                if (Math.Abs(i - 8) > 1 && Math.Abs(i + 8) > 1)
                {
                    entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(0.5f, i + 0.5f)));
                    entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(i + 0.5f, 0.5f)));
                }
            }

            // Sealed 5x5 room in the north-east corner (floor inside, no way in).
            for (var x = 10; x <= 16; x++)
            {
                for (var y = 10; y <= 16; y++)
                {
                    if (x == 10 || y == 10 || x == 16 || y == 16)
                        entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f)));
                }
            }

            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(-6.5f, -6.5f)));
        });
        await server.WaitRunTicks(5);

        var lastPos = Vector2.Zero;
        var stillFor = 0.0;
        var maxStill = 0.0;
        var stillSum = 0.0;
        var noPath = 0;
        var samples = 0;
        var moving = 0;
        var startStalls = 0;
        var xformSys = entMan.System<SharedTransformSystem>();
        await server.WaitAssertion(() =>
        {
            lastPos = xformSys.GetWorldPosition(gilbert);
            startStalls = entMan.GetComponent<GilbertAIComponent>(gilbert).StallCount;
        });

        const double step = 0.25;
        for (var t = 0.0; t < 90; t += step)
        {
            await RunSeconds(pair, step);
            await server.WaitAssertion(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                var pos = xformSys.GetWorldPosition(gilbert);
                var moved = (pos - lastPos).Length() > 0.05f;
                lastPos = pos;
                samples++;
                if (entMan.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(gilbert, out var st)
                    && st.Status == Content.Server.NPC.Components.SteeringStatus.NoPath)
                    noPath++;

                var busy = comp.Task != GilbertTask.None || comp.State != GilbertAIState.Idle;
                if (moved || busy)
                {
                    if (moved)
                        moving++;
                    stillFor = 0;
                    return;
                }

                stillFor += step;
                stillSum += step;
                maxStill = Math.Max(maxStill, stillFor);
            });
        }

        var stalls = 0;
        await server.WaitAssertion(() => stalls = entMan.GetComponent<GilbertAIComponent>(gilbert).StallCount - startStalls);
        TestContext.Out.WriteLine($"[v8] patrol: samples={samples} moving={moving} idleStill={stillSum:0.0}s maxGap={maxStill:0.00}s noPathSamples={noPath} stalls={stalls}");

        Assert.That(maxStill, Is.LessThanOrEqualTo(2.0), "no idle gap longer than the arrival pause");
        Assert.That(moving, Is.GreaterThan(samples * 0.75), "walking most of the time");
        Assert.That(stalls, Is.LessThanOrEqualTo(3), "path failures / stalls are rare");

        await pair.CleanReturnAsync();
    }

    #endregion

    #region Berserk (v8)

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
            // v13: these targeting tests predate the rage abilities; a leap or levitation mid-test muddies them.
            entMan.RemoveComponent<GilbertRageAbilitiesComponent>(gilbert);
        });
        await pair.Server.WaitRunTicks(3);
        await pair.Server.WaitPost(() =>
            entMan.System<GilbertBerserkSystem>().StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.3);
        await pair.Server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage)));
        return gilbert;
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

    /// <summary>
    /// Berserk vision: 360° (behind him too) within 10 tiles, line of sight needed, windows see-through; nothing
    /// in sight -> no target; a voice within 15 tiles sends him to the spot.
    /// </summary>
    [Test]
    public async Task BerserkVisionTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 16);
        var entMan = server.EntMan;
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, behind = default, far = default, walled = default, glass = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            behind = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 8.5f)));
            far = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(12.5f, 0.5f)));
            Box(entMan, map, -5, 0);
            walled = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-4.5f, 0.5f)));
            Sturdy(entMan, walled);
            Box(entMan, map, 0, -6, "Window");
            glass = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, -5.5f)));
        });
        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            var xform = entMan.GetComponent<TransformComponent>(gilbert);
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.VisionRange, Is.EqualTo(10f));
            Assert.That(comp.HearingRange, Is.EqualTo(15f));
            Assert.That(ai.BerserkCanSee(gilbert, xform, behind, comp), "360°: 8 tiles behind his back");
            Assert.That(ai.BerserkCanSee(gilbert, xform, far, comp), Is.False, "12 tiles: too far");
            Assert.That(ai.BerserkCanSee(gilbert, xform, walled, comp), Is.False, "behind walls");
            Assert.That(ai.BerserkCanSee(gilbert, xform, glass, comp), "behind windows");
            entMan.DeleteEntity(behind);
            entMan.DeleteEntity(glass);
        });

        await server.WaitPost(() => entMan.System<GilbertBerserkSystem>()
            .StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await server.WaitPost(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            comp.TransitionDuration = TimeSpan.FromSeconds(1.0);
            comp.BurstAt = TimeSpan.FromSeconds(0.6);
            comp.RageDuration = TimeSpan.FromSeconds(300);
        });
        await RunSeconds(pair, 1.3);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).BerserkTarget, Is.Null,
                "nobody in sight (one too far, one walled in)");
        });

        // The walled-in one shouts: he goes there and breaks in.
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(walled, "помогите!",
            InGameICChatType.Speak, ChatTransmitRange.Normal));
        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).BerserkLastKnown, Is.Not.Null, "heard the voice"));

        var hit = false;
        var start = Blunt(entMan, walled);
        for (var i = 0; i < 40 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() =>
            {
                hit = Blunt(entMan, walled) > start;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v8] vision: smash={comp.BerserkSmashPunches} repaths={comp.BerserkRepaths} unstuck={comp.BerserkUnstucks}");
            Assert.That(hit, "went for the voice, smashed through the walls and hit them within 20 s");
            Assert.That(comp.BerserkSmashPunches, Is.GreaterThan(0));
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>+30 % fist range while raging (v9: was +50 %).</summary>
    [Test]
    public async Task BerserkMeleeRangeTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var entMan = server.EntMan;

        var gilbert = EntityUid.Invalid;
        var before = 0f;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            before = entMan.GetComponent<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(gilbert).Range;
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            comp.TransitionDuration = TimeSpan.FromSeconds(1.0);
            comp.BurstAt = TimeSpan.FromSeconds(0.6);
            comp.RageDuration = TimeSpan.FromSeconds(300);
        });
        await server.WaitRunTicks(3);
        await server.WaitPost(() => entMan.System<GilbertBerserkSystem>()
            .StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(gilbert).Range, Is.EqualTo(before), "not during the transition"));
        await RunSeconds(pair, 1.3);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(gilbert).Range,
                Is.EqualTo(before * 1.3f).Within(0.001f), "+30 % reach in the rage"));
        // v9 (t260u): base fist reach 1.5 (MeleeWeaponComponent default, not overridden in gilbert.yml) -> 1.95.
        await server.WaitAssertion(() =>
        {
            Assert.That(before, Is.EqualTo(1.5f).Within(0.001f), "base reach");
            Assert.That(entMan.GetComponent<Content.Shared.Weapons.Melee.MeleeWeaponComponent>(gilbert).Range,
                Is.EqualTo(1.95f).Within(0.001f), "final berserk reach");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Any blocker between him and his prey (wall, locked airlock, window, grille, vending machine) is beaten
    /// until it breaks, and he carries on to the victim.
    /// </summary>
    [TestCase("WallSolid")]
    [TestCase("AirlockCommandLocked")]
    [TestCase("Window")]
    [TestCase("Grille")]
    [TestCase("VendingMachineCola")]
    public async Task BerserkSmashesAnyBlockerTest(string blockerProto)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.EntMan;
        var gilbert = await SpawnRaging(pair, map, new Vector2(-3.5f, 0.5f));

        EntityUid victim = default, blocker = default;
        await server.WaitPost(() =>
        {
            // Dead-end corridor x = 0..4 along y = 0 (reinforced walls round it), the blocker at x = 1.
            for (var x = 0; x <= 5; x++)
            {
                entMan.SpawnEntity("WallReinforced", map.GridCoords.Offset(new Vector2(x + 0.5f, 1.5f)));
                entMan.SpawnEntity("WallReinforced", map.GridCoords.Offset(new Vector2(x + 0.5f, -0.5f)));
            }
            entMan.SpawnEntity("WallReinforced", map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
            blocker = entMan.SpawnEntity(blockerProto, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(3.5f, 0.5f)));
            Sturdy(entMan, victim);
        });
        await server.WaitRunTicks(2);
        await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(victim, "не трогай меня!",
            InGameICChatType.Speak, ChatTransmitRange.Normal));

        var hit = false;
        var start = FixedPoint2.Zero;
        await server.WaitPost(() => start = Blunt(entMan, victim));
        for (var i = 0; i < 60 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            await server.WaitPost(() =>
            {
                hit = Blunt(entMan, victim) > start;
            });
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v8] smash {blockerProto}: broken={entMan.Deleted(blocker)} punches={comp.BerserkSmashPunches} repaths={comp.BerserkRepaths} unstuck={comp.BerserkUnstucks}");
            // v9 (t258u): in the rage he rips an airlock open instantly (bolts or not) instead of punching it apart.
            Assert.That(entMan.Deleted(blocker) || entMan.GetComponent<TransformComponent>(blocker).Anchored == false
                || entMan.TryGetComponent<Content.Shared.Doors.Components.DoorComponent>(blocker, out var door)
                   && door.State is Content.Shared.Doors.Components.DoorState.Open or Content.Shared.Doors.Components.DoorState.Opening,
                $"{blockerProto} broken or ripped open");
            Assert.That(hit, $"through the {blockerProto} and at the victim within 30 s");
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The nearest one he can actually get at; then, once they are out of sight, he goes to where he last saw
    /// them and searches around there.
    /// </summary>
    [Test]
    public async Task BerserkNearestAndLastKnownTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 16);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));

        EntityUid caged = default, open = default, farther = default;
        await server.WaitPost(() =>
        {
            // Closest, but behind glass.
            Box(entMan, map, -3, 0, "Window");
            caged = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-2.5f, 0.5f)));
            open = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 5.0f)));
            farther = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(7.5f, 0.5f)));
            foreach (var v in new[] { caged, open, farther })
                Sturdy(entMan, v);
        });
        await RunSeconds(pair, 0.4);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).BerserkTarget, Is.EqualTo(open),
                "the nearest one he can walk straight up to"));

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(caged);
            entMan.DeleteEntity(farther);
        });

        // Gone out of sight: whisked into a sealed cell far away.
        EntityCoordinates lastSeen = default;
        await RunSeconds(pair, 0.3);
        await server.WaitPost(() =>
        {
            lastSeen = entMan.GetComponent<TransformComponent>(open).Coordinates;
            Box(entMan, map, 14, 14);
            xformSys.SetCoordinates(open, map.GridCoords.Offset(new Vector2(14.5f, 14.5f)));
        });

        // v9: the search now moves on along the guessed escape route the moment the spot is reached, so sample
        // finely: he must lose the target, remember the spot, and be at it (within 3 tiles) while searching.
        var lost = false;
        var remembered = false;
        var searched = false;
        for (var i = 0; i < 90 && !searched; i++)
        {
            await RunSeconds(pair, 0.1);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                lost |= comp.BerserkTarget == null;
                remembered |= comp.BerserkLastKnown != null;
                var d = (xformSys.GetWorldPosition(gilbert) - xformSys.ToMapCoordinates(lastSeen).Position).Length();
                searched = comp.BerserkSearchUntil > TimeSpan.Zero && d < 3f;
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(lost, "lost sight");
            Assert.That(remembered, "remembers where");
        });
        await server.WaitAssertion(() => Assert.That(searched, "went to the last-known spot and searched there"));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Chasing round a corner: a wall between them with an open way round; he must get there (round or through)
    /// without getting stuck on the corner.
    /// </summary>
    [Test]
    public async Task BerserkCornerChaseTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));

        EntityUid victim = default;
        await server.WaitPost(() =>
        {
            // L-shaped wall: east of him and south, the victim tucked behind the corner, seen through one window.
            for (var y = -4; y <= 3; y++)
                entMan.SpawnEntity(y == 3 ? "Window" : "WallReinforced", map.GridCoords.Offset(new Vector2(3.5f, y + 0.5f)));
            for (var x = 4; x <= 7; x++)
                entMan.SpawnEntity("WallReinforced", map.GridCoords.Offset(new Vector2(x + 0.5f, -4.5f)));
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(5.5f, -2.5f)));
            Sturdy(entMan, victim);
        });

        var hit = false;
        var start = FixedPoint2.Zero;
        await server.WaitPost(() => start = Blunt(entMan, victim));
        // He sees them through the window, then loses them behind the wall: last-known + hearing (a scream).
        for (var i = 0; i < 40 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            if (i % 6 == 0)
            {
                await server.WaitPost(() => entMan.System<ChatSystem>().TrySendInGameICMessage(victim, "а-а-а!",
                    InGameICChatType.Speak, ChatTransmitRange.Normal));
            }
            await server.WaitPost(() => hit = Blunt(entMan, victim) > start);
        }

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            TestContext.Out.WriteLine($"[v8] corner: punches={comp.BerserkSmashPunches} repaths={comp.BerserkRepaths} unstuck={comp.BerserkUnstucks}");
            Assert.That(hit, "got round (or through) the corner and hit them within 20 s");
        });
        await pair.CleanReturnAsync();
    }

    #endregion

    #region Reaction latency (v8)

    /// <summary>Phase 1: someone stepping into his view is answered within 0.5 s.</summary>
    [Test]
    public async Task ReactsToTargetFastTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        var gilbert = EntityUid.Invalid;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f))));
        await RunSeconds(pair, 3);

        var spawned = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            var (pos, rot) = xformSys.GetWorldPositionRotation(gilbert);
            var at = pos + rot.ToWorldVec() * 5f;
            var victim = entMan.SpawnEntity(Victim, new MapCoordinates(at, entMan.GetComponent<TransformComponent>(gilbert).MapID));
            spawned = timing.CurTime;
        });

        var reacted = TimeSpan.MaxValue;
        for (var i = 0; i < 60 && reacted == TimeSpan.MaxValue; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                if (entMan.GetComponent<GilbertAIComponent>(gilbert).State != GilbertAIState.Idle)
                    reacted = timing.CurTime - spawned;
            });
        }

        TestContext.Out.WriteLine($"[v8] reaction (spotted): {reacted.TotalSeconds:0.000}s");
        Assert.That(reacted.TotalSeconds, Is.LessThanOrEqualTo(0.5), "reacts to a target within 0.5 s");
        await pair.CleanReturnAsync();
    }

    /// <summary>Phase 1: a voice behind a wall gets him moving within 0.5 s; being hit turns him on the attacker at once.</summary>
    [Test]
    public async Task ReactsToNoiseAndDamageFastTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var xformSys = entMan.System<SharedTransformSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        EntityUid gilbert = default, speaker = default;
        await server.WaitPost(() =>
        {
            for (var x = -4; x <= 4; x++)
                entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, 4.5f)));
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            speaker = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 8.5f)));
        });
        await RunSeconds(pair, 3);

        var said = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            entMan.System<ChatSystem>().TrySendInGameICMessage(speaker, "кто тут?", InGameICChatType.Speak, ChatTransmitRange.Normal);
            said = timing.CurTime;
        });

        var reacted = TimeSpan.MaxValue;
        for (var i = 0; i < 60 && reacted == TimeSpan.MaxValue; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                if (comp.State == GilbertAIState.Investigate && entMan.HasComponent<Content.Server.NPC.Components.NPCSteeringComponent>(gilbert))
                    reacted = timing.CurTime - said;
            });
        }

        TestContext.Out.WriteLine($"[v8] reaction (voice): {reacted.TotalSeconds:0.000}s");
        Assert.That(reacted.TotalSeconds, Is.LessThanOrEqualTo(0.5), "moves towards a voice within 0.5 s");

        // Hit from behind by someone standing right next to him.
        EntityUid attacker = default;
        var hitAt = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            var (pos, rot) = xformSys.GetWorldPositionRotation(gilbert);
            attacker = entMan.SpawnEntity(Victim, new MapCoordinates(pos - rot.ToWorldVec() * 3f, entMan.GetComponent<TransformComponent>(gilbert).MapID));
        });
        await server.WaitRunTicks(1);
        await server.WaitPost(() =>
        {
            entMan.System<DamageableSystem>().TryChangeDamage(gilbert,
                new DamageSpecifier { DamageDict = { ["Blunt"] = FixedPoint2.New(5) } }, origin: attacker);
            hitAt = timing.CurTime;
        });

        var answered = TimeSpan.MaxValue;
        for (var i = 0; i < 60 && answered == TimeSpan.MaxValue; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                if (comp.State is GilbertAIState.Combat or GilbertAIState.Surrender && comp.Target == attacker)
                    answered = timing.CurTime - hitAt;
            });
        }

        TestContext.Out.WriteLine($"[v8] reaction (hit): {answered.TotalSeconds:0.000}s");
        Assert.That(answered.TotalSeconds, Is.LessThanOrEqualTo(0.5), "turns on the attacker within 0.5 s");
        await pair.CleanReturnAsync();
    }

    /// <summary>Berserk: a victim appearing in sight is targeted and run at within 0.5 s.</summary>
    [Test]
    public async Task BerserkReactsFastTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var gilbert = await SpawnRaging(pair, map, new Vector2(0.5f, 0.5f));
        await RunSeconds(pair, 1);

        EntityUid victim = default;
        var spawned = TimeSpan.Zero;
        await server.WaitPost(() =>
        {
            var pos = entMan.System<SharedTransformSystem>().GetWorldPosition(gilbert);
            victim = entMan.SpawnEntity(Victim, new MapCoordinates(pos + new Vector2(-6f, 0f), entMan.GetComponent<TransformComponent>(gilbert).MapID));
            spawned = timing.CurTime;
        });

        var reacted = TimeSpan.MaxValue;
        for (var i = 0; i < 60 && reacted == TimeSpan.MaxValue; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                if (entMan.GetComponent<GilbertAIComponent>(gilbert).BerserkTarget == victim
                    && entMan.HasComponent<Content.Server.NPC.Components.NPCSteeringComponent>(gilbert))
                    reacted = timing.CurTime - spawned;
            });
        }

        TestContext.Out.WriteLine($"[v8] reaction (berserk): {reacted.TotalSeconds:0.000}s");
        Assert.That(reacted.TotalSeconds, Is.LessThanOrEqualTo(0.5), "berserk: goes for a new victim within 0.5 s");
        await pair.CleanReturnAsync();
    }

    #endregion
}

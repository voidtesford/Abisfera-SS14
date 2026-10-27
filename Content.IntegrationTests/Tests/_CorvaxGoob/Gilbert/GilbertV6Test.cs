using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Jittering;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.Traits.Assorted;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Components;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v6: the second phase «Берсерк» (50 HP left): trigger and clamp, invulnerability, the 10 tile burst, fists only,
/// punch bonus, run speed, hunting pets, smashing obstacles, the timed death that can't be undone, the stronger
/// interference.
/// </summary>
[TestFixture]
public sealed class GilbertV6Test
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

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.Server.WaitRunTicks((int) Math.Ceiling(seconds * rate) + 1);
    }

    /// <summary>Short timers so the whole phase fits in a test.</summary>
    private static void ShortTimers(GilbertBerserkComponent comp, double rage = 2.0)
    {
        comp.TransitionDuration = TimeSpan.FromSeconds(1.0);
        comp.BurstAt = TimeSpan.FromSeconds(0.6);
        comp.RageDuration = TimeSpan.FromSeconds(rage);
        comp.DyingDuration = TimeSpan.FromSeconds(0.8);
        comp.FallAt = TimeSpan.FromSeconds(0.5);
    }

    private static DamageSpecifier Dmg(string type, float amount)
    {
        return new DamageSpecifier { DamageDict = { [type] = FixedPoint2.New(amount) } };
    }

    /// <summary>
    /// A hit that would take him past 450 damage is cut down to land exactly on 450 (50 HP) and starts the phase;
    /// from then on nothing hurts or heals him.
    /// </summary>
    [Test]
    public async Task TriggerAndInvulnerableTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var damage = server.System<DamageableSystem>();
        var mobState = server.System<MobStateSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            // «Садист впадает в бешенство» is an emote ("*Gilbert flies into a rage!"), not a popup or a spoken line.
            Assert.That(Loc.TryGetString("gilbert-berserk-emote", out _), "berserk emote is localised");
            Assert.That(Loc.TryGetString("gilbert-berserk-popup", out _), Is.False, "no popup any more");

            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            damage.TryChangeDamage(gilbert, Dmg("Blunt", 300), ignoreResistances: true, canMiss: false);
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.None), "300 damage: still the first phase");

            damage.TryChangeDamage(gilbert, Dmg("Blunt", 400), ignoreResistances: true, canMiss: false);
            var total = entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage;
            Assert.That(total, Is.EqualTo(FixedPoint2.New(450)), "the big hit is cut down to 50 HP left");
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.Transition));
            Assert.That(mobState.IsAlive(gilbert));
            Assert.That(entMan.HasComponent<JitteringComponent>(gilbert), "he shakes");

            // Invulnerable: no damage (any kind, resistances or not), no healing.
            damage.TryChangeDamage(gilbert, Dmg("Piercing", 200), ignoreResistances: true, canMiss: false);
            damage.TryChangeDamage(gilbert, Dmg("Shock", 50), canMiss: false);
            damage.TryChangeDamage(gilbert, Dmg("Blunt", -100), ignoreResistances: true, canMiss: false);
            Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage, Is.EqualTo(FixedPoint2.New(450)));
        });

        await server.WaitPost(() => entMan.DeleteEntity(gilbert));
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Transition: hands emptied (and nothing can be picked up), frozen, and at the burst every living mob within
    /// 10 tiles takes 20 Shock; farther away nothing.
    /// </summary>
    [Test]
    public async Task TransitionBurstTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 14);
        var entMan = server.EntMan;
        var berserk = server.System<GilbertBerserkSystem>();
        var hands = server.System<SharedHandsSystem>();

        EntityUid gilbert = default, near = default, far = default, item = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            ShortTimers(entMan.GetComponent<GilbertBerserkComponent>(gilbert));
            near = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(6.5f, 0.5f)));
            far = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-12.5f, 0.5f)));
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            // Whatever he holds now goes on the floor.
            foreach (var hand in hands.EnumerateHands(gilbert).ToList())
                hands.TryDrop(gilbert, hand, checkActionBlocker: false);
            item = entMan.SpawnEntity("Crowbar", entMan.GetComponent<TransformComponent>(gilbert).Coordinates);
            Assert.That(hands.TryPickupAnyHand(gilbert, item, checkActionBlocker: false), "test setup: holding a crowbar");
            berserk.StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert)));
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(hands.EnumerateHeld(gilbert).Any(), Is.False, "everything dropped");
            Assert.That(hands.TryPickupAnyHand(gilbert, item), Is.False, "no picking anything up: fists only");
            Assert.That(entMan.GetComponent<DamageableComponent>(near).Damage.DamageDict.GetValueOrDefault("Shock"), Is.EqualTo(FixedPoint2.Zero), "no Shock before the burst");
        });

        await RunSeconds(pair, 0.75); // past BurstAt (0.6), before the end of the transition (1.0)

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.Transition));
            Assert.That(comp.BurstDone);
            var shock = entMan.GetComponent<DamageableComponent>(near).Damage.DamageDict.GetValueOrDefault("Shock");
            Assert.That(shock, Is.EqualTo(FixedPoint2.New(20)), "20 Shock within 10 tiles");
            Assert.That(entMan.GetComponent<DamageableComponent>(far).Damage.DamageDict.GetValueOrDefault("Shock"), Is.EqualTo(FixedPoint2.Zero), "no Shock at 13 tiles");
            Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage, Is.EqualTo(FixedPoint2.Zero), "not himself");
            Assert.That(comp.LoudAudio, Is.Not.Empty, "screams are playing during the transition");
        });

        var res = server.ResolveDependency<IResourceManager>();
        await RunSeconds(pair, 0.4);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            Assert.That(entMan.HasComponent<JitteringComponent>(gilbert), Is.False, "stops shaking once raging");

            // End of the transition: screams cut off, «Я ТЕБЕ ЧЕРЕП ПРОЛОМЛЮ!!!» shouted (voice + chat).
            Assert.That(comp.LoudAudio, Is.Empty, "screams stopped for the shout");
            Assert.That(comp.SkullShoutSound, Is.InstanceOf<SoundPathSpecifier>());
            var path = ((SoundPathSpecifier) comp.SkullShoutSound).Path;
            Assert.That(path.ToString(), Is.EqualTo("/Audio/_CorvaxGoob/Gilbert/gilbert_berserk_skull_1.ogg"));
            Assert.That(res.ContentFileExists(path), $"{path} exists");
            var playing = false;
            var audioQuery = entMan.EntityQueryEnumerator<AudioComponent>();
            while (audioQuery.MoveNext(out _, out var audio))
            {
                if (audio.FileName == path.ToString())
                    playing = true;
            }

            Assert.That(playing, "the shout clip is playing");
            Assert.That(Loc.TryGetString("gilbert-berserk-skull-shout", out var shout));
            Assert.That(shout, Does.EndWith("!!!"));
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(near);
            entMan.DeleteEntity(far);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Rage: +15 Shock / +100 Structural on his fists, ×1.5 run speed, no dash.</summary>
    [Test]
    public async Task RageFistsAndSpeedTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var entMan = server.EntMan;
        var berserk = server.System<GilbertBerserkSystem>();
        var melee = server.System<SharedMeleeWeaponSystem>();
        var res = server.ResolveDependency<IResourceManager>();

        EntityUid gilbert = default;
        FixedPoint2 baseShock = default, baseStructural = default, baseBlunt = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            ShortTimers(entMan.GetComponent<GilbertBerserkComponent>(gilbert), rage: 60);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var dmg = melee.GetDamage(gilbert, gilbert);
            baseShock = dmg.DamageDict.GetValueOrDefault("Shock");
            baseStructural = dmg.DamageDict.GetValueOrDefault("Structural");
            baseBlunt = dmg.DamageDict.GetValueOrDefault("Blunt");
            var dummy = entMan.SpawnEntity(null, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            var hitEv = new MeleeHitEvent(new List<EntityUid> { dummy }, gilbert, gilbert, dmg, null, map.GridCoords);
            entMan.EventBus.RaiseLocalEvent(gilbert, hitEv);
            Assert.That(hitEv.HitSoundOverride, Is.Null, "normal punch sound outside the rage");
            entMan.DeleteEntity(dummy);
            berserk.StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert)));
        });

        await server.WaitAssertion(() =>
        {
            var dmg = melee.GetDamage(gilbert, gilbert);
            Assert.That(dmg.DamageDict.GetValueOrDefault("Shock"), Is.EqualTo(baseShock), "no bonus during the transition");
        });

        await RunSeconds(pair, 1.2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Rage));
            var dmg = melee.GetDamage(gilbert, gilbert);
            Assert.That(dmg.DamageDict.GetValueOrDefault("Shock"), Is.EqualTo(baseShock + 15));
            Assert.That(dmg.DamageDict.GetValueOrDefault("Structural"), Is.EqualTo(baseStructural + 100));
            Assert.That(dmg.DamageDict.GetValueOrDefault("Blunt"), Is.EqualTo(baseBlunt), "his normal punch stays");

            var move = entMan.GetComponent<MovementSpeedModifierComponent>(gilbert);
            // Relative to whatever else slows/speeds him: drop only the berserk bonus and compare.
            var rage = move.CurrentSprintSpeed;
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            comp.SprintModifier = 1f;
            server.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(gilbert);
            Assert.That(rage, Is.EqualTo(move.CurrentSprintSpeed * 1.5f).Within(0.01f), "runs 50% faster");
            comp.SprintModifier = 1.5f;
            server.System<MovementSpeedModifierSystem>().RefreshMovementSpeedModifiers(gilbert);
            Assert.That(server.System<GilbertAbilitiesSystem>().CanDash(gilbert), Is.False, "fists only, no dash");

            // His own heavy energy hit sound replaces the normal punch.
            var dummy = entMan.SpawnEntity(null, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            var hitEv = new MeleeHitEvent(new List<EntityUid> { dummy }, gilbert, gilbert, dmg, null, map.GridCoords);
            entMan.EventBus.RaiseLocalEvent(gilbert, hitEv);
            var sound = entMan.GetComponent<GilbertBerserkComponent>(gilbert).PunchSound;
            Assert.That(hitEv.HitSoundOverride, Is.SameAs(sound), "berserk punch sound");
            Assert.That(sound, Is.TypeOf<SoundCollectionSpecifier>());
            var collection = server.ProtoMan.Index<SoundCollectionPrototype>(((SoundCollectionSpecifier) sound).Collection!);
            Assert.That(collection.PickFiles, Has.Count.EqualTo(3));
            foreach (var file in collection.PickFiles)
                Assert.That(res.ContentFileExists(file), file.ToString());
            entMan.DeleteEntity(dummy);
        });

        await server.WaitPost(() => entMan.DeleteEntity(gilbert));
        await pair.CleanReturnAsync();
    }

    /// <summary>He hunts anything alive, pets too, and beats it with his fists.</summary>
    [Test]
    public async Task HuntsPetsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var berserk = server.System<GilbertBerserkSystem>();

        EntityUid gilbert = default, pet = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            ShortTimers(entMan.GetComponent<GilbertBerserkComponent>(gilbert), rage: 120);
        });
        await server.WaitRunTicks(5);
        await server.WaitPost(() => berserk.StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.2);

        // Spawned after the burst, so any damage is from his fists.
        await server.WaitPost(() => pet = entMan.SpawnEntity("MobCorgi", map.GridCoords.Offset(new Vector2(4.5f, 0.5f))));

        var hit = false;
        for (var i = 0; i < 20 && !hit; i++)
        {
            await RunSeconds(pair, 0.5);
            // Shock only comes from his berserk fists (the corgi can bump into things on a bare test map).
            hit = entMan.Deleted(pet)
                  || entMan.GetComponent<DamageableComponent>(pet).Damage.DamageDict.GetValueOrDefault("Shock") > 0;
        }

        Assert.That(hit, "the berserk Gilbert went for the corgi");
        await server.WaitAssertion(() =>
        {
            if (!entMan.Deleted(pet))
                Assert.That(entMan.GetComponent<DamageableComponent>(pet).Damage.DamageDict.GetValueOrDefault("Shock"), Is.GreaterThan(FixedPoint2.Zero), "his punches carry Shock");
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            if (!entMan.Deleted(pet))
                entMan.DeleteEntity(pet);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>A victim walled in: he smashes through the walls to get to them.</summary>
    [Test]
    public async Task SmashesObstaclesTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var berserk = server.System<GilbertBerserkSystem>();

        EntityUid gilbert = default, victim = default;
        var walls = new List<EntityUid>();
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            ShortTimers(entMan.GetComponent<GilbertBerserkComponent>(gilbert), rage: 120);
        });
        await server.WaitRunTicks(5);
        await server.WaitPost(() => berserk.StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.2);

        await server.WaitPost(() =>
        {
            // 3x3 box of walls around the victim at (5, 0).
            for (var x = 4; x <= 6; x++)
            {
                for (var y = -1; y <= 1; y++)
                {
                    if (x == 5 && y == 0)
                        continue;
                    // v8: berserk vision needs line of sight; the side facing him is a window he can see through.
                    walls.Add(entMan.SpawnEntity(x == 4 && y == 0 ? "Window" : "WallSolid", map.GridCoords.Offset(new Vector2(x + 0.5f, y + 0.5f))));
                }
            }

            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(5.5f, 0.5f)));
        });

        var smashed = false;
        for (var i = 0; i < 40 && !smashed; i++)
        {
            await RunSeconds(pair, 0.5);
            smashed = walls.Any(w => entMan.Deleted(w)
                || entMan.TryGetComponent<DamageableComponent>(w, out var d) && d.TotalDamage > 0);
        }

        Assert.That(smashed, "he punched through a wall in his way");

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(victim);
            foreach (var w in walls.Where(w => !entMan.Deleted(w)))
                entMan.DeleteEntity(w);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Time's up: he stops, falls, explodes (20 Shock around) and dies; he can't be revived (unrevivable, and
    /// healing him and setting him alive both fail).
    /// </summary>
    [Test]
    public async Task TimedDeathUnrevivableTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var berserk = server.System<GilbertBerserkSystem>();
        var mobState = server.System<MobStateSystem>();
        var damage = server.System<DamageableSystem>();

        EntityUid gilbert = default, victim = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            ShortTimers(entMan.GetComponent<GilbertBerserkComponent>(gilbert), rage: 0.3);
        });
        await server.WaitRunTicks(5);
        await server.WaitPost(() => berserk.StartBerserk((gilbert, entMan.GetComponent<GilbertBerserkComponent>(gilbert))));
        await RunSeconds(pair, 1.4); // transition + rage over -> dying

        await server.WaitPost(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.Dying));
            // Someone standing next to him when he goes off (behind him; he is frozen anyway).
            victim = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(2.5f, 0.5f)));
        });

        await RunSeconds(pair, 1.0);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertBerserkComponent>(gilbert);
            Assert.That(comp.Phase, Is.EqualTo(GilbertBerserkPhase.Dead));
            Assert.That(mobState.IsDead(gilbert), "the explosion kills him");
            Assert.That(entMan.Deleted(gilbert), Is.False, "the corpse stays");
            Assert.That(entMan.HasComponent<UnrevivableComponent>(gilbert), "can't be revived");
            Assert.That(server.System<StandingStateSystem>().IsDown(gilbert), "he fell");
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).Damage.DamageDict.GetValueOrDefault("Shock"),
                Is.EqualTo(FixedPoint2.New(20)), "the explosion shocks everyone around");

            // Healing doesn't take, and even forcing him alive doesn't stick.
            damage.TryChangeDamage(gilbert, Dmg("Shock", -500), ignoreResistances: true, canMiss: false);
            Assert.That(mobState.IsDead(gilbert));
            mobState.ChangeMobState(gilbert, MobState.Alive);
            Assert.That(mobState.IsDead(gilbert), "nothing brings him back");
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(victim);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>Screen interference: 12 tiles normally, 18 (and stronger, up to 2) while berserk.</summary>
    [Test]
    public void InterferenceRangeTest()
    {
        var rage = new GilbertBerserkComponent { Phase = GilbertBerserkPhase.Rage };
        var dead = new GilbertBerserkComponent { Phase = GilbertBerserkPhase.Dead };

        Assert.That(GilbertProximitySystem.IntensityFrom(15f, null), Is.EqualTo(0f));
        Assert.That(GilbertProximitySystem.IntensityFrom(15f, rage), Is.GreaterThan(0f), "felt at 15 tiles");
        Assert.That(GilbertProximitySystem.IntensityFrom(18.5f, rage), Is.EqualTo(0f), "not beyond 18");
        Assert.That(GilbertProximitySystem.IntensityFrom(5f, rage), Is.GreaterThan(1f), "stronger than ever up close");
        Assert.That(GilbertProximitySystem.IntensityFrom(5f, rage), Is.LessThanOrEqualTo(2f));
        Assert.That(GilbertProximitySystem.IntensityFrom(5f, dead), Is.EqualTo(GilbertProximitySystem.IntensityFrom(5f, null)));
    }
}

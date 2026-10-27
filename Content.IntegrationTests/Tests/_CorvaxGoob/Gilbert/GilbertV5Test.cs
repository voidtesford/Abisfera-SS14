using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Pair;
using Content.Client._CorvaxGoob.Gilbert;
using Content.Goobstation.Maths.FixedPoint;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.ActionBlocker;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Server.Chat.Systems;
using Content.Server.Gravity;
using Content.Shared.Chat;
using Content.Shared.Cuffs;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Stunnable;
using Content.Shared.Gravity;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v5: whispers ignored, spawn sequence, ability effects, M8 crackle and armor penetration, SHOGKK radio.
/// </summary>
[TestFixture]
public sealed class GilbertV5Test
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

    /// <summary>
    /// A whisper, even right next to him, is not heard: no noise, no investigation, and it does not count as the
    /// "lying still -> speaks" wake-up. Normal speech still does all of that.
    /// </summary>
    [Test]
    public async Task WhispersIgnoredTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var chat = server.System<ChatSystem>();
        var xformSys = server.System<SharedTransformSystem>();

        EntityUid gilbert = default, whisperer = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            xformSys.SetWorldRotation(gilbert, Angle.Zero); // facing south, the whisperer is behind him
            // A cuffed captive lying still: the one whose speech would wake him up.
            whisperer = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0.5f, 2f)));
            var cuffs = entMan.SpawnEntity("Handcuffs", map.GridCoords);
            Assert.That(server.System<SharedCuffableSystem>().TryAddNewCuffs(whisperer, whisperer, cuffs), "test setup: cuffed");
            server.System<SharedStunSystem>().TryKnockdown(whisperer, TimeSpan.FromSeconds(60), true);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            comp.LastActive.Remove(whisperer);
            var heard = comp.HeardCount;
            var state = comp.State;

            chat.TrySendInGameICMessage(whisperer, "тихо...", InGameICChatType.Whisper, ChatTransmitRange.Normal);
            chat.TrySendInGameICMessage(whisperer, "он не слышит", InGameICChatType.Whisper, ChatTransmitRange.Normal);
            Assert.Multiple(() =>
            {
                Assert.That(comp.HeardCount, Is.EqualTo(heard), "whispers are not heard");
                Assert.That(comp.State, Is.EqualTo(state), "no investigation");
                Assert.That(comp.LastActive.ContainsKey(whisperer), Is.False, "a whisper doesn't wake a still captive");
            });

            chat.TrySendInGameICMessage(whisperer, "Эй!", InGameICChatType.Speak, ChatTransmitRange.Normal);
            Assert.Multiple(() =>
            {
                Assert.That(comp.HeardCount, Is.EqualTo(heard + 1), "normal speech is heard");
                Assert.That(comp.LastNoise, Is.EqualTo(GilbertNoise.Speech));
                Assert.That(comp.LastActive.ContainsKey(whisperer), "speaking wakes a still captive");
            });
        });

        await pair.CleanReturnAsync();
    }
    /// <summary>
    /// Spawn sequence: red energy silhouette assembling (converging streaks, silhouette shader on the client),
    /// then a small harmless energy burst, then his normal look and he becomes active. Inactive and
    /// invulnerable the whole time, and the AI doesn't think until it's over.
    /// </summary>
    [Test]
    public async Task SpawnSequenceTest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var entMan = server.EntMan;
        var protoMan = server.ProtoMan;
        var damageable = server.System<DamageableSystem>();
        var blocker = server.System<ActionBlockerSystem>();

        DamageSpecifier Hit() => new(protoMan.Index<DamageTypePrototype>("Blunt"), FixedPoint2.New(10));

        int Count(string proto)
        {
            var n = 0;
            var q = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (q.MoveNext(out var meta))
            {
                if (meta.EntityPrototype?.ID == proto)
                    n++;
            }
            return n;
        }

        EntityUid gilbert = default, bystander = default, control = default;
        await server.WaitPost(() =>
        {
            gilbert = entMan.SpawnEntity(Gilbert, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            bystander = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.5f, 0.5f)));
            control = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-3.5f, 0.5f)));
        });

        var tickRate = (int) server.Timing.TickRate;
        await pair.RunTicksSync(tickRate / 2); // 0.5 s: assembling

        var cGilbert = client.EntMan.GetEntity(server.EntMan.GetNetEntity(gilbert));
        var initialThink = TimeSpan.Zero;
        await server.WaitAssertion(() =>
        {
            initialThink = entMan.GetComponent<GilbertAIComponent>(gilbert).NextThink;
            var comp = entMan.GetComponent<GilbertComponent>(gilbert);
            var appearance = server.System<SharedAppearanceSystem>();
            Assert.Multiple(() =>
            {
                Assert.That(comp.Active, Is.False, "inactive while assembling");
                Assert.That(appearance.TryGetData<bool>(gilbert, GilbertVisuals.Silhouette, out var sil) && sil, "silhouette");
                Assert.That(Count("GilbertSpawnConverge"), Is.EqualTo(1), "converging red energy");
                Assert.That(blocker.CanMove(gilbert), Is.False, "can't move");
                Assert.That(damageable.TryChangeDamage(gilbert, Hit(), true), Is.Null, "invulnerable");
                Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage, Is.EqualTo(FixedPoint2.Zero));
                Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).Task, Is.EqualTo(GilbertTask.None), "AI hasn't done anything");
                Assert.That(entMan.GetComponent<Robust.Server.GameObjects.PointLightComponent>(gilbert).Enabled, Is.False);
            });
        });
        await client.WaitAssertion(() =>
        {
            var sprite = client.EntMan.GetComponent<Robust.Client.GameObjects.SpriteComponent>(cGilbert);
            Assert.That(sprite.PostShader, Is.SameAs(client.System<GilbertVisualizerSystem>().SilhouetteInstance), "client draws the red silhouette");
            var progress = client.System<GilbertVisualizerSystem>().SilhouetteProgress(client.EntMan.GetComponent<GilbertComponent>(cGilbert));
            Assert.That(progress, Is.GreaterThan(0.1f).And.LessThan(0.6f), "assembling");
        });

        await pair.RunTicksSync(tickRate * 13 / 10); // 1.8 s: right after the burst
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertComponent>(gilbert);
            var appearance = server.System<SharedAppearanceSystem>();
            Assert.Multiple(() =>
            {
                Assert.That(comp.BurstDone, "the energy burst went off");
                Assert.That(Count("GilbertSpawnBurst"), Is.EqualTo(1));
                Assert.That(appearance.TryGetData<bool>(gilbert, GilbertVisuals.Silhouette, out var sil) && sil, Is.False);
                Assert.That(appearance.TryGetData<bool>(gilbert, GilbertVisuals.SpawnTint, out var tint) && tint, "fading red tint");
                Assert.That(comp.Active, Is.False, "not active yet");
                // The airless test map hurts both the same (barotrauma, cold, suffocation): the burst adds nothing.
                var near = entMan.GetComponent<DamageableComponent>(bystander).TotalDamage;
                var away = entMan.GetComponent<DamageableComponent>(control).TotalDamage;
                Assert.That((near - away).Float(), Is.EqualTo(0f).Within(0.05f), "the burst does no damage");
                Assert.That(entMan.GetComponent<Robust.Server.GameObjects.PointLightComponent>(gilbert).Enabled, "eye light on");
            });
        });
        await pair.RunTicksSync(tickRate / 6); // 2.0 s: give the client a few ticks to get the state
        await client.WaitAssertion(() =>
        {
            var sprite = client.EntMan.GetComponent<Robust.Client.GameObjects.SpriteComponent>(cGilbert);
            var cApp = client.System<SharedAppearanceSystem>();
            var hasSil = cApp.TryGetData<bool>(cGilbert, GilbertVisuals.Silhouette, out var sil);
            Assert.That(sprite.PostShader, Is.Not.SameAs(client.System<GilbertVisualizerSystem>().SilhouetteInstance),
                $"normal look after the burst (client silhouette data: {hasSil}/{sil}, shader {sprite.PostShader})");
        });

        await pair.RunTicksSync(tickRate * 5 / 10); // 2.5 s: active
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(comp.Active, "active after the sequence");
                Assert.That(blocker.CanMove(gilbert), "can move");
                Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).NextThink, Is.GreaterThan(initialThink), "AI started");
                Assert.That(damageable.TryChangeDamage(gilbert, Hit(), true), Is.Not.Null, "can be hurt again");
            });
        });
        await client.WaitAssertion(() =>
        {
            var sprite = client.EntMan.GetComponent<Robust.Client.GameObjects.SpriteComponent>(cGilbert);
            Assert.That(sprite.Color, Is.EqualTo(Color.White), "tint gone");
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(bystander);
            entMan.DeleteEntity(control);
        });
        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Geiger crackle curve: silent beyond 12 tiles, louder and faster the closer he is, full at 6 tiles and
    /// closer (same curve as the screen interference), and it uses the repo's own geiger loops.
    /// </summary>
    [Test]
    public void GeigerCurveTest()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GilbertGeigerSystem.LevelFor(GilbertProximitySystem.IntensityAt(13f)), Is.EqualTo(-1), "silent past 12");
            Assert.That(GilbertGeigerSystem.LevelFor(0f), Is.EqualTo(-1));
            Assert.That(GilbertGeigerSystem.LevelFor(GilbertProximitySystem.IntensityAt(6f)), Is.EqualTo(3), "fastest at 6");
            Assert.That(GilbertGeigerSystem.LevelFor(GilbertProximitySystem.IntensityAt(1f)), Is.EqualTo(3), "fastest inside 6");
            Assert.That(GilbertGeigerSystem.VolumeFor(GilbertProximitySystem.IntensityAt(6f)), Is.EqualTo(GilbertGeigerSystem.MaxVolume), "loudest at 6");
            Assert.That(GilbertGeigerSystem.Loops.Select(l => l.Path.ToString()),
                Is.All.StartsWith("/Audio/Items/Geiger/"), "the dosimeter's own sounds");
        });

        var lastLevel = 3;
        var lastVolume = GilbertGeigerSystem.MaxVolume;
        for (var d = 6f; d <= 12f; d += 0.25f)
        {
            var i = GilbertProximitySystem.IntensityAt(d);
            var level = GilbertGeigerSystem.LevelFor(i);
            var volume = GilbertGeigerSystem.VolumeFor(i);
            Assert.That(level, Is.LessThanOrEqualTo(lastLevel), $"click rate falls off ({d})");
            Assert.That(volume, Is.LessThanOrEqualTo(lastVolume), $"volume falls off ({d})");
            lastLevel = level;
            lastVolume = volume;
        }

        Assert.That(GilbertGeigerSystem.LevelFor(GilbertProximitySystem.IntensityAt(11f)), Is.LessThan(GilbertGeigerSystem.LevelFor(GilbertProximitySystem.IntensityAt(8f))), "slower far away");
    }

    /// <summary>
    /// Client: the local player near a living Gilbert hears the crackle, it eases out after he dies; a ghost
    /// next to him hears nothing.
    /// </summary>
    [Test]
    public async Task GeigerClientTest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 4);
        var geiger = client.System<GilbertGeigerSystem>();

        EntityUid sGilbert = default, sPlayer = default, sGhost = default;
        await server.WaitPost(() =>
        {
            var origin = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            sGilbert = GilbertTestUtil.SpawnActive(server.EntMan, Gilbert, origin);
            sPlayer = server.EntMan.SpawnEntity(Victim, origin.Offset(new Vector2(2f, 0f)));
            server.PlayerMan.SetAttachedEntity(pair.Player!, sPlayer);
        });

        var tickRate = (int) server.Timing.TickRate;
        await pair.RunTicksSync(tickRate * 2); // the 1.2 s fade-in
        await client.WaitAssertion(() =>
        {
            Assert.That(client.System<GilbertInterferenceSystem>().Intensity, Is.GreaterThan(0.9f), "overlay faded in");
            Assert.That(geiger.Level, Is.EqualTo(3), "fast crackle right next to him");
            Assert.That(geiger.Volume, Is.EqualTo(GilbertGeigerSystem.MaxVolume).Within(1f), "loud");
        });

        await server.WaitPost(() => server.System<MobStateSystem>().ChangeMobState(sGilbert, MobState.Dead));
        await pair.RunTicksSync(tickRate / 2);
        await client.WaitAssertion(() =>
        {
            Assert.That(geiger.Level, Is.InRange(0, 2), "slows and quietens while fading out");
            Assert.That(geiger.Volume, Is.LessThan(GilbertGeigerSystem.MaxVolume));
        });
        await pair.RunTicksSync(tickRate * 2);
        await client.WaitAssertion(() => Assert.That(geiger.Level, Is.EqualTo(-1), "stops once he is dead"));

        // A ghost right next to a living Gilbert: nothing.
        await server.WaitPost(() =>
        {
            var origin = map.GridCoords.Offset(new Vector2(0.5f, 0.5f));
            server.EntMan.DeleteEntity(sGilbert);
            sGilbert = GilbertTestUtil.SpawnActive(server.EntMan, Gilbert, origin);
            sGhost = server.EntMan.SpawnEntity("MobObserver", origin.Offset(new Vector2(1f, 0f)));
            server.PlayerMan.SetAttachedEntity(pair.Player!, sGhost);
        });
        await pair.RunTicksSync(tickRate * 2);
        await client.WaitAssertion(() => Assert.That(geiger.Level, Is.EqualTo(-1), "ghosts don't hear it"));

        await server.WaitPost(() =>
        {
            server.EntMan.DeleteEntity(sGhost);
            server.EntMan.DeleteEntity(sPlayer);
            server.EntMan.DeleteEntity(sGilbert);
        });
        await pair.CleanReturnAsync();
    }

    private static int CountProto(IEntityManager entMan, string proto)
    {
        var n = 0;
        var query = entMan.EntityQueryEnumerator<MetaDataComponent>();
        while (query.MoveNext(out _, out var meta))
        {
            if (meta.EntityPrototype?.ID == proto && meta.EntityLifeStage < EntityLifeStage.Terminating)
                n++;
        }
        return n;
    }

    /// <summary>
    /// Extra red effects on his abilities: dash leaves a burst where it starts and where it ends plus an
    /// afterimage trail, shove flashes a red impact on the victim, the scream adds red crackles and sparks to the
    /// white wave, a bare-handed punch sparks on the target. All of it is purely visual and despawns.
    /// </summary>
    [Test]
    public async Task AbilityEffectsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var abilities = server.System<GilbertAbilitiesSystem>();

        EntityUid gilbert = default, target = default, attacker = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4f, 0f)));
            attacker = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, 1f)));
        });
        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAbilitiesComponent>(gilbert);

            Assert.That(abilities.TryDash(gilbert, target), "dash must succeed");
            Assert.That(CountProto(entMan, comp.DashBurstEffect), Is.EqualTo(2), "burst at the start and at the end");
            Assert.That(CountProto(entMan, comp.DashTrailEffect), Is.GreaterThanOrEqualTo(4), "afterimage trail along ~3 tiles");

            var victimPos = server.System<SharedTransformSystem>().GetWorldPosition(attacker);
            Assert.That(abilities.TryShove((gilbert, comp), attacker), "shove must succeed");
            Assert.That(CountProto(entMan, comp.ShoveImpactEffect), Is.EqualTo(1), "red impact on the victim");
            var impact = entMan.AllEntities<MetaDataComponent>().First(e => e.Comp.EntityPrototype?.ID == comp.ShoveImpactEffect);
            Assert.That((server.System<SharedTransformSystem>().GetWorldPosition(impact) - victimPos).Length(), Is.LessThan(0.1f), "on the victim");

            abilities.Scream((gilbert, comp));
            Assert.That(CountProto(entMan, comp.ScreamEffect), Is.EqualTo(1), "white shockwave stays");
            Assert.That(CountProto(entMan, comp.ScreamCrackleEffect), Is.EqualTo(1), "plus red crackles");
            Assert.That(CountProto(entMan, comp.PunchSparkEffect), Is.EqualTo(comp.ScreamSparks), "and sparks");

            // Bare-handed punch (Gilbert is his own weapon); punches with another weapon do not spark.
            var hit = new Content.Shared.Weapons.Melee.Events.MeleeHitEvent(new System.Collections.Generic.List<EntityUid> { target }, gilbert, gilbert,
                new DamageSpecifier(), null, entMan.GetComponent<TransformComponent>(target).Coordinates);
            entMan.EventBus.RaiseLocalEvent(gilbert, hit);
            Assert.That(CountProto(entMan, comp.PunchSparkEffect), Is.EqualTo(comp.ScreamSparks + 1), "punch spark");
        });

        await server.WaitRunTicks((int) server.Timing.TickRate); // 1 s
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAbilitiesComponent>(gilbert);
            foreach (var proto in new[] { comp.DashBurstEffect, comp.DashTrailEffect, comp.ShoveImpactEffect,
                         comp.ScreamCrackleEffect, comp.PunchSparkEffect })
            {
                Assert.That(CountProto(entMan, proto), Is.Zero, $"{proto} despawns");
            }
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(target);
            entMan.DeleteEntity(attacker);
        });
        await pair.CleanReturnAsync();
    }
}

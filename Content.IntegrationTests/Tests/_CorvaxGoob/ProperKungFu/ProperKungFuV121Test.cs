using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Throwing;
using Robust.Shared.Audio;
using Robust.Shared.ContentPack;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// Proper Kung Fu v12.1: every prototype / sound the art references in code exists, and the thrown kunai finisher
/// plays in order (doubles throw → dash-through slash → turn and backflip with the volley) and leaves no kunai behind.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuSystem))]
public sealed class ProperKungFuV121Test
{
    private const string Namespace = "_CorvaxGoob.ProperKungFu";

    [Test]
    public async Task PrototypeReferencesTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var res = server.ResolveDependency<IResourceManager>();

        var problems = new List<string>();
        var checkedCount = 0;

        await server.WaitAssertion(() =>
        {
            var assemblies = new[] { typeof(ProperKungFuSystem).Assembly, typeof(ProperKungFuComponent).Assembly };
            foreach (var type in assemblies.SelectMany(a => a.GetTypes()))
            {
                if (type.Namespace == null || !type.Namespace.Contains(Namespace) || type.ContainsGenericParameters)
                    continue;

                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var field in type.GetFields(all | BindingFlags.Static))
                {
                    if (field.IsLiteral)
                        continue;
                    Check($"{type.Name}.{field.Name}", field.GetValue(null));
                }

                // Default values of components and events (systems only have the static ones above).
                if (type.IsAbstract || typeof(IEntitySystem).IsAssignableFrom(type))
                    continue;

                object? instance = null;
                try
                {
                    if (type.IsValueType || type.GetConstructor(Type.EmptyTypes) != null)
                        instance = Activator.CreateInstance(type);
                }
                catch
                {
                    // Not default-constructible: nothing to check.
                }

                if (instance == null)
                    continue;

                foreach (var field in type.GetFields(all | BindingFlags.Instance))
                {
                    Check($"{type.Name}.{field.Name}", field.GetValue(instance));
                }
            }
        });

        Assert.That(checkedCount, Is.GreaterThan(50), "The reference scan found the art's prototype and sound fields");
        Assert.That(problems, Is.Empty, string.Join("\n", problems));
        await pair.CleanReturnAsync();

        void Check(string where, object? value)
        {
            switch (value)
            {
                case null:
                    return;
                case string:
                    return;
                case EntProtoId ent:
                    if (string.IsNullOrEmpty(ent.Id))
                        return;
                    checkedCount++;
                    if (!protoMan.HasIndex(ent))
                        problems.Add($"{where}: no entity prototype '{ent.Id}'");
                    return;
                case SoundCollectionSpecifier collection:
                    checkedCount++;
                    if (collection.Collection == null || !protoMan.HasIndex<SoundCollectionPrototype>(collection.Collection))
                        problems.Add($"{where}: no sound collection '{collection.Collection}'");
                    return;
                case SoundPathSpecifier path:
                    checkedCount++;
                    if (!res.ContentFileExists(path.Path))
                        problems.Add($"{where}: no sound file '{path.Path}'");
                    return;
                case IEnumerable list:
                    foreach (var item in list)
                    {
                        Check(where + "[]", item);
                    }
                    return;
            }

            var valueType = value.GetType();
            if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ProtoId<>))
            {
                var id = (string?) valueType.GetProperty(nameof(ProtoId<EntityPrototype>.Id))!.GetValue(value);
                if (string.IsNullOrEmpty(id))
                    return; // an unset default

                checkedCount++;
                var kind = valueType.GetGenericArguments()[0];
                if (!protoMan.TryIndex(kind, id, out _))
                    problems.Add($"{where}: no {kind.Name} '{id}'");
            }
        }
    }

    [Test]
    public async Task ThrownKunaiFinisherOrderTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var throwing = entMan.System<ThrowingSystem>();

        EntityUid user = default;
        EntityUid victim = default;
        EntityUid kunai = default;
        var startPos = Vector2.Zero;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -4; x <= 10; x++)
            for (var y = -4; y <= 4; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = Learn(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(user);
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 3.5f, 0.5f));
            entMan.RemoveComponent<RespiratorComponent>(victim);

            foreach (var (part, amount) in new[] { (TargetBodyPart.LeftLeg, 28f), (TargetBodyPart.RightLeg, 28f), (TargetBodyPart.Groin, 29f) })
            {
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Blunt", amount);
                damageSys.TryChangeDamage(victim, dmg, ignoreResistances: true, targetPart: part, canMiss: false);
            }

            kunai = entMan.SpawnEntity("ProperKungFuKunai", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            entMan.GetComponent<ProperKungFuKunaiComponent>(kunai).Summoner = user;
            entMan.GetComponent<ProperKungFuKunaiComponent>(kunai).ExpiresAt = TimeSpan.FromHours(1);
            startPos = xformSys.GetWorldPosition(user);
            throwing.TryThrow(kunai, new Vector2(3f, 0f), 10f, user, pushbackRatio: 0f, recoil: false, playSound: false);
        });

        var started = false;
        var finished = false;
        var maxPhase = -1;
        var movedBeforeDash = false;
        var flipBeforeDash = false;
        var doublesAliveAfterThrows = false;
        var cloneKunai = new HashSet<EntityUid>();
        var volleyKunai = new HashSet<EntityUid>();
        var cloneKunaiAfterDash = 0;
        var volleyBeforeFlip = 0;
        var maxDoubles = 0;
        var behindVictim = false;
        var realKunaiLeft = false;

        for (var i = 0; i < 300 && !finished; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                var hasFinisher = entMan.TryGetComponent<ProperKungFuKunaiFinisherComponent>(user, out var finisher);
                if (hasFinisher)
                {
                    started = true;
                    maxPhase = Math.Max(maxPhase, finisher!.Phase);
                }
                else if (started)
                {
                    finished = true;
                }

                var phase = hasFinisher ? finisher!.Phase : started ? 99 : -1;
                var doubles = 0;
                var doubleQuery = entMan.EntityQueryEnumerator<MetaDataComponent>();
                while (doubleQuery.MoveNext(out var uid, out var meta))
                {
                    if (meta.EntityPrototype?.ID == "ProperKungFuKunaiDouble" && !entMan.IsQueuedForDeletion(uid))
                        doubles++;
                }

                maxDoubles = Math.Max(maxDoubles, doubles);
                if (phase >= 2 && doubles > 0)
                    doublesAliveAfterThrows = true;

                var flying = entMan.EntityQueryEnumerator<ProperKungFuFlyingKunaiComponent>();
                while (flying.MoveNext(out var uid, out var fk))
                {
                    if (fk.User != user)
                        continue;

                    if (fk.Damage <= 1f)
                    {
                        if (cloneKunai.Add(uid) && phase >= 3)
                            cloneKunaiAfterDash++;
                    }
                    else if (volleyKunai.Add(uid) && phase < 4)
                    {
                        volleyBeforeFlip++;
                    }
                }

                if (started && phase <= 1)
                {
                    if ((xformSys.GetWorldPosition(user) - startPos).Length() > 0.05f)
                        movedBeforeDash = true;
                    if (entMan.HasComponent<ProperKungFuSlidingComponent>(user))
                        flipBeforeDash = true;
                }

                if (phase == 3 || phase == 4)
                {
                    // After the dash the user stands past the victim (further along +X).
                    if (xformSys.GetWorldPosition(user).X > xformSys.GetWorldPosition(victim).X + 0.3f)
                        behindVictim = true;
                }
            });
        }

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            var query = entMan.EntityQueryEnumerator<ProperKungFuKunaiComponent>();
            while (query.MoveNext(out _, out _))
            {
                realKunaiLeft = true;
            }

            Assert.That(started, "A thrown kunai hit on an 85-damage target starts the thrown finisher");
            Assert.That(finished, "The thrown finisher ends");
            Assert.That(maxPhase, Is.EqualTo(4), "All phases played");
            Assert.That(maxDoubles, Is.EqualTo(ProperKungFuSystem.KunaiDoubleCount), "Ten crimson doubles appear");
            Assert.That(doublesAliveAfterThrows, Is.False, "Every double vanished after its throw");
            Assert.That(cloneKunai, Has.Count.EqualTo(ProperKungFuSystem.KunaiDoubleCount), "Ten 1-damage double kunai");
            Assert.That(cloneKunaiAfterDash, Is.Zero, "The doubles all throw before the dash");
            Assert.That(movedBeforeDash, Is.False, "Nobody teleports and the user does not move while the doubles throw");
            Assert.That(flipBeforeDash, Is.False, "No backflip before the doubles are done");
            Assert.That(behindVictim, "The dash carries the user through and behind the victim");
            Assert.That(volleyKunai, Has.Count.EqualTo(ProperKungFuSystem.KunaiVolleyCount), "Ten volley kunai");
            Assert.That(volleyBeforeFlip, Is.Zero, "The volley flies only during the backflip");
            Assert.That(entMan.Deleted(kunai) || entMan.IsQueuedForDeletion(kunai), "The thrown kunai is gone");
            Assert.That(realKunaiLeft, Is.False, "No real kunai is left stuck in the victim");
        });

        await pair.CleanReturnAsync();
    }

    private static EntityUid Learn(IEntityManager entMan, EntityCoordinates coords)
    {
        var user = SpawnMob(entMan, coords);
        var book = entMan.SpawnEntity("ProperKungFuManual", coords);
        Assert.That(entMan.System<SharedHandsSystem>().TryPickupAnyHand(user, book), Is.True);
        entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        return user;
    }

    private static EntityUid SpawnMob(IEntityManager entMan, EntityCoordinates coords)
    {
        var mob = entMan.SpawnEntity("MobHuman", coords);
        entMan.RemoveComponent<BarotraumaComponent>(mob);
        entMan.RemoveComponent<TemperatureComponent>(mob);
        return mob;
    }
}

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Temperature.Components;
using Content.Server._CorvaxGoob.ProperKungFu;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Actions.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

/// <summary>
/// Proper Kung Fu v13.1: Wolf Judgment (and the other v13 skills that move bodies) stay where they were cast.
/// </summary>
[TestFixture]
[TestOf(typeof(ProperKungFuSystem))]
public sealed class ProperKungFuV131Test
{
    /// <summary>
    /// On a moved and rotated grid, with the user off its origin (and optionally holding the victim in a hard grab):
    /// until the final palm, the user stays within the dash of the cast spot and the victim within 1.5 tiles of where
    /// it stood; afterwards only the victim flies (about 20 tiles) and the user ends near the combo.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task JudgmentStaysInPlaceTest(bool grabbed)
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var actions = entMan.System<ActionsSystem>();
        var xform = entMan.System<SharedTransformSystem>();
        var pulling = entMan.System<PullingSystem>();

        EntityUid user = default;
        EntityUid victim = default;
        var userStart = Vector2.Zero;
        var victimStart = Vector2.Zero;
        var distance = grabbed ? 1.0f : 2.0f;

        await server.WaitPost(() =>
        {
            // Off the map origin and turned, so local and world coordinates differ.
            xform.SetWorldPositionRotation(map.Grid.Owner, new Vector2(37.3f, -21.7f), Angle.FromDegrees(30));
            Floor(entMan, tileDefs, map, 32);
            user = Learn(entMan, new EntityCoordinates(map.Grid, 6.5f, 4.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 6.5f, 4.5f + distance));
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            if (grabbed)
            {
                Assert.That(pulling.TryStartPull(user, victim, grabStageOverride: GrabStage.Hard), "Grabbed");
                Assert.That(pulling.IsPulling(user));
            }

            userStart = xform.GetWorldPosition(user);
            victimStart = xform.GetWorldPosition(victim);

            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            kungFu.UltimateCharge = kungFu.MaxUltimateCharge;
            actions.PerformAction(user, FindAction(entMan, user, "ActionProperKungFuUltimate"), predicted: false);
            Assert.That(kungFu.UltimateActive);

            userStart = xform.GetWorldPosition(user);
            victimStart = xform.GetWorldPosition(victim);
            var judgment = FindAction(entMan, user, "ActionProperKungFuWolfJudgment");
            Assert.That(actions.SetEventTarget(judgment, victim));
            actions.PerformAction(user, judgment, predicted: false);
            Assert.That(entMan.HasComponent<ProperKungFuJudgmentComponent>(user), "Judgment started");
        });

        var maxUser = 0f;
        var maxVictim = 0f;
        var reachedPalm = false;
        var palmCenter = Vector2.Zero;
        var log = new List<string>();
        for (var i = 0; i < timing.TickRate * 8; i++)
        {
            await server.WaitRunTicks(1);
            var done = false;
            await server.WaitPost(() =>
            {
                var u = xform.GetWorldPosition(user);
                var v = xform.GetWorldPosition(victim);
                var stage = entMan.TryGetComponent<ProperKungFuJudgmentComponent>(user, out var comp) ? comp.Stage : -1;
                if (i % 5 == 0)
                    log.Add($"t{i} s{stage} u{(u - userStart).Length():0.0} v{(v - victimStart).Length():0.0}");

                if (!reachedPalm && stage is >= 0 and < 5)
                {
                    maxUser = Math.Max(maxUser, (u - userStart).Length());
                    maxVictim = Math.Max(maxVictim, (v - victimStart).Length());
                }
                else if (!reachedPalm && stage >= 5)
                {
                    reachedPalm = true;
                    palmCenter = comp!.Center;
                }

                done = stage == -1 && i > timing.TickRate * 4;
            });

            if (done)
                break;
        }

        // Let the 20-tile knockback finish before measuring it.
        for (var i = 0; i < timing.TickRate * 4; i++)
        {
            await server.WaitRunTicks(1);
            var sliding = true;
            await server.WaitPost(() => sliding = entMan.HasComponent<ProperKungFuSlidingComponent>(victim));
            if (!sliding)
                break;
        }

        TestContext.Out.WriteLine(string.Join("\n", log));

        await server.WaitAssertion(() =>
        {
            Assert.That(reachedPalm, "The full combo reached the double palm");
            Assert.That(maxVictim, Is.LessThanOrEqualTo(1.5f), "The victim stays within 1.5 tiles until the palm");
            Assert.That(maxUser, Is.LessThanOrEqualTo(distance + 1.6f), "The user stays at the dash and the star around the victim");
            Assert.That((palmCenter - victimStart).Length(), Is.LessThanOrEqualTo(1.5f), "The combo happens where the victim stood");

            var user2 = xform.GetWorldPosition(user);
            Assert.That((user2 - palmCenter).Length(), Is.LessThanOrEqualTo(3.5f), "The user ends near the combo");
            var flown = (xform.GetWorldPosition(victim) - palmCenter).Length();
            Assert.That(flown, Is.InRange(14f, 22f), "Only the final palm sends the victim far");
            Assert.That(pulling.IsPulling(user), Is.False, "The grab is released");
        });

        await pair.CleanReturnAsync();
    }

    private static void Floor(IEntityManager entMan, ITileDefinitionManager tileDefs, Content.IntegrationTests.Pair.TestMapData map, int half)
    {
        var mapSys = entMan.System<SharedMapSystem>();
        var tile = new Tile(tileDefs["Plating"].TileId);
        for (var x = -half; x <= half; x++)
        for (var y = -half; y <= half; y++)
        {
            mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
        }
    }

    private static Entity<ActionComponent> FindAction(IEntityManager entMan, EntityUid user, string proto)
    {
        var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
        foreach (var uid in kungFu.ActionEntities.Concat(kungFu.UltimateActionEntities))
        {
            if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID == proto)
                return (uid, entMan.GetComponent<ActionComponent>(uid));
        }

        Assert.Fail($"{proto} not granted");
        return default;
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
        entMan.RemoveComponent<RespiratorComponent>(mob);
        return mob;
    }
}

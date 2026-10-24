using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Server.Actions;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Actions.Components;
using Content.Shared.Damage;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction.Events;
using Content.Shared.Maps;
using Content.Shared.Stunnable;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._CorvaxGoob.ProperKungFu;

[TestFixture]
[TestOf(typeof(ProperKungFuComponent))]
public sealed class ProperKungFuTest
{
    [Test]
    public async Task WolfDanceTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var actions = entMan.System<ActionsSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid user = default;
        EntityUid target = default;
        EntityUid shieldTarget = default;
        EntityUid shield = default;
        EntityUid book = default;
        Entity<ActionComponent> action = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -2; x <= 20; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, 4.5f, 0.5f));
            book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);

            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<MartialArtsKnowledgeComponent>(user, out var knowledge));
            Assert.That(knowledge!.MartialArtsForm, Is.EqualTo(MartialArtsForms.ProperKungFu));
            Assert.That(entMan.TryGetComponent<ProperKungFuComponent>(user, out var kungFu));
            Assert.That(kungFu!.ActionEntities, Has.Count.EqualTo(1));
            Assert.That(entMan.Deleted(book), "The manual should be consumed");

            var actionUid = kungFu.ActionEntities[0];
            action = (actionUid, entMan.GetComponent<ActionComponent>(actionUid));
            Assert.That(entMan.GetComponent<MetaDataComponent>(actionUid).EntityPrototype!.ID, Is.EqualTo("ActionProperKungFuWolfDance"));

            Assert.That(actions.SetEventTarget(actionUid, target));
            actions.PerformAction(user, action, predicted: false);

            var userPos = xformSys.GetWorldPosition(user);
            var targetPos = xformSys.GetWorldPosition(target);
            Assert.That((targetPos - userPos).Length(), Is.LessThan(1.2f), "User should land next to the target");

            var damage = entMan.GetComponent<DamageableComponent>(target);
            Assert.That(damage.TotalDamage.Float(), Is.EqualTo(20f).Within(0.5f));
            Assert.That(entMan.HasComponent<StunnedComponent>(target), "Target should be stunned");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Target should be knocked down");
            Assert.That(action.Comp.Cooldown, Is.Not.Null, "Cooldown should start");
            Assert.That(kungFu.UltimateCharge, Is.EqualTo(5f), "Special hit should charge the ultimate meter");
        });

        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var targetX = xformSys.GetWorldPosition(target).X;
            Assert.That(targetX, Is.GreaterThan(6.5f), "Target should be knocked back away from the user");
        });

        // Shield check.
        await server.WaitPost(() =>
        {
            shieldTarget = entMan.SpawnEntity("MobHuman", new EntityCoordinates(map.Grid, 0.5f, -2.5f));
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(shieldTarget) + new Vector2(-4f, 0f));
            shield = entMan.SpawnEntity("RiotShield", new EntityCoordinates(map.Grid, 0.5f, -2.5f));
            Assert.That(hands.TryPickupAnyHand(shieldTarget, shield), Is.True);

            actions.ClearCooldown(action.Owner);
            Assert.That(actions.SetEventTarget(action, shieldTarget));
            actions.PerformAction(user, action, predicted: false);

            var damage = entMan.GetComponent<DamageableComponent>(shieldTarget);
            Assert.That(damage.TotalDamage.Float(), Is.EqualTo(0f), "Shield should fully block the kick");
            Assert.That(entMan.HasComponent<StunnedComponent>(shieldTarget), Is.False);
            Assert.That(hands.EnumerateHeld(shieldTarget).Contains(shield), Is.False, "Shield should be knocked out of hands");
        });

        await pair.CleanReturnAsync();
    }
}

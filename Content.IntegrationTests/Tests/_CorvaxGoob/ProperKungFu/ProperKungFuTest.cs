using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Goobstation.Shared.Sprinting;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Temperature.Components;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Actions.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Components;
using Content.Shared.Movement.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Standing;
using Content.Shared.StatusEffect;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Events;
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
        EntityUid missTarget = default;
        EntityUid stunTarget = default;
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

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 4.5f, 0.5f));
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

            // The move is not instant any more: the user is now rolling.
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), "User should be performing the Wolf Dance");
            Assert.That(entMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.EqualTo(0f), "Kick should not land instantly");
            Assert.That(action.Comp.Cooldown, Is.Not.Null, "Cooldown should start");
        });

        // Mid-roll (~0.2 s): moved some of the way, but not next to the target yet.
        await server.WaitRunTicks(6);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing));
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.Roll));
            var userX = xformSys.GetWorldPosition(user).X;
            Assert.That(userX, Is.GreaterThan(0.7f).And.LessThan(3f), "User should be partway through the roll");
        });

        // Roll (0.45 s) + dash (0.25 s) = 0.7 s, ~21 ticks at 30 tps.
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Wolf Dance should be finished");

            var damage = entMan.GetComponent<DamageableComponent>(target);
            Assert.That(Blunt(damage), Is.EqualTo(20f).Within(0.5f),
                string.Join(", ", damage.Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")) + " | user: " + string.Join(", ", entMan.GetComponent<DamageableComponent>(user).Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));
            Assert.That(entMan.HasComponent<StunnedComponent>(target), "Target should be stunned");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Target should be knocked down");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f), "Special hit should charge the ultimate meter");
        });

        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var targetX = xformSys.GetWorldPosition(target).X;
            Assert.That(targetX, Is.InRange(6.5f, 8.5f), $"Target should be knocked back ~3 tiles, x = {targetX}");
        });

        // Shield check.
        await server.WaitPost(() =>
        {
            shieldTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 14.5f, -2.5f));
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(shieldTarget) + new Vector2(-4f, 0f));
            shield = entMan.SpawnEntity("RiotShield", new EntityCoordinates(map.Grid, 14.5f, -2.5f));
            Assert.That(hands.TryPickupAnyHand(shieldTarget, shield), Is.True);

            actions.ClearCooldown(action.Owner);
            Assert.That(actions.SetEventTarget(action, shieldTarget));
            actions.PerformAction(user, action, predicted: false);
        });

        await server.WaitRunTicks(35);

        await server.WaitAssertion(() =>
        {
            var userPos = xformSys.GetWorldPosition(user);
            var targetPos = xformSys.GetWorldPosition(shieldTarget);
            Assert.That((targetPos - userPos).Length(), Is.LessThan(1.5f), "User should end up next to the target");

            var damage = entMan.GetComponent<DamageableComponent>(shieldTarget);
            Assert.That(Blunt(damage), Is.EqualTo(0f), "Shield should fully block the kick");
            Assert.That(entMan.HasComponent<StunnedComponent>(shieldTarget), Is.False);
            Assert.That(hands.EnumerateHeld(shieldTarget).Contains(shield), Is.False, "Shield should be knocked out of hands");
        });

        // Target escapes during the roll: the kick whiffs.
        await server.WaitPost(() =>
        {
            missTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 6.5f, 2.5f));
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(missTarget) + new Vector2(-4f, 0f));

            actions.ClearCooldown(action.Owner);
            Assert.That(actions.SetEventTarget(action, missTarget));
            actions.PerformAction(user, action, predicted: false);
        });

        await server.WaitRunTicks(4);

        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(missTarget, xformSys.GetWorldPosition(missTarget) + new Vector2(8f, 0f));
        });

        await server.WaitRunTicks(35);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False);
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(missTarget)), Is.EqualTo(0f), "Kick should miss an escaped target");
            Assert.That(entMan.HasComponent<StunnedComponent>(missTarget), Is.False);
        });

        // Stunned mid-move: the move is cancelled.
        await server.WaitPost(() =>
        {
            stunTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 18.5f, 0.5f));
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(stunTarget) + new Vector2(-4f, 0f));

            actions.ClearCooldown(action.Owner);
            Assert.That(actions.SetEventTarget(action, stunTarget));
            actions.PerformAction(user, action, predicted: false);
        });

        await server.WaitRunTicks(4);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user));
            entMan.System<SharedStunSystem>().TryParalyze(user, TimeSpan.FromSeconds(3), true);
        });

        await server.WaitRunTicks(35);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Stun should cancel the Wolf Dance");
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(stunTarget)), Is.EqualTo(0f));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PassivesAndClawSeriesTest()
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
        var melee = entMan.System<SharedMeleeWeaponSystem>();
        var speed = entMan.System<MovementSpeedModifierSystem>();

        EntityUid user = default;
        EntityUid target = default;
        Entity<ActionComponent> action = default;
        float baseStamina = 0f, baseRate = 0f, baseWalk = 0f, baseSprintingSpeed = 0f;
        Vector2 targetPosBeforeCombo = default;
        string baseAnim = "", baseWide = "", baseMiss = "";

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -2; x <= 20; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));

            baseStamina = entMan.GetComponent<StaminaComponent>(user).CritThreshold;
            baseRate = melee.GetAttackRate(user, user);
            baseWalk = entMan.GetComponent<MovementSpeedModifierComponent>(user).CurrentWalkSpeed;
            var baseFist = entMan.GetComponent<MeleeWeaponComponent>(user);
            (baseAnim, baseWide, baseMiss) = (baseFist.Animation.Id, baseFist.WideAnimation.Id, baseFist.MissAnimation.Id);
            Assert.That(baseAnim, Is.EqualTo("WeaponArcFist"));

            var sprinter = entMan.GetComponent<SprinterComponent>(user);
            sprinter.IsSprinting = true;
            speed.RefreshMovementSpeedModifiers(user);
            baseSprintingSpeed = entMan.GetComponent<MovementSpeedModifierComponent>(user).CurrentSprintSpeed;
            sprinter.IsSprinting = false;
            speed.RefreshMovementSpeedModifiers(user);

            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuComponent>(user));

            // Passives.
            Assert.That(entMan.GetComponent<StaminaComponent>(user).CritThreshold, Is.EqualTo(baseStamina + 10f).Within(0.01f));
            Assert.That(melee.GetAttackRate(user, user), Is.EqualTo(baseRate * 1.5f).Within(0.01f));
            Assert.That(entMan.GetComponent<MovementSpeedModifierComponent>(user).CurrentWalkSpeed, Is.EqualTo(baseWalk).Within(0.01f),
                "Walking must not get faster");

            var sprinter = entMan.GetComponent<SprinterComponent>(user);
            sprinter.IsSprinting = true;
            speed.RefreshMovementSpeedModifiers(user);
            Assert.That(entMan.GetComponent<MovementSpeedModifierComponent>(user).CurrentSprintSpeed,
                Is.EqualTo(baseSprintingSpeed * 1.15f).Within(0.01f));
            sprinter.IsSprinting = false;
            speed.RefreshMovementSpeedModifiers(user);

            // Fist arcs are replaced by the wolf claw scratch.
            var fist = entMan.GetComponent<MeleeWeaponComponent>(user);
            Assert.That(fist.Animation.Id, Is.EqualTo("WeaponArcProperKungFuClaw"));
            Assert.That(fist.WideAnimation.Id, Is.EqualTo("WeaponArcProperKungFuClaw"));
            Assert.That(fist.MissAnimation.Id, Is.EqualTo("WeaponArcProperKungFuClaw"));

            // Guns are forbidden like Sleeping Carp.
            var shot = new ShotAttemptedEvent { User = user };
            entMan.EventBus.RaiseLocalEvent(user, ref shot);
            Assert.That(shot.Cancelled, "Proper Kung Fu must forbid guns");

            // Claw Series: Harm, Harm, Harm.
            targetPosBeforeCombo = xformSys.GetWorldPosition(target);
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Harm));
            }

            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(target)), Is.EqualTo(10f).Within(0.5f),
                "Claw Series bonus damage (fist hits themselves are not simulated here)");
            Assert.That(entMan.HasComponent<StunnedComponent>(target), "Claw Series stuns");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), Is.False, "Claw Series stun keeps the target standing");
            Assert.That(entMan.System<StandingStateSystem>().IsDown(target), Is.False);

            var flashes = 0;
            var metaQuery = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (metaQuery.MoveNext(out var meta))
            {
                if (meta.EntityPrototype?.ID == "ProperKungFuPalmFlash")
                    flashes++;
            }

            Assert.That(flashes, Is.EqualTo(1), "One finisher flash");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(target), "Claw Series marks the target");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f));
        });

        await server.WaitRunTicks(15);

        await server.WaitPost(() =>
        {
            var moved = xformSys.GetWorldPosition(target).X - targetPosBeforeCombo.X;
            Assert.That(moved, Is.EqualTo(3f).Within(0.15f), $"Claw Series slide, moved {moved}");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(target), "Mark still alive after 0.5 s");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), Is.False, "Slides back on its feet");

            // Remove the Claw Series stun so we can check that the synergy kick itself does not stun.
            entMan.System<StatusEffectsSystem>().TryRemoveStatusEffect(target, "Stun");
            Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False);

            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            var actionUid = kungFu.ActionEntities[0];
            action = (actionUid, entMan.GetComponent<ActionComponent>(actionUid));
            targetPosBeforeCombo = xformSys.GetWorldPosition(target);
            Assert.That(actions.SetEventTarget(action, target));
            actions.PerformAction(user, action, predicted: false);
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user));
        });

        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False);
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(target)), Is.EqualTo(40f).Within(0.5f),
                $"10 (Claw Series) + 30 (synergy kick); user {xformSys.GetWorldPosition(user)} target {xformSys.GetWorldPosition(target)} start {targetPosBeforeCombo} mark {entMan.HasComponent<ProperKungFuClawMarkComponent>(target)}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Synergy kick knocks down");
            Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False, "Synergy kick must not stun");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(target), Is.False, "Mark is consumed");
        });

        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(target).X - targetPosBeforeCombo.X;
            // The user ends next to the target and kicks it 5 tiles further (the normal kick throws 3).
            Assert.That(moved, Is.InRange(4.5f, 6f), $"Synergy knockback, moved {moved}");

            // Losing the art removes the passives.
            entMan.RemoveComponent<ProperKungFuComponent>(user);
            Assert.That(entMan.GetComponent<StaminaComponent>(user).CritThreshold, Is.EqualTo(baseStamina).Within(0.01f));
            Assert.That(melee.GetAttackRate(user, user), Is.EqualTo(baseRate).Within(0.01f));
            var fistAfter = entMan.GetComponent<MeleeWeaponComponent>(user);
            Assert.That(fistAfter.Animation.Id, Is.EqualTo(baseAnim), "Fist animation restored");
            Assert.That(fistAfter.WideAnimation.Id, Is.EqualTo(baseWide));
            Assert.That(fistAfter.MissAnimation.Id, Is.EqualTo(baseMiss));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The move takes real time now and the test map is a vacuum, so strip environmental damage
    /// (barotrauma is Blunt) to keep the damage asserts exact.
    /// </summary>
    private static EntityUid SpawnMob(IEntityManager entMan, EntityCoordinates coords)
    {
        var mob = entMan.SpawnEntity("MobHuman", coords);
        entMan.RemoveComponent<BarotraumaComponent>(mob);
        entMan.RemoveComponent<TemperatureComponent>(mob);
        return mob;
    }

    private static float Blunt(DamageableComponent damage)
    {
        return damage.Damage.DamageDict.TryGetValue("Blunt", out var blunt) ? blunt.Float() : 0f;
    }
}

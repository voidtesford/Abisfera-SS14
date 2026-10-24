using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.MartialArts;
using Content.Goobstation.Shared.MartialArts.Components;
using Content.Goobstation.Shared.Sprinting;
using Content.Server.Actions;
using Content.Server.Atmos.Components;
using Content.Server.Body.Components;
using Content.Server.Body.Systems;
using Content.Server.Temperature.Components;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared._Shitmed.Medical.Surgery.Traumas;
using Content.Shared._Shitmed.Targeting;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.ActionBlocker;
using Content.Shared.Body.Components;
using Content.Shared.Body.Part;
using Content.Shared.CombatMode;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Body.Systems;
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
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared._Shitmed.Body.Organ;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Organ;
using Content.Shared.Stunnable;
using Content.Server._CorvaxGoob.ProperKungFu;
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
            Assert.That(kungFu!.ActionEntities, Has.Count.EqualTo(4));
            Assert.That(entMan.Deleted(book), "The manual should be consumed");

            var actionUid = kungFu.ActionEntities[0];
            action = (actionUid, entMan.GetComponent<ActionComponent>(actionUid));
            Assert.That(entMan.GetComponent<MetaDataComponent>(actionUid).EntityPrototype!.ID, Is.EqualTo("ActionProperKungFuWolfDance"));

            Assert.That(actions.SetEventTarget(actionUid, target));
            actions.PerformAction(user, action, predicted: false);

            // The move is not instant: the user is now dashing at the target.
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), "User should be performing the Wolf Dance");
            Assert.That(entMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(), Is.EqualTo(0f), "Kick should not land instantly");
            Assert.That(action.Comp.Cooldown, Is.Not.Null, "Cooldown should start");
        });

        // Mid-dash (~0.2 s of 0.35 s): moved some of the way along a straight line, not at the target yet.
        await server.WaitRunTicks(6);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing));
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.PassThrough), "Plain Wolf Dance: a fast dash through the target");
            var userPos = xformSys.GetWorldPosition(user);
            Assert.That(userPos.X, Is.GreaterThan(0.7f).And.LessThan(4.2f), $"User should be partway through the dash, x = {userPos.X}");
            Assert.That(userPos.Y, Is.EqualTo(0.5f).Within(0.05f), "Straight line, no side drift");
        });

        // Flight 0.35 s, ~11 ticks at 30 tps.
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Wolf Dance should be finished");

            var damage = entMan.GetComponent<DamageableComponent>(target);
            Assert.That(Blunt(damage), Is.EqualTo(20f).Within(0.5f),
                string.Join(", ", damage.Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")) + " | user: " + string.Join(", ", entMan.GetComponent<DamageableComponent>(user).Damage.DamageDict.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key}={kv.Value}")));
            AssertTorsoHit(entMan, target, 20f, "Wolf Dance kick");
            Assert.That(entMan.HasComponent<StunnedComponent>(target), "Target should be stunned");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Target lands knocked down after the toss");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f), "Special hit should charge the ultimate meter");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), Is.False, "The user stays on their feet in the plain variant");
            Assert.That(entMan.HasComponent<ProperKungFuWolfDanceMarkComponent>(target), "Wolf Dance mark for the Hammer");
            var userX = xformSys.GetWorldPosition(user).X;
            Assert.That(userX, Is.EqualTo(5.5f).Within(0.3f), $"The user passes through and stops ~1 tile past the target, x = {userX}");
            var targetX = xformSys.GetWorldPosition(target).X;
            Assert.That(targetX, Is.EqualTo(4.5f).Within(0.2f), $"Tossed in place, x = {targetX}");
        });

        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var targetX = xformSys.GetWorldPosition(target).X;
            Assert.That(targetX, Is.EqualTo(4.5f).Within(0.2f), $"The target lands on the same spot, x = {targetX}");
            Assert.That(entMan.HasComponent<ProperKungFuWolfDanceMarkComponent>(target), Is.False, "Mark lasts 2 s");
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
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), Is.False, "The user stays up after a blocked plain dash");
        });

        // The user is still on the floor after the blocked kick (1.5 s).
        await server.WaitRunTicks(50);

        // Target escapes during the flight: the kick whiffs.
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

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), Is.False, "A missed plain dash doesn't floor the user");
        });

        await server.WaitRunTicks(50);

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
            AssertTorsoHit(entMan, target, 10f, "Claw Series bonus");
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
            AssertTorsoHit(entMan, target, 40f, "Claw Series + Wolf Dance synergy kick");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Synergy kick knocks down");
            Assert.That(entMan.HasComponent<StunnedComponent>(target), Is.False, "Synergy kick must not stun");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(target), Is.False, "Mark is consumed");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), "The user lands on the floor after the synergy kick");
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

    [Test]
    public async Task ClawWhirlwindTest()
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
        var blocker = entMan.System<ActionBlockerSystem>();
        var body = entMan.System<SharedBodySystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid user = default;
        EntityUid target = default;
        EntityUid farTarget = default;
        EntityUid comboTarget = default;
        Entity<ActionComponent> whirlwind = default;
        Entity<ActionComponent> wolfDance = default;
        Vector2 comboStart = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 22; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            farTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 5.5f, 2.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            foreach (var uid in kungFu.ActionEntities)
            {
                var id = entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID;
                if (id == "ActionProperKungFuClawWhirlwind")
                    whirlwind = (uid, entMan.GetComponent<ActionComponent>(uid));
                else if (id == "ActionProperKungFuWolfDance")
                    wolfDance = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            Assert.That(whirlwind.Owner.IsValid(), "Claw Whirlwind is granted with the art");
            Assert.That(whirlwind.Comp.UseDelay, Is.EqualTo(TimeSpan.FromSeconds(30)), "30 s cooldown");

            // Out of fist reach: nothing happens and no cooldown.
            Assert.That(actions.SetEventTarget(whirlwind, farTarget));
            actions.PerformAction(user, whirlwind, predicted: false);
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(farTarget), Is.False, "Fist range is enforced");
            Assert.That(whirlwind.Comp.Cooldown, Is.Null);

            Assert.That(blocker.CanMove(target), "Free before the move");
            Assert.That(actions.SetEventTarget(whirlwind, target));
            actions.PerformAction(user, whirlwind, predicted: false);
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(target), "Target is grabbed");
            Assert.That(blocker.CanMove(target), Is.False, "Target can't move during the whirlwind");
            Assert.That(whirlwind.Comp.Cooldown, Is.Not.Null);
            var cd = whirlwind.Comp.Cooldown!.Value;
            Assert.That((cd.End - cd.Start).TotalSeconds, Is.EqualTo(30).Within(0.01));
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(target)), Is.EqualTo(0f), "Knee comes after the throw");
        });

        // Throw (0.5 s) done, knee landed, now in the air.
        await server.WaitRunTicks(21);

        await server.WaitAssertion(() =>
        {
            var targetX = xformSys.GetWorldPosition(target).X;
            Assert.That(targetX, Is.LessThan(0.2f), $"Thrown over the user to the other side, x = {targetX}");
            Assert.That(entMan.GetComponent<ClawWhirlwindedComponent>(target).Phase, Is.EqualTo(ClawWhirlwindPhase.Air));
            Assert.That(blocker.CanMove(target), Is.False);

            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(target)), Is.EqualTo(10f).Within(0.5f), "10 Blunt");
            var head = body.GetBodyChildrenOfType(target, BodyPartType.Head).First().Id;
            AssertTorsoHit(entMan, target, 10f, "Knee");

            var bone = entMan.GetComponent<WoundableComponent>(head).Bone.ContainedEntities.First();
            Assert.That(entMan.GetComponent<BoneComponent>(bone).BoneSeverity, Is.EqualTo(BoneSeverity.Broken), "Skull is broken");

            var brain = body.GetPartOrgans(head).First(o => entMan.HasComponent<BrainComponent>(o.Id));
            Assert.That(brain.Component.OrganIntegrity.Float(), Is.LessThan(brain.Component.IntegrityCap.Float()), "Brain trauma");
            Assert.That(brain.Component.OrganIntegrity.Float(), Is.GreaterThan(0f), "The brain is hurt, not destroyed");

            Assert.That(entMan.HasComponent<ProperKungFuWhirlwindMarkComponent>(target), "Whirlwind mark");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
        });

        // 2 s total: landed and released.
        await server.WaitRunTicks(45);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(target), Is.False, "Released after ~2 s");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Lands knocked down");
        });

        await server.WaitRunTicks(75);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), Is.False);
            Assert.That(blocker.CanMove(target), "Target can move again");
        });

        // Synergy: Claw Whirlwind, then Wolf Dance from 3 tiles away. Also carries a Claw Series mark: Whirlwind wins.
        await server.WaitPost(() =>
        {
            comboTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 11.5f, -2.5f));
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(comboTarget) + new Vector2(-1f, 0f));
            actions.ClearCooldown(whirlwind.Owner);
            Assert.That(actions.SetEventTarget(whirlwind, comboTarget));
            actions.PerformAction(user, whirlwind, predicted: false);
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(comboTarget));
        });

        await server.WaitRunTicks(18);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuWhirlwindMarkComponent>(comboTarget));
            var clawMark = entMan.EnsureComponent<ProperKungFuClawMarkComponent>(comboTarget);
            clawMark.Marker = user;
            clawMark.ExpiresAt = timing.CurTime + TimeSpan.FromSeconds(2);

            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(comboTarget) + new Vector2(-3f, 0f));
            comboStart = xformSys.GetWorldPosition(comboTarget);
            actions.ClearCooldown(wolfDance.Owner);
            Assert.That(actions.SetEventTarget(wolfDance, comboTarget));
            actions.PerformAction(user, wolfDance, predicted: false);

            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing));
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.Dash), "One straight flying kick");
            Assert.That(dancing.WhirlwindVariant);
            Assert.That(entMan.HasComponent<ProperKungFuWhirlwindMarkComponent>(comboTarget), Is.False, "Mark consumed");
        });

        await server.WaitRunTicks(12);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Fast dash is over");
            var damage = entMan.GetComponent<DamageableComponent>(comboTarget);
            // The skull is already broken and the brain hurt: Shitmed may still roll a dismemberment
            // (up to 70%), which moves extra damage around. Exact numbers only make sense with the head on.
            var headAttached = body.GetBodyChildrenOfType(comboTarget, BodyPartType.Head).Any();
            if (headAttached)
            {
                Assert.That(Blunt(damage), Is.EqualTo(25f).Within(0.5f), "10 (knee) + 15 Blunt");
                AssertTorsoHit(entMan, comboTarget, 25f, "Knee + Whirlwind dash kick");
                Assert.That(damage.Damage.DamageDict["Slash"].Float(), Is.EqualTo(15f).Within(0.5f), "15 Slash");
            }
            else
            {
                TestContext.WriteLine("Wolf Dance after Claw Whirlwind took the head off (random Shitmed dismemberment).");
                Assert.That(damage.Damage.DamageDict["Slash"].Float(), Is.GreaterThanOrEqualTo(15f), "At least 15 Slash");
            }
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), "The user falls after the hit");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(comboTarget), "Target is knocked down");
            Assert.That(entMan.HasComponent<ProperKungFuClawMarkComponent>(comboTarget), Is.False, "Claw mark consumed too");
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(comboTarget), Is.False, "The kick takes the target out of the whirlwind");
        });

        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(comboTarget).X - comboStart.X;
            Assert.That(moved, Is.EqualTo(5f).Within(0.3f), $"Flies 5 tiles, moved {moved}");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Plain Claw Whirlwind finisher: a target with at least 80 damage before the whirlwind gets a huge ascending
    /// and a huge descending claw slash in the air (90 Slash to the torso each) and is slammed into the floor.
    /// With 79 it is the normal whirlwind.
    /// </summary>
    [Test]
    public async Task ClawWhirlwindFinisherTest()
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
        var blocker = entMan.System<ActionBlockerSystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid user = default;
        EntityUid weak = default;
        EntityUid victim = default;
        Entity<ActionComponent> whirlwind = default;
        Vector2 slamPos = default;

        void PreDamage(EntityUid uid, float amount)
        {
            // Off the torso (it has its own damage cap and takes the 180 Slash), whole numbers so the total is exact.
            var third = MathF.Floor(amount / 3f);
            var parts = new[] { (TargetBodyPart.LeftLeg, third), (TargetBodyPart.RightLeg, third), (TargetBodyPart.Groin, amount - 2 * third) };
            foreach (var (part, value) in parts)
            {
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Blunt", value);
                damageSys.TryChangeDamage(uid, dmg, targetPart: part, canMiss: false);
            }
        }

        float Total(EntityUid uid, string type)
        {
            return entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict.TryGetValue(type, out var v) ? v.Float() : 0f;
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -4; x <= 4; x++)
            for (var y = -3; y <= 12; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            weak = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 9.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            PreDamage(victim, 80f);
            PreDamage(weak, 79f);
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.EqualTo(80f).Within(0.01f));
            foreach (var uid in entMan.GetComponent<ProperKungFuComponent>(user).ActionEntities)
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID == "ActionProperKungFuClawWhirlwind")
                    whirlwind = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            Assert.That(actions.SetEventTarget(whirlwind, victim));
            actions.PerformAction(user, whirlwind, predicted: false);
            Assert.That(entMan.TryGetComponent<ClawWhirlwindedComponent>(victim, out var held));
            Assert.That(held!.Finisher, "80 before the whirlwind: finisher");
            Assert.That(whirlwind.Comp.Cooldown, Is.Not.Null, "Normal cooldown");
        });

        // Throw done, knee landed, in the air, no slash yet.
        await server.WaitRunTicks(21);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<ClawWhirlwindedComponent>(victim).Phase, Is.EqualTo(ClawWhirlwindPhase.Air));
            Assert.That(Total(victim, "Blunt"), Is.EqualTo(90f).Within(0.5f), "80 + 10 Blunt knee");
            Assert.That(Total(victim, "Slash"), Is.EqualTo(0f));
            Assert.That(entMan.HasComponent<ProperKungFuWhirlwindMarkComponent>(victim), "Synergy mark still set");
        });

        // Ascending slash (0.4 s into the air).
        await server.WaitRunTicks(11);

        await server.WaitAssertion(() =>
        {
            var held = entMan.GetComponent<ClawWhirlwindedComponent>(victim);
            Assert.That(held.FinisherSlashes, Is.EqualTo(1), "Ascending slash");
            Assert.That(held.Phase, Is.EqualTo(ClawWhirlwindPhase.Air), "Still in the air");
            Assert.That(Total(victim, "Slash"), Is.EqualTo(90f).Within(0.5f), "90 Slash");
            Assert.That(blocker.CanMove(victim), Is.False);
        });

        // Descending slash (0.75 s into the air) slams the target down.
        await server.WaitRunTicks(9);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.TryGetComponent<ClawWhirlwindedComponent>(victim, out var held), "Still held until the usual release");
            Assert.That(held!.FinisherSlashes, Is.EqualTo(2), "Descending slash");
            Assert.That(held.Landed, "Slammed into the floor");
            Assert.That(Total(victim, "Slash"), Is.EqualTo(180f).Within(0.5f), "90 + 90 Slash");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Slash"), Is.EqualTo(180f).Within(0.5f), "All on the torso");
            Assert.That(Total(victim, "Blunt"), Is.EqualTo(90f).Within(0.5f), "Only the 10 Blunt knee on top");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Knocked down");
            Assert.That(blocker.CanMove(victim), Is.False);
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
            slamPos = xformSys.GetWorldPosition(victim);
        });

        // No knockback, and 4 s on the floor (the normal landing is 2 s).
        await server.WaitRunTicks(75);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(victim), Is.False, "Released");
            Assert.That((xformSys.GetWorldPosition(victim) - slamPos).Length(), Is.LessThan(0.05f), "No knockback");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Still down after 2.5 s (4 s knockdown)");
        });

        // ---- 79 before the whirlwind: the normal whirlwind ----
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(weak) + new Vector2(-1f, 0f));
            actions.ClearCooldown(whirlwind.Owner);
            Assert.That(actions.SetEventTarget(whirlwind, weak));
            actions.PerformAction(user, whirlwind, predicted: false);
            Assert.That(entMan.GetComponent<ClawWhirlwindedComponent>(weak).Finisher, Is.False, "79: no finisher");
        });

        await server.WaitRunTicks(50);

        await server.WaitAssertion(() =>
        {
            Assert.That(Total(weak, "Slash"), Is.EqualTo(0f), "No slashes");
            Assert.That(Total(weak, "Blunt"), Is.EqualTo(89f).Within(0.5f), "Only the knee");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(weak), "Normal landing");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WolfGripTest()
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
        var body = entMan.System<SharedBodySystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var pulling = entMan.System<PullingSystem>();
        var respirator = entMan.System<RespiratorSystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid user = default;
        EntityUid target = default;
        Entity<ActionComponent> whirlwind = default;
        Vector2 strikeStart = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 22; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            target = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        // Harm, Disarm, then a real grab in combat mode.
        await server.WaitPost(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            foreach (var uid in kungFu.ActionEntities)
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID == "ActionProperKungFuClawWhirlwind")
                    whirlwind = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            combat.SetInCombatMode(user, true);
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Harm));
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Disarm));
            Assert.That(entMan.HasComponent<KravMagaBlockedBreathingComponent>(target), Is.False);
            Assert.That(pulling.TryStartPull(user, target), "The grab itself works");
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var damage = entMan.GetComponent<DamageableComponent>(target);
            Assert.That(damage.Damage.DamageDict["Slash"].Float(), Is.EqualTo(15f).Within(0.5f), "15 Slash");

            Assert.That(entMan.TryGetComponent<KravMagaBlockedBreathingComponent>(target, out var blocked), "Can't breathe");
            Assert.That((blocked!.BlockedTime - timing.CurTime).TotalSeconds, Is.EqualTo(10).Within(0.2), "for 10 s");
            Assert.That(respirator.CanBreathe(target, entMan.GetComponent<RespiratorComponent>(target)), Is.False);

            var puller = entMan.GetComponent<PullerComponent>(user);
            var pullable = entMan.GetComponent<PullableComponent>(target);
            Assert.That(puller.Pulling, Is.EqualTo(target));
            Assert.That(puller.GrabStage, Is.EqualTo(GrabStage.Hard), "Hard grab (user side)");
            Assert.That(pullable.GrabStage, Is.EqualTo(GrabStage.Hard), "Hard grab (target side)");

            var offset = xformSys.GetWorldPosition(target) - xformSys.GetWorldPosition(user);
            Assert.That(offset.Length(), Is.EqualTo(0.6f).Within(0.1f), $"Held right in front, offset {offset}");
            Assert.That(offset.X, Is.GreaterThan(0.4f), "In front, on the target's side");

            Assert.That(entMan.TryGetComponent<ProperKungFuWolfGripMarkComponent>(target, out var mark), "Wolf Grip mark");
            Assert.That(mark!.Marker, Is.EqualTo(user));
            Assert.That((mark.ExpiresAt - timing.CurTime).TotalSeconds, Is.EqualTo(2).Within(0.1));
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
            Assert.That(entMan.GetComponent<CanPerformComboComponent>(user).LastAttacks, Is.Empty, "No leftover chain");
        });

        // Within 2 s: Claw Whirlwind becomes the two-handed cross strike.
        await server.WaitRunTicks(15);

        await server.WaitAssertion(() =>
        {
            strikeStart = xformSys.GetWorldPosition(target);
            Assert.That(actions.SetEventTarget(whirlwind, target));
            actions.PerformAction(user, whirlwind, predicted: false);

            Assert.That(entMan.HasComponent<ClawWhirlwindedComponent>(target), Is.False, "No throw over the shoulder");
            Assert.That(entMan.HasComponent<ProperKungFuWolfGripMarkComponent>(target), Is.False, "Mark consumed");
            Assert.That(entMan.GetComponent<PullableComponent>(target).Puller, Is.Null, "Grab released");
            Assert.That(whirlwind.Comp.Cooldown, Is.Not.Null, "Normal whirlwind cooldown");

            var damage = entMan.GetComponent<DamageableComponent>(target);
            Assert.That(Blunt(damage), Is.EqualTo(20f).Within(0.5f), "20 Blunt");
            Assert.That(damage.Damage.DamageDict["Slash"].Float(), Is.EqualTo(25f).Within(0.5f), "15 (grip) + 10 Slash");
            AssertTorsoHit(entMan, target, 20f, "Wolf Grip two-hand strike");
            Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Slash"), Is.EqualTo(25f).Within(0.5f), "Wolf Grip Slash on the torso");
            Assert.That(PartDamage(entMan, target, BodyPartType.Head, "Slash"), Is.EqualTo(0f), "Wolf Grip Slash not on the head");

            var chest = body.GetBodyChildrenOfType(target, BodyPartType.Chest).First().Id;
            var bone = entMan.GetComponent<WoundableComponent>(chest).Bone.ContainedEntities.First();
            Assert.That(entMan.GetComponent<BoneComponent>(bone).BoneSeverity, Is.EqualTo(BoneSeverity.Broken), "Chest bones broken");

            var lungs = body.GetPartOrgans(chest).Where(o => entMan.HasComponent<LungComponent>(o.Id)).ToList();
            Assert.That(lungs, Is.Not.Empty);
            foreach (var lung in lungs)
            {
                var ratio = lung.Component.OrganIntegrity.Float() / lung.Component.IntegrityCap.Float();
                Assert.That(ratio, Is.EqualTo(0.6f).Within(0.05f), $"Lungs lose 40%, left {ratio:P0}");
            }

            var blocked = entMan.GetComponent<KravMagaBlockedBreathingComponent>(target);
            Assert.That((blocked.BlockedTime - timing.CurTime).TotalSeconds, Is.EqualTo(60).Within(0.2), "No breathing for 60 s");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Knocked down");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(10f).Within(0.01f), "+5 ult");
        });

        // 8 tiles at 10 tiles/s.
        await server.WaitRunTicks(36);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(target).X - strikeStart.X;
            Assert.That(moved, Is.EqualTo(8f).Within(0.3f), $"Flies 8 tiles, moved {moved}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target));
        });

        // ~9 s after the strike: still on the floor.
        await server.WaitRunTicks(234);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), "Can't get up for 10 s");
            Assert.That(entMan.HasComponent<KravMagaBlockedBreathingComponent>(target), "Still can't breathe");
        });

        // ~11.5 s: the knockdown is over.
        await server.WaitRunTicks(75);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(target), Is.False, "Knockdown ends after 10 s");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task HammerTest()
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
        var body = entMan.System<SharedBodySystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var pulling = entMan.System<PullingSystem>();
        var stun = entMan.System<SharedStunSystem>();
        var mobState = entMan.System<MobStateSystem>();
        var statusNew = entMan.System<Content.Shared.StatusEffectNew.StatusEffectsSystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid user = default;
        EntityUid baseTarget = default;
        EntityUid lyingTarget = default;
        EntityUid critTarget = default;
        EntityUid gripTarget = default;
        EntityUid farTarget = default;
        EntityUid critHead = default;
        EntityUid danceTarget = default;
        Entity<ActionComponent> hammer = default;
        Entity<ActionComponent> wolfDance = default;
        Vector2 flipStart = default;
        Vector2 kickStart = default;

        // Eyes and brain of the head are left at ~70%.
        void AssertHeadOrgans(EntityUid target, string what)
        {
            var head = body.GetBodyChildrenOfType(target, BodyPartType.Head).FirstOrDefault();
            Assert.That(head.Id.IsValid(), $"{what}: the Hammer never takes the head off");

            var organs = body.GetPartOrgans(head.Id).ToList();
            var eyes = organs.Where(o => entMan.HasComponent<EyesComponent>(o.Id)).ToList();
            var brain = organs.Where(o => entMan.HasComponent<BrainComponent>(o.Id)).ToList();
            Assert.That(eyes, Is.Not.Empty, what);
            Assert.That(brain, Is.Not.Empty, what);
            foreach (var organ in eyes.Concat(brain))
            {
                var ratio = organ.Component.OrganIntegrity.Float() / organ.Component.IntegrityCap.Float();
                Assert.That(ratio, Is.EqualTo(0.7f).Within(0.05f), $"{what}: organ loses 30%, left {ratio:P0}");
            }
        }

        float HeadBlunt(EntityUid target)
        {
            var head = body.GetBodyChildrenOfType(target, BodyPartType.Head).FirstOrDefault();
            return head == default ? -1f : Blunt(entMan.GetComponent<DamageableComponent>(head.Id));
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 22; x++)
            for (var y = -9; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            baseTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            farTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 5.5f, 2.5f));
            lyingTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, -2.5f));
            critTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, -5.5f));
            gripTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 10.5f, -8.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        // Base hit.
        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.ActionEntities, Has.Count.EqualTo(4));
            foreach (var uid in kungFu.ActionEntities)
            {
                var id = entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID;
                if (id == "ActionProperKungFuHammer")
                    hammer = (uid, entMan.GetComponent<ActionComponent>(uid));
                else if (id == "ActionProperKungFuWolfDance")
                    wolfDance = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            Assert.That(hammer.Owner.IsValid(), "Hammer is granted with the art");
            Assert.That(hammer.Comp.UseDelay, Is.EqualTo(TimeSpan.FromSeconds(20)), "20 s cooldown");

            Assert.That(actions.SetEventTarget(hammer, farTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(entMan.GetComponent<DamageableComponent>(farTarget).TotalDamage.Float(), Is.EqualTo(0f), "Fist range is enforced");
            Assert.That(hammer.Comp.Cooldown, Is.Null, "No cooldown when out of reach");

            Assert.That(actions.SetEventTarget(hammer, baseTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(hammer.Comp.Cooldown, Is.Not.Null);

            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(baseTarget)), Is.EqualTo(15f).Within(0.5f), "15 Blunt");
            var headBlunt = HeadBlunt(baseTarget);
            if (headBlunt >= 0)
                Assert.That(headBlunt, Is.EqualTo(15f).Within(0.5f), "Damage goes to the head");
            AssertHeadOrgans(baseTarget, "Base hit");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(baseTarget), "Floored");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
        });

        // ~4.3 s later: still on the floor.
        await server.WaitRunTicks(130);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(baseTarget), "Can't get up for 5 s");
        });

        // ~6 s: up again.
        await server.WaitRunTicks(50);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(baseTarget), Is.False, "Knockdown ends after 5 s");
        });

        // Already lying: 50 Blunt to the head and 30 s of forced sleep.
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(lyingTarget) + new Vector2(-1f, 0f));
            stun.TryKnockdown(lyingTarget, TimeSpan.FromSeconds(10), true);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(lyingTarget));
            actions.ClearCooldown(hammer.Owner);
            Assert.That(actions.SetEventTarget(hammer, lyingTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(hammer.Comp.Cooldown, Is.Not.Null);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(HeadBlunt(lyingTarget), Is.EqualTo(50f).Within(0.5f), "50 Blunt to the head, the head stays on");
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(lyingTarget)), Is.GreaterThanOrEqualTo(49.5f), "50 Blunt");
            AssertHeadOrgans(lyingTarget, "Lying hit");

            Assert.That(statusNew.TryGetTime(lyingTarget, "StatusEffectForcedSleeping", out var sleep), "Forced sleep");
            Assert.That(sleep.EndEffectTime, Is.Not.Null);
            Assert.That((sleep.EndEffectTime!.Value - timing.CurTime).TotalSeconds, Is.EqualTo(30).Within(0.3), "for 30 s");
            Assert.That(entMan.HasComponent<SleepingComponent>(lyingTarget), "Asleep");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(10f).Within(0.01f), "+5 ult");
        });

        // Critical: the skull breaks, the head stays on.
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(critTarget) + new Vector2(-1f, 0f));
            mobState.ChangeMobState(critTarget, MobState.Critical);
            critHead = body.GetBodyChildrenOfType(critTarget, BodyPartType.Head).First().Id;
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(mobState.IsCritical(critTarget));
            actions.ClearCooldown(hammer.Owner);
            Assert.That(actions.SetEventTarget(hammer, critTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(hammer.Comp.Cooldown, Is.Not.Null);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityExists(critHead) && !entMan.Deleted(critHead), "The head is not destroyed");
            Assert.That(body.GetBodyChildrenOfType(critTarget, BodyPartType.Head).Select(p => p.Id), Does.Contain(critHead), "The head stays attached");
            Assert.That(HeadBlunt(critTarget), Is.EqualTo(15f).Within(0.5f), "15 Blunt to the head");
            var skull = entMan.GetComponent<WoundableComponent>(critHead).Bone.ContainedEntities;
            Assert.That(skull, Is.Not.Empty, "Head has a skull");
            foreach (var bone in skull)
                Assert.That(entMan.GetComponent<BoneComponent>(bone).BoneSeverity, Is.EqualTo(BoneSeverity.Broken), "Skull is broken");
            AssertHeadOrgans(critTarget, "Crit hit");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(15f).Within(0.01f), "+5 ult");
        });

        // Wolf Grip synergy.
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(gripTarget) + new Vector2(-1f, 0f));
            combat.SetInCombatMode(user, true);
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, gripTarget, user, ComboAttackType.Harm));
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, gripTarget, user, ComboAttackType.Disarm));
            Assert.That(pulling.TryStartPull(user, gripTarget), "The grab itself works");
        });

        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuWolfGripMarkComponent>(gripTarget), "Wolf Grip mark");
            Assert.That(entMan.GetComponent<PullableComponent>(gripTarget).Puller, Is.EqualTo(user), "Held");
            flipStart = xformSys.GetWorldPosition(user);

            actions.ClearCooldown(hammer.Owner);
            Assert.That(actions.SetEventTarget(hammer, gripTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(hammer.Comp.Cooldown, Is.Not.Null);

            Assert.That(entMan.HasComponent<ProperKungFuWolfGripMarkComponent>(gripTarget), Is.False, "Grip mark consumed");
            Assert.That(entMan.GetComponent<PullableComponent>(gripTarget).Puller, Is.Null, "Grab released");
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(gripTarget)), Is.EqualTo(20f).Within(0.5f), "20 Blunt");
            var headBlunt = HeadBlunt(gripTarget);
            if (headBlunt >= 0)
                Assert.That(headBlunt, Is.EqualTo(20f).Within(0.5f), "to the head");
            AssertHeadOrgans(gripTarget, "Grip synergy");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(gripTarget), Is.False, "No knockdown in the synergy");
            Assert.That(entMan.TryGetComponent<ProperKungFuHammerMarkComponent>(gripTarget, out var mark), "Hammer mark");
            Assert.That((mark!.ExpiresAt - timing.CurTime).TotalSeconds, Is.EqualTo(2).Within(0.1));
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(25f).Within(0.01f), "+5 grip +5 hammer");
        });

        // Backflip done (0.35 s).
        await server.WaitRunTicks(14);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(user).X - flipStart.X;
            Assert.That(moved, Is.EqualTo(-1f).Within(0.2f), $"Backflip about 1 tile back, moved {moved}");

            kickStart = xformSys.GetWorldPosition(gripTarget);
            actions.ClearCooldown(wolfDance.Owner);
            Assert.That(actions.SetEventTarget(wolfDance, gripTarget));
            actions.PerformAction(user, wolfDance, predicted: false);
            Assert.That(wolfDance.Comp.Cooldown, Is.Not.Null);
            Assert.That(entMan.HasComponent<ProperKungFuHammerMarkComponent>(gripTarget), Is.False, "Hammer mark consumed");
            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing), "Dashing");
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.Dash), "Flying kick");
            Assert.That(dancing.WhirlwindVariant, Is.False);
            Assert.That(dancing.PhaseDuration.TotalSeconds, Is.LessThan(0.1), "Instant dash");
        });

        await server.WaitRunTicks(4);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Dash already over");
            var blunt = Blunt(entMan.GetComponent<DamageableComponent>(gripTarget));
            Assert.That(blunt, Is.EqualTo(40f).Within(0.5f), "20 (hammer) + 20 (normal kick)");
            Assert.That(PartDamage(entMan, gripTarget, BodyPartType.Chest, "Blunt"), Is.EqualTo(20f).Within(0.5f), "Instant-dash kick hits the torso");
            var dashHead = PartDamage(entMan, gripTarget, BodyPartType.Head, "Blunt");
            if (dashHead >= 0)
                Assert.That(dashHead, Is.EqualTo(20f).Within(0.5f), "Only the hammer hit is on the head");
            Assert.That(entMan.HasComponent<StunnedComponent>(gripTarget), "Normal kick stuns");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(gripTarget), "and knocks down");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), "The user lands on the floor after the instant flying kick");
        });

        await server.WaitRunTicks(45);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(gripTarget).X - kickStart.X;
            Assert.That(moved, Is.EqualTo(3f).Within(0.4f), $"Normal 3 tile knockback, moved {moved}");
        });

        // Let the user get up after the instant flying kick.
        await server.WaitRunTicks(40);

        // Wolf Dance mark: plain dance, then the Hammer hits the body and sends the target exactly 5 tiles.
        await server.WaitPost(() =>
        {
            danceTarget = SpawnMob(entMan, new EntityCoordinates(map.Grid, 16.5f, 0.5f));
            xformSys.SetWorldPosition(user, new Vector2(12.5f, 0.5f));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(user), Is.False, "User is up again");
            actions.ClearCooldown(wolfDance.Owner);
            Assert.That(actions.SetEventTarget(wolfDance, danceTarget));
            actions.PerformAction(user, wolfDance, predicted: false);
            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing), "Dashing");
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.PassThrough), "Plain Wolf Dance");
        });

        // Dash 0.35 s, toss 0.6 s.
        await server.WaitRunTicks(32);

        float ultBefore = 0;
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False);
            Assert.That(PartDamage(entMan, danceTarget, BodyPartType.Chest, "Blunt"), Is.EqualTo(20f).Within(0.5f), "Dance kick on the torso");
            Assert.That(entMan.TryGetComponent<ProperKungFuWolfDanceMarkComponent>(danceTarget, out var mark), "Wolf Dance mark");
            Assert.That(mark!.Marker, Is.EqualTo(user));
            Assert.That(entMan.HasComponent<KnockedDownComponent>(danceTarget), "Landed knocked down");
            Assert.That((xformSys.GetWorldPosition(danceTarget) - xformSys.GetWorldPosition(user)).Length(), Is.LessThan(1.5f), "Within fist reach");

            ultBefore = entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge;
            kickStart = xformSys.GetWorldPosition(danceTarget);
            actions.ClearCooldown(hammer.Owner);
            Assert.That(actions.SetEventTarget(hammer, danceTarget));
            actions.PerformAction(user, hammer, predicted: false);
            Assert.That(hammer.Comp.Cooldown, Is.Not.Null, "Normal Hammer cooldown");

            Assert.That(entMan.HasComponent<ProperKungFuWolfDanceMarkComponent>(danceTarget), Is.False, "Wolf Dance mark consumed");
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(danceTarget)), Is.EqualTo(40f).Within(0.5f), "20 (dance) + 20 (hammer)");
            Assert.That(PartDamage(entMan, danceTarget, BodyPartType.Chest, "Blunt"), Is.EqualTo(40f).Within(0.5f), "Hammer hits the body");
            Assert.That(HeadBlunt(danceTarget), Is.EqualTo(0f).Within(0.01f), "Nothing to the head");
            Assert.That(entMan.HasComponent<SleepingComponent>(danceTarget), Is.False, "No sleep");
            var head = body.GetBodyChildrenOfType(danceTarget, BodyPartType.Head).First();
            foreach (var organ in body.GetPartOrgans(head.Id))
            {
                if (entMan.HasComponent<EyesComponent>(organ.Id) || entMan.HasComponent<BrainComponent>(organ.Id))
                    Assert.That(organ.Component.OrganIntegrity.Float(), Is.EqualTo(organ.Component.IntegrityCap.Float()).Within(0.01f), "No eye/brain trauma");
            }
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(ultBefore + 5f).Within(0.01f), "+5 ult");
        });

        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            var moved = (xformSys.GetWorldPosition(danceTarget) - kickStart).Length();
            Assert.That(moved, Is.EqualTo(5f).Within(0.2f), $"Exactly 5 tiles, moved {moved}");
            Assert.That(xformSys.GetWorldPosition(danceTarget).X, Is.LessThan(kickStart.X), "Away from the user");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(danceTarget), "Still on the floor");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WolfInstinctTest()
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
        var combat = entMan.System<SharedCombatModeSystem>();
        var melee = entMan.System<SharedMeleeWeaponSystem>();
        var throwing = entMan.System<Content.Shared.Throwing.ThrowingSystem>();
        var guns = entMan.System<Content.Shared.Weapons.Ranged.Systems.SharedGunSystem>();

        EntityUid user = default;
        EntityUid attacker = default;
        EntityUid attacker2 = default;
        EntityUid thrower = default;
        EntityUid shooter = default;
        EntityUid farShooter = default;
        EntityUid gun = default;
        EntityUid item = default;
        Entity<ActionComponent> instinct = default;
        Vector2 attackerStart = default;
        Vector2 attacker2Start = default;
        var ultBefore = 0f;

        void Cast()
        {
            actions.ClearCooldown(instinct.AsNullable());
            actions.PerformAction(user, instinct, predicted: false);
        }

        void MoveUser(float x, float y)
        {
            xformSys.SetCoordinates(user, new EntityCoordinates(map.Grid, x, y));
        }

        EntityUid instinctAura = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -4; x <= 16; x++)
            for (var y = -13; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            attacker = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            attacker2 = SpawnMob(entMan, new EntityCoordinates(map.Grid, -0.5f, 0.5f));
            thrower = SpawnMob(entMan, new EntityCoordinates(map.Grid, 4.5f, -3.5f));
            shooter = SpawnMob(entMan, new EntityCoordinates(map.Grid, 6.5f, -7.5f));
            farShooter = SpawnMob(entMan, new EntityCoordinates(map.Grid, 12.5f, -11.5f));
            gun = entMan.SpawnEntity("WeaponPistolViper", new EntityCoordinates(map.Grid, 6.5f, -7.5f));
            Assert.That(hands.TryPickupAnyHand(shooter, gun), Is.True);
            combat.SetInCombatMode(attacker, true);
            combat.SetInCombatMode(attacker2, true);

            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        // ---- Melee: parry, two-palm counter, several counters per stance ----
        await server.WaitAssertion(() =>
        {
            var kungFu = entMan.GetComponent<ProperKungFuComponent>(user);
            Assert.That(kungFu.ActionEntities, Has.Count.EqualTo(4));
            foreach (var uid in kungFu.ActionEntities)
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID == "ActionProperKungFuWolfInstinct")
                    instinct = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            Assert.That(instinct.Owner.IsValid(), "Wolf Instinct is granted with the art");
            Assert.That(instinct.Comp.UseDelay, Is.EqualTo(TimeSpan.FromSeconds(30)), "30 s cooldown");

            ultBefore = kungFu.UltimateCharge;
            actions.PerformAction(user, instinct, predicted: false);
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), "Counter stance is on");
            Assert.That(entMan.GetComponent<WolfInstinctStanceComponent>(user).Aura, Is.Not.Null, "Aura is shown");
            instinctAura = entMan.GetComponent<WolfInstinctStanceComponent>(user).Aura!.Value;
            Assert.That(entMan.GetComponent<MetaDataComponent>(instinctAura).EntityPrototype!.ID, Is.EqualTo("ProperKungFuInstinctAura"));
            Assert.That(entMan.GetComponent<TransformComponent>(instinctAura).ParentUid, Is.EqualTo(user), "Ring follows the user");
            var pulses = 0;
            var pulseQuery = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (pulseQuery.MoveNext(out _, out var pulseMeta, out var pulseXform))
            {
                if (pulseMeta.EntityPrototype?.ID == "ProperKungFuInstinctAuraPulse" && pulseXform.ParentUid == user)
                    pulses++;
            }
            Assert.That(pulses, Is.EqualTo(1), "Activation ring pulse");

            attackerStart = xformSys.GetWorldPosition(attacker);
            var weapon = entMan.GetComponent<MeleeWeaponComponent>(attacker);
            melee.AttemptLightAttack(attacker, attacker, weapon, user);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.EqualTo(0f), "The parried punch does no damage");
            Assert.That(PartDamage(entMan, attacker, BodyPartType.Chest, "Slash"), Is.EqualTo(25f).Within(0.5f), "25 Slash into the chest");
            Assert.That(PartDamage(entMan, attacker, BodyPartType.Head, "Slash"), Is.LessThanOrEqualTo(0f), "Nothing on the head");
            Assert.That(entMan.GetComponent<StaminaComponent>(attacker).StaminaDamage, Is.EqualTo(75f).Within(3f), "75 stamina damage");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge - ultBefore, Is.EqualTo(5f).Within(0.01f), "+5 ult per counter");

            // A second attacker in the same stance is countered too.
            attacker2Start = xformSys.GetWorldPosition(attacker2);
            var weapon = entMan.GetComponent<MeleeWeaponComponent>(attacker2);
            melee.AttemptLightAttack(attacker2, attacker2, weapon, user);
        });

        await server.WaitRunTicks(20);

        await server.WaitAssertion(() =>
        {
            var moved = xformSys.GetWorldPosition(attacker).X - attackerStart.X;
            Assert.That(moved, Is.EqualTo(3f).Within(0.4f), $"Thrown back 3 tiles, moved {moved}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(attacker), Is.False, "but stays on their feet");

            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.EqualTo(0f), "Second punch parried too");
            Assert.That(PartDamage(entMan, attacker2, BodyPartType.Chest, "Slash"), Is.EqualTo(25f).Within(0.5f), "Second counter");
            var moved2 = attacker2Start.X - xformSys.GetWorldPosition(attacker2).X;
            Assert.That(moved2, Is.EqualTo(3f).Within(0.4f), $"Second attacker thrown the other way, moved {moved2}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(attacker2), Is.False);
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge - ultBefore, Is.EqualTo(10f).Within(0.01f), "+5 for each counter");
        });

        // The stance runs out after 3 s.
        await server.WaitRunTicks(75);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), Is.False, "Stance ends after 3 s");
            Assert.That(entMan.Deleted(instinctAura), "Ring removed with the stance");
        });

        // ---- Thrown objects: no damage, flies back twice as fast ----
        var thrownSpeed = 0f;
        await server.WaitPost(() =>
        {
            MoveUser(0.5f, -3.5f);
            Cast();
            item = entMan.SpawnEntity("CombatKnife", new EntityCoordinates(map.Grid, 3.7f, -3.5f));
            throwing.TryThrow(item, new Vector2(-4f, 0f), 8f, thrower, pushbackRatio: 0f, recoil: false, playSound: false);
            thrownSpeed = entMan.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(item).LinearVelocity.Length();
        });

        var reflectedSpeed = 0f;
        var reflectedX = 0f;
        for (var i = 0; i < 30 && reflectedSpeed == 0f; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                if (entMan.TryGetComponent<Content.Shared.Throwing.ThrownItemComponent>(item, out var thrown) && thrown.Thrower == user)
                {
                    var velocity = entMan.GetComponent<Robust.Shared.Physics.Components.PhysicsComponent>(item).LinearVelocity;
                    reflectedSpeed = velocity.Length();
                    reflectedX = velocity.X;
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(thrownSpeed, Is.GreaterThan(1f), "The test throw moves");
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.EqualTo(0f), "Thrown object does no damage");
            Assert.That(reflectedSpeed, Is.EqualTo(thrownSpeed * 2f).Within(thrownSpeed * 0.3f), $"Reflected at double speed: {thrownSpeed} -> {reflectedSpeed}");
            Assert.That(reflectedX, Is.GreaterThan(0f), "back toward the thrower");
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), "The stance continues after a reflection");
        });

        await server.WaitRunTicks(15);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(thrower).TotalDamage.Float(), Is.GreaterThan(0f), "The reflected knife hits the thrower");
        });

        // ---- Gun within 8 tiles: dash, knockdown, gun knocked out, hard grab, no damage ----
        await server.WaitPost(() =>
        {
            entMan.RemoveComponent<WolfInstinctStanceComponent>(user);
            MoveUser(0.5f, -7.5f);
            Cast();
            var bullet = entMan.SpawnEntity("BulletPistol", new EntityCoordinates(map.Grid, 5.7f, -7.5f));
            guns.ShootProjectile(bullet, new Vector2(-1f, 0f), Vector2.Zero, gun, shooter, 20f);
        });

        await server.WaitRunTicks(20);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.GreaterThan(0f), "The bullet still hurts");
            var gap = xformSys.GetWorldPosition(shooter) - xformSys.GetWorldPosition(user);
            Assert.That(gap.Length(), Is.LessThan(1.5f), $"Dashed to the shooter, gap {gap}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(shooter), "Shooter knocked down");
            Assert.That(hands.EnumerateHeld(shooter).Contains(gun), Is.False, "Gun knocked out of the hands");
            var puller = entMan.GetComponent<PullerComponent>(user);
            var pullable = entMan.GetComponent<PullableComponent>(shooter);
            Assert.That(puller.Pulling, Is.EqualTo(shooter), "Grabbed");
            Assert.That(puller.GrabStage, Is.EqualTo(GrabStage.Hard), "Hard grab (user side)");
            Assert.That(pullable.GrabStage, Is.EqualTo(GrabStage.Hard), "Hard grab (target side)");
            Assert.That(entMan.GetComponent<DamageableComponent>(shooter).TotalDamage.Float(), Is.EqualTo(0f), "No damage to the shooter");
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), Is.False, "A gun dash ends the stance");

            entMan.System<PullingSystem>().TryStopPull(shooter, pullable, user);
        });

        // ---- Gun further than 8 tiles: dash 8 tiles and miss ----
        var missStart = Vector2.Zero;
        await server.WaitPost(() =>
        {
            MoveUser(0.5f, -11.5f);
            missStart = new Vector2(0.5f, -11.5f);
            Cast();
            var bullet = entMan.SpawnEntity("BulletPistol", new EntityCoordinates(map.Grid, 11.7f, -11.5f));
            guns.ShootProjectile(bullet, new Vector2(-1f, 0f), Vector2.Zero, gun, farShooter, 20f);
        });

        await server.WaitRunTicks(40);

        await server.WaitAssertion(() =>
        {
            var pos = xformSys.GetWorldPosition(user);
            var moved = pos.X - missStart.X;
            Assert.That(moved, Is.EqualTo(8f).Within(0.3f), $"Dashed the full 8 tiles, moved {moved}");
            Assert.That(pos.Y, Is.EqualTo(missStart.Y).Within(0.2f), "straight at the shooter");
            Assert.That(entMan.GetComponent<PullerComponent>(user).Pulling, Is.Null, "Missed, nobody grabbed");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(farShooter), Is.False, "Far shooter untouched");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Wolf Instinct finisher: a melee attacker with 85+ damage (before the counter) gets 6 claw slashes of 1 Slash
    /// while both travel 3 tiles, then a 100 Slash cross strike that throws them 10 tiles. Below 85: normal counter.
    /// </summary>
    /// <summary>
    /// Plain Wolf Dance finisher: a target with at least 85 damage before the dance gets 10 more passes (5 Blunt each)
    /// and a final strike (30 Slash to every body part, 7 tile knockback); with 84 it is the normal plain dance.
    /// </summary>
    [Test]
    public async Task WolfDanceFinisherTest()
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
        var body = entMan.System<SharedBodySystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid user = default;
        EntityUid weak = default;
        EntityUid victim = default;
        Entity<ActionComponent> dance = default;
        Vector2 victimStart = default;
        Vector2 weakStart = default;

        void PreDamage(EntityUid uid, float amount)
        {
            // All on the torso (it can't come off), whole numbers so the total is exact.
            var dmg = new DamageSpecifier();
            dmg.DamageDict.Add("Blunt", amount);
            damageSys.TryChangeDamage(uid, dmg, targetPart: TargetBodyPart.Chest, canMiss: false);
        }

        float Total(EntityUid uid, string type)
        {
            return entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict.TryGetValue(type, out var v) ? v.Float() : 0f;
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -14; x <= 14; x++)
            for (var y = -10; y <= 26; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, -3.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            weak = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 18.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, -3.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            PreDamage(victim, 85f);
            PreDamage(weak, 84f);
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.EqualTo(85f).Within(0.01f));
            Assert.That(entMan.GetComponent<DamageableComponent>(weak).TotalDamage.Float(), Is.EqualTo(84f).Within(0.01f));
            foreach (var uid in entMan.GetComponent<ProperKungFuComponent>(user).ActionEntities)
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID == "ActionProperKungFuWolfDance")
                    dance = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            victimStart = xformSys.GetWorldPosition(victim);
            Assert.That(actions.SetEventTarget(dance, victim));
            actions.PerformAction(user, dance, predicted: false);
            Assert.That(dance.Comp.Cooldown, Is.Not.Null, "Normal cooldown");
            Assert.That(entMan.TryGetComponent<WolfDancingComponent>(user, out var dancing), "First pass: the plain dash");
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.PassThrough));
            Assert.That(dancing.FinisherPending, "85 before the dance: finisher");
        });

        // First pass done (0.35 s) and a few extra passes in.
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False);
            Assert.That(entMan.TryGetComponent<ProperKungFuDanceFinisherComponent>(user, out var finisher), "Finisher running");
            Assert.That(finisher!.PassesDone, Is.InRange(1, 9));
            Assert.That((xformSys.GetWorldPosition(victim) - victimStart).Length(), Is.LessThan(0.05f), "Victim locked in place");
            Assert.That(entMan.HasComponent<StunnedComponent>(victim), "Victim stunned");
            Assert.That(Total(victim, "Slash"), Is.EqualTo(0f), "No final strike yet");
        });

        // Wait for the end of the sequence.
        for (var i = 0; i < 120; i++)
        {
            await server.WaitRunTicks(1);
            var running = false;
            await server.WaitPost(() => running = entMan.HasComponent<ProperKungFuDanceFinisherComponent>(user));
            if (!running)
                break;
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuDanceFinisherComponent>(user), Is.False, "Finisher over");
            // 85 pre + 20 first pass + 10 x 5.
            Assert.That(Total(victim, "Blunt"), Is.EqualTo(155f).Within(0.5f), "20 + 10 passes x 5 Blunt");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Blunt"), Is.EqualTo(155f).Within(0.5f), "All the Blunt on the torso");
            Assert.That(Total(victim, "Slash"), Is.EqualTo(180f).Within(0.5f), "30 Slash x 6 parts");

            foreach (var type in new[] { BodyPartType.Head, BodyPartType.Chest, BodyPartType.Arm, BodyPartType.Leg })
            {
                var parts = body.GetBodyChildrenOfType(victim, type).ToList();
                Assert.That(parts, Has.Count.EqualTo(type is BodyPartType.Arm or BodyPartType.Leg ? 2 : 1), $"{type}: still attached");
                foreach (var part in parts)
                {
                    var slash = entMan.GetComponent<DamageableComponent>(part.Id).Damage.DamageDict.TryGetValue("Slash", out var v) ? v.Float() : 0f;
                    Assert.That(slash, Is.EqualTo(30f).Within(0.5f), $"{type} {part.Component.Symmetry}: 30 Slash");
                }
            }

            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
        });

        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            var moved = (xformSys.GetWorldPosition(victim) - victimStart).Length();
            Assert.That(moved, Is.EqualTo(7f).Within(0.3f), $"Thrown 7 tiles, moved {moved}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Lands knocked down");
        });

        // ---- 84 before the dance: the normal plain dance ----
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(weak) + new Vector2(-4f, 0f));
            weakStart = xformSys.GetWorldPosition(weak);
            actions.ClearCooldown(dance.Owner);
            Assert.That(actions.SetEventTarget(dance, weak));
            actions.PerformAction(user, dance, predicted: false);
            Assert.That(entMan.GetComponent<WolfDancingComponent>(user).FinisherPending, Is.False, "84: no finisher");
        });

        await server.WaitRunTicks(40);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False);
            Assert.That(entMan.HasComponent<ProperKungFuDanceFinisherComponent>(user), Is.False, "No finisher");
            Assert.That(Total(weak, "Blunt"), Is.EqualTo(104f).Within(0.5f), "Only the plain 20 Blunt");
            Assert.That(Total(weak, "Slash"), Is.EqualTo(0f));
            Assert.That((xformSys.GetWorldPosition(weak) - weakStart).Length(), Is.LessThan(0.2f), "Tossed in place");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WolfInstinctFinisherTest()
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
        var combat = entMan.System<SharedCombatModeSystem>();
        var melee = entMan.System<SharedMeleeWeaponSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid user = default;
        EntityUid weak = default;   // 84 damage: normal counter
        EntityUid victim = default; // 85 damage: finisher
        Entity<ActionComponent> instinct = default;
        Vector2 weakStart = default;
        Vector2 victimStart = default;
        Vector2 userStart = default;
        var ultBefore = 0f;

        float Slash(EntityUid uid)
        {
            return entMan.GetComponent<DamageableComponent>(uid).Damage.DamageDict.TryGetValue("Slash", out var v) ? v.Float() : 0f;
        }

        void PreDamage(EntityUid uid, float amount)
        {
            // Whole numbers so the total is exact (85 = 28 + 28 + 29).
            var parts = new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.Groin };
            var share = MathF.Floor(amount / 3f);
            for (var p = 0; p < parts.Length; p++)
            {
                var dmg = new DamageSpecifier();
                dmg.DamageDict.Add("Blunt", p == parts.Length - 1 ? amount - share * 2 : share);
                damageSys.TryChangeDamage(uid, dmg, targetPart: parts[p], canMiss: false);
            }
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -8; x <= 22; x++)
            for (var y = -3; y <= 3; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            weak = SpawnMob(entMan, new EntityCoordinates(map.Grid, -0.5f, 0.5f));
            combat.SetInCombatMode(victim, true);
            combat.SetInCombatMode(weak, true);

            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            PreDamage(weak, 84f);
            PreDamage(victim, 85f);
        });

        await server.WaitRunTicks(1);

        // ---- 84 damage: the normal palm counter, the stance goes on ----
        await server.WaitPost(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(weak).TotalDamage.Float(), Is.EqualTo(84f).Within(0.01f));
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.EqualTo(85f).Within(0.01f));

            foreach (var uid in entMan.GetComponent<ProperKungFuComponent>(user).ActionEntities)
            {
                if (entMan.GetComponent<MetaDataComponent>(uid).EntityPrototype!.ID == "ActionProperKungFuWolfInstinct")
                    instinct = (uid, entMan.GetComponent<ActionComponent>(uid));
            }

            ultBefore = entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge;
            actions.PerformAction(user, instinct, predicted: false);
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user));

            weakStart = xformSys.GetWorldPosition(weak);
            melee.AttemptLightAttack(weak, weak, entMan.GetComponent<MeleeWeaponComponent>(weak), user);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(Slash(weak), Is.EqualTo(25f).Within(0.5f), "Below 85: the normal 25 Slash palm counter");
            Assert.That(entMan.HasComponent<ProperKungFuInstinctFinisherComponent>(user), Is.False, "Below 85: no finisher");
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), "Below 85: the stance goes on");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge - ultBefore, Is.EqualTo(5f).Within(0.01f));
        });

        await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.5f));

        // ---- 85 damage: the finisher ----
        await server.WaitPost(() =>
        {
            Assert.That(xformSys.GetWorldPosition(weak).X, Is.LessThan(weakStart.X - 2f), "The weak attacker was thrown back");
            userStart = xformSys.GetWorldPosition(user);
            victimStart = xformSys.GetWorldPosition(victim);
            ultBefore = entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge;
            melee.AttemptLightAttack(victim, victim, entMan.GetComponent<MeleeWeaponComponent>(victim), user);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(user).TotalDamage.Float(), Is.EqualTo(0f), "The attack is still parried");
            Assert.That(entMan.HasComponent<ProperKungFuInstinctFinisherComponent>(user), "85+: the finisher starts");
            Assert.That(entMan.HasComponent<WolfInstinctStanceComponent>(user), Is.False, "The stance ends");
            Assert.That(entMan.HasComponent<StunnedComponent>(victim), "The victim is locked");
            Assert.That(Slash(victim), Is.LessThan(3f), "No 25 Slash palm counter, only the first claw slash");
            Assert.That(entMan.GetComponent<StaminaComponent>(victim).StaminaDamage, Is.LessThan(1f), "No stamina damage from the palm counter");
        });

        // Halfway: both moved ~1.5 tiles together.
        await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.6f));
        await server.WaitAssertion(() =>
        {
            var victimPos = xformSys.GetWorldPosition(victim);
            var userPos = xformSys.GetWorldPosition(user);
            Assert.That(victimPos.X - victimStart.X, Is.EqualTo(1.5f).Within(0.35f), $"Victim carried forward ({victimPos})");
            Assert.That(victimPos.X - userPos.X, Is.EqualTo(victimStart.X - userStart.X).Within(0.05f), "The user follows the victim");
            Assert.That(Slash(victim), Is.InRange(3f, 4.5f), "Claw slashes land one by one, 1 Slash each");
            Assert.That(entMan.HasComponent<StunnedComponent>(victim), "Still locked");
        });

        // Cross strike at 1.2 s.
        await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.7f));
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuInstinctFinisherComponent>(user), Is.False, "Finisher over");
            Assert.That(Slash(victim), Is.EqualTo(106f).Within(0.5f), "6 x 1 Slash + 100 Slash");
            var chest = PartDamage(entMan, victim, BodyPartType.Chest, "Slash");
            Assert.That(chest, Is.GreaterThan(0f), "Slashes go to the torso");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Head, "Slash"), Is.LessThanOrEqualTo(0f), "Nothing on the head");
            Assert.That(xformSys.GetWorldPosition(user).X - userStart.X, Is.EqualTo(3f).Within(0.2f), "The user travelled 3 tiles");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge - ultBefore, Is.EqualTo(5f).Within(0.01f), "+5 ult");

            var crosses = 0;
            var q = entMan.EntityQueryEnumerator<MetaDataComponent>();
            while (q.MoveNext(out _, out var meta))
            {
                if (meta.EntityPrototype?.ID == "ProperKungFuInstinctFinisherCross")
                    crosses++;
            }
            Assert.That(crosses, Is.EqualTo(1), "Big cross claw strike");
        });

        // Knockback: 10 tiles in 0.5 s.
        await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.8f));
        await server.WaitAssertion(() =>
        {
            var traveled = xformSys.GetWorldPosition(victim).X - victimStart.X;
            Assert.That(traveled, Is.EqualTo(13f).Within(0.4f), $"3 tiles carried + 10 tiles knockback, got {traveled}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "The victim lands knocked down");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Perfect Flow (Disarm, Harm, Disarm, Harm): 10 Blunt into each arm and the torso (hands untouched),
    /// 5 s disorientation, broken arms from 50 total damage, finisher from 80 total damage.
    /// </summary>
    [Test]
    public async Task PerfectFlowTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var statusNew = entMan.System<Content.Shared.StatusEffectNew.StatusEffectsSystem>();
        var statusOld = entMan.System<StatusEffectsSystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        // Pre-damage on the legs/groin (never on the parts the combo hits).
        // Both thresholds count the damage before the combo: 0 -> nothing, 50 -> fractures only, 79 -> fractures only, 80 -> finisher.
        var preDamage = new[] { 0f, 50f, 79f, 80f };
        var targets = new EntityUid[preDamage.Length];
        EntityUid user = default;

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -3; x <= 6; x++)
            for (var y = -3; y <= 12; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            for (var i = 0; i < targets.Length; i++)
            {
                targets[i] = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f + i * 3));
            }

            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            for (var i = 0; i < targets.Length; i++)
            {
                if (preDamage[i] <= 0f)
                    continue;

                // Whole numbers so the total is exact (80 = 26 + 26 + 28).
                var parts = new[] { TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg, TargetBodyPart.Groin };
                var share = MathF.Floor(preDamage[i] / 3f);
                for (var p = 0; p < parts.Length; p++)
                {
                    var dmg = new DamageSpecifier();
                    dmg.DamageDict.Add("Blunt", p == parts.Length - 1 ? preDamage[i] - share * 2 : share);
                    damageSys.TryChangeDamage(targets[i], dmg, targetPart: parts[p], canMiss: false);
                }

                Assert.That(entMan.GetComponent<DamageableComponent>(targets[i]).TotalDamage.Float(),
                    Is.EqualTo(preDamage[i]).Within(0.01f), "Pre-damage applied exactly");
            }
        });

        await server.WaitRunTicks(1);

        for (var i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            var expectedTotal = preDamage[i] + 30f;
            var finisher = preDamage[i] >= 80f;
            var fractures = preDamage[i] >= 50f;
            var userStart = new Vector2(0.5f, 0.5f + i * 3);

            await server.WaitPost(() =>
            {
                xformSys.SetWorldPosition(user, userStart);
                combat.SetInCombatMode(user, true);
                Assert.That(entMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(),
                    Is.EqualTo(preDamage[i]).Within(0.5f), $"Case {i}: total before the combo");

                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Disarm));
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Harm));
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Disarm));
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Harm));
            });

            await server.WaitRunTicks(1);

            await server.WaitAssertion(() =>
            {
                var what = $"Case {i} (total {expectedTotal})";

                // Damage per part: arms and torso 10 Blunt each, hands nothing.
                Assert.That(ArmDamage(entMan, target, BodyPartSymmetry.Left), Is.EqualTo(10f).Within(0.5f), $"{what}: 10 Blunt on the left arm");
                Assert.That(ArmDamage(entMan, target, BodyPartSymmetry.Right), Is.EqualTo(10f).Within(0.5f), $"{what}: 10 Blunt on the right arm");
                Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Blunt"), Is.EqualTo(10f).Within(0.5f), $"{what}: 10 Blunt on the torso");
                foreach (var hand in entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, BodyPartType.Hand))
                {
                    var handDamage = entMan.GetComponent<DamageableComponent>(hand.Id).Damage.DamageDict.TryGetValue("Blunt", out var hb) ? hb.Float() : 0f;
                    Assert.That(handDamage, Is.LessThanOrEqualTo(0f), $"{what}: no damage on the hands");
                }

                Assert.That(PartDamage(entMan, target, BodyPartType.Head, "Blunt"), Is.EqualTo(finisher ? 20f : 0f).Within(0.5f),
                    finisher ? $"{what}: finisher, 20 Blunt on the head" : $"{what}: no finisher, head untouched");
                Assert.That(entMan.GetComponent<DamageableComponent>(target).TotalDamage.Float(),
                    Is.EqualTo(expectedTotal + (finisher ? 20f : 0f)).Within(0.5f), $"{what}: total damage");

                // Broken arms from 50.
                foreach (var symmetry in new[] { BodyPartSymmetry.Left, BodyPartSymmetry.Right })
                {
                    var arm = entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, BodyPartType.Arm, symmetry: symmetry).First().Id;
                    var bone = entMan.GetComponent<WoundableComponent>(arm).Bone.ContainedEntities.First();
                    var severity = entMan.GetComponent<BoneComponent>(bone).BoneSeverity;
                    if (fractures)
                        Assert.That(severity, Is.EqualTo(BoneSeverity.Broken), $"{what}: {symmetry} arm broken");
                    else
                        Assert.That(severity, Is.Not.EqualTo(BoneSeverity.Broken), $"{what}: {symmetry} arm intact");
                }

                // 5 s disorientation.
                Assert.That(statusNew.TryGetTime(target, "StatusEffectProperKungFuDisoriented", out var time), $"{what}: disoriented");
                Assert.That(time.EndEffectTime, Is.Not.Null);
                Assert.That((time.EndEffectTime!.Value - timing.CurTime).TotalSeconds, Is.EqualTo(5).Within(0.2), $"{what}: for 5 s");
                Assert.That(entMan.HasComponent<ProperKungFuDisorientedComponent>(target), $"{what}: mob-side disorientation");
                Assert.That(statusOld.HasStatusEffect(target, "SlurredSpeech"), $"{what}: slurred speech");
                Assert.That(entMan.HasComponent<Content.Shared.Jittering.JitteringComponent>(target), $"{what}: jitter");

                // Finisher: the user is behind the target, on the side opposite to where they stood.
                var userPos = xformSys.GetWorldPosition(user);
                var targetPos = xformSys.GetWorldPosition(target);
                if (finisher)
                {
                    Assert.That(userPos.X - targetPos.X, Is.EqualTo(0.8f).Within(0.1f), $"{what}: user behind the target ({userPos}, target {targetPos})");
                    Assert.That(userPos.Y - targetPos.Y, Is.EqualTo(0f).Within(0.1f), $"{what}: on the same line");
                    var afterimages = 0;
                    var query = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                    while (query.MoveNext(out var uid, out var meta, out _))
                    {
                        if (meta.EntityPrototype?.ID == "ProperKungFuFlowAfterimage"
                            && (xformSys.GetWorldPosition(uid) - userStart).Length() < 0.1f)
                            afterimages++;
                    }
                    Assert.That(afterimages, Is.EqualTo(1), $"{what}: burgundy afterimage at the old spot");
                }
                else
                {
                    Assert.That((userPos - userStart).Length(), Is.LessThan(0.05f), $"{what}: no finisher, the user stays");
                }

                Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f * (i + 1)).Within(0.01f),
                    $"{what}: +5 ult, nothing extra for the finisher");
                Assert.That(entMan.GetComponent<CanPerformComboComponent>(user).LastAttacks, Is.Empty, $"{what}: no leftover chain");
            });

            // Sequential white-red flashes: left arm right away, right arm ~0.1 s later, chest ~0.2 s later.
            if (i == 0)
            {
                await server.WaitAssertion(() =>
                    Assert.That(FlowFlashes(entMan, target), Is.EqualTo(1), "First flash (left arm) right away"));
                await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.12f));
                await server.WaitAssertion(() =>
                    Assert.That(FlowFlashes(entMan, target), Is.EqualTo(2), "Second flash (right arm) after ~0.1 s"));
                await server.WaitRunTicks((int) Math.Ceiling(timing.TickRate * 0.1f));
                await server.WaitAssertion(() =>
                {
                    Assert.That(FlowFlashes(entMan, target), Is.EqualTo(3), "Third flash (chest) after ~0.2 s");
                    var offsets = new System.Collections.Generic.List<Vector2>();
                    var q = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
                    while (q.MoveNext(out _, out var meta, out var xform))
                    {
                        if (meta.EntityPrototype?.ID == "ProperKungFuFlowFlash" && xform.ParentUid == target)
                            offsets.Add(xform.LocalPosition);
                    }
                    Assert.That(offsets.Select(o => MathF.Round(o.X, 2)).Distinct().Count(), Is.EqualTo(3), "Each flash on its own hit location");
                });
            }
        }

        // The disorientation wears off after 5 s.
        await server.WaitRunTicks((int) (timing.TickRate * 5.5f));

        await server.WaitAssertion(() =>
        {
            Assert.That(statusNew.HasStatusEffect(targets[0], "StatusEffectProperKungFuDisoriented"), Is.False, "Disorientation over");
            Assert.That(entMan.HasComponent<ProperKungFuDisorientedComponent>(targets[0]), Is.False, "Mob-side disorientation removed");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ClawSeriesFinisherTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var body = entMan.System<SharedBodySystem>();
        var blocker = entMan.System<ActionBlockerSystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid user = default;
        EntityUid victim = default;
        EntityUid weak = default;

        void PreDamage(EntityUid uid, float amount)
        {
            // Off the torso and the limbs (they take the finisher / get severed), whole numbers so the total is exact.
            var half = MathF.Floor(amount / 2f);
            foreach (var (part, value) in new[] { (TargetBodyPart.Head, half), (TargetBodyPart.Groin, amount - half) })
            {
                for (var left = value; left > 0f; left -= 5f)
                {
                    var dmg = new DamageSpecifier();
                    dmg.DamageDict.Add("Blunt", MathF.Min(5f, left));
                    damageSys.TryChangeDamage(uid, dmg, targetPart: part, canMiss: false);
                }
            }
        }

        int Parts(EntityUid uid, BodyPartType type) => body.GetBodyChildrenOfType(uid, type).Count();

        bool CloneExists()
        {
            var query = entMan.AllEntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var meta))
            {
                if (meta.EntityPrototype?.ID == "ProperKungFuSeriesClone")
                    return true;
            }

            return false;
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -12; x <= 12; x++)
            for (var y = -3; y <= 12; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            weak = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 9.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            PreDamage(victim, 80f);
            PreDamage(weak, 79f);
        });

        await server.WaitRunTicks(1);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.EqualTo(80f).Within(0.01f));
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, victim, user, ComboAttackType.Harm));
            }

            Assert.That(entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(user), "80 before the series: finisher");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
        });

        var clone = false;
        Vector2? holdPos = null;
        for (var i = 0; i < 150; i++)
        {
            await server.WaitRunTicks(1);
            await server.WaitPost(() =>
            {
                clone |= CloneExists();
                if (holdPos == null
                    && entMan.TryGetComponent<ProperKungFuSeriesFinisherComponent>(user, out var finisher)
                    && finisher.Phase == ClawSeriesFinisherPhase.Strikes)
                {
                    holdPos = xformSys.GetWorldPosition(victim);
                    var userPos = xformSys.GetWorldPosition(user);
                    Assert.That((holdPos.Value - userPos).Length(), Is.LessThan(1.6f), "Pushed back next to the user");
                    Assert.That(blocker.CanMove(victim), Is.False, "Locked during the strikes");
                }
            });

            if (holdPos != null && !entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(user))
                break;
        }

        // Cross knockback.
        await server.WaitRunTicks(20);

        await server.WaitAssertion(() =>
        {
            Assert.That(clone, "Burgundy double spawned");
            Assert.That(holdPos, Is.Not.Null, "Strike phase reached");
            Assert.That(entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(user), Is.False, "Finisher done");

            var chestSlash = PartDamage(entMan, victim, BodyPartType.Chest, "Slash");
            Assert.That(chestSlash, Is.GreaterThanOrEqualTo(119.5f), $"10x2 + 100 Slash on the torso, got {chestSlash}");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Head, "Slash"), Is.LessThanOrEqualTo(0f), "No Slash on the head");
            Assert.That(Parts(victim, BodyPartType.Arm), Is.EqualTo(1), "One arm severed");
            Assert.That(Parts(victim, BodyPartType.Leg), Is.EqualTo(1), "One leg severed");

            var moved = (xformSys.GetWorldPosition(victim) - holdPos!.Value).Length();
            Assert.That(moved, Is.EqualTo(3f).Within(0.5f), $"Cross knockback ~3 tiles, moved {moved}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Knocked down by the cross");
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "Still only +5 ult");
        });

        // ---- 79 before the series: the normal series ----
        await server.WaitPost(() =>
        {
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(weak) + new Vector2(-1f, 0f));
            for (var i = 0; i < 3; i++)
            {
                entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, weak, user, ComboAttackType.Harm));
            }

            Assert.That(entMan.HasComponent<ProperKungFuSeriesFinisherComponent>(user), Is.False, "79: no finisher");
        });

        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            Assert.That(PartDamage(entMan, weak, BodyPartType.Chest, "Slash"), Is.LessThanOrEqualTo(0f), "No strikes");
            Assert.That(Parts(weak, BodyPartType.Arm), Is.EqualTo(2));
            Assert.That(Parts(weak, BodyPartType.Leg), Is.EqualTo(2));
            Assert.That(entMan.HasComponent<KnockedDownComponent>(weak), Is.False, "Normal series keeps it standing");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task WolfGripFinisherTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var tileDefs = server.ResolveDependency<ITileDefinitionManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var hands = entMan.System<SharedHandsSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var body = entMan.System<SharedBodySystem>();
        var combat = entMan.System<SharedCombatModeSystem>();
        var pulling = entMan.System<PullingSystem>();
        var damageSys = entMan.System<DamageableSystem>();
        var timing = server.ResolveDependency<Robust.Shared.Timing.IGameTiming>();

        EntityUid user = default;
        EntityUid victim = default;
        EntityUid weak = default;

        void PreDamage(EntityUid uid, float amount)
        {
            // Off the head and the torso (they take the slam), in small chunks so nothing gets severed.
            var third = MathF.Floor(amount / 3f);
            foreach (var (part, value) in new[] { (TargetBodyPart.LeftLeg, third), (TargetBodyPart.RightLeg, third), (TargetBodyPart.Groin, amount - 2 * third) })
            {
                for (var left = value; left > 0f; left -= 5f)
                {
                    var dmg = new DamageSpecifier();
                    dmg.DamageDict.Add("Blunt", MathF.Min(5f, left));
                    damageSys.TryChangeDamage(uid, dmg, targetPart: part, canMiss: false);
                }
            }
        }

        void WolfGrip(EntityUid target)
        {
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Harm));
            entMan.EventBus.RaiseLocalEvent(user, new ComboAttackPerformedEvent(user, target, user, ComboAttackType.Disarm));
            Assert.That(pulling.TryStartPull(user, target), "The grab itself works");
        }

        await server.WaitPost(() =>
        {
            var tile = new Tile(tileDefs["Plating"].TileId);
            for (var x = -6; x <= 6; x++)
            for (var y = -3; y <= 12; y++)
            {
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), tile);
            }

            user = SpawnMob(entMan, new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            victim = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 0.5f));
            weak = SpawnMob(entMan, new EntityCoordinates(map.Grid, 1.5f, 9.5f));
            var book = entMan.SpawnEntity("ProperKungFuManual", new EntityCoordinates(map.Grid, 0.5f, 0.5f));
            Assert.That(hands.TryPickupAnyHand(user, book), Is.True);
            entMan.EventBus.RaiseLocalEvent(book, new UseInHandEvent(user));
        });

        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            PreDamage(victim, 80f);
            PreDamage(weak, 79f);
        });

        await server.WaitRunTicks(1);

        await server.WaitPost(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(victim).TotalDamage.Float(), Is.EqualTo(80f).Within(0.01f));
            combat.SetInCombatMode(user, true);
            WolfGrip(victim);
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuGripSuplexComponent>(victim), "80 before the grip: suplex");
            Assert.That(entMan.GetComponent<PullerComponent>(user).Pulling, Is.EqualTo(victim), "Normal hard grab first");
            Assert.That(entMan.HasComponent<KravMagaBlockedBreathingComponent>(victim), "Breath block as usual");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Slash"), Is.EqualTo(15f).Within(0.5f), "15 Slash as usual");
        });

        // 0.25 s hold + 0.55 s flight.
        await server.WaitRunTicks(35);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuGripSuplexComponent>(victim), Is.False, "Landed");
            Assert.That(entMan.GetComponent<PullableComponent>(victim).Puller, Is.Null, "Grab released");

            Assert.That(PartDamage(entMan, victim, BodyPartType.Head, "Blunt"), Is.EqualTo(25f).Within(0.5f), "25 Blunt to the head");
            Assert.That(PartDamage(entMan, victim, BodyPartType.Chest, "Blunt"), Is.EqualTo(50f).Within(0.5f), "50 Blunt to the torso");

            var head = body.GetBodyChildrenOfType(victim, BodyPartType.Head).First().Id;
            var skull = entMan.GetComponent<WoundableComponent>(head).Bone.ContainedEntities;
            Assert.That(skull, Is.Not.Empty);
            foreach (var bone in skull)
            {
                Assert.That(entMan.GetComponent<BoneComponent>(bone).BoneSeverity, Is.EqualTo(BoneSeverity.Broken), "Skull is broken");
            }

            var userPos = xformSys.GetWorldPosition(user);
            var victimPos = xformSys.GetWorldPosition(victim);
            Assert.That(victimPos.X, Is.LessThan(userPos.X - 0.5f), $"Behind the user: user {userPos}, victim {victimPos}");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Knocked down");

            Assert.That(entMan.TryGetComponent<ProperKungFuWolfGripMarkComponent>(victim, out var mark), "Wolf Grip mark still applied");
            Assert.That(mark!.Marker, Is.EqualTo(user));
            Assert.That(mark.ExpiresAt, Is.GreaterThan(timing.CurTime));
            Assert.That(entMan.GetComponent<ProperKungFuComponent>(user).UltimateCharge, Is.EqualTo(5f).Within(0.01f), "+5 ult");
        });

        // ~3.5 s after landing: still down (4 s knockdown).
        await server.WaitRunTicks(100);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<KnockedDownComponent>(victim), "Still down after ~3.3 s");
        });

        // ---- 79 before the grip: the normal Wolf Grip ----
        await server.WaitPost(() =>
        {
            if (entMan.GetComponent<PullerComponent>(user).Pulling is { } pulled)
                pulling.TryStopPull(pulled, entMan.GetComponent<PullableComponent>(pulled), user, true);
            xformSys.SetWorldPosition(user, xformSys.GetWorldPosition(weak) + new Vector2(-1f, 0f));
            WolfGrip(weak);
        });

        await server.WaitRunTicks(35);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<ProperKungFuGripSuplexComponent>(weak), Is.False, "79: no suplex");
            Assert.That(entMan.GetComponent<PullerComponent>(user).Pulling, Is.EqualTo(weak), "Still held");
            Assert.That(PartDamage(entMan, weak, BodyPartType.Head, "Blunt"), Is.LessThanOrEqualTo(0f), "No head slam");
            Assert.That(xformSys.GetWorldPosition(weak).X, Is.GreaterThan(xformSys.GetWorldPosition(user).X), "Still in front");
            Assert.That(entMan.HasComponent<KnockedDownComponent>(weak), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    private static int FlowFlashes(IEntityManager entMan, EntityUid target)
    {
        var count = 0;
        var query = entMan.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var meta, out var xform))
        {
            if (meta.EntityPrototype?.ID == "ProperKungFuFlowFlash" && xform.ParentUid == target)
                count++;
        }

        return count;
    }

    private static float ArmDamage(IEntityManager entMan, EntityUid target, BodyPartSymmetry symmetry)
    {
        var part = entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, BodyPartType.Arm, symmetry: symmetry).FirstOrDefault();
        if (part == default)
            return -1f;

        return entMan.GetComponent<DamageableComponent>(part.Id).Damage.DamageDict.TryGetValue("Blunt", out var value)
            ? value.Float()
            : 0f;
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

    /// <summary>
    /// Damage of one type on the first body part of that type, or -1 if the part is gone.
    /// </summary>
    private static float PartDamage(IEntityManager entMan, EntityUid target, BodyPartType type, string damageType)
    {
        var part = entMan.System<SharedBodySystem>().GetBodyChildrenOfType(target, type).FirstOrDefault();
        if (part == default)
            return -1f;

        return entMan.GetComponent<DamageableComponent>(part.Id).Damage.DamageDict.TryGetValue(damageType, out var value)
            ? value.Float()
            : 0f;
    }

    /// <summary>
    /// Everything except Hammer must put its Blunt on the torso, never on the head.
    /// </summary>
    private static void AssertTorsoHit(IEntityManager entMan, EntityUid target, float blunt, string what)
    {
        Assert.That(PartDamage(entMan, target, BodyPartType.Chest, "Blunt"), Is.EqualTo(blunt).Within(0.5f), $"{what}: Blunt goes to the torso");
        var head = PartDamage(entMan, target, BodyPartType.Head, "Blunt");
        Assert.That(head, Is.LessThanOrEqualTo(0f), $"{what}: no Blunt on the head");
    }

    private static float Blunt(DamageableComponent damage)
    {
        return damage.Damage.DamageDict.TryGetValue("Blunt", out var blunt) ? blunt.Float() : 0f;
    }
}

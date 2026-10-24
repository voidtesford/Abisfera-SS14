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
            Assert.That(kungFu!.ActionEntities, Has.Count.EqualTo(2));
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
            Assert.That(Blunt(entMan.GetComponent<DamageableComponent>(head)), Is.EqualTo(10f).Within(0.5f), "Damage goes to the head");

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
            Assert.That(dancing!.Phase, Is.EqualTo(WolfDancePhase.Dash), "No roll: one straight dash");
            Assert.That(dancing.WhirlwindVariant);
            Assert.That(entMan.HasComponent<ProperKungFuWhirlwindMarkComponent>(comboTarget), Is.False, "Mark consumed");
        });

        await server.WaitRunTicks(12);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<WolfDancingComponent>(user), Is.False, "Fast dash is over");
            var damage = entMan.GetComponent<DamageableComponent>(comboTarget);
            // The kick goes to the head with the skull already broken: Shitmed may roll a dismemberment
            // (up to 70%), which moves extra damage around. Exact numbers only make sense with the head on.
            var headAttached = body.GetBodyChildrenOfType(comboTarget, BodyPartType.Head).Any();
            if (headAttached)
            {
                Assert.That(Blunt(damage), Is.EqualTo(25f).Within(0.5f), "10 (knee) + 15 Blunt");
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

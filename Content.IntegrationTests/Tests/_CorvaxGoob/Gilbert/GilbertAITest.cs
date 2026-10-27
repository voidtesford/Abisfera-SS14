using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server.Gravity;
using Content.Shared.Gravity;
using Content.Shared.Chemistry.Components;
using Content.Shared.Cuffs.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Medical.Healing;
using Content.Shared.Stacks;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

[TestFixture]
public sealed class GilbertAITest
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

            // Station-like conditions: with no gravity mobs glide and overshoot.
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    /// <summary>Total "doses" of medicine Gilbert carries: stack counts of topicals + number of loaded injectors.</summary>
    private static int CountMedicine(IEntityManager entMan, GilbertAISystem ai, EntityUid gilbert)
    {
        var total = 0;
        foreach (var item in ai.EnumerateStored(gilbert))
        {
            if (entMan.HasComponent<HealingComponent>(item))
                total += entMan.TryGetComponent<StackComponent>(item, out var stack) ? stack.Count : 1;
            else if (entMan.HasComponent<HyposprayComponent>(item))
                total += 1;
        }

        return total;
    }

    [Test]
    public async Task VisionConeTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 12);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, front = default, behind = default, walled = default, far = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            // Angle 0 faces south (-Y).
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            front = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1f, -6f)));
            behind = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, 5f)));
            walled = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(-6f, -3f)));
            entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(-3f, -1.5f)));
            entMan.SpawnEntity("WallSolid", map.GridCoords.Offset(new Vector2(-3f, -2.5f)));
            far = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, -30f)));
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(ai.CanSee(gilbert, front), "sees enemies in front of him");
                Assert.That(ai.CanSee(gilbert, behind), Is.False, "does not see behind his back");
                Assert.That(ai.CanSee(gilbert, walled), Is.False, "does not see through walls");
                Assert.That(ai.CanSee(gilbert, far), Is.False, "vision range is 15 tiles");
                Assert.That(ai.IsEnemy(front), "every humanoid is an enemy");
                Assert.That(ai.IsEnemy(gilbert), Is.False);
            });

            // Turning around flips it.
            xformSys.SetWorldRotation(gilbert, Angle.FromDegrees(180));
            Assert.Multiple(() =>
            {
                Assert.That(ai.CanSee(gilbert, behind), "sees what is in front after turning");
                Assert.That(ai.CanSee(gilbert, front), Is.False);
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// After combat he searches (combat/search mode) while nobody is in his cone; once the search timeout passes
    /// without seeing anyone he goes back to passive mode.
    /// </summary>
    [Test]
    public async Task SearchRevertsToPassiveTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, target = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, -4f)));
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            // Shortened timings so the test runs quickly; the default timeout is 3 minutes.
            Assert.That(comp.SearchTimeout, Is.EqualTo(TimeSpan.FromMinutes(3)));
            comp.SearchTimeout = TimeSpan.FromSeconds(6);
            comp.LoseTargetDelay = TimeSpan.FromSeconds(1);
        });
        await server.WaitRunTicks(5);

        await server.WaitPost(() =>
        {
            ai.ForceCombat(gilbert, target);
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).State, Is.EqualTo(GilbertAIState.Combat));
            // The target vanishes far away, out of his sight.
            xformSys.SetCoordinates(target, map.GridCoords.Offset(new Vector2(0f, 60f)));
        });

        var sawSearch = false;
        var reachedIdle = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 20 && !reachedIdle; i++)
        {
            await server.WaitRunTicks(tickRate);
            await server.WaitPost(() =>
            {
                var state = entMan.GetComponent<GilbertAIComponent>(gilbert).State;
                if (state == GilbertAIState.Search)
                    sawSearch = true;
                if (state == GilbertAIState.Idle)
                    reachedIdle = true;
            });
        }

        Assert.Multiple(() =>
        {
            Assert.That(sawSearch, "loses the target and searches its last known position first");
            Assert.That(reachedIdle, "goes back to passive mode after the search timeout");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Below half health with nobody around he patches himself up with the real contents of his combat medkits.
    /// </summary>
    [Test]
    public async Task SelfHealConsumesMedkitTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var ai = entMan.System<GilbertAISystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(10);

        var medsBefore = 0;
        var damageBefore = FixedPoint2.Zero;
        await server.WaitPost(() =>
        {
            medsBefore = CountMedicine(entMan, ai, gilbert);
            // Spread over several hits (each lands on some body part; a single part caps at 250).
            for (var n = 0; n < 6; n++)
            {
                damageSys.TryChangeDamage(gilbert, new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Blunt"), 30), ignoreResistances: true, canMiss: false);
                damageSys.TryChangeDamage(gilbert, new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Heat"), 20), ignoreResistances: true, canMiss: false);
            }
            damageBefore = entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage;
        });

        Assert.That(medsBefore, Is.GreaterThan(0), "combat medkits must be in his bag");
        Assert.That(damageBefore, Is.GreaterThanOrEqualTo(FixedPoint2.New(250)));

        // Never heals right after being hurt.
        await server.WaitRunTicks((int) server.Timing.TickRate * 2);
        await server.WaitAssertion(() =>
            Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).State, Is.Not.EqualTo(GilbertAIState.Heal)));

        var sawHeal = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 60; i++)
        {
            await server.WaitRunTicks(tickRate);
            await server.WaitPost(() =>
            {
                if (entMan.GetComponent<GilbertAIComponent>(gilbert).State == GilbertAIState.Heal)
                    sawHeal = true;
            });
        }

        await server.WaitAssertion(() =>
        {
            var damageAfter = entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage;
            var medsAfter = CountMedicine(entMan, ai, gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(sawHeal, "enters the healing state");
                Assert.That(damageAfter, Is.LessThan(damageBefore), "the medicine actually heals him");
                Assert.That(medsAfter, Is.LessThan(medsBefore), "medicine is really used up");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// An enemy coming into view mid-heal makes him abort healing and fight.
    /// </summary>
    [Test]
    public async Task SelfHealAbortsOnEnemyTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var damageSys = entMan.System<DamageableSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default;
        await server.WaitPost(() => gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(10);
        await server.WaitPost(() =>
        {
            for (var n = 0; n < 9; n++)
                damageSys.TryChangeDamage(gilbert, new DamageSpecifier(protoMan.Index<DamageTypePrototype>("Blunt"), 30), ignoreResistances: true, canMiss: false);
            entMan.GetComponent<GilbertAIComponent>(gilbert).HealSafeDelay = TimeSpan.FromSeconds(2);
        });

        var healing = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 20 && !healing; i++)
        {
            await server.WaitRunTicks(tickRate / 2);
            await server.WaitPost(() =>
            {
                healing = entMan.GetComponent<GilbertAIComponent>(gilbert).State == GilbertAIState.Heal;
            });
        }

        Assert.That(healing, "starts healing when alone and below half health");

        await server.WaitPost(() =>
        {
            var (pos, rot) = xformSys.GetWorldPositionRotation(gilbert);
            var spot = pos + rot.ToWorldVec() * 3f;
            entMan.SpawnEntity(Victim, new MapCoordinates(spot, map.MapId));
        });
        await server.WaitRunTicks(tickRate);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
            Assert.Multiple(() =>
            {
                Assert.That(comp.State, Is.Not.EqualTo(GilbertAIState.Heal), "aborts healing when an enemy shows up");
                Assert.That(comp.Task, Is.Not.EqualTo(GilbertTask.HealItem));
                Assert.That(comp.Target, Is.Not.Null, "goes after the enemy");
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// An unarmed humanoid that stands still after the order gets cuffed with the cuffs from his belt.
    /// </summary>
    [Test]
    public async Task SurrenderAndCuffTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, target = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, -4f)));
        });

        var sawSurrender = false;
        var sawCombat = false;
        var cuffed = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 30 && !cuffed; i++)
        {
            await server.WaitRunTicks(tickRate);
            await server.WaitPost(() =>
            {
                var comp = entMan.GetComponent<GilbertAIComponent>(gilbert);
                if (comp.State == GilbertAIState.Surrender)
                    sawSurrender = true;
                if (comp.State == GilbertAIState.Combat)
                    sawCombat = true;
                cuffed = entMan.TryGetComponent<CuffableComponent>(target, out var cuffable) && cuffable.CuffedHandCount > 0;
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(sawSurrender, "orders an unarmed target to surrender first");
                Assert.That(cuffed, "cuffs a target that complied");
                // The test map has no air (barotrauma is Blunt), so check he never went hostile instead.
                Assert.That(sawCombat, Is.False, "a compliant target is not attacked");
                Assert.That(entMan.GetComponent<GilbertAIComponent>(gilbert).State, Is.EqualTo(GilbertAIState.Idle));
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// In combat he shoots his shotgun at the target (racking it as a pump-action) and reloads from his belt.
    /// </summary>
    [Test]
    public async Task CombatShootsAndReloadsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var ai = entMan.System<GilbertAISystem>();
        var xformSys = entMan.System<SharedTransformSystem>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid gilbert = default, target = default;
        var boxesBefore = 0;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            xformSys.SetWorldRotation(gilbert, Angle.Zero);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, -4f)));
        });
        await server.WaitRunTicks(5);
        await server.WaitPost(() =>
        {
            // A sturdy dummy so the fight lasts through a reload.
            entMan.GetComponent<GilbertAIComponent>(gilbert).MeleeSwitchRange = 0f;
            var thresholds = entMan.System<Content.Shared.Mobs.Systems.MobThresholdSystem>();
            thresholds.SetMobStateThreshold(target, FixedPoint2.New(100000), Content.Shared.Mobs.MobState.Critical);
            thresholds.SetMobStateThreshold(target, FixedPoint2.New(200000), Content.Shared.Mobs.MobState.Dead);
            boxesBefore = ai.EnumerateStored(gilbert).Count(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID.StartsWith("MagazineBoxShotgun") == true);
            ai.ForceCombat(gilbert, target);
        });

        var maxDamage = FixedPoint2.Zero;
        var reloaded = false;
        var tickRate = (int) server.Timing.TickRate;
        for (var i = 0; i < 40 && !(reloaded && maxDamage > 0); i++)
        {
            await server.WaitRunTicks(tickRate);
            await server.WaitPost(() =>
            {
                var total = entMan.GetComponent<DamageableComponent>(target).TotalDamage;
                if (total > maxDamage)
                    maxDamage = total;
                // Keep the dummy standing.
                if (total > 150)
                    damageSys.SetAllDamage(target, entMan.GetComponent<DamageableComponent>(target), 0);
                if (entMan.GetComponent<GilbertAIComponent>(gilbert).Task == GilbertTask.Reload)
                    reloaded = true;
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(maxDamage, Is.GreaterThan(FixedPoint2.Zero), "shoots the target");
                Assert.That(reloaded, "reloads from the ammo boxes on his belt");
                Assert.That(boxesBefore, Is.GreaterThan(0));
            });
        });

        await pair.CleanReturnAsync();
    }
}

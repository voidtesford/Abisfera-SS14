using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Fluids.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Stunnable;
using Content.Shared.Weapons.Melee.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

[TestFixture]
public sealed class GilbertAbilitiesTest
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";

    private static FixedPoint2 BloodVolume(IEntityManager entMan, EntityUid uid)
    {
        var blood = entMan.GetComponent<BloodstreamComponent>(uid);
        return blood.BloodSolution?.Comp.Solution.Volume ?? FixedPoint2.Zero;
    }

    /// <summary>Fills a square of floor around the test grid origin so puddles and movement have tiles.</summary>
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
        });
    }

    [Test]
    public async Task ScreamTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var damageSys = entMan.System<DamageableSystem>();

        EntityUid gilbert = default, near = default, far = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            near = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(2f, 0f)));
            far = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(0f, 9f)));
        });
        await server.WaitRunTicks(30);

        var nearBlood = FixedPoint2.Zero;
        await server.WaitPost(() =>
        {
            nearBlood = BloodVolume(entMan, near);
            var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
            // 40 + 40 = 80 damage within 30 s: the scream must trigger once the 75 threshold is passed.
            damageSys.TryChangeDamage(gilbert, new DamageSpecifier(blunt, 40), ignoreResistances: true, canMiss: false);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.HasComponent<StunnedComponent>(near), Is.False, "scream must not trigger below 75 damage");
        });

        await server.WaitPost(() =>
        {
            var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
            damageSys.TryChangeDamage(gilbert, new DamageSpecifier(blunt, 40), ignoreResistances: true, canMiss: false);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<StunnedComponent>(gilbert), Is.False, "Gilbert must not freeze himself while screaming");
                Assert.That(entMan.HasComponent<StunnedComponent>(near), "victim in range must be stunned");
                Assert.That(entMan.HasComponent<KnockedDownComponent>(near), "victim in range must be paralyzed");
                Assert.That(entMan.HasComponent<StunnedComponent>(far), Is.False, "victim out of range is not affected");
                Assert.That(BloodVolume(entMan, near), Is.LessThanOrEqualTo(nearBlood - 29), "victim must lose ~30u blood");

                var query = entMan.EntityQueryEnumerator<PuddleComponent>();
                Assert.That(query.MoveNext(out _, out _), "blood puddle must be spilled");

                var effects = entMan.EntityQuery<MetaDataComponent>()
                    .Count(m => m.EntityPrototype?.ID == "GilbertShockwaveEffect");
                Assert.That(effects, Is.GreaterThan(0), "shockwave effect must spawn");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DashTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var abilities = entMan.System<GilbertAbilitiesSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        EntityUid gilbert = default, target = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            target = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(4f, 0f)));
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            var start = xformSys.GetWorldPosition(gilbert);
            Assert.That(abilities.TryDash(gilbert, target), "dash must succeed");
            var moved = (xformSys.GetWorldPosition(gilbert) - start).Length();
            Assert.Multiple(() =>
            {
                Assert.That(moved, Is.GreaterThan(2.5f).And.LessThanOrEqualTo(4.01f), "dash covers up to 4 tiles");
                Assert.That(entMan.HasComponent<StunnedComponent>(target), "dash target must be stunned");
                Assert.That(abilities.TryDash(gilbert, target), Is.False, "dash has a 60 s cooldown");
            });
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ShoveTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 10);
        var entMan = server.ResolveDependency<IEntityManager>();
        var hands = entMan.System<SharedHandsSystem>();

        EntityUid gilbert = default, attacker = default, item = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            attacker = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1f, 0f)));
            item = entMan.SpawnEntity("Crowbar", map.GridCoords.Offset(new Vector2(1f, 0f)));
        });
        await server.WaitRunTicks(10);

        await server.WaitPost(() =>
        {
            Assert.That(hands.TryPickupAnyHand(attacker, item));
            var ev = new AttackedEvent(attacker, attacker, entMan.GetComponent<TransformComponent>(gilbert).Coordinates);
            entMan.EventBus.RaiseLocalEvent(gilbert, ev);
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.GetComponent<DamageableComponent>(attacker).TotalDamage, Is.GreaterThanOrEqualTo(FixedPoint2.New(14)), "shove hits for punch damage");
                Assert.That(entMan.HasComponent<StunnedComponent>(attacker), "shove stuns");
                Assert.That(entMan.HasComponent<KnockedDownComponent>(attacker), "shove knocks down");
                Assert.That(hands.EnumerateHeld(attacker).Contains(item), Is.False, "attacker drops what they hold");
            });
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<GilbertAbilitiesComponent>(gilbert);
            Assert.That(abilities(entMan).TryShove((gilbert, comp), attacker), Is.False, "shove has a 180 s cooldown");
        });

        await pair.CleanReturnAsync();
    }

    private static GilbertAbilitiesSystem abilities(IEntityManager entMan) => entMan.System<GilbertAbilitiesSystem>();
}

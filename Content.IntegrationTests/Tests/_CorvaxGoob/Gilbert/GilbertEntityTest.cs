using System.Collections.Generic;
using System.Linq;
using Content.Server.Atmos.Components;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._Shitmed.Body.Components;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Melee.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

[TestFixture]
public sealed class GilbertEntityTest
{
    private const string Gilbert = "MobGilbert";

    [Test]
    public async Task SpawnAndStatsTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var body = entMan.System<SharedBodySystem>();

        var uid = EntityUid.Invalid;
        await server.WaitPost(() => uid = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(entMan.HasComponent<GilbertComponent>(uid));
                Assert.That(entMan.HasComponent<BreathingImmunityComponent>(uid));
                Assert.That(entMan.HasComponent<PressureImmunityComponent>(uid));

                var thresholds = entMan.GetComponent<MobThresholdsComponent>(uid).Thresholds;
                Assert.That(thresholds.Values, Does.Not.Contain(MobState.Critical), "Gilbert must have no crit state");
                Assert.That(thresholds.First(t => t.Value == MobState.Dead).Key, Is.EqualTo(FixedPoint2.New(500)));

                var melee = entMan.GetComponent<MeleeWeaponComponent>(uid);
                Assert.That(melee.Damage.DamageDict["Blunt"], Is.EqualTo(FixedPoint2.New(15)));
                Assert.That(entMan.GetComponent<MeleeThrowOnHitComponent>(uid).Distance, Is.EqualTo(1f));

                var parts = body.GetBodyChildren(uid).ToList();
                Assert.That(parts, Is.Not.Empty);
                foreach (var (part, partComp) in parts)
                {
                    if (!entMan.TryGetComponent<WoundableComponent>(part, out var woundable))
                        continue;
                    // v8: torso and groin 500, everything else 250.
                    var expected = partComp.PartType is Content.Shared.Body.Part.BodyPartType.Chest or Content.Shared.Body.Part.BodyPartType.Groin ? 500 : 250;
                    Assert.That(woundable.IntegrityCap, Is.EqualTo(FixedPoint2.New(expected)), $"part {entMan.ToPrettyString(part)}");
                }

                var ev = new TryingToSleepEvent(uid);
                entMan.EventBus.RaiseLocalEvent(uid, ref ev);
                Assert.That(ev.Cancelled, "Gilbert must not be able to fall asleep");
            });
        });

        await server.WaitPost(() => entMan.DeleteEntity(uid));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LoadoutTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var inv = entMan.System<InventorySystem>();
        var hands = entMan.System<SharedHandsSystem>();

        var uid = EntityUid.Invalid;
        await server.WaitPost(() => uid = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(5);

        var expected = new Dictionary<string, string>
        {
            ["jumpsuit"] = "ClothingUniformJumpsuitShogkk",
            ["shoes"] = "ClothingShoesBootsShogkk",
            ["head"] = "ClothingHeadHelmetShogkk",
            ["mask"] = "ClothingMaskGasSwat",
            ["neck"] = "ClothingNeckDogtagShogkk", // v7
            ["gloves"] = "ClothingHandsGlovesCombat",
            ["outerClothing"] = "ClothingOuterArmorBulletproof",
            ["belt"] = "ClothingBeltShogkkWebbingFilled",
            ["back"] = "ClothingBackpackDuffelMilitaryShogkkFilled",
            ["id"] = "ShogkkIDCard",
        };

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var (slot, proto) in expected)
                {
                    Assert.That(inv.TryGetSlotEntity(uid, slot, out var item), $"slot {slot} is empty");
                    Assert.That(entMan.GetComponent<MetaDataComponent>(item!.Value).EntityPrototype?.ID, Is.EqualTo(proto));
                }

                var held = hands.EnumerateHeld(uid)
                    .Select(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID)
                    .ToList();
                Assert.That(held, Does.Contain("WeaponShotgunCombatShogkk"));
            });
        });

        await server.WaitPost(() => entMan.DeleteEntity(uid));
        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task DiesAt500WithoutCritTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.ResolveDependency<IEntityManager>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var damageSys = entMan.System<DamageableSystem>();
        var mobState = entMan.System<MobStateSystem>();

        var uid = EntityUid.Invalid;
        await server.WaitPost(() =>
        {
            uid = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            // v6: at 450 he goes berserk instead (GilbertV6Test); the 500 dead threshold underneath is still what
            // the berserk explosion kills him with, so check it without the second phase.
            entMan.RemoveComponent<GilbertBerserkComponent>(uid);
        });
        await server.WaitRunTicks(60);

        var blunt = protoMan.Index<DamageTypePrototype>("Blunt");
        var heat = protoMan.Index<DamageTypePrototype>("Heat");

        // Deal damage in steps; he must stay Alive (never Critical) until total damage reaches 500.
        for (var i = 0; i < 20; i++)
        {
            await server.WaitPost(() =>
            {
                var type = i % 2 == 0 ? blunt : heat;
                damageSys.TryChangeDamage(uid, new DamageSpecifier(type, FixedPoint2.New(25)), ignoreResistances: true, canMiss: false);
            });
            await server.WaitRunTicks(2);

            await server.WaitAssertion(() =>
            {
                var total = entMan.GetComponent<DamageableComponent>(uid).TotalDamage;
                Assert.That(mobState.IsCritical(uid), Is.False, $"crit at {total}");
                if (total < 500)
                    Assert.That(mobState.IsAlive(uid), $"not alive at {total}");
            });

            if (entMan.GetComponent<DamageableComponent>(uid).TotalDamage >= 500)
                break;
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(uid).TotalDamage, Is.GreaterThanOrEqualTo(FixedPoint2.New(500)));
            Assert.That(mobState.IsDead(uid), "Gilbert must die at 500 damage");
            Assert.That(entMan.Deleted(uid), Is.False, "Gilbert must not gib");
            Assert.That(entMan.System<SharedPointLightSystem>().TryGetLight(uid, out var light));
            Assert.That(light!.Enabled, Is.False, "his red light must turn off on death");
        });

        await server.WaitPost(() => entMan.DeleteEntity(uid));
        await pair.CleanReturnAsync();
    }
}

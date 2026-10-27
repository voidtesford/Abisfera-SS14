using System.Collections.Generic;
using System.Linq;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared._Shitmed.Medical.Surgery.Traumas;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared._Shitmed.Targeting;
using Content.Shared.Body.Part;
using Content.Shared.Body.Systems;
using Content.Shared.Damage;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v6: Gilbert's bones never break, in either phase. Heavy blunt hits on his limbs still wound and damage him but
/// cause no fracture and no bone trauma; a bone damaged directly (CQC, reagents) is mended at once.
/// </summary>
[TestFixture]
public sealed class GilbertFractureTest
{
    private static readonly TargetBodyPart[] Limbs =
    {
        TargetBodyPart.LeftArm, TargetBodyPart.RightArm, TargetBodyPart.LeftLeg, TargetBodyPart.RightLeg,
    };

    private static DamageSpecifier Blunt(float amount)
    {
        return new DamageSpecifier { DamageDict = { ["Blunt"] = FixedPoint2.New(amount) } };
    }

    private static List<(EntityUid Bone, BoneComponent Comp)> Bones(IEntityManager entMan, SharedBodySystem body, EntityUid uid)
    {
        var list = new List<(EntityUid, BoneComponent)>();
        foreach (var part in body.GetBodyChildren(uid))
        {
            if (!entMan.TryGetComponent<WoundableComponent>(part.Id, out var woundable))
                continue;

            foreach (var bone in woundable.Bone.ContainedEntities)
            {
                if (entMan.TryGetComponent<BoneComponent>(bone, out var comp))
                    list.Add((bone, comp));
            }
        }

        return list;
    }

    [Test]
    public async Task NoFracturesTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var damage = server.System<DamageableSystem>();
        var body = server.System<SharedBodySystem>();
        var trauma = server.System<TraumaSystem>();

        EntityUid gilbert = default;
        EntityUid human = default;
        await server.WaitPost(() =>
        {
            gilbert = GilbertTestUtil.SpawnActive(entMan, "MobGilbert", map.GridCoords);
            human = entMan.SpawnEntity("MobHuman", map.GridCoords);
        });
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var bones = Bones(entMan, body, gilbert);
            Assert.That(bones, Is.Not.Empty, "Gilbert has bones");
            foreach (var part in body.GetBodyChildren(gilbert))
                Assert.That(entMan.HasComponent<GilbertFractureImmunePartComponent>(part.Id), $"{entMan.ToPrettyString(part.Id)} is protected");
        });

        // Phase one: 2 heavy blunt hits on each limb (360 in total, below the 450 that starts the berserk).
        for (var round = 0; round < 2; round++)
        {
            await server.WaitPost(() =>
            {
                foreach (var limb in Limbs)
                {
                    damage.TryChangeDamage(gilbert, Blunt(45), ignoreResistances: true, targetPart: limb, canMiss: false);
                    damage.TryChangeDamage(human, Blunt(45), ignoreResistances: true, targetPart: limb, canMiss: false);
                }
            });
            await server.WaitRunTicks(10);
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<DamageableComponent>(gilbert).TotalDamage, Is.GreaterThan(FixedPoint2.New(300)),
                "the hits still hurt him");
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.EqualTo(GilbertBerserkPhase.None));
            foreach (var (bone, comp) in Bones(entMan, body, gilbert))
            {
                Assert.That(comp.BoneSeverity, Is.EqualTo(BoneSeverity.Normal), $"{entMan.ToPrettyString(bone)} not broken");
                Assert.That(comp.BoneIntegrity, Is.EqualTo(comp.IntegrityCap), $"{entMan.ToPrettyString(bone)} intact");
            }

            Assert.That(trauma.HasBodyTrauma(gilbert, TraumaType.BoneDamage), Is.False, "no bone trauma on Gilbert");
        });

        // Control: the same kind of hits do crack an ordinary human's bones. Bone traumas are rolled by chance,
        // so keep hitting the human (only) for a few more rounds until one lands.
        var cracked = false;
        for (var round = 0; round < 12 && !cracked; round++)
        {
            await server.WaitAssertion(() =>
                cracked = Bones(entMan, body, human).Any(b => b.Comp.BoneIntegrity < b.Comp.IntegrityCap));
            if (cracked)
                break;
            await server.WaitPost(() =>
            {
                foreach (var limb in Limbs)
                    damage.TryChangeDamage(human, Blunt(45), ignoreResistances: true, targetPart: limb, canMiss: false);
            });
            await server.WaitRunTicks(10);
        }
        Assert.That(cracked, "the same hits crack a human's bones");

        // Direct bone damage (what CQC's leg break does) is mended right away, in phase one...
        await server.WaitPost(() =>
        {
            foreach (var (bone, comp) in Bones(entMan, body, gilbert))
                trauma.ApplyDamageToBone(bone, comp.IntegrityCap, comp);
        });
        await server.WaitRunTicks(40);
        await server.WaitAssertion(() =>
        {
            foreach (var (bone, comp) in Bones(entMan, body, gilbert))
                Assert.That(comp.BoneSeverity, Is.EqualTo(BoneSeverity.Normal), $"{entMan.ToPrettyString(bone)} mended");
        });

        // ...and in the berserk phase.
        await server.WaitPost(() =>
        {
            damage.TryChangeDamage(gilbert, Blunt(200), ignoreResistances: true, canMiss: false);
        });
        await server.WaitRunTicks(5);
        await server.WaitPost(() =>
        {
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(gilbert).Phase, Is.Not.EqualTo(GilbertBerserkPhase.None));
            foreach (var (bone, comp) in Bones(entMan, body, gilbert))
                trauma.ApplyDamageToBone(bone, comp.IntegrityCap, comp);
        });
        await server.WaitRunTicks(40);
        await server.WaitAssertion(() =>
        {
            foreach (var (bone, comp) in Bones(entMan, body, gilbert))
            {
                Assert.That(comp.BoneSeverity, Is.EqualTo(BoneSeverity.Normal), $"{entMan.ToPrettyString(bone)} mended in berserk");
                Assert.That(comp.BoneIntegrity, Is.EqualTo(comp.IntegrityCap));
            }

            Assert.That(trauma.HasBodyTrauma(gilbert, TraumaType.BoneDamage), Is.False);
        });

        await server.WaitPost(() =>
        {
            entMan.DeleteEntity(gilbert);
            entMan.DeleteEntity(human);
        });
        await pair.CleanReturnAsync();
    }
}

using System.Linq;
using Content.Server._CorvaxGoob.Kamerton;
using Content.Shared.Singularity.Components;
using Content.Server.Singularity.EntitySystems;
using Content.Server.Tesla.Components;
using Content.Server.Beam.Components;
using Content.Shared.Electrocution;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton tesla: prototype, offspring timer, tentacle bursts and immunity to singularities.
/// The collapse spawning it is checked in <see cref="KamertonReactorTest"/>.
/// </summary>
[TestFixture]
[TestOf(typeof(KamertonTeslaSystem))]
public sealed class KamertonTeslaTest
{
    [Test]
    public async Task KamertonTeslaPrototypes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protos = server.ProtoMan;
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            var tesla = protos.Index<EntityPrototype>("KamertonTesla");
            Assert.That(tesla.Parents, Does.Contain("TeslaEnergyBall"));
            Assert.That(tesla.TryGetComponent<KamertonTeslaComponent>(out var kt, factory), Is.True);
            Assert.That(kt!.SpawnInterval, Is.EqualTo(30f));
            Assert.That(kt.SingularityChance, Is.EqualTo(0.5f));
            Assert.That(kt.TentacleRange, Is.EqualTo(50f));
            Assert.That(kt.BurstMinInterval, Is.EqualTo(15f));
            Assert.That(kt.BurstMaxInterval, Is.EqualTo(20f));
            Assert.That(tesla.TryGetComponent<LightningArcShooterComponent>(out var arcs, factory), Is.True);
            Assert.That(arcs!.LightningPrototype, Is.EqualTo("KamertonScarletLightning"));
            Assert.That(tesla.TryGetComponent<EventHorizonComponent>(out var eh, factory), Is.True);
            Assert.That(eh!.Radius, Is.EqualTo(1f), "twice the tesla orb");
            Assert.That(tesla.Categories.Any(c => c.ID == "HideSpawnMenu"), Is.False, "admins can spawn it");

            Assert.That(protos.HasIndex<EntityPrototype>("KamertonScarletLightning"));
            var tentacle = protos.Index<EntityPrototype>("KamertonTeslaTentacle");
            Assert.That(tentacle.TryGetComponent<ElectrifiedComponent>(out var shock, factory), Is.True);
            Assert.That(shock!.ShockDamage, Is.LessThan(10f), "a light shock");
            Assert.That(tentacle.TryGetComponent<BeamComponent>(out _, factory), Is.True);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KamertonTeslaSpawnsTeslaOrSingularity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var xforms = entMan.System<SharedTransformSystem>();
        var center = new MapCoordinates(new System.Numerics.Vector2(200, 200), map.MapId);
        EntityUid tesla = default;

        foreach (var (chance, expected) in new[] { (0f, "TeslaEnergyBall"), (1f, "Singularity") })
        {
            await server.WaitPost(() =>
            {
                tesla = entMan.SpawnEntity("KamertonTesla", center);
                var comp = entMan.GetComponent<KamertonTeslaComponent>(tesla);
                comp.SingularityChance = chance;
                comp.BurstTimer = 1000f;
                comp.SpawnTimer = 0.05f;
            });
            await pair.RunTicksSync(10);

            await server.WaitAssertion(() =>
            {
                var found = entMan.AllEntities<MetaDataComponent>()
                    .Where(e => e.Comp.EntityPrototype?.ID == expected && entMan.GetComponent<TransformComponent>(e).MapID == map.MapId)
                    .ToList();
                Assert.That(found, Has.Count.EqualTo(1), expected);
                var dist = (xforms.GetWorldPosition(found[0]) - xforms.GetWorldPosition(tesla)).Length();
                Assert.That(dist, Is.InRange(7f, 16f), "8-15 tiles away");
                Assert.That(entMan.GetComponent<KamertonTeslaComponent>(tesla).SpawnTimer, Is.GreaterThan(29f), "next one in 30 s");
                entMan.DeleteEntity(found[0]);
                entMan.DeleteEntity(tesla);
            });
        }

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KamertonTeslaIsNotConsumedBySingularity()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var horizon = entMan.System<EventHorizonSystem>();
        EntityUid tesla = default, sing = default, orb = default;

        await server.WaitPost(() =>
        {
            var at = new MapCoordinates(new System.Numerics.Vector2(200, 200), map.MapId);
            tesla = entMan.SpawnEntity("KamertonTesla", at);
            sing = entMan.SpawnEntity("Singularity", at);
            orb = entMan.SpawnEntity("TeslaMiniEnergyBall", at);
            var comp = entMan.GetComponent<KamertonTeslaComponent>(tesla);
            comp.SpawnTimer = 1000f;
            comp.BurstTimer = 1000f;
        });

        await server.WaitAssertion(() =>
        {
            var eh = entMan.GetComponent<EventHorizonComponent>(sing);
            Assert.That(horizon.CanConsumeEntity(sing, tesla, eh), Is.False);
            Assert.That(horizon.CanConsumeEntity(sing, orb, eh), Is.True, "an ordinary mini orb is food");
        });
        await pair.RunTicksSync(60);

        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(tesla), Is.False, "the Kamerton tesla survives the singularity");
            entMan.DeleteEntity(tesla);
            if (!entMan.Deleted(sing))
                entMan.DeleteEntity(sing);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task KamertonTeslaTentacleBurst()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;
        var map = await pair.CreateTestMap();
        var sys = entMan.System<KamertonTeslaSystem>();
        var xforms = entMan.System<SharedTransformSystem>();
        EntityUid tesla = default;
        var segments = 0;

        await server.WaitPost(() =>
        {
            tesla = entMan.SpawnEntity("KamertonTesla", new MapCoordinates(new System.Numerics.Vector2(300, 300), map.MapId));
            var comp = entMan.GetComponent<KamertonTeslaComponent>(tesla);
            comp.SpawnTimer = 1000f;
            comp.BurstTimer = 1000f;
            segments = sys.Burst((tesla, comp));
        });

        await server.WaitAssertion(() =>
        {
            Assert.That(segments, Is.InRange(8 * 20, 12 * 20), "8-12 tentacles of 20 segments");
            Assert.That(entMan.GetComponent<KamertonTeslaComponent>(tesla).Pending, Has.Count.EqualTo(segments));
        });

        // Halfway through the growth the tentacles are only partly out.
        await pair.RunSeconds(0.5f);
        var half = 0f;
        await server.WaitAssertion(() =>
        {
            half = MaxTentacleReach(entMan, xforms, tesla);
            Assert.That(half, Is.InRange(10f, 40f), "tentacles grow outward");
        });

        await pair.RunSeconds(0.6f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<KamertonTeslaComponent>(tesla).Pending, Is.Empty);
            var reach = MaxTentacleReach(entMan, xforms, tesla);
            Assert.That(reach, Is.InRange(35f, 52f), "tentacles reach about 50 tiles");
        });

        await pair.RunSeconds(3f);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.EntityQuery<BeamComponent>().Count(), Is.Zero, "tentacles fade quickly");
            entMan.DeleteEntity(tesla);
        });

        await pair.CleanReturnAsync();
    }

    private static float MaxTentacleReach(IEntityManager entMan, SharedTransformSystem xforms, EntityUid tesla)
    {
        var center = xforms.GetWorldPosition(tesla);
        var max = 0f;
        foreach (var (meta, xform) in entMan.EntityQuery<MetaDataComponent, TransformComponent>())
        {
            if (meta.EntityPrototype?.ID != "KamertonTeslaTentacle")
                continue;
            max = MathF.Max(max, (xforms.GetWorldPosition(xform) - center).Length());
        }
        return max;
    }
}

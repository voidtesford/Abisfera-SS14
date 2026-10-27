using Content.Server._CorvaxGoob.Redspace;
using System.Linq;
using Content.Server.Stack;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Stacks;
using Robust.Client.GameObjects;
using Robust.Client.ResourceManagement;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Redspace;

[TestFixture]
[TestOf(typeof(PeriodicCrackleSystem))]
public sealed class RedspaceCrystalTest
{
    private const string Full = "MaterialRedspaceCrystal";
    private const string Single = "MaterialRedspaceCrystal1";
    private const string StackId = "RedspaceCrystal";

    private const string CrystalRsi = "/Textures/_CorvaxGoob/Objects/Materials/redspace_crystal.rsi";
    private const string CrackleRsi = "/Textures/_CorvaxGoob/Effects/redspace_crackle.rsi";

    [Test]
    public async Task PrototypesAndSprites()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.That(protoMan.TryIndex<StackPrototype>(StackId, out var stack), Is.True);
            Assert.That(stack!.MaxCount, Is.EqualTo(10));
            Assert.That(stack.Spawn.Id, Is.EqualTo(Single));
            Assert.That(protoMan.HasIndex<EntityPrototype>(stack.Spawn), Is.True);

            foreach (var (id, count) in new[] { (Full, 10), (Single, 1) })
            {
                var proto = protoMan.Index<EntityPrototype>(id);
                Assert.That(proto.TryGetComponent<StackComponent>(out var stackComp, factory), Is.True, id);
                Assert.That(stackComp!.StackTypeId, Is.EqualTo(StackId), id);
                Assert.That(stackComp.Count, Is.EqualTo(count), id);
                Assert.That(proto.TryGetComponent<PeriodicCrackleComponent>(out _, factory), Is.True, id);
                Assert.That(proto.TryGetComponent<RedspaceCrystalComponent>(out _, factory), Is.True, id);
            }
        });

        // Every sprite state the prototypes and the stack visuals reference must exist.
        var resCache = pair.Client.ResolveDependency<IResourceCache>();
        await pair.Client.WaitAssertion(() =>
        {
            var crystal = resCache.GetResource<RSIResource>(new ResPath(CrystalRsi)).RSI;
            foreach (var state in new[] { "crystal", "crystal_2", "crystal_3", "crystal_4", "inhand-left", "inhand-right", "inhand-left-crackle", "inhand-right-crackle" })
                Assert.That(crystal.TryGetState(state, out _), Is.True, state);

            var crackle = resCache.GetResource<RSIResource>(new ResPath(CrackleRsi)).RSI;
            Assert.That(crackle.TryGetState("crackle", out var crackleState), Is.True);
            Assert.That(crackleState!.DelayCount, Is.GreaterThan(1), "the crackle overlay should be animated");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task StackAndCrackle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var timing = server.ResolveDependency<IGameTiming>();
        var stackSys = entMan.System<StackSystem>();
        var crackleSys = entMan.System<PeriodicCrackleSystem>();
        var appearance = entMan.System<SharedAppearanceSystem>();
        var containers = entMan.System<SharedContainerSystem>();

        EntityUid full = default;
        EntityUid split = default;
        await server.WaitPost(() =>
        {
            full = entMan.SpawnEntity(Full, map.GridCoords);
            var stack = entMan.GetComponent<StackComponent>(full);
            Assert.That(stack.Count, Is.EqualTo(10));
            Assert.That(stackSys.GetMaxCount(stack), Is.EqualTo(10));

            // Can't go past 10.
            stackSys.SetCount(full, 15, stack);
            Assert.That(stack.Count, Is.EqualTo(10));

            // A split-off pile is a proper crystal too and crackles on its own timer.
            split = stackSys.Split(full, 3, map.GridCoords, stack) ?? default;
            Assert.That(split, Is.Not.EqualTo(EntityUid.Invalid));
            Assert.That(entMan.GetComponent<StackComponent>(split).Count, Is.EqualTo(3));
            Assert.That(stack.Count, Is.EqualTo(7));
            Assert.That(entMan.HasComponent<PeriodicCrackleComponent>(split), Is.True);

            // The first crackle is scheduled within the configured interval.
            var comp = entMan.GetComponent<PeriodicCrackleComponent>(full);
            var delay = comp.NextCrackle - timing.CurTime;
            Assert.That(delay, Is.GreaterThan(TimeSpan.Zero));
            Assert.That(delay, Is.LessThanOrEqualTo(comp.MaxInterval));
            Assert.That(comp.Crackling, Is.False);
        });

        // Force a crackle on the floor.
        TimeSpan forcedAt = default;
        await server.WaitPost(() =>
        {
            forcedAt = timing.CurTime;
            entMan.GetComponent<PeriodicCrackleComponent>(full).NextCrackle = forcedAt;
        });
        await pair.RunTicksSync(3);

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<PeriodicCrackleComponent>(full);
            Assert.That(comp.Crackling, Is.True, "a crystal on the floor should crackle");
            Assert.That(appearance.TryGetData<bool>(full, PeriodicCrackleVisuals.Crackling, out var visual) && visual, Is.True);
            Assert.That(comp.NextCrackle - forcedAt, Is.GreaterThanOrEqualTo(comp.MinInterval));
            Assert.That(comp.NextCrackle - forcedAt, Is.LessThanOrEqualTo(comp.MaxInterval));
        });

        // The client shows the crackle overlay layer.
        var clientEnt = client.ResolveDependency<IEntityManager>();
        await client.WaitAssertion(() =>
        {
            var cUid = clientEnt.GetEntity(entMan.GetNetEntity(full));
            var sprite = clientEnt.GetComponent<SpriteComponent>(cUid);
            var spriteSys = clientEnt.System<SpriteSystem>();
            Assert.That(spriteSys.LayerMapTryGet((cUid, sprite), PeriodicCrackleLayers.Crackle, out var layer, false), Is.True);
            Assert.That(sprite[layer].Visible, Is.True, "crackle overlay should be visible on the client");
        });

        // It fades after its duration.
        await pair.RunTicksSync(60);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.GetComponent<PeriodicCrackleComponent>(full).Crackling, Is.False);
            Assert.That(appearance.TryGetData<bool>(full, PeriodicCrackleVisuals.Crackling, out var visual) && visual, Is.False);
        });

        // Packed away in a container (not a hand): no crackle, but the timer moves on.
        await server.WaitPost(() =>
        {
            var box = entMan.SpawnEntity(null, map.GridCoords);
            var container = containers.EnsureContainer<Container>(box, "redspace-test");
            Assert.That(containers.Insert(full, container), Is.True);
            forcedAt = timing.CurTime;
            entMan.GetComponent<PeriodicCrackleComponent>(full).NextCrackle = forcedAt;
        });
        await pair.RunTicksSync(3);
        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<PeriodicCrackleComponent>(full);
            Assert.That(comp.Crackling, Is.False, "a stored crystal should stay quiet");
            Assert.That(comp.NextCrackle, Is.GreaterThan(forcedAt));
            Assert.That(crackleSys.TryCrackle(full, comp), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A held crystal visibly crackles: the server's crackle toggle adds bright arc layers to the holder's
    /// in-hand sprite on the client, and they go away when the crackle ends.
    /// </summary>
    [Test]
    public async Task InhandCrackle()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();

        var entMan = server.ResolveDependency<IEntityManager>();
        var cEntMan = client.ResolveDependency<IEntityManager>();
        var crackleSys = entMan.System<PeriodicCrackleSystem>();
        var hands = entMan.System<SharedHandsSystem>();

        EntityUid mob = default;
        EntityUid crystal = default;
        await server.WaitPost(() =>
        {
            mob = entMan.SpawnEntity("MobHuman", map.GridCoords);
            crystal = entMan.SpawnEntity(Single, map.GridCoords);
            Assert.That(hands.TryPickupAnyHand(mob, crystal, checkActionBlocker: false, animate: false), Is.True);
            var comp = entMan.GetComponent<PeriodicCrackleComponent>(crystal);
            Assert.That(comp.InhandVisuals.Keys, Is.EquivalentTo(new[] { HandLocation.Left, HandLocation.Right }));
            comp.NextCrackle = TimeSpan.MaxValue; // only our forced crackles
        });
        await pair.RunTicksSync(5);

        static bool HasCrackleLayer(IEntityManager ents, EntityUid holder, out bool visible)
        {
            visible = false;
            var handsComp = ents.GetComponent<HandsComponent>(holder);
            var key = handsComp.RevealedLayers.Values.SelectMany(x => x).FirstOrDefault(k => k.EndsWith("-crackle"));
            if (key == null)
                return false;
            var sprite = ents.GetComponent<SpriteComponent>(holder);
            if (!ents.System<SpriteSystem>().LayerMapTryGet((holder, sprite), key, out var layer, false))
                return false;
            visible = sprite[layer].Visible;
            return true;
        }

        await client.WaitAssertion(() =>
        {
            var cMob = cEntMan.GetEntity(entMan.GetNetEntity(mob));
            Assert.That(HasCrackleLayer(cEntMan, cMob, out _), Is.False, "no arcs while not crackling");
        });

        await server.WaitAssertion(() =>
        {
            var comp = entMan.GetComponent<PeriodicCrackleComponent>(crystal);
            Assert.That(crackleSys.TryCrackle(crystal, comp), Is.True, "a held crystal crackles");
        });
        await pair.RunTicksSync(5);

        await client.WaitAssertion(() =>
        {
            var cMob = cEntMan.GetEntity(entMan.GetNetEntity(mob));
            Assert.That(HasCrackleLayer(cEntMan, cMob, out var visible), Is.True, "in-hand crackle layer added");
            Assert.That(visible, Is.True);
        });

        await pair.RunTicksSync(60);
        await client.WaitAssertion(() =>
        {
            var cMob = cEntMan.GetEntity(entMan.GetNetEntity(mob));
            Assert.That(HasCrackleLayer(cEntMan, cMob, out _), Is.False, "in-hand crackle removed after the crackle");
        });

        await pair.CleanReturnAsync();
    }
}

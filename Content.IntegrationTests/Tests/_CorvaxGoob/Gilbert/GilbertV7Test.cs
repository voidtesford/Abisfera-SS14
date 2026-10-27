using System.Linq;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Clothing.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v7: the silver SHOGKK dog tag on Gilbert's neck.
/// </summary>
[TestFixture]
public sealed class GilbertV7Test
{
    private const string Gilbert = "MobGilbert";
    private const string Tag = "ClothingNeckDogtagShogkk";

    /// <summary>He spawns wearing the tag on his neck; it's an ordinary neck item with the right name and text.</summary>
    [Test]
    public async Task DogtagOnNeckTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var inv = server.System<InventorySystem>();

        var uid = EntityUid.Invalid;
        await server.WaitPost(() => uid = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords));
        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            Assert.That(inv.TryGetSlotEntity(uid, "neck", out var tag), "neck slot is empty");
            var meta = entMan.GetComponent<MetaDataComponent>(tag!.Value);
            Assert.Multiple(() =>
            {
                Assert.That(meta.EntityPrototype?.ID, Is.EqualTo(Tag));
                Assert.That(meta.EntityName, Is.EqualTo("жетон ШОГКК"));
                Assert.That(meta.EntityDescription,
                    Is.EqualTo("Гилберт Берран капрал. Жетон присвоенный штурмовой оперативной группе красный космос."));
                Assert.That(entMan.HasComponent<ItemComponent>(tag.Value), "can be picked up");
                Assert.That(entMan.GetComponent<ClothingComponent>(tag.Value).Slots, Is.EqualTo(SlotFlags.NECK));
            });
        });

        await server.WaitPost(() => entMan.DeleteEntity(uid));
        await pair.CleanReturnAsync();
    }

    /// <summary>The berserk transition empties his hands only: the tag stays on his neck.</summary>
    [Test]
    public async Task DogtagStaysThroughBerserkTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var inv = server.System<InventorySystem>();
        var hands = server.System<SharedHandsSystem>();
        var berserk = server.System<GilbertBerserkSystem>();
        var timing = server.ResolveDependency<IGameTiming>();

        var uid = EntityUid.Invalid;
        EntityUid? tag = null;
        await server.WaitPost(() =>
        {
            uid = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            inv.TryGetSlotEntity(uid, "neck", out tag);
            berserk.StartBerserk((uid, entMan.GetComponent<GilbertBerserkComponent>(uid)));
        });
        await server.WaitRunTicks((int) (timing.TickRate * 1.5));

        await server.WaitAssertion(() =>
        {
            Assert.That(tag, Is.Not.Null, "tag on spawn");
            Assert.That(entMan.GetComponent<GilbertBerserkComponent>(uid).Phase, Is.Not.EqualTo(GilbertBerserkPhase.None));
            Assert.That(hands.EnumerateHeld(uid).Any(), Is.False, "hands emptied by the transition");
            Assert.That(inv.TryGetSlotEntity(uid, "neck", out var worn), "tag dropped by berserk");
            Assert.That(worn, Is.EqualTo(tag));
        });

        await server.WaitPost(() => entMan.DeleteEntity(uid));
        await pair.CleanReturnAsync();
    }
}

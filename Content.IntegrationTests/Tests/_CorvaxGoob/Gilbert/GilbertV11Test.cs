using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Goobstation.Maths.FixedPoint;
using Content.IntegrationTests.Pair;
using Content.Server._CorvaxGoob.Gilbert;
using Content.Server._CorvaxGoob.Gilbert.Finisher;
using Content.Server._CorvaxGoob.ProcAnim;
using Content.Server.Gravity;
using Content.Shared._CorvaxGoob.ProcAnim;
using Content.Shared.Damage;
using Content.Shared.Gravity;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Weapons.Ranged.Components;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v11: the finishers use Gilbert's real items - V3 swings his own truncheon (taken from wherever it is and put back
/// there), V3 is never picked without one, and both hands are emptied into his inventory before any finisher
/// (shield / truncheon stowed with a reach each) and re-equipped after.
/// </summary>
[TestFixture]
public sealed class GilbertV11Test
{
    private const string Gilbert = "MobGilbert";
    private const string Victim = "MobHuman";
    private const string GroundPound = "GilbertFinisherGroundPound";

    private static async Task FillFloor(TestPair pair, TestMapData map, int radius)
    {
        await pair.Server.WaitPost(() =>
        {
            var mapSys = pair.Server.System<SharedMapSystem>();
            for (var x = -radius; x <= radius; x++)
            for (var y = -radius; y <= radius; y++)
                mapSys.SetTile(map.Grid.Owner, map.Grid.Comp, new Vector2i(x, y), map.Tile.Tile);
            var gravity = pair.Server.EntMan.EnsureComponent<GravityComponent>(map.Grid);
            pair.Server.System<GravitySystem>().EnableGravity(map.Grid, gravity);
        });
    }

    private static async Task RunSeconds(TestPair pair, double seconds)
    {
        var rate = pair.Server.ResolveDependency<IGameTiming>().TickRate;
        await pair.RunTicksSync((int) Math.Ceiling(seconds * rate) + 1);
    }

    private static void NoAir(IEntityManager entMan, EntityUid uid) =>
        entMan.EnsureComponent<Content.Server.Atmos.Components.PressureImmunityComponent>(uid);

    private static void HoldFire(IEntityManager entMan, IGameTiming timing, EntityUid gilbert)
    {
        entMan.GetComponent<GilbertAIComponent>(gilbert).NextGrenade = timing.CurTime + TimeSpan.FromHours(1);
        var q = entMan.EntityQueryEnumerator<GunComponent>();
        while (q.MoveNext(out _, out var gun))
            gun.NextFire = timing.CurTime + TimeSpan.FromHours(1);
    }

    /// <summary>Entities of <paramref name="proto"/> anywhere on <paramref name="owner"/> (hands, slots, bags).</summary>
    private static List<EntityUid> Carried(IEntityManager entMan, EntityUid owner, params string[] protos)
    {
        var list = new List<EntityUid>();
        var q = entMan.EntityQueryEnumerator<MetaDataComponent>();
        while (q.MoveNext(out var uid, out var meta))
        {
            if (meta.EntityPrototype?.ID is not { } id || !protos.Contains(id))
                continue;
            if (IsCarriedBy(entMan, uid, owner))
                list.Add(uid);
        }

        return list;
    }

    private static bool IsCarriedBy(IEntityManager entMan, EntityUid item, EntityUid owner)
    {
        var xform = entMan.System<SharedTransformSystem>();
        var containers = entMan.System<SharedContainerSystem>();
        var cur = item;
        for (var i = 0; i < 8; i++)
        {
            if (!containers.IsEntityInContainer(cur))
                return false;
            cur = xform.GetParentUid(cur);
            if (cur == owner)
                return true;
        }

        return false;
    }

    private static List<EntityUid> RealHeld(IEntityManager entMan, EntityUid uid) => entMan.System<SharedHandsSystem>()
        .EnumerateHeld(uid).Where(e => !entMan.HasComponent<VirtualItemComponent>(e)).ToList();

    /// <summary>V3 draws the very truncheon from his duffel, the prop is bound to it, and it goes back into the duffel.</summary>
    [Test]
    public async Task GroundPoundUsesRealTruncheon()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var sys = server.System<GilbertFinisherSystem>();
        var containers = server.System<SharedContainerSystem>();
        var hands = server.System<SharedHandsSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(GroundPound);

        EntityUid g = default, v = default, truncheon = default;
        EntityUid fromOwner = default;
        string fromId = "";
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            HoldFire(entMan, timing, g);
            v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            NoAir(entMan, v);
        });
        await RunSeconds(pair, 0.3);

        await server.WaitAssertion(() =>
        {
            var found = Carried(entMan, g, "TruncheonShogkk");
            Assert.That(found, Has.Count.EqualTo(1), "one truncheon in his kit");
            truncheon = found[0];
            Assert.That(hands.IsHolding(g, truncheon), Is.False, "starts stowed");
            Assert.That(containers.TryGetContainingContainer((truncheon, null, null), out var c));
            fromOwner = c!.Owner;
            fromId = c.ID;
            Assert.That(sys.TryStart(g, v, out var err, GroundPound), err);
        });

        var item = proto.Actors[0].Items.First(i => i.Id == "truncheon");
        // Mid-swing (draw 6.5 .. return 10.42): the same entity is in his hand and the scene's bound item.
        await RunSeconds(pair, item.Draw!.Value + 0.6);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g));
            Assert.That(hands.IsHolding(g, truncheon), "his own truncheon drawn into his hand");
            var comp = entMan.GetComponent<ProcAnimComponent>(g);
            Assert.That(comp.Items, Has.Member(truncheon), "the prop is bound to that entity");
            Assert.That(Carried(entMan, g, "TruncheonShogkk", "Truncheon"), Has.Count.EqualTo(1), "no copy spawned");
        });

        await RunSeconds(pair, ProcAnimTimeline.TotalLength(proto) - item.Draw!.Value + 1.5);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g), Is.False, "ended");
            Assert.That(entMan.EntityExists(truncheon), "same entity");
            Assert.That(containers.TryGetContainingContainer((truncheon, null, null), out var c), "put away");
            Assert.That(c!.Owner, Is.EqualTo(fromOwner), "back in the same container");
            Assert.That(c.ID, Is.EqualTo(fromId));
            Assert.That(hands.IsHolding(g, truncheon), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>Without a truncheon V3 is never picked, and the command refuses it with a clear error.</summary>
    [Test]
    public async Task NoTruncheonNoGroundPound()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 6);
        var entMan = server.EntMan;
        var ai = server.System<GilbertAISystem>();
        var sys = server.System<GilbertFinisherSystem>();
        var procAnim = server.System<ProcAnimSystem>();
        var loc = server.ResolveDependency<Robust.Shared.Localization.ILocalizationManager>();

        await server.WaitAssertion(() =>
        {
            var g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            var v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            Assert.That(procAnim.HasRequiredItems(GroundPound, 0, g), "with his kit: allowed");

            foreach (var t in Carried(entMan, g, "TruncheonShogkk", "Truncheon"))
                entMan.DeleteEntity(t);

            Assert.That(procAnim.HasRequiredItems(GroundPound, 0, g), Is.False, "no truncheon");
            Assert.That(procAnim.HasRequiredItems("GilbertFinisherBarrel", 0, g), "the shotgun one still allowed");
            var comp = entMan.GetComponent<GilbertAIComponent>(g);
            for (var i = 0; i < 200; i++)
                Assert.That(ai.PickFinisher((g, comp)), Is.Not.EqualTo(GroundPound));

            Assert.That(sys.TryStart(g, v, out var err, GroundPound), Is.False);
            Assert.That(err, Is.EqualTo("cmd-gilbertfinisher-missing-item"));
            Assert.That(sys.IsPlaying(g), Is.False);
            Assert.That(sys.IsPlaying(v), Is.False);
            Assert.That(loc.HasString(err!), "localised");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Shield + truncheon in hand: both are put away (hands empty, items in his inventory) through the finisher, then
    /// back in his hands (shield active again) and he fights on with them.
    /// </summary>
    [Test]
    public async Task StowsHandsAndReequips()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        await FillFloor(pair, map, 8);
        var entMan = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        var sys = server.System<GilbertFinisherSystem>();
        var ai = server.System<GilbertAISystem>();
        var hands = server.System<SharedHandsSystem>();
        var inventory = server.System<InventorySystem>();
        var toggle = server.System<ItemToggleSystem>();
        var proto = server.ResolveDependency<IPrototypeManager>().Index<ProcAnimScenePrototype>(GilbertFinisherSystem.DefaultFinisher);

        EntityUid g = default, v = default, shield = default, truncheon = default, gun = default;
        await server.WaitPost(() =>
        {
            g = GilbertTestUtil.SpawnActive(entMan, Gilbert, map.GridCoords);
            HoldFire(entMan, timing, g);
            v = entMan.SpawnEntity(Victim, map.GridCoords.Offset(new Vector2(1.3f, 0.1f)));
            NoAir(entMan, v);
        });
        await RunSeconds(pair, 0.3);

        await server.WaitAssertion(() =>
        {
            // Melee loadout: shotgun on the vest, truncheon + raised shield in hand.
            gun = RealHeld(entMan, g).Single(e => entMan.HasComponent<GunComponent>(e));
            Assert.That(inventory.TryEquip(g, gun, "suitstorage", silent: true, force: true));
            truncheon = Carried(entMan, g, "TruncheonShogkk").Single();
            shield = Carried(entMan, g, "TelescopicShieldShogkk").Single();
            Assert.That(hands.TryPickupAnyHand(g, truncheon, checkActionBlocker: false, animate: false));
            Assert.That(hands.TryPickupAnyHand(g, shield, checkActionBlocker: false, animate: false));
            toggle.TryActivate(shield, g, predicted: false);
            Assert.That(toggle.IsActivated(shield), "shield up");
            entMan.GetComponent<GilbertAIComponent>(g).WeaponMode = GilbertWeaponMode.Melee;
            Assert.That(RealHeld(entMan, g), Is.EquivalentTo(new[] { truncheon, shield }));
            Assert.That(sys.TryStart(g, v, out var err), err);
            Assert.That(entMan.GetComponent<ProcAnimComponent>(g).PreBeat, Is.GreaterThan(0.5f), "one stow beat per item");
        });

        // Through the scene: hands empty, both items still on him.
        var total = ProcAnimTimeline.TotalLength(proto) + proto.Intro + 0.64f;
        await RunSeconds(pair, 1.5);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g));
            Assert.That(RealHeld(entMan, g), Is.Empty, "both hands emptied");
            Assert.That(IsCarriedBy(entMan, shield, g), "shield stowed on him");
            Assert.That(IsCarriedBy(entMan, truncheon, g), "truncheon stowed on him");
            Assert.That(inventory.TryGetSlotEntity(g, "belt", out var belt) && IsCarriedBy(entMan, truncheon, belt!.Value)
                        || inventory.TryGetSlotEntity(g, "back", out var back) && IsCarriedBy(entMan, truncheon, back!.Value),
                "truncheon on the belt / in the bag");
        });

        await RunSeconds(pair, total - 1.5 + 1.5);
        await server.WaitAssertion(() =>
        {
            Assert.That(sys.IsPlaying(g), Is.False, "ended");
            Assert.That(hands.IsHolding(g, truncheon), "truncheon back in hand");
            Assert.That(hands.IsHolding(g, shield), "shield back in hand");
            Assert.That(toggle.IsActivated(shield), "shield raised again");
        });

        // He fights on with them.
        EntityUid v2 = default;
        await server.WaitPost(() =>
        {
            // Right next to where the finisher left him (melee range).
            v2 = entMan.SpawnEntity(Victim, entMan.GetComponent<TransformComponent>(g).Coordinates.Offset(new Vector2(-0.9f, 0f)));
            NoAir(entMan, v2);
            ai.ForceCombat(g, v2);
        });
        var hit = false;
        var withGear = false;
        for (var i = 0; i < 24 && !hit; i++)
        {
            await RunSeconds(pair, 0.25);
            await server.WaitPost(() =>
            {
                var dmg = entMan.GetComponent<DamageableComponent>(v2).Damage.DamageDict;
                hit = dmg.Values.Any(x => x > FixedPoint2.Zero);
                if (hit)
                {
                    withGear = hands.IsHolding(g, truncheon);
                    TestContext.Out.WriteLine($"[v11] hit after {i * 0.25:F2}s: {string.Join(",", dmg.Where(x => x.Value > 0).Select(x => $"{x.Key}={x.Value}"))} " +
                                              $"held={string.Join(",", RealHeld(entMan, g).Select(e => entMan.GetComponent<MetaDataComponent>(e).EntityPrototype?.ID))}");
                }
            });
        }

        await server.WaitAssertion(() =>
        {
            Assert.That(hit, "attacks after the finisher");
            Assert.That(withGear, "with his re-equipped truncheon");
        });

        await pair.CleanReturnAsync();
    }
}

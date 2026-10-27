using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Client._CorvaxGoob.Gilbert;
using Content.IntegrationTests.Pair;
using Content.Shared._CorvaxGoob.Gilbert;
using Content.Client.Inventory;
using Content.Shared.Armor;
using Content.Shared.Blocking;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Hands.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Item.ItemToggle;
using Content.Server.Radio.Components;
using Content.Shared.Projectiles;
using Content.Shared.Radio;
using Content.Server.Speech.Components;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Client.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.IntegrationTests.Tests._CorvaxGoob.Gilbert;

/// <summary>
/// v5 gear: looping crackle on Gilbert's items (floor / hands / worn), M8 and truncheon armour penetration,
/// SHOGKK radio, indestructible telescopic shield.
/// </summary>
[TestFixture]
public sealed class GilbertGearV5Test
{
    private const string Shotgun = "WeaponShotgunCombatShogkk";
    private const string Human = "MobHuman";
    private const string Radio = "RadioHandheldShogkk";
    private const string Shield = "TelescopicShieldShogkk";
    private const string Truncheon = "TruncheonShogkk";

    /// <summary>Items with crackle and the inventory slot to try them in (null = hands only).</summary>
    private static readonly (string Proto, string? Slot)[] CrackleItems =
    {
        (Shotgun, "back"),
        (Radio, null),
        (Shield, null),
        (Truncheon, "belt"),
    };

    /// <summary>
    /// Every pellet of a real M8 shot (a high-calibre shell) carries 10% armour penetration, while the pellet
    /// prototype itself is untouched; the bayonet has 10% as well.
    /// </summary>
    [Test]
    public async Task ShotgunArmorPenetrationTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var gun = entMan.SpawnEntity(Shotgun, map.GridCoords);
            var user = entMan.SpawnEntity(Human, map.GridCoords);

            Assert.That(entMan.GetComponent<GilbertArmorPenetrationComponent>(gun).Projectile, Is.EqualTo(0.1f));
            Assert.That(entMan.GetComponent<MeleeWeaponComponent>(gun).Damage.ArmorPenetration,
                Is.EqualTo(0.1f).Within(0.001f), "bayonet");

            var shell = entMan.SpawnEntity("ShellShotgunHighCaliber", map.GridCoords);
            server.System<SharedGunSystem>().Shoot(gun, entMan.GetComponent<GunComponent>(gun), shell,
                map.GridCoords, map.GridCoords.Offset(new Vector2(5f, 0f)), out _, user);

            var pellets = new List<ProjectileComponent>();
            var query = entMan.EntityQueryEnumerator<ProjectileComponent>();
            while (query.MoveNext(out _, out var proj))
            {
                if (proj.Weapon == gun)
                    pellets.Add(proj);
            }

            Assert.That(pellets, Has.Count.GreaterThan(1), "a shotgun shell fires several pellets");
            foreach (var pellet in pellets)
            {
                Assert.That(pellet.Damage.ArmorPenetration, Is.EqualTo(0.1f).Within(0.001f));
            }

            var fresh = entMan.SpawnEntity("HighCaliberPelletShotgun", map.GridCoords);
            Assert.That(entMan.GetComponent<ProjectileComponent>(fresh).Damage.ArmorPenetration,
                Is.LessThan(0.1f), "ordinary pellets are unaffected");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// The SHOGKK radio is on 1910 with speaker and microphone on from spawn, and Gilbert spawns with it in his
    /// pocket.
    /// </summary>
    [Test]
    public async Task RadioTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var channel = server.ProtoMan.Index<RadioChannelPrototype>("GilbertShogkk");
            Assert.That(channel.Frequency, Is.EqualTo(1910));

            var radio = entMan.SpawnEntity(Radio, map.GridCoords);
            var mic = entMan.GetComponent<RadioMicrophoneComponent>(radio);
            var speaker = entMan.GetComponent<RadioSpeakerComponent>(radio);
            Assert.That(mic.Enabled, "microphone on");
            Assert.That(mic.BroadcastChannel, Is.EqualTo("GilbertShogkk"));
            Assert.That(entMan.HasComponent<ActiveListenerComponent>(radio), "microphone actually listening");
            Assert.That(speaker.Enabled, "speaker on");
            Assert.That(entMan.GetComponent<ActiveRadioComponent>(radio).Channels, Does.Contain("GilbertShogkk"));
            Assert.That(entMan.HasComponent<GilbertCrackleComponent>(radio));

            var gilbert = entMan.SpawnEntity("MobGilbert", map.GridCoords);
            Assert.That(server.System<InventorySystem>().TryGetSlotEntity(gilbert, "pocket1", out var pocket),
                "something in Gilbert's pocket");
            Assert.That(entMan.GetComponent<MetaDataComponent>(pocket!.Value).EntityPrototype?.ID, Is.EqualTo(Radio));
            entMan.DeleteEntity(gilbert);
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Gilbert's shield never breaks - neither from direct damage far past the normal break threshold nor while
    /// actively blocking a beating - and a normal telescopic shield still breaks.
    /// </summary>
    [Test]
    public async Task ShieldIndestructibleTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var damageSys = server.System<DamageableSystem>();
        var blunt = server.ProtoMan.Index<DamageTypePrototype>("Blunt");

        EntityUid ours = default, normal = default, blocked = default, user = default;
        await server.WaitAssertion(() =>
        {
            ours = entMan.SpawnEntity(Shield, map.GridCoords);
            normal = entMan.SpawnEntity("TelescopicShield", map.GridCoords);
            foreach (var shield in new[] { ours, normal })
            {
                damageSys.TryChangeDamage(shield, new DamageSpecifier(blunt, 1000), ignoreResistances: true);
            }

            user = entMan.SpawnEntity(Human, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            blocked = entMan.SpawnEntity(Shield, map.GridCoords.Offset(new Vector2(0.5f, 0.5f)));
            Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(user, blocked, checkActionBlocker: false));
            server.System<ItemToggleSystem>().TryActivate(blocked, user, predicted: false);
            Assert.That(server.System<BlockingSystem>().StartBlocking(blocked,
                entMan.GetComponent<BlockingComponent>(blocked), user), "blocking with the shield");
        });

        await pair.RunTicksSync(5);
        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                damageSys.TryChangeDamage(user, new DamageSpecifier(blunt, 60), origin: user);
            }
        });

        await pair.RunTicksSync(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(entMan.Deleted(normal), "a normal telescopic shield still breaks");
            Assert.That(entMan.Deleted(ours), Is.False, "Gilbert's shield survives 1000 damage");
            Assert.That(entMan.Deleted(blocked), Is.False, "Gilbert's shield survives blocking");
            Assert.That(entMan.GetComponent<DamageableComponent>(blocked).TotalDamage.Float(), Is.GreaterThan(140f),
                "the shield really soaked more than the normal break threshold while blocking");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Gilbert's truncheon has 50% armour penetration in its DamageSpecifier (the M8 mechanism) and it really
    /// goes through the armour code: against a bulletproof vest (Blunt x0.8) it deals ~17.9 (20 * 0.8^0.5) instead of 16.
    /// </summary>
    [Test]
    public async Task TruncheonArmorPenetrationTest()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var map = await pair.CreateTestMap();

        await server.WaitAssertion(() =>
        {
            var entMan = server.EntMan;
            var ours = entMan.GetComponent<MeleeWeaponComponent>(entMan.SpawnEntity(Truncheon, map.GridCoords)).Damage;
            var stock = entMan.GetComponent<MeleeWeaponComponent>(entMan.SpawnEntity("Truncheon", map.GridCoords)).Damage;
            Assert.That(ours.ArmorPenetration, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(ours.GetTotal(), Is.EqualTo(stock.GetTotal()), "same damage as a normal truncheon");
            Assert.That(stock.ArmorPenetration, Is.EqualTo(0f), "normal truncheons are unaffected");

            var vest = entMan.GetComponent<ArmorComponent>(entMan.SpawnEntity("ClothingOuterArmorBulletproof", map.GridCoords)).Modifiers;
            var throughVest = DamageSpecifier.ApplyModifierSet(ours, DamageSpecifier.PenetrateArmor(vest, ours.ArmorPenetration));
            var stockThroughVest = DamageSpecifier.ApplyModifierSet(stock, DamageSpecifier.PenetrateArmor(vest, stock.ArmorPenetration));
            Assert.That(stockThroughVest.GetTotal().Float(), Is.EqualTo(16f).Within(0.01f));
            // Armour penetration raises the coefficient to the power (1 - AP): 20 * 0.8^0.5 ~= 17.89.
            Assert.That(throughVest.GetTotal().Float(), Is.EqualTo(20f * MathF.Sqrt(0.8f)).Within(0.01f));
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// Client: each crackling item has a looping crackle layer over its floor sprite, and adds a crackle layer on
    /// top of its in-hand layer and its worn layer on the carrier, always with a matching fx- state.
    /// </summary>
    [Test]
    public async Task CrackleVisualsTest()
    {
        await using var pair = await PoolManager.GetServerClient(new PoolSettings { Connected = true, Dirty = true });
        var server = pair.Server;
        var client = pair.Client;
        var map = await pair.CreateTestMap();
        var tickRate = (int) server.Timing.TickRate;

        EntityUid sPlayer = default;
        await server.WaitPost(() =>
        {
            sPlayer = server.EntMan.SpawnEntity(Human, map.GridCoords);
            server.PlayerMan.SetAttachedEntity(pair.Player!, sPlayer);
        });

        foreach (var (proto, slot) in CrackleItems)
        {
            EntityUid sItem = default;
            await server.WaitPost(() => sItem = server.EntMan.SpawnEntity(proto, map.GridCoords));
            await pair.RunTicksSync(tickRate / 2);

            // Floor.
            await client.WaitAssertion(() =>
            {
                var entMan = client.EntMan;
                var cItem = pair.ToClientUid(sItem);
                var crackle = entMan.GetComponent<GilbertCrackleComponent>(cItem);
                var sprite = entMan.GetComponent<SpriteComponent>(cItem);
                var spriteSys = client.System<SpriteSystem>();
                Assert.That(spriteSys.LayerMapTryGet((cItem, sprite), GilbertCrackleVisualsSystem.LayerKey,
                    out var idx, false), $"{proto}: floor crackle layer");
                var layer = sprite[idx];
                Assert.That(layer.Visible, $"{proto}: floor crackle visible");
                Assert.That(layer.RsiState.Name, Does.StartWith(crackle.StatePrefix));
                Assert.That(layer.ActualRsi?.Path.ToString(), Does.EndWith(crackle.Sprite.ToString()), $"{proto}: crackle RSI");
                Assert.That(layer.AutoAnimated, $"{proto}: animation loops");
                Assert.That(layer.ActualRsi!.TryGetState(layer.RsiState, out var st) && st.DelayCount > 1,
                    $"{proto}: crackle is an animated cycle");
            });

            // In hand.
            await server.WaitAssertion(() =>
                Assert.That(server.System<SharedHandsSystem>().TryPickupAnyHand(sPlayer, sItem, checkActionBlocker: false),
                    $"{proto} picked up"));
            await pair.RunTicksSync(tickRate / 2);
            await client.WaitAssertion(() => AssertCarrierLayer(pair, proto, true));

            // Raised shield: the crackle follows the extended shield in hand and on the floor.
            if (proto == Shield)
            {
                await server.WaitAssertion(() =>
                    Assert.That(server.System<ItemToggleSystem>().TryActivate(sItem, sPlayer, predicted: false), "shield raised"));
                await pair.RunTicksSync(tickRate);
                await client.WaitAssertion(() =>
                    Assert.That(AssertCarrierLayer(pair, proto, true), Has.Some.StartsWith("fx-shield-inhand"),
                        "raised shield crackles in hand"));

                await server.WaitPost(() =>
                    server.System<SharedHandsSystem>().TryDrop(sPlayer, sItem, checkActionBlocker: false));
                await pair.RunTicksSync(tickRate / 2);
                await client.WaitAssertion(() =>
                {
                    var cItem = pair.ToClientUid(sItem);
                    var sprite = client.EntMan.GetComponent<SpriteComponent>(cItem);
                    Assert.That(client.System<SpriteSystem>().LayerMapTryGet((cItem, sprite),
                        GilbertCrackleVisualsSystem.LayerKey, out var idx, false));
                    Assert.That(sprite[idx].RsiState.Name, Is.EqualTo("fx-teleriot-on"), "raised shield on the floor");
                });
            }

            // Worn.
            if (slot != null)
            {
                await server.WaitPost(() =>
                {
                    server.System<SharedHandsSystem>().TryDrop(sPlayer, sItem, checkActionBlocker: false);
                    Assert.That(server.System<InventorySystem>().TryEquip(sPlayer, sItem, slot, true, true),
                        $"{proto} equips to {slot}");
                });
                await pair.RunTicksSync(tickRate / 2);
                await client.WaitAssertion(() => AssertCarrierLayer(pair, proto, false));
            }

            await server.WaitPost(() => server.EntMan.DeleteEntity(sItem));
        }

        await pair.CleanReturnAsync();
    }

    /// <summary>Checks the layers the item added to its carrier (hands or inventory bookkeeping) for a crackle.</summary>
    private static List<string> AssertCarrierLayer(TestPair pair, string proto, bool hands)
    {
        var client = pair.Client;
        var cPlayer = client.Session!.AttachedEntity!.Value;
        var sprite = client.EntMan.GetComponent<SpriteComponent>(cPlayer);
        var spriteSys = client.System<SpriteSystem>();
        var keys = hands
            ? client.EntMan.GetComponent<HandsComponent>(cPlayer).RevealedLayers.Values.SelectMany(k => k).ToList()
            : client.EntMan.GetComponent<InventorySlotsComponent>(cPlayer).VisualLayerKeys.Values.SelectMany(k => k).ToList();
        var crackleKeys = keys.Where(k => k.EndsWith("-gilbert-crackle")).ToList();
        Assert.That(crackleKeys, Is.Not.Empty, $"{proto}: crackle layer on the carrier (keys: {string.Join(", ", keys)})");

        var states = new List<string>();
        foreach (var key in crackleKeys)
        {
            Assert.That(spriteSys.LayerMapTryGet((cPlayer, sprite), key, out var idx, false), key);
            var layer = sprite[idx];
            Assert.That(layer.Visible, key);
            Assert.That(layer.RsiState.Name, Does.StartWith("fx-"), key);
            Assert.That(layer.ActualRsi, Is.Not.Null, key);
            Assert.That(layer.ActualRsi!.TryGetState(layer.RsiState, out var st) && st.DelayCount > 1,
                $"{key}: animated");
            Assert.That(layer.AutoAnimated, key);
            states.Add(layer.RsiState.Name!);
        }

        return states;
    }
}

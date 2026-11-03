using Content.Server.Humanoid;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared._Starlight.CollectiveMind;
using Content.Shared.Clothing;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Content.Shared.Preferences;
using Content.Shared.Random.Helpers;
using Content.Shared.Storage;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// Spawn setup and the (only) look of a SHOGKK operative: random Russian full name on him and on his dog tag, a random
/// male human with glowing scarlet eyes (light off on death), and a random weapon from his list with two spare
/// magazines / shell boxes in the bag.
/// </summary>
public sealed class ShogkkOperativeSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedStorageSystem _storage = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly ShogkkOperativeAISystem _ai = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<ShogkkOperativeGearComponent, MapInitEvent>(OnMapInit, after: [typeof(LoadoutSystem)]);
        SubscribeLocalEvent<ShogkkOperativeComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnMapInit(Entity<ShogkkOperativeGearComponent> ent, ref MapInitEvent args)
    {
        ApplyAppearance(ent);
        ApplyName(ent);
        IssueWeapon(ent);
        _appearance.SetData(ent, ShogkkOperativeVisuals.EyesGlow, true);

        // In no hive mind (and away from CollectiveMindUpdateSystem's static id table, unsafe with several test servers).
        RemComp<CollectiveMindComponent>(ent);
    }

    private void OnMobStateChanged(Entity<ShogkkOperativeComponent> ent, ref MobStateChangedEvent args)
    {
        var alive = args.NewMobState != MobState.Dead;
        _appearance.SetData(ent, ShogkkOperativeVisuals.EyesGlow, alive);
        _light.SetEnabled(ent, alive);
    }

    private void ApplyAppearance(Entity<ShogkkOperativeGearComponent> ent)
    {
        if (!TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
            return;

        var appearance = HumanoidCharacterAppearance.Random(humanoid.Species, Sex.Male).WithEyeColor(ent.Comp.EyeColor);
        var profile = HumanoidCharacterProfile.DefaultWithSpecies(humanoid.Species)
            .WithSex(Sex.Male)
            .WithGender(Robust.Shared.Enums.Gender.Male)
            .WithAge(_random.Next(20, 41))
            .WithCharacterAppearance(appearance);

        _humanoid.LoadProfile(ent, profile, humanoid);
    }

    private void ApplyName(Entity<ShogkkOperativeGearComponent> ent)
    {
        var first = _random.Pick(_proto.Index(ent.Comp.FirstNames));
        var last = _random.Pick(_proto.Index(ent.Comp.LastNames));
        var name = Loc.GetString("shogkk-operative-full-name", ("first", first), ("last", last));
        ent.Comp.FullName = name;
        _meta.SetEntityName(ent, name);

        if (_inventory.TryGetSlotEntity(ent, "neck", out var tag))
            _meta.SetEntityDescription(tag.Value, Loc.GetString("shogkk-operative-dogtag-desc", ("name", name)));
    }

    private void IssueWeapon(Entity<ShogkkOperativeGearComponent> ent)
    {
        if (ent.Comp.Weapons.Count == 0)
            return;

        var pick = _random.Pick(ent.Comp.Weapons);
        ent.Comp.IssuedWeapon = pick.Weapon;
        var coords = Transform(ent).Coordinates;

        var gun = Spawn(pick.Weapon, coords);
        if (!_hands.TryPickupAnyHand(ent, gun, checkActionBlocker: false, animate: false))
        {
            Del(gun);
            return;
        }

        if (TryComp<WieldableComponent>(gun, out var wield))
            _wieldable.TryWield(gun, wield, ent, showMessage: false);

        _ai.SetGun(ent, gun);

        var bag = _inventory.TryGetSlotEntity(ent, "back", out var back) && HasComp<StorageComponent>(back) ? back : null;
        for (var i = 0; i < pick.AmmoCount; i++)
        {
            var ammo = Spawn(pick.Ammo, coords);
            if (bag == null || !_storage.Insert(bag.Value, ammo, out _, playSound: false, stackAutomatically: false))
            {
                if (!_hands.TryPickupAnyHand(ent, ammo, checkActionBlocker: false, animate: false))
                    Log.Warning($"{ToPrettyString(ent)}: no room for spare ammo {pick.Ammo}, left on the floor");
            }
        }
    }
}

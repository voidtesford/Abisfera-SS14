using Content.Server.Humanoid;
using Content.Server.Station.Systems;
using Content.Shared.Random.Helpers;
using Content.Shared.Body.Systems;
using Content.Shared.Clothing;
using Content.Shared._CorvaxGoob.ShogkkOperative;
using Content.Shared._Starlight.CollectiveMind;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Humanoid;
using Content.Shared.Mobs;
using Content.Shared.Preferences;
using Robust.Server.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// Spawn setup and the look of an ordinary SHOGKK zombified: random human (man or woman) with a random Russian full
/// name and a dim, pale amber-red glow in the eyes (light off on death), dressed for a random job of his list,
/// sometimes with a job tool in hand.
/// </summary>
public sealed class ShogkkZombifiedSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private readonly MetaDataSystem _meta = default!;
    [Dependency] private readonly PointLightSystem _light = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly StationSpawningSystem _spawning = default!;

    public override void Initialize()
    {
        base.Initialize();
        // After the body (which grows the hands) and the hands themselves, so the tool can be picked up.
        SubscribeLocalEvent<ShogkkZombifiedGearComponent, MapInitEvent>(OnMapInit,
            after: [typeof(SharedBodySystem), typeof(SharedHandsSystem), typeof(LoadoutSystem)]);
        SubscribeLocalEvent<ShogkkZombifiedComponent, MobStateChangedEvent>(OnMobStateChanged);
    }

    private void OnMapInit(Entity<ShogkkZombifiedGearComponent> ent, ref MapInitEvent args)
    {
        var sex = _random.Prob(0.5f) ? Sex.Male : Sex.Female;
        ApplyAppearance(ent, sex);
        ApplyName(ent, sex);
        Dress(ent);
        _appearance.SetData(ent, ShogkkOperativeVisuals.EyesGlow, true);

        // In no hive mind (and away from CollectiveMindUpdateSystem's static id table, unsafe with several test servers).
        RemComp<CollectiveMindComponent>(ent);
    }

    private void OnMobStateChanged(Entity<ShogkkZombifiedComponent> ent, ref MobStateChangedEvent args)
    {
        var alive = args.NewMobState != MobState.Dead;
        _appearance.SetData(ent, ShogkkOperativeVisuals.EyesGlow, alive);
        _light.SetEnabled(ent, alive);
    }

    private void ApplyAppearance(Entity<ShogkkZombifiedGearComponent> ent, Sex sex)
    {
        if (!TryComp<HumanoidAppearanceComponent>(ent, out var humanoid))
            return;

        var appearance = HumanoidCharacterAppearance.Random(humanoid.Species, sex).WithEyeColor(ent.Comp.EyeColor);
        var gender = sex == Sex.Female ? Robust.Shared.Enums.Gender.Female : Robust.Shared.Enums.Gender.Male;
        var profile = HumanoidCharacterProfile.DefaultWithSpecies(humanoid.Species)
            .WithSex(sex)
            .WithGender(gender)
            .WithAge(_random.Next(20, 61))
            .WithCharacterAppearance(appearance);

        _humanoid.LoadProfile(ent, profile, humanoid);
    }

    private void ApplyName(Entity<ShogkkZombifiedGearComponent> ent, Sex sex)
    {
        var female = sex == Sex.Female;
        var first = _random.Pick(_proto.Index(female ? ent.Comp.FemaleFirstNames : ent.Comp.MaleFirstNames));
        var last = _random.Pick(_proto.Index(ent.Comp.LastNames));
        var name = Loc.GetString(female ? "shogkk-zombified-full-name-female" : "shogkk-zombified-full-name-male",
            ("first", first), ("last", last));
        ent.Comp.FullName = name;
        _meta.SetEntityName(ent, name);
    }

    private void Dress(Entity<ShogkkZombifiedGearComponent> ent)
    {
        if (ent.Comp.Jobs.Count == 0)
            return;

        var job = _random.Pick(ent.Comp.Jobs);
        ent.Comp.Job = job.Id;
        if (job.Gear.Count > 0)
        {
            var outfit = _random.Pick(job.Gear);
            ent.Comp.Outfit = outfit;
            _spawning.EquipStartingGear(ent, outfit, raiseEvent: false);
        }

        if (job.Tools.Count == 0 || !_random.Prob(ent.Comp.ToolChance))
            return;

        var tool = Spawn(_random.Pick(job.Tools), Transform(ent).Coordinates);
        if (!_hands.TryPickupAnyHand(ent, tool, checkActionBlocker: false, animate: false))
        {
            Del(tool);
            return;
        }

        _hands.TrySelect(ent.Owner, tool);
        ent.Comp.Tool = tool;
    }
}

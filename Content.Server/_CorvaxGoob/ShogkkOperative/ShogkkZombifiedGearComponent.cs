using Content.Shared.Dataset;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>What a zombified of one job wears and may hold.</summary>
[DataDefinition]
public sealed partial class ShogkkZombifiedJob
{
    /// <summary>Short job key ("engineer", "security"...), for logs, tests and the admin view.</summary>
    [DataField(required: true)]
    public string Id = string.Empty;

    /// <summary>The job's outfits; one is picked at random (so some engineers wear a hard hat, others a hi-vis vest).</summary>
    [DataField(required: true)]
    public List<ProtoId<StartingGearPrototype>> Gear = new();

    /// <summary>Job melee tools; with <see cref="ShogkkZombifiedGearComponent.ToolChance"/> one is in his hand.</summary>
    [DataField]
    public List<EntProtoId> Tools = new();
}

/// <summary>
/// Spawn setup of an ordinary SHOGKK zombified (ShogkkZombifiedSystem, MapInit): random human of either sex with a
/// random Russian full name, pale amber-red eyes, a job picked from <see cref="Jobs"/> with its outfit, and now and
/// then a job tool in hand. Never a gun.
/// </summary>
[RegisterComponent]
public sealed partial class ShogkkZombifiedGearComponent : Component
{
    [DataField(required: true)]
    public List<ShogkkZombifiedJob> Jobs = new();

    /// <summary>Chance he spawns holding one of his job's tools (and hits with it); otherwise fists.</summary>
    [DataField]
    public float ToolChance = 0.15f;

    [DataField]
    public ProtoId<LocalizedDatasetPrototype> MaleFirstNames = "NamesShogkkOperativeFirst";

    [DataField]
    public ProtoId<LocalizedDatasetPrototype> FemaleFirstNames = "NamesShogkkZombifiedFirstFemale";

    /// <summary>Male surnames; the female form is made by the "shogkk-zombified-full-name-female" string.</summary>
    [DataField]
    public ProtoId<LocalizedDatasetPrototype> LastNames = "NamesShogkkOperativeLast";

    [DataField]
    public Color EyeColor = Color.FromHex("#ff9a7a");

    /// <summary>The job picked at spawn.</summary>
    [ViewVariables]
    public string? Job;

    /// <summary>The outfit picked at spawn.</summary>
    [ViewVariables]
    public ProtoId<StartingGearPrototype>? Outfit;

    /// <summary>The tool he spawned holding, if any.</summary>
    [ViewVariables]
    public EntityUid? Tool;

    [ViewVariables]
    public string? FullName;
}

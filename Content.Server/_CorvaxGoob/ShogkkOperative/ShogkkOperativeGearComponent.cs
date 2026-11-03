using Content.Shared.Dataset;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>One weapon an operative can be issued: the gun (in hand) and the ammo for his bag.</summary>
[DataDefinition]
public sealed partial class ShogkkOperativeWeapon
{
    [DataField(required: true)]
    public EntProtoId Weapon;

    /// <summary>Spare magazines (or shell boxes for the pump shotgun) put in his backpack.</summary>
    [DataField(required: true)]
    public EntProtoId Ammo;

    [DataField]
    public int AmmoCount = 2;
}

/// <summary>
/// Spawn setup of a SHOGKK operative (ShogkkOperativeSystem, MapInit after the loadout): random Russian full name (also
/// on his dog tag), random male human look with scarlet eyes, and a weapon picked from <see cref="Weapons"/>.
/// </summary>
[RegisterComponent]
public sealed partial class ShogkkOperativeGearComponent : Component
{
    [DataField(required: true)]
    public List<ShogkkOperativeWeapon> Weapons = new();

    [DataField]
    public ProtoId<LocalizedDatasetPrototype> FirstNames = "NamesShogkkOperativeFirst";

    [DataField]
    public ProtoId<LocalizedDatasetPrototype> LastNames = "NamesShogkkOperativeLast";

    [DataField]
    public Color EyeColor = Color.FromHex("#ff4a1a");

    /// <summary>The issued weapon, once spawned.</summary>
    [ViewVariables]
    public EntProtoId? IssuedWeapon;

    /// <summary>Full name ("Имя Фамилия"), once spawned.</summary>
    [ViewVariables]
    public string? FullName;
}

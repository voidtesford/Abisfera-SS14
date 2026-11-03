using Robust.Shared.Audio;

namespace Content.Server._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Neck collar that suppresses redspace around its wearer while it has a bluespace crystal to burn.
/// Consumption rule: a crystal is taken out of the slot and starts burning the moment the collar needs charge
/// (worn on an active redspace map and not already covered by a beacon or a gate); it lasts <see cref="CrystalTime"/>
/// seconds of such use. Time spent outside redspace, under a beacon/gate or off the neck costs nothing.
/// </summary>
[RegisterComponent]
public sealed partial class RedspaceCollarComponent : Component
{
    /// <summary>Seconds of active suppression one crystal gives.</summary>
    [DataField]
    public float CrystalTime = 1800f;

    /// <summary>Crystals the slot holds (not counting the one already burning).</summary>
    [DataField]
    public int CrystalCapacity = 1;

    [DataField]
    public string CrystalStack = "BSCrystal";

    [DataField]
    public string Slot = "neck";

    [DataField]
    public SoundSpecifier InsertSound = new SoundPathSpecifier("/Audio/Weapons/Guns/MagIn/batrifle_magin.ogg");

    [DataField]
    public SoundSpecifier DepletedSound = new SoundPathSpecifier("/Audio/Machines/buzz-sigh.ogg");

    /// <summary>Crystals waiting in the slot.</summary>
    [DataField]
    public int Crystals;

    /// <summary>Seconds left in the crystal that is burning now.</summary>
    [DataField]
    public float Remaining;

    [ViewVariables]
    public EntityUid? Wearer;
}

/// <summary>
/// «Маяк подавитель»: anchored and fuelled with super-energy cells it suppresses redspace in a large radius.
/// A cell is taken from storage when the previous one burns out; one lasts <see cref="CellTime"/> seconds.
/// </summary>
[RegisterComponent]
public sealed partial class RedspaceBeaconComponent : Component
{
    [DataField]
    public float CellTime = 1800f;

    /// <summary>Most cells the beacon holds, counting the one burning.</summary>
    [DataField]
    public int MaxCells = 3;

    /// <summary>Cells waiting in storage.</summary>
    [DataField]
    public int Cells;

    /// <summary>Seconds left in the burning cell.</summary>
    [DataField]
    public float Remaining;

    [DataField]
    public SoundSpecifier InsertSound = new SoundPathSpecifier("/Audio/Machines/id_insert.ogg");

    [DataField]
    public SoundSpecifier PowerDownSound = new SoundPathSpecifier("/Audio/Machines/buzz-sigh.ogg");
}

/// <summary>«Сверхэнергетическая ячейка»: fuel for the suppressor beacon.</summary>
[RegisterComponent]
public sealed partial class RedspaceFuelCellComponent : Component;

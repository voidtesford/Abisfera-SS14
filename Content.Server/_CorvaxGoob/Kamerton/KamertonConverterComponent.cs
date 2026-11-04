using Content.Shared._CorvaxGoob.Kamerton;
using Content.Shared.Materials;
using Content.Shared.Stacks;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Kamerton crystal converter: bluespace -> redspace crystals, crystals -> stable/unstable energy,
/// energy -> charged Kamerton cells, grid power + plasma/uranium sheets -> bluespace crystals.
/// </summary>
[RegisterComponent]
public sealed partial class KamertonConverterComponent : Component
{
    [DataField]
    public KamertonConverterMode Mode = KamertonConverterMode.Create;

    [ViewVariables]
    public KamertonConverterWork Work = KamertonConverterWork.Idle;

    [ViewVariables]
    public TimeSpan WorkEnd;

    /// <summary>Bluespace crystals loaded in the top rack.</summary>
    [DataField]
    public int Bluespace;

    /// <summary>Redspace crystals loaded (Destroy mode only).</summary>
    [DataField]
    public int Redspace;

    /// <summary>Rack size; also how many bluespace crystals one redspace crystal costs.</summary>
    [DataField]
    public int MaxCrystals = 10;

    [DataField]
    public ProtoId<StackPrototype> BluespaceStack = "BSCrystal";

    [DataField]
    public ProtoId<StackPrototype> RedspaceStack = "RedspaceCrystal";

    [DataField]
    public EntProtoId BluespaceCrystal = "MaterialBSCrystal1";

    [DataField]
    public EntProtoId RedspaceCrystal = "MaterialRedspaceCrystal1";

    [DataField]
    public float StableEnergy;

    [DataField]
    public float UnstableEnergy;

    /// <summary>Stable energy per ground bluespace crystal.</summary>
    [DataField]
    public float StablePerBluespace = 10f;

    /// <summary>Unstable energy per ground redspace crystal.</summary>
    [DataField]
    public float UnstablePerRedspace = 100f;

    /// <summary>Energy (of the matching kind) one cell charge costs.</summary>
    [DataField]
    public float ChargeCost = 100f;

    /// <summary>Internal battery, filled from the grid while powered.</summary>
    [DataField]
    public float Power;

    [DataField]
    public float MaxPower = 40000f;

    /// <summary>Battery charge per second while the machine is powered.</summary>
    [DataField]
    public float ChargeRate = 100f;

    [DataField]
    public float PrintPower = 1000f;

    /// <summary>Battery energy one redspace crystal costs on top of the 10 bluespace crystals.</summary>
    [DataField]
    public float CreatePower = 10000f;

    /// <summary>Sheets of each of plasma and uranium (MaterialStorage) one printed bluespace crystal costs.</summary>
    [DataField]
    public int PrintSheets = 5;

    [DataField]
    public ProtoId<MaterialPrototype> Plasma = "Plasma";

    [DataField]
    public ProtoId<MaterialPrototype> Uranium = "Uranium";

    [DataField]
    public TimeSpan CreateTime = TimeSpan.FromSeconds(6);

    [DataField]
    public TimeSpan GrindTime = TimeSpan.FromSeconds(4);

    [DataField]
    public TimeSpan PrintTime = TimeSpan.FromSeconds(5);

    [DataField]
    public string CellSlot = "cell";

    [DataField]
    public SoundSpecifier? WorkSound = new SoundPathSpecifier("/Audio/Machines/anomaly_generate.ogg");

    [DataField]
    public SoundSpecifier? DoneSound = new SoundPathSpecifier("/Audio/Machines/phasein.ogg");

    /// <summary>Played when a cell is charged.</summary>
    [DataField]
    public SoundSpecifier? ChargeSound = new SoundPathSpecifier("/Audio/Effects/sparks4.ogg");

    [DataField]
    public SoundSpecifier? DenySound = new SoundPathSpecifier("/Audio/Machines/custom_deny.ogg");

    /// <summary>Last UI refresh, so the battery number moves without spamming states.</summary>
    [ViewVariables]
    public float UiAccumulator;
}

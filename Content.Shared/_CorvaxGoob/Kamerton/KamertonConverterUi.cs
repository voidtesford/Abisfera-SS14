using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Kamerton;

/// <summary>Crystal converter mode, switched with a context-menu verb.</summary>
[Serializable, NetSerializable]
public enum KamertonConverterMode : byte
{
    /// <summary>10 bluespace crystals -> 1 redspace crystal.</summary>
    Create,
    /// <summary>Grinds inserted crystals into stable/unstable energy.</summary>
    Destroy,
}

/// <summary>What the converter is busy with (drives the working animation).</summary>
[Serializable, NetSerializable]
public enum KamertonConverterWork : byte
{
    Idle,
    Creating,
    Grinding,
    Printing,
}

[Serializable, NetSerializable]
public enum KamertonConverterUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum KamertonConverterVisuals : byte
{
    /// <summary>int 0..10: bluespace crystals in the top rack.</summary>
    Fill,
    /// <summary><see cref="KamertonConverterWork"/>.</summary>
    Work,
    /// <summary><see cref="KamertonConverterMode"/>.</summary>
    Mode,
}

[Serializable, NetSerializable]
public enum KamertonConverterVisualLayers : byte
{
    Fill,
    Work,
    Mode,
}

[Serializable, NetSerializable]
public sealed class KamertonConverterBoundUserInterfaceState(
    KamertonConverterMode mode,
    KamertonConverterWork work,
    int bluespace,
    int redspace,
    int maxCrystals,
    float stableEnergy,
    float unstableEnergy,
    float chargeCost,
    float power,
    float maxPower,
    float printPower,
    int plasma,
    int uranium,
    int sheetCost,
    string? cellName,
    bool cellCharged,
    bool cellStable) : BoundUserInterfaceState
{
    public readonly KamertonConverterMode Mode = mode;
    public readonly KamertonConverterWork Work = work;
    public readonly int Bluespace = bluespace;
    public readonly int Redspace = redspace;
    public readonly int MaxCrystals = maxCrystals;
    public readonly float StableEnergy = stableEnergy;
    public readonly float UnstableEnergy = unstableEnergy;
    public readonly float ChargeCost = chargeCost;
    public readonly float Power = power;
    public readonly float MaxPower = maxPower;
    public readonly float PrintPower = printPower;
    /// <summary>Plasma stored, in sheets.</summary>
    public readonly int Plasma = plasma;
    /// <summary>Uranium stored, in sheets.</summary>
    public readonly int Uranium = uranium;
    public readonly int SheetCost = sheetCost;
    public readonly string? CellName = cellName;
    public readonly bool CellCharged = cellCharged;
    public readonly bool CellStable = cellStable;
}

[Serializable, NetSerializable]
public sealed class KamertonConverterStartMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonConverterChargeCellMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonConverterPrintCrystalMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class KamertonConverterEjectMessage : BoundUserInterfaceMessage;

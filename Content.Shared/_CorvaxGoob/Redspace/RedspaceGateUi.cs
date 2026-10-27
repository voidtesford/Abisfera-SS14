using System.Numerics;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Redspace;

[Serializable, NetSerializable]
public enum RedspaceGateUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public enum RedspaceGateVisuals : byte
{
    /// <summary>Portal is open.</summary>
    Active,
    /// <summary>Gate holds any fuel.</summary>
    Fueled,
    /// <summary>Socketed crystals shown in the bluespace rack: 0..10 (one per 5 crystals).</summary>
    BluespaceLevel,
    /// <summary>Socketed crimson crystals shown on the gate frame: 0..12 (one per 2 crystals).</summary>
    RedspaceLevel,
}

[Serializable, NetSerializable]
public enum RedspaceGateVisualLayers : byte
{
    Portal,
    Fuel,
    BluespaceRack,
    RedspaceSockets,
    // Looping arcs around the open portal ring / the exit portal rim.
    PortalFrame,
}

[Serializable, NetSerializable]
public sealed class RedspaceGateBoundUserInterfaceState : BoundUserInterfaceState
{
    public bool Open;
    public Vector2 GatePosition;
    public string TargetMapName = string.Empty;
    public bool DiskInserted;
    /// <summary>False when a disk is inserted but its map is missing / paused / not initialised.</summary>
    public bool DiskValid;
    public Vector2? Target;
    /// <summary>Coordinates stored on the inserted ftldiskgate disk: the portal always opens there.</summary>
    public Vector2? DiskTarget;

    public int Bluespace;
    public int Redspace;
    /// <summary>Capacity of the bluespace storage rack.</summary>
    public int BluespaceCapacity;
    /// <summary>Capacity of the gate's own redspace sockets.</summary>
    public int RedspaceCapacity;
}

[Serializable, NetSerializable]
public sealed class RedspaceGateOpenPortalMessage(float x, float y) : BoundUserInterfaceMessage
{
    public readonly float X = x;
    public readonly float Y = y;
}

[Serializable, NetSerializable]
public sealed class RedspaceGateClosePortalMessage : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class RedspaceGateEjectDiskMessage : BoundUserInterfaceMessage;

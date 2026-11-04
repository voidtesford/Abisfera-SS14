using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

[Serializable, NetSerializable]
public enum RedspaceSuppressorVisuals : byte
{
    /// <summary>bool: the suppressor is working right now.</summary>
    Active,
}

[Serializable, NetSerializable]
public enum RedspaceSuppressorLayers : byte
{
    Lights,
}

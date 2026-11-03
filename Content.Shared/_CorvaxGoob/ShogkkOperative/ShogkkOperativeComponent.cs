using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ShogkkOperative;

/// <summary>
/// A rank-and-file SHOGKK operative: a zombified soldier of the same group as Gilbert, but an ordinary human with
/// Gilbert-like brains (see the server ShogkkOperativeAIComponent). Networked so the client can draw the glowing scarlet eyes.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShogkkOperativeComponent : Component;

[Serializable, NetSerializable]
public enum ShogkkOperativeVisuals : byte
{
    /// <summary>bool: the eyes glow (off once dead).</summary>
    EyesGlow,
}

public enum ShogkkOperativeVisualLayers : byte
{
    Eyes,
    EyesHalo,
}

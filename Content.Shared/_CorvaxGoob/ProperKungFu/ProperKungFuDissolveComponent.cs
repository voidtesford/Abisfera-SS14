using System.Numerics;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// A purely visual effect that, on the client, holds for <see cref="Hold"/> seconds and then dissolves into the air
/// over <see cref="Fade"/> seconds: its sprite and light fade out while it drifts and changes scale.
/// The server only deletes it (TimedDespawn a little after Hold + Fade); nothing here is networked.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuDissolveComponent : Component
{
    /// <summary>Seconds at full brightness before the fade starts.</summary>
    [DataField]
    public float Hold;

    /// <summary>Seconds of fading out, ending fully transparent.</summary>
    [DataField]
    public float Fade = 1f;

    /// <summary>Sprite offset reached by the end of the fade, in tiles (screen up is +Y).</summary>
    [DataField]
    public Vector2 Drift;

    /// <summary>Random extra drift in any direction, up to this length, picked per entity.</summary>
    [DataField]
    public float DriftJitter;

    /// <summary>Sprite scale at spawn and at <see cref="ScaleTime"/>, multiplied with the prototype's scale.</summary>
    [DataField]
    public float ScaleFrom = 1f;

    [DataField]
    public float ScaleTo = 1f;

    /// <summary>Seconds to go from <see cref="ScaleFrom"/> to <see cref="ScaleTo"/> (eased out); 0 means the whole life.</summary>
    [DataField]
    public float ScaleTime;

    // Client-side bookkeeping.
    [ViewVariables]
    public TimeSpan Start;

    [ViewVariables]
    public Vector2 BaseScale = Vector2.One;

    [ViewVariables]
    public Vector2 BaseOffset;

    [ViewVariables]
    public float BaseAlpha = 1f;

    [ViewVariables]
    public float BaseEnergy;

    [ViewVariables]
    public Vector2 DriftTotal;

    [ViewVariables]
    public bool Started;
}

using Robust.Shared.GameStates;

namespace Content.Shared._CorvaxGoob.Redspace.Physics;

/// <summary>
/// Marker: redspace map physics never touch this entity. No organism damage, jitter or vomiting, no inertia, current,
/// waves, weightlessness or slow DoAfters, no blinks, wrap-around, echoes, hallucinations, discharges or radio static,
/// and its own bullets and thrown items don't curve or slide. Given to the zombified SHOGKK: Gilbert and the operatives.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RedspaceImmuneComponent : Component;

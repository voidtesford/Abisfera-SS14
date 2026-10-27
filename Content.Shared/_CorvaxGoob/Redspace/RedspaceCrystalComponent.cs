using Robust.Shared.GameStates;

namespace Content.Shared._CorvaxGoob.Redspace;

/// <summary>
/// Marks a crimson redspace crystal. No behaviour yet: future redspace content (recipes, machines,
/// anomalies) can look for this component instead of hard-coding prototype ids.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class RedspaceCrystalComponent : Component;

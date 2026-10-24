using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Put on a victim right before a Proper Kung Fu user damages it: wounds opened or widened in that same tick
/// bleed more. Removed on the next tick.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuBleedBoostComponent : Component
{
    [ViewVariables]
    public EntityUid Attacker;

    [ViewVariables]
    public GameTick Tick;

    [ViewVariables]
    public float Modifier = 1f;
}

using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;

namespace Content.Server.Nutrition.Components;

[RegisterComponent]
public sealed partial class JunkFoodSlowdownComponent : Component
{
    [DataField("speedMultiplier")]
    public float SpeedMultiplier = 0.70f; 
}
using Robust.Shared.GameStates;
using Robust.Shared.Utility;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Constant, looping red energy crackle on one of Gilbert's items: arcs leaping out around it and running over
/// the sprite itself. It shows on the floor, in-hand and when worn (back, suit storage, belt): for whatever
/// sprite state the item is currently drawn with (e.g. <c>icon</c>, <c>inhand-left</c>, <c>equipped-BACKPACK</c>)
/// the client adds an unshaded layer with the matching <c>fx-&lt;state&gt;</c> state from <see cref="Sprite"/>.
/// The crackle RSI is 64x64 so the arcs can reach outside the 32x32 sprite; it is centred, so it lines up.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class GilbertCrackleComponent : Component
{
    [DataField(required: true)]
    public ResPath Sprite;

    [DataField]
    public string StatePrefix = "fx-";

    /// <summary>Client: crackle state currently shown on the item's own sprite.</summary>
    [ViewVariables]
    public string? CurrentState;
}

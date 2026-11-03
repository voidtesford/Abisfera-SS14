using Robust.Shared.GameStates;

namespace Content.Shared._CorvaxGoob.ShogkkOperative;

/// <summary>
/// An ordinary SHOGKK zombified: a crew member taken by the red space, wandering the place in search of victims (see
/// the server ShogkkZombifiedAIComponent). Not Gilbert and not an operative (though they share
/// BaseMobShogkkZombified). Neutral to the operatives and they to him. Networked so the client can draw the dim, pale
/// amber-red glow of his eyes (same <see cref="ShogkkOperativeVisuals.EyesGlow"/> data as the operatives).
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ShogkkZombifiedComponent : Component;

namespace Content.Shared._CorvaxGoob.ShogkkOperative;

/// <summary>
/// The operatives and the ordinary zombified are NPCs nobody plays. A prototype can't drop the SSDIndicator it inherits
/// from BaseMobHuman, so the server removes it on map init (ShogkkNoSsdIndicatorSystem). Without it they are never
/// "SSD" (a player who left), so they don't get the forced SSD sleep after ic.ssd_sleep_time (10 minutes) and show no
/// SSD icon.
/// </summary>
[RegisterComponent]
public sealed partial class ShogkkNoSsdIndicatorComponent : Component;

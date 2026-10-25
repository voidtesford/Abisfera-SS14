namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// "Blood Harvest" (Кровавая жатва): put by the Bloodlust passive on a victim hurt by a Proper Kung Fu master.
/// While it lasts, every bleed tick of the victim within range heals the master.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuBloodHarvestComponent : Component
{
    [ViewVariables]
    public EntityUid Harvester;

    [ViewVariables]
    public TimeSpan ExpiresAt;

    /// <summary>
    /// The bloodstream's NextUpdate seen last time: when it moves on, the bloodstream has just ticked.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastBloodTick;

    /// <summary>
    /// Floor on the heal rate, in case the bloodstream ticks faster than usual.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextHeal;
}

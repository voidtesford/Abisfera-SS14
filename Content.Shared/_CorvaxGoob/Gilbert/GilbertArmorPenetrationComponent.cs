namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Энни-style armour penetration for one of Gilbert's guns: every projectile (each shotgun pellet) it fires gets
/// <see cref="Projectile"/> written into its <c>DamageSpecifier.ArmorPenetration</c> - the same field Энни's
/// bullets set in YAML (<c>armorPenetration: 0.4</c>), so it is handled by the normal armour code.
/// Works with any ammo loaded into the gun, so reloads from ordinary shell boxes keep the penetration.
/// Melee penetration (bayonet, truncheon) is set directly with <c>armorPenetration</c> in the weapon's damage.
/// </summary>
[RegisterComponent]
public sealed partial class GilbertArmorPenetrationComponent : Component
{
    /// <summary>0.1 = 10% of the target's armour is ignored. Never lowers penetration the ammo already has.</summary>
    [DataField]
    public float Projectile = 0.1f;
}

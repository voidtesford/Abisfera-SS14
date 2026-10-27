using Content.Shared._CorvaxGoob.Gilbert;
using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Events;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>Applies <see cref="GilbertArmorPenetrationComponent"/> to everything the gun fires.</summary>
public sealed class GilbertArmorPenetrationSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertArmorPenetrationComponent, AmmoShotEvent>(OnShot);
    }

    private void OnShot(EntityUid uid, GilbertArmorPenetrationComponent comp, AmmoShotEvent args)
    {
        foreach (var projectile in args.FiredProjectiles)
        {
            Apply(comp, projectile);
        }
    }

    public void Apply(GilbertArmorPenetrationComponent comp, EntityUid projectile)
    {
        if (!TryComp<ProjectileComponent>(projectile, out var proj))
            return;

        // Copy so the change can never leak into a shared/prototype DamageSpecifier instance.
        proj.Damage = new DamageSpecifier(proj.Damage)
        {
            ArmorPenetration = MathF.Max(proj.Damage.ArmorPenetration, comp.Projectile),
        };
    }
}

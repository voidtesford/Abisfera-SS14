using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Body.Components;
using Content.Shared.Mobs.Components;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// Passive "Bloodlust" (Жажда крови): victims hurt by the master get "Blood Harvest" for 30 s. Each time a marked
/// victim within 10 tiles bleeds (one bleed tick of its bloodstream, every few seconds), the master heals 5.
/// Pupils and Wolf Pack clones don't have it.
/// </summary>
public sealed partial class ProperKungFuSystem
{
    /// <summary>
    /// Bleed ticks are already seconds apart; this only guards against a much faster bloodstream.
    /// </summary>
    private static readonly TimeSpan BloodlustMinInterval = TimeSpan.FromSeconds(1);

    private void MarkBloodHarvest(EntityUid attacker, EntityUid target)
    {
        if (attacker == target
            || !TryComp<ProperKungFuComponent>(attacker, out var kungFu)
            || !kungFu.HasPassives
            || HasComp<ProperKungFuCloneComponent>(attacker)
            || !HasComp<MobStateComponent>(target)
            || !TryComp<BloodstreamComponent>(target, out var blood))
            return;

        var now = _timing.CurTime;
        if (!TryComp<ProperKungFuBloodHarvestComponent>(target, out var mark))
        {
            mark = AddComp<ProperKungFuBloodHarvestComponent>(target);
            mark.LastBloodTick = blood.NextUpdate;
        }

        mark.Harvester = attacker;
        mark.ExpiresAt = now + kungFu.BloodlustDuration;
    }

    private void UpdateBloodlust(TimeSpan now)
    {
        var query = EntityQueryEnumerator<ProperKungFuBloodHarvestComponent, BloodstreamComponent>();
        while (query.MoveNext(out var uid, out var mark, out var blood))
        {
            if (now >= mark.ExpiresAt
                || TerminatingOrDeleted(mark.Harvester)
                || !TryComp<ProperKungFuComponent>(mark.Harvester, out var kungFu))
            {
                RemCompDeferred(uid, mark);
                continue;
            }

            if (blood.NextUpdate == mark.LastBloodTick)
                continue;

            // The bloodstream just ticked: did it lose blood to bleeding?
            mark.LastBloodTick = blood.NextUpdate;
            if (blood.BleedAmount <= 0f || now < mark.NextHeal)
                continue;

            var harvester = mark.Harvester;
            if (_mobState.IsDead(harvester))
                continue;

            var harvesterPos = _transform.GetMapCoordinates(harvester);
            var victimPos = _transform.GetMapCoordinates(uid);
            if (harvesterPos.MapId != victimPos.MapId
                || (harvesterPos.Position - victimPos.Position).Length() > kungFu.BloodlustRange)
                continue;

            mark.NextHeal = now + BloodlustMinInterval;
            HealKungFuUser(harvester, kungFu.BloodlustHeal);
        }
    }
}

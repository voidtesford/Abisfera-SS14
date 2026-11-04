using Content.Goobstation.Maths.FixedPoint;
using Content.Server.Radio.Components;
using Content.Shared.Damage;
using Content.Shared.Inventory;
using Content.Shared.Mobs;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;

namespace Content.Server._CorvaxGoob.ShogkkOperative;

/// <summary>
/// The three radio phrases on the SHOGKK channel. Each goes out through his own radio (no radio, no call) as a real
/// radio message players on 1910 hear; what it tells the other operatives is handed over here directly, to those
/// within <see cref="ShogkkOperativeAIComponent.RadioRange"/> of the caller. Every phrase has its own per-operative
/// cooldown (<see cref="ShogkkOperativeAIComponent.RadioCooldown"/>) on top of its trigger.
/// </summary>
public sealed partial class ShogkkOperativeAISystem
{
    #region Phrases

    /// <summary>«Вижу противника»: once per newly seen enemy (not one an ally already called in), with his position.</summary>
    private void TryCallEnemy(Entity<ShogkkOperativeAIComponent> ent, EntityUid enemy, TimeSpan now)
    {
        var comp = ent.Comp;
        if (comp.ReportedEnemies.Contains(enemy) || !CanSay(comp, ShogkkOperativePhrase.EnemySpotted, now))
            return;

        var coords = Transform(enemy).Coordinates;
        var (x, y) = RoundedWorld(coords);
        if (!Say(ent, ShogkkOperativePhrase.EnemySpotted,
                Loc.GetString("shogkk-operative-radio-enemy", ("x", x), ("y", y)), now))
            return;

        comp.ReportedEnemies.Add(enemy);
        foreach (var (ally, _) in AlliesInRange(ent, comp.RadioRange))
        {
            var allyComp = ally.Comp;
            allyComp.ReportedEnemies.Add(enemy);
            allyComp.KnownEnemies[enemy] = new ShogkkKnownEnemy(coords, now);

            // Free ones go and check (the ones fighting / reloading carry on).
            if (allyComp.State is ShogkkOperativeAIState.Patrol or ShogkkOperativeAIState.Search)
                BeginAssist(ally, coords, null);
        }
    }

    /// <summary>
    /// «Нужна помощь»: hurt down to <see cref="ShogkkOperativeAIComponent.HelpHealthMargin"/> HP before crit with no
    /// other operative within <see cref="ShogkkOperativeAIComponent.HelpAllyRange"/>. The nearest free ones in radio
    /// range come to him.
    /// </summary>
    private void TryCallHelp(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (!NeedsHelp(ent) || !CanSay(comp, ShogkkOperativePhrase.NeedHelp, now))
            return;

        var allies = AlliesInRange(ent, comp.RadioRange);
        if (allies.Count > 0 && allies[0].Distance <= comp.HelpAllyRange)
            return;

        var (x, y) = RoundedWorld(xform.Coordinates);
        if (!Say(ent, ShogkkOperativePhrase.NeedHelp, Loc.GetString("shogkk-operative-radio-help", ("x", x), ("y", y)), now))
            return;

        var sent = 0;
        foreach (var (ally, _) in allies)
        {
            if (sent >= comp.HelpResponders)
                break;

            if (ally.Comp.State == ShogkkOperativeAIState.Combat || ally.Comp.Task == ShogkkOperativeTask.Reload)
                continue;

            ally.Comp.AllyPositions[ent] = new ShogkkKnownEnemy(xform.Coordinates, now);
            BeginAssist(ally, xform.Coordinates, ent);
            sent++;
        }
    }

    /// <summary>
    /// «Докладываю» (on patrol, every <see cref="ShogkkOperativeAIComponent.ReportInterval"/>): his position, and his
    /// map knowledge around him (explored cells, known enemies) for the operatives in radio range.
    /// </summary>
    private bool TryReport(Entity<ShogkkOperativeAIComponent> ent, TransformComponent xform, TimeSpan now)
    {
        var comp = ent.Comp;
        if (!CanSay(comp, ShogkkOperativePhrase.Report, now))
            return false;

        var (x, y) = RoundedWorld(xform.Coordinates);
        if (!Say(ent, ShogkkOperativePhrase.Report, Loc.GetString("shogkk-operative-radio-report", ("x", x), ("y", y)), now))
            return false;

        var myCell = CurrentCell(ent, comp, xform);
        var cellRange = (int) MathF.Ceiling(comp.RadioRange / Math.Max(1, comp.CellSize));
        foreach (var (ally, _) in AlliesInRange(ent, comp.RadioRange))
        {
            var allyComp = ally.Comp;
            allyComp.AllyPositions[ent] = new ShogkkKnownEnemy(xform.Coordinates, now);

            if (myCell is { } here)
            {
                foreach (var (key, when) in comp.Visited)
                {
                    if (key.Grid != here.Grid || Math.Abs(key.Cell.X - here.Cell.X) > cellRange
                        || Math.Abs(key.Cell.Y - here.Cell.Y) > cellRange)
                        continue;

                    if (!allyComp.Visited.TryGetValue(key, out var theirs) || theirs < when)
                        allyComp.Visited[key] = when;
                }

                TrimVisited(allyComp);
            }

            foreach (var (enemy, known) in comp.KnownEnemies)
            {
                if (!allyComp.KnownEnemies.TryGetValue(enemy, out var theirs) || theirs.Time < known.Time)
                    allyComp.KnownEnemies[enemy] = known;
            }
        }

        return true;
    }

    #endregion

    #region Helpers

    public bool CanSay(ShogkkOperativeAIComponent comp, ShogkkOperativePhrase phrase, TimeSpan now)
    {
        return !comp.NextPhrase.TryGetValue(phrase, out var next) || now >= next;
    }

    /// <summary>
    /// True when he is down to <see cref="ShogkkOperativeAIComponent.HelpHealthMargin"/> HP (or less) before his
    /// critical threshold.
    /// </summary>
    public bool NeedsHelp(Entity<ShogkkOperativeAIComponent> ent)
    {
        if (!TryComp<DamageableComponent>(ent, out var damageable) || damageable.TotalDamage <= FixedPoint2.Zero)
            return false;

        if (!_thresholds.TryGetThresholdForState(ent, MobState.Critical, out var crit))
            return false;

        return crit.Value - damageable.TotalDamage <= ent.Comp.HelpHealthMargin;
    }

    private bool Say(Entity<ShogkkOperativeAIComponent> ent, ShogkkOperativePhrase phrase, string message, TimeSpan now)
    {
        if (FindRadio(ent, ent.Comp) is not { } radio)
            return false;

        ent.Comp.NextPhrase[phrase] = now + ent.Comp.RadioCooldown;
        ent.Comp.PhraseCount[phrase] = ent.Comp.PhraseCount.GetValueOrDefault(phrase) + 1;
        _radio.SendRadioMessage(ent, message, ent.Comp.RadioChannel, radio);
        return true;
    }

    private static readonly string[] RadioSlots = { "pocket1", "pocket2", "belt", "ears", "neck" };

    /// <summary>His SHOGKK radio: in hand, in a pocket or worn.</summary>
    public EntityUid? FindRadio(EntityUid uid, ShogkkOperativeAIComponent comp)
    {
        foreach (var held in _hands.EnumerateHeld(uid))
        {
            if (IsOperativeRadio(held, comp))
                return held;
        }

        foreach (var slot in RadioSlots)
        {
            if (_inventory.TryGetSlotEntity(uid, slot, out var item) && IsOperativeRadio(item.Value, comp))
                return item;
        }

        return null;
    }

    private bool IsOperativeRadio(EntityUid uid, ShogkkOperativeAIComponent comp)
    {
        return TryComp<RadioMicrophoneComponent>(uid, out var mic) && mic.BroadcastChannel == comp.RadioChannel.Id;
    }

    private (int X, int Y) RoundedWorld(EntityCoordinates coords)
    {
        var pos = _transform.ToMapCoordinates(coords).Position;
        return ((int) MathF.Round(pos.X), (int) MathF.Round(pos.Y));
    }

    #endregion
}

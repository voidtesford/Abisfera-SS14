using System.Linq;
using Content.Server.EntityEffects.EffectConditions;
using Content.Server.EntityEffects.Effects;
using Content.Shared.Body.Components;
using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Goobstation.Maths.FixedPoint;
using Content.Shared.Medical.Healing;
using Content.Shared.Storage;
using Content.Shared.Tools.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Gilbert;

/// <summary>What one reagent does for him, read from its prototype's metabolism effects.</summary>
public sealed class GilbertReagentInfo
{
    /// <summary>Damage type -> damage healed per unit taken (positive = heals).</summary>
    public readonly Dictionary<string, float> HealPerUnit = new();

    /// <summary>Blood volume restored per unit.</summary>
    public float BloodPerUnit;

    /// <summary>Bleeding stopped per unit (positive = less bleeding).</summary>
    public float BleedStopPerUnit;

    /// <summary>Hurts at any dose (poisons, romerol...): never something he takes.</summary>
    public bool Harmful;

    /// <summary>Lowest amount in the blood at which it starts hurting him; null: no overdose.</summary>
    public FixedPoint2? Overdose;

    public bool Useful => HealPerUnit.Count > 0 || BloodPerUnit > 0 || BleedStopPerUnit > 0;
}

/// <summary>What using an item once would do for him.</summary>
public sealed class GilbertTreatment
{
    public readonly Dictionary<string, float> Heals = new();
    public float StopsBleeding;
    public float RestoresBlood;
    public bool Topical;
    /// <summary>Instant (autoinjector / hypospray): the only thing he uses mid-fight.</summary>
    public bool Fast;
    /// <summary>Reagents one use puts into his blood.</summary>
    public readonly Dictionary<string, FixedPoint2> Dose = new();
    /// <summary>Has a reagent that hurts at any dose.</summary>
    public bool Harmful;
}

/// <summary>
/// v9 medicine: a knowledge table of reagents built from the reagent prototypes (what each heals per damage type,
/// blood restoring and bleeding, overdose threshold); every medical item is judged by what it actually does against
/// his current damage, bleeding and blood loss, never pushing a reagent in his blood over its overdose.
/// Topicals are preferred; injectors are for serious damage or when he can't stop for a bandage.
/// </summary>
public sealed partial class GilbertAISystem
{
    private Dictionary<string, GilbertReagentInfo>? _reagentTable;

    private void OnPrototypesReloadedMedicine(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<ReagentPrototype>())
            _reagentTable = null;
    }

    /// <summary>The reagent knowledge table (built once from the prototypes).</summary>
    public IReadOnlyDictionary<string, GilbertReagentInfo> ReagentTable
    {
        get
        {
            _reagentTable ??= BuildReagentTable();
            return _reagentTable;
        }
    }

    public GilbertReagentInfo? ReagentInfo(string reagent)
    {
        return ReagentTable.TryGetValue(reagent, out var info) ? info : null;
    }

    private Dictionary<string, GilbertReagentInfo> BuildReagentTable()
    {
        var table = new Dictionary<string, GilbertReagentInfo>();
        foreach (var reagent in _proto.EnumeratePrototypes<ReagentPrototype>())
        {
            if (reagent.Metabolisms == null)
                continue;

            var info = new GilbertReagentInfo();
            foreach (var entry in reagent.Metabolisms.Values)
            {
                var rate = Math.Max(entry.MetabolismRate.Float(), 0.01f);
                foreach (var effect in entry.Effects)
                {
                    // Overdose-only (or otherwise dose-gated "min") effects: what happens above the threshold.
                    FixedPoint2? minDose = null;
                    var otherConditions = false;
                    if (effect.Conditions != null)
                    {
                        foreach (var condition in effect.Conditions)
                        {
                            if (condition is ReagentThreshold threshold && (threshold.Reagent == null || threshold.Reagent == reagent.ID))
                            {
                                if (threshold.Min > 0)
                                    minDose = minDose == null ? threshold.Min : FixedPoint2.Min(minDose.Value, threshold.Min);
                            }
                            else
                            {
                                otherConditions = true;
                            }
                        }
                    }

                    switch (effect)
                    {
                        case HealthChange health:
                        {
                            var harm = false;
                            foreach (var (type, amount) in health.Damage.DamageDict)
                            {
                                if (amount > 0)
                                {
                                    harm = true;
                                    continue;
                                }

                                if (minDose != null || amount == 0)
                                    continue;

                                var perUnit = health.ScaleByQuantity ? -amount.Float() : -amount.Float() / rate;
                                info.HealPerUnit[type] = info.HealPerUnit.GetValueOrDefault(type) + perUnit;
                            }

                            if (harm && minDose != null)
                                info.Overdose = info.Overdose == null ? minDose : FixedPoint2.Min(info.Overdose.Value, minDose.Value);
                            else if (harm && !otherConditions && !health.Damage.DamageDict.Any(kv => kv.Value < 0))
                                info.Harmful = true;
                            break;
                        }
                        case ModifyBloodLevel blood when minDose == null && blood.Amount > 0:
                            info.BloodPerUnit += blood.Scaled ? blood.Amount.Float() : blood.Amount.Float() / rate;
                            break;
                        case ModifyBleedAmount bleed when minDose == null && bleed.Amount < 0:
                            info.BleedStopPerUnit += bleed.Scaled ? -bleed.Amount : -bleed.Amount / rate;
                            break;
                    }
                }
            }

            if (info.Useful || info.Harmful || info.Overdose != null)
                table[reagent.ID] = info;
        }

        return table;
    }

    /// <summary>How much of a reagent is in his blood right now.</summary>
    public FixedPoint2 InBloodstream(EntityUid uid, string reagent)
    {
        if (!TryComp<BloodstreamComponent>(uid, out var blood)
            || !_solutions.TryGetSolution(uid, blood.ChemicalSolutionName, out _, out var chem))
            return FixedPoint2.Zero;

        return chem.GetTotalPrototypeQuantity(reagent);
    }

    /// <summary>Would one use of this treatment push any reagent over its overdose threshold?</summary>
    public bool WouldOverdose(EntityUid uid, GilbertTreatment treatment)
    {
        foreach (var (reagent, dose) in treatment.Dose)
        {
            if (ReagentInfo(reagent)?.Overdose is not { } od)
                continue;

            // A small margin: metabolism is not instant and the threshold itself already hurts.
            if (InBloodstream(uid, reagent) + dose >= od - FixedPoint2.New(0.5f))
                return true;
        }

        return false;
    }

    /// <summary>What one use of this item does, or null if it is not medicine.</summary>
    public GilbertTreatment? GetTreatment(EntityUid item)
    {
        if (TerminatingOrDeleted(item))
            return null;

        if (TryComp<HealingComponent>(item, out var healing))
        {
            var t = new GilbertTreatment { Topical = true };
            foreach (var (type, amount) in healing.Damage.DamageDict)
            {
                if (amount < 0)
                    t.Heals[type] = -amount.Float();
            }

            t.StopsBleeding = Math.Max(0f, -healing.BloodlossModifier);
            // Shitmed (Goob) bodies: a negative modifyBloodLevel restores blood (blood packs use -30); nothing drains it.
            t.RestoresBlood = Math.Abs(healing.ModifyBloodLevel);
            return t;
        }

        string? solutionName = null;
        FixedPoint2 transfer = FixedPoint2.Zero;
        var fast = false;
        if (TryComp<HyposprayComponent>(item, out var hypo))
        {
            solutionName = hypo.SolutionName;
            transfer = hypo.TransferAmount;
            fast = true;
        }
        else if (TryComp<InjectorComponent>(item, out var injector) && injector.ToggleState == InjectorToggleMode.Inject)
        {
            solutionName = injector.SolutionName;
            transfer = injector.TransferAmount;
        }

        if (solutionName == null || !_solutions.TryGetSolution(item, solutionName, out _, out var solution) || solution.Volume <= 0)
            return null;

        var chem = new GilbertTreatment { Fast = fast };
        var fraction = Math.Min(1f, transfer.Float() / solution.Volume.Float());
        foreach (var quantity in solution.Contents)
        {
            var dose = quantity.Quantity * fraction;
            var id = quantity.Reagent.Prototype;
            chem.Dose[id] = chem.Dose.GetValueOrDefault(id) + dose;
            if (ReagentInfo(id) is not { } info)
                continue;

            chem.Harmful |= info.Harmful;
            foreach (var (type, perUnit) in info.HealPerUnit)
                chem.Heals[type] = chem.Heals.GetValueOrDefault(type) + perUnit * dose.Float();
            chem.RestoresBlood += info.BloodPerUnit * dose.Float();
            chem.StopsBleeding += info.BleedStopPerUnit * dose.Float();
        }

        return chem;
    }

    /// <summary>Does this item treat anything a body like his can have (and nothing in it hurts)?</summary>
    public bool IsUsefulMedicine(EntityUid uid, EntityUid item)
    {
        if (HasComp<ToolComponent>(item))
            return false;

        if (TryComp<HealingComponent>(item, out var healing) && healing.DamageContainers is { } containers
            && (!TryComp<DamageableComponent>(uid, out var damageable) || damageable.DamageContainerID is not { } mine
                || !containers.Contains(mine)))
            return false; // cable coils & co. fix robots, not him.

        if (GetTreatment(item) is not { } t || t.Harmful)
            return false;

        return t.Heals.Count > 0 || t.StopsBleeding > 0 || t.RestoresBlood > 0;
    }

    /// <summary>A first-aid kit or similar box that holds medicine he'd use.</summary>
    public bool IsMedkitWithMeds(EntityUid uid, EntityUid item)
    {
        if (!TryComp<StorageComponent>(item, out var storage) || !IsMedkit(item))
            return false;

        return storage.Container.ContainedEntities.Any(e => IsUsefulMedicine(uid, e) && HealCharges(e) is > 0f);
    }

    public bool IsMedkit(EntityUid item)
    {
        var id = MetaData(item).EntityPrototype?.ID;
        return HasComp<StorageComponent>(item) && id != null
               && (id.StartsWith("Medkit") || id.Contains("FirstAid") || id.StartsWith("Stimkit"));
    }

    private static float MaxBlood(BloodstreamComponent blood)
    {
        FixedPoint2 max = blood.BloodMaxVolume;
        return max.Float();
    }

    /// <summary>Bleeding rate, blood fraction (1 = full).</summary>
    public (float Bleed, float Blood) BloodState(EntityUid uid)
    {
        if (!TryComp<BloodstreamComponent>(uid, out var blood))
            return (0f, 1f);

        var fraction = 1f;
        var max = MaxBlood(blood);
        if (_solutions.TryGetSolution(uid, blood.BloodSolutionName, out _, out var sol) && max > 0)
            fraction = sol.Volume.Float() / max;

        return (blood.BleedAmount, fraction);
    }

    /// <summary>Does he need patching up at all (damage, bleeding or blood loss)?</summary>
    public bool NeedsTreatment(GilbertAIComponent comp, EntityUid uid, DamageableComponent damageable, bool starting)
    {
        var (bleed, blood) = BloodState(uid);
        if (bleed >= comp.HealBleedThreshold || blood < (starting ? comp.HealBloodThreshold : comp.HealBloodStop))
            return true;

        return damageable.TotalDamage >= (starting ? comp.HealStartDamage : comp.HealStopDamage + FixedPoint2.New(0.01f));
    }

    /// <summary>
    /// How much good one use of this item does him now: damage it would actually heal (of the kinds he has),
    /// bleeding stopped, blood restored. 0 = useless now, or it would overdose him.
    /// </summary>
    public float ScoreTreatment(GilbertAIComponent comp, EntityUid uid, EntityUid item, DamageableComponent damageable, bool fastOnly = false)
    {
        if (!IsConsumableHealItem(comp, item) || !IsUsefulMedicine(uid, item) || GetTreatment(item) is not { } t)
            return 0f;

        if (fastOnly && !t.Fast)
            return 0f;

        if (WouldOverdose(uid, t))
            return 0f;

        var damageScore = 0f;
        var damageHave = 0f;
        foreach (var (type, heal) in t.Heals)
        {
            if (!damageable.Damage.DamageDict.TryGetValue(type, out var have) || have <= 0)
                continue;
            damageScore += Math.Min(have.Float(), heal);
            damageHave += have.Float();
        }

        var (bleed, blood) = BloodState(uid);
        var bleedScore = 0f;
        if (bleed > 0.05f && t.StopsBleeding > 0)
            bleedScore = 20f + Math.Min(bleed, t.StopsBleeding) * 10f;

        var bloodScore = 0f;
        if (blood < comp.HealBloodStop && t.RestoresBlood > 0 && TryComp<BloodstreamComponent>(uid, out var stream))
        {
            var missing = (comp.HealBloodStop - blood) * MaxBlood(stream);
            bloodScore = Math.Min(missing, t.RestoresBlood) * 0.5f + (blood < comp.HealBloodThreshold ? 15f : 0f);
        }

        // Injections are for serious damage of their kind (or for blood / bleeding): a pen is one big dose.
        if (!t.Topical && damageHave < comp.InjectorMinDamage)
            damageScore = 0f;

        var score = damageScore + bleedScore + bloodScore;
        return t.Topical ? score * comp.TopicalPreference : score;
    }
}

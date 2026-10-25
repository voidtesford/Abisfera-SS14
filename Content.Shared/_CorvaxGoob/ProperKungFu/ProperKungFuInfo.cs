using Content.Goobstation.Common.MartialArts;
using Robust.Shared.Utility;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// What the "Proper Kung Fu" section of the character info window lists: every combo and skill with its icon,
/// name and a short description, the combo inputs, the passives, the synergies and the finishers with their thresholds.
/// </summary>
public static class ProperKungFuInfo
{
    public enum Kind : byte
    {
        Combo,
        Skill,
        Passive,
        Synergy,
        Finisher,
    }

    public sealed record Entry(
        Kind Kind,
        SpriteSpecifier? Icon,
        string Name,
        string Description,
        ComboAttackType[] Inputs,
        float? Threshold = null);

    private const string ActionRsi = "/Textures/_CorvaxGoob/Interface/Actions/proper_kung_fu.rsi";
    private const string ComboRsi = "/Textures/_CorvaxGoob/Interface/Misc/proper_kung_fu_info.rsi";
    public const string IntentRsi = "/Textures/_CorvaxGoob/Interface/Misc/proper_kung_fu_intents.rsi";

    public static readonly ComboAttackType[] ClawSeriesInputs = { ComboAttackType.Harm, ComboAttackType.Harm, ComboAttackType.Harm };
    public static readonly ComboAttackType[] WolfGripInputs = { ComboAttackType.Harm, ComboAttackType.Disarm, ComboAttackType.Grab };
    public static readonly ComboAttackType[] PerfectFlowInputs = { ComboAttackType.Disarm, ComboAttackType.Harm, ComboAttackType.Disarm, ComboAttackType.Harm };

    /// <summary>
    /// Claw Mastery: every combo shortened to its last two inputs (no sequence is a prefix of another).
    /// </summary>
    public static readonly ComboAttackType[] UltClawSeriesInputs = { ComboAttackType.Harm, ComboAttackType.Harm };
    public static readonly ComboAttackType[] UltWolfGripInputs = { ComboAttackType.Disarm, ComboAttackType.Grab };
    public static readonly ComboAttackType[] UltPerfectFlowInputs = { ComboAttackType.Disarm, ComboAttackType.Harm };

    /// <summary>
    /// Finisher thresholds: damage the living target must already have. Checked against the prototypes by the tests.
    /// </summary>
    public const float ClawSeriesFinisherThreshold = 80f;
    public const float WolfGripFinisherThreshold = 80f;
    public const float PerfectFlowFinisherThreshold = 80f;
    public const float WolfDanceFinisherThreshold = 85f;
    public const float ClawWhirlwindFinisherThreshold = 80f;
    public const float HammerFinisherThreshold = 80f;
    public const float WolfInstinctFinisherThreshold = 85f;
    public const float KunaiFinisherThreshold = 80f;

    /// <summary>
    /// The intent icon state for a combo input.
    /// </summary>
    public static SpriteSpecifier IntentIcon(ComboAttackType type)
    {
        var state = type switch
        {
            ComboAttackType.Disarm => "disarm",
            ComboAttackType.Grab => "grab",
            _ => "harm",
        };
        return new SpriteSpecifier.Rsi(new ResPath(IntentRsi), state);
    }

    private static SpriteSpecifier Action(string state) => new SpriteSpecifier.Rsi(new ResPath(ActionRsi), state);
    private static SpriteSpecifier Combo(string state) => new SpriteSpecifier.Rsi(new ResPath(ComboRsi), state);

    private static Entry Make(Kind kind, SpriteSpecifier? icon, string key, ComboAttackType[]? inputs = null, float? threshold = null)
        => new(kind, icon, $"proper-kung-fu-info-{key}-name", $"proper-kung-fu-info-{key}-desc", inputs ?? Array.Empty<ComboAttackType>(), threshold);

    public static List<Entry> GetEntries(bool pupil)
    {
        if (pupil)
        {
            return new List<Entry>
            {
                Make(Kind.Combo, Combo("claw_series"), "pupil-claw-series", ClawSeriesInputs),
                Make(Kind.Combo, Combo("wolf_grip"), "pupil-wolf-grip", WolfGripInputs),
                Make(Kind.Combo, Combo("perfect_flow"), "pupil-perfect-flow", PerfectFlowInputs),
                Make(Kind.Passive, null, "pupil-limits"),
            };
        }

        return new List<Entry>
        {
            Make(Kind.Combo, Combo("claw_series"), "claw-series", ClawSeriesInputs),
            Make(Kind.Combo, Combo("wolf_grip"), "wolf-grip", WolfGripInputs),
            Make(Kind.Combo, Combo("perfect_flow"), "perfect-flow", PerfectFlowInputs),
            Make(Kind.Combo, Combo("claw_series"), "ult-claw-series", UltClawSeriesInputs),
            Make(Kind.Combo, Combo("wolf_grip"), "ult-wolf-grip", UltWolfGripInputs),
            Make(Kind.Combo, Combo("perfect_flow"), "ult-perfect-flow", UltPerfectFlowInputs),

            Make(Kind.Skill, Action("wolf_dance"), "wolf-dance"),
            Make(Kind.Skill, Action("claw_whirlwind"), "claw-whirlwind"),
            Make(Kind.Skill, Action("hammer"), "hammer"),
            Make(Kind.Skill, Action("wolf_instinct"), "wolf-instinct"),
            Make(Kind.Skill, Action("wolf_pack"), "wolf-pack"),
            Make(Kind.Skill, Action("kunai"), "kunai"),
            Make(Kind.Skill, Action("join_pack"), "join-pack"),
            Make(Kind.Skill, Action("ultimate"), "ultimate"),
            Make(Kind.Skill, Action("ultimate_clones"), "ultimate-clones"),
            Make(Kind.Skill, Action("summon_pack"), "summon-pack"),
            Make(Kind.Skill, Action("wolf_judgment"), "wolf-judgment"),
            Make(Kind.Skill, Action("wolf_gaze"), "wolf-gaze"),
            Make(Kind.Skill, Action("wolf_seal"), "wolf-seal"),
            Make(Kind.Skill, Action("ultimate_kunai"), "fan-kunai"),

            Make(Kind.Passive, null, "passive-speed"),
            Make(Kind.Passive, null, "passive-gunshot"),
            Make(Kind.Passive, null, "passive-bleed"),
            Make(Kind.Passive, null, "passive-no-guns"),
            Make(Kind.Passive, null, "passive-heal"),
            Make(Kind.Passive, null, "passive-bloodlust"),

            Make(Kind.Synergy, Combo("claw_series"), "synergy-series-dance"),
            Make(Kind.Synergy, Action("claw_whirlwind"), "synergy-whirlwind-dance"),
            Make(Kind.Synergy, Combo("wolf_grip"), "synergy-grip-whirlwind"),
            Make(Kind.Synergy, Combo("wolf_grip"), "synergy-grip-hammer"),
            Make(Kind.Synergy, Action("wolf_dance"), "synergy-dance-hammer"),

            Make(Kind.Finisher, Combo("claw_series"), "finisher-claw-series", threshold: ClawSeriesFinisherThreshold),
            Make(Kind.Finisher, Combo("wolf_grip"), "finisher-wolf-grip", threshold: WolfGripFinisherThreshold),
            Make(Kind.Finisher, Combo("perfect_flow"), "finisher-perfect-flow", threshold: PerfectFlowFinisherThreshold),
            Make(Kind.Finisher, Action("wolf_dance"), "finisher-wolf-dance", threshold: WolfDanceFinisherThreshold),
            Make(Kind.Finisher, Action("claw_whirlwind"), "finisher-claw-whirlwind", threshold: ClawWhirlwindFinisherThreshold),
            Make(Kind.Finisher, Action("hammer"), "finisher-hammer", threshold: HammerFinisherThreshold),
            Make(Kind.Finisher, Action("wolf_instinct"), "finisher-wolf-instinct", threshold: WolfInstinctFinisherThreshold),
            Make(Kind.Finisher, Action("kunai"), "finisher-kunai", threshold: KunaiFinisherThreshold),
        };
    }

    /// <summary>
    /// Locale id of a section header.
    /// </summary>
    public static string SectionName(Kind kind) => kind switch
    {
        Kind.Combo => "proper-kung-fu-info-section-combos",
        Kind.Skill => "proper-kung-fu-info-section-skills",
        Kind.Passive => "proper-kung-fu-info-section-passives",
        Kind.Synergy => "proper-kung-fu-info-section-synergies",
        _ => "proper-kung-fu-info-section-finishers",
    };
}

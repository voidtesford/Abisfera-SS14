using System.Diagnostics.CodeAnalysis;
using Content.Shared.Players.PlayTimeTracking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Traits;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Added by the "Pack Hound" trait: teaches the pupil form of Proper Kung Fu when the character spawns, then goes away.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuPackHoundComponent : Component;

/// <summary>
/// Requires some time on a play time tracker. Unlike <c>RoleTimeRequirement</c> the tracker doesn't have to belong to a
/// job, so a tracker can be used as a persistent per-player unlock flag (play times are stored in the database).
/// </summary>
[Serializable, NetSerializable]
public sealed partial class PlayTimeTrackerRequirement : JobRequirement
{
    [DataField(required: true)]
    public ProtoId<PlayTimeTrackerPrototype> Tracker;

    [DataField]
    public TimeSpan Time = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Shown when the requirement isn't met.
    /// </summary>
    [DataField(required: true)]
    public LocId Reason;

    public override bool Check(IEntityManager entManager,
        IPrototypeManager protoManager,
        HumanoidCharacterProfile? profile,
        IReadOnlyDictionary<string, TimeSpan> playTimes,
        [NotNullWhen(false)] out FormattedMessage? reason)
    {
        reason = new FormattedMessage();
        playTimes.TryGetValue(Tracker, out var time);
        var met = time >= Time;
        if (Inverted)
            met = !met;

        if (met)
            return true;

        reason = FormattedMessage.FromUnformatted(Loc.GetString(Reason));
        return false;
    }
}

public static class ProperKungFuPackHound
{
    /// <summary>
    /// The play time tracker that marks a player who has been taught by a master ("Join the Pack").
    /// </summary>
    public const string Tracker = "ProperKungFuPackHound";

    public const string Trait = "ProperKungFuPackHound";

    /// <summary>
    /// The time put on <see cref="Tracker"/> when a player is taught; any time above zero unlocks the trait.
    /// </summary>
    public static readonly TimeSpan UnlockTime = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Checks a trait's requirements against a player's play times. Always enforced, regardless of the role timer cvar.
    /// </summary>
    public static bool TraitRequirementsMet(TraitPrototype trait,
        IEntityManager entManager,
        IPrototypeManager protoManager,
        HumanoidCharacterProfile? profile,
        IReadOnlyDictionary<string, TimeSpan> playTimes,
        [NotNullWhen(false)] out FormattedMessage? reason)
    {
        reason = null;
        if (trait.Requirements == null)
            return true;

        var reasons = new List<string>();
        foreach (var requirement in trait.Requirements)
        {
            if (!requirement.Check(entManager, protoManager, profile, playTimes, out var why))
                reasons.Add(why.ToMarkup());
        }

        if (reasons.Count == 0)
            return true;

        reason = FormattedMessage.FromMarkupOrThrow(string.Join('\n', reasons));
        return false;
    }
}

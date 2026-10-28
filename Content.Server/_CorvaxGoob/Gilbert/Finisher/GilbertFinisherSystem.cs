using System.Diagnostics.CodeAnalysis;
using Content.Server._CorvaxGoob.ProcAnim;
using Content.Shared._CorvaxGoob.ProcAnim;
using Robust.Shared.Prototypes;

namespace Content.Server._CorvaxGoob.Gilbert.Finisher;

/// <summary>
/// Gilbert's phase-1 finishers: a thin wrapper over the generic <see cref="ProcAnimSystem"/>. The three variants are
/// plain <see cref="ProcAnimScenePrototype"/> scenes (<c>Resources/Prototypes/_CorvaxGoob/Gilbert/finisher.yml</c>)
/// with the roles <c>attacker</c> and <c>victim</c>; this only maps the variant numbers and the old error ids.
/// </summary>
public sealed class GilbertFinisherSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly ProcAnimSystem _procAnim = default!;

    public const string DefaultFinisher = "GilbertFinisherPhase1";

    /// <summary>The three phase-1 variants, in command order (1 = kick combo, 2 = shotgun barrel, 3 = ground and pound).</summary>
    public static readonly string[] Variants = { DefaultFinisher, "GilbertFinisherBarrel", "GilbertFinisherGroundPound" };

    public const string AttackerRole = "attacker";
    public const string VictimRole = "victim";

    /// <summary>"1".."3" or a scene prototype id → prototype id; null when unknown.</summary>
    public string? ResolveVariant(string arg)
    {
        if (int.TryParse(arg, out var n))
            return n >= 1 && n <= Variants.Length ? Variants[n - 1] : null;
        return _proto.HasIndex<ProcAnimScenePrototype>(arg) ? arg : null;
    }

    public bool IsPlaying(EntityUid uid) => _procAnim.IsPlaying(uid);

    /// <summary>Starts the cutscene. Returns false with a locale id in <paramref name="error"/> when it can't.</summary>
    public bool TryStart(EntityUid attacker, EntityUid victim, [NotNullWhen(false)] out string? error,
        string finisher = DefaultFinisher, bool keepDown = true)
    {
        if (_procAnim.TryPlay(finisher, new[] { attacker, victim }, out error, keepDown))
            return true;

        // Keep the command's old wording ("cmd-gilbertfinisher-busy", ...).
        error = error.Replace("procanim-error-", "cmd-gilbertfinisher-");
        if (error == "cmd-gilbertfinisher-actor-count")
            error = "cmd-gilbertfinisher-no-proto";
        return false;
    }

    /// <summary>Stops the cutscene on <paramref name="uid"/> and its partner, restoring both.</summary>
    public void Stop(EntityUid uid) => _procAnim.Stop(uid);

    public void ApplyHit(EntityUid attacker, EntityUid target, ProcAnimHit hit) => _procAnim.ApplyHit(attacker, target, hit);

    /// <summary>The victim's <see cref="ProcAnimActor.FinalDamage"/> of <paramref name="proto"/> (armour ignored, untargeted).</summary>
    public void ApplyFinalDamage(EntityUid victim, EntityUid? attacker, ProcAnimScenePrototype proto)
    {
        _procAnim.ApplyFinalDamage(victim, attacker, proto.Actor(VictimRole).FinalDamage);
    }
}

using Content.Shared._Shitmed.Medical.Surgery.Traumas;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Components;
using Content.Shared._Shitmed.Medical.Surgery.Traumas.Systems;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Body.Systems;
using Robust.Shared.Network;
using Robust.Shared.Timing;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert's bones never break, in either phase. Wounds and damage are untouched; only bone traumas are stopped.
/// </summary>
[RegisterComponent]
public sealed partial class GilbertFractureImmuneComponent : Component
{
    /// <summary>How often the server re-checks his limbs (new limbs, bones hit directly by a martial art or a reagent).</summary>
    [DataField]
    public TimeSpan CheckInterval = TimeSpan.FromSeconds(0.5);

    [ViewVariables]
    public TimeSpan NextCheck;
}

/// <summary>Marks a body part or bone of a fracture-immune body.</summary>
[RegisterComponent]
public sealed partial class GilbertFractureImmunePartComponent : Component;

/// <summary>
/// Two layers:
/// <list type="bullet">
/// <item>Every bone trauma a wound would cause on one of his limbs is cancelled (no fracture, crack sound, popup
/// or pain from it).</item>
/// <item>Anything that damages a bone directly (CQC leg break, bone-damage reagents) is undone right away: his bones
/// are put back to full integrity on the next check.</item>
/// </list>
/// </summary>
public sealed class GilbertFractureImmunitySystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly TraumaSystem _trauma = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GilbertFractureImmuneComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<GilbertFractureImmunePartComponent, BeforeTraumaInducedEvent>(OnBeforeTrauma);
    }

    private void OnMapInit(Entity<GilbertFractureImmuneComponent> ent, ref MapInitEvent args)
    {
        Protect(ent);
    }

    private void OnBeforeTrauma(Entity<GilbertFractureImmunePartComponent> ent, ref BeforeTraumaInducedEvent args)
    {
        if (args.TraumaType == TraumaType.BoneDamage)
            args.Cancelled = true;
    }

    /// <summary>Marks his limbs and bones and mends any bone that got damaged anyway.</summary>
    public void Protect(EntityUid body)
    {
        foreach (var part in _body.GetBodyChildren(body))
        {
            EnsureComp<GilbertFractureImmunePartComponent>(part.Id);
            if (!TryComp<WoundableComponent>(part.Id, out var woundable))
                continue;

            foreach (var bone in woundable.Bone.ContainedEntities)
            {
                EnsureComp<GilbertFractureImmunePartComponent>(bone);
                if (TryComp<BoneComponent>(bone, out var boneComp)
                    && (boneComp.BoneIntegrity < boneComp.IntegrityCap || boneComp.BoneSeverity != BoneSeverity.Normal))
                {
                    _trauma.SetBoneIntegrity(bone, boneComp.IntegrityCap, boneComp);
                }
            }
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        if (!_net.IsServer)
            return;

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<GilbertFractureImmuneComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (now < comp.NextCheck)
                continue;

            comp.NextCheck = now + comp.CheckInterval;
            Protect(uid);
        }
    }
}

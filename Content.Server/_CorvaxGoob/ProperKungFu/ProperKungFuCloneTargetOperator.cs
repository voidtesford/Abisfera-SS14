using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.NPC;
using Content.Server.NPC.HTN;
using Content.Server.NPC.HTN.PrimitiveTasks;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Map;

namespace Content.Server._CorvaxGoob.ProperKungFu;

/// <summary>
/// First task of the Proper Kung Fu clone plan, the same pattern as the stock UtilityOperator: the
/// <see cref="ProperKungFuSystem"/> only writes the pack's victim into <see cref="SourceKey"/>, and this operator turns
/// it into the <see cref="TargetKey"/> / <see cref="TargetCoordinatesKey"/> planning effects that the following
/// MoveTo and Melee tasks of the same branch read. The system never writes or removes the keys those operators read,
/// so a plan can never start a MoveTo whose coordinates were removed while the plan was being built.
/// </summary>
public sealed partial class ProperKungFuCloneTargetOperator : HTNOperator
{
    [Dependency] private readonly IEntityManager _entManager = default!;

    public const string DefaultSourceKey = "ProperKungFuCloneTarget";

    [DataField]
    public string SourceKey = DefaultSourceKey;

    [DataField]
    public string TargetKey = "Target";

    [DataField]
    public string TargetCoordinatesKey = "TargetCoordinates";

    public override async Task<(bool Valid, Dictionary<string, object>? Effects)> Plan(NPCBlackboard blackboard,
        CancellationToken cancelToken)
    {
        if (!blackboard.TryGetValue<EntityUid>(SourceKey, out var target, _entManager) || !IsValidTarget(target))
            return (false, null);

        return (true, new Dictionary<string, object>
        {
            { TargetKey, target },
            { TargetCoordinatesKey, new EntityCoordinates(target, Vector2.Zero) },
        });
    }

    public override HTNOperatorStatus Update(NPCBlackboard blackboard, float frameTime)
    {
        base.Update(blackboard, frameTime);

        // The plan may have been built for a victim the pack has dropped since; fail it so a fresh plan is made
        // instead of walking to a stale (possibly deleted) target.
        if (!blackboard.TryGetValue<EntityUid>(SourceKey, out var wanted, _entManager)
            || !blackboard.TryGetValue<EntityUid>(TargetKey, out var planned, _entManager)
            || wanted != planned
            || !IsValidTarget(planned)
            || !blackboard.ContainsKey(TargetCoordinatesKey))
            return HTNOperatorStatus.Failed;

        return HTNOperatorStatus.Finished;
    }

    private bool IsValidTarget(EntityUid target)
    {
        return target.IsValid()
               && !_entManager.Deleted(target)
               && _entManager.TryGetComponent<MetaDataComponent>(target, out var meta)
               && meta.EntityLifeStage < EntityLifeStage.Terminating
               && !_entManager.System<MobStateSystem>().IsDead(target);
    }
}

using Content.Server.NodeContainer.NodeGroups;
using Content.Server.NodeContainer.Nodes;
using Content.Shared.NodeContainer;
using Content.Shared.NodeContainer.NodeGroups;

namespace Content.Server._CorvaxGoob.Kamerton;

/// <summary>
/// Node group of a Kamerton reactor, the same idea as the AME's <c>AmeNodeGroup</c>: every part carries an
/// <see cref="AdjacentNode"/> on <see cref="NodeGroupID.KamertonReactor"/>, so anchored parts standing
/// side by side (N/E/S/W) form one group. The first core in the group is the master: it reads the group
/// for its layout and stays the source of temperature and output. Extra cores in the same group idle.
/// </summary>
[NodeGroup(NodeGroupID.KamertonReactor)]
public sealed class KamertonNodeGroup : BaseNodeGroup
{
    [Dependency] private readonly IEntityManager _entMan = default!;

    /// <summary>The core that drives this group, if any.</summary>
    [ViewVariables]
    public EntityUid? MasterCore { get; private set; }

    [ViewVariables]
    public int CoreCount { get; private set; }

    public override void LoadNodes(List<Node> groupNodes)
    {
        base.LoadNodes(groupNodes);

        MasterCore = null;
        CoreCount = 0;
        foreach (var node in groupNodes)
        {
            if (!_entMan.HasComponent<KamertonReactorCoreComponent>(node.Owner))
                continue;

            CoreCount++;
            MasterCore ??= node.Owner;
        }

        var reactor = _entMan.System<KamertonReactorSystem>();
        reactor.UpdateLinkVisuals(this);
        if (MasterCore is { } core && _entMan.TryGetComponent<KamertonReactorCoreComponent>(core, out var comp))
            reactor.Scan((core, comp));
    }
}

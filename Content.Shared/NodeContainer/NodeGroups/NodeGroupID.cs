namespace Content.Shared.NodeContainer.NodeGroups;

public enum NodeGroupID : byte
{
    Default,
    HVPower,
    MVPower,
    Apc,
    AMEngine,
    Pipe,
    WireNet,

    /// <summary>
    /// Group used by the TEG.
    /// </summary>
    /// <seealso cref="Content.Server.Power.Generation.Teg.TegSystem"/>
    /// <seealso cref="Content.Server.Power.Generation.Teg.TegNodeGroup"/>
    Teg,

    // CorvaxGoob-Kamerton-start
    /// <summary>
    /// Kamerton reactor parts, linked like the AME shielding.
    /// </summary>
    KamertonReactor,
    // CorvaxGoob-Kamerton-end
}

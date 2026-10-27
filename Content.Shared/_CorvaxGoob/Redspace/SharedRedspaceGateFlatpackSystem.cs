using Content.Shared.Examine;

namespace Content.Shared._CorvaxGoob.Redspace;

public abstract class SharedRedspaceGateFlatpackSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RedspaceGateFlatpackComponent, ExaminedEvent>(OnExamined);
        SubscribeLocalEvent<RedspaceGateUnfoldingComponent, ExaminedEvent>(OnUnfoldingExamined);
    }

    private void OnExamined(Entity<RedspaceGateFlatpackComponent> ent, ref ExaminedEvent args)
    {
        if (args.IsInDetailsRange)
            args.PushMarkup(Loc.GetString("redspace-gate-flatpack-examine"));
    }

    private void OnUnfoldingExamined(Entity<RedspaceGateUnfoldingComponent> ent, ref ExaminedEvent args)
    {
        args.PushMarkup(Loc.GetString("redspace-gate-flatpack-examine-unfolding"));
    }
}

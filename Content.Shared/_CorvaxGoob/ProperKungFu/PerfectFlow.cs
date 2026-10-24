using System.Numerics;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Movement.Systems;
using Content.Shared.StatusEffectNew;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Result event of the "Perfect Flow" combo (Disarm, Harm, Disarm, Harm), the last Proper Kung Fu combo.
/// 10 Blunt to both arms and the torso, broken arms on a battered target, a strong disorientation and,
/// on a badly beaten target, a spinning kick to the head from behind.
/// </summary>
[Serializable, NetSerializable, DataDefinition]
public sealed partial class ProperKungFuPerfectFlowPerformedEvent : EntityEventArgs
{
    /// <summary>
    /// Damage dealt to each of: left arm, right arm, torso. Hands are not hit.
    /// </summary>
    [DataField]
    public float PartDamage = 10f;

    [DataField]
    public ProtoId<DamageTypePrototype> DamageType = "Blunt";

    /// <summary>
    /// Total damage of the target (after this combo's damage) from which both arms break.
    /// </summary>
    [DataField]
    public float FractureThreshold = 50f;

    [DataField]
    public EntProtoId DisorientEffect = "StatusEffectProperKungFuDisoriented";

    [DataField]
    public TimeSpan DisorientTime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Total damage of the target (after this combo's damage) from which the finisher happens.
    /// </summary>
    [DataField]
    public float FinisherThreshold = 80f;

    /// <summary>
    /// The spinning kick only hits the head.
    /// </summary>
    [DataField]
    public float FinisherDamage = 20f;

    [DataField]
    public ProtoId<DamageTypePrototype> FinisherDamageType = "Blunt";

    /// <summary>
    /// Distance (tiles, center to center) behind the target where the user appears.
    /// </summary>
    [DataField]
    public float BehindDistance = 0.8f;

    [DataField]
    public float SpinDuration = 0.35f;

    [DataField]
    public EntProtoId? ArmCutEffect = "ProperKungFuFlowArmCuts";

    [DataField]
    public EntProtoId? PalmsEffect = "ProperKungFuFlowPalms";

    [DataField]
    public EntProtoId? AxeKickEffect = "ProperKungFuFlowAxeKick";

    [DataField]
    public EntProtoId? AfterimageEffect = "ProperKungFuFlowAfterimage";

    [DataField]
    public SoundSpecifier? HitSound;

    [DataField]
    public SoundSpecifier? FractureSound;

    [DataField]
    public SoundSpecifier? FinisherSound;
}

/// <summary>
/// Sent to clients in PVS so they spin the user's sprite for the Perfect Flow finisher.
/// </summary>
[Serializable, NetSerializable]
public sealed class ProperKungFuFlowSpinEvent : EntityEventArgs
{
    public NetEntity User;
    public float Duration;

    public ProperKungFuFlowSpinEvent(NetEntity user, float duration)
    {
        User = user;
        Duration = duration;
    }
}

/// <summary>
/// Put on the status effect entity of the Perfect Flow disorientation. The client draws a heavy
/// blur / double vision / flash overlay for the affected player while it lasts.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProperKungFuDisorientedStatusEffectComponent : Component
{
    /// <summary>
    /// Walk and sprint speed multiplier while disoriented.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float SpeedModifier = 0.65f;

    /// <summary>
    /// Speed (tiles/s) of the random stumbles.
    /// </summary>
    [DataField, AutoNetworkedField]
    public float StaggerSpeed = 2.5f;

    /// <summary>
    /// Random interval between stumbles, (min, max) seconds.
    /// </summary>
    [DataField, AutoNetworkedField]
    public Vector2 StaggerInterval = new(0.35f, 0.7f);

    [DataField, AutoNetworkedField]
    public float JitterAmplitude = 18f;

    [DataField, AutoNetworkedField]
    public float JitterFrequency = 7f;
}

/// <summary>
/// Kept on the disoriented mob itself (the status effect lives on a separate entity): slows it down and
/// makes it stumble around. Added and removed together with the status effect.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ProperKungFuDisorientedComponent : Component
{
    [DataField, AutoNetworkedField]
    public float SpeedModifier = 0.65f;

    [DataField, AutoNetworkedField]
    public float StaggerSpeed = 2.5f;

    [DataField, AutoNetworkedField]
    public Vector2 StaggerInterval = new(0.35f, 0.7f);

    /// <summary>
    /// Server only: when the next stumble happens.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextStagger;
}

public sealed class SharedProperKungFuDisorientSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly StatusEffectsSystem _statusEffects = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ProperKungFuDisorientedStatusEffectComponent, StatusEffectAppliedEvent>(OnApplied);
        SubscribeLocalEvent<ProperKungFuDisorientedStatusEffectComponent, StatusEffectRemovedEvent>(OnRemoved);
        SubscribeLocalEvent<ProperKungFuDisorientedComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    private void OnApplied(Entity<ProperKungFuDisorientedStatusEffectComponent> ent, ref StatusEffectAppliedEvent args)
    {
        var mob = EnsureComp<ProperKungFuDisorientedComponent>(args.Target);
        mob.SpeedModifier = ent.Comp.SpeedModifier;
        mob.StaggerSpeed = ent.Comp.StaggerSpeed;
        mob.StaggerInterval = ent.Comp.StaggerInterval;
        Dirty(args.Target, mob);
        _movementSpeed.RefreshMovementSpeedModifiers(args.Target);
    }

    private void OnRemoved(Entity<ProperKungFuDisorientedStatusEffectComponent> ent, ref StatusEffectRemovedEvent args)
    {
        if (TerminatingOrDeleted(args.Target))
            return;

        RemComp<ProperKungFuDisorientedComponent>(args.Target);
        _movementSpeed.RefreshMovementSpeedModifiers(args.Target);
    }

    private void OnRefreshSpeed(Entity<ProperKungFuDisorientedComponent> ent, ref RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(ent.Comp.SpeedModifier, ent.Comp.SpeedModifier);
    }

    public bool IsDisoriented(EntityUid uid)
    {
        return _statusEffects.HasEffectComp<ProperKungFuDisorientedStatusEffectComponent>(uid);
    }
}

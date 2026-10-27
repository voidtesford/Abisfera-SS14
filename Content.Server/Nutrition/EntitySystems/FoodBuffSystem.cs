using Content.Server.Nutrition.Components;
using Content.Shared.Movement.Systems;
using Content.Shared.Nutrition;
using Content.Shared.Nutrition.Components;
using Content.Shared.Tag;

namespace Content.Server.Nutrition.EntitySystems;

public sealed class FoodBuffSystem : EntitySystem
{
    [Dependency] private readonly MovementSpeedModifierSystem _movementSpeed = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;

    private const string JunkFoodTag = "JunkFood";

    public override void Initialize()
    {
        base.Initialize();

        // AfterFullyEatenEvent — readonly record struct, срабатывает на еде после того, как её съели.
        // args.User — тот, кто съел.
        SubscribeLocalEvent<FoodComponent, AfterFullyEatenEvent>(OnFoodIngested);

        SubscribeLocalEvent<JunkFoodSlowdownComponent, RefreshMovementSpeedModifiersEvent>(OnRefreshSpeed);
    }

    private void OnFoodIngested(EntityUid uid, FoodComponent component, ref AfterFullyEatenEvent args)
    {
        var consumer = args.User;

        if (consumer == EntityUid.Invalid)
            return;

        if (_tagSystem.HasTag(uid, JunkFoodTag))
        {
            EnsureComp<JunkFoodSlowdownComponent>(consumer);
            _movementSpeed.RefreshMovementSpeedModifiers(consumer);
        }
        else
        {
            if (RemComp<JunkFoodSlowdownComponent>(consumer))
            {
                _movementSpeed.RefreshMovementSpeedModifiers(consumer);
            }
        }
    }

    private void OnRefreshSpeed(EntityUid uid, JunkFoodSlowdownComponent component, RefreshMovementSpeedModifiersEvent args)
    {
        args.ModifySpeed(component.SpeedMultiplier, component.SpeedMultiplier);
    }
}
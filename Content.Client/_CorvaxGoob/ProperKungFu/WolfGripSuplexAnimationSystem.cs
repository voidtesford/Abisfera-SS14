using System.Numerics;
using Content.Client.Rotation;
using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Rotation;
using Robust.Client.Animations;
using RobustAnimation = Robust.Client.Animations.Animation;
using Robust.Client.GameObjects;
using Robust.Shared.Animations;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// The Wolf Grip finisher (bridge suplex): the target arcs over the user's back with a half flip and hits the floor
/// head first behind them, staying upside-down for a moment before the normal lying-down rotation takes over.
/// The movement itself is done (and networked) by the server.
/// </summary>
public sealed class WolfGripSuplexAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly RotationVisualizerSystem _rotation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string AnimationKey = "proper-kung-fu-grip-suplex";

    /// <summary>
    /// How long the target stays upside-down on the floor after the impact.
    /// </summary>
    private const float LandHold = 0.3f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ProperKungFuGripSuplexAnimationEvent>(OnSuplex);
        SubscribeLocalEvent<SpriteComponent, AnimationCompletedEvent>(OnAnimationCompleted);
    }

    private void OnSuplex(ProperKungFuGripSuplexAnimationEvent ev)
    {
        var uid = GetEntity(ev.Target);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !HasComp<SpriteComponent>(uid))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        var rest = CompOrNull<RotationVisualsComponent>(uid)?.VerticalRotation ?? Angle.Zero;

        // Thrown backwards over the user: the feet go up first, rotating towards the flight direction.
        var sign = ev.Direction.X >= 0f ? -1.0 : 1.0;
        _animation.Play((uid, player), Suplex(rest, sign, ev.Duration, ev.Height), AnimationKey);
    }

    private void OnAnimationCompleted(Entity<SpriteComponent> ent, ref AnimationCompletedEvent args)
    {
        if (args.Key != AnimationKey)
            return;

        _sprite.SetOffset((ent, ent.Comp), Vector2.Zero);
        var visuals = CompOrNull<RotationVisualsComponent>(ent);
        if (visuals != null
            && _appearance.TryGetData<RotationState>(ent, RotationVisuals.RotationState, out var state)
            && state == RotationState.Horizontal)
        {
            _rotation.AnimateSpriteRotation(ent, ent.Comp, visuals.HorizontalRotation, visuals.AnimationTime);
            return;
        }

        _sprite.SetRotation((ent, ent.Comp), visuals?.VerticalRotation ?? Angle.Zero);
    }

    private static RobustAnimation Suplex(Angle rest, double sign, float duration, float height)
    {
        var length = MathF.Max(duration, 0.05f);

        // Two quarter turns (head down at the end), so the interpolation never takes the short way around.
        var rotation = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Rotation),
            InterpolationMode = AnimationInterpolationMode.Linear,
            KeyFrames =
            {
                new AnimationTrackProperty.KeyFrame(rest, 0f),
                new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(90 * sign), length / 2f),
                new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(180 * sign), length / 2f),
                new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(180 * sign), LandHold),
            },
        };

        // Up over the user's back, then straight down into the floor.
        var offset = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Offset),
            InterpolationMode = AnimationInterpolationMode.Cubic,
            KeyFrames =
            {
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height), length * 0.5f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.6f), length * 0.25f),
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.25f),
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, LandHold),
            },
        };

        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length + LandHold),
            AnimationTracks = { rotation, offset },
        };
    }
}

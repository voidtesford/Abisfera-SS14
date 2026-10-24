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
/// Plays the sprite animation of each Wolf Dance phase: a full forward roll, a forward lean during the dash
/// and a quick lean back for the high kick. The actual movement is done (and networked) by the server.
/// </summary>
public sealed class WolfDanceAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly RotationVisualizerSystem _rotation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string AnimationKey = "wolf-dance";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<WolfDanceAnimationEvent>(OnAnimation);
    }

    private void OnAnimation(WolfDanceAnimationEvent ev)
    {
        var uid = GetEntity(ev.User);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        var rest = TryComp<RotationVisualsComponent>(uid, out var rotationVisuals)
            ? rotationVisuals.VerticalRotation
            : Angle.Zero;

        // Rolling to the right on screen is a clockwise (negative) rotation.
        var sign = ev.Direction.X > 0.05f || MathF.Abs(ev.Direction.X) <= 0.05f ? -1.0 : 1.0;

        switch (ev.Phase)
        {
            case WolfDancePhase.Roll:
                Play(uid, player, RollAnimation(rest, sign, ev.Duration));
                break;
            case WolfDancePhase.Dash:
                Play(uid, player, DashAnimation(rest, sign, ev.Duration));
                break;
            case WolfDancePhase.Kick:
                Play(uid, player, KickAnimation(rest, sign, ev.Duration));
                break;
            case WolfDancePhase.Cancel:
                RestoreRotation(uid, sprite, rotationVisuals, rest);
                break;
        }
    }

    private void Play(EntityUid uid, AnimationPlayerComponent player, RobustAnimation animation)
    {
        _animation.Play((uid, player), animation, AnimationKey);
    }

    /// <summary>
    /// If the move was interrupted (e.g. knocked down), put the sprite back to the angle it should have.
    /// </summary>
    private void RestoreRotation(EntityUid uid, SpriteComponent sprite, RotationVisualsComponent? visuals, Angle rest)
    {
        if (visuals != null
            && _appearance.TryGetData<RotationState>(uid, RotationVisuals.RotationState, out var state)
            && state == RotationState.Horizontal)
        {
            _rotation.AnimateSpriteRotation(uid, sprite, visuals.HorizontalRotation, visuals.AnimationTime);
            return;
        }

        _sprite.SetRotation((uid, sprite), rest);
    }

    private static RobustAnimation RollAnimation(Angle rest, double sign, float duration)
    {
        var length = MathF.Max(duration, 0.05f);
        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Rotation),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(rest, 0f),
                        new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(120 * sign), length / 3f),
                        new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(240 * sign), length / 3f),
                        new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(360 * sign), length / 3f),
                        new AnimationTrackProperty.KeyFrame(rest, 0f),
                    },
                },
                // Tuck down a bit while rolling over the floor.
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Offset),
                    InterpolationMode = AnimationInterpolationMode.Cubic,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, -0.15f), length / 2f),
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, length / 2f),
                    },
                },
            },
        };
    }

    private static RobustAnimation DashAnimation(Angle rest, double sign, float duration)
    {
        var length = MathF.Max(duration, 0.05f);
        var lean = rest + Angle.FromDegrees(30 * sign);
        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Rotation),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(rest, 0f),
                        new AnimationTrackProperty.KeyFrame(lean, length * 0.3f),
                        new AnimationTrackProperty.KeyFrame(lean, length * 0.7f),
                    },
                },
                // Low, fast lunge.
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Offset),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, 0.12f), length * 0.5f),
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.5f),
                    },
                },
            },
        };
    }

    private static RobustAnimation KickAnimation(Angle rest, double sign, float duration)
    {
        var length = MathF.Max(duration, 0.1f);
        // Lean back while the leg swings up into the high roundhouse kick, then recover.
        var leanBack = rest + Angle.FromDegrees(-35 * sign);
        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks =
            {
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Rotation),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(30 * sign), 0f),
                        new AnimationTrackProperty.KeyFrame(leanBack, length * 0.2f),
                        new AnimationTrackProperty.KeyFrame(leanBack, length * 0.4f),
                        new AnimationTrackProperty.KeyFrame(rest, length * 0.4f),
                    },
                },
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Offset),
                    InterpolationMode = AnimationInterpolationMode.Linear,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, 0.08f), length * 0.3f),
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.7f),
                    },
                },
            },
        };
    }
}

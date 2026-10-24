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
/// Plays the sprite animation of the Wolf Dance flying kick: the sprite tips over to fly feet first in a straight
/// line, the feet hit, then the user drops to the floor. The actual movement is done (and networked) by the server.
/// </summary>
public sealed class WolfDanceAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly RotationVisualizerSystem _rotation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string AnimationKey = "wolf-dance";

    /// <summary>
    /// How high (tiles) the sprite rises in the middle of the leap.
    /// </summary>
    private const float LeapHeight = 0.25f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<WolfDanceAnimationEvent>(OnAnimation);
        SubscribeNetworkEvent<WolfDanceTossAnimationEvent>(OnToss);
    }

    private const string TossKey = "wolf-dance-toss";

    /// <summary>
    /// Plain Wolf Dance: the target flies straight up with a flip and drops back onto the same spot.
    /// The server keeps it in place and knocks it down on landing.
    /// </summary>
    private void OnToss(WolfDanceTossAnimationEvent ev)
    {
        var uid = GetEntity(ev.Target);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !HasComp<SpriteComponent>(uid))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, TossKey))
            _animation.Stop((uid, player), TossKey);

        var rest = CompOrNull<RotationVisualsComponent>(uid)?.VerticalRotation ?? Angle.Zero;
        // Flips backwards, away from the user's dash.
        var sign = ev.Direction.X >= 0f ? 1.0 : -1.0;
        _animation.Play((uid, player), TossAnimation(rest, sign, ev.Duration, ev.Height, ev.Spins), TossKey);
    }

    private static RobustAnimation TossAnimation(Angle rest, double sign, float duration, float height, float spins)
    {
        var length = MathF.Max(duration, 0.1f);

        // Quarter turns, so the linear interpolation never takes the short way around.
        var rotation = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Rotation),
            InterpolationMode = AnimationInterpolationMode.Linear,
        };
        var steps = Math.Max(1, (int) MathF.Round(spins * 4f));
        rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest, 0f));
        for (var i = 1; i <= steps; i++)
        {
            rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(90 * i * sign), length / steps));
        }

        // Fast rise, a short hang at the top, quicker drop.
        var offset = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Offset),
            InterpolationMode = AnimationInterpolationMode.Cubic,
            KeyFrames =
            {
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.75f), length * 0.25f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height), length * 0.2f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.7f), length * 0.2f),
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.35f),
            },
        };

        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks = { rotation, offset },
        };
    }

    private void OnAnimation(WolfDanceAnimationEvent ev)
    {
        var uid = GetEntity(ev.User);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        TryComp<RotationVisualsComponent>(uid, out var rotationVisuals);
        var rest = rotationVisuals?.VerticalRotation ?? Angle.Zero;
        var lying = rotationVisuals?.HorizontalRotation ?? Angle.FromDegrees(90);

        // Feet first: flying to the right on screen the head points left (counter-clockwise, positive).
        var sign = ev.Direction.X < -0.05f ? -1.0 : 1.0;
        var pose = rest + Angle.FromDegrees(90 * sign);

        switch (ev.Phase)
        {
            case WolfDancePhase.Dash:
                Play(uid, player, FlightAnimation(rest, pose, ev.Duration));
                break;
            case WolfDancePhase.Kick:
                Play(uid, player, LandingAnimation(pose, lying, ev.Duration));
                break;
            case WolfDancePhase.PassThrough:
                // Upright, a slight forward lean for the whole dash.
                Play(uid, player, DashLeanAnimation(rest, rest - Angle.FromDegrees(15 * sign), ev.Duration));
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
        _sprite.SetOffset((uid, sprite), Vector2.Zero);

        if (visuals != null
            && _appearance.TryGetData<RotationState>(uid, RotationVisuals.RotationState, out var state)
            && state == RotationState.Horizontal)
        {
            _rotation.AnimateSpriteRotation(uid, sprite, visuals.HorizontalRotation, visuals.AnimationTime);
            return;
        }

        _sprite.SetRotation((uid, sprite), rest);
    }

    /// <summary>
    /// Tips over into the feet-first pose right away and holds it for the whole straight flight, with a small hop.
    /// The instant dash after Hammer plays the same pose, just much shorter.
    /// </summary>
    private static RobustAnimation FlightAnimation(Angle rest, Angle pose, float duration)
    {
        var length = MathF.Max(duration, 0.05f);
        var tip = MathF.Min(0.08f, length * 0.3f);
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
                        new AnimationTrackProperty.KeyFrame(pose, tip),
                        new AnimationTrackProperty.KeyFrame(pose, length - tip),
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
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, LeapHeight), length * 0.4f),
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, LeapHeight * 0.6f), length * 0.6f),
                    },
                },
            },
        };
    }

    /// <summary>
    /// Plain Wolf Dance: a fast upright dash with a small forward lean, back upright at the end.
    /// </summary>
    private static RobustAnimation DashLeanAnimation(Angle rest, Angle lean, float duration)
    {
        var length = MathF.Max(duration, 0.05f);
        var tip = MathF.Min(0.06f, length * 0.25f);
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
                        new AnimationTrackProperty.KeyFrame(lean, tip),
                        new AnimationTrackProperty.KeyFrame(lean, length - tip * 2),
                        new AnimationTrackProperty.KeyFrame(rest, tip),
                    },
                },
            },
        };
    }

    /// <summary>
    /// The feet connect, then the user drops flat onto the floor (ends at the lying angle, since the server
    /// knocks the user down right after the kick).
    /// </summary>
    private static RobustAnimation LandingAnimation(Angle pose, Angle lying, float duration)
    {
        var length = MathF.Max(duration, 0.1f);
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
                        new AnimationTrackProperty.KeyFrame(pose, 0f),
                        new AnimationTrackProperty.KeyFrame(pose, length * 0.35f),
                        new AnimationTrackProperty.KeyFrame(lying, length * 0.65f),
                    },
                },
                new AnimationTrackComponentProperty
                {
                    ComponentType = typeof(SpriteComponent),
                    Property = nameof(SpriteComponent.Offset),
                    InterpolationMode = AnimationInterpolationMode.Cubic,
                    KeyFrames =
                    {
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, LeapHeight * 0.6f), 0f),
                        new AnimationTrackProperty.KeyFrame(new Vector2(0f, LeapHeight * 0.6f), length * 0.35f),
                        new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.65f),
                    },
                },
            },
        };
    }
}

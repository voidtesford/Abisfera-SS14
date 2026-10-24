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
/// Sprite animation of the Claw Whirlwind target: a somersault over the user, then a slow spinning rise
/// into the air and the fall. The actual position is moved (and networked) by the server.
/// </summary>
public sealed class ClawWhirlwindAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly RotationVisualizerSystem _rotation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string AnimationKey = "claw-whirlwind";

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ClawWhirlwindAnimationEvent>(OnAnimation);
    }

    private void OnAnimation(ClawWhirlwindAnimationEvent ev)
    {
        var uid = GetEntity(ev.Target);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !TryComp<SpriteComponent>(uid, out var sprite))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        var rotationVisuals = CompOrNull<RotationVisualsComponent>(uid);
        var rest = rotationVisuals?.VerticalRotation ?? Angle.Zero;

        // Flipping toward the right of the screen turns clockwise (negative).
        var sign = ev.Direction.X >= 0f ? -1.0 : 1.0;

        switch (ev.Phase)
        {
            case ClawWhirlwindPhase.Flip:
                _animation.Play((uid, player), Spin(rest, sign, ev.Duration, ev.Spins, ev.Height, 0.5f), AnimationKey);
                break;
            case ClawWhirlwindPhase.Air:
                // Slow rise for most of the time in the air, then a quicker drop.
                _animation.Play((uid, player), Spin(rest, sign, ev.Duration, ev.Spins, ev.Height, 0.7f), AnimationKey);
                break;
            case ClawWhirlwindPhase.Land:
                _sprite.SetOffset((uid, sprite), Vector2.Zero);
                RestoreRotation(uid, sprite, rotationVisuals, rest);
                break;
        }
    }

    /// <summary>
    /// Continuous rotation by <paramref name="spins"/> full turns while the sprite goes up to
    /// <paramref name="height"/> at <paramref name="peak"/> (0-1) of the duration and back down.
    /// </summary>
    private static RobustAnimation Spin(Angle rest, double sign, float duration, float spins, float height, float peak)
    {
        var length = MathF.Max(duration, 0.05f);

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

        var offset = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Offset),
            InterpolationMode = AnimationInterpolationMode.Cubic,
            KeyFrames =
            {
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height * 0.6f), length * peak * 0.5f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, height), length * peak * 0.5f),
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * (1f - peak)),
            },
        };

        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks = { rotation, offset },
        };
    }

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
}

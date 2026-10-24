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
/// The user's backflip after the Hammer's Wolf Grip synergy: one full turn backwards in a small hop.
/// The movement itself is done (and networked) by the server. A Wolf Dance started mid-flip cuts it short.
/// </summary>
public sealed class HammerBackflipAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly RotationVisualizerSystem _rotation = default!;
    [Dependency] private readonly SpriteSystem _sprite = default!;

    private const string AnimationKey = "proper-kung-fu-backflip";
    private const float Height = 0.45f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<ProperKungFuBackflipEvent>(OnBackflip);
        SubscribeNetworkEvent<WolfDanceAnimationEvent>(OnWolfDance);
    }

    private void OnBackflip(ProperKungFuBackflipEvent ev)
    {
        var uid = GetEntity(ev.User);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !HasComp<SpriteComponent>(uid))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        var rest = CompOrNull<RotationVisualsComponent>(uid)?.VerticalRotation ?? Angle.Zero;

        // Flying to the right while facing left: the head goes back first, i.e. clockwise (negative).
        var sign = ev.Direction.X >= 0f ? -1.0 : 1.0;
        _animation.Play((uid, player), Flip(rest, sign, ev.Duration), AnimationKey);
    }

    private void OnWolfDance(WolfDanceAnimationEvent ev)
    {
        var uid = GetEntity(ev.User);
        if (!uid.IsValid()
            || TerminatingOrDeleted(uid)
            || !TryComp<SpriteComponent>(uid, out var sprite)
            || !TryComp<AnimationPlayerComponent>(uid, out var player)
            || !_animation.HasRunningAnimation(player, AnimationKey))
            return;

        _animation.Stop((uid, player), AnimationKey);
        _sprite.SetOffset((uid, sprite), Vector2.Zero);
        var visuals = CompOrNull<RotationVisualsComponent>(uid);
        if (visuals != null
            && _appearance.TryGetData<RotationState>(uid, RotationVisuals.RotationState, out var state)
            && state == RotationState.Horizontal)
        {
            _rotation.AnimateSpriteRotation(uid, sprite, visuals.HorizontalRotation, visuals.AnimationTime);
            return;
        }

        _sprite.SetRotation((uid, sprite), visuals?.VerticalRotation ?? Angle.Zero);
    }

    private static RobustAnimation Flip(Angle rest, double sign, float duration)
    {
        var length = MathF.Max(duration, 0.05f);

        // Quarter turns, so the linear interpolation never takes the short way around.
        var rotation = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Rotation),
            InterpolationMode = AnimationInterpolationMode.Linear,
        };
        rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest, 0f));
        for (var i = 1; i <= 4; i++)
        {
            rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(90 * i * sign), length / 4f));
        }

        var offset = new AnimationTrackComponentProperty
        {
            ComponentType = typeof(SpriteComponent),
            Property = nameof(SpriteComponent.Offset),
            InterpolationMode = AnimationInterpolationMode.Cubic,
            KeyFrames =
            {
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, 0f),
                new AnimationTrackProperty.KeyFrame(new Vector2(0f, Height), length * 0.45f),
                new AnimationTrackProperty.KeyFrame(Vector2.Zero, length * 0.55f),
            },
        };

        return new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks = { rotation, offset },
        };
    }
}

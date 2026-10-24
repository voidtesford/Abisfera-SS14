using Content.Shared._CorvaxGoob.ProperKungFu;
using Content.Shared.Rotation;
using Robust.Client.Animations;
using RobustAnimation = Robust.Client.Animations.Animation;
using Robust.Client.GameObjects;
using Robust.Shared.Animations;

namespace Content.Client._CorvaxGoob.ProperKungFu;

/// <summary>
/// Perfect Flow finisher: the user spins once on the spot (the spinning kick to the head).
/// </summary>
public sealed class PerfectFlowSpinAnimationSystem : EntitySystem
{
    [Dependency] private readonly AnimationPlayerSystem _animation = default!;

    private const string AnimationKey = "proper-kung-fu-flow-spin";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<ProperKungFuFlowSpinEvent>(OnSpin);
    }

    private void OnSpin(ProperKungFuFlowSpinEvent ev)
    {
        var uid = GetEntity(ev.User);
        if (!uid.IsValid() || TerminatingOrDeleted(uid) || !HasComp<SpriteComponent>(uid))
            return;

        var player = EnsureComp<AnimationPlayerComponent>(uid);
        if (_animation.HasRunningAnimation(player, AnimationKey))
            _animation.Stop((uid, player), AnimationKey);

        var rest = CompOrNull<RotationVisualsComponent>(uid)?.VerticalRotation ?? Angle.Zero;
        var length = MathF.Max(ev.Duration, 0.05f);

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
            rotation.KeyFrames.Add(new AnimationTrackProperty.KeyFrame(rest + Angle.FromDegrees(-90 * i), length / 4f));
        }

        _animation.Play((uid, player), new RobustAnimation
        {
            Length = TimeSpan.FromSeconds(length),
            AnimationTracks = { rotation },
        }, AnimationKey);
    }
}

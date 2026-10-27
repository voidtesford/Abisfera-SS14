using Content.Shared._CorvaxGoob.Gilbert;
using Robust.Client.Graphics;
using Robust.Client.Player;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Drives <see cref="GilbertInterferenceOverlay"/> for the local player: the target comes from
/// <see cref="GilbertProximitySystem"/> (living Gilbert within 12 tiles, full at 6; berserk (v6): within 18 tiles,
/// full at 9 and up to 2, the shader's rage mode above 1) and is eased in and out,
/// so the effect fades smoothly when he comes close, leaves or dies.
/// </summary>
public sealed class GilbertInterferenceSystem : EntitySystem
{
    [Dependency] private readonly IOverlayManager _overlayManager = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly GilbertProximitySystem _proximity = default!;

    /// <summary>Seconds to go from nothing to full interference.</summary>
    public const float FadeInTime = 1.2f;

    /// <summary>Seconds to go from full interference to nothing.</summary>
    public const float FadeOutTime = 1.8f;

    private GilbertInterferenceOverlay _overlay = default!;

    /// <summary>The smoothed value the overlay draws with.</summary>
    public float Intensity => _overlay.Intensity;

    /// <summary>Where <see cref="Intensity"/> is heading this frame.</summary>
    public float TargetIntensity { get; private set; }

    public override void Initialize()
    {
        base.Initialize();
        _overlay = new GilbertInterferenceOverlay();
        _overlayManager.AddOverlay(_overlay);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _overlayManager.RemoveOverlay(_overlay);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        TargetIntensity = _player.LocalEntity is { } local ? _proximity.GetIntensity(local) : 0f;
        _overlay.Intensity = Step(_overlay.Intensity, TargetIntensity, frameTime);
    }

    /// <summary>Moves <paramref name="current"/> toward <paramref name="target"/> at the fade-in / fade-out rate.</summary>
    public static float Step(float current, float target, float frameTime)
    {
        var rate = target > current ? 1f / FadeInTime : 1f / FadeOutTime;
        var delta = rate * MathF.Max(frameTime, 0f);
        return target > current
            ? MathF.Min(current + delta, target)
            : MathF.Max(current - delta, target);
    }
}

using Robust.Shared.GameStates;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// v13: Gilbert hangs in the air on red threads (his phase-2 levitation). Only drives the client's sprite lift /
/// bob (GilbertLevitationVisualsSystem); the bolts and threads are server-spawned effect entities.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class GilbertLevitatingComponent : Component
{
    [DataField, AutoNetworkedField]
    public TimeSpan Start;

    [DataField, AutoNetworkedField]
    public TimeSpan End;

    /// <summary>Seconds to rise to <see cref="Height"/> (and to come back down at the end).</summary>
    [DataField, AutoNetworkedField]
    public float Rise = 1.2f;

    /// <summary>Lift of the sprite, tiles.</summary>
    [DataField, AutoNetworkedField]
    public float Height = 0.45f;

    /// <summary>Sprite lift (tiles) at <paramref name="now"/>: eases up, bobs while hanging, eases down.</summary>
    public static float Lift(GilbertLevitatingComponent comp, TimeSpan now)
    {
        var t = (float) (now - comp.Start).TotalSeconds;
        var left = (float) (comp.End - now).TotalSeconds;
        var rise = MathF.Max(comp.Rise, 0.01f);
        var up = Math.Clamp(MathF.Min(t / rise, left / 0.35f), 0f, 1f);
        up = up * up * (3f - 2f * up);
        return comp.Height * up + 0.05f * up * MathF.Sin(t * 3.1f);
    }
}

namespace Content.Shared._CorvaxGoob.ProperKungFu;

/// <summary>
/// Put on a target hit by the "Claw Series" combo. While it lasts, a Wolf Dance kick from the same user
/// becomes the stronger synergy kick and consumes the mark.
/// </summary>
[RegisterComponent]
public sealed partial class ProperKungFuClawMarkComponent : Component
{
    /// <summary>
    /// Who left the mark. Only their Wolf Dance gets the bonus.
    /// </summary>
    [ViewVariables]
    public EntityUid Marker;

    [ViewVariables]
    public TimeSpan ExpiresAt;
}

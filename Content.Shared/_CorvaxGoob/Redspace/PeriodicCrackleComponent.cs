using Content.Shared.Hands.Components;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Shared._CorvaxGoob.Redspace;

/// <summary>
/// Every few seconds (randomised) the entity briefly "crackles": the server flips
/// <see cref="PeriodicCrackleVisuals.Crackling"/> for <see cref="Duration"/>, which shows the
/// <see cref="PeriodicCrackleLayers.Crackle"/> sprite layer on the client, and plays a quiet sound.
/// Generic on purpose, so any future redspace (or other) content can reuse it.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
public sealed partial class PeriodicCrackleComponent : Component
{
    /// <summary>
    /// Whether it crackles at all. Lets owners (e.g. the Euclidean gate) crackle only in some states.
    /// Toggle with PeriodicCrackleSystem.SetEnabled so the timer restarts properly.
    /// </summary>
    [DataField]
    public bool Enabled = true;

    /// <summary>Shortest pause between two crackles.</summary>
    [DataField]
    public TimeSpan MinInterval = TimeSpan.FromSeconds(4);

    /// <summary>Longest pause between two crackles.</summary>
    [DataField]
    public TimeSpan MaxInterval = TimeSpan.FromSeconds(9);

    /// <summary>How long the crackle overlay stays visible.</summary>
    [DataField]
    public TimeSpan Duration = TimeSpan.FromSeconds(0.6);

    /// <summary>Played from the entity when it crackles in the world. Null for a silent crackle.</summary>
    [DataField]
    public SoundSpecifier? Sound = new SoundCollectionSpecifier("sparks", AudioParams.Default.WithVolume(-12f).WithMaxDistance(4f));

    /// <summary>
    /// Whether it still crackles while held in a hand (sound from the holder, quieter by <see cref="HeldVolumeOffset"/>).
    /// Inside any other container (bags, pockets, crates) it stays silent and dark.
    /// </summary>
    [DataField]
    public bool CrackleWhenHeld = true;

    [DataField]
    public float HeldVolumeOffset = -4f;

    /// <summary>
    /// Extra layers added to the holder's in-hand sprite while crackling (e.g. bright arcs around the hand),
    /// so a held item visibly crackles too. Empty for none.
    /// </summary>
    [DataField]
    public Dictionary<HandLocation, List<PrototypeLayerData>> InhandVisuals = new();

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextCrackle;

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan CrackleEnd;

    [ViewVariables]
    public bool Crackling;
}

[Serializable, NetSerializable]
public enum PeriodicCrackleVisuals : byte
{
    Crackling,
}

[Serializable, NetSerializable]
public enum PeriodicCrackleLayers : byte
{
    Crackle,
}

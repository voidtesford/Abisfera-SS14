using Robust.Client.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Player;

namespace Content.Client._CorvaxGoob.Gilbert;

/// <summary>
/// Geiger counter (dosimeter) crackle that goes with the proximity screen interference: only for the local
/// player, driven by the same eased intensity <see cref="GilbertInterferenceSystem"/> draws with (12 tiles,
/// full at 6; berserk: 18 tiles, full at 9, intensity above 1 just stays at "extreme"), so it fades in and out with the overlay and stops when he dies. Closer means louder and a
/// faster click rate (the stock geiger loops: low -> med -> high -> extreme). Gilbert himself and ghosts
/// never get it, the same as the overlay.
/// </summary>
public sealed class GilbertGeigerSystem : EntitySystem
{
    [Dependency] private readonly AudioSystem _audio = default!;
    [Dependency] private readonly GilbertInterferenceSystem _interference = default!;

    /// <summary>The repo's own geiger counter loops (the same ones the dosimeter uses), slow to fast.</summary>
    public static readonly SoundPathSpecifier[] Loops =
    {
        new("/Audio/Items/Geiger/low.ogg"),
        new("/Audio/Items/Geiger/med.ogg"),
        new("/Audio/Items/Geiger/high.ogg"),
        new("/Audio/Items/Geiger/ext.ogg"),
    };

    /// <summary>Below this eased intensity the counter is silent.</summary>
    public const float Threshold = 0.02f;

    /// <summary>Volume (dB) at the faintest and at full intensity.</summary>
    public const float MinVolume = -16f;
    public const float MaxVolume = 0f;

    /// <summary>Which loop plays: -1 for none, 0..3 for low..extreme.</summary>
    public int Level { get; private set; } = -1;

    /// <summary>Current loop volume in dB.</summary>
    public float Volume { get; private set; } = MinVolume;

    private EntityUid? _stream;

    /// <summary>Click-rate step for an intensity: none, then low / med / high / extreme as he gets closer.</summary>
    public static int LevelFor(float intensity)
    {
        if (intensity < Threshold)
            return -1;
        if (intensity < 0.3f)
            return 0;
        if (intensity < 0.6f)
            return 1;
        if (intensity < 0.9f)
            return 2;
        return 3;
    }

    /// <summary>Louder the closer he is: <see cref="MinVolume"/> at the threshold up to <see cref="MaxVolume"/> at full.</summary>
    public static float VolumeFor(float intensity)
    {
        var k = Math.Clamp(intensity, 0f, 1f);
        return MinVolume + (MaxVolume - MinVolume) * MathF.Sqrt(k);
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _stream = _audio.Stop(_stream);
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        var intensity = _interference.Intensity;
        var level = LevelFor(intensity);
        Volume = VolumeFor(intensity);

        if (level != Level)
        {
            Level = level;
            _stream = _audio.Stop(_stream);
            if (level >= 0)
            {
                var param = AudioParams.Default.WithLoop(true).WithVolume(Volume);
                _stream = _audio.PlayGlobal(Loops[level], Filter.Local(), false, param)?.Entity;
            }
        }
        else if (_stream != null)
        {
            _audio.SetVolume(_stream, Volume);
        }
    }
}

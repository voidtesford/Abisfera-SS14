using Content.Shared._CorvaxGoob.Redspace;
using Content.Shared.Hands.EntitySystems;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._CorvaxGoob.Redspace;

public sealed class PeriodicCrackleSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly AppearanceSystem _appearance = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PeriodicCrackleComponent, MapInitEvent>(OnMapInit);
    }

    private void OnMapInit(Entity<PeriodicCrackleComponent> ent, ref MapInitEvent args)
    {
        // Random first delay so a pile of freshly spawned crystals doesn't crackle in sync.
        ent.Comp.NextCrackle = _timing.CurTime + NextInterval(ent.Comp);
    }

    private TimeSpan NextInterval(PeriodicCrackleComponent comp)
    {
        var min = comp.MinInterval.TotalSeconds;
        var max = Math.Max(min, comp.MaxInterval.TotalSeconds);
        return TimeSpan.FromSeconds(_random.NextDouble(min, max));
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<PeriodicCrackleComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.Crackling && (now >= comp.CrackleEnd || !comp.Enabled))
                SetCrackling(uid, comp, false);

            if (!comp.Enabled)
                continue;

            if (now < comp.NextCrackle)
                continue;

            comp.NextCrackle = now + NextInterval(comp);
            TryCrackle(uid, comp, now);
        }
    }

    /// <summary>
    /// Turns periodic crackling on or off. Enabling schedules the first crackle within the normal interval;
    /// disabling hides an ongoing crackle on the next update.
    /// </summary>
    public void SetEnabled(EntityUid uid, bool enabled, PeriodicCrackleComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false) || comp.Enabled == enabled)
            return;

        comp.Enabled = enabled;
        if (enabled)
            comp.NextCrackle = _timing.CurTime + NextInterval(comp);
        else if (comp.Crackling)
            SetCrackling(uid, comp, false);
    }

    /// <summary>
    /// Changes the crackle interval. If the pending crackle is further away than the new maximum,
    /// it is rescheduled within the new interval so a switch to "more often" takes effect right away.
    /// </summary>
    public void SetInterval(EntityUid uid, TimeSpan min, TimeSpan max, PeriodicCrackleComponent? comp = null)
    {
        if (!Resolve(uid, ref comp, false))
            return;

        if (comp.MinInterval == min && comp.MaxInterval == max)
            return;

        comp.MinInterval = min;
        comp.MaxInterval = max;
        var now = _timing.CurTime;
        if (comp.NextCrackle > now + max)
            comp.NextCrackle = now + NextInterval(comp);
    }

    /// <summary>
    /// Crackles now if the entity is lying in the world (or held, when allowed). Returns whether it did.
    /// </summary>
    public bool TryCrackle(EntityUid uid, PeriodicCrackleComponent? comp = null, TimeSpan? now = null)
    {
        if (!Resolve(uid, ref comp, false))
            return false;

        EntityUid soundSource;
        var volumeOffset = 0f;
        if (!_container.TryGetContainingContainer((uid, null, null), out var container))
        {
            soundSource = uid;
        }
        else if (comp.CrackleWhenHeld && _hands.IsHolding(container.Owner, uid))
        {
            soundSource = container.Owner;
            volumeOffset = comp.HeldVolumeOffset;
        }
        else
        {
            // Bags, pockets, crates: no light show and no noise from inside.
            return false;
        }

        comp.CrackleEnd = (now ?? _timing.CurTime) + comp.Duration;
        SetCrackling(uid, comp, true);

        if (comp.Sound is { } sound)
            _audio.PlayPvs(sound, soundSource, sound.Params.AddVolume(volumeOffset));

        return true;
    }

    private void SetCrackling(EntityUid uid, PeriodicCrackleComponent comp, bool value)
    {
        comp.Crackling = value;
        _appearance.SetData(uid, PeriodicCrackleVisuals.Crackling, value);
    }
}

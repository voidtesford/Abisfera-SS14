using Content.Shared.DoAfter;
using Robust.Shared.Serialization;

namespace Content.Shared._CorvaxGoob.Gilbert;

/// <summary>
/// Gilbert frisking a cuffed / downed person before he starts taking their things.
/// </summary>
[Serializable, NetSerializable]
public sealed partial class GilbertFriskDoAfterEvent : SimpleDoAfterEvent;

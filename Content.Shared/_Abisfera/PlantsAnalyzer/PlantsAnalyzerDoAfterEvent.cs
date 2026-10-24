using Content.Shared.DoAfter;
using Robust.Shared.Serialization;
using Content.Shared._Abisfera.PlantsAnalyzer;

namespace Content.Shared._Abisfera.PlantsAnalyzer;

[Serializable, NetSerializable]
public sealed partial class PlantsAnalyzerDoAfterEvent : SimpleDoAfterEvent
{
}

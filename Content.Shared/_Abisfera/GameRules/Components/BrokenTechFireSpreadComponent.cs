using System;
using System.Collections.Generic;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Damage;
using Content.Goobstation.Maths.FixedPoint;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.Shared.Abisfera.GameRules.Components;

[RegisterComponent]
public sealed partial class BrokenTechFireSpreadComponent : Component
{
    [DataField]
    public int MaxRadius = 15;

    [DataField]
    public float SpreadDelay = 5f;

    [DataField]
    public List<ProtoId<ReagentPrototype>> WaterReagents = new() { "Water", "Holywater", "CoconutWater" };

    [DataField]
    public DamageSpecifier BlobTileDamage = new()
    {
        DamageDict = new Dictionary<string, FixedPoint2>
        {
            { "Heat", 10 },
        }
    };

    [DataField]
    public float BlobTileDamageInterval = 1f;

    public EntityUid? OriginGrid;
    public Vector2i OriginTile;
    public bool HasOrigin;
    public int Distance;
    public TimeSpan NextSpread;
    public TimeSpan NextBlobTileDamage;
    public TimeSpan NextWaterCheck;
    public bool Finished;
}

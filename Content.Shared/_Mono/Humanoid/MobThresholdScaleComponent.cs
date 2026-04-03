using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;

namespace Content.Shared._Mono.Traits.Physical;

/// <summary>
/// Adjusts Mobthresholds based off a given scale.
/// </summary>
[RegisterComponent]
public sealed partial class MobThresholdAdjustmentComponent : Component
{

    /// <summary>
    /// Scales all thresholds on an entity multiplicatively, before modifiers are applied.
    /// </summary>
    [DataField]
    public float Scale = 1;

    /// <summary>
    /// Modifies the damage required to reach the critical threshold
    /// </summary>
    [DataField]
    public FixedPoint2 CritThresholdMod = 0;

    /// <summary>
    /// Modifies the damage required to reach the death threshold
    /// </summary>
    [DataField]
    public FixedPoint2 DeathThresholdMod = 0;

    [DataField]
    public SortedDictionary<FixedPoint2, MobState> OldThresholds = new();

}

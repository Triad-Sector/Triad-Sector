using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Robust.Shared.GameStates;

namespace Content.Shared._Mono.Traits.Physical;

/// <summary>
/// Adjusts Mobthresholds based off a given scale.
/// </summary>
[RegisterComponent]
public sealed partial class MobThresholdScaleComponent : Component
{

    [DataField]
    public float Scale = 1;

    /// <summary>
    /// Modified Thresholds
    /// </summary>
    [DataField]
    public SortedDictionary<FixedPoint2, MobState> OldThresholds = new();

    /// <summary>
    /// Multiplies the effect of the scaled HP.
    /// </summary>
    [DataField]
    public float Multiplier = 1;
}

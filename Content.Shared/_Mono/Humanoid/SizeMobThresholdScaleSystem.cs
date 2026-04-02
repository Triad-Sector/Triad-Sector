using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;

namespace Content.Shared._Mono.Traits.Physical;

/// <summary>
/// Applies the Will To Live trait effects by increasing the death health threshold.
/// </summary>
public sealed class MobThresholdScaleSystem : EntitySystem
{
    [Dependency] private readonly MobThresholdSystem _mobThresholds = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MobThresholdScaleComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<MobThresholdScaleComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(Entity<MobThresholdScaleComponent> ent, ref ComponentStartup args)
    {
        ScaleMobThresholds(ent, MobState.Critical);
        ScaleMobThresholds(ent, MobState.Dead);
    }

    private void OnShutdown(Entity<MobThresholdScaleComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.Scale = 1;
        ScaleMobThresholds(ent, MobState.Critical);
        ScaleMobThresholds(ent, MobState.Dead);
    }

    private void ScaleMobThresholds(Entity<MobThresholdScaleComponent> ent, MobState state, MobThresholdsComponent? thresholdsComp = null) // issue: triggers twice. Get current hitbox vs old and recalcualte instead
    {

        if (!_mobThresholds.TryGetThresholdForState(ent, state, out var threshold, thresholdsComp))
            return;
        var thresholdModification = FixedPoint2.Max(0, threshold.Value * ent.Comp.scale);

        _mobThresholds.SetMobStateThreshold(ent.Owner, newThresholdValue, state, thresholdsComp);
    }
}




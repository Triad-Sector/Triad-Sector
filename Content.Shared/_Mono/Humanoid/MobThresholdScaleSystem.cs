using System.Xml;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;

namespace Content.Shared._Mono.Traits.Physical;

/// <summary>
/// Applies the Will To Live trait effects by increasing the death health threshold.
/// </summary>
public sealed class MobThresholdAdjustmentSystem : EntitySystem
{
    [Dependency] private readonly MobThresholdSystem _mobThresholds = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<MobThresholdAdjustmentComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<MobThresholdAdjustmentComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnStartup(EntityUid uid, MobThresholdAdjustmentComponent comp, ref ComponentStartup args)
    {
        if (!TryComp<MobThresholdsComponent>(uid, out var thresholdsComp))
            return;
        comp.OldThresholds = thresholdsComp.Thresholds;
        ScaleMobThresholds(uid, comp);
    }

    private void OnShutdown(EntityUid uid, MobThresholdAdjustmentComponent comp, ref ComponentShutdown args)
    {
        ResetMobThresholds(uid, comp);
    }

    private void ResetMobThresholds(EntityUid uid, MobThresholdAdjustmentComponent comp)
    {
        var oldThresholds = new Dictionary<FixedPoint2, MobState>(comp.OldThresholds);

        foreach (var (damageThreshold, state) in oldThresholds)
        {
            _mobThresholds.SetMobStateThreshold(uid, damageThreshold, state);
        }
    }
    public bool ScaleMobThresholds(EntityUid uid, MobThresholdAdjustmentComponent comp)
    {
        if (!TryComp<MobThresholdsComponent>(uid, out var thresholdsComp))
            return false;

        ResetMobThresholds(uid, comp);

        foreach (MobState mobstate in Enum.GetValues<MobState>())
        {
            if (mobstate == MobState.Invalid)
                continue;

            if (_mobThresholds.TryGetThresholdForState(uid, mobstate, out var threshold, thresholdsComp))
            {
                threshold *= comp.Scale;
                if (mobstate == MobState.Critical)
                    threshold += comp.CritThresholdMod;
                if (mobstate == MobState.Dead)
                    threshold += comp.DeathThresholdMod;
                _mobThresholds.SetMobStateThreshold(uid, (FixedPoint2)threshold, mobstate, thresholdsComp);
            }
        }
        return true;
    }
}

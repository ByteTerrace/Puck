namespace Puck.SdfVm;

/// <summary>The temporal reconstruction's sampling, shared by the host's history (<c>SdfTemporalHistory</c>) and the
/// resolve, which <see cref="SdfIsaHlsl"/> writes it into.</summary>
public static class SdfTemporalSamples {
    /// <summary>The number of samples in one convergence period, and the weight a history accumulates up to
    /// (<c>SDF_HISTORY_WEIGHT_CAP</c>). A sub-pixel feature the jitter hits in some positions of the period and misses
    /// in others is remembered for this many frames (<c>SDF_TEMPORAL_PERIOD</c>), until the jitter comes round to it
    /// again.</summary>
    public const uint Period = 8;
    /// <summary>The periods of samples a history accumulates up to while its pixel carries a remembered sub-pixel
    /// feature (<c>SDF_HISTORY_FEATURE_WEIGHT_CAP</c>, <see cref="Period"/> times this): a feature hit in one position
    /// of the period moves the mean by a thirty-third of its contrast a frame rather than a ninth, so the mean pulses by
    /// a fifth over the period rather than doubling.</summary>
    public const uint FeaturePeriods = 4;
}

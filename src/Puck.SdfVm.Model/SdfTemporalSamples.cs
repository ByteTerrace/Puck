using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The temporal reconstruction's sampling, shared by the host's history (<c>SdfTemporalHistory</c>) and the
/// resolve, which <see cref="SdfIsaHlsl"/> writes it into.</summary>
public static class SdfTemporalSamples {
    /// <summary>The periods of samples a history accumulates up to while its pixel carries a remembered sub-pixel
    /// feature (<c>SDF_HISTORY_FEATURE_WEIGHT_CAP</c>, <see cref="SdfWorldPackage.TemporalPeriod"/> times this): a feature hit in one position
    /// of the period moves the mean by a thirty-third of its contrast a frame rather than a ninth, so the mean pulses by
    /// a fifth over the period rather than doubling.</summary>
    public const uint FeaturePeriods = 4;
}

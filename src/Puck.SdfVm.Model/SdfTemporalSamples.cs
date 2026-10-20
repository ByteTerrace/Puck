namespace Puck.SdfVm;

/// <summary>The temporal reconstruction's sampling, shared by the host's history (<c>SdfTemporalHistory</c>) and the
/// resolve, which <see cref="SdfIsaHlsl"/> writes it into.</summary>
public static class SdfTemporalSamples {
    /// <summary>The number of samples in one convergence period, and the weight a history accumulates up to
    /// (<c>SDF_HISTORY_WEIGHT_CAP</c>).</summary>
    public const uint Period = 8;
}

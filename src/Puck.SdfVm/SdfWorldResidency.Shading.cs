using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    // Kept outside device-owned tables. A lost device must not discard the identities needed for quarantine.
    private readonly List<(IGpuSubmissionFence Fence, IReadOnlyList<string> Identities)> m_shadeSubmissions = [];

    /// <summary>The identities in shade submissions whose fences have not retired. On loss, the remaining list
    /// is conservative: querying a lost fence cannot establish retirement.</summary>
    public IReadOnlyList<string> InFlightShadeIdentities => m_shadeSubmissions.SelectMany(static s => s.Identities).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal void SubmittedShade(SdfShadeDispatch plan, IGpuSubmissionFence fence) {
        if (plan.Identities.Count != 0) { m_shadeSubmissions.Add((fence, plan.Identities)); }
    }

    // Recording into the same frame slot proves its old fence has retired, even if the host already reset it.
    // Retirement is before recording; submission callbacks then append every views/glass identity under one fence.
    internal void RetireShade(IGpuSubmissionFence? retired) {
        for (var i = m_shadeSubmissions.Count - 1; i >= 0; i--) {
            if (ReferenceEquals(m_shadeSubmissions[i].Fence, retired) || Retired(m_shadeSubmissions[i].Fence)) {
                m_shadeSubmissions.RemoveAt(i);
            }
        }
    }

    private static bool Retired(IGpuSubmissionFence fence) {
        try { return fence.IsSignaled; }
        catch (DeviceLostException) { return false; }
    }
}

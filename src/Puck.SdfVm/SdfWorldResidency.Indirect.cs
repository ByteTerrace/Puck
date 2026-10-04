using Puck.Abstractions.Counting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private readonly WorkCounterSet m_indirectWork = new(name: SdfIndirectWork.SourceName, kinds: SdfIndirectWork.Kinds);

    /// <summary>Gets the deterministic schedule counters retained across cache rebuilds.</summary>
    public IWorkCounterSource IndirectWork => m_indirectWork;
    /// <summary>Gets or sets the host lever applied to this residency, including nested views.</summary>
    public SdfIndirectTier? IndirectTierOverride { get; set; }
    /// <summary>Gets the effective cache tier.</summary>
    public SdfIndirectTier IndirectTier => (IndirectTierOverride ?? (m_frame?.IndirectTier ?? SdfIndirectTier.Off));
    /// <summary>Gets this residency's sole indirect producer name.</summary>
    public string IndirectInstanceName => $"{Name}.indirect";

    private void PrepareIndirect(SdfWorldTables tables, SdfFrame frame) => tables.SetIndirect(tier: IndirectTier, farDistance: frame.FarDistance, work: m_indirectWork);
}

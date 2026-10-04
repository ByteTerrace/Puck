using Puck.Abstractions.Counting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private readonly WorkCounterSet m_indirectWork = new(name: SdfIndirectWork.SourceName, kinds: SdfIndirectWork.Kinds);
    private int m_indirectResetRequested;

    /// <summary>Gets the deterministic schedule counters retained across cache rebuilds.</summary>
    public IWorkCounterSource IndirectWork => m_indirectWork;
    /// <summary>Gets or sets the host lever applied to this residency, including nested views.</summary>
    public SdfIndirectTier? IndirectTierOverride { get; set; }
    /// <summary>Gets the effective cache tier.</summary>
    public SdfIndirectTier IndirectTier => (IndirectTierOverride ?? (m_frame?.IndirectTier ?? SdfIndirectTier.Off));
    /// <summary>Gets this residency's sole indirect producer name.</summary>
    public string IndirectInstanceName => $"{Name}.indirect";
    /// <summary>Gets or sets whether new indirect updates are paused after the already admitted frame. This residency
    /// control survives tier changes and table replacement; views continue to read and count the retained cache.</summary>
    public bool IndirectFrozen { get; set; }
    /// <summary>Gets whether an explicit cache reset is waiting for the next frame this residency can render.</summary>
    public bool IndirectResetPending => Volatile.Read(ref m_indirectResetRequested) != 0;
    /// <summary>Queues a presentation-cache reset for the next renderable frame. It changes no authoritative world
    /// state; while frozen it withdraws old lighting without admitting replacement updates.</summary>
    public void RequestIndirectReset() => Interlocked.Exchange(ref m_indirectResetRequested, 1);

    private void PrepareIndirect(SdfWorldTables tables, SdfFrame frame) {
        tables.SetIndirect(tier: IndirectTier, farDistance: frame.FarDistance, work: m_indirectWork);
        if (tables.Indirect is { } cache) { cache.Frozen = IndirectFrozen; }
    }
    private void ApplyIndirectReset(SdfWorldTables tables) {
        if (Interlocked.Exchange(ref m_indirectResetRequested, 0) == 0) { return; }
        tables.ResetIndirectPresentation();
        IndirectLightViews.InvalidateStorage();
        Array.Clear(m_renderedSignatures);
    }
}

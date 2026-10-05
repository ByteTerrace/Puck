using Puck.Abstractions.Counting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    private readonly WorkCounterSet m_indirectWork = new(name: SdfIndirectWork.SourceName, kinds: SdfIndirectWork.Kinds);

    private int m_indirectResetRequested;
    private bool m_captureLightingReset;

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
    public bool IndirectResetPending => (Volatile.Read(location: ref m_indirectResetRequested) != 0);
    /// <summary>Gets whether the latest desired, packed frame's shared indirect cache has finished transport and its
    /// finite lighting solve, and an existing view readback fence has completed that exact allocation, epoch,
    /// publication and captured source. An older completed source never satisfies a newer desired frame.</summary>
    /// <remarks>Read on the render/console owner thread after frame preparation. This starts no work and allocates
    /// no readback. It describes the shared cache, not every view's receiver-certificate admission; an absent or off
    /// cache, pending reset, or frame waiting for packing is not ready.</remarks>
    public bool IsIndirectReady => (!IndirectResetPending && !m_captureLightingReset && m_packed && m_renders && (m_pendingFrame is null) &&
        !m_programPending && (m_frame is { } frame) && ReferenceEquals(objA: frame, objB: m_packedFrame) &&
        (m_tables?.Indirect is { } cache) && (cache.Layout.Tier == IndirectTier) && cache.IsReadyFor(frame: frame));

    /// <summary>Queues a presentation-cache reset for the next renderable frame. It changes no authoritative world
    /// state; while frozen it withdraws old lighting without admitting replacement updates.</summary>
    public void RequestIndirectReset() => Interlocked.Exchange(location1: ref m_indirectResetRequested, value: 1);

    private void PrepareIndirect(SdfWorldTables tables, SdfFrame frame) {
        tables.SetIndirect(tier: IndirectTier, farDistance: frame.FarDistance, work: m_indirectWork);
        if (tables.Indirect is { } cache) { cache.Frozen = IndirectFrozen; }
    }
    private void ApplyIndirectReset(SdfWorldTables tables) {
        if (m_captureLightingReset) {
            m_captureLightingReset = false;
            tables.Indirect?.ResetLightingForCapture();
            Array.Clear(array: m_renderedSignatures);
        }
        if (Interlocked.Exchange(location1: ref m_indirectResetRequested, value: 0) == 0) { return; }
        tables.ResetIndirectPresentation();
        IndirectLightViews.InvalidateStorage();
        Array.Clear(array: m_renderedSignatures);
    }
}

using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

/// <summary>The exact cache identity seen by temporal readers. Allocation replacement, geometry reset and complete
/// lighting publication each change a separate lane; no lossy hash combines them.</summary>
/// <param name="Allocation">The process-local allocation identity.</param>
/// <param name="Epoch">The cache geometry epoch.</param>
/// <param name="Publication">The completed lighting publication sequence.</param>
public readonly record struct SdfIndirectHistory(long Allocation, uint Epoch, ulong Publication);

/// <summary>One allocated brick's actual host admission and submitted trace masks. Probe classes and proof results
/// live on the GPU and are deliberately absent from this host snapshot.</summary>
/// <param name="Key">The world-space lattice brick.</param>
/// <param name="Slot">Its current physical brick slot.</param>
/// <param name="Placed">Whether its placements have been admitted to the update stream.</param>
/// <param name="SubmittedStrata">One submitted stratum mask for each of its 64 probes, in lattice order.</param>
public sealed record SdfIndirectBrickSnapshot(IrradianceBrickKey Key, int Slot, bool Placed, IReadOnlyList<uint> SubmittedStrata);

/// <summary>An immutable copy of the current cache's CPU-owned inventory. Its geometry and submission tokens qualify
/// separate fenced GPU pick records; this record never guesses GPU classifications from schedule counts.</summary>
/// <param name="Allocation">The process-local identity of this exact cache allocation, unchanged by an epoch reset.</param>
/// <param name="Tier">The allocation's tier.</param>
/// <param name="Epoch">The exact geometry epoch of its probes and cells.</param>
/// <param name="Submission">The update submission sequence.</param>
/// <param name="FarDistance">The trace exit distance used by this allocation.</param>
/// <param name="TraceComplete">Whether all demanded transport records have been submitted.</param>
/// <param name="PendingPlacements">The admitted brick placements not yet submitted.</param>
/// <param name="PendingClassifications">The admitted cell partitions not yet submitted.</param>
/// <param name="PendingTraces">The admitted probe strata not yet submitted.</param>
/// <param name="Frozen">Whether later update admission is paused; an already admitted frame may finish.</param>
/// <param name="Bytes">Every owned cache and host-region byte, including their rings.</param>
/// <param name="Levels">The allocation's immutable level descriptions.</param>
/// <param name="Bricks">The current brick inventory in lattice order.</param>
public sealed record SdfIndirectCacheSnapshot(long Allocation, SdfIndirectTier Tier, uint Epoch, uint Submission, float FarDistance,
    bool TraceComplete, int PendingPlacements, int PendingClassifications, int PendingTraces, bool Frozen, GpuMemoryBytes Bytes,
    IReadOnlyList<IrradianceLevel> Levels, IReadOnlyList<SdfIndirectBrickSnapshot> Bricks);

public sealed partial class SdfIndirectCache {
    private static long NextAllocation;
    private readonly long m_allocation = Interlocked.Increment(ref NextAllocation);

    /// <summary>Gets the exact identity temporal readers see, including completed lighting publications.</summary>
    public SdfIndirectHistory History => new(m_allocation, Epoch, LightingPublication);
    /// <summary>Gets the lighting publication sequence, advanced by a complete submitted sweep or a reset that
    /// withdraws the old publication.</summary>
    public ulong LightingPublication { get; private set; }

    /// <summary>Copies the actual host state for diagnostics. Call on the rendering owner thread; the returned
    /// inventory and every trace-mask list remain unchanged by later planning, submission or reset.</summary>
    /// <returns>The allocation's immutable CPU snapshot. GPU classes and proof masks require a fenced readback.</returns>
    public SdfIndirectCacheSnapshot Snapshot() {
        var bricks = new SdfIndirectBrickSnapshot[m_slots.Count];
        var index = 0;
        foreach (var (key, slot) in m_slots.OrderBy(static entry => entry.Key)) {
            var masks = m_traceStates.AsSpan(slot * SdfIndirectLayout.ProbesPerBrick, SdfIndirectLayout.ProbesPerBrick).ToArray();
            bricks[index++] = new SdfIndirectBrickSnapshot(key, slot, m_placed.Contains(key), Array.AsReadOnly(masks));
        }
        return new SdfIndirectCacheSnapshot(m_allocation, Layout.Tier, Epoch, Frame, FarDistance, IsComplete,
            PlaceCount, ClassifyCount, TraceCount, Frozen, Bytes, Array.AsReadOnly(Layout.Levels.ToArray()), Array.AsReadOnly(bricks));
    }
}

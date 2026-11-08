using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>A requested byte-range copy of a buffer version a package pass just accessed. The graph records the copy,
/// its barriers and the transfer-to-host barrier through the pass's counting recorder. The package owns the readback
/// buffer and reads it only once the node reports the copy's slot complete
/// (<see cref="IRenderGraphPackageReadback.Completed"/>, through <see cref="RenderGraphSlotResults{T}"/>).</summary>
/// <param name="Version">The buffer version bound to an input or output of this pass.</param>
/// <param name="SourceOffsetBytes">The first source byte copied.</param>
/// <param name="SizeBytes">The positive byte count, fitting the source range and the readback buffer's range from
/// <paramref name="DestinationOffsetBytes"/>.</param>
/// <param name="Destination">The package-owned readback buffer.</param>
/// <param name="DestinationOffsetBytes">The first destination byte written, so several copies of one recording can
/// share one readback buffer.</param>
public readonly record struct RenderGraphBufferReadback(string Version, ulong SourceOffsetBytes, ulong SizeBytes, IGpuReadbackBuffer Destination, ulong DestinationOffsetBytes = 0);
/// <summary>Optional asynchronous buffer readback for a package recorder. No copy or barrier is recorded when
/// <see cref="TryReadback"/> returns false for the first index. The graph still owns each buffer's access state,
/// including a copy's override of its planned state, so a later access is ordered after the transfer.</summary>
public interface IRenderGraphPackageReadback {
    /// <summary>Gets the actual GPU bytes of this recorder's readback allocations, including every retained ring slot.
    /// These bytes remain owned until this recorder disposes them, even after its graph retires.</summary>
    ulong ReadbackBytes { get; }

    /// <summary>Takes one copy the pass's latest recording requested. The graph asks for indexes from zero, in order,
    /// once per recorded frame, and records every copy until the first false.</summary>
    /// <param name="slot">The frame slot whose previous submission is complete.</param>
    /// <param name="index">The copy's index among the recording's copies.</param>
    /// <param name="readback">The source range and destination when requested; default otherwise.</param>
    /// <returns>Whether the graph should copy the requested range.</returns>
    bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback);
    /// <summary>Reports the latest recording submitted. The node calls it only for a pass whose recording drew in the
    /// frame just submitted (<see cref="RenderGraphPackageOutcome.Drew"/>); a pass that skipped, stood or drew nothing is
    /// not called, so no recorder arms an earlier recording's result. No fence is handed over: completion is the node's
    /// to prove (<see cref="Completed"/>).</summary>
    /// <param name="slot">The submitted frame slot.</param>
    void Submitted(int slot);
    /// <summary>Reports that the slot's previous submission has completed: the node waited its fence, either as it reuses
    /// or releases the slot or as soon as the fence read signaled between frames, or retired the graph this recorder
    /// belonged to once every submission of it completed. The node calls it for every readback pass, whether or not that
    /// pass recorded into the slot, and calls it again harmlessly when it waits the same slot before reusing it; it
    /// always precedes the next recording into the slot. It is never called for a submission a device loss interrupted,
    /// since the node then releases its recorders without waiting. The default has nothing to retire.</summary>
    /// <param name="slot">The frame slot whose previous submission completed.</param>
    void Completed(int slot) { }
}

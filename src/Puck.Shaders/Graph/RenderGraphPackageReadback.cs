using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>A requested byte-range copy of a buffer version a package pass just accessed. The graph records the copy,
/// its barriers and the transfer-to-host barrier through the pass's counting recorder. The package owns the readback
/// buffer and reads it only after the fence supplied by <see cref="IRenderGraphPackageReadback.Submitted"/> signals.</summary>
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
    /// <summary>Takes one copy the pass's latest recording requested. The graph asks for indexes from zero, in order,
    /// once per recorded frame, and records every copy until the first false.</summary>
    /// <param name="slot">The frame slot whose previous submission is complete.</param>
    /// <param name="index">The copy's index among the recording's copies.</param>
    /// <param name="readback">The source range and destination when requested; default otherwise.</param>
    /// <returns>Whether the graph should copy the requested range.</returns>
    bool TryReadback(int slot, int index, out RenderGraphBufferReadback readback);
    /// <summary>Arms the latest recording's result with its submission fence. A recorder must read or discard any
    /// completed result before recording into that slot again. It must never wait for the fence merely to pick.</summary>
    /// <param name="slot">The submitted frame slot.</param>
    /// <param name="fence">The reusable fence of the submission containing the copies.</param>
    void Submitted(int slot, IGpuSubmissionFence fence);
}

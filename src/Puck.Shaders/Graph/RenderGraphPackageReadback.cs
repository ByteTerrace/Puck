using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>A requested byte-range copy of a buffer version a package pass just accessed. The graph records the copy,
/// its barriers and the transfer-to-host barrier through the pass's counting recorder. The package owns the readback
/// buffer and reads it only after the fence supplied by <see cref="IRenderGraphPackageReadback.Submitted"/> signals.</summary>
/// <param name="Version">The buffer version bound to an input or output of this pass.</param>
/// <param name="SourceOffsetBytes">The first source byte copied.</param>
/// <param name="SizeBytes">The positive byte count, fitting the source range and the readback buffer.</param>
/// <param name="Destination">The package-owned readback buffer.</param>
public readonly record struct RenderGraphBufferReadback(string Version, ulong SourceOffsetBytes, ulong SizeBytes, IGpuReadbackBuffer Destination);
/// <summary>Optional asynchronous buffer readback for a package recorder. No copy or barrier is recorded when
/// <see cref="TryReadback"/> returns false. The graph still owns the buffer's access state, including the copy's
/// override of its planned state, so a later access is ordered after the transfer.</summary>
public interface IRenderGraphPackageReadback {
    /// <summary>Takes the readback requested by the pass's latest recording, once per recorded frame.</summary>
    /// <param name="slot">The frame slot whose previous submission is complete.</param>
    /// <param name="readback">The source range and destination when requested; default otherwise.</param>
    /// <returns>Whether the graph should copy the requested range.</returns>
    bool TryReadback(int slot, out RenderGraphBufferReadback readback);
    /// <summary>Arms the latest recording's result with its submission fence. A recorder must read or discard any
    /// completed result before recording into that slot again. It must never wait for the fence merely to pick.</summary>
    /// <param name="slot">The submitted frame slot.</param>
    /// <param name="fence">The reusable fence of the submission containing the copy.</param>
    void Submitted(int slot, IGpuSubmissionFence fence);
}

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Records one frame slot's owed staged copies of any number of regions into one command buffer, as the SDF engine's
/// upload pass and the render node do: each region is flushed for the slot, and a region owing a copy records it (<see cref="GpuRegion.RecordCopy"/>).
/// The first copy is preceded by one memory barrier that orders every earlier read of a staged destination by the
/// regions' reader stages, in this or an earlier submission, before the copies write it, and <see cref="Finish"/> makes
/// each copied buffer's writes visible to those stages with one buffer barrier. A recording with nothing owed records
/// nothing and never begins its command buffer.
/// </summary>
/// <param name="recorder">The recorder the copies and barriers are recorded through: the one the regions record their
/// copies through.</param>
/// <param name="readers">The shader stages that read the regions' buffers.</param>
/// <param name="begin">Returns the command buffer the copies are recorded into, called once, before the first copy: an
/// owner recording ahead of its passes begins a buffer of its own there, and one recording inside its frame returns the
/// frame's buffer.</param>
public sealed class GpuRegionCopyRecording(IGpuRecorder recorder, GpuStage readers, Func<nint> begin) {
    private readonly List<IGpuBuffer> m_copied = [];

    private nint m_commandBuffer;

    /// <summary>Flushes a region's share of the slot and, when the region owes a copy, records it, recording first, at the
    /// recording's first copy, the barrier that orders the earlier reads of every staged destination before the copies'
    /// writes.</summary>
    /// <param name="region">The region.</param>
    /// <param name="slot">The frame slot the command buffer belongs to, whose fence has retired.</param>
    /// <exception cref="ArgumentNullException"><paramref name="region"/> is <see langword="null"/>.</exception>
    public void Record(GpuRegion region, int slot) {
        ArgumentNullException.ThrowIfNull(argument: region);

        region.Flush(slot: slot);

        if (!region.OwesCopy) {
            return;
        }

        if (m_commandBuffer == 0) {
            m_commandBuffer = begin();
            recorder.MemoryBarrier(
                commandBufferHandle: m_commandBuffer,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                sourceAccessMask: GpuAccess.ShaderRead,
                sourceStageMask: readers
            );
        }

        region.RecordCopy(
            commandBuffer: m_commandBuffer,
            slot: slot
        );
        m_copied.Add(item: region.Buffer(slot: slot));
    }
    /// <summary>Makes every copied buffer's writes visible to the readers, one buffer barrier each, and starts the next
    /// recording empty.</summary>
    /// <returns>The command buffer holding the recording, or zero when nothing was owed.</returns>
    public nint Finish() {
        foreach (var buffer in m_copied) {
            recorder.TransitionBuffer(
                bufferHandle: buffer.BufferHandle,
                commandBufferHandle: m_commandBuffer,
                destinationAccessMask: GpuAccess.ShaderRead,
                destinationStageMask: readers,
                sourceAccessMask: GpuAccess.ShaderWrite,
                sourceStageMask: GpuStage.ComputeShader
            );
        }

        var commandBuffer = m_commandBuffer;

        m_copied.Clear();
        m_commandBuffer = 0;

        return commandBuffer;
    }
}

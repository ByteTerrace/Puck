namespace Puck.Abstractions.Gpu;

/// <summary>Records the copy of a storage buffer a recording just wrote into a readback buffer the host reads once the
/// submission completes: the source moved from its writer to the transfer, the copy, and the readback made visible to
/// the host. The source is left in <see cref="GpuAccess.TransferRead"/> at <see cref="GpuStage.Transfer"/>; the caller
/// returns it to its own next access.</summary>
public static class GpuReadbackCopy {
    /// <summary>Records the copy and its barriers.</summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="source">The storage buffer copied, last accessed as <paramref name="sourceAccess"/>.</param>
    /// <param name="sourceAccess">The source's prior accesses.</param>
    /// <param name="sourceStage">The stages of the source's prior accesses.</param>
    /// <param name="destination">The readback buffer written.</param>
    /// <param name="sizeBytes">The bytes copied from the start of each buffer; positive and within both.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/>, <paramref name="source"/> or
    /// <paramref name="destination"/> is <see langword="null"/>.</exception>
    public static void Record(IGpuRecorder recorder, nint commandBuffer, IGpuBuffer source, GpuAccess sourceAccess, GpuStage sourceStage, IGpuReadbackBuffer destination, ulong sizeBytes) {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer,
            sourceAccessMask: sourceAccess, sourceStageMask: sourceStage);
        recorder.CopyBuffer(commandBufferHandle: commandBuffer, destinationBufferHandle: destination.BufferHandle, sizeBytes: sizeBytes, sourceBufferHandle: source.BufferHandle);
        recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer);
    }
}

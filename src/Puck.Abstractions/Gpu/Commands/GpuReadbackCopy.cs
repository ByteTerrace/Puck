namespace Puck.Abstractions.Gpu;

/// <summary>Records the copy of a storage buffer or an image a recording wrote into a readback buffer the host reads once
/// the submission completes: the source moved from its writer to the transfer, the copy, and the readback made visible to
/// the host, since a completed submission alone makes no device write visible to the host.</summary>
public static class GpuReadbackCopy {
    /// <summary>Records the copy of a whole single-level color image into a readback buffer's rows, and its barriers: the
    /// image moves from <paramref name="sourceLayout"/> to <see cref="GpuImageLayout.TransferSource"/>, is copied, and
    /// returns to <paramref name="sourceLayout"/> with the accesses that layout's producer left it scoped for, so the
    /// image's next reader finds it as it was.</summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="commandBuffer">The command buffer being recorded.</param>
    /// <param name="imageHandle">The image copied.</param>
    /// <param name="sourceLayout">The image's layout, which must match what its producer left:
    /// <see cref="GpuImageLayout.External"/> (the producer-side handoff, Vulkan <c>GENERAL</c> scoped for its completed
    /// write's read-only handoff), <see cref="GpuImageLayout.General"/> (a working storage image) or
    /// <see cref="GpuImageLayout.ShaderReadOnly"/> (a sampled image).</param>
    /// <param name="destinationBufferHandle">The readback buffer written
    /// (<see cref="IGpuBufferFactory.CreateReadback"/>), at least <see cref="GpuCopyFootprint.ByteLength"/> long.</param>
    /// <param name="footprint">How the image's rows lie in the buffer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sourceLayout"/> is none of those layouts.</exception>
    public static void RecordImage(IGpuRecorder recorder, nint commandBuffer, nint imageHandle, GpuImageLayout sourceLayout, nint destinationBufferHandle, in GpuCopyFootprint footprint) {
        ArgumentNullException.ThrowIfNull(recorder);

        var (sourceAccess, sourceStages) = sourceLayout switch {
            GpuImageLayout.External => (GpuAccess.ShaderRead, GpuStage.ComputeShader),
            GpuImageLayout.General => ((GpuAccess.ShaderRead | GpuAccess.ShaderWrite), GpuStage.ComputeShader),
            GpuImageLayout.ShaderReadOnly => (GpuAccess.ShaderRead, (GpuStage.ComputeShader | GpuStage.FragmentShader)),
            _ => throw new ArgumentOutOfRangeException(
                actualValue: sourceLayout,
                message: "Readback requires an External, General, or ShaderReadOnly source image.",
                paramName: nameof(sourceLayout)
            ),
        };

        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: imageHandle,
            oldLayout: sourceLayout, newLayout: GpuImageLayout.TransferSource,
            sourceAccessMask: sourceAccess, destinationAccessMask: GpuAccess.TransferRead,
            sourceStageMask: sourceStages, destinationStageMask: GpuStage.Transfer);
        recorder.CopyImageToBuffer(bufferHandle: destinationBufferHandle, commandBufferHandle: commandBuffer, footprint: in footprint, imageHandle: imageHandle);
        recorder.TransitionBuffer(bufferHandle: destinationBufferHandle, commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer);
        recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, imageHandle: imageHandle,
            oldLayout: GpuImageLayout.TransferSource, newLayout: sourceLayout,
            sourceAccessMask: GpuAccess.TransferRead, destinationAccessMask: sourceAccess,
            sourceStageMask: GpuStage.Transfer, destinationStageMask: sourceStages);
    }
    /// <summary>Records the copy of a storage buffer a recording just wrote into a readback buffer, and its barriers.
    /// The source is left in <see cref="GpuAccess.TransferRead"/> at <see cref="GpuStage.Transfer"/>; the caller returns
    /// it to its own next access.</summary>
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

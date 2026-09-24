using System.Runtime.CompilerServices;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// Wraps a render node's neutral GPU services so each call is passed through once and counted once into a
/// <see cref="GpuWorkLedger"/>. A wrapper counts after its forwarded call returns, so a call that throws is not
/// counted. Wrapping a wrapper is refused, because its calls would be counted twice.
/// <para>
/// Per-submission counts go to the pass the ledger is in: dispatches, indirect dispatches, draws, render passes,
/// command buffers begun, image, memory, and buffer barriers, pipeline and descriptor-set binds, push-constant bytes,
/// descriptor writes, bytes written to host-visible storage buffers, and storage image and buffer clears. Lifetime
/// counts are the compute and graphics pipelines, shader modules, images, storage buffers, descriptor pools,
/// and descriptor sets created. See <see cref="GpuWork"/> for the kinds.
/// </para>
/// </summary>
public static class GpuWorkCounting {
    /// <summary>Wraps every counted member of a compute service bundle. The command-pool factory and the
    /// surface-transfer factory are passed through unwrapped, because nothing they do is counted.</summary>
    /// <param name="services">The node's services.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>A bundle whose members count into <paramref name="ledger"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="services"/> is already a counting bundle.</exception>
    public static IGpuComputeServices Wrap(IGpuComputeServices services, GpuWorkLedger ledger) =>
        new CountingComputeServices(
            ledger: ledger,
            services: Guard(
                instance: services,
                ledger: ledger
            )
        );
    /// <summary>Wraps a graphics command recorder. Counts command buffers begun, render passes begun, pipeline and
    /// descriptor-set binds, push-constant bytes, and draws.</summary>
    /// <param name="recorder">The recorder to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting recorder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="recorder"/> is already counting.</exception>
    public static IGpuCommandRecorder Wrap(IGpuCommandRecorder recorder, GpuWorkLedger ledger) =>
        new CountingCommandRecorder(
            inner: Guard(
                instance: recorder,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a compute command recorder. Counts command buffers begun, pipeline and descriptor-set binds,
    /// push-constant bytes, dispatches, indirect dispatches, and image, memory, and buffer barriers. The wrapper
    /// implements <see cref="IGpuImageInitializationRecorder"/> and <see cref="IGpuBufferInitializationRecorder"/>
    /// exactly when <paramref name="recorder"/> does, and counts each clear.</summary>
    /// <param name="recorder">The recorder to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting recorder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="recorder"/> is already counting.</exception>
    public static IGpuComputeRecorder Wrap(IGpuComputeRecorder recorder, GpuWorkLedger ledger) {
        _ = Guard(
            instance: recorder,
            ledger: ledger
        );

        return (recorder, recorder) switch {
            (IGpuImageInitializationRecorder image, IGpuBufferInitializationRecorder buffer) => new CountingClearingComputeRecorder(
                bufferClears: buffer,
                imageClears: image,
                inner: recorder,
                ledger: ledger
            ),
            (IGpuImageInitializationRecorder image, _) => new CountingImageClearingComputeRecorder(
                imageClears: image,
                inner: recorder,
                ledger: ledger
            ),
            (_, IGpuBufferInitializationRecorder buffer) => new CountingBufferClearingComputeRecorder(
                bufferClears: buffer,
                inner: recorder,
                ledger: ledger
            ),
            _ => new CountingComputeRecorder(
                inner: recorder,
                ledger: ledger
            ),
        };
    }
    /// <summary>Wraps a descriptor allocator. Counts every descriptor write, and the descriptor pools and sets created.</summary>
    /// <param name="allocator">The allocator to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting allocator.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="allocator"/> is already counting.</exception>
    public static IGpuDescriptorAllocator Wrap(IGpuDescriptorAllocator allocator, GpuWorkLedger ledger) =>
        new CountingDescriptorAllocator(
            inner: Guard(
                instance: allocator,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a compute pipeline factory. Counts the pipelines created; the pipelines themselves are the
    /// backend's, unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuComputePipelineFactory Wrap(IGpuComputePipelineFactory factory, GpuWorkLedger ledger) =>
        new CountingComputePipelineFactory(
            inner: Guard(
                instance: factory,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a graphics pipeline factory. Counts the pipelines created; the pipelines themselves are the
    /// backend's, unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuPipelineFactory Wrap(IGpuPipelineFactory factory, GpuWorkLedger ledger) =>
        new CountingPipelineFactory(
            inner: Guard(
                instance: factory,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a queue submitter. Each submission seals the ledger's current record with the next submission
    /// identity. A fence from <see cref="IGpuQueueSubmitter.CreateSubmissionFence"/> is wrapped so its
    /// <see cref="IGpuSubmissionFence.Wait"/> completes its submission, and it is unwrapped again before it reaches
    /// <paramref name="submitter"/>. A <see cref="IGpuQueueSubmitter.SubmitAndWait"/> submission completes at once;
    /// an unfenced one completes when a later submission of the same ledger does.</summary>
    /// <param name="submitter">The submitter to forward to.</param>
    /// <param name="ledger">The ledger the submissions are sealed in.</param>
    /// <returns>The counting submitter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="submitter"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="submitter"/> is already counting.</exception>
    public static IGpuQueueSubmitter Wrap(IGpuQueueSubmitter submitter, GpuWorkLedger ledger) =>
        new CountingQueueSubmitter(
            inner: Guard(
                instance: submitter,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a shader-module factory. Counts the modules created; the modules themselves are the backend's,
    /// unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuShaderModuleFactory Wrap(IGpuShaderModuleFactory factory, GpuWorkLedger ledger) =>
        new CountingShaderModuleFactory(
            inner: Guard(
                instance: factory,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a storage-buffer factory. Counts every buffer created. A host-writable buffer comes back
    /// wrapped, and each <see cref="IGpuStorageBuffer.Write{T}(ReadOnlySpan{T})"/> counts the bytes written; a
    /// device-local buffer comes back unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuStorageBufferFactory Wrap(IGpuStorageBufferFactory factory, GpuWorkLedger ledger) =>
        new CountingStorageBufferFactory(
            inner: Guard(
                instance: factory,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps an image factory. Counts the images created, whatever their usages; the images themselves are the
    /// backend's, unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuImageFactory Wrap(IGpuImageFactory factory, GpuWorkLedger ledger) =>
        new CountingImageFactory(
            inner: Guard(
                instance: factory,
                ledger: ledger
            ),
            ledger: ledger
        );

    // Refuses a null service or ledger, and a service that already counts; returns the service to wrap.
    private static T Guard<T>(T instance, GpuWorkLedger ledger, [CallerArgumentExpression(parameterName: nameof(instance))] string? paramName = null) where T : class {
        ArgumentNullException.ThrowIfNull(
            argument: instance,
            paramName: paramName
        );
        ArgumentNullException.ThrowIfNull(ledger);

        if (instance is CountingWrapper) {
            throw new ArgumentException(
                message: "The service already counts GPU work; wrapping it again would count every call twice.",
                paramName: paramName
            );
        }

        return instance;
    }
}

// The one shape every counting wrapper shares: it counts into its ledger after the forwarded call returns, and a
// service of this type is refused as the inner of another wrapper, which would count every call twice.
file abstract class CountingWrapper(GpuWorkLedger ledger) {
    protected GpuWorkLedger Ledger =>
        ledger;

    protected T Created<T>(T created, int lifetimeIndex) {
        ledger.CountCreated(lifetimeIndex: lifetimeIndex);

        return created;
    }
    protected void Tally(int column, long amount = 1L) =>
        ledger.Count(
            amount: amount,
            column: column
        );
}
file sealed class CountingComputeServices(IGpuComputeServices services, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuComputeServices {
    public IGpuComputeCommandPoolFactory CommandPoolFactory { get; } = services.CommandPoolFactory;
    public IGpuComputePipelineFactory ComputePipelineFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.ComputePipelineFactory,
        ledger: ledger
    );
    public IGpuComputeRecorder ComputeRecorder { get; } = GpuWorkCounting.Wrap(
        ledger: ledger,
        recorder: services.ComputeRecorder
    );
    public IGpuDescriptorAllocator DescriptorAllocator { get; } = GpuWorkCounting.Wrap(
        allocator: services.DescriptorAllocator,
        ledger: ledger
    );
    public IGpuQueueSubmitter QueueSubmitter { get; } = GpuWorkCounting.Wrap(
        ledger: ledger,
        submitter: services.QueueSubmitter
    );
    public IGpuShaderModuleFactory ShaderModuleFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.ShaderModuleFactory,
        ledger: ledger
    );
    public IGpuStorageBufferFactory StorageBufferFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.StorageBufferFactory,
        ledger: ledger
    );
    public IGpuImageFactory ImageFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.ImageFactory,
        ledger: ledger
    );
    public IGpuSurfaceTransferFactory SurfaceTransferFactory { get; } = services.SurfaceTransferFactory;
}
file sealed class CountingCommandRecorder(IGpuCommandRecorder inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuCommandRecorder {
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        inner.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.CommandBuffersColumn);
    }
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        inner.BeginDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            label: label
        );
    public void BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer) {
        inner.BeginRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            framebuffer: framebuffer
        );
        Tally(column: GpuWork.RenderPassesColumn);
    }
    public void BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        inner.BindDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            pipelineLayoutHandle: pipelineLayoutHandle
        );
        Tally(column: GpuWork.DescriptorSetBindsColumn);
    }
    public void BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        inner.BindGraphicsPipeline(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            pipelineHandle: pipelineHandle
        );
        Tally(column: GpuWork.PipelineBindsColumn);
    }
    public void BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) =>
        inner.BindVertexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            sizeBytes: sizeBytes,
            strideBytes: strideBytes
        );
    public void BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) =>
        inner.BindIndexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            format: format,
            offsetBytes: offsetBytes,
            sizeBytes: sizeBytes
        );
    public void Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters) {
        inner.Draw(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            parameters: in parameters
        );
        Tally(column: GpuWork.DrawsColumn);
    }
    public void DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount) {
        inner.DrawIndexed(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            indexCount: indexCount
        );
        Tally(column: GpuWork.DrawsColumn);
    }
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        inner.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) =>
        inner.EndDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    public void EndRenderPass(nint deviceHandle, nint commandBufferHandle) =>
        inner.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        inner.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            deviceHandle: deviceHandle,
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: stageFlags
        );
        Tally(
            amount: data.Length,
            column: GpuWork.PushConstantBytesColumn
        );
    }
    public void SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) =>
        inner.SetScissor(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            height: height,
            width: width,
            x: x,
            y: y
        );
}
file class CountingComputeRecorder(IGpuComputeRecorder inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuComputeRecorder {
    public void BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) {
        inner.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.CommandBuffersColumn);
    }
    public void BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) =>
        inner.BeginDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            label: label
        );
    public void BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        inner.BindComputeDescriptorSet(
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            pipelineLayoutHandle: pipelineLayoutHandle
        );
        Tally(column: GpuWork.DescriptorSetBindsColumn);
    }
    public void BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
        inner.BindComputePipeline(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            pipelineHandle: pipelineHandle
        );
        Tally(column: GpuWork.PipelineBindsColumn);
    }
    public void Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        inner.Dispatch(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            groupCountX: groupCountX,
            groupCountY: groupCountY,
            groupCountZ: groupCountZ
        );
        Tally(column: GpuWork.DispatchesColumn);
    }
    public void DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) {
        inner.DispatchIndirect(
            argumentBufferHandle: argumentBufferHandle,
            argumentBufferOffset: argumentBufferOffset,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.IndirectDispatchesColumn);
    }
    public void EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) =>
        inner.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    public void EndDebugGroup(nint deviceHandle, nint commandBufferHandle) =>
        inner.EndDebugGroup(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
    public void MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.MemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            deviceHandle: deviceHandle,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.MemoryBarriersColumn);
    }
    public void PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        inner.PushConstants(
            commandBufferHandle: commandBufferHandle,
            data: data,
            deviceHandle: deviceHandle,
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: stageFlags
        );
        Tally(
            amount: data.Length,
            column: GpuWork.PushConstantBytesColumn
        );
    }
    public void TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.TransitionBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            deviceHandle: deviceHandle,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.BufferBarriersColumn);
    }
    public void TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.TransitionImageLayout(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            deviceHandle: deviceHandle,
            imageHandle: imageHandle,
            newLayout: newLayout,
            oldLayout: oldLayout,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.ImageBarriersColumn);
    }

    protected void ClearBuffer(IGpuBufferInitializationRecorder bufferClears, nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        bufferClears.ClearStorageBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            sizeBytes: sizeBytes
        );
        Tally(column: GpuWork.ClearsColumn);
    }
    protected void ClearImage(IGpuImageInitializationRecorder imageClears, nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        imageClears.ClearStorageImage(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            format: format,
            imageHandle: imageHandle
        );
        Tally(column: GpuWork.ClearsColumn);
    }
}
file sealed class CountingBufferClearingComputeRecorder(IGpuComputeRecorder inner, IGpuBufferInitializationRecorder bufferClears, GpuWorkLedger ledger)
    : CountingComputeRecorder(
        inner: inner,
        ledger: ledger
    ), IGpuBufferInitializationRecorder {
    public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) =>
        ClearBuffer(
            bufferClears: bufferClears,
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            sizeBytes: sizeBytes
        );
}
file sealed class CountingClearingComputeRecorder(IGpuComputeRecorder inner, IGpuImageInitializationRecorder imageClears, IGpuBufferInitializationRecorder bufferClears, GpuWorkLedger ledger)
    : CountingComputeRecorder(
        inner: inner,
        ledger: ledger
    ), IGpuImageInitializationRecorder, IGpuBufferInitializationRecorder {
    public void ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) =>
        ClearBuffer(
            bufferClears: bufferClears,
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            sizeBytes: sizeBytes
        );
    public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) =>
        ClearImage(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            format: format,
            imageClears: imageClears,
            imageHandle: imageHandle
        );
}
file sealed class CountingImageClearingComputeRecorder(IGpuComputeRecorder inner, IGpuImageInitializationRecorder imageClears, GpuWorkLedger ledger)
    : CountingComputeRecorder(
        inner: inner,
        ledger: ledger
    ), IGpuImageInitializationRecorder {
    public void ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) =>
        ClearImage(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            format: format,
            imageClears: imageClears,
            imageHandle: imageHandle
        );
}
file sealed class CountingComputePipelineFactory(IGpuComputePipelineFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuComputePipelineFactory {
    public IGpuComputePipeline Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) =>
        Created(
            created: inner.Create(
                computeShaderModule: computeShaderModule,
                description: description,
                deviceContext: deviceContext
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
}
file sealed class CountingDescriptorAllocator(IGpuDescriptorAllocator inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuDescriptorAllocator {
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) =>
        Created(
            created: inner.AllocateSet(
                descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                deviceHandle: deviceHandle,
                poolHandle: poolHandle
            ),
            lifetimeIndex: GpuWork.DescriptorSetsCreatedIndex
        );
    public nint CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) =>
        Created(
            created: inner.CreatePool(
                deviceHandle: deviceHandle,
                sizes: in sizes
            ),
            lifetimeIndex: GpuWork.DescriptorPoolsCreatedIndex
        );
    public nint CreateSampler(nint deviceHandle, GpuSamplerFilter filter = GpuSamplerFilter.Linear) =>
        inner.CreateSampler(
            deviceHandle: deviceHandle,
            filter: filter
        );
    public void DestroyPool(nint deviceHandle, nint poolHandle) =>
        inner.DestroyPool(
            deviceHandle: deviceHandle,
            poolHandle: poolHandle
        );
    public void DestroySampler(nint deviceHandle, nint samplerHandle) =>
        inner.DestroySampler(
            deviceHandle: deviceHandle,
            samplerHandle: samplerHandle
        );
    public void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        inner.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            imageViewHandle: imageViewHandle,
            samplerHandle: samplerHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable) {
        inner.WriteRawBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            writable: writable
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        inner.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        inner.WriteStorageBufferReadOnly(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        inner.WriteStorageBufferReadWrite(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        inner.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            imageViewHandle: imageViewHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }

    private void CountWrite() =>
        Tally(column: GpuWork.DescriptorWritesColumn);
}
file sealed class CountingPipelineFactory(IGpuPipelineFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuPipelineFactory {
    public IGpuPipeline Create(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, uint width, uint height) =>
        Created(
            created: inner.Create(
                description: description,
                deviceContext: deviceContext,
                fragmentShaderModule: fragmentShaderModule,
                height: height,
                renderPass: renderPass,
                vertexShaderModule: vertexShaderModule,
                width: width
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
}
file sealed class CountingQueueSubmitter(IGpuQueueSubmitter inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuQueueSubmitter {
    public IGpuSubmissionFence CreateSubmissionFence(IGpuDeviceContext deviceContext) =>
        new GpuWorkCountingFence(
            inner: inner.CreateSubmissionFence(deviceContext: deviceContext),
            ledger: Ledger
        );
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        inner.Submit(
            commandBufferHandles: commandBufferHandles,
            deviceContext: deviceContext
        );
        _ = Ledger.Seal(fence: null);
    }
    public void Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var counting = (fence as GpuWorkCountingFence);

        inner.Submit(
            commandBufferHandles: commandBufferHandles,
            deviceContext: deviceContext,
            fence: (counting?.Inner ?? fence)
        );

        // A fence counting for another ledger still reaches the backend, but only this ledger's fences complete here.
        var owned = (ReferenceEquals(
            objA: counting?.Ledger,
            objB: Ledger
        ) ? counting : null);
        var submission = Ledger.Seal(fence: owned);

        owned?.ArmedSubmission = submission;
    }
    public void SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) {
        inner.SubmitAndWait(
            commandBufferHandles: commandBufferHandles,
            deviceContext: deviceContext
        );
        Ledger.Complete(submission: Ledger.Seal(fence: null));
    }
}
file sealed class CountingShaderModuleFactory(IGpuShaderModuleFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuShaderModuleFactory {
    public IGpuShaderModule Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) =>
        Created(
            created: inner.Create(
                bytecode: bytecode,
                deviceContext: deviceContext,
                stage: stage
            ),
            lifetimeIndex: GpuWork.ShaderModulesCreatedIndex
        );
}
file sealed class CountingStorageBuffer(IGpuStorageBuffer inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuStorageBuffer {
    public nint BufferHandle =>
        inner.BufferHandle;
    public ulong SizeBytes =>
        inner.SizeBytes;

    public void Dispose() =>
        inner.Dispose();
    public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged {
        inner.Write(data: data);
        CountBytes(data: data);
    }
    public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged {
        inner.Write(
            data: data,
            destinationOffsetBytes: destinationOffsetBytes
        );
        CountBytes(data: data);
    }

    private void CountBytes<T>(ReadOnlySpan<T> data) where T : unmanaged =>
        Tally(
            amount: checked((((long)data.Length) * Unsafe.SizeOf<T>())),
            column: GpuWork.HostVisibleUploadBytesColumn
        );
}
file sealed class CountingStorageBufferFactory(IGpuStorageBufferFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuStorageBufferFactory {
    public IGpuStorageBuffer Create(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        WrapHostWritable(buffer: inner.Create(
            deviceContext: deviceContext,
            sizeBytes: sizeBytes
        ));
    public IGpuBuffer CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        Created(
            created: inner.CreateDeviceLocal(
                deviceContext: deviceContext,
                sizeBytes: sizeBytes
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
    public IGpuBuffer CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        Created(
            created: inner.CreateDeviceLocalIndirectArgs(
                deviceContext: deviceContext,
                sizeBytes: sizeBytes
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
    public IGpuStorageBuffer CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) =>
        WrapHostWritable(buffer: inner.CreateIndirectArgs(
            deviceContext: deviceContext,
            sizeBytes: sizeBytes
        ));

    private CountingStorageBuffer WrapHostWritable(IGpuStorageBuffer buffer) =>
        Created(
            created: new CountingStorageBuffer(
                inner: buffer,
                ledger: Ledger
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
}
file sealed class CountingImageFactory(IGpuImageFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuImageFactory {
    public IGpuImage Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        Created(
            created: inner.Create(
                deviceContext: deviceContext,
                format: format,
                height: height,
                usage: usage,
                width: width
            ),
            lifetimeIndex: GpuWork.ImagesCreatedIndex
        );
}

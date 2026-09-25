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
    /// <summary>Wraps a command recorder. Counts command buffers begun, render passes begun, pipeline and
    /// descriptor-set binds, push-constant bytes, draws, dispatches, indirect dispatches, image, memory, and buffer
    /// barriers, and storage image and buffer clears.</summary>
    /// <param name="recorder">The recorder to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting recorder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="recorder"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="recorder"/> is already counting.</exception>
    public static IGpuRecorder Wrap(IGpuRecorder recorder, GpuWorkLedger ledger) =>
        new CountingRecorder(
            inner: Guard(
                instance: recorder,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a bindings service. Counts every descriptor write, and the descriptor pools and sets created.</summary>
    /// <param name="bindings">The bindings service to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting bindings service.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bindings"/> is already counting.</exception>
    public static IGpuBindings Wrap(IGpuBindings bindings, GpuWorkLedger ledger) =>
        new CountingBindings(
            inner: Guard(
                instance: bindings,
                ledger: ledger
            ),
            ledger: ledger
        );
    /// <summary>Wraps a pipeline factory. Counts the compute and graphics pipelines created; the pipelines themselves
    /// are the backend's, unwrapped.</summary>
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
    /// <summary>Wraps a buffer factory. Counts every buffer created. A host-visible buffer comes back wrapped, and each
    /// <see cref="IGpuStorageBuffer.Write{T}(ReadOnlySpan{T})"/> counts the bytes written, as does the initial data
    /// one is created with; a device-local buffer comes back unwrapped.</summary>
    /// <param name="factory">The factory to forward to.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>The counting factory.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="factory"/> is already counting.</exception>
    public static IGpuBufferFactory Wrap(IGpuBufferFactory factory, GpuWorkLedger ledger) =>
        new CountingBufferFactory(
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
    public IGpuPipelineFactory PipelineFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.PipelineFactory,
        ledger: ledger
    );
    public IGpuRecorder Recorder { get; } = GpuWorkCounting.Wrap(
        ledger: ledger,
        recorder: services.Recorder
    );
    public IGpuBindings Bindings { get; } = GpuWorkCounting.Wrap(
        bindings: services.Bindings,
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
    public IGpuBufferFactory BufferFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.BufferFactory,
        ledger: ledger
    );
    public IGpuImageFactory ImageFactory { get; } = GpuWorkCounting.Wrap(
        factory: services.ImageFactory,
        ledger: ledger
    );
    public IGpuSurfaceTransferFactory SurfaceTransferFactory { get; } = services.SurfaceTransferFactory;
}
file sealed class CountingRecorder(IGpuRecorder inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuRecorder {
    public void BeginCommandBuffer(nint commandBufferHandle) {
        inner.BeginCommandBuffer(commandBufferHandle: commandBufferHandle);
        Tally(column: GpuWork.CommandBuffersColumn);
    }
    public void EndCommandBuffer(nint commandBufferHandle) =>
        inner.EndCommandBuffer(commandBufferHandle: commandBufferHandle);
    public void BeginDebugGroup(nint commandBufferHandle, string label) =>
        inner.BeginDebugGroup(
            commandBufferHandle: commandBufferHandle,
            label: label
        );
    public void EndDebugGroup(nint commandBufferHandle) =>
        inner.EndDebugGroup(commandBufferHandle: commandBufferHandle);
    public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) {
        inner.BeginRenderPass(
            area: area,
            commandBufferHandle: commandBufferHandle,
            framebuffer: framebuffer
        );
        Tally(column: GpuWork.RenderPassesColumn);
    }
    public void EndRenderPass(nint commandBufferHandle) =>
        inner.EndRenderPass(commandBufferHandle: commandBufferHandle);
    public void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) {
        inner.BindPipeline(
            bindPoint: bindPoint,
            commandBufferHandle: commandBufferHandle,
            pipelineHandle: pipelineHandle
        );
        Tally(column: GpuWork.PipelineBindsColumn);
    }
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) {
        inner.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            pipelineLayoutHandle: pipelineLayoutHandle
        );
        Tally(column: GpuWork.DescriptorSetBindsColumn);
    }
    public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
        inner.PushConstants(
            bindPoint: bindPoint,
            commandBufferHandle: commandBufferHandle,
            data: data,
            offset: offset,
            pipelineLayoutHandle: pipelineLayoutHandle,
            stageFlags: stageFlags
        );
        Tally(
            amount: data.Length,
            column: GpuWork.PushConstantBytesColumn
        );
    }
    public void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) =>
        inner.BindVertexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            sizeBytes: sizeBytes,
            strideBytes: strideBytes
        );
    public void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) =>
        inner.BindIndexBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            format: format,
            offsetBytes: offsetBytes,
            sizeBytes: sizeBytes
        );
    public void SetScissor(nint commandBufferHandle, GpuPixelRect rect) =>
        inner.SetScissor(
            commandBufferHandle: commandBufferHandle,
            rect: rect
        );
    public void Draw(nint commandBufferHandle, in GpuDrawParameters parameters) {
        inner.Draw(
            commandBufferHandle: commandBufferHandle,
            parameters: in parameters
        );
        Tally(column: GpuWork.DrawsColumn);
    }
    public void DrawIndexed(nint commandBufferHandle, uint indexCount) {
        inner.DrawIndexed(
            commandBufferHandle: commandBufferHandle,
            indexCount: indexCount
        );
        Tally(column: GpuWork.DrawsColumn);
    }
    public void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) {
        inner.Dispatch(
            commandBufferHandle: commandBufferHandle,
            groupCountX: groupCountX,
            groupCountY: groupCountY,
            groupCountZ: groupCountZ
        );
        Tally(column: GpuWork.DispatchesColumn);
    }
    public void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) {
        inner.DispatchIndirect(
            argumentBufferHandle: argumentBufferHandle,
            argumentBufferOffset: argumentBufferOffset,
            commandBufferHandle: commandBufferHandle
        );
        Tally(column: GpuWork.IndirectDispatchesColumn);
    }
    public void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) {
        inner.ClearStorageImage(
            commandBufferHandle: commandBufferHandle,
            format: format,
            imageHandle: imageHandle
        );
        Tally(column: GpuWork.ClearsColumn);
    }
    public void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) {
        inner.ClearStorageBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            sizeBytes: sizeBytes
        );
        Tally(column: GpuWork.ClearsColumn);
    }
    public void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.TransitionImageLayout(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            imageHandle: imageHandle,
            newLayout: newLayout,
            oldLayout: oldLayout,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.ImageBarriersColumn);
    }
    public void MemoryBarrier(nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.MemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.MemoryBarriersColumn);
    }
    public void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) {
        inner.TransitionBuffer(
            bufferHandle: bufferHandle,
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.BufferBarriersColumn);
    }
}
file sealed class CountingBindings(IGpuBindings inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuBindings {
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) =>
        Created(
            created: inner.AllocateSet(
                descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                poolHandle: poolHandle
            ),
            lifetimeIndex: GpuWork.DescriptorSetsCreatedIndex
        );
    public nint CreatePool(in GpuDescriptorPoolSizes sizes) =>
        Created(
            created: inner.CreatePool(sizes: in sizes),
            lifetimeIndex: GpuWork.DescriptorPoolsCreatedIndex
        );
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) =>
        inner.CreateSampler(filter: filter);
    public void DestroyPool(nint poolHandle) =>
        inner.DestroyPool(poolHandle: poolHandle);
    public void DestroySampler(nint samplerHandle) =>
        inner.DestroySampler(samplerHandle: samplerHandle);
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBufferAccess access, uint elementStride) {
        inner.WriteBuffer(
            access: access,
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            elementStride: elementStride
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        inner.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle,
            samplerHandle: samplerHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        inner.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
}
file sealed class CountingPipelineFactory(IGpuPipelineFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuPipelineFactory {
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) =>
        Created(
            created: inner.Create(
                computeShaderModule: computeShaderModule,
                description: description
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
    public IGpuPipeline Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) =>
        Created(
            created: inner.Create(
                description: description,
                fragmentShaderModule: fragmentShaderModule,
                renderPass: renderPass,
                vertexShaderModule: vertexShaderModule
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
}
file sealed class CountingQueueSubmitter(IGpuQueueSubmitter inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuQueueSubmitter {
    public IGpuSubmissionFence CreateSubmissionFence() =>
        new GpuWorkCountingFence(
            inner: inner.CreateSubmissionFence(),
            ledger: Ledger
        );
    public void Submit(ReadOnlySpan<nint> commandBufferHandles) {
        inner.Submit(commandBufferHandles: commandBufferHandles);
        _ = Ledger.Seal(fence: null);
    }
    public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        var counting = (fence as GpuWorkCountingFence);

        inner.Submit(
            commandBufferHandles: commandBufferHandles,
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
    public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) {
        inner.SubmitAndWait(commandBufferHandles: commandBufferHandles);
        Ledger.Complete(submission: Ledger.Seal(fence: null));
    }
}
file sealed class CountingShaderModuleFactory(IGpuShaderModuleFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuShaderModuleFactory {
    public IGpuShaderModule Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) =>
        Created(
            created: inner.Create(
                bytecode: bytecode,
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
file sealed class CountingBufferFactory(IGpuBufferFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuBufferFactory {
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) =>
        Created(
            created: inner.CreateDeviceLocal(
                sizeBytes: sizeBytes,
                usage: usage
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) =>
        WrapHostVisible(buffer: inner.CreateHostVisible(
            sizeBytes: sizeBytes,
            usage: usage
        ));
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        var buffer = WrapHostVisible(buffer: inner.CreateHostVisible(
            data: data,
            usage: usage
        ));

        Tally(
            amount: data.Length,
            column: GpuWork.HostVisibleUploadBytesColumn
        );

        return buffer;
    }

    private CountingStorageBuffer WrapHostVisible(IGpuStorageBuffer buffer) =>
        Created(
            created: new CountingStorageBuffer(
                inner: buffer,
                ledger: Ledger
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
}
file sealed class CountingImageFactory(IGpuImageFactory inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuImageFactory {
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) =>
        Created(
            created: inner.Create(
                format: format,
                height: height,
                usage: usage,
                width: width
            ),
            lifetimeIndex: GpuWork.ImagesCreatedIndex
        );
}

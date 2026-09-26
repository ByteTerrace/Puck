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
    /// <summary>Wraps every counted member of a device's services. The command-pool, render-pass and surface-transfer
    /// factories are passed through unwrapped, because nothing they do is counted.</summary>
    /// <param name="services">The device's services.</param>
    /// <param name="ledger">The ledger the counts go to.</param>
    /// <returns>A set whose counted members count into <paramref name="ledger"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A member of <paramref name="services"/> already counts.</exception>
    public static GpuDeviceServices Wrap(GpuDeviceServices services, GpuWorkLedger ledger) {
        ArgumentNullException.ThrowIfNull(services);

        return new GpuDeviceServices {
            Bindings = Wrap(
                bindings: services.Bindings,
                ledger: ledger
            ),
            BufferFactory = Wrap(
                factory: services.BufferFactory,
                ledger: ledger
            ),
            CommandPoolFactory = services.CommandPoolFactory,
            Faults = services.Faults,
            Naming = services.Naming,
            ImageFactory = Wrap(
                factory: services.ImageFactory,
                ledger: ledger
            ),
            PipelineFactory = Wrap(
                factory: services.PipelineFactory,
                ledger: ledger
            ),
            QueueSubmitter = Wrap(
                ledger: ledger,
                submitter: services.QueueSubmitter
            ),
            Recorder = Wrap(
                ledger: ledger,
                recorder: services.Recorder
            ),
            RenderPassFactory = services.RenderPassFactory,
            ShaderModuleFactory = Wrap(
                factory: services.ShaderModuleFactory,
                ledger: ledger
            ),
            SurfaceTransferFactory = services.SurfaceTransferFactory,
        };
    }
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
    public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) {
        inner.BindDescriptorSet(
            bindPoint: bindPoint,
            commandBufferHandle: commandBufferHandle,
            descriptorSetHandle: descriptorSetHandle,
            group: group,
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
    public void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
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
    public void MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
        inner.MemoryBarrier(
            commandBufferHandle: commandBufferHandle,
            destinationAccessMask: destinationAccessMask,
            destinationStageMask: destinationStageMask,
            sourceAccessMask: sourceAccessMask,
            sourceStageMask: sourceStageMask
        );
        Tally(column: GpuWork.MemoryBarriersColumn);
    }
    public void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) {
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
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) =>
        Created(
            created: inner.AllocateSet(
                descriptorSetLayoutHandle: descriptorSetLayoutHandle,
                name: name,
                poolHandle: poolHandle
            ),
            lifetimeIndex: GpuWork.DescriptorSetsCreatedIndex
        );

    public long HeapReleaseRevision => inner.HeapReleaseRevision;

    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) =>
        inner.CanAdmit(
            owner: owner,
            pools: pools,
            refusal: out refusal
        );
    public nint CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) =>
        Created(
            created: inner.CreatePool(name: name, sizes: in sizes),
            lifetimeIndex: GpuWork.DescriptorPoolsCreatedIndex
        );
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) =>
        inner.CreateSampler(filter: filter);
    public void DestroyPool(nint poolHandle) =>
        inner.DestroyPool(poolHandle: poolHandle);
    public void DestroySampler(nint samplerHandle) =>
        inner.DestroySampler(samplerHandle: samplerHandle);
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) {
        inner.WriteBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            elementStride: elementStride,
            kind: kind
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) {
        inner.WriteConstantBuffer(
            arrayElement: arrayElement,
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        inner.WriteSampledImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            imageViewHandle: imageViewHandle
        );
        Tally(column: GpuWork.DescriptorWritesColumn);
    }
    public void WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) {
        inner.WriteSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
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
    public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) =>
        Created(
            created: inner.Create(
                computeShaderModule: computeShaderModule,
                description: description,
                name: name
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
    public IGpuPipeline Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) =>
        Created(
            created: inner.Create(
                description: description,
                fragmentShaderModule: fragmentShaderModule,
                name: name,
                renderPass: renderPass,
                vertexShaderModule: vertexShaderModule
            ),
            lifetimeIndex: GpuWork.PipelinesCreatedIndex
        );
}
file sealed class CountingQueueSubmitter(IGpuQueueSubmitter inner, GpuWorkLedger ledger) : CountingWrapper(ledger: ledger), IGpuQueueSubmitter {
    public void AddExternalWait(GpuExternalWait wait) =>
        inner.AddExternalWait(wait: wait);
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
    public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) =>
        Created(
            created: inner.CreateDeviceLocal(
                name: name,
                sizeBytes: sizeBytes,
                usage: usage
            ),
            lifetimeIndex: GpuWork.BuffersCreatedIndex
        );
    public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) =>
        WrapHostVisible(buffer: inner.CreateHostVisible(
            name: name,
            sizeBytes: sizeBytes,
            usage: usage
        ));
    public IGpuStorageBuffer CreateHostVisibleDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) =>
        WrapHostVisible(buffer: inner.CreateHostVisibleDeviceLocal(
            name: name,
            sizeBytes: sizeBytes,
            usage: usage
        ));
    public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) {
        var buffer = WrapHostVisible(buffer: inner.CreateHostVisible(
            data: data,
            name: name,
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
    public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) =>
        Created(
            created: inner.Create(
                format: format,
                height: height,
                name: name,
                usage: usage,
                width: width
            ),
            lifetimeIndex: GpuWork.ImagesCreatedIndex
        );
}

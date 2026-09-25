using Puck.Abstractions.Gpu;

namespace Puck.Testing;

/// <summary>
/// A device whose every service call refuses: for a law over code that must hold its services without ever reaching
/// them, such as a node that is built but never produces, or a teardown after a device that never came up. Each refusal
/// throws and is counted in <see cref="Reaches"/>, because code that keeps an earlier fault could otherwise swallow the
/// throw. Draining returns at once, since nothing was ever submitted, and destroying the zero pool handle is accepted,
/// because a node that never allocated a pool releases that one.
/// </summary>
internal sealed class RefusingGpuDevice :
    IGpuDeviceContext,
    IGpuCommandPoolFactory,
    IGpuPipelineFactory,
    IGpuRecorder,
    IGpuBindings,
    IGpuQueueSubmitter,
    IGpuShaderModuleFactory,
    IGpuBufferFactory,
    IGpuImageFactory,
    IGpuRenderPassFactory,
    IGpuSurfaceTransferFactory {
    private int m_reaches;

    /// <summary>Initializes a new instance of the <see cref="RefusingGpuDevice"/> class.</summary>
    public RefusingGpuDevice() =>
        Services = new GpuDeviceServices {
            Bindings = this,
            BufferFactory = this,
            CommandPoolFactory = this,
            ImageFactory = this,
            PipelineFactory = this,
            QueueSubmitter = this,
            Recorder = this,
            RenderPassFactory = this,
            ShaderModuleFactory = this,
            SurfaceTransferFactory = this,
        };

    public long AdapterLuid => 0L;
    public GpuDeviceCapabilities? Capabilities => null;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;
    /// <summary>Gets the number of calls refused.</summary>
    public int Reaches => Volatile.Read(location: ref m_reaches);
    /// <summary>Gets this device as every one of its own services.</summary>
    public GpuDeviceServices Services { get; }

    /// <summary>Counts a call that must not happen and returns the exception its caller throws.</summary>
    /// <param name="member">The member reached, named in the message.</param>
    /// <returns>The refusal.</returns>
    public InvalidOperationException Reach(string member) {
        _ = Interlocked.Increment(location: ref m_reaches);

        return new InvalidOperationException(message: $"A GPU seam was reached: {member}.");
    }
    public void WaitIdle() { }

    IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description, in GpuObjectName name) => throw Reach(member: "IGpuRenderPassFactory.Create");
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => throw Reach(member: "IGpuRenderPassFactory.CreateFramebuffer");
    IGpuCommandPool IGpuCommandPoolFactory.Create(in GpuObjectName name) => throw Reach(member: "IGpuCommandPoolFactory.Create");
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description, in GpuObjectName name) => throw Reach(member: "IGpuPipelineFactory.Create(compute)");
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, in GpuObjectName name) => throw Reach(member: "IGpuPipelineFactory.Create(graphics)");
    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) => throw Reach(member: "IGpuRecorder.BeginCommandBuffer");
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) => throw Reach(member: "IGpuRecorder.EndCommandBuffer");
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) => throw Reach(member: "IGpuRecorder.BeginDebugGroup");
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) => throw Reach(member: "IGpuRecorder.EndDebugGroup");
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) => throw Reach(member: "IGpuRecorder.BeginRenderPass");
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) => throw Reach(member: "IGpuRecorder.EndRenderPass");
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => throw Reach(member: "IGpuRecorder.BindPipeline");
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, uint group, nint descriptorSetHandle) => throw Reach(member: "IGpuRecorder.BindDescriptorSet");
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => throw Reach(member: "IGpuRecorder.PushConstants");
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => throw Reach(member: "IGpuRecorder.BindVertexBuffer");
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => throw Reach(member: "IGpuRecorder.BindIndexBuffer");
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) => throw Reach(member: "IGpuRecorder.SetScissor");
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => throw Reach(member: "IGpuRecorder.Draw");
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) => throw Reach(member: "IGpuRecorder.DrawIndexed");
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => throw Reach(member: "IGpuRecorder.Dispatch");
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => throw Reach(member: "IGpuRecorder.DispatchIndirect");
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => throw Reach(member: "IGpuRecorder.ClearStorageImage");
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => throw Reach(member: "IGpuRecorder.ClearStorageBuffer");
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reach(member: "IGpuRecorder.TransitionImageLayout");
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reach(member: "IGpuRecorder.MemoryBarrier");
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reach(member: "IGpuRecorder.TransitionBuffer");

    // A revision read reaches no device: a holder reads it to decide whether a refused build is due again.
    long IGpuBindings.HeapReleaseRevision => 0L;

    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle, in GpuObjectName name) => throw Reach(member: "IGpuBindings.AllocateSet");
    bool IGpuBindings.CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) => throw Reach(member: "IGpuBindings.CanAdmit");
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes, in GpuObjectName name) => throw Reach(member: "IGpuBindings.CreatePool");
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => throw Reach(member: "IGpuBindings.CreateSampler");
    void IGpuBindings.DestroyPool(nint poolHandle) {
        if (poolHandle != 0) {
            throw Reach(member: "IGpuBindings.DestroyPool");
        }
    }
    void IGpuBindings.DestroySampler(nint samplerHandle) => throw Reach(member: "IGpuBindings.DestroySampler");
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw Reach(member: "IGpuBindings.WriteCombinedImageSampler");
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => throw Reach(member: "IGpuBindings.WriteBuffer");
    void IGpuBindings.WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) => throw Reach(member: "IGpuBindings.WriteConstantBuffer");
    void IGpuBindings.WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw Reach(member: "IGpuBindings.WriteSampledImage");
    void IGpuBindings.WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) => throw Reach(member: "IGpuBindings.WriteSampler");
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw Reach(member: "IGpuBindings.WriteStorageImage");
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => throw Reach(member: "IGpuQueueSubmitter.CreateSubmissionFence");
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) => throw Reach(member: "IGpuQueueSubmitter.Submit");
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw Reach(member: "IGpuQueueSubmitter.Submit(fence)");
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => throw Reach(member: "IGpuQueueSubmitter.SubmitAndWait");
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw Reach(member: "IGpuShaderModuleFactory.Create");
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => throw Reach(member: "IGpuBufferFactory.CreateHostVisible");
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage, in GpuObjectName name) => throw Reach(member: "IGpuBufferFactory.CreateHostVisible(data)");
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage, in GpuObjectName name) => throw Reach(member: "IGpuBufferFactory.CreateDeviceLocal");
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage, in GpuObjectName name) => throw Reach(member: "IGpuImageFactory.Create");
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw Reach(member: "IGpuSurfaceTransferFactory.CreateImport");
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => throw Reach(member: "IGpuSurfaceTransferFactory.CreateReadback");
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => throw Reach(member: "IGpuSurfaceTransferFactory.CreateUpload");
}

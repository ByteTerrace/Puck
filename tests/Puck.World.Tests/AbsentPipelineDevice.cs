using Puck.Abstractions.Gpu;

namespace Puck.World.Tests;

// A device a pipeline render node can be built over without a GPU: nothing to wait for, and a node built over it never
// records, allocates, or submits, so a law over it reads only its readiness. The node wraps every service once for
// counting when it is built, so each service exists, and every call on one refuses.
internal sealed class AbsentPipelineDevice : IGpuDeviceContext, IGpuCommandPoolFactory, IGpuPipelineFactory,
    IGpuRecorder, IGpuBindings, IGpuQueueSubmitter, IGpuShaderModuleFactory, IGpuBufferFactory,
    IGpuImageFactory, IGpuRenderPassFactory, IGpuSurfaceTransferFactory {
    public AbsentPipelineDevice() =>
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
    public GpuDeviceServices Services { get; }

    public void WaitIdle() { }

    IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description) => throw new NotSupportedException();
    IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => throw new NotSupportedException();
    IGpuCommandPool IGpuCommandPoolFactory.Create() => throw new NotSupportedException();
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) => throw new NotSupportedException();
    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) => throw new NotSupportedException();
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) => throw new NotSupportedException();
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => throw new NotSupportedException();
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) => throw new NotSupportedException();
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => throw new NotSupportedException();
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => throw new NotSupportedException();
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) => throw new NotSupportedException();
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => throw new NotSupportedException();
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) => throw new NotSupportedException();
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => throw new NotSupportedException();
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => throw new NotSupportedException();
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => throw new NotSupportedException();
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw new NotSupportedException();
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw new NotSupportedException();
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw new NotSupportedException();
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => throw new NotSupportedException();
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) => throw new NotSupportedException();
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => throw new NotSupportedException();
    void IGpuBindings.DestroyPool(nint poolHandle) {
        if (0 != poolHandle) {
            throw new NotSupportedException();
        }
    }
    void IGpuBindings.DestroySampler(nint samplerHandle) => throw new NotSupportedException();
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => throw new NotSupportedException();
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw new NotSupportedException();
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => throw new NotSupportedException();
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw new NotSupportedException();
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw new NotSupportedException();
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => throw new NotSupportedException();
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => throw new NotSupportedException();
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => throw new NotSupportedException();
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) => throw new NotSupportedException();
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => throw new NotSupportedException();
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => throw new NotSupportedException();
}

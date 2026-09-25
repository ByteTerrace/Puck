using Puck.Abstractions.Gpu;

namespace Puck.World.Tests;

/// <summary>A device a pipeline render node can be built over without a GPU: it has no handle and nothing to wait for.</summary>
internal sealed class AbsentPipelineDevice : IGpuDeviceContext {
    public long AdapterLuid => 0L;
    public nint DeviceHandle => 0;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;

    public void WaitIdle() { }
}
// A render node built over this never records, allocates, or submits: a law over it reads only its readiness. The
// node wraps every service once for counting when it is built, so each service exists, and every call on one refuses.
internal sealed class AbsentPipelineGpu : IGpuComputeServices, IGpuComputeCommandPoolFactory, IGpuComputePipelineFactory,
    IGpuRecorder, IGpuBindings, IGpuQueueSubmitter, IGpuShaderModuleFactory, IGpuStorageBufferFactory,
    IGpuImageFactory, IGpuSurfaceTransferFactory {
    public IGpuBindings Bindings => this;
    public IGpuComputeCommandPoolFactory CommandPoolFactory => this;
    public IGpuComputePipelineFactory ComputePipelineFactory => this;
    public IGpuImageFactory ImageFactory => this;
    public IGpuQueueSubmitter QueueSubmitter => this;
    public IGpuRecorder Recorder => this;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuStorageBufferFactory StorageBufferFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

    IGpuComputeCommandPool IGpuComputeCommandPoolFactory.Create(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuComputePipeline IGpuComputePipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) => throw new NotSupportedException();
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
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => throw new NotSupportedException();
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) => throw new NotSupportedException();
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => throw new NotSupportedException();
    void IGpuBindings.DestroyPool(nint poolHandle) => throw new NotSupportedException();
    void IGpuBindings.DestroySampler(nint samplerHandle) => throw new NotSupportedException();
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBufferAccess access, uint elementStride) => throw new NotSupportedException();
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw new NotSupportedException();
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw new NotSupportedException();
    void IGpuQueueSubmitter.SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
    IGpuShaderModule IGpuShaderModuleFactory.Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw new NotSupportedException();
    IGpuStorageBuffer IGpuStorageBufferFactory.Create(IGpuDeviceContext deviceContext, ulong sizeBytes) => throw new NotSupportedException();
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) => throw new NotSupportedException();
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) => throw new NotSupportedException();
    IGpuStorageBuffer IGpuStorageBufferFactory.CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) => throw new NotSupportedException();
    IGpuImage IGpuImageFactory.Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) => throw new NotSupportedException();
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
}

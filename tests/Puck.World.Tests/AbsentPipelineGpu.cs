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
    IGpuComputeRecorder, IGpuDescriptorAllocator, IGpuQueueSubmitter, IGpuShaderModuleFactory, IGpuStorageBufferFactory,
    IGpuImageFactory, IGpuSurfaceTransferFactory {
    public IGpuComputeCommandPoolFactory CommandPoolFactory => this;
    public IGpuComputePipelineFactory ComputePipelineFactory => this;
    public IGpuComputeRecorder ComputeRecorder => this;
    public IGpuDescriptorAllocator DescriptorAllocator => this;
    public IGpuImageFactory ImageFactory => this;
    public IGpuQueueSubmitter QueueSubmitter => this;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuStorageBufferFactory StorageBufferFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

    IGpuComputeCommandPool IGpuComputeCommandPoolFactory.Create(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuComputePipeline IGpuComputePipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) => throw new NotSupportedException();
    void IGpuComputeRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuComputeRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) => throw new NotSupportedException();
    void IGpuComputeRecorder.BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) => throw new NotSupportedException();
    void IGpuComputeRecorder.BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) => throw new NotSupportedException();
    void IGpuComputeRecorder.Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();
    void IGpuComputeRecorder.DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => throw new NotSupportedException();
    void IGpuComputeRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuComputeRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
    void IGpuComputeRecorder.MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    void IGpuComputeRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => throw new NotSupportedException();
    void IGpuComputeRecorder.TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    void IGpuComputeRecorder.TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
    nint IGpuDescriptorAllocator.AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) => throw new NotSupportedException();
    nint IGpuDescriptorAllocator.CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) => throw new NotSupportedException();
    nint IGpuDescriptorAllocator.CreateSampler(nint deviceHandle, GpuSamplerFilter filter) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.DestroyPool(nint deviceHandle, nint poolHandle) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.DestroySampler(nint deviceHandle, nint samplerHandle) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
    void IGpuDescriptorAllocator.WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw new NotSupportedException();
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

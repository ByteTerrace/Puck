using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// A recording stand-in for every neutral GPU service the counting wrappers cover. Each member adds one to its own
/// entry in <see cref="Calls"/>, keyed <c>Interface.Member</c>, and allocates nothing once that key exists. Its
/// submitter arms a <see cref="FakeFence"/> by casting, as both backends do, so a fence that is not the fake's own
/// type fails the submit.
/// </summary>
internal sealed class FakeGpu :
    IGpuBufferInitializationRecorder,
    IGpuCommandRecorder,
    IGpuComputeCommandPoolFactory,
    IGpuComputePipelineFactory,
    IGpuComputeRecorder,
    IGpuComputeServices,
    IGpuDescriptorAllocator,
    IGpuDeviceContext,
    IGpuImageInitializationRecorder,
    IGpuPipelineFactory,
    IGpuQueueSubmitter,
    IGpuShaderModuleFactory,
    IGpuStorageBufferFactory,
    IGpuImageFactory,
    IGpuSurfaceTransferFactory {
    public Dictionary<string, int> Calls { get; } = new(comparer: StringComparer.Ordinal);
    public FakeFence? LastCreatedFence { get; private set; }
    public IGpuSubmissionFence? LastSubmittedFence { get; private set; }
    public long AdapterLuid => 0L;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;
    public IGpuComputeCommandPoolFactory CommandPoolFactory => this;
    public IGpuComputePipelineFactory ComputePipelineFactory => this;
    public IGpuComputeRecorder ComputeRecorder => this;
    public IGpuDescriptorAllocator DescriptorAllocator => this;
    public nint DeviceHandle => 1;
    public IGpuQueueSubmitter QueueSubmitter => this;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuStorageBufferFactory StorageBufferFactory => this;
    public IGpuImageFactory ImageFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

    public int Count(string key) => (Calls.TryGetValue(key: key, value: out var count) ? count : 0);
    public void Hit(string key) => CollectionsMarshal.GetValueRefOrAddDefault(dictionary: Calls, key: key, exists: out _)++;
    public void WaitIdle() => Hit(key: "IGpuDeviceContext.WaitIdle");

    void IGpuBufferInitializationRecorder.ClearStorageBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => Hit(key: "IGpuBufferInitializationRecorder.ClearStorageBuffer");
    void IGpuCommandRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuCommandRecorder.BeginCommandBuffer");
    void IGpuCommandRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) => Hit(key: "IGpuCommandRecorder.BeginDebugGroup");
    void IGpuCommandRecorder.BeginRenderPass(nint deviceHandle, nint commandBufferHandle, IGpuFramebuffer framebuffer) => Hit(key: "IGpuCommandRecorder.BeginRenderPass");
    void IGpuCommandRecorder.BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) => Hit(key: "IGpuCommandRecorder.BindDescriptorSet");
    void IGpuCommandRecorder.BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) => Hit(key: "IGpuCommandRecorder.BindGraphicsPipeline");
    void IGpuCommandRecorder.BindIndexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => Hit(key: "IGpuCommandRecorder.BindIndexBuffer");
    void IGpuCommandRecorder.BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => Hit(key: "IGpuCommandRecorder.BindVertexBuffer");
    void IGpuCommandRecorder.Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters) => Hit(key: "IGpuCommandRecorder.Draw");
    void IGpuCommandRecorder.DrawIndexed(nint deviceHandle, nint commandBufferHandle, uint indexCount) => Hit(key: "IGpuCommandRecorder.DrawIndexed");
    void IGpuCommandRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuCommandRecorder.EndCommandBuffer");
    void IGpuCommandRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuCommandRecorder.EndDebugGroup");
    void IGpuCommandRecorder.EndRenderPass(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuCommandRecorder.EndRenderPass");
    void IGpuCommandRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => Hit(key: "IGpuCommandRecorder.PushConstants");
    void IGpuCommandRecorder.SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) => Hit(key: "IGpuCommandRecorder.SetScissor");
    IGpuComputeCommandPool IGpuComputeCommandPoolFactory.Create(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, uint width, uint height) {
        Hit(key: "IGpuPipelineFactory.Create");

        return new FakePipeline();
    }
    IGpuComputePipeline IGpuComputePipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        Hit(key: "IGpuComputePipelineFactory.Create");

        return new FakePipeline();
    }
    void IGpuComputeRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuComputeRecorder.BeginCommandBuffer");
    void IGpuComputeRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) => Hit(key: "IGpuComputeRecorder.BeginDebugGroup");
    void IGpuComputeRecorder.BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) => Hit(key: "IGpuComputeRecorder.BindComputeDescriptorSet");
    void IGpuComputeRecorder.BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) => Hit(key: "IGpuComputeRecorder.BindComputePipeline");
    void IGpuComputeRecorder.Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => Hit(key: "IGpuComputeRecorder.Dispatch");
    void IGpuComputeRecorder.DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => Hit(key: "IGpuComputeRecorder.DispatchIndirect");
    void IGpuComputeRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuComputeRecorder.EndCommandBuffer");
    void IGpuComputeRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) => Hit(key: "IGpuComputeRecorder.EndDebugGroup");
    void IGpuComputeRecorder.MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => Hit(key: "IGpuComputeRecorder.MemoryBarrier");
    void IGpuComputeRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => Hit(key: "IGpuComputeRecorder.PushConstants");
    void IGpuComputeRecorder.TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => Hit(key: "IGpuComputeRecorder.TransitionBuffer");
    void IGpuComputeRecorder.TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => Hit(key: "IGpuComputeRecorder.TransitionImageLayout");
    nint IGpuDescriptorAllocator.AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
        Hit(key: "IGpuDescriptorAllocator.AllocateSet");

        return 7;
    }
    nint IGpuDescriptorAllocator.CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) {
        Hit(key: "IGpuDescriptorAllocator.CreatePool");

        return 8;
    }
    nint IGpuDescriptorAllocator.CreateSampler(nint deviceHandle, GpuSamplerFilter filter) {
        Hit(key: "IGpuDescriptorAllocator.CreateSampler");

        return 9;
    }
    void IGpuDescriptorAllocator.DestroyPool(nint deviceHandle, nint poolHandle) => Hit(key: "IGpuDescriptorAllocator.DestroyPool");
    void IGpuDescriptorAllocator.DestroySampler(nint deviceHandle, nint samplerHandle) => Hit(key: "IGpuDescriptorAllocator.DestroySampler");
    void IGpuDescriptorAllocator.WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => Hit(key: "IGpuDescriptorAllocator.WriteCombinedImageSampler");
    void IGpuDescriptorAllocator.WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable) => Hit(key: "IGpuDescriptorAllocator.WriteRawBuffer");
    void IGpuDescriptorAllocator.WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => Hit(key: "IGpuDescriptorAllocator.WriteStorageBuffer");
    void IGpuDescriptorAllocator.WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => Hit(key: "IGpuDescriptorAllocator.WriteStorageBufferReadOnly");
    void IGpuDescriptorAllocator.WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => Hit(key: "IGpuDescriptorAllocator.WriteStorageBufferReadWrite");
    void IGpuDescriptorAllocator.WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => Hit(key: "IGpuDescriptorAllocator.WriteStorageImage");
    void IGpuImageInitializationRecorder.ClearStorageImage(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => Hit(key: "IGpuImageInitializationRecorder.ClearStorageImage");
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence(IGpuDeviceContext deviceContext) {
        Hit(key: "IGpuQueueSubmitter.CreateSubmissionFence");
        LastCreatedFence = new FakeFence(gpu: this);

        return LastCreatedFence;
    }
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => Hit(key: "IGpuQueueSubmitter.Submit");
    void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        Hit(key: "IGpuQueueSubmitter.Submit(fence)");
        LastSubmittedFence = fence;
        ((FakeFence)fence).Arm();
    }
    void IGpuQueueSubmitter.SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => Hit(key: "IGpuQueueSubmitter.SubmitAndWait");
    IGpuShaderModule IGpuShaderModuleFactory.Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        Hit(key: "IGpuShaderModuleFactory.Create");

        return new FakeModule();
    }
    IGpuStorageBuffer IGpuStorageBufferFactory.Create(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        Hit(key: "IGpuStorageBufferFactory.Create");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocal(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        Hit(key: "IGpuStorageBufferFactory.CreateDeviceLocal");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuBuffer IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        Hit(key: "IGpuStorageBufferFactory.CreateDeviceLocalIndirectArgs");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuStorageBuffer IGpuStorageBufferFactory.CreateIndirectArgs(IGpuDeviceContext deviceContext, ulong sizeBytes) {
        Hit(key: "IGpuStorageBufferFactory.CreateIndirectArgs");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuImage IGpuImageFactory.Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        Hit(key: "IGpuImageFactory.Create");

        return new FakeImage();
    }
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload(IGpuDeviceContext deviceContext) => throw new NotSupportedException();

    /// <summary>A fence the test signals by hand; it reads signaled when nothing is armed, as the backends' do.</summary>
    internal sealed class FakeFence(FakeGpu gpu) : IGpuSubmissionFence {
        public bool Armed { get; private set; }
        public bool Completed { get; set; }
        public bool IsSignaled {
            get {
                gpu.Hit(key: "IGpuSubmissionFence.IsSignaled");

                return (!Armed || Completed);
            }
        }

        public void Arm() {
            if (Armed) {
                throw new InvalidOperationException(message: "A submission is already outstanding on this fence.");
            }

            Armed = true;
            Completed = false;
        }
        public void Dispose() => gpu.Hit(key: "IGpuSubmissionFence.Dispose");
        public void Wait() {
            gpu.Hit(key: "IGpuSubmissionFence.Wait");
            Armed = false;
            Completed = false;
        }
    }

    private sealed class FakeBuffer(FakeGpu gpu, ulong sizeBytes) : IGpuStorageBuffer {
        public nint BufferHandle => 3;
        public ulong SizeBytes => sizeBytes;

        public void Dispose() => gpu.Hit(key: "IGpuBuffer.Dispose");
        public void Write<T>(ReadOnlySpan<T> data) where T : unmanaged => gpu.Hit(key: "IGpuStorageBuffer.Write");
        public void Write<T>(ReadOnlySpan<T> data, ulong destinationOffsetBytes) where T : unmanaged => gpu.Hit(key: "IGpuStorageBuffer.Write(offset)");
    }
    private sealed class FakeImage : IGpuImage {
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public uint Height => 1;
        public nint ImageHandle => 4;
        public nint ImageViewHandle => 5;
        public GpuImageUsage Usage => GpuImageUsage.Storage;
        public uint Width => 1;

        public void Dispose() { }
    }
    private sealed class FakeModule : IGpuShaderModule {
        public nint Handle => 6;

        public void Dispose() { }
    }
    private sealed class FakePipeline : IGpuComputePipeline, IGpuPipeline {
        public nint DescriptorSetLayoutHandle => 10;
        public nint Handle => 11;
        public nint LayoutHandle => 12;

        public void Dispose() { }
    }
}

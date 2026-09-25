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
    IGpuRecorder,
    IGpuCommandPoolFactory,
    IGpuPipelineFactory,
    IGpuComputeServices,
    IGpuBindings,
    IGpuDeviceContext,
    IGpuQueueSubmitter,
    IGpuShaderModuleFactory,
    IGpuBufferFactory,
    IGpuImageFactory,
    IGpuSurfaceTransferFactory {
    public Dictionary<string, int> Calls { get; } = new(comparer: StringComparer.Ordinal);
    public FakeFence? LastCreatedFence { get; private set; }
    public IGpuSubmissionFence? LastSubmittedFence { get; private set; }
    public long AdapterLuid => 0L;
    public GpuDeviceIdentity? Identity => null;
    public GpuMemoryProfile MemoryProfile => default;
    public IGpuCommandPoolFactory CommandPoolFactory => this;
    public IGpuPipelineFactory PipelineFactory => this;
    public IGpuRecorder Recorder => this;
    public IGpuBindings Bindings => this;
    public nint DeviceHandle => 1;
    public IGpuQueueSubmitter QueueSubmitter => this;
    public IGpuShaderModuleFactory ShaderModuleFactory => this;
    public IGpuBufferFactory BufferFactory => this;
    public IGpuImageFactory ImageFactory => this;
    public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

    public int Count(string key) => (Calls.TryGetValue(key: key, value: out var count) ? count : 0);
    public void Hit(string key) => CollectionsMarshal.GetValueRefOrAddDefault(dictionary: Calls, key: key, exists: out _)++;
    public void WaitIdle() => Hit(key: "IGpuDeviceContext.WaitIdle");

    void IGpuRecorder.BeginCommandBuffer(nint commandBufferHandle) => Hit(key: "IGpuRecorder.BeginCommandBuffer");
    void IGpuRecorder.EndCommandBuffer(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndCommandBuffer");
    void IGpuRecorder.BeginDebugGroup(nint commandBufferHandle, string label) => Hit(key: "IGpuRecorder.BeginDebugGroup");
    void IGpuRecorder.EndDebugGroup(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndDebugGroup");
    void IGpuRecorder.BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area) => Hit(key: "IGpuRecorder.BeginRenderPass");
    void IGpuRecorder.EndRenderPass(nint commandBufferHandle) => Hit(key: "IGpuRecorder.EndRenderPass");
    void IGpuRecorder.BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => Hit(key: "IGpuRecorder.BindPipeline");
    void IGpuRecorder.BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) => Hit(key: "IGpuRecorder.BindDescriptorSet");
    void IGpuRecorder.PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => Hit(key: "IGpuRecorder.PushConstants");
    void IGpuRecorder.BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => Hit(key: "IGpuRecorder.BindVertexBuffer");
    void IGpuRecorder.BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => Hit(key: "IGpuRecorder.BindIndexBuffer");
    void IGpuRecorder.SetScissor(nint commandBufferHandle, GpuPixelRect rect) => Hit(key: "IGpuRecorder.SetScissor");
    void IGpuRecorder.Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => Hit(key: "IGpuRecorder.Draw");
    void IGpuRecorder.DrawIndexed(nint commandBufferHandle, uint indexCount) => Hit(key: "IGpuRecorder.DrawIndexed");
    void IGpuRecorder.Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => Hit(key: "IGpuRecorder.Dispatch");
    void IGpuRecorder.DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => Hit(key: "IGpuRecorder.DispatchIndirect");
    void IGpuRecorder.ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => Hit(key: "IGpuRecorder.ClearStorageImage");
    void IGpuRecorder.ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => Hit(key: "IGpuRecorder.ClearStorageBuffer");
    void IGpuRecorder.TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.TransitionImageLayout");
    void IGpuRecorder.MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.MemoryBarrier");
    void IGpuRecorder.TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => Hit(key: "IGpuRecorder.TransitionBuffer");
    IGpuCommandPool IGpuCommandPoolFactory.Create() => throw new NotSupportedException();
    IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) {
        Hit(key: "IGpuPipelineFactory.Create(graphics)");

        return new FakePipeline();
    }
    IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
        Hit(key: "IGpuPipelineFactory.Create(compute)");

        return new FakePipeline();
    }
    nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) {
        Hit(key: "IGpuBindings.AllocateSet");

        return 7;
    }
    nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) {
        Hit(key: "IGpuBindings.CreatePool");

        return 8;
    }
    nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) {
        Hit(key: "IGpuBindings.CreateSampler");

        return 9;
    }
    void IGpuBindings.DestroyPool(nint poolHandle) => Hit(key: "IGpuBindings.DestroyPool");
    void IGpuBindings.DestroySampler(nint samplerHandle) => Hit(key: "IGpuBindings.DestroySampler");
    void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => Hit(key: "IGpuBindings.WriteCombinedImageSampler");
    void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBufferAccess access, uint elementStride) => Hit(key: "IGpuBindings.WriteBuffer");
    void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => Hit(key: "IGpuBindings.WriteStorageImage");
    IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() {
        Hit(key: "IGpuQueueSubmitter.CreateSubmissionFence");
        LastCreatedFence = new FakeFence(gpu: this);

        return LastCreatedFence;
    }
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) => Hit(key: "IGpuQueueSubmitter.Submit");
    void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) {
        Hit(key: "IGpuQueueSubmitter.Submit(fence)");
        LastSubmittedFence = fence;
        ((FakeFence)fence).Arm();
    }
    void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => Hit(key: "IGpuQueueSubmitter.SubmitAndWait");
    IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
        Hit(key: "IGpuShaderModuleFactory.Create");

        return new FakeModule();
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateHostVisible(data)");

        return new FakeBuffer(gpu: this, sizeBytes: ((ulong)data.Length));
    }
    IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) {
        Hit(key: "IGpuBufferFactory.CreateDeviceLocal");

        return new FakeBuffer(gpu: this, sizeBytes: sizeBytes);
    }
    IGpuImage IGpuImageFactory.Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        Hit(key: "IGpuImageFactory.Create");

        return new FakeImage();
    }
    IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
    IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => throw new NotSupportedException();
    IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => throw new NotSupportedException();

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

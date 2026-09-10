using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Shaders.Study;

namespace Puck.Shaders.Tests.Study;

// StudyPassNode's own resource creation runs through IGpuComputeServices' backend-neutral GPU interfaces, and no
// test project in this repository drives a real Vulkan or Direct3D 12 device, so these tests fake every GPU call.
// They prove the protocol StudyPassNode runs against those interfaces — resource build order, the fence-gated
// Swap/Resize discipline, the layout transition, push-constant contents, and the capture path — not that a real
// device paints the expected pixels; the windowed game is that proof.
public sealed class StudyPassNodeTests {
    private const uint Height = 4;
    private const uint Width = 8;

    [Fact]
    public void FirstProducedFrameDispatchesWithoutEverCallingSwap() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        var surface = node.ProduceFrame(default);

        Assert.Contains("Dispatch:1x1", harness.Calls);
        Assert.Single(harness.Pipelines);
        Assert.True(surface.IsSameDeviceImage);
        Assert.Equal(expected: Width, actual: surface.Width);
        Assert.Equal(expected: Height, actual: surface.Height);
        Assert.Equal(expected: SurfaceFormat.R8G8B8A8Unorm, actual: surface.Format);
        Assert.Equal(expected: harness.Images[0].ImageViewHandle, actual: surface.ImageViewHandle);
    }

    [Fact]
    public void TheImageIsTransitionedFromUndefinedOnItsFirstFrameAndFromGeneralAfterwards() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        _ = node.ProduceFrame(default);
        _ = node.ProduceFrame(default);

        Assert.Equal(expected: [(GpuImageLayout.Undefined, GpuImageLayout.General), (GpuImageLayout.General, GpuImageLayout.General)], actual: harness.Transitions);
    }

    [Fact]
    public void SwapBeforeTheFirstProducedFrameIsPickedUpByTheFirstPipelineBuild() {
        var harness = new Harness();
        using var node = harness.CreateNode();
        var program = new byte[] { 9, 9, 9, 9 };

        node.Swap(spirv: program, dxil: ReadOnlyMemory<byte>.Empty);
        Assert.Empty(harness.Pipelines); // Lazy: no GPU resource touched before the first ProduceFrame.

        _ = node.ProduceFrame(default);

        Assert.Single(harness.Pipelines);
        Assert.Equal(expected: program, actual: harness.ShaderModules[^1].Bytecode.ToArray());
    }

    [Fact]
    public void SwapAfterResourcesAreBuiltInstallsANewPipelineAndDisposesTheOldOneOnlyAfterTheFenceWait() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        _ = node.ProduceFrame(default);
        var firstPipeline = harness.Pipelines[0];
        var waitCountBeforeSwap = harness.Fence!.WaitCount;
        var program = new byte[] { 1, 2, 3, 4 };

        node.Swap(spirv: program, dxil: ReadOnlyMemory<byte>.Empty);

        Assert.Equal(expected: 2, actual: harness.Pipelines.Count);
        Assert.True(condition: firstPipeline.Disposed);
        Assert.True(condition: (harness.Fence!.WaitCount > waitCountBeforeSwap));
        Assert.Equal(expected: program, actual: harness.ShaderModules[^1].Bytecode.ToArray());
        Assert.Single(harness.AllocatedSets); // The one-storage-image set survives a swap.

        _ = node.ProduceFrame(default);

        Assert.Equal(expected: harness.Pipelines[1].Handle, actual: harness.LastBoundPipelineHandle);
    }

    [Fact]
    public void SwapIgnoresEmptyBytecodeForTheActiveBackendAndKeepsTheLastGoodPipeline() {
        var harness = new Harness();
        using var node = harness.CreateNode(hostsOnDirectX: false);

        _ = node.ProduceFrame(default);
        var pipelineCountBefore = harness.Pipelines.Count;

        // Empty SPIR-V on a Vulkan-hosted node is a failed compile: the last good (here, placeholder) pipeline
        // must keep rendering, and no new GPU resource is created for it.
        node.Swap(spirv: ReadOnlyMemory<byte>.Empty, dxil: new byte[] { 7, 7, 7, 7 });

        Assert.Equal(expected: pipelineCountBefore, actual: harness.Pipelines.Count);

        _ = node.ProduceFrame(default);

        Assert.Contains("Dispatch:1x1", harness.Calls);
    }

    [Fact]
    public void ResizeBeforeTheFirstProducedFrameNeedsNoRebuildAndTheFirstBuildUsesTheNewSize() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        node.Resize(width: 16, height: 12);
        Assert.Empty(harness.ImageRequests);

        var surface = node.ProduceFrame(default);

        Assert.Equal(expected: ((16u, 12u)), actual: harness.ImageRequests[0]);
        Assert.Equal(expected: 16u, actual: surface.Width);
        Assert.Equal(expected: 12u, actual: surface.Height);
        Assert.Contains("Dispatch:2x2", harness.Calls);
    }

    [Fact]
    public void ResizeAfterResourcesAreBuiltWaitsForTheFenceReplacesTheImageRewritesTheDescriptorAndKeepsThePipeline() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        _ = node.ProduceFrame(default);
        var firstImage = harness.Images[0];
        var waitCountBeforeResize = harness.Fence!.WaitCount;
        var descriptorWritesBefore = harness.DescriptorWrites.Count;

        node.Resize(width: 16, height: 12);

        Assert.True(condition: firstImage.Disposed);
        Assert.True(condition: (harness.Fence!.WaitCount > waitCountBeforeResize));
        Assert.Equal(expected: ((16u, 12u)), actual: harness.ImageRequests[1]);
        Assert.Single(harness.Pipelines);
        Assert.Equal(expected: (descriptorWritesBefore + 1), actual: harness.DescriptorWrites.Count);
        Assert.Equal(expected: harness.Images[1].ImageViewHandle, actual: harness.DescriptorWrites[^1]);

        // The replacement image starts undefined again.
        _ = node.ProduceFrame(default);
        Assert.Equal(expected: (GpuImageLayout.Undefined, GpuImageLayout.General), actual: harness.Transitions[^1]);

        // A same-size Resize is a no-op: no further image is built.
        node.Resize(width: 16, height: 12);
        Assert.Equal(expected: 2, actual: harness.ImageRequests.Count);
    }

    [Fact]
    public void PushConstantsCarryTheHostsInputAndTheResolutionAndTheFrameCounterAdvancesEachFrame() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        node.Input = new StudyFrameInput(
            Seconds: 3.5,
            DeltaSeconds: 0.02,
            Mouse: new System.Numerics.Vector4(1f, 2f, -3f, -4f),
            Date: new System.Numerics.Vector4(2026f, 9f, 10f, 5000f),
            CameraPos: new System.Numerics.Vector3(1f, 2f, 3f),
            CameraTarget: new System.Numerics.Vector3(4f, 5f, 6f),
            CameraUp: new System.Numerics.Vector3(0f, 1f, 0f),
            CameraFov: 0.9f
        );

        _ = node.ProduceFrame(default);
        var first = MemoryMarshal.Read<StudyPushConstants>(harness.LastPushConstants);

        Assert.Equal(expected: new System.Numerics.Vector3(Width, Height, 1f), actual: first.IResolution);
        Assert.Equal(expected: 3.5f, actual: first.ITime);
        Assert.Equal(expected: 0.02f, actual: first.ITimeDelta);
        Assert.Equal(expected: 0, actual: first.IFrame);
        Assert.Equal(expected: node.Input.Mouse, actual: first.IMouse);
        Assert.Equal(expected: node.Input.CameraFov, actual: first.ICameraFov);

        _ = node.ProduceFrame(default);
        var second = MemoryMarshal.Read<StudyPushConstants>(harness.LastPushConstants);

        Assert.Equal(expected: 1, actual: second.IFrame);
    }

    [Fact]
    public void ThePipelineDescriptionDeclaresOneStorageImageAndAComputePushConstantBlock() {
        var harness = new Harness();
        using var node = harness.CreateNode();

        _ = node.ProduceFrame(default);
        var description = harness.PipelineDescriptions[0];

        var binding = Assert.Single(description.Bindings);

        Assert.Equal(expected: 0u, actual: binding.Binding);
        Assert.Equal(expected: GpuComputeBindingKind.StorageImage, actual: binding.Kind);
        Assert.Equal(expected: ((uint)StudyPushConstants.SizeBytes), actual: description.PushConstantBinding!.Size);
        Assert.Equal(expected: GpuShaderStage.Compute, actual: description.PushConstantBinding.StageFlags);
    }

    [Fact]
    public void RequestCaptureWritesAPngFileOnTheNextProducedFrame() {
        var harness = new Harness();
        using var node = harness.CreateNode();
        var path = Path.Combine(Path.GetTempPath(), $"puck-study-pass-node-test-{Guid.NewGuid():n}.png");

        try {
            var request = new FrameCaptureRequest(path);

            node.RequestCapture(request);
            Assert.Equal(expected: path, actual: node.PendingCapturePath);

            _ = node.ProduceFrame(default);

            Assert.Null(node.PendingCapturePath);
            Assert.True(condition: File.Exists(path));
            Assert.Equal(expected: GpuImageLayout.General, actual: harness.LastReadbackLayout);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposalFailsAnUnservedCaptureRequestAndRefusesFurtherRequests() {
        var harness = new Harness();
        var node = harness.CreateNode();
        var request = new FrameCaptureRequest("never-served.png");

        node.RequestCapture(request);
        Assert.Equal(expected: "never-served.png", actual: node.PendingCapturePath);
        Assert.Throws<InvalidOperationException>(() => node.RequestCapture(new FrameCaptureRequest("second.png")));

        node.Dispose();

        Assert.IsType<ObjectDisposedException>((await request.Completion).Error);
        Assert.Throws<ObjectDisposedException>(() => node.RequestCapture(new FrameCaptureRequest("after-dispose.png")));
    }

    [Fact]
    public void DisposalReleasesEveryResourceAfterTheFenceWaitAndALaterProduceFrameIsEmpty() {
        var harness = new Harness();
        var node = harness.CreateNode();

        _ = node.ProduceFrame(default);

        node.Dispose();

        Assert.True(condition: harness.Images[0].Disposed);
        Assert.True(condition: harness.Pipelines[0].Disposed);
        Assert.Equal(expected: 1, actual: harness.DestroyedPools);
        Assert.True(condition: node.ProduceFrame(default).IsEmpty);
    }

    private sealed class FakeFence : IGpuSubmissionFence {
        public int WaitCount { get; private set; }
        public void Dispose() { }
        public void Wait() => WaitCount++;
    }
    private sealed class FakePipeline(nint handle) : IGpuComputePipeline {
        public nint DescriptorSetLayoutHandle => (100 + handle);
        public bool Disposed { get; private set; }
        public nint Handle => handle;
        public nint LayoutHandle => (200 + handle);
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeReadback(Harness harness) : IGpuSurfaceReadback {
        public bool IsReadComplete() => true;
        public ReadOnlyMemory<byte> MapPixels() => default;
        public ReadOnlyMemory<byte> Read(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) {
            harness.LastReadbackLayout = sourceLayout;

            return new byte[checked((int)(width * height * bytesPerPixel))];
        }
        public void SubmitRead(IGpuDeviceContext deviceContext, nint sourceImageHandle, GpuPixelFormat format, uint width, uint height, uint bytesPerPixel, GpuImageLayout sourceLayout) { }
        public void Dispose() { }
    }
    private sealed class FakeStorageImage(uint width, uint height, nint id) : IGpuStorageImage {
        public bool Disposed { get; private set; }
        public uint Height => height;
        public nint ImageHandle => (3000 + id);
        public nint ImageViewHandle => (4000 + id);
        public uint Width => width;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeShaderModule(nint handle, ReadOnlyMemory<byte> bytecode) : IGpuShaderModule {
        public ReadOnlyMemory<byte> Bytecode => bytecode;
        public bool Disposed { get; private set; }
        public nint Handle => handle;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeCommandPool : IGpuComputeCommandPool {
        public nint CommandBufferHandle => 1000;
        public void Dispose() { }
    }

    // Every GPU call StudyPassNode can make, faked and instrumented — no real device, no real dispatch.
    private sealed class Harness :
        IGpuComputeServices,
        IGpuComputeCommandPoolFactory,
        IGpuComputePipelineFactory,
        IGpuComputeRecorder,
        IGpuDescriptorAllocator,
        IGpuDeviceContext,
        IGpuQueueSubmitter,
        IGpuShaderModuleFactory,
        IGpuStorageImageFactory,
        IGpuSurfaceTransferFactory {
        private int m_imageIdSeed;
        private int m_pipelineHandleSeed;
        private int m_poolHandleSeed;
        private int m_setHandleSeed;
        private int m_shaderHandleSeed;

        public List<nint> AllocatedSets { get; } = [];
        public List<string> Calls { get; } = [];
        public List<nint> DescriptorWrites { get; } = [];
        public int DestroyedPools { get; private set; }
        public FakeFence? Fence { get; private set; }
        public List<(uint Width, uint Height)> ImageRequests { get; } = [];
        public List<FakeStorageImage> Images { get; } = [];
        public nint LastBoundPipelineHandle { get; private set; }
        public byte[] LastPushConstants { get; private set; } = [];
        public GpuImageLayout? LastReadbackLayout { get; set; }
        public List<GpuComputePipelineDescription> PipelineDescriptions { get; } = [];
        public List<FakePipeline> Pipelines { get; } = [];
        public List<FakeShaderModule> ShaderModules { get; } = [];
        public List<(GpuImageLayout Old, GpuImageLayout New)> Transitions { get; } = [];

        public IGpuComputeCommandPoolFactory CommandPoolFactory => this;
        public IGpuComputePipelineFactory ComputePipelineFactory => this;
        public IGpuComputeRecorder ComputeRecorder => this;
        public IGpuDescriptorAllocator DescriptorAllocator => this;
        public IGpuQueueSubmitter QueueSubmitter => this;
        public IGpuShaderModuleFactory ShaderModuleFactory => this;
        public IGpuStorageBufferFactory StorageBufferFactory => throw new NotSupportedException("StudyPassNode allocates no buffers.");
        public IGpuStorageImageFactory StorageImageFactory => this;
        public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

        long IGpuDeviceContext.AdapterLuid => 1;
        nint IGpuDeviceContext.DeviceHandle => 42;

        public StudyPassNode CreateNode(string name = "test-study", uint width = Width, uint height = Height, bool hostsOnDirectX = false) =>
            new(
                name: name,
                gpu: this,
                deviceContext: this,
                hostsOnDirectX: hostsOnDirectX,
                width: width,
                height: height
            );

        nint IGpuDescriptorAllocator.AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
            var set = ((nint)(++m_setHandleSeed));

            AllocatedSets.Add(item: set);

            return set;
        }
        void IGpuComputeRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Calls.Add(item: "BeginCommandBuffer");
        void IGpuComputeRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) => Calls.Add(item: "BeginDebugGroup");
        void IGpuComputeRecorder.BindComputeDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) => Calls.Add(item: $"BindComputeDescriptorSet:{descriptorSetHandle}");
        void IGpuComputeRecorder.BindComputePipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) {
            Calls.Add(item: "BindComputePipeline");
            LastBoundPipelineHandle = pipelineHandle;
        }
        IGpuComputeCommandPool IGpuComputeCommandPoolFactory.Create(IGpuDeviceContext deviceContext) => new FakeCommandPool();
        IGpuComputePipeline IGpuComputePipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) {
            PipelineDescriptions.Add(item: description);

            var pipeline = new FakePipeline(handle: (++m_pipelineHandleSeed));

            Pipelines.Add(item: pipeline);

            return pipeline;
        }
        IGpuStorageImage IGpuStorageImageFactory.Create(IGpuDeviceContext deviceContext, GpuPixelFormat format, uint width, uint height) {
            Assert.Equal(expected: GpuPixelFormat.R8G8B8A8Unorm, actual: format);
            ImageRequests.Add((width, height));

            var image = new FakeStorageImage(width: width, height: height, id: (++m_imageIdSeed));

            Images.Add(item: image);

            return image;
        }
        IGpuShaderModule IGpuShaderModuleFactory.Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) {
            Assert.Equal(expected: GpuShaderStage.Compute, actual: stage);

            var module = new FakeShaderModule(handle: (++m_shaderHandleSeed), bytecode: bytecode);

            ShaderModules.Add(item: module);

            return module;
        }
        IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        nint IGpuDescriptorAllocator.CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) {
            Assert.Equal(expected: 1u, actual: sizes.StorageImageCount);

            return (++m_poolHandleSeed);
        }
        IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback(IGpuDeviceContext deviceContext) => new FakeReadback(harness: this);
        nint IGpuDescriptorAllocator.CreateSampler(nint deviceHandle, GpuSamplerFilter filter) => throw new NotSupportedException();
        IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence(IGpuDeviceContext deviceContext) => (Fence ??= new FakeFence());
        IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.DestroyPool(nint deviceHandle, nint poolHandle) => DestroyedPools++;
        void IGpuDescriptorAllocator.DestroySampler(nint deviceHandle, nint samplerHandle) => throw new NotSupportedException();
        void IGpuComputeRecorder.Dispatch(nint deviceHandle, nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => Calls.Add(item: $"Dispatch:{groupCountX}x{groupCountY}");
        void IGpuComputeRecorder.DispatchIndirect(nint deviceHandle, nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => throw new NotSupportedException();
        void IGpuComputeRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) => Calls.Add(item: "EndCommandBuffer");
        void IGpuComputeRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) => Calls.Add(item: "EndDebugGroup");
        void IGpuComputeRecorder.MemoryBarrier(nint deviceHandle, nint commandBufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
        void IGpuComputeRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) {
            Assert.Equal(expected: GpuShaderStage.Compute, actual: stageFlags);
            Assert.Equal(expected: 0u, actual: offset);

            LastPushConstants = data.ToArray();

            Calls.Add(item: "PushConstants");
        }
        void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => Calls.Add(item: "Submit");
        void IGpuQueueSubmitter.SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuComputeRecorder.TransitionBuffer(nint deviceHandle, nint commandBufferHandle, nint bufferHandle, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => throw new NotSupportedException();
        void IGpuComputeRecorder.TransitionImageLayout(nint deviceHandle, nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuComputeAccess sourceAccessMask, GpuComputeAccess destinationAccessMask, GpuComputeStage sourceStageMask, GpuComputeStage destinationStageMask) => Transitions.Add((oldLayout, newLayout));
        void IGpuDeviceContext.WaitIdle() { }
        void IGpuDescriptorAllocator.WriteAccelerationStructure(nint deviceHandle, nint descriptorSetHandle, uint binding, nint accelerationStructureReference) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
            Assert.Equal(expected: 0u, actual: binding);
            Assert.Equal(expected: 0u, actual: arrayElement);
            DescriptorWrites.Add(item: imageViewHandle);
        }
    }
}

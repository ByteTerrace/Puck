using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class FullscreenPassNodeConfigTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Sdf",
            "sdf-film-grain.puck.shader.json"
        );

    private static FullscreenPassNode CreateNode(IRenderNode? inner = null) {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var config = manifest.BindConfig(config: null);
        var services = new UnusedGpuServices();

        return new FullscreenPassNode(
            inner: (inner ?? new StubRenderNode()),
            manifest: manifest,
            config: config,
            services: services,
            hostsOnDirectX: false,
            width: 64,
            height: 64
        );
    }

    [Fact]
    public async Task CaptureIsNotReplacedAndDisposalCompletesAnUnservedRequest() {
        var node = CreateNode();
        var first = new FrameCaptureRequest(path: "first.png");

        node.RequestCapture(request: first);
        Assert.Equal(
            "first.png",
            node.PendingCapturePath
        );
        Assert.Throws<InvalidOperationException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "second.png")));
        Assert.False(condition: first.Completion.IsCompleted);
        node.Dispose();
        Assert.IsType<ObjectDisposedException>(@object: (await first.Completion).Error);
        Assert.Throws<ObjectDisposedException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "closed.png")));
    }
    [Fact]
    public async Task PassThroughForwardsTheSameRequestAndReportsInnerShutdown() {
        var inner = new CaptureStub();
        var node = CreateNode(inner: inner);
        var request = new FrameCaptureRequest(path: "forwarded.png");

        node.RequestCapture(request: request);
        _ = node.ProduceFrame(context: default);
        Assert.Same(
            request,
            inner.Pending
        );
        Assert.Equal(
            request.Path,
            node.PendingCapturePath
        );
        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(testCode: () => node.RequestCapture(request: new FrameCaptureRequest(path: "busy.png")));
        node.Dispose();
        Assert.IsType<ObjectDisposedException>(@object: (await request.Completion).Error);
    }
    [Fact]
    public void TrySetConfig_refuses_a_uint_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "seed",
            value: 1f
        ));
        Assert.Equal(
            expected: 0u,
            actual: node.Config["seed"].ComponentBits(index: 0)
        );
    }
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void TrySetConfig_refuses_a_value_the_field_schema_would_refuse_at_bind_time(float value) {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "intensity",
            value: value
        ));
        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_refuses_an_unknown_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(
            field: "no-such-field",
            value: 1f
        ));
        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_rewrites_a_field_in_place_after_its_first_write() {
        using var node = CreateNode();

        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.25f
        ));

        var live = node.Config;
        var liveValue = live["intensity"];

        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.75f
        ));
        Assert.Same(
            expected: live,
            actual: node.Config
        );
        Assert.Same(
            expected: liveValue,
            actual: node.Config["intensity"]
        );
        Assert.Equal(
            expected: 0.75f,
            actual: BitConverter.UInt32BitsToSingle(value: liveValue.ComponentBits(index: 0))
        );
    }
    [Fact]
    public void TrySetConfig_round_trips_through_config_and_its_json() {
        using var node = CreateNode();

        Assert.Equal(
            expected: 0.05f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );
        Assert.True(condition: node.TrySetConfig(
            field: "intensity",
            value: 0.42f
        ));
        Assert.Equal(
            expected: 0.42f,
            actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0))
        );

        var json = node.Config.ToJson();

        Assert.Equal(
            expected: 0.42f,
            actual: json.GetProperty(propertyName: "intensity").GetSingle()
        );
        Assert.Equal(
            expected: 24u,
            actual: json.GetProperty(propertyName: "flickerHz").GetUInt32()
        );
    }

    private sealed class CaptureStub : IRenderNode, ICaptureRequestTarget {
        public NodeDescriptor Descriptor { get; } = new(
            Name: "capture-stub",
            SurfaceId: SurfaceId.New()
        );
        public FrameCaptureRequest? Pending { get; private set; }
        public string? PendingCapturePath => Pending?.Path;

        public void Dispose() => Pending?.TryFail(error: new ObjectDisposedException(objectName: nameof(CaptureStub)));
        public Surface ProduceFrame(in FrameContext context) => default;
        public void RequestCapture(FrameCaptureRequest request) => Pending = request;
    }
    // A render node this test never drives past construction — FullscreenPassNode's constructor reads the
    // manifest and its config, but calls ProduceFrame on nothing.
    private sealed class StubRenderNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new NodeDescriptor(
            Name: "stub",
            SurfaceId: SurfaceId.New()
        );

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) => throw new NotSupportedException();
    }
    // Every GPU factory FullscreenPassNode's constructor stores but never calls before ProduceFrame — this test
    // never reaches ProduceFrame, so every member here is unreachable and exists only to satisfy the interfaces.
    private sealed class UnusedGpuServices :
        IFullscreenPassServices,
        IGpuRecorder,
        IGpuBindings,
        IGpuDeviceContext,
        IGpuPipelineFactory,
        IGpuQueueSubmitter,
        IGpuShaderModuleFactory,
        IGpuSurfaceTransferFactory,
        IGpuBufferFactory,
        IGpuRenderPassFactory {
        long IGpuDeviceContext.AdapterLuid => throw new NotSupportedException();
        nint IGpuDeviceContext.DeviceHandle => throw new NotSupportedException();
        GpuDeviceIdentity? IGpuDeviceContext.Identity => null;
        GpuDeviceCapabilities? IGpuDeviceContext.Capabilities => null;
        GpuMemoryProfile IGpuDeviceContext.MemoryProfile => default;

        public IGpuBindings Bindings => this;
        public IGpuBufferFactory BufferFactory => this;
        public IGpuDeviceContext DeviceContext => this;
        public IGpuPipelineFactory PipelineFactory => this;
        public IGpuQueueSubmitter QueueSubmitter => this;
        public IGpuRecorder Recorder => this;
        public IGpuRenderPassFactory RenderPassFactory => this;
        public IGpuShaderModuleFactory ShaderModuleFactory => this;
        public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;

        nint IGpuBindings.AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => throw new NotSupportedException();
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
        IGpuPipeline IGpuPipelineFactory.Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => throw new NotSupportedException();
        IGpuShaderModule IGpuShaderModuleFactory.Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw new NotSupportedException();
        IGpuComputePipeline IGpuPipelineFactory.Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) => throw new NotSupportedException();
        IGpuBuffer IGpuBufferFactory.CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => throw new NotSupportedException();
        IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => throw new NotSupportedException();
        IGpuStorageBuffer IGpuBufferFactory.CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => throw new NotSupportedException();
        IGpuRenderPass IGpuRenderPassFactory.Create(GpuRenderPassDescription description) => throw new NotSupportedException();
        IGpuFramebuffer IGpuRenderPassFactory.CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => throw new NotSupportedException();
        IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport() => throw new NotSupportedException();
        nint IGpuBindings.CreatePool(in GpuDescriptorPoolSizes sizes) => throw new NotSupportedException();
        IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback() => throw new NotSupportedException();
        nint IGpuBindings.CreateSampler(GpuSamplerFilter filter) => throw new NotSupportedException();
        IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence() => throw new NotSupportedException();
        IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload() => throw new NotSupportedException();
        void IGpuBindings.DestroyPool(nint poolHandle) {
            if (0 != poolHandle) {
                throw new NotSupportedException();
            }
        }
        void IGpuBindings.DestroySampler(nint samplerHandle) => throw new NotSupportedException();
        void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuQueueSubmitter.Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw new NotSupportedException();
        void IGpuQueueSubmitter.SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuDeviceContext.WaitIdle() => throw new NotSupportedException();
        void IGpuBindings.WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
        void IGpuBindings.WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => throw new NotSupportedException();
        void IGpuBindings.WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw new NotSupportedException();
    }
}

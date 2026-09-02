using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed class FullscreenPassNodeConfigTests {
    private static string FilmGrainManifestPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Sdf", "sdf-film-grain.puck.shader.json");

    [Fact]
    public void TrySetConfig_round_trips_through_config_and_the_push_constant_bytes() {
        using var node = CreateNode();

        Assert.Equal(expected: 0.05f, actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0)));
        Assert.True(condition: node.TrySetConfig(field: "intensity", value: 0.42f));
        Assert.Equal(expected: 0.42f, actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0)));
    }
    [Fact]
    public void TrySetConfig_refuses_an_unknown_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(field: "no-such-field", value: 1f));
        Assert.Equal(expected: 0.05f, actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0)));
    }
    [Fact]
    public void TrySetConfig_refuses_a_uint_field() {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(field: "seed", value: 1f));
        Assert.Equal(expected: 0u, actual: node.Config["seed"].ComponentBits(index: 0));
    }
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [Theory]
    public void TrySetConfig_refuses_a_value_the_field_schema_would_refuse_at_bind_time(float value) {
        using var node = CreateNode();

        Assert.False(condition: node.TrySetConfig(field: "intensity", value: value));
        Assert.Equal(expected: 0.05f, actual: BitConverter.UInt32BitsToSingle(value: node.Config["intensity"].ComponentBits(index: 0)));
    }
    [Fact]
    public void TrySetConfig_rewrites_a_field_in_place_after_its_first_write() {
        using var node = CreateNode();

        Assert.True(condition: node.TrySetConfig(field: "intensity", value: 0.25f));

        var live = node.Config;
        var liveValue = live["intensity"];

        Assert.True(condition: node.TrySetConfig(field: "intensity", value: 0.75f));
        Assert.Same(expected: live, actual: node.Config);
        Assert.Same(expected: liveValue, actual: node.Config["intensity"]);
        Assert.Equal(expected: 0.75f, actual: BitConverter.UInt32BitsToSingle(value: liveValue.ComponentBits(index: 0)));
    }

    private static FullscreenPassNode CreateNode() {
        var manifest = ShaderSetManifest.Load(manifestPath: FilmGrainManifestPath);
        var config = manifest.BindConfig(config: null);
        var services = new UnusedGpuServices();

        return new FullscreenPassNode(inner: new StubRenderNode(), manifest: manifest, config: config, services: services, hostsOnDirectX: false, width: 64, height: 64);
    }

    // A render node this test never drives past construction — FullscreenPassNode's constructor reads the
    // manifest and fills its static push constants from `config`, but calls ProduceFrame on nothing.
    private sealed class StubRenderNode : IRenderNode {
        public NodeDescriptor Descriptor { get; } = new NodeDescriptor(Name: "stub", SurfaceId: SurfaceId.New());

        public void Dispose() { }
        public Surface ProduceFrame(in FrameContext context) => throw new NotSupportedException();
    }
    // Every GPU factory FullscreenPassNode's constructor stores but never calls before ProduceFrame — this test
    // never reaches ProduceFrame, so every member here is unreachable and exists only to satisfy the interfaces.
    private sealed class UnusedGpuServices :
        IFullscreenPassServices,
        IGpuCommandRecorder,
        IGpuDescriptorAllocator,
        IGpuDeviceContext,
        IGpuPipelineFactory,
        IGpuQueueSubmitter,
        IGpuShaderModuleFactory,
        IGpuSurfaceTransferFactory,
        IGpuVertexBufferFactory {
        public IGpuCommandRecorder CommandRecorder => this;
        public Func<uint, uint, IGpuRenderTarget> CreateRenderTarget => static (_, _) => throw new NotSupportedException();
        public IGpuDescriptorAllocator DescriptorAllocator => this;
        public IGpuDeviceContext DeviceContext => this;
        public IGpuPipelineFactory PipelineFactory => this;
        public IGpuQueueSubmitter QueueSubmitter => this;
        public IGpuShaderModuleFactory ShaderModuleFactory => this;
        public IGpuSurfaceTransferFactory SurfaceTransferFactory => this;
        public IGpuVertexBufferFactory VertexBufferFactory => this;

        long IGpuDeviceContext.AdapterLuid => throw new NotSupportedException();
        nint IGpuDeviceContext.DeviceHandle => throw new NotSupportedException();

        nint IGpuDescriptorAllocator.AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.BeginCommandBuffer(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.BeginDebugGroup(nint deviceHandle, nint commandBufferHandle, string label) => throw new NotSupportedException();
        void IGpuCommandRecorder.BeginRenderPass(nint deviceHandle, nint commandBufferHandle, nint renderPassHandle, nint framebufferHandle, uint width, uint height) => throw new NotSupportedException();
        void IGpuCommandRecorder.BindDescriptorSet(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, nint descriptorSetHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.BindGraphicsPipeline(nint deviceHandle, nint commandBufferHandle, nint pipelineHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.BindVertexBuffer(nint deviceHandle, nint commandBufferHandle, nint vertexBufferHandle) => throw new NotSupportedException();
        IGpuPipeline IGpuPipelineFactory.Create(IGpuDeviceContext deviceContext, IGpuRenderTarget renderTarget, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description, uint width, uint height) => throw new NotSupportedException();
        IGpuShaderModule IGpuShaderModuleFactory.Create(IGpuDeviceContext deviceContext, GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw new NotSupportedException();
        IGpuVertexBuffer IGpuVertexBufferFactory.Create(IGpuDeviceContext deviceContext, byte[] vertexData, uint strideBytes) => throw new NotSupportedException();
        IGpuSurfaceImport IGpuSurfaceTransferFactory.CreateImport(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        nint IGpuDescriptorAllocator.CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) => throw new NotSupportedException();
        IGpuSurfaceReadback IGpuSurfaceTransferFactory.CreateReadback(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        nint IGpuDescriptorAllocator.CreateSampler(nint deviceHandle, GpuSamplerFilter filter) => throw new NotSupportedException();
        IGpuSubmissionFence IGpuQueueSubmitter.CreateSubmissionFence(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        IGpuSurfaceUpload IGpuSurfaceTransferFactory.CreateUpload(IGpuDeviceContext deviceContext) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.DestroyPool(nint deviceHandle, nint poolHandle) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.DestroySampler(nint deviceHandle, nint samplerHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.Draw(nint deviceHandle, nint commandBufferHandle, in GpuDrawParameters parameters) => throw new NotSupportedException();
        void IGpuCommandRecorder.EndCommandBuffer(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.EndDebugGroup(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.EndRenderPass(nint deviceHandle, nint commandBufferHandle) => throw new NotSupportedException();
        void IGpuCommandRecorder.PushConstants(nint deviceHandle, nint commandBufferHandle, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => throw new NotSupportedException();
        void IGpuCommandRecorder.SetScissor(nint deviceHandle, nint commandBufferHandle, int x, int y, uint width, uint height) => throw new NotSupportedException();
        void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuQueueSubmitter.Submit(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw new NotSupportedException();
        void IGpuQueueSubmitter.SubmitAndWait(IGpuDeviceContext deviceContext, ReadOnlySpan<nint> commandBufferHandles) => throw new NotSupportedException();
        void IGpuDeviceContext.WaitIdle() => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteAccelerationStructure(nint deviceHandle, nint descriptorSetHandle, uint binding, nint accelerationStructureReference) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) => throw new NotSupportedException();
        void IGpuDescriptorAllocator.WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw new NotSupportedException();
    }
}

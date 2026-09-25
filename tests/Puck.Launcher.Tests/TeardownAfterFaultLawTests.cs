using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Puck.Abstractions.Gpu;
using Puck.Overlays;
using Puck.Text;
using Xunit;

namespace Puck.Launcher.Tests;

/// <summary>
/// A pump that faulted keeps its fault through teardown, and teardown releases only what was acquired. The windowed
/// case these laws stand for is a boot whose device never came up: the presenter's activation raises
/// <see cref="GpuDeviceUnavailableException"/>, and then the render root, whose GPU consumers never allocated anything,
/// is disposed against a renderer that was never initialized. No GPU is involved; see <see cref="WindowedHostFixture"/>.
/// </summary>
public sealed class TeardownAfterFaultLawTests {
    private const string Unsupported = "[world.host: unsupported: vulkan device unavailable: no Vulkan physical devices were reported for the current instance.]";

    private static GpuDeviceUnavailableException NoDevice() => new(
        backend: "vulkan",
        reason: "no Vulkan physical devices were reported for the current instance."
    );
    private static async Task<(int Exit, string Error)> RunAsync(IHost host) {
        using var error = new StringWriter();
        var exit = await LauncherHostRun.RunAsync(
            error: error,
            host: host,
            label: "world"
        ).WaitAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            timeout: WindowedHostFixture.HostBudget
        );

        return (exit, error.ToString().TrimEnd());
    }
    // The shipped overlay decorator over a producer that renders nothing, on services that must never be called: a
    // node that never produced a frame acquired nothing, so its teardown reaches none of them.
    private static UnifiedOverlayNode Overlay(IGpuDeviceContext device, UnusedGpu unused) {
        var glyphs = OverlayGlyphSdfPack.TryCreate(new FontAtlas(
            FontAtlasKind.Mtsdf,
            "test://fixed-grid",
            32,
            8,
            4,
            2,
            default,
            [new(
                '!',
                1,
                null,
                new(
                    Bottom: 2,
                    Left: 0,
                    Right: 2,
                    Top: 0
                )
            )],
            [],
            new FontAtlasImageData(
                Enumerable.Repeat(
                    count: 32,
                    element: ((byte)127)
                ).ToArray(),
                2,
                4
            )
        ))!;

        return new UnifiedOverlayNode(
            capacity: new OverlayCapacity(
                BindingBarMaxBanks: 1,
                BindingBarMaxModifiers: 1,
                BindingBarMaxSlotsPerBank: 1,
                HudElementsPerPanel: 1,
                HudElementsPerSeatPanel: 1,
                HudPanels: 1,
                HudSeatPanelsPerSeat: 1,
                MarkerMaxChipsPerSeat: 1,
                Seats: 1,
                WheelMaxRings: 1,
                WheelMaxSectorsPerRing: 1
            ),
            fragmentBytecode: new byte[] { 1 },
            glyphs: glyphs,
            height: 32U,
            inner: new WindowedHostFixture.FakeRenderNode(),
            deviceContext: device,
            frameSources: unused,
            sources: new UnifiedOverlaySources(
                BindingBar: null,
                Console: null,
                FeedTick: null,
                Toast: null
            ),
            vertexBytecode: new byte[] { 1 },
            width: 32U
        );
    }

    [Fact]
    public async Task ADeviceThatNeverCameUpSurfacesUnmaskedThroughTheRealOverlayTeardown() {
        var unused = new UnusedGpu();
        var device = new WindowedHostFixture.NeverInitializedDeviceContext(services: unused.Services);
        var presenter = new WindowedHostFixture.FakePresenter(failure: NoDevice());
        var host = WindowedHostFixture.Build(
            device: device,
            presenter: presenter,
            root: Overlay(
                device: device,
                unused: unused
            )
        );

        var (exit, error) = await RunAsync(host: host);

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
        Assert.Equal(
            expected: 0,
            actual: unused.Reaches
        );
        Assert.Equal(
            expected: 1,
            actual: presenter.DisposeCalls
        );
    }
    [Fact]
    public async Task ATeardownStepThatThrowsAfterTheFaultNeitherReplacesItNorSkipsTheSteps() {
        var root = new WindowedHostFixture.FakeRenderNode(failure: new InvalidOperationException(message: "The renderer must be initialized before its device is used."));
        var presenter = new WindowedHostFixture.FakePresenter(failure: NoDevice());
        var host = WindowedHostFixture.Build(
            device: new WindowedHostFixture.NeverInitializedDeviceContext(),
            presenter: presenter,
            root: root
        );

        var (exit, error) = await RunAsync(host: host);

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
        Assert.Equal(
            expected: 1,
            actual: root.DisposeCalls
        );
        Assert.Equal(
            expected: 1,
            actual: presenter.DisposeCalls
        );
    }
    [Fact]
    public void AnOverlayThatNeverProducedReleasesNothingAndNeverAsksForTheDevice() {
        var unused = new UnusedGpu();
        var overlay = Overlay(
            device: new WindowedHostFixture.NeverInitializedDeviceContext(services: unused.Services),
            unused: unused
        );

        overlay.Dispose();
        overlay.OnDeviceLost();

        Assert.Equal(
            expected: 0,
            actual: unused.Reaches
        );
    }
    [Fact]
    public void TeardownRunsEveryStepAndKeepsTheFirstFailure() {
        var fault = new InvalidOperationException(message: "the pump's fault");
        var ran = new List<string>();

        void Fails(string step) {
            ran.Add(item: step);

            throw new InvalidOperationException(message: $"{step} teardown failure");
        }

        LauncherHostRun.RunTeardown(
            fault,
            NullLogger(),
            ("first", () => Fails(step: "first")),
            ("second", () => ran.Add(item: "second"))
        );

        Assert.Equal(
            actual: ran,
            expected: ["first", "second"]
        );

        ran.Clear();

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => LauncherHostRun.RunTeardown(
            null,
            NullLogger(),
            ("first", () => Fails(step: "first")),
            ("second", () => Fails(step: "second"))
        ));

        Assert.Equal(
            expected: "first teardown failure",
            actual: thrown.Message
        );
        Assert.Equal(
            actual: ran,
            expected: ["first", "second"]
        );
    }
    [Fact]
    public async Task AHostedServiceWhoseConstructionFindsNoDeviceExitsTwo() {
        var builder = Host.CreateApplicationBuilder(settings: new HostApplicationBuilderSettings {
            DisableDefaults = true,
        });

        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<NoDeviceService>(implementationFactory: static _ => throw NoDevice());

        var (exit, error) = await RunAsync(host: builder.Build());

        Assert.Equal(
            actual: error,
            expected: Unsupported
        );
        Assert.Equal(
            actual: exit,
            expected: 2
        );
    }

    private static ILogger NullLogger() => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    private sealed class NoDeviceService : BackgroundService {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
    // Every GPU seam the overlay holds; reaching any of them from a node that never produced a frame is a failure, and
    // is counted, because a teardown that keeps the pump's fault could otherwise swallow the throw.
    private sealed class UnusedGpu : IGpuRecorder, IGpuBindings, IOverlayFrameSources, IGpuPipelineFactory,
        IGpuQueueSubmitter, IGpuShaderModuleFactory, IGpuBufferFactory, IGpuSurfaceTransferFactory, IGpuImageFactory,
        IGpuRenderPassFactory, IGpuCommandPoolFactory {
        public UnusedGpu() =>
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

        public int Reaches { get; private set; }
        public GpuDeviceServices Services { get; }

        private InvalidOperationException Reached() {
            Reaches++;

            return new(message: "A GPU seam was reached during teardown.");
        }

        public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) => throw Reached();
        public void BeginCommandBuffer(nint commandBufferHandle) => throw Reached();
        public void EndCommandBuffer(nint commandBufferHandle) => throw Reached();
        public void BeginDebugGroup(nint commandBufferHandle, string label) => throw Reached();
        public void EndDebugGroup(nint commandBufferHandle) => throw Reached();
        public void BeginRenderPass(nint commandBufferHandle, IGpuFramebuffer framebuffer, GpuPixelRect? area = null) => throw Reached();
        public void EndRenderPass(nint commandBufferHandle) => throw Reached();
        public void BindPipeline(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineHandle) => throw Reached();
        public void BindDescriptorSet(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, nint descriptorSetHandle) => throw Reached();
        public void PushConstants(nint commandBufferHandle, GpuBindPoint bindPoint, nint pipelineLayoutHandle, GpuShaderStage stageFlags, uint offset, ReadOnlySpan<byte> data) => throw Reached();
        public void BindVertexBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes, uint strideBytes) => throw Reached();
        public void BindIndexBuffer(nint commandBufferHandle, nint bufferHandle, ulong offsetBytes, ulong sizeBytes, GpuIndexFormat format) => throw Reached();
        public void SetScissor(nint commandBufferHandle, GpuPixelRect rect) => throw Reached();
        public void Draw(nint commandBufferHandle, in GpuDrawParameters parameters) => throw Reached();
        public void DrawIndexed(nint commandBufferHandle, uint indexCount) => throw Reached();
        public void Dispatch(nint commandBufferHandle, uint groupCountX, uint groupCountY, uint groupCountZ) => throw Reached();
        public void DispatchIndirect(nint commandBufferHandle, nint argumentBufferHandle, ulong argumentBufferOffset) => throw Reached();
        public void ClearStorageImage(nint commandBufferHandle, nint imageHandle, GpuPixelFormat format) => throw Reached();
        public void ClearStorageBuffer(nint commandBufferHandle, nint bufferHandle, ulong sizeBytes) => throw Reached();
        public void TransitionImageLayout(nint commandBufferHandle, nint imageHandle, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reached();
        public void MemoryBarrier(nint commandBufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reached();
        public void TransitionBuffer(nint commandBufferHandle, nint bufferHandle, GpuAccess sourceAccessMask, GpuAccess destinationAccessMask, GpuStage sourceStageMask, GpuStage destinationStageMask) => throw Reached();
        public IGpuPipeline Create(IGpuRenderPass renderPass, IGpuShaderModule vertexShaderModule, IGpuShaderModule fragmentShaderModule, GpuGraphicsPipelineDescription description) => throw Reached();
        public IGpuComputePipeline Create(IGpuShaderModule computeShaderModule, GpuComputePipelineDescription description) => throw Reached();
        public IGpuShaderModule Create(GpuShaderStage stage, ReadOnlyMemory<byte> bytecode) => throw Reached();
        public IGpuStorageBuffer CreateHostVisible(ulong sizeBytes, GpuBufferUsage usage) => throw Reached();
        public IGpuStorageBuffer CreateHostVisible(ReadOnlySpan<byte> data, GpuBufferUsage usage) => throw Reached();
        public IGpuImage Create(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) => throw Reached();
        public IGpuRenderPass Create(GpuRenderPassDescription description) => throw Reached();
        public IGpuCommandPool Create() => throw Reached();
        public IGpuFramebuffer CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) => throw Reached();
        public IGpuBuffer CreateDeviceLocal(ulong sizeBytes, GpuBufferUsage usage) => throw Reached();
        public IGpuSurfaceImport CreateImport() => throw Reached();
        public nint CreatePool(in GpuDescriptorPoolSizes sizes) => throw Reached();
        public IGpuSurfaceReadback CreateReadback() => throw Reached();
        public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) => throw Reached();
        public IGpuSubmissionFence CreateSubmissionFence() => throw Reached();
        public IGpuSurfaceUpload CreateUpload() => throw Reached();
        public void DestroyPool(nint poolHandle) {
            if (0 != poolHandle) {
                throw Reached();
            }
        }
        public void DestroySampler(nint samplerHandle) => throw Reached();
        public void Submit(ReadOnlySpan<nint> commandBufferHandles) => throw Reached();
        public void Submit(ReadOnlySpan<nint> commandBufferHandles, IGpuSubmissionFence fence) => throw Reached();
        public void SubmitAndWait(ReadOnlySpan<nint> commandBufferHandles) => throw Reached();
        public bool TryAcquire(int key, out Puck.Hosting.GpuImageLease lease) => throw Reached();
        public void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) => throw Reached();
        public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride) => throw Reached();
        public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) => throw Reached();
    }
}

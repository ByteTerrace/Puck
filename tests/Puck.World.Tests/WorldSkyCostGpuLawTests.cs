using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Assets;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class WorldSkyCostGpuLawTests {
    [Fact]
    public void VulkanSkyCostPixelsLoseTheirEvaluationWhenTheGradientIsMuted() {
        using var device = HeadlessVulkanDevice.Create(nameof(WorldSkyCostGpuLawTests));
        Verify(device, ".spv");
    }
    [Fact]
    public void DirectXSkyCostPixelsLoseTheirEvaluationWhenTheGradientIsMuted() {
        using var device = DirectXTestDevices.Hardware();
        Verify(device, ".dxil");
    }
    private static void Verify(IGpuDeviceContext device, string extension) {
        using var files = new TemporaryDirectory();
        var source = new Source();
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache), new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        using var view = new SdfTestView(new SdfWorldResidency(pipelines, source, pipelines.LoadDeployed(bytecodeExtension: extension), SdfTestView.Instance, 32, 32, brickPoolVoxelCapacity: 0), pipelines, device, 32);
        view.Residency.DebugMode = DebugViewModes.SkyCost;
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);
        TestLiveness.Until(step: () => view.Produce(in context), reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        byte[] Capture(string name) {
            var request = new FrameCaptureRequest(files.PathOf(name));
            view.CaptureTarget.RequestCapture(request);
            TestLiveness.Until(step: () => {
                _ = view.Produce(in context);
                device.WaitIdle();
                return request.Completion.IsCompleted;
            }, reason: () => view.NotReadyReason);
            Assert.Null(request.Completion.Result.Error);
            return PngDecoder.Decode(File.ReadAllBytes(request.Path)).RgbaPixels;
        }
        var active = Capture("p18-12-active.png");
        source.Sky.StopCount = 0;
        var muted = Capture("p18-12-muted.png");
        for (var pixel = 0; pixel < active.Length; pixel += 4) {
            Assert.True(active[pixel] > 0 && muted[pixel] == 0, "The sky-cost red channel must lose the muted gradient's evaluation.");
            Assert.Equal(0, active[pixel + 1]);
            Assert.Equal(0, muted[pixel + 1]);
            Assert.True(active[pixel + 2] > 0 && muted[pixel + 2] > 0, "Both pixels read the retained field-run textures.");
        }
    }
    private sealed class Source : ISdfFrameSource {
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();
        public SdfSky Sky { get; } = new();
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(
            Program: m_program, ProgramChanged: false, Time: 0f, Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(new Vector3(0, 0, -5), Vector3.Zero, 1f, width, height),
                Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0))]) { Sky = Sky, Lights = SdfLights.Default() };
    }
}

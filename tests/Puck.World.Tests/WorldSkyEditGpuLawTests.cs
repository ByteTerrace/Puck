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
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class WorldSkyEditGpuLawTests {
    [Fact]
    public void VulkanReloadedSkyChangesTheHeldImageAndSavesToSource() {
        using var device = HeadlessVulkanDevice.Create(nameof(WorldSkyEditGpuLawTests));

        Verify(device, ".spv");
    }
    [Fact]
    public void DirectXReloadedSkyChangesTheHeldImageAndSavesToSource() {
        using var device = DirectXTestDevices.Hardware();

        Verify(device, ".dxil");
    }

    private static void Verify(IGpuDeviceContext device, string extension) {
        using var files = new TemporaryDirectory();
        using var session = new WorldSkyEditLawTests.Session();
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache), new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        using var view = new SdfTestView(new SdfWorldResidency(pipelines, new Source(session), pipelines.LoadDeployed(bytecodeExtension: extension), SdfTestView.Instance, 32, 32, brickPoolVoxelCapacity: 0), pipelines, device, 32);
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);

        TestLiveness.Until(step: () => view.Produce(in context), reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        PngImage Capture(string name) {
            var request = new FrameCaptureRequest(files.PathOf(name));

            view.CaptureTarget.RequestCapture(request);
            TestLiveness.Until(step: () => {
                _ = view.Produce(in context);
                device.WaitIdle();
                return request.Completion.IsCompleted;
            }, reason: () => view.NotReadyReason);
            Assert.Null(request.Completion.Result.Error);
            return PngDecoder.Decode(File.ReadAllBytes(request.Path));
        }
        var seat = new WorldSeatView(Present: true, Region: new NormalizedRect(0, 0, 1, 1), Camera: default, Width: 32, Height: 32);

        session.Comparison.Hold(0, Capture("p18-12-before.png"), seat, session.Server.CompletedEngineTicks);
        session.EditAndReload();
        var difference = session.Comparison.Measure(0, Capture("p18-12-after.png"), seat);

        Assert.True((difference.ChangedPixels > 0), "Reloading the blue sky must change the held red sky image.");
        session.SaveAndAssert();
    }

    private sealed class Source(WorldSkyEditLawTests.Session session) : ISdfFrameSource {
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var environment = session.Resolve();

            return new SdfFrame(Program: m_program, ProgramChanged: false, Time: 0f, Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(new Vector3(0, 0, -5), Vector3.Zero, 1f, width, height),
                Region: new NormalizedRect(0, 0, 1, 1))]) { Lights = environment.Lights, Sky = environment.Sky };
        }
    }
}

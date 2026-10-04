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

        Verify(device: device, extension: ".spv");
    }
    [Fact]
    public void DirectXReloadedSkyChangesTheHeldImageAndSavesToSource() {
        using var device = DirectXTestDevices.Hardware();

        Verify(device: device, extension: ".dxil");
    }

    private static void Verify(IGpuDeviceContext device, string extension) {
        using var files = new TemporaryDirectory();
        using var session = new WorldSkyEditLawTests.Session();
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache), new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        using var view = new SdfTestView(new SdfWorldResidency(pipelines, new Source(session: session), pipelines.LoadDeployed(bytecodeExtension: extension), SdfTestView.Instance, 32, 32, brickPoolVoxelCapacity: 0), pipelines, device, 32);
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        PngImage Capture(string name) {
            var request = new FrameCaptureRequest(files.PathOf(name: name));

            view.CaptureTarget.RequestCapture(request: request);
            TestLiveness.Until(step: () => {
                _ = view.Produce(context: in context);
                device.WaitIdle();
                return request.Completion.IsCompleted;
            }, reason: () => view.NotReadyReason);
            Assert.Null(@object: request.Completion.Result.Error);
            return PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: request.Path));
        }
        var seat = new WorldSeatView(Present: true, Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0), Camera: default, Width: 32, Height: 32);

        session.Comparison.Hold(0, Capture(name: "p18-12-before.png"), seat, session.Server.CompletedEngineTicks);
        session.EditAndReload();
        var difference = session.Comparison.Measure(0, Capture(name: "p18-12-after.png"), seat);

        Assert.True(condition: (difference.ChangedPixels > 0), userMessage: "Reloading the blue sky must change the held red sky image.");
        session.SaveAndAssert();
    }

    private sealed class Source(WorldSkyEditLawTests.Session session) : ISdfFrameSource {
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var environment = session.Resolve();

            return new SdfFrame(Program: m_program, ProgramChanged: false, Time: 0f, Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(new Vector3(x: 0, y: 0, z: -5), Vector3.Zero, 1f, width, height),
                Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0))]) { Lights = environment.Lights, Sky = environment.Sky };
        }
    }
}

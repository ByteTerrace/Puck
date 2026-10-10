using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
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
public sealed class WorldTimelineLeverGpuLawTests {
    [Fact]
    public void HeldSkySubmitsOnlyItsFirstFrameOnVulkan() {
        using var device = HeadlessVulkanDevice.Create(nameof(WorldTimelineLeverGpuLawTests));

        Verify(device: device, extension: ".spv");
    }
    [Fact]
    public void HeldSkySubmitsOnlyItsFirstFrameOnDirectX() {
        using var device = DirectXTestDevices.Hardware();

        Verify(device: device, extension: ".dxil");
    }

    private static void Verify(IGpuDeviceContext device, string extension) {
        var definition = TimelineLeverFixtures.Definition();
        var mirror = ClientFixtures.StateMirror(definition);

        Assert.True(condition: mirror.ControlClock(name: "tide", operation: WorldTimelineOperation.At, rate: 1d, refusal: out var refusal, tick: 12600UL), userMessage: refusal);
        using var source = new Source(definition: definition, mirror: mirror);
        var cache = new GpuPassPipelineCache();
        var pipelines = new SdfWorldPipelineCatalog(new GpuRegionCopyPass(bytecodeExtension: extension, pipelines: cache), new SdfMeshRasterPass(bytecodeExtension: extension, pipelines: cache));
        using var view = new SdfTestView(new SdfWorldResidency(pipelines, source, pipelines.LoadDeployed(bytecodeExtension: extension), SdfTestView.Instance, 32, 32, brickPoolVoxelCapacity: 0), pipelines, device, 32, hostsOnDirectX: (extension == ".dxil"));
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = device }),
            StepTicks: 0, TargetHeight: 32, TargetWidth: 32);

        TestLiveness.Until(step: () => view.Produce(context: in context), reason: () => view.NotReadyReason, wait: view.Residency.WaitPipelineBuilds);
        device.WaitIdle();
        var sample = new GpuWorkSample();

        TestLiveness.Until(step: () => {
            _ = view.Produce(context: in context);
            device.WaitIdle();
            return view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample);
        }, reason: () => "the first sky submission has not completed");
        Assert.Equal(1L, sample.Submission);
        var environmentRenders = view.Residency.Tables!.SkyEnvironmentRenders;

        for (var index = 1; (index <= 12); index++) {
            mirror.Advance(tick: ((ulong)index), engineTick: (((ulong)index) * Fixtures.StepTicks));
            mirror.Apply(fraction: 1f);
            _ = view.Produce(context: in context);
            device.WaitIdle();
            Assert.True(condition: view.Runtime.Work(instance: 0).TryReadCompleted(sample: sample));
            Assert.True(condition: (sample.Submission == 1L), userMessage: "A held sky must render nothing new after its first frame.");
            Assert.Equal(environmentRenders, view.Residency.Tables.SkyEnvironmentRenders);
        }
    }

    private sealed class Source(WorldDefinition definition, WorldStateMirror mirror) : ISdfFrameSource, IDisposable {
        private readonly WorldEnvironmentResolve m_environment = new(domains: new WorldValueDomainGuard());
        private readonly SdfProgram m_program = new SdfProgramBuilder().Build();

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var environment = m_environment.Resolve(definition, 0, mirror);

            return new SdfFrame(Program: m_program, ProgramChanged: false, Time: 0f, Views: [new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(position: new Vector3(x: 0, y: 0, z: -5), target: Vector3.Zero, fieldOfViewRadians: 1f, viewportWidth: width, viewportHeight: height),
                Region: new NormalizedRect(Height: 1, Width: 1, X: 0, Y: 0))]) {
                Clock = mirror.Presented,
                EnableCadenceGate = true,
                Lights = environment.Lights,
                Sky = environment.Sky,
            };
        }
        public void Dispose() => m_environment.Dispose();
    }
}

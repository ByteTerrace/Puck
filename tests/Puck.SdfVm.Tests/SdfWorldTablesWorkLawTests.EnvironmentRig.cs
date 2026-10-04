using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    // The same residency, environment producer and dependent view that the World schedules. Sky edits are sampled by
    // the frame source, and only the graph's completed counter slots supply the environment work sample.
    private sealed class EnvironmentRig : IDisposable, ISdfFrameSource {
        private readonly SdfTestView m_view;
        private readonly FrameContext m_context;

        public EnvironmentRig(bool holdFences = false) {
            Gpu = new FakeGpuDevice(holdFences: holdFences);
            var pipelines = SdfTestPipelines.Cache();
            Frame = new SdfFrame(Program: Program(), ProgramChanged: false, Time: 0f,
                Views: [new SdfViewSnapshot(Camera: CameraSnapshot.LookAt(fieldOfViewRadians: 1f,
                    position: new Vector3(x: 0f, y: 0f, z: -5f), target: Vector3.Zero,
                    viewportHeight: Extent, viewportWidth: Extent),
                    Region: new NormalizedRect(Height: 1f, Width: 1f, X: 0f, Y: 0f))]);
            var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0, frameSource: this,
                height: Extent, width: Extent, kernels: SdfTestPipelines.Kernels(), name: SdfTestView.Instance,
                pipelines: pipelines);
            m_view = new SdfTestView(residency: residency, pipelines: pipelines, device: Gpu,
                extent: Extent, hostsOnDirectX: false);
            m_context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
                Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = Gpu }),
                StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
        }

        public SdfFrame Frame { get; }
        public FakeGpuDevice Gpu { get; }
        public SdfWorldTables Engine => m_view.Residency.Tables!;
        public ShaderPipelineRenderNode Node => m_view.Runtime.NodeOf(instance: SdfTestView.EnvironmentInstance)!;
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame with { };
        public void Dispose() => m_view.Dispose();
        public void Render() => TestLiveness.Until(step: () => m_view.Produce(context: m_context),
            reason: () => m_view.NotReadyReason, wait: m_view.Residency.WaitPipelineBuilds);
        public void AssertStanding() {
            var frame = Node.FrameCounter;
            Render();
            Assert.Equal(expected: frame, actual: Node.FrameCounter);
        }
        public void Complete() {
            foreach (var fence in Gpu.SubmittedFences) { fence.Completed = true; }
            Node.PollReadbacks();
        }
        public int Reload(SdfKernelSet kernels) {
            using var scratch = new TemporaryDirectory(prefix: "puck-environment-reload-");
            var passes = Directory.CreateDirectory(path: SdfKernelSet.PassesDirectory(tree: scratch.RootPath)).FullName;
            File.WriteAllBytes(path: Path.Combine(path1: passes, path2: $"{SdfKernelSet.StemOf(kernel: SdfKernel.Beam)}.comp.spv"),
                bytes: kernels[SdfKernel.Beam].ToArray());
            Assert.True(condition: m_view.Residency.RequestShaderReload(
                compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache")), tree: scratch.RootPath));
            TestLiveness.Until(step: () => {
                _ = m_view.Produce(context: m_context);
                return m_view.Residency.ShaderReloadStatus.State != "pending";
            }, reason: () => $"the environment reload is {m_view.Residency.ShaderReloadStatus.State}",
                wait: m_view.Residency.WaitPipelineBuilds);
            Assert.Contains(expected: m_view.Residency.ShaderReloadStatus.State, collection: new[] { "applied", "unchanged" });
            return m_view.Residency.ShaderReloadStatus.ChangedPipelines;
        }
    }
}

using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Abstractions.Counting;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the GPU work <see cref="SdfEngineNode"/> reports when a host drives it the way the windowed and offscreen
/// hosts do: one <see cref="SdfEngineNode.ProduceFrame"/> per produced frame, with the device resolved from the frame
/// context, over <see cref="FakeGpuDevice"/>. A frame's submission is fire-and-forget, so it is published by the next
/// produced frame, which observes its fence; before that, nothing is available.
/// </summary>
public sealed class SdfEngineNodeWorkLawTests {
    private const uint Extent = 64;

    [Fact]
    public void AProducedFrameIsPublishedByTheNextProducedFrame() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        Assert.False(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        rig.ProduceFirst();
        Assert.False(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        var first = sample.Submission;

        Assert.Equal(expected: SdfWorldEngine.PassLabels.ToArray(), actual: sample.PassLabels.ToArray());
        Assert.True(condition: sample.TryGetPassCount(column: IndirectDispatchesColumn, pass: CompositePass, value: out var indirect));
        Assert.Equal(actual: indirect, expected: 1L);

        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: (first + 1L), actual: sample.Submission);
    }
    [Fact]
    public void ADeviceLossWithdrawsTheSampleAndTheRebuiltEngineContinuesTheSubmissions() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        rig.ProduceFirst();
        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        var before = sample.Submission;

        rig.Node.OnDeviceLost();
        Assert.False(condition: rig.Node.IsReady);
        Assert.False(condition: rig.Node.Work.TryReadCompleted(sample: sample));

        rig.ProduceFirst();
        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));
        Assert.True(
            condition: (sample.Submission > before),
            userMessage: $"The rebuilt engine's submission {sample.Submission} did not follow {before}."
        );
    }
    [Fact]
    public void ASteadyStateProducedFrameAllocatesNothingForItsCounting() {
        using var rig = new Rig();
        var sample = new GpuWorkSample();

        void Frame() {
            rig.Produce();
            _ = rig.Node.Work.TryReadCompleted(sample: sample);
        }

        rig.ProduceFirst();

        for (var warm = 0; (warm < 4); warm++) {
            Frame();
        }

        Assert.Equal(expected: 0L, actual: AllocationWindow.Least(window: Frame));
    }

    private const int CompositePass = 9;
    private const int IndirectDispatchesColumn = 1;

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
    private sealed class Rig : IDisposable {
        private readonly FrameContext m_context;

        public Rig() {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );

            var frame = new SdfFrame(
                Program: builder.Build(),
                ProgramChanged: false,
                Time: 0f,
                Views: [new SdfViewSnapshot(
                    Camera: CameraSnapshot.LookAt(
                        fieldOfViewRadians: 1f,
                        position: new Vector3(x: 0f, y: 0f, z: -5f),
                        target: Vector3.Zero,
                        viewportHeight: Extent,
                        viewportWidth: Extent
                    ),
                    Region: new NormalizedRect(
                        Height: 1f,
                        Width: 1f,
                        X: 0f,
                        Y: 0f
                    )
                )]
            );

            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: new FixedFrameSource(frame: frame),
                height: Extent,
                kernels: SdfTestPipelines.Kernels(),
                pipelines: SdfTestPipelines.Cache(),
                width: Extent
            );
            m_context = new FrameContext(
                AccumulatorTicks: 0UL,
                DeltaTicks: 0UL,
                ElapsedTicks: 0UL,
                FrameDeltaTicks: 0UL,
                Host: new HostContext(capabilities: new Dictionary<Type, object> {
                    [typeof(IGpuDeviceContext)] = gpu,
                }),
                StepTicks: 0UL,
                TargetHeight: Extent,
                TargetWidth: Extent
            );
        }

        public SdfEngineNode Node { get; }

        public void Dispose() => Node.Dispose();
        public void Produce() => _ = Node.ProduceFrame(context: in m_context);
        public void ProduceFirst() => _ = Node.ProduceFirstFrame(context: in m_context);
    }
}

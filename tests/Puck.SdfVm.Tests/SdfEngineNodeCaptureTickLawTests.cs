using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for the tick a capture served by <see cref="SdfEngineNode"/> records: the <see cref="SdfFrame.StateTick"/> of
/// the frame that rendered view 0's output, which the frame source names, and never a tick the request supplies.
/// </summary>
public sealed class SdfEngineNodeCaptureTickLawTests {
    private const uint Extent = 64;

    /// <summary>A capture records the state tick of the frame that rendered the image it reads: a frame naming tick 5
    /// serves a capture with tick 5, a later frame naming tick 9 serves one with tick 9, and a frame naming none serves
    /// one with none.</summary>
    [Fact]
    public void ACaptureRecordsTheStateTickOfTheFrameThatRenderedItsImage() {
        using var rig = new Rig();

        rig.Source.StateTick = 5UL;
        rig.ProduceFirst();
        Assert.Equal(expected: 5UL, actual: rig.CaptureAt().Tick);

        rig.Source.StateTick = 9UL;
        Assert.Equal(expected: 9UL, actual: rig.CaptureAt().Tick);

        rig.Source.StateTick = null;
        Assert.Null(@object: rig.CaptureAt().Tick);
    }
    /// <summary>A frame the cadence gate skips leaves its retained output standing for its own state, whose inputs render
    /// that output pixel for pixel, so a capture served on it records the skipped frame's tick.</summary>
    [Fact]
    public void ACadenceSkippedFrameStandsForItsOwnStateTick() {
        using var rig = new Rig(cadenceGate: true);

        rig.Source.StateTick = 5UL;
        rig.ProduceFirst();
        rig.Produce();
        rig.Source.StateTick = 6UL;

        Assert.Equal(expected: 6UL, actual: rig.CaptureAt().Tick);

        // The next produced frame publishes the capture frame's work, whose views pass the gate skipped.
        var sample = new GpuWorkSample();

        rig.Produce();
        Assert.True(condition: rig.Node.Work.TryReadCompleted(sample: sample));
        Assert.Equal(expected: GpuPassState.Skipped, actual: sample.GetPassState(pass: ViewsPass));
    }

    // The views pass's index in SdfWorldEngine.PassLabels.
    private const int ViewsPass = 8;

    private sealed class TickingFrameSource(SdfFrame frame) : ISdfFrameSource {
        public ulong? StateTick { get; set; }

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            (frame with { StateTick = StateTick });
    }
    private sealed class Rig : IDisposable {
        private readonly FrameContext m_context;
        private readonly string m_directory = Directory.CreateTempSubdirectory(prefix: "puck-sdf-capture-tick-").FullName;

        public Rig(bool cadenceGate = false) {
            var gpu = new FakeGpuDevice(reportVersion: SdfIsa.Version);
            var builder = new SdfProgramBuilder();

            builder.Sphere(
                material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
                radius: 1f
            );

            Source = new TickingFrameSource(frame: new SdfFrame(
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
            ) { EnableCadenceGate = cadenceGate });
            Node = new SdfEngineNode(
                brickPoolVoxelCapacity: 0,
                frameSource: Source,
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
        public TickingFrameSource Source { get; }

        // Arms a capture on the node and produces the frame that serves it.
        public FrameCaptureResult CaptureAt() {
            var request = new FrameCaptureRequest(path: Path.Combine(
                path1: m_directory,
                path2: $"{Guid.NewGuid():N}.png"
            ));

            Node.RequestCapture(request: request);
            Produce();
            Assert.True(condition: request.Completion.IsCompleted);

            var result = request.Completion.Result;

            Assert.Null(@object: result.Error);

            return result;
        }
        public void Dispose() {
            Node.Dispose();
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        }
        public void Produce() => _ = Node.Produce(context: in m_context, height: Extent, width: Extent);
        public void ProduceFirst() => _ = Node.ProduceFirstFrame(context: in m_context);
    }
}

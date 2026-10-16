using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Client.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a residency waits on its one views kernel. While the driver holds that kernel's creation the
/// residency keeps producing without being ready, its reason names the kernel, and a readiness wait started then stays
/// pending; once the kernel is built the residency is ready, the wait completes and the captured program is live, even if
/// the film gate has captured nothing since. The fake driver holds the views kernel's creation until the law releases it.
/// </summary>
public sealed class SdfViewsReadinessLawTests {
    private const uint Extent = 32;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AResidencyWaitsOnItsViewsKernelWhetherOrNotItKeepsFilming(bool keepsFilming) {
        using var gate = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice() {
            BeforeComputePipeline = description => {
                if (description.Name is "sdf-world-views") {
                    gate.Wait();
                }
            },
        };
        var source = new FixedFrameSource(frame: Frame(program: Sphere()));
        var films = true;

        using var residency = new SdfWorldResidency(
            brickPoolVoxelCapacity: 0,
            film: _ => films,
            frameSource: source,
            height: Extent,
            kernels: SdfTestPipelines.Kernels(),
            name: "world",
            pipelines: SdfTestPipelines.Cache(),
            width: Extent
        );
        // Disposed before the residency, so a failing assertion releases the held build instead of leaving the
        // residency's disposal waiting on it.
        using var opener = new GateOpener(gate: gate);
        var context = new FrameContext(
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

        TestLiveness.Until(
            reason: () => residency.NotReadyReason,
            step: () => {
                _ = residency.Produce(context: in context);
                films = keepsFilming;

                return (residency.NotReadyReason?.Contains(value: "'sdf-world-views'", comparisonType: StringComparison.Ordinal) ?? false);
            }
        );
        for (var frame = 0; (frame < 4); frame++) {
            _ = residency.Produce(context: in context);
            Assert.False(condition: residency.IsReady);
        }
        Assert.Contains(actualString: residency.NotReadyReason, expectedSubstring: "'sdf-world-views'");
        using var cancellation = new CancellationTokenSource();
        var ready = residency.WaitReadyAsync(cancellationToken: cancellation.Token);

        Assert.False(condition: ready.IsCompleted);

        // Released, the views kernel builds and the captured program becomes live, including when the film gate has stayed
        // closed since the first capture.
        gate.Set();
        TestLiveness.Until(
            reason: () => residency.NotReadyReason,
            step: () => {
                _ = residency.Produce(context: in context);

                return residency.IsReady;
            },
            wait: residency.WaitPipelineBuilds
        );
        TestLiveness.Until(step: () => ready.IsCompleted);
        Assert.True(condition: ready.IsCompletedSuccessfully);
        Assert.Same(
            actual: residency.Frame!.Program,
            expected: source.Frame.Program
        );
        Assert.Equal(actual: residency.CopyLiveProgramWords(), expected: source.Frame.Program.Words.ToArray());
    }

    private static SdfProgram Sphere() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return builder.Build();
    }
    private static SdfFrame Frame(SdfProgram program) => new(
        Program: program,
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

    // Films the one frame the law gives it.
    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame Frame { get; } = frame;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
    }
    private sealed class GateOpener(ManualResetEventSlim gate) : IDisposable {
        public void Dispose() => gate.Set();
    }
}

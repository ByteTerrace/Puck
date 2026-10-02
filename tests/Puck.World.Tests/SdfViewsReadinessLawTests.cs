using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a residency waits only on the views kernel its program selects. A program that selects the core
/// variant is ready while the full ISA's and the folds' translations are still in the driver, which is how a cold floor
/// machine boots a world whose program needs neither. A captured program that selects a views kernel still building is not
/// uploaded: the residency holds the frame it last packed, which the live program's views keep rendering, and its reason
/// names the kernel; once the kernel is built the program uploads and the residency is ready again, even if the film gate
/// captures nothing during the hold. A readiness wait started during the hold completes only after it releases. The fake
/// driver holds the full and folds kernels' creations until the law releases them.
/// </summary>
public sealed class SdfViewsReadinessLawTests {
    private const uint Extent = 32;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AResidencyHoldsAndReleasesItsPendingFrameWhetherOrNotItKeepsFilming(bool keepsFilming) {
        using var gate = new ManualResetEventSlim(initialState: false);
        var gpu = new FakeGpuDevice() {
            BeforeComputePipeline = description => {
                if (description.Name is "sdf-world-views" or "sdf-world-views-folds") {
                    gate.Wait();
                }
            },
        };
        var source = new SwitchingFrameSource(frame: Frame(program: Sphere()));
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
        // Disposed before the residency, so a failing assertion releases the held builds instead of leaving the
        // residency's disposal waiting on them.
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

        // The sphere selects the core variant: the residency is ready while the full and folds kernels are held.
        TestLiveness.Until(
            reason: () => residency.NotReadyReason,
            step: () => {
                _ = residency.Produce(context: in context);

                return residency.IsReady;
            }
        );
        Assert.False(condition: gate.IsSet);
        Assert.True(condition: residency.WaitReadyAsync(cancellationToken: CancellationToken.None).IsCompletedSuccessfully);
        var packedWords = residency.CopyLiveProgramWords();

        // A trapezoid selects the full ISA, still held: the program waits, the packed sphere frame stands and renders, and
        // the reason names the kernel. Its camera also moves, so holding only the program would mismatch the packed frame.
        var pending = Frame(program: Trapezoid(), cameraZ: -8f) with { ProgramChanged = true };

        source.Frame = pending;

        for (var frame = 0; (frame < 4); frame++) {
            Assert.True(condition: residency.Produce(context: in context));
            Assert.False(condition: residency.IsReady);
            Assert.Same(
                actual: residency.Frame,
                expected: source.First
            );
            Assert.Equal(actual: residency.CopyLiveProgramWords(), expected: packedWords);
            films = keepsFilming;
        }

        Assert.Contains(
            actualString: residency.NotReadyReason,
            expectedSubstring: "holds its frame until the views kernel its program selects, 'sdf-world-views', is built"
        );
        using var cancellation = new CancellationTokenSource();
        var ready = residency.WaitReadyAsync(cancellationToken: cancellation.Token);

        Assert.False(condition: ready.IsCompleted);

        // Released, the full kernel builds, the trapezoid uploads and its captured camera becomes current, including
        // when the film gate has stayed closed since the switch.
        source.Frame = pending with { ProgramChanged = false };
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
            expected: pending.Program
        );
        Assert.Equal(actual: residency.Frame.Views, expected: pending.Views);
        Assert.Equal(actual: residency.CopyLiveProgramWords(), expected: pending.Program.Words.ToArray());
    }

    private static SdfProgram Sphere() {
        var builder = new SdfProgramBuilder();

        builder.Sphere(
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            radius: 1f
        );

        return builder.Build();
    }
    private static SdfProgram Trapezoid() {
        var builder = new SdfProgramBuilder();

        builder.Trapezoid(
            bottomHalfWidth: 1f,
            halfHeight: 0.5f,
            lift: SdfLift.Revolve,
            liftAmount: 0f,
            material: builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)),
            topHalfWidth: 0.5f
        );

        return builder.Build();
    }
    private static SdfFrame Frame(SdfProgram program, float cameraZ = -5f) => new(
        Program: program,
        ProgramChanged: false,
        Time: 0f,
        Views: [new SdfViewSnapshot(
            Camera: CameraSnapshot.LookAt(
                fieldOfViewRadians: 1f,
                position: new Vector3(x: 0f, y: 0f, z: cameraZ),
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

    // Films whatever frame the law sets, remembering the first.
    private sealed class SwitchingFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame First { get; } = frame;
        public SdfFrame Frame { get; set; } = frame;

        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) => Frame;
    }
    private sealed class GateOpener(ManualResetEventSlim gate) : IDisposable {
        public void Dispose() => gate.Set();
    }
}

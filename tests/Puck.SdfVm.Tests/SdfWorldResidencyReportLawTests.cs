using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws for what <see cref="SdfWorldResidency"/> reports and carries from its host over <see cref="FakeGpuDevice"/>: the
/// live program report <c>world.budget</c> prints is the resident program's own words, instances, step scale, its binder
/// and field-scope clamps, and the frame's bounded volumes, nothing before the first frame; and the debug view mode
/// <c>world.debug-view</c> sets reaches every pass block, whether it is set before the tables exist or after.
/// </summary>
public sealed class SdfWorldResidencyReportLawTests {
    private const uint Extent = 64;

    // Two instances: a sphere under a field scope a cell displacement clamps, and a twisted sphere whose twist binds the
    // program's step scale.
    private static SdfProgram Program() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        _ = builder.BeginInstance(
            boundCenter: Vector3.Zero,
            boundRadius: 4f
        );
        _ = builder.ResetPoint().PushField().Sphere(
            material: material,
            radius: 1f
        ).ResetPoint().CellDisplace(
            amplitude: 2f,
            frequency: 1f,
            mode: SdfCellMode.F2MinusF1,
            randomness: 0.2f,
            seed: 0
        ).PopField();
        builder.EndInstance();
        _ = builder.BeginInstance(
            boundCenter: new Vector3(x: 3f, y: 0f, z: 0f),
            boundRadius: 4f
        );
        _ = builder.ResetPoint().Translate(offset: new Vector3(x: 3f, y: 0f, z: 0f)).TwistY(rate: 2f).Sphere(
            material: material,
            radius: 1f
        );
        builder.EndInstance();

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
    ) {
        Volumes = [new SdfVolume(
            Axis: 1f,
            DynamicSlot: -1,
            Extinction: 0.5f,
            HalfExtent: Vector3.One,
            Intensity: 1f,
            Kind: SdfVolumeKind.Flow,
            Position: Vector3.Zero,
            Ramp: [new SdfDensityStop(Color: Vector3.One, Density: 0.5f)],
            Rotation: Quaternion.Identity,
            Seed: 1u,
            Speed: 1f,
            Steps: 16,
            Width: 0.5f
        )],
    };
    private static SdfWorldResidency Residency(SdfFrame frame) => new(
        brickPoolVoxelCapacity: 0,
        frameSource: new FixedFrameSource(frame: frame),
        height: Extent,
        kernels: SdfTestPipelines.Kernels(),
        name: "world",
        pipelines: SdfTestPipelines.Cache(),
        width: Extent
    );
    private static FrameContext Context() => new(
        AccumulatorTicks: 0UL,
        DeltaTicks: 0UL,
        ElapsedTicks: 0UL,
        FrameDeltaTicks: 0UL,
        Host: new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = new FakeGpuDevice(),
        }),
        StepTicks: 0UL,
        TargetHeight: Extent,
        TargetWidth: Extent
    );
    // The debug view mode a pass block of the residency's latest frame carries, as a view's pass writes it.
    private static int BlockDebugMode(SdfWorldResidency residency, SdfFrame frame) {
        var block = new byte[SdfFrameBlock.SizeBytes];

        SdfFrameBlock.Write(
            block: block,
            frame: frame,
            height: Extent,
            sceneTime: frame.Time,
            tables: residency.Tables!.PassValues,
            view: 0,
            width: Extent
        );

        return BitConverter.ToInt32(
            startIndex: ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: SdfWorldPackage.DebugMode)),
            value: block
        );
    }

    [Fact]
    public void TheLiveProgramReportIsTheResidentProgramsAndTheFramesVolumes() {
        var program = Program();
        var frame = Frame(program: program);
        using var residency = Residency(frame: frame);

        // The program exercises every clause of the report.
        Assert.True(condition: (program.Instances.Count == 2));
        Assert.NotEmpty(collection: program.FieldScopeClamps);
        Assert.NotNull(@object: program.StepScaleBinder);
        Assert.True(condition: (program.StepScale < 1f));

        Assert.Equal(expected: 0, actual: residency.LiveProgramWords);
        Assert.Empty(collection: residency.LiveProgramFieldScopeClamps);

        residency.ProduceFirstFrame(context: Context());

        Assert.Equal(
            actual: (residency.LiveProgramWords, residency.LiveProgramInstances, residency.LiveProgramStepScale, residency.LiveProgramStepScaleBinder, residency.LiveVolumes),
            expected: (program.Words.Length, program.Instances.Count, program.StepScale, program.StepScaleBinder, frame.Volumes.Count)
        );
        Assert.Equal(
            actual: residency.LiveProgramFieldScopeClamps,
            expected: program.FieldScopeClamps
        );
    }
    [Fact]
    public void TheDebugViewModeReachesEveryPassBlockWhenSetBeforeTheTablesOrAfter() {
        Assert.True(condition: DebugViewModes.TryParse(mode: out var visibility, name: "visibility"));
        Assert.True(condition: DebugViewModes.TryParse(mode: out var depth, name: "depth"));

        var frame = Frame(program: Program());
        var context = Context();
        using var residency = Residency(frame: frame);

        // Set before the tables exist, the mode reaches the first frame's blocks.
        residency.DebugMode = visibility;
        residency.ProduceFirstFrame(context: in context);
        Assert.Equal(expected: visibility, actual: BlockDebugMode(frame: frame, residency: residency));

        residency.DebugMode = depth;
        residency.ProduceFrame(context: in context);
        Assert.Equal(expected: depth, actual: BlockDebugMode(frame: frame, residency: residency));

        residency.DebugMode = 0;
        residency.ProduceFrame(context: in context);
        Assert.Equal(expected: 0, actual: BlockDebugMode(frame: frame, residency: residency));
    }

    private sealed class FixedFrameSource(SdfFrame frame) : ISdfFrameSource {
        public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) =>
            frame;
    }
}

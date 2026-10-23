using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfCompositionFrameSourceLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABackgroundRebuildHoldsTheProgramUntilItFinishesAndLockedPresentationWaitsForIt(bool tickLocked) {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };
        var first = Capture(source: source).Program;

        // The first program has nothing to hold, so it builds in its frame.
        Assert.False(condition: source.Building);
        Assert.Equal(expected: 1f, actual: first.Instances[0].Radius);

        emitter.Radius = 2f;

        var held = Capture(source: source);

        // The frame that found the content moved still carries, and holds, the program before it: a wait for the new
        // program's bake reads Building and Program, not the frame count alone.
        Assert.True(condition: source.Building);
        Assert.Same(expected: first, actual: held.Program);
        Assert.Same(expected: first, actual: source.Program);
        Assert.Equal(expected: 2L, actual: source.Frames);

        var adopted = held.Program;

        for (var attempt = 0; ((attempt < 2000) && ReferenceEquals(objA: adopted, objB: first)); attempt++) {
            Thread.Sleep(millisecondsTimeout: 1);
            adopted = Capture(source: source).Program;
        }

        Assert.False(condition: source.Building);
        Assert.Equal(expected: 2f, actual: adopted.Instances[0].Radius);
        Assert.Same(expected: adopted, actual: source.Program);

        emitter.Radius = 3f;
        _ = Capture(source: source);
        Assert.True(condition: source.Building);

        if (tickLocked) {
            emitter.TickLocked = true;
        } else {
            source.BeginConvergence(request: new FrameCaptureRequest(path: "converge.png"));
        }

        // A converging capture adopts the build in flight, and builds a program it moves to in its own frame.
        var converged = Capture(source: source).Program;

        Assert.False(condition: source.Building);
        Assert.Equal(expected: 3f, actual: converged.Instances[0].Radius);

        emitter.Radius = 4f;

        Assert.Equal(expected: 4f, actual: Capture(source: source).Program.Instances[0].Radius);
        Assert.False(condition: source.Building);
    }
    // THE LAW: the frame that adopts a background build carries the words its program differs in from the program it
    // replaced, measured by the build, and owes only the dynamic-transform rows the rebuild's repack moved; the first
    // program, built in its frame, carries no delta, and the first frame owes every row.
    [Fact]
    public void AnAdoptedBuildCarriesItsDeltaAndOwesOnlyTheRowsItsRepackMoved() {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };
        var first = Capture(source: source);

        Assert.Null(@object: first.ProgramDelta);
        Assert.Equal(expected: 2L, actual: OwedRows(source: source));

        emitter.Radius = 2f;

        var adopted = Capture(source: source);

        for (var attempt = 0; ((attempt < 2000) && ReferenceEquals(objA: adopted.Program, objB: first.Program)); attempt++) {
            Thread.Sleep(millisecondsTimeout: 1);
            adopted = Capture(source: source);
        }

        var delta = Assert.IsType<SdfProgramDelta>(@object: adopted.ProgramDelta);

        Assert.Same(expected: first.Program, actual: delta.From);
        Assert.Same(expected: adopted.Program, actual: delta.To);
        Assert.InRange(actual: delta.Runs.Count, high: Puck.Abstractions.Gpu.GpuRegion.MaxCopyRuns, low: 1);
        Assert.Equal(expected: 3L, actual: OwedRows(source: source));
        Assert.Equal(expected: 2f, actual: adopted.DynamicTransforms[1].Position.X);
    }
    [Fact]
    public void ABackgroundBuildThatThrowsThrowsWhenItIsAdoptedAndTheNextFrameEmitsAgain() {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };

        _ = Capture(source: source);
        emitter.Unbalanced = true;
        _ = Capture(source: source);
        source.BeginConvergence(request: new FrameCaptureRequest(path: "converge.png"));

        _ = Assert.Throws<InvalidOperationException>(testCode: () => Capture(source: source));
        Assert.False(condition: source.Building);

        emitter.Unbalanced = false;
        emitter.Radius = 5f;

        Assert.Equal(expected: 5f, actual: Capture(source: source).Program.Instances[0].Radius);
    }

    private static long OwedRows(SdfCompositionFrameSource source) {
        Assert.True(condition: source.MovedTransforms.TryRead(kind: SdfMovedTransforms.OwedRows, value: out var owed));

        return owed;
    }
    private static SdfFrame Capture(SdfCompositionFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 8,
        interpolationAlpha: 0f,
        width: 8
    );

    // One sphere instance of the radius the test sets, whose every change moves the emitter's revision; an unbalanced
    // emission leaves an instance open, which the build refuses. Of its two dynamic slots, a repack poses the first at the
    // origin and the second at the radius along X.
    private sealed class RadiusEmitter : ISdfSceneEmitter, ISdfFrameDresser {
        public bool TickLocked { get; set; }
        private SdfProgram? m_dressed;
        private int m_revision;

        public float Radius {
            get;
            set {
                field = value;
                m_revision++;
            }
        } = 1f;
        public bool Unbalanced {
            get;
            set {
                field = value;
                m_revision++;
            }
        }
        public int RevisionComponentCount => 1;
        public int DynamicSlotCount => 2;

        public void WriteRevision(Span<int> destination) => destination[0] = m_revision;
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: Radius);
            _ = builder.Sphere(material: material, radius: Radius);
            if (!Unbalanced) {
                _ = builder.EndInstance();
            }
        }
        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context, SdfMovedTransforms moved) {
            if (!moved.Everything) {
                return;
            }

            slots[context.SlotBase] = new DynamicTransform(Orientation: Quaternion.Identity, Position: Vector3.Zero);
            slots[(context.SlotBase + 1)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: (Vector3.UnitX * Radius));
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var changed = !ReferenceEquals(objA: program, objB: m_dressed);

            m_dressed = program;

            return new SdfFrame(Program: program, ProgramChanged: changed, Time: 0f, Views: []) { DynamicTransforms = transforms };
        }
    }
}

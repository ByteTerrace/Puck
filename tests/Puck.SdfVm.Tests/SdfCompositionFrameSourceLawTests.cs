using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfCompositionFrameSourceLawTests {
    [Fact]
    public void ABackgroundRebuildHoldsTheProgramBeforeItUntilTheBuildFinishesAndAConvergingCaptureWaitsForIt() {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };
        var first = Capture(source: source).Program;

        // The first program has nothing to hold, so it builds in its frame.
        Assert.False(condition: source.Building);
        Assert.Equal(expected: 1f, actual: first.Instances[0].Radius);

        emitter.Radius = 2f;

        var held = Capture(source: source);

        Assert.True(condition: source.Building);
        Assert.Same(expected: first, actual: held.Program);

        var adopted = held.Program;

        for (var attempt = 0; ((attempt < 2000) && ReferenceEquals(objA: adopted, objB: first)); attempt++) {
            Thread.Sleep(millisecondsTimeout: 1);
            adopted = Capture(source: source).Program;
        }

        Assert.False(condition: source.Building);
        Assert.Equal(expected: 2f, actual: adopted.Instances[0].Radius);

        emitter.Radius = 3f;
        _ = Capture(source: source);
        Assert.True(condition: source.Building);

        source.BeginConvergence(request: new FrameCaptureRequest(path: "converge.png"));

        // A converging capture adopts the build in flight, and builds a program it moves to in its own frame.
        var converged = Capture(source: source).Program;

        Assert.False(condition: source.Building);
        Assert.Equal(expected: 3f, actual: converged.Instances[0].Radius);

        emitter.Radius = 4f;

        Assert.Equal(expected: 4f, actual: Capture(source: source).Program.Instances[0].Radius);
        Assert.False(condition: source.Building);
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

    private static SdfFrame Capture(SdfCompositionFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 8,
        interpolationAlpha: 0f,
        width: 8
    );

    // One sphere instance of the radius the test sets, whose every change moves the emitter's revision; an unbalanced
    // emission leaves an instance open, which the build refuses.
    private sealed class RadiusEmitter : ISdfSceneEmitter, ISdfFrameDresser {
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

        public void WriteRevision(Span<int> destination) => destination[0] = m_revision;
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: Radius);
            _ = builder.Sphere(material: material, radius: Radius);
            if (!Unbalanced) {
                _ = builder.EndInstance();
            }
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var changed = !ReferenceEquals(objA: program, objB: m_dressed);

            m_dressed = program;

            return new SdfFrame(Program: program, ProgramChanged: changed, Time: 0f, Views: []);
        }
    }
}

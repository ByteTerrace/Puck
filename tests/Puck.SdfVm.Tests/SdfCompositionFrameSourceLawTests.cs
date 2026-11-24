using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfCompositionFrameSourceLawTests {
    // THE LAW (C0.4): a background build never blocks the frame. The first frame films nothing and names the first
    // program's build; a moved program in ordinary presentation holds the program before it and answers nothing; a
    // tick-locked or converging frame carries the held program and answers waiting with the build's reason until the
    // build lands, and the frame after it carries the program its content calls for and answers nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ABackgroundBuildNeverBlocksTheFrameAndLockedPresentationAnswersWaitingUntilItLands(bool tickLocked) {
        var emitter = new RadiusEmitter();
        using var gate = new BuildGate();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };

        source.ObserveBuilds(built: gate.Hold);

        // The first program builds off the frame: the frame films nothing and says why.
        Assert.Null(@object: Capture(source: source));
        Assert.True(condition: source.Building);
        Assert.Equal(expected: SdfCompositionFrameSource.FirstProgramWaiting, actual: source.Waiting);
        Assert.Null(@object: Capture(source: source));
        gate.Open();

        var first = Settle(source: source);

        Assert.Equal(expected: 1f, actual: first.Program.Instances[0].Radius);
        Assert.Null(@object: source.Waiting);
        gate.Close();
        emitter.Radius = 2f;

        var held = Capture(source: source)!;

        // The frame that found the content moved carries, and holds, the program before it, and answers nothing.
        Assert.True(condition: source.Building);
        Assert.Same(expected: first.Program, actual: held.Program);
        Assert.Null(@object: source.Waiting);
        gate.Open();

        var adopted = Settle(source: source, until: frame => !ReferenceEquals(objA: frame.Program, objB: first.Program));

        Assert.Equal(expected: 2f, actual: adopted.Program.Instances[0].Radius);
        gate.Close();

        if (tickLocked) {
            emitter.TickLocked = true;
        } else {
            source.BeginConvergence(request: new FrameCaptureRequest(path: "converge.png"));
        }
        emitter.Radius = 3f;

        // A locked or converging frame never waits for the build: it carries the held program with the build's reason,
        // frame after frame, until the build lands.
        for (var frame = 0; (frame < 3); frame++) {
            var waiting = Capture(source: source)!;

            Assert.Same(expected: adopted.Program, actual: waiting.Program);
            Assert.Equal(expected: SdfCompositionFrameSource.LockedWaiting, actual: source.Waiting);
        }
        gate.Open();

        var converged = Settle(source: source);

        Assert.Equal(expected: 3f, actual: converged.Program.Instances[0].Radius);
        Assert.Null(@object: source.Waiting);
        Assert.False(condition: gate.TimedOut);
    }
    // THE LAW (C0.4): a slot layout change while a build stands films nothing, names the layout's build, and never waits
    // for the standing build; the program emitted for the new layout lands on a later frame, with every slot packed.
    [Fact]
    public void ALayoutChangeWhileABuildStandsFilmsNothingUntilTheNewLayoutsProgramLands() {
        var emitter = new RadiusEmitter();
        using var gate = new BuildGate();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };

        source.ObserveBuilds(built: gate.Hold);
        gate.Open();

        var first = Settle(source: source);

        gate.Close();
        emitter.Radius = 2f;
        Assert.Same(expected: first.Program, actual: Capture(source: source)!.Program);
        Assert.True(condition: source.Building);
        emitter.Slots = 3;

        // The standing build was emitted under the old layout, so no frame can pack under the held program; the frame
        // answers at once, filming nothing, while the standing build is still held by its gate.
        Assert.Null(@object: Capture(source: source));
        Assert.Equal(expected: SdfCompositionFrameSource.LayoutWaiting, actual: source.Waiting);
        Assert.Null(@object: Capture(source: source));
        gate.Open();

        var relaid = Settle(source: source);

        Assert.Equal(expected: 3, actual: relaid.DynamicTransforms.Count);
        Assert.Equal(expected: 2f, actual: relaid.Program.Instances[0].Radius);
        Assert.Equal(expected: 2f, actual: relaid.DynamicTransforms[1].Position.X);
        Assert.Null(@object: source.Waiting);
        Assert.False(condition: gate.TimedOut);
    }
    // THE LAW: the frame that adopts a background build carries the words its program differs in from the program it
    // replaced, measured by the build, and owes only the dynamic-transform rows the rebuild's repack moved; the first
    // program, which replaced none, carries no delta, and the first frame owes every row.
    [Fact]
    public void AnAdoptedBuildCarriesItsDeltaAndOwesOnlyTheRowsItsRepackMoved() {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };
        var first = Settle(source: source);

        Assert.Null(@object: first.ProgramDelta);
        Assert.Equal(expected: 2L, actual: OwedRows(source: source));

        emitter.Radius = 2f;

        var adopted = Settle(source: source, until: frame => !ReferenceEquals(objA: frame.Program, objB: first.Program));
        var delta = Assert.IsType<SdfProgramDelta>(@object: adopted.ProgramDelta);

        Assert.True(condition: delta.LeadsFrom(program: first.Program));
        Assert.Same(expected: adopted.Program, actual: delta.To);
        Assert.InRange(actual: delta.Runs.Count, high: Puck.Abstractions.Gpu.GpuRegion.MaxCopyRuns, low: 1);
        Assert.Equal(expected: 3L, actual: OwedRows(source: source));
        Assert.Equal(expected: 2f, actual: adopted.DynamicTransforms[1].Position.X);
    }
    [Fact]
    public void ABackgroundBuildThatThrowsThrowsWhenItIsAdoptedAndTheNextFrameEmitsAgain() {
        var emitter = new RadiusEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]) { BuildsInBackground = true };

        _ = Settle(source: source);
        emitter.Unbalanced = true;
        _ = Capture(source: source);
        source.BeginConvergence(request: new FrameCaptureRequest(path: "converge.png"));

        InvalidOperationException? thrown = null;

        for (var attempt = 0; ((attempt < 2000) && (thrown is null)); attempt++) {
            try {
                _ = Capture(source: source);
                Thread.Sleep(millisecondsTimeout: 1);
            } catch (InvalidOperationException exception) {
                thrown = exception;
            }
        }

        Assert.NotNull(@object: thrown);
        Assert.False(condition: source.Building);

        emitter.Unbalanced = false;
        emitter.Radius = 5f;

        Assert.Equal(expected: 5f, actual: Settle(source: source).Program.Instances[0].Radius);
    }

    private static long OwedRows(SdfCompositionFrameSource source) {
        Assert.True(condition: source.MovedTransforms.TryRead(kind: SdfMovedTransforms.OwedRows, value: out var owed));

        return owed;
    }
    private static SdfFrame? Capture(SdfCompositionFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 8,
        interpolationAlpha: 0f,
        width: 8
    );
    // Captures until a frame carries the program its content calls for (no build stands and none is waited for) and
    // satisfies the condition, which the background builds reach within the bound.
    private static SdfFrame Settle(SdfCompositionFrameSource source, Func<SdfFrame, bool>? until = null) {
        for (var attempt = 0; (attempt < 5000); attempt++) {
            if ((Capture(source: source) is { } frame) && !source.Building && (source.Waiting is null) && (until?.Invoke(arg: frame) ?? true)) {
                return frame;
            }

            Thread.Sleep(millisecondsTimeout: 1);
        }

        throw new Xunit.Sdk.XunitException(userMessage: $"the composition did not settle: {source.Waiting}");
    }

    // Holds every background build on its own thread, inside the build, while the gate is closed: what a frame does while
    // a build stands is then decided by the test, not by how fast the pool runs it. A hold that outlives its bound is
    // recorded, which a frame that waited for the build it held would cause.
    private sealed class BuildGate : IDisposable {
        private readonly ManualResetEventSlim m_open = new(initialState: false);

        public bool TimedOut { get; private set; }

        public void Close() => m_open.Reset();
        public void Open() => m_open.Set();
        public void Hold(SdfProgram program, IReadOnlyList<DynamicTransform> transforms) {
            if (!m_open.Wait(timeout: TimeSpan.FromSeconds(value: 30))) {
                TimedOut = true;
            }
        }
        public void Dispose() {
            m_open.Set();
            m_open.Dispose();
        }
    }

    // One sphere instance of the radius the test sets, whose every change moves the emitter's revision; an unbalanced
    // emission leaves an instance open, which the build refuses. Of its dynamic slots, a repack poses the first at the
    // origin and the second at the radius along X; a change of their count moves the composition's slot layout.
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
        public int Slots { get; set; } = 2;
        public int RevisionComponentCount => 1;
        public int DynamicSlotCount => Slots;

        public void WriteRevision(Span<int> destination) => destination[0] = m_revision;
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: Radius);
            _ = builder.Sphere(material: material, radius: Radius);
            if (!Unbalanced) {
                _ = builder.EndInstance();
            }
        }
        public void PackDynamicTransforms(DynamicTransform[] slots, in SdfEmitContext context, SdfMovedTransforms moved) {
            if (!moved.Everything) {
                return;
            }

            slots[context.SlotBase] = new DynamicTransform(Orientation: Quaternion.Identity, Position: Vector3.Zero);
            slots[(context.SlotBase + 1)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: (Vector3.UnitX * Radius));
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, SdfMeshDrawList meshDraws, uint width, uint height, float deltaSeconds, float interpolationAlpha) {
            var changed = !ReferenceEquals(objA: program, objB: m_dressed);

            m_dressed = program;

            return new SdfFrame(Program: program, ProgramChanged: changed, Time: 0f, Views: []) { DynamicTransforms = transforms };
        }
    }
}

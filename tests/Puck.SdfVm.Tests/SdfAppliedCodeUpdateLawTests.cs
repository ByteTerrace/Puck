using System.Numerics;
using Puck.Abstractions;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.Tests.Shared;
using Xunit;

namespace Puck.SdfVm.Tests;

// A hot-reload update to code is an invalidation like any other input's: the composition host re-emits every emitter
// whose own revisions stood still, and the bake keys what it stores under an identity that folds in an update to the
// code it runs, and only to that code.
[Collection(name: nameof(AppliedCodeUpdateCollection))]
public sealed class SdfAppliedCodeUpdateLawTests {
    private static readonly (string Assembly, string Type, string? Method)[] FixtureRoot = [("Puck.SdfVm.Tests", "Puck.SdfVm.Tests.SdfMeshBakeClosureFixture", "Run")];

    [Fact]
    public void AnAppliedUpdateRebuildsTheProgramWhoseEmittersStoodStill() {
        var emitter = new CountingEmitter();
        var source = new SdfCompositionFrameSource(dresser: emitter, emitters: [emitter]);
        var first = Capture(source: source);

        Assert.Same(expected: first, actual: Capture(source: source));
        Assert.Equal(expected: 1, actual: emitter.Emissions);

        AppliedCodeUpdate.Apply(typeof(CountingEmitter));

        var rebuilt = Capture(source: source);

        // The emission is code the update may have changed, so the program is emitted again although no emitter moved.
        Assert.NotSame(expected: first, actual: rebuilt);
        Assert.Equal(expected: 2, actual: emitter.Emissions);
        Assert.Same(expected: rebuilt, actual: Capture(source: source));
    }
    [Fact]
    public void ABakeIdentityFoldsInAnUpdateToTheCodeItRunsAlone() {
        using var scratch = new TemporaryDirectory(prefix: "puck-applied-bake-");
        var store = new SdfMeshStore(directory: scratch.PathOf(name: "meshes"));
        var kernels = new SdfKernelSet(bytecode: SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [((byte)kernel)])).ToArray());
        var fixture = SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory, roots: FixtureRoot);
        var built = SdfMeshBakeIdentity.Of(code: fixture, kernels: kernels);

        Assert.True(condition: fixture.Reads(type: typeof(SdfMeshBakeClosureFixture)));
        Assert.True(condition: fixture.Reads(type: typeof(SdfMeshBakeClosureFixture.Doubling)));
        Assert.False(condition: fixture.Reads(type: typeof(SdfAppliedCodeUpdateLawTests)));
        _ = store.ExplainMisses(identity: built);

        // An update elsewhere leaves the identity, and every stored mesh under it, as they were.
        AppliedCodeUpdate.Apply(typeof(SdfAppliedCodeUpdateLawTests));

        var elsewhere = built.UnderRunningCode(code: fixture);

        Assert.Equal(expected: built.Value, actual: elsewhere.Value);
        Assert.Equal(expected: string.Empty, actual: elsewhere.Applied);
        Assert.Same(expected: elsewhere, actual: elsewhere.UnderRunningCode(code: fixture));

        // An update to a type the bake's code runs keys its products apart, and the miss names it.
        AppliedCodeUpdate.Apply(typeof(SdfMeshBakeClosureFixture.Doubling));

        var edited = elsewhere.UnderRunningCode(code: fixture);

        Assert.NotEqual(expected: built.Value, actual: edited.Value);
        Assert.NotEqual(expected: built.Code, actual: edited.Code);
        Assert.EndsWith(expectedEndString: $".{CodeRevision.Current}", actualString: edited.Applied);
        Assert.Equal(expected: edited.Value, actual: SdfMeshBakeIdentity.Of(code: fixture, kernels: kernels).Value);
        Assert.Equal(expected: edited.Value, actual: SdfMeshStore.BuildIdentityOf(bakeKernel: edited.BakeKernel, code: edited.Code, maskKernel: edited.MaskKernel));
        Assert.Contains(expectedSubstring: $"the code, by the hot-reload update {edited.Applied}", actualString: store.ExplainMisses(identity: edited));

        // A value alone has no code to follow.
        var alone = new SdfMeshBakeIdentity(value: 5);

        Assert.Same(expected: alone, actual: alone.UnderRunningCode(code: fixture));
        Assert.Throws<ArgumentNullException>(testCode: () => built.UnderRunningCode(code: null!));
        Assert.Throws<ArgumentNullException>(testCode: () => fixture.Reads(type: null!));
    }
    [Fact]
    public void TheBakesOwnClosureReadsWhatItRunsAndNotTheRendererBesideIt() {
        var closure = SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory);

        Assert.True(condition: closure.Reads(type: typeof(SdfMeshPathBake)));
        Assert.True(condition: closure.Reads(type: typeof(SdfProgram)));
        Assert.False(condition: closure.Reads(type: typeof(SdfCompositionFrameSource)));
        Assert.False(condition: closure.Reads(type: typeof(SdfWorldResidency)));
    }

    [Fact]
    public void BakeInputStorageUpdatesKeepTheIdentityWhilePackingUpdatesInvalidateIt() {
        var closure = SdfMeshBakeClosure.Of(directory: AppContext.BaseDirectory);
        var kernels = new SdfKernelSet(bytecode: SdfKernelSet.Kernels.Select(selector: static kernel => new ReadOnlyMemory<byte>(array: [((byte)kernel)])).ToArray());
        var built = SdfMeshBakeIdentity.Of(code: closure, kernels: kernels);

        Assert.False(condition: closure.Reads(type: typeof(SdfMeshBakeInputTransport)));
        Assert.True(condition: closure.Reads(type: typeof(SdfMeshBakeBatch)));
        AppliedCodeUpdate.Apply(typeof(SdfMeshBakeInputTransport));
        var reallocated = built.UnderRunningCode(code: closure);

        // Compare with the current identity, including any earlier process update: test ordering must not require
        // the real production closure to have an empty applied token.
        Assert.Equal(expected: built.Value, actual: reallocated.Value);
        Assert.Equal(expected: built.Code, actual: reallocated.Code);
        Assert.Equal(expected: built.Applied, actual: reallocated.Applied);
        Assert.Equal(expected: built.Value, actual: SdfMeshBakeIdentity.Of(code: closure, kernels: kernels).Value);

        AppliedCodeUpdate.Apply(typeof(SdfMeshBakeBatch));
        var repacked = reallocated.UnderRunningCode(code: closure);

        Assert.NotEqual(expected: built.Value, actual: repacked.Value);
        Assert.NotEqual(expected: built.Code, actual: repacked.Code);
        Assert.EndsWith(expectedEndString: $".{CodeRevision.Current}", actualString: repacked.Applied);
        Assert.Equal(expected: repacked.Value, actual: SdfMeshBakeIdentity.Of(code: closure, kernels: kernels).Value);
    }

    private static SdfProgram Capture(SdfCompositionFrameSource source) => source.CaptureFrame(
        deltaSeconds: 0f,
        height: 8,
        interpolationAlpha: 0f,
        width: 8
    ).Program;

    // One sphere, whose revision never moves; it counts its emissions.
    private sealed class CountingEmitter : ISdfSceneEmitter, ISdfFrameDresser {
        public int Emissions { get; private set; }
        public int RevisionComponentCount => 1;
        public int DynamicSlotCount => 0;

        public void WriteRevision(Span<int> destination) => destination[0] = 7;
        public void Emit(SdfProgramBuilder builder, in SdfEmitContext context) {
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            Emissions++;
            _ = builder.BeginInstance(boundCenter: Vector3.Zero, boundRadius: 1f);
            _ = builder.Sphere(material: material, radius: 1f);
            _ = builder.EndInstance();
        }
        public void PackDynamicTransforms(Span<DynamicTransform> slots, in SdfEmitContext context, SdfMovedTransforms moved) {
        }
        public SdfFrame Dress(SdfProgram program, DynamicTransform[] transforms, SdfMovedTransforms moved, IReadOnlyList<SdfMeshDraw> meshDraws, long meshDrawsRevision, uint width, uint height, float deltaSeconds, float interpolationAlpha) => new(Program: program, ProgramChanged: false, Time: 0f, Views: []) { DynamicTransforms = transforms };
    }
}

using System.Numerics;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Client.Sdf;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

/// <summary>Laws for the render envelope: the world scene's CPU count against what the construction probe and the static
/// stamper emit, and the oracle's registrations.</summary>
[Collection(SceneProbeCollection.Name)]
public sealed partial class WorldRenderEnvelopeLawTests {
    [Fact]
    public void AMeasurerPastAnEngineCeilingIsRefusedByName() {
        var envelope = new WorldRenderEnvelope();
        var definition = Fixtures.BuildDocument();

        using (envelope.Configure(measure: _ => new WorldRenderCost(Instances: (SdfProgramBuilder.MaxInstances + 1), Materials: 0))) {
            Assert.False(condition: envelope.TryFit(candidate: definition, reason: out var reason));
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "instances 65537 exceed the engine ceiling");
        }

        using (envelope.Configure(measure: _ => new WorldRenderCost(Instances: 0, Materials: (SdfProgramBuilder.ScreenMaterialId + 1)))) {
            Assert.False(condition: envelope.TryFit(candidate: definition, reason: out var reason));
            Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "materials 65536 exceed the engine ceiling");
        }

        using (envelope.Configure(measure: _ => new WorldRenderCost(Instances: SdfProgramBuilder.MaxInstances, Materials: SdfProgramBuilder.ScreenMaterialId))) {
            Assert.True(condition: envelope.TryFit(candidate: definition, reason: out var reason), userMessage: reason);
        }
    }
    [Fact]
    public void ADocumentTheComposedSceneRefusesLeavesThePreviousOneLoaded() {
        var emitter = new WorldSdfDocumentEmitter();
        var document = """
            {"schema":"puck.sdf.v1","materials":[{"albedo":[1,0,0]}],"ops":[{"op":"sphere","radius":1,"material":0}]}
            """u8.ToArray();

        emitter.ConfigureAdmission(refuse: _ => null);
        emitter.Load(utf8Json: document);
        var accepted = emitter.CurrentProgram;
        Span<int> before = stackalloc int[1];

        emitter.WriteRevision(destination: before);

        emitter.ConfigureAdmission(refuse: program => new WorldRenderCost(Instances: 0, Materials: (SdfProgramBuilder.ScreenMaterialId + program.Materials.Count)).Exceeds());
        var refusal = Assert.Throws<SdfDocumentException>(testCode: () => emitter.Load(utf8Json: document));

        Assert.Equal(expected: SdfRefusal.ComposedEnvelopeExceeded, actual: refusal.Reason);
        Assert.Contains("materials 65536 exceed the engine ceiling", refusal.Message, StringComparison.Ordinal);
        Assert.Same(accepted, emitter.CurrentProgram);
        Span<int> after = stackalloc int[1];

        emitter.WriteRevision(destination: after);
        Assert.Equal(before[0], after[0]);
    }
    /// <summary>The count's reservation is the construction probe's instance count: the boot placements of both
    /// emission classes, a derived-face band, and the authored headroom.</summary>
    [InlineData(0)]
    [InlineData(1000)]
    [Theory]
    public void TheCountReservesWhatTheConstructionProbeEmits(int headroom) {
        var definition = Document(headroom: headroom, placements: [Placed(id: "a", prototype: "plaque"), Placed(id: "b", prototype: "store"), Placed(id: "c", prototype: "seam")]);
        var (_, scene) = SceneEmitter(definition: definition);
        var envelope = new WorldSceneEnvelope(boot: definition);
        var probe = new SdfProgramBuilder();

        using (probe.BeginMaterialScope()) {
            scene.Emit(builder: probe, context: new(true, 0, Vector3.Zero, Vector3.Zero, 0));
        }

        Assert.Equal(expected: headroom, actual: envelope.HeadroomInstances);
        Assert.Equal(expected: envelope.ReservedInstances, actual: probe.InstanceCount);
    }
    /// <summary>The headroom a document asks for is clamped to what the instance ceiling leaves above its boot scene,
    /// so asking for more reserves the ceiling and refuses nothing.</summary>
    [Fact]
    public void HeadroomPastTheCeilingReservesTheCeiling() {
        var envelope = new WorldSceneEnvelope(boot: Document(headroom: SdfProgramBuilder.MaxInstances, placements: []));

        Assert.Null(@object: envelope.Boot.Cost.Exceeds());
        Assert.Equal(expected: SdfProgramBuilder.MaxInstances, actual: envelope.ReservedInstances);
    }
    /// <summary>The count's placement term covers what the static stamper emits for placements of a panelled, a trimmed
    /// and a plain scope-free creation, and is exact for the plain one: a panelled shape or a trim charges its two
    /// chains as two instances.</summary>
    [InlineData("plaque", 4)]
    [InlineData("seam", 8)]
    [InlineData("store", 4)]
    [Theory]
    public void ThePlacementCountCoversWhatTheStaticStamperEmits(string prototype, int counted) {
        var definition = Document(headroom: 0, placements: [Placed(id: "a", prototype: prototype), Placed(id: "b", prototype: prototype)]);
        var live = new SdfProgramBuilder();

        WorldPlacementStamper.EmitStatic(
            builder: live,
            creations: definition.Creations,
            definition: definition,
            placements: definition.Placements
        );

        Assert.Equal(expected: counted, actual: WorldSceneEnvelope.Of(candidate: definition).Placements.Instances);
        Assert.InRange(actual: live.InstanceCount, high: counted, low: ((prototype == "store") ? counted : 1));
    }
    /// <summary>Every active renderer constrains admission independently, and disposing one renderer removes only
    /// its own constraint: no last-writer-wins overwrite and no stale capacity after the consumer goes away.</summary>
    [Fact]
    public void RegistrationsComposeAndDisposeIndependently() {
        var envelope = new WorldRenderEnvelope();
        var definition = Fixtures.BuildDocument();
        var accepting = envelope.Configure(measure: static _ => new WorldRenderCost(Instances: 10, Materials: 10));
        var refusing = envelope.Configure(measure: static _ => new WorldRenderCost(Instances: (SdfProgramBuilder.MaxInstances + 1), Materials: 10));

        Assert.False(condition: envelope.TryFit(
            candidate: definition,
            reason: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "exceed the engine ceiling"
        );

        refusing.Dispose();

        Assert.True(
            condition: envelope.TryFit(
                candidate: definition,
                reason: out var acceptedReason
            ),
            userMessage: acceptedReason
        );

        accepting.Dispose();

        Assert.True(
            condition: envelope.TryFit(
                candidate: definition,
                reason: out var unconfiguredReason
            ),
            userMessage: unconfiguredReason
        );

        // Idempotent teardown is required because view release and composition-root disposal can converge.
        accepting.Dispose();
        refusing.Dispose();
    }

    private sealed class SilentAudio : IWorldAudioCueSink {
        public void SubmitCue(string eventToken, Vector3? site) { }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public void BeginTick(ulong tick) { }
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) { neighbour = null; return false; }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
        public bool TryLocalDepartedFrom(int index, out WorldEntityAddress departedFrom) {
            departedFrom = default;

            return false;
        }
    }
}

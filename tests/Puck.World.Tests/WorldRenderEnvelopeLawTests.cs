using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Client.Sdf;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the render-capacity registrations shared by the world continuum and session-screen views.</summary>
/// <param name="scenes">The boot probes the headroom laws share, built at most once for this class.</param>
/// <param name="output">The test's output, where the shipped-world law prints its measured figures.</param>
public sealed partial class WorldRenderEnvelopeLawTests(WorldRenderEnvelopeLawTests.Scenes scenes, ITestOutputHelper output) : IClassFixture<WorldRenderEnvelopeLawTests.Scenes> {
    [Fact]
    public void GrowingConsumerStillRefusesTheEngineInstanceCeiling() {
        var envelope = new WorldRenderEnvelope();
        using var registration = envelope.Configure(
            allowGrowth: true,
            instanceCapacity: 1,
            measure: _ => (100, (SdfProgramBuilder.MaxInstances + 1)),
            programWordCapacity: 1
        );

        Assert.False(condition: envelope.TryFit(candidate: Fixtures.BuildDocument(), reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "engine ceiling");
    }
    [Fact]
    public void LiveDocumentCanGrowButRefusalsPreserveItsPreviousProgram() {
        var emitter = new WorldSdfDocumentEmitter();
        var document = """
            {"schema":"puck.sdf.v1","materials":[{"albedo":[1,0,0]}],"ops":[{"op":"sphere","radius":1,"material":0}]}
            """u8.ToArray();

        emitter.Configure(1, 0, _ => (1024, 1), allowGrowth: true);
        emitter.Load(utf8Json: document);
        var accepted = emitter.CurrentProgram;
        Span<int> before = stackalloc int[1];

        emitter.WriteRevision(destination: before);

        emitter.Configure(1, 0, _ => (1024, (SdfProgramBuilder.MaxInstances + 1)), allowGrowth: true);
        Assert.Throws<SdfDocumentException>(testCode: () => emitter.Load(utf8Json: document));
        Assert.Same(accepted, emitter.CurrentProgram);

        emitter.Configure(1, 0, _ => throw new InvalidOperationException(message: "composed structural limit"), allowGrowth: true);
        var refusal = Assert.Throws<SdfDocumentException>(testCode: () => emitter.Load(utf8Json: document));

        Assert.Contains("composed structural limit", refusal.Message, StringComparison.Ordinal);
        Assert.Same(accepted, emitter.CurrentProgram);

        emitter.Configure(1, 0, _ => (1024, 1));
        Assert.Throws<SdfDocumentException>(testCode: () => emitter.Load(utf8Json: document));
        Assert.Same(accepted, emitter.CurrentProgram);
        Span<int> after = stackalloc int[1];

        emitter.WriteRevision(destination: after);
        Assert.Equal(before[0], after[0]);
    }
    /// <summary>The panel case: a panelled shape emits TWO shape instructions (the plate and its eroded copy) instead
    /// of one, so a new placement referencing it must still fit inside the boot probe's already-reserved headroom
    /// (<see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> covers it — see
    /// <see cref="Puck.World.Authoring.CreationDocument.StampShapeCount"/>, which charges a panelled shape as 2).</summary>
    [Fact]
    public void AuthoredHeadroomAdmitsANewPanelledPlacement() {
        var scene = scenes.Headroom(headroom: 1);
        var measured = scene.Measure(candidate: scene.Candidate(
            name: "new-plaque",
            prototype: "plaque"
        ));
        var solo = Solo(prototype: Plaque);

        // The panelled plate emits two ShapeBlend instructions (the plate and its eroded copy) in isolation, and
        // the whole candidate scene (this placement plus everything else the boot probe already covers) still
        // fits inside the boot probe's own reservation.
        Assert.Equal(
            2,
            solo.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend))
        );
        Assert.True(condition: (measured.Words <= scene.BootWords));
        Assert.True(condition: (measured.Instances <= scene.BootInstances));
    }
    /// <summary>The trim case: a trimmed shape's document emits its own instruction PLUS the reference's own PLUS
    /// the trim's own two (the host's eroded copy and the reference's dilated copy), so a new placement referencing
    /// it must still fit inside the boot probe's already-reserved headroom (<see cref="WorldPlacementPolicy.MaxShapesPerStamp"/>
    /// covers it — see <see cref="Puck.World.Authoring.CreationDocument.StampShapeCount"/>, which charges each of a
    /// shape's trims as 2).</summary>
    [Fact]
    public void AuthoredHeadroomAdmitsANewTrimmedPlacement() {
        var scene = scenes.Headroom(headroom: 1);
        var measured = scene.Measure(candidate: scene.Candidate(
            name: "new-seam",
            prototype: "seam"
        ));
        var solo = Solo(prototype: Seam);

        // The cutter's own shape, the plate's own shape, plus the trim's two (eroded plate copy, dilated cutter
        // copy) — four ShapeBlend instructions in isolation — and the whole candidate scene still fits inside the
        // boot probe's own reservation.
        Assert.Equal(
            4,
            solo.Instructions.Count(predicate: instruction => (instruction.Op == SdfOp.ShapeBlend))
        );
        Assert.True(condition: (measured.Words <= scene.BootWords));
        Assert.True(condition: (measured.Instances <= scene.BootInstances));
    }
    [InlineData(0)]
    [InlineData(1)]
    [Theory]
    public void LiveGrowthAdmitsNewScopeFreePlacementsWithOrWithoutHeadroom(int headroom) {
        var scene = scenes.Headroom(headroom: headroom);
        var candidate = scene.Candidate(
            name: "new-store",
            prototype: "store"
        );
        var measured = scene.Measure(candidate: candidate);

        var envelope = new WorldRenderEnvelope();
        using var growing = envelope.Configure(
            programWordCapacity: scene.BootWords,
            instanceCapacity: scene.BootInstances,
            measure: _ => measured,
            allowGrowth: true
        );

        Assert.True(condition: envelope.TryFit(candidate: candidate, reason: out var reason), userMessage: reason);
        // The zero-reserve case reproduces adding the queen to an unauthored placement policy.
        Assert.Equal((headroom == 0), (measured.Instances > scene.BootInstances));
        using var fixedConsumer = envelope.Configure(
            programWordCapacity: scene.BootWords,
            instanceCapacity: scene.BootInstances,
            measure: _ => measured
        );

        Assert.Equal((headroom != 0), envelope.TryFit(candidate: candidate, reason: out _));
    }
    /// <summary>Every active renderer constrains admission independently, and disposing one renderer removes only
    /// its own constraint. This pins both halves of the lease contract: no last-writer-wins overwrite and no stale
    /// capacity after the consumer goes away.</summary>
    [Fact]
    public void RegistrationsComposeAndDisposeIndependently() {
        var envelope = new WorldRenderEnvelope();
        var definition = Fixtures.BuildDocument();
        var accepting = envelope.Configure(
            instanceCapacity: 10,
            measure: static _ => (Words: 10, Instances: 10),
            programWordCapacity: 10
        );
        var refusing = envelope.Configure(
            instanceCapacity: 10,
            measure: static _ => (Words: 11, Instances: 10),
            programWordCapacity: 10
        );

        Assert.False(condition: envelope.TryFit(
            candidate: definition,
            reason: out var refusal
        ));
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: "program words 11 exceed"
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
    /// <summary>Measures the shipped overworld's worst-case COMPOSED boot probe — the same four emitters
    /// <c>WorldFramePresenter</c> hands <see cref="Puck.SdfVm.SdfCompositionFrameSource"/> (the scene, the SDF
    /// document reservation, every adjacency band's reservation, the field bricks), each under the composition's
    /// own probe context and material-scope rule — against the interpreter's hard
    /// <see cref="SdfProgramBuilder.MaxInstances"/> ceiling, and requires real headroom for statics, screens, and
    /// avatars beyond the stamp pool's own worst case (<see cref="WorldPlacementPolicy.MaxStampRegistrations"/> x
    /// <see cref="WorldPlacementPolicy.MaxShapesPerStamp"/>). The scene emitter alone under-counts: the adjacency
    /// emitter reserves placement stamps and rigs per band, so the figure that governs the constant is the composed one.</summary>
    [Fact]
    public void ShippedWorldBootProbeInstancesFitTheEngineCeilingWithHeadroom() {
        var definition = AuthoredGameFixtures.Nexus;

        var (client, scene) = SceneEmitter(definition: definition);
        var adjacencies = new WorldAdjacencySceneEmitter(
            client,
            new NoNeighbours()
        );
        // The presenter's emitter list, in its order: [scene, sdf documents, adjacencies, fields].
        ISdfSceneEmitter[] emitters = [scene, new WorldSdfDocumentEmitter(), adjacencies, new WorldFieldEmitter(client: client)];
        var bootBuilder = new SdfProgramBuilder();
        var sceneOnlyInstances = -1;
        var slotBase = 0;

        foreach (var emitter in emitters) {
            var context = new SdfEmitContext(
                true,
                0,
                Vector3.Zero,
                Vector3.Zero,
                slotBase
            );

            if (emitter.OwnsMaterialScope) {
                using var scope = bootBuilder.BeginMaterialScope();

                emitter.Emit(
                    builder: bootBuilder,
                    context: context
                );
            } else {
                emitter.Emit(
                    builder: bootBuilder,
                    context: context
                );
            }

            // The scene emitter leads the list at slot base zero into an empty builder, so the instances it leaves
            // are exactly the ones it composes alone.
            if (ReferenceEquals(
                objA: emitter,
                objB: scene
            )) {
                sceneOnlyInstances = bootBuilder.InstanceCount;
            }

            slotBase += Math.Max(
                val1: 0,
                val2: emitter.DynamicSlotCount
            );
        }

        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var stampPoolWorstCase = (WorldPlacementPolicy.MaxStampRegistrations * WorldPlacementPolicy.MaxShapesPerStamp);
        var headroom = (SdfProgramBuilder.MaxInstances - boot.Instances.Count);

        output.WriteLine(message: (((string)$"world.budget probe: composed boot instances={boot.Instances.Count} (scene emitter alone {sceneOnlyInstances}, adjacency bands {WorldAdjacencyBands.ProjectionCapacity(definition: definition)}) words={boot.Words.Length} ") +
            $"ceiling={SdfProgramBuilder.MaxInstances} headroom={headroom} stampPoolWorstCase={stampPoolWorstCase}"));

        var figures = (((string)$"composed boot probe {boot.Instances.Count} instance(s) (scene emitter alone {sceneOnlyInstances}, {WorldAdjacencyBands.ProjectionCapacity(definition: definition)} adjacency band(s)), stamp pool worst case {stampPoolWorstCase}, ") +
            $"headroom {headroom} under the {SdfProgramBuilder.MaxInstances}-instance ceiling at MaxShapesPerStamp {WorldPlacementPolicy.MaxShapesPerStamp}");

        Assert.True(
            condition: (boot.Instances.Count <= SdfProgramBuilder.MaxInstances),
            userMessage: $"{figures}: the composed probe exceeds the ceiling."
        );
        Assert.True(
            condition: (headroom >= 4096),
            userMessage: $"{figures}: falls below the 4096-instance floor left for statics, screens, and avatars."
        );
        // The composed figure is the one WorldPlacementPolicy.MaxShapesPerStamp is measured against; the scene
        // emitter alone under-counts by the adjacency/field reservations, so a law reading only it would admit a
        // constant the real boot refuses.
        Assert.True(
            condition: (boot.Instances.Count > sceneOnlyInstances),
            userMessage: $"{figures}: the composed probe reserves nothing beyond the scene emitter, so the composed measure is not discriminating."
        );
    }

    // One prototype stamped alone at the origin under its own material scope.
    private static SdfProgram Solo(WorldPrototype prototype) {
        var builder = new SdfProgramBuilder();

        using (builder.BeginMaterialScope()) {
            CreationStampEmitter.Emit(
                builder,
                prototype.Document,
                new(
                    Vector3.Zero,
                    Quaternion.Identity,
                    1f,
                    null
                ),
                _ => builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One))
            );
        }

        return builder.Build(buildInstanceGrid: false);
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

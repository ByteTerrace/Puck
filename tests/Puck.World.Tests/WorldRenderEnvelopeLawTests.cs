using System.Numerics;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the render-capacity registrations shared by the world continuum and session-screen views.</summary>
public sealed class WorldRenderEnvelopeLawTests {
    [Fact]
    public void AuthoredHeadroomAdmitsNewScopeFreeMultiShapePlacements() {
        var prototype = new WorldPrototype("store", new("puck.creation.v1", "store", [new("#AA7755", null, null, null)],
            [new(0, "wall", SdfSolidPrimitive.Box, Vector3.Zero, Quaternion.Identity, Vector3.One, 0, null, 0, null),
             new(1, "roof", SdfSolidPrimitive.Box, Vector3.UnitY * 2, Quaternion.Identity, Vector3.One, 0, null, 0, null)], null));
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [prototype],
            PlacementsRaw = new(Rows: [], Policy: new(0, 1, 1, 1, 10, 4, 8, 0)),
        };
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(new PlayerRoster(definition, new SilentLink(definition), new WorldSeatBindings(definition)),
            definition, new WorldCompositionState(), routes);
        var emitter = new WorldSceneEmitter(client, new(definition.Render), new(), new SilentAudio(), new(),
            new(client, routes, new NoNeighbours()), new(new(definition, "unused.world.json")));
        var bootBuilder = new SdfProgramBuilder();
        using (bootBuilder.BeginMaterialScope()) { emitter.Emit(bootBuilder, new(true, 0, Vector3.Zero, Vector3.Zero, 0)); }
        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var candidate = definition with { PlacementsRaw = definition.PlacementsRaw! with { Rows = [new("new-store", "store", Vector3.Zero, 0, 1)] } };
        var candidateBuilder = new SdfProgramBuilder();
        emitter.ComposeCandidate(candidateBuilder, candidate);
        var measured = candidateBuilder.Build(buildInstanceGrid: false);
        Assert.True(measured.Words.Length <= boot.Words.Length);
        Assert.True(measured.Instances.Count <= boot.Instances.Count);
    }

    /// <summary>The panel case: a panelled shape emits TWO shape instructions (the plate and its eroded copy) instead
    /// of one, so a new placement referencing it must still fit inside the boot probe's already-reserved headroom
    /// (<see cref="WorldPlacementPolicy.MaxShapesPerStamp"/> covers it — see
    /// <see cref="Puck.World.Authoring.CreationDocument.StampShapeCount"/>, which charges a panelled shape as 2).</summary>
    [Fact]
    public void AuthoredHeadroomAdmitsANewPanelledPlacement() {
        var prototype = new WorldPrototype("plaque", new("puck.creation.v1", "plaque",
            [new("#AA7755", null, null, null), new("#EEEEDD", null, null, null)],
            [new(0, "plate", SdfSolidPrimitive.Box, Vector3.Zero, Quaternion.Identity, new Vector3(0.4f, 0.3f, 0.2f), 0, null, 0, null,
                Panel: new(Inset: 0.05f, Depth: 0.1f, Material: 1))], null));
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [prototype],
            PlacementsRaw = new(Rows: [], Policy: new(0, 1, 1, 1, 10, 4, 8, 0)),
        };
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(new PlayerRoster(definition, new SilentLink(definition), new WorldSeatBindings(definition)),
            definition, new WorldCompositionState(), routes);
        var emitter = new WorldSceneEmitter(client, new(definition.Render), new(), new SilentAudio(), new(),
            new(client, routes, new NoNeighbours()), new(new(definition, "unused.world.json")));
        var bootBuilder = new SdfProgramBuilder();
        using (bootBuilder.BeginMaterialScope()) { emitter.Emit(bootBuilder, new(true, 0, Vector3.Zero, Vector3.Zero, 0)); }
        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var candidate = definition with { PlacementsRaw = definition.PlacementsRaw! with { Rows = [new("new-plaque", "plaque", Vector3.Zero, 0, 1)] } };
        var candidateBuilder = new SdfProgramBuilder();
        emitter.ComposeCandidate(candidateBuilder, candidate);
        var measured = candidateBuilder.Build(buildInstanceGrid: false);
        var soloBuilder = new SdfProgramBuilder();

        using (soloBuilder.BeginMaterialScope()) { CreationStampEmitter.Emit(soloBuilder, prototype.Document,
            new(Vector3.Zero, Quaternion.Identity, 1f, null), _ => soloBuilder.AddMaterial(new SdfMaterial(Albedo: Vector3.One))); }

        var solo = soloBuilder.Build(buildInstanceGrid: false);

        // The panelled plate emits two ShapeBlend instructions (the plate and its eroded copy) in isolation, and
        // the whole candidate scene (this placement plus everything else the boot probe already covers) still
        // fits inside the boot probe's own reservation.
        Assert.Equal(2, solo.Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend));
        Assert.True(measured.Words.Length <= boot.Words.Length);
        Assert.True(measured.Instances.Count <= boot.Instances.Count);
    }

    /// <summary>The trim case: a trimmed shape's document emits its own instruction PLUS the reference's own PLUS
    /// the trim's own two (the host's eroded copy and the reference's dilated copy), so a new placement referencing
    /// it must still fit inside the boot probe's already-reserved headroom (<see cref="WorldPlacementPolicy.MaxShapesPerStamp"/>
    /// covers it — see <see cref="Puck.World.Authoring.CreationDocument.StampShapeCount"/>, which charges each of a
    /// shape's trims as 2).</summary>
    [Fact]
    public void AuthoredHeadroomAdmitsANewTrimmedPlacement() {
        var prototype = new WorldPrototype("seam", new("puck.creation.v1", "seam",
            [new("#AA7755", null, null, null), new("#EEEEDD", null, null, null)],
            [new(0, "cutter", SdfSolidPrimitive.Sphere, Vector3.UnitZ, Quaternion.Identity, new Vector3(0.3f), 0, null, 0, null),
             new(1, "plate", SdfSolidPrimitive.Box, Vector3.Zero, Quaternion.Identity, Vector3.One, 0, null, 0, null,
                Trims: [new(Shape: "cutter", Width: 0.4f, Material: 1)])], null));
        var definition = Fixtures.BuildDocument() with {
            CreationsRaw = [prototype],
            PlacementsRaw = new(Rows: [], Policy: new(0, 1, 1, 1, 10, 4, 8, 0)),
        };
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(new PlayerRoster(definition, new SilentLink(definition), new WorldSeatBindings(definition)),
            definition, new WorldCompositionState(), routes);
        var emitter = new WorldSceneEmitter(client, new(definition.Render), new(), new SilentAudio(), new(),
            new(client, routes, new NoNeighbours()), new(new(definition, "unused.world.json")));
        var bootBuilder = new SdfProgramBuilder();
        using (bootBuilder.BeginMaterialScope()) { emitter.Emit(bootBuilder, new(true, 0, Vector3.Zero, Vector3.Zero, 0)); }
        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var candidate = definition with { PlacementsRaw = definition.PlacementsRaw! with { Rows = [new("new-seam", "seam", Vector3.Zero, 0, 1)] } };
        var candidateBuilder = new SdfProgramBuilder();
        emitter.ComposeCandidate(candidateBuilder, candidate);
        var measured = candidateBuilder.Build(buildInstanceGrid: false);
        var soloBuilder = new SdfProgramBuilder();

        using (soloBuilder.BeginMaterialScope()) { CreationStampEmitter.Emit(soloBuilder, prototype.Document,
            new(Vector3.Zero, Quaternion.Identity, 1f, null), _ => soloBuilder.AddMaterial(new SdfMaterial(Albedo: Vector3.One))); }

        var solo = soloBuilder.Build(buildInstanceGrid: false);

        // The cutter's own shape, the plate's own shape, plus the trim's two (eroded plate copy, dilated cutter
        // copy) — four ShapeBlend instructions in isolation — and the whole candidate scene still fits inside the
        // boot probe's own reservation.
        Assert.Equal(4, solo.Instructions.Count(instruction => instruction.Op == SdfOp.ShapeBlend));
        Assert.True(measured.Words.Length <= boot.Words.Length);
        Assert.True(measured.Instances.Count <= boot.Instances.Count);
    }

    private sealed class SilentAudio : IWorldAudioCueSink {
        public void SubmitCue(string eventToken, Vector3? site) { }
    }
    private sealed class NoNeighbours : IWorldAdjacencySource {
        public WorldEntityAddress LocalEntityAddress(int index) => default;
        public WorldBodyContactMode LocalBodyContact(int index) => WorldBodyContactMode.Solid;
        public void BeginTick(ulong tick) { }
        public bool TryResolve(string adjacencyName, out IWorldAdjacencyNeighbour? neighbour) { neighbour = null; return false; }
        public IReadOnlyList<WorldAdjacencyProjection> Visuals() => [];
    }
    private sealed class SilentLink(WorldDefinition definition) : IServerLink {
        public void Query(WorldQuery query, Action<QueryAnswer> completion) {
            if (query is WorldQuery.PopulationChannels) { completion(new(Payload: WorldChannelTable.Compile(definition.Channels), Text: "")); }
        }
        public long SubmitEnvelope(WorldSubmissionPayload payload, WorldPrincipal principal) => 0;
        public void SubmitIntent(in IntentSubmission submission) { }
        public void SubmitSession(SessionRequest request, Action<SessionReply> completion) { }
    }
    /// <summary>Every active renderer constrains admission independently, and disposing one renderer removes only
    /// its own constraint. This pins both halves of the lease contract: no last-writer-wins overwrite and no stale
    /// capacity after the consumer goes away.</summary>
    [Fact]
    public void RegistrationsComposeAndDisposeIndependently() {
        var envelope = new WorldRenderEnvelope();
        var definition = Fixtures.BuildDocument();
        var accepting = envelope.Configure(instanceCapacity: 10, measure: static _ => (Words: 10, Instances: 10), programWordCapacity: 10);
        var refusing = envelope.Configure(instanceCapacity: 10, measure: static _ => (Words: 11, Instances: 10), programWordCapacity: 10);

        Assert.False(condition: envelope.TryFit(candidate: definition, reason: out var refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: "program words 11 exceed");

        refusing.Dispose();

        Assert.True(condition: envelope.TryFit(candidate: definition, reason: out var acceptedReason), userMessage: acceptedReason);

        accepting.Dispose();

        Assert.True(condition: envelope.TryFit(candidate: definition, reason: out var unconfiguredReason), userMessage: unconfiguredReason);

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
        var routes = new WorldSeatAuthorityRouter();
        var client = new WorldClient(new PlayerRoster(definition, new SilentLink(definition), new WorldSeatBindings(definition)),
            definition, new WorldCompositionState(), routes);
        var scene = new WorldSceneEmitter(client, new(definition.Render), new(), new SilentAudio(), new(),
            new(client, routes, new NoNeighbours()), new(new(definition, "unused.world.json")));
        var adjacencies = new WorldAdjacencySceneEmitter(client, new NoNeighbours());
        // The presenter's emitter list, in its order: [scene, sdf documents, adjacencies, fields].
        ISdfSceneEmitter[] emitters = [scene, new WorldSdfDocumentEmitter(), adjacencies, new WorldFieldEmitter(client)];
        var bootBuilder = new SdfProgramBuilder();
        var sceneOnlyBuilder = new SdfProgramBuilder();
        var slotBase = 0;

        using (sceneOnlyBuilder.BeginMaterialScope()) { scene.Emit(sceneOnlyBuilder, new(true, 0, Vector3.Zero, Vector3.Zero, 0)); }

        foreach (var emitter in emitters) {
            var context = new SdfEmitContext(true, 0, Vector3.Zero, Vector3.Zero, slotBase);

            if (emitter.OwnsMaterialScope) {
                using var scope = bootBuilder.BeginMaterialScope();
                emitter.Emit(bootBuilder, context);
            } else {
                emitter.Emit(bootBuilder, context);
            }

            slotBase += Math.Max(0, emitter.DynamicSlotCount);
        }

        var boot = bootBuilder.Build(buildInstanceGrid: false);
        var sceneOnly = sceneOnlyBuilder.Build(buildInstanceGrid: false);
        var stampPoolWorstCase = (WorldPlacementPolicy.MaxStampRegistrations * WorldPlacementPolicy.MaxShapesPerStamp);
        var headroom = (SdfProgramBuilder.MaxInstances - boot.Instances.Count);

        Console.WriteLine($"world.budget probe: composed boot instances={boot.Instances.Count} (scene emitter alone {sceneOnly.Instances.Count}, " +
            $"adjacency bands {WorldAdjacencyBands.ProjectionCapacity(definition)}) words={boot.Words.Length} " +
            $"ceiling={SdfProgramBuilder.MaxInstances} headroom={headroom} stampPoolWorstCase={stampPoolWorstCase}");

        var figures = $"composed boot probe {boot.Instances.Count} instance(s) (scene emitter alone {sceneOnly.Instances.Count}, " +
            $"{WorldAdjacencyBands.ProjectionCapacity(definition)} adjacency band(s)), stamp pool worst case {stampPoolWorstCase}, " +
            $"headroom {headroom} under the {SdfProgramBuilder.MaxInstances}-instance ceiling at MaxShapesPerStamp {WorldPlacementPolicy.MaxShapesPerStamp}";

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
            condition: (boot.Instances.Count > sceneOnly.Instances.Count),
            userMessage: $"{figures}: the composed probe reserves nothing beyond the scene emitter, so the composed measure is not discriminating."
        );
    }
}

using System.Numerics;
using Puck.SignedDistance;
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
}

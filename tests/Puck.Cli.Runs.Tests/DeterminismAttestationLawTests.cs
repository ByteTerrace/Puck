using Puck.Cli.Determinism;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.Testing;
using Puck.World;
using Puck.World.Protocol;
using Xunit;

namespace Puck.Cli.Runs.Tests;

public sealed class DeterminismAttestationLawTests {
    private const string Pin = "sha256-64/0123456789abcdef";

    private static string World(string name) {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));

        return Path.Combine(path1: root, path2: $"tests/Puck.World.Canaries/{name}/fixture.puck");
    }

    [InlineData("velocity")]
    [InlineData("remainder")]
    [InlineData("timer")]
    [Theory]
    public void EqualPosesWithDifferentContinuationsDivergeAtTheRecordedTick(string field) {
        Assert.True(condition: DeterminismRecorder.TryLoadWorld(path: World(name: "traveller-kit"), authored: out _, definition: out var definition, error: out var error), userMessage: error);
        using var host = WorldBenchServer.Boot(definition: definition!);
        var server = host.Server;

        Assert.True(condition: server.ApplySession(request: new SessionRequest.Join(IdentityName: null, Principal: Principal.Seat(slot: 0), Slot: 0, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        server.Advance(stepTicks: EngineTicks.PerRate(ratePerSecond: ((uint)definition!.SimulationRateHz)));

        var body = server.Body(index: 0)!;
        var before = DeterminismRecorder.Vector(server: server);
        var pose = WorldReplaySnapshot.HashState(population: server.Population);
        var state = body.CaptureTransferState();
        var residue = body.CaptureIntegrationResidue();

        switch (field) {
            case "velocity":
                body.ApplyTransferState(state: state with { VerticalVelocity = (state.VerticalVelocity + FixedQ4816.One) });
                break;
            case "remainder":
                residue = residue with { PositionRemainderX = (residue.PositionRemainderX + 1) };
                break;
            case "timer":
                var timers = ((ulong[])state.ChannelTimerTicks.Clone());
                timers[0]++;
                body.ApplyTransferState(state: state with { ChannelTimerTicks = timers });
                break;
        }

        body.ApplyIntegrationResidue(residue: residue);
        Assert.Equal(expected: pose, actual: WorldReplaySnapshot.HashState(population: server.Population));
        var after = DeterminismRecorder.Vector(server: server);

        Assert.True(condition: DeterminismComparison.TryCompare(
            left: new DeterminismStream(ManifestPin: Pin, Scenarios: [new DeterminismScenarioRecord(Documents: [], Name: "body", Ticks: [before])]),
            right: new DeterminismStream(ManifestPin: Pin, Scenarios: [new DeterminismScenarioRecord(Documents: [], Name: "body", Ticks: [after])]),
            comparison: out var comparison, refusal: out var refusal), userMessage: refusal);
        var divergence = Assert.Single(collection: comparison!.Divergences);

        Assert.Equal(expected: 1, actual: divergence.Tick);
        Assert.Equal(expected: "BodyContinuation", actual: divergence.Component);
    }
    [Fact]
    public void ARefusedCellWriteCannotProduceASuccessfulAttestation() {
        var scenario = new DeterminismScenario(Name: "refused-write", World: World(name: "records-pools"), Ticks: 2, Seats: [], Intents: [],
            Cells: [new DeterminismCellWrite(Key: "$other", Row: "request", Tick: 1, Value: "1")], Exercises: ["Arena"]);

        Assert.False(condition: DeterminismRecorder.TryRecord(error: out var error, record: out var record, scenario: scenario));
        Assert.Null(@object: record);
        Assert.Contains(actualString: error, expectedSubstring: "tick 1");
        Assert.Contains(actualString: error, expectedSubstring: "request/$other");
        Assert.Contains(actualString: error, expectedSubstring: "refused");
    }
    [InlineData("")]
    [InlineData("document unexpected abc\n")]
    [InlineData("document fingerprint abc\ndocument definition abc\ndocument catalog abc\ndocument unexpected abc\n")]
    [Theory]
    public void StreamsWithoutTheDocumentAttestationAreRefused(string documents) {
        var text = $"{DeterminismStream.Version} {FormatLedgerShapes.Of(id: "DeterminismStream.Version")}\nmanifest {Pin}\ncomponents {string.Join(separator: ' ', values: DeterminismStream.Components)}\nscenario body 1\n{documents}tick 1 {string.Join(separator: ' ', values: Enumerable.Repeat("0000000000000000", DeterminismStream.Components.Count))}\nend\n";

        Assert.False(condition: DeterminismStream.TryParse(error: out var error, stream: out _, text: text));
        Assert.Contains(actualString: error, expectedSubstring: "document");
    }
    [Fact]
    public void TheShippedManifestExercisesFlockSamplingAndRetainsNeighbors() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        Assert.True(condition: DeterminismManifest.TryLoad(path: Path.Combine(path1: root, path2: "tests/Puck.Determinism/determinism.json"), manifest: out var manifest, error: out var error), userMessage: error);
        var updates = 0;
        var neighbors = 0;

        foreach (var scenario in manifest!.Scenarios) {
            Assert.True(condition: DeterminismRecorder.TryLoadWorld(path: scenario.World, authored: out _, definition: out var definition, error: out error), userMessage: error);
            if (!definition!.Kits.Any(predicate: static kit => kit.Producers.Values.Any(predicate: static producer => (producer.Flock is not null)))) {
                continue;
            }

            using var host = WorldBenchServer.Boot(definition: definition);

            for (var tick = 0; (tick < scenario.Ticks); tick++) {
                host.Server.Advance(stepTicks: EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz)));
                updates += host.Server.Population.FlockStatistics.Updates;
                neighbors += host.Server.Population.FlockStatistics.RetainedNeighbors;
            }

            Assert.True(condition: DeterminismRecorder.TryRecord(error: out error, record: out var record, scenario: scenario), userMessage: error);
            var component = DeterminismStream.Components.ToList().IndexOf(item: "Flock");

            Assert.NotEqual(expected: record!.Ticks[0][component], actual: record.Ticks[^1][component]);
        }

        Assert.True(condition: (updates > 0), userMessage: "The manifest performs no flock sample.");
        Assert.True(condition: (neighbors > 0), userMessage: "The manifest retains no flock neighbor.");
    }
}

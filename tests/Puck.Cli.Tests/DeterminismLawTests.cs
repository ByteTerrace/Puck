using System.Text;
using Puck.Assets;
using Puck.Cli.Determinism;
using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck determinism</c> records what a scenario hashes to and compares two records.
/// A stream round-trips its one spelling; the same scenario recorded twice diverges nowhere; a divergence planted at a
/// tick is reported at exactly that tick and the system it split; a CRLF canonical writer on one host is reported at
/// the document hash it moves; and streams of another version, manifest or scenario list are refused by name rather
/// than compared. The scenario is the records-pools canary's world, whose pool rule a state write starts.</summary>
public sealed class DeterminismLawTests {
    private const string Pin = "sha256-64/0123456789abcdef";
    private const int Ticks = 20;

    private static string World() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));

        return Path.Combine(
            path1: root,
            path2: "tests/Puck.World.Canaries/records-pools/fixture.world.json"
        );
    }
    // The records-pools world for Ticks ticks, its pool request written before the given tick.
    private static DeterminismScenarioRecord Record(int requestTick) {
        Assert.True(
            condition: DeterminismRecorder.TryRecord(
                error: out var error,
                record: out var record,
                scenario: new DeterminismScenario(
                    Cells: [new DeterminismCellWrite(Key: "$value", Row: "request", Tick: requestTick, Value: "1")],
                    Intents: [],
                    Name: "records-pools",
                    Seats: [],
                    Ticks: Ticks,
                    World: World()
                )
            ),
            userMessage: error
        );

        return record!;
    }
    private static DeterminismComparison Compare(DeterminismScenarioRecord left, DeterminismScenarioRecord right) {
        Assert.True(
            condition: DeterminismComparison.TryCompare(
                comparison: out var comparison,
                left: new DeterminismStream(ManifestPin: Pin, Scenarios: [left]),
                refusal: out var refusal,
                right: new DeterminismStream(ManifestPin: Pin, Scenarios: [right])
            ),
            userMessage: refusal
        );

        return comparison!;
    }

    [Fact]
    public void ARecordedStreamRoundTripsThroughItsOneSpelling() {
        var stream = new DeterminismStream(ManifestPin: Pin, Scenarios: [Record(requestTick: 10)]);
        var text = stream.Render();

        Assert.True(condition: DeterminismStream.TryParse(error: out var error, stream: out var parsed, text: text), userMessage: error);
        Assert.Equal(expected: text, actual: parsed!.Render());
        Assert.Equal(expected: Pin, actual: parsed.ManifestPin);
        Assert.Equal(expected: stream.Scenarios[0].Documents, actual: parsed.Scenarios[0].Documents);
        Assert.Equal(expected: stream.Scenarios[0].Ticks, actual: parsed.Scenarios[0].Ticks);
        Assert.DoesNotContain(actualString: text, expectedSubstring: "\r");
    }
    [Fact]
    public void TheSameScenarioRecordedTwiceDivergesNowhereAndEveryHashIsCounted() {
        var first = Record(requestTick: 10);
        var comparison = Compare(left: first, right: Record(requestTick: 10));

        Assert.Empty(collection: comparison.Divergences);
        Assert.Equal(expected: 1, actual: comparison.Scenarios);
        Assert.Equal(expected: Ticks, actual: comparison.Ticks);
        Assert.Equal(expected: (first.Documents.Count + (((long)Ticks) * DeterminismStream.Components.Count)), actual: comparison.Hashes);
    }
    [Fact]
    public void AStateDivergencePlantedAtATickIsReportedAtThatTickAndTheSystemItSplit() {
        var comparison = Compare(left: Record(requestTick: 10), right: Record(requestTick: 14));
        var divergence = Assert.Single(collection: comparison.Divergences);

        Assert.Equal(expected: 10, actual: divergence.Tick);
        Assert.Equal(expected: "Arena", actual: divergence.Component);
        Assert.NotEqual(expected: divergence.Left, actual: divergence.Right);
        // The ticks before the divergence agree and are counted; what follows it is not compared.
        Assert.Equal(expected: 10, actual: comparison.Ticks);
    }
    [Fact]
    public void ACrlfCanonicalWriterOnOneHostIsReportedAtTheDocumentHashItMoves() {
        Assert.True(condition: DeterminismRecorder.TryLoadWorld(authored: out _, definition: out var definition, error: out var error, path: World()), userMessage: error);

        var lf = Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition!));
        var crlf = ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: lf.Replace(newValue: "\r\n", oldValue: "\n"))).Hex;
        var left = Record(requestTick: 10);
        var right = left with {
            Documents = [.. left.Documents.Select(selector: document => ((document.Name == "fingerprint") ? (document with { Value = crlf }) : document))],
        };

        Assert.Equal(expected: ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: lf)).Hex, actual: left.Documents.Single(predicate: static document => (document.Name == "fingerprint")).Value);

        var divergence = Assert.Single(collection: Compare(left: left, right: right).Divergences);

        Assert.Equal(expected: 0, actual: divergence.Tick);
        Assert.Equal(expected: "fingerprint", actual: divergence.Component);
        Assert.Equal(expected: crlf, actual: divergence.Right);
        Assert.NotEqual(expected: divergence.Left, actual: divergence.Right);
    }
    [Fact]
    public void AStreamOfAnotherVersionIsRefusedByName() {
        var text = new DeterminismStream(ManifestPin: Pin, Scenarios: [Record(requestTick: 10)]).Render();

        Assert.False(condition: DeterminismStream.TryParse(error: out var error, stream: out _, text: text.Replace(newValue: "puck.determinism.stream.v2", oldValue: DeterminismStream.Version)));
        Assert.Contains(actualString: error, expectedSubstring: "not the version token puck.determinism.stream.v1");
        Assert.False(condition: DeterminismStream.TryParse(error: out error, stream: out _, text: text.Replace(newValue: "\r\n", oldValue: "\n")));
    }
    [Fact]
    public void StreamsOfAnotherManifestOrScenarioListAreRefusedByNameNotCompared() {
        var record = Record(requestTick: 10);
        var stream = new DeterminismStream(ManifestPin: Pin, Scenarios: [record]);

        Assert.False(condition: DeterminismComparison.TryCompare(comparison: out _, left: stream, refusal: out var refusal, right: (stream with { ManifestPin = "sha256-64/fedcba9876543210" })));
        Assert.Contains(actualString: refusal, expectedSubstring: "different manifests");
        Assert.False(condition: DeterminismComparison.TryCompare(comparison: out _, left: stream, refusal: out refusal, right: (stream with { Scenarios = [record with { Ticks = [.. record.Ticks.Take(count: (Ticks - 1))] }] })));
        Assert.Contains(actualString: refusal, expectedSubstring: "different scenarios or tick counts");

        // The verb refuses the same way, with the refusal exit code.
        using var directory = new TemporaryDirectory(prefix: "puck-determinism-law-");

        directory.WriteText(name: "left.stream", text: stream.Render());
        directory.WriteText(name: "right.stream", text: (stream with { ManifestPin = "sha256-64/fedcba9876543210" }).Render());

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => PuckRootCommand.Invoke(args: ["determinism", "compare", directory.PathOf(name: "left.stream"), directory.PathOf(name: "right.stream")]));

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "different manifests");
    }
    [Fact]
    public void TheShippedManifestLoadsAndAMalformedOneIsRefusedByName() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        Assert.True(condition: DeterminismManifest.TryLoad(error: out var error, manifest: out var manifest, path: Path.Combine(path1: root, path2: "tests/Puck.Determinism/determinism.json")), userMessage: error);
        Assert.All(action: static scenario => Assert.True(condition: File.Exists(path: scenario.World), userMessage: scenario.World), collection: manifest!.Scenarios);

        using var directory = new TemporaryDirectory(prefix: "puck-determinism-law-");

        directory.WriteText(name: "extra.json", text: """{ "schema": "puck.determinism.manifest.v1", "scenarios": [], "extra": 1 }""");
        directory.WriteText(name: "version.json", text: """{ "schema": "puck.determinism.manifest.v2", "scenarios": [] }""");

        Assert.False(condition: DeterminismManifest.TryLoad(error: out error, manifest: out _, path: directory.PathOf(name: "extra.json")));
        Assert.Contains(actualString: error, expectedSubstring: "extra");
        Assert.False(condition: DeterminismManifest.TryLoad(error: out error, manifest: out _, path: directory.PathOf(name: "version.json")));
        Assert.Contains(actualString: error, expectedSubstring: "puck.determinism.manifest.v2");
    }
}

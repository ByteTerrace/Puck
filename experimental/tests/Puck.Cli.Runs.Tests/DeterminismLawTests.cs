using System.Text;
using Puck.Assets;
using Puck.Cli.Determinism;
using Puck.Testing;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck determinism</c> records what a scenario hashes to and compares two records.
/// A stream round-trips its one spelling; the same scenario recorded twice diverges nowhere; a divergence planted at a
/// tick is reported at exactly that tick and the system it split; a CRLF canonical writer on one host is reported at
/// the document hash it moves; and streams of another version, manifest or scenario list are refused by name rather
/// than compared. The scenario is the records-pools canary's world, whose pool rule a state write starts. Every shipped
/// scenario moves each component it names as exercised, every per-tick component but the declared topologies is
/// exercised by some scenario, and the topologies, which nothing at run time redeclares, are folded and hold
/// still.</summary>
public sealed class DeterminismLawTests {
    private const string Pin = "sha256-64/0123456789abcdef";
    private const int Ticks = 20;

    private static string World() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));

        return Path.Combine(
            path1: root,
            path2: "tests/Puck.World.Canaries/records-pools/fixture.puck"
        );
    }
    private static DeterminismManifest ShippedManifest() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));
        Assert.True(condition: DeterminismManifest.TryLoad(error: out var error, manifest: out var manifest, path: Path.Combine(path1: root, path2: "tests/Puck.Determinism/determinism.json")), userMessage: error);

        return manifest!;
    }
    private static DeterminismScenarioRecord RecordShipped(string name) {
        var scenario = ShippedManifest().Scenarios.Single(predicate: scenario => (scenario.Name == name));

        Assert.True(condition: DeterminismRecorder.TryRecord(error: out var error, record: out var record, scenario: scenario), userMessage: error);

        return record!;
    }
    // A component's hash on every tick, read off the vector by its position in the stream's components line.
    private static ulong[] Column(DeterminismScenarioRecord record, string component) {
        var column = DeterminismStream.Components.ToList().IndexOf(item: component);

        Assert.True(condition: (column >= 0), userMessage: component);

        return [.. record.Ticks.Select(selector: vector => vector[column])];
    }
    // The records-pools world for Ticks ticks, its pool request written before the given tick.
    private static DeterminismScenarioRecord Record(int requestTick) {
        Assert.True(
            condition: DeterminismRecorder.TryRecord(
                error: out var error,
                record: out var record,
                scenario: new DeterminismScenario(
                    Cells: [new DeterminismCellWrite(Key: "$value", Row: "request", Tick: requestTick, Value: "1")],
                    Exercises: ["Arena"],
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
    // The first line names the version token, then the shape fingerprint the ledger records; the same version under another
    // shape is refused by the fingerprint's name and never compared.
    [Fact]
    public void AStreamOfTheSameVersionAndAnotherShapeIsRefusedByItsFingerprint() {
        var text = new DeterminismStream(ManifestPin: Pin, Scenarios: [Record(requestTick: 10)]).Render();
        var recorded = FormatLedgerShapes.Of(id: "DeterminismStream.Version");

        Assert.StartsWith(actualString: text, expectedStartString: $"{DeterminismStream.Version} {recorded}\n");
        Assert.True(condition: DeterminismStream.TryParse(error: out _, stream: out _, text: text));
        Assert.False(condition: DeterminismStream.TryParse(error: out var error, stream: out _, text: text.Replace(newValue: "0000000000000000", oldValue: recorded)));
        Assert.Contains(actualString: error, expectedSubstring: "names shape fingerprint '0000000000000000'");
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

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => SuiteRoot.Invoke(args: ["determinism", "compare", directory.PathOf(name: "left.stream"), directory.PathOf(name: "right.stream")]));

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
        directory.WriteText(name: "exercises.json", text: """{ "schema": "puck.determinism.manifest.v1", "scenarios": [{ "name": "a", "world": "a.world.json", "ticks": 1, "seats": [], "intents": [], "cells": [], "exercises": ["authoritative"] }] }""");
        directory.WriteText(name: "version.json", text: """{ "schema": "puck.determinism.manifest.v2", "scenarios": [] }""");

        Assert.False(condition: DeterminismManifest.TryLoad(error: out error, manifest: out _, path: directory.PathOf(name: "extra.json")));
        Assert.Contains(actualString: error, expectedSubstring: "extra");
        Assert.False(condition: DeterminismManifest.TryLoad(error: out error, manifest: out _, path: directory.PathOf(name: "version.json")));
        Assert.Contains(actualString: error, expectedSubstring: "puck.determinism.manifest.v2");
        // An aggregate moves on every tick whatever the systems do, so it is no component a scenario can exercise.
        Assert.False(condition: DeterminismManifest.TryLoad(error: out error, manifest: out _, path: directory.PathOf(name: "exercises.json")));
        Assert.Contains(actualString: error, expectedSubstring: "names 'authoritative', not a per-tick component");
    }
    [MemberData(memberName: nameof(ShippedScenarios))]
    [Theory]
    public void EachShippedScenarioMovesEveryComponentItExercises(string name) {
        var scenario = ShippedManifest().Scenarios.Single(predicate: scenario => (scenario.Name == name));
        var record = RecordShipped(name: name);

        Assert.Equal(expected: scenario.Ticks, actual: record.Ticks.Count);

        foreach (var component in scenario.Exercises) {
            Assert.True(condition: (Column(component: component, record: record).Distinct().Count() > 1), userMessage: $"{name} never moves {component}");
        }
    }
    [Fact]
    public void EveryPerTickComponentButTheDeclaredTopologiesIsExercisedBySomeShippedScenario() {
        var exercised = ShippedManifest().Scenarios.SelectMany(selector: static scenario => scenario.Exercises).ToHashSet(comparer: StringComparer.Ordinal);
        var expected = DeterminismStream.TickComponents.Where(predicate: static component => (component != WorldStateHashComponent.Topologies)).Select(selector: static component => component.ToString());

        Assert.Equal(expected: expected.Order(), actual: exercised.Order());
    }
    [Fact]
    public void AScenarioThatNeverMovesAComponentItExercisesIsRefusedByName() {
        Assert.False(condition: DeterminismRecorder.TryRecord(
            error: out var error,
            record: out var record,
            scenario: new DeterminismScenario(
                Cells: [new DeterminismCellWrite(Key: "$value", Row: "request", Tick: 10, Value: "1")],
                Exercises: ["Arena", "Search"],
                Intents: [],
                Name: "records-pools",
                Seats: [],
                Ticks: Ticks,
                World: World()
            )
        ));
        Assert.Null(@object: record);
        Assert.Contains(actualString: error, expectedSubstring: "exercises Search, but its Search hash never changed in 20 ticks");
    }
    // Nothing at run time redeclares a lattice, so no scenario can move the topologies: what holds instead is that a
    // world declaring one folds it (its hash is not a no-lattice world's) and that the fold holds still across a run.
    [Fact]
    public void TheDeclaredTopologiesAreFoldedAndHoldStillAcrossARun() {
        var declared = Column(component: nameof(WorldStateHashComponent.Topologies), record: RecordShipped(name: "board-enforcement"));
        var undeclared = Column(component: nameof(WorldStateHashComponent.Topologies), record: RecordShipped(name: "decisions"));

        Assert.Single(collection: declared.Distinct());
        Assert.Single(collection: undeclared.Distinct());
        Assert.NotEqual(expected: undeclared[0], actual: declared[0]);
    }
    /// <summary>Gets the shipped manifest's scenario names.</summary>
    /// <returns>One row per scenario.</returns>
    public static TheoryData<string> ShippedScenarios() => [.. ShippedManifest().Scenarios.Select(selector: static scenario => scenario.Name)];
}

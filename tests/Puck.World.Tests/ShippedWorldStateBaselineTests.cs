using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Replays each shipped world's committed scripted input sequence and compares the canonical state export
/// and the tick-cost record against the committed baselines under
/// <c>tests/Puck.World.Tests/ShippedWorldStateBaselines</c>.</summary>
/// <remarks>Setting <c>PUCK_RECORD_STATE_BASELINES=1</c> turns <see cref="RecordShippedWorldStateBaselines"/> from
/// a skip into the recorder that rewrites every export and cost file; it runs each sequence twice and refuses to
/// write when the two runs differ. <c>PUCK_RECORD_STATE_BASELINE_WALL_TIME=1</c> additionally rewrites the
/// machine-scoped advisory wall-time record, which is deliberately not rewritten by an ordinary re-record.</remarks>
public sealed class ShippedWorldStateBaselineTests {
    public static TheoryData<string> Names() => new(values: ShippedWorldStateBaselines.Names());
    [MemberData(nameof(Names))]
    [Theory]
    public void TheRecordedExportReproduces(string name) {
        var run = ShippedWorldStateBaselines.Run(name: name);
        var recorded = File.ReadAllBytes(path: ShippedWorldStateBaselines.PathOf(
            name: name,
            suffix: "state.json"
        ));

        Assert.Equal(
            expected: ShippedWorldStateBaselines.Text(bytes: recorded),
            actual: ShippedWorldStateBaselines.Text(bytes: run.Export)
        );
    }
    [MemberData(nameof(Names))]
    [Theory]
    public void TheRecordedTickCostReproduces(string name) {
        var run = ShippedWorldStateBaselines.Run(name: name);
        var recorded = File.ReadAllBytes(path: ShippedWorldStateBaselines.PathOf(
            name: name,
            suffix: "cost.json"
        ));

        Assert.Equal(
            expected: ShippedWorldStateBaselines.Text(bytes: recorded),
            actual: ShippedWorldStateBaselines.Text(bytes: run.Cost)
        );
    }
    // A sequence that leaves the world's state substrate exactly as it booted proves nothing about the rules it was
    // written to exercise. Each sequence declares whether it must move the world-scope state hash.
    [MemberData(nameof(Names))]
    [Theory]
    public void TheSequenceExercisesTheWorldsState(string name) {
        var run = ShippedWorldStateBaselines.Run(name: name);

        if (!run.ExpectsStateChange) {
            Assert.Equal(
                expected: run.BootWorldHash,
                actual: run.FinalWorldHash
            );

            return;
        }

        Assert.NotEqual(
            expected: run.BootWorldHash,
            actual: run.FinalWorldHash
        );
    }
    [Fact]
    public void RecordShippedWorldStateBaselines() {
        Assert.SkipUnless(
            condition: (Environment.GetEnvironmentVariable(variable: "PUCK_RECORD_STATE_BASELINES") == "1"),
            reason: "set PUCK_RECORD_STATE_BASELINES=1 to re-record the shipped-world state baselines"
        );
        ShippedWorldStateBaselines.Record(wallTime: (Environment.GetEnvironmentVariable(variable: "PUCK_RECORD_STATE_BASELINE_WALL_TIME") == "1"));
    }
}

/// <summary>Boots a shipped world, replays its committed scripted input sequence, and renders the canonical state
/// export and the tick-cost record the baselines pin.</summary>
internal static class ShippedWorldStateBaselines {
    private const string SequenceSuffix = ".sequence.json";

    private static WorldDefinition Compose(JsonObject sequence) {
        var host = sequence[propertyName: "host"]!.AsObject();

        return (host[propertyName: "kind"]!.GetValue<string>() switch {
            "document" => LoadDocument(
                relativePath: ("src/Puck.World/Assets/worlds/" + sequence[propertyName: "world"]!.GetValue<string>()),
                rewriteBasis: (host[propertyName: "rewriteBasis"]?.GetValue<string>())
            ),
            "fixture" => LoadDocument(relativePath: host[propertyName: "path"]!.GetValue<string>()),
            "spliced" => Splice(
                host: host,
                sequence: sequence
            ),
            var kind => throw new InvalidOperationException(message: $"unknown host kind '{kind}'"),
        });
    }
    private static JsonObject CostDocument(JsonObject sequence, WorldRuleWorkBudget boot, WorldRuleWorkBudget final, long ticks, uint rateHz) => new() {
        ["schema"] = "puck.world.state-baseline-cost.v1",
        ["world"] = sequence[propertyName: "world"]!.GetValue<string>(),
        ["simulationRateHz"] = rateHz,
        ["ticks"] = ticks,
        ["workUnits"] = new JsonObject {
            ["boot"] = Measured(budget: boot),
            ["final"] = Measured(budget: final),
        },
    };
    private static WorldDefinition LoadDocument(string relativePath, string? rewriteBasis = null) {
        var authored = Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: relativePath
        );
        var directory = (Path.GetDirectoryName(path: authored) ?? AuthoredGameFixtures.Root);
        // A document whose basis names a `.puck` source cannot be composed here: this project holds no transpiler.
        // The sequence names the generated twin instead, absolute so the copy still resolves it beside the original.
        var path = ((rewriteBasis is null)
            ? authored
            : RewrittenBasisCopy(
                authored: authored,
                basis: Path.Combine(
                    path1: directory,
                    path2: rewriteBasis
                )
            )
        );
        var neighbours = new GeneratedTwinNeighbourResolver(inner: new WorldFileNeighbourResolver(baseDirectory: () => directory));

        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                path,
                out var definition,
                out var reason,
                neighbours: neighbours,
                catalog: TestHookInstaller.CreateMachineCatalog()
            ),
            userMessage: $"{relativePath}: {reason}"
        );

        return definition!;
    }
    private static string MachineDescription() => string.Join(
        separator: "; ",
        values: [
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            $"{Environment.ProcessorCount} logical processor(s)",
            RuntimeInformation.FrameworkDescription,
        ]
    );
    private static JsonObject Measured(WorldRuleWorkBudget budget) => new() {
        ["ruleRows"] = budget.RuleRows,
        ["interactionRows"] = budget.InteractionRows,
        ["evaluationSlots"] = budget.EvaluationSlots,
        ["workUnitsPerTick"] = budget.WorkUnitsPerTick,
        ["flockAffinityWorkUnitsPerTick"] = budget.FlockAffinityWorkUnitsPerTick,
        ["decisionImagePointsPerTick"] = budget.DecisionImagePointsPerTick,
        ["decisionGridBuildsPerTick"] = budget.DecisionGridBuildsPerTick,
        ["decisionGridPointsPerTick"] = budget.DecisionGridPointsPerTick,
    };
    // The same shape a module import composes with: an array section concatenates, an object section merges
    // member-wise, anything else replaces. The host's own rows survive a module that declares rows beside them.
    private static JsonNode? Merged(JsonNode? existing, JsonNode addition) {
        if (
            (existing is JsonArray target) &&
            (addition is JsonArray extra)
        ) {
            var merged = target.DeepClone().AsArray();

            foreach (var row in extra) { merged.Add(value: row!.DeepClone()); }

            return merged;
        }
        if (
            (existing is JsonObject host) &&
            (addition is JsonObject added)
        ) {
            var merged = host.DeepClone().AsObject();

            foreach (var (member, value) in added) {
                merged[propertyName: member] = Merged(
                    addition: value!,
                    existing: merged[propertyName: member]
                );
            }

            return merged;
        }

        return addition.DeepClone();
    }
    // A seat joins through the same door a player's client uses, so the seat body a rule reads a channel off is
    // human-occupied: WorldServer.ReadChannelValue answers zero for an unoccupied seat.
    private static void Join(WorldFixture fixture, JsonObject step) {
        var slot = (step[propertyName: "seat"]!.GetValue<int>() - 1);
        var principal = WorldPrincipal.Seat(slot: slot);

        Assert.True(
            condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                principal,
                principal.Index,
                null,
                WorldProtocol.WireProtocolKey
            )).Accepted,
            userMessage: $"seat {(slot + 1)} did not join"
        );
    }
    private static void Pose(WorldFixture fixture, JsonObject step) {
        var bodyIndex = PlacementBody(
            fixture: fixture,
            id: step[propertyName: "placement"]!.GetValue<string>()
        );
        var body = fixture.Server.Body(index: bodyIndex)!;

        // A cell-addressed pose lands on the topology's own computed centre, in the topology's own fixed point,
        // keeping the body's current height and upright attitude the way a board return does.
        if (step[propertyName: "cell"] is { } cell) {
            var topology = WorldTopologyCompilation.Find(
                definition: fixture.Server.Definition,
                name: step[propertyName: "topology"]!.GetValue<string>()
            );

            Assert.NotNull(@object: topology);

            var centre = topology!.CellCentre(cell: cell.GetValue<int>());

            body.Pose(
                pitchRadians: FixedQ4816.Zero,
                position: new FixedVector3(
                    X: centre.X,
                    Y: body.FixedPosition.Y,
                    Z: centre.Z
                ),
                rollRadians: FixedQ4816.Zero,
                yawRadians: FixedQ4816.Zero
            );

            return;
        }
        body.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: step[propertyName: "x"]!.GetValue<float>(),
            y: step[propertyName: "y"]!.GetValue<float>(),
            yawRadians: 0f,
            z: step[propertyName: "z"]!.GetValue<float>()
        );
    }
    private static int PlacementBody(WorldFixture fixture, string id) {
        var placements = fixture.Server.Definition.Placements;
        var ordinal = -1;

        for (var index = 0; (index < placements.Count); index++) {
            if (string.Equals(
                a: placements[index].Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                ordinal = index;

                break;
            }
        }

        Assert.True(
            condition: (ordinal >= 0),
            userMessage: $"'{id}' names no declared placement"
        );

        var bodyIndex = fixture.Server.Population.BodyForPlacementOrdinal(ordinal: ordinal);

        Assert.True(
            condition: (bodyIndex >= 0),
            userMessage: $"'{id}' is not inhabited"
        );

        return bodyIndex;
    }
    // The timed-hold press WorldBody.PressChannel gives `body.press`: the channel reads held for the authored
    // seconds of simulation time and releases itself, so a charge-and-release gesture is two advances, not a
    // second step.
    private static void Press(WorldFixture fixture, JsonObject step) {
        var channels = fixture.Server.Definition.Channels;
        var name = step[propertyName: "channel"]!.GetValue<string>();
        var ordinal = -1;

        for (var index = 0; (index < channels.Count); index++) {
            if (string.Equals(
                a: channels[index].Name,
                b: name,
                comparisonType: StringComparison.Ordinal
            )) {
                ordinal = index;

                break;
            }
        }

        Assert.True(
            condition: (ordinal >= 0),
            userMessage: $"'{name}' names no declared channel"
        );

        var slot = (step[propertyName: "seat"]!.GetValue<int>() - 1);
        var body = fixture.Server.Body(index: slot);

        Assert.NotNull(@object: body);
        body!.PressChannel(
            authoredMaximum: FixedQ4816.FromDouble(value: 60d),
            holdSeconds: step[propertyName: "holdSeconds"]!.GetValue<float>(),
            ordinal: ordinal,
            value: FixedQ4816.FromDouble(value: step[propertyName: "value"]!.GetValue<double>())
        );
    }
    private static string RewrittenBasisCopy(string authored, string basis) {
        var tree = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: authored))!.AsObject();
        var copy = Path.Combine(
            path1: System.IO.Directory.CreateTempSubdirectory(prefix: "puck-state-baseline-").FullName,
            path2: Path.GetFileName(path: authored)
        );

        tree[propertyName: "basis"] = basis.Replace(
            newChar: '/',
            oldChar: '\\'
        );
        File.WriteAllBytes(
            bytes: Puck.Abstractions.Documents.CanonicalJsonDocument.Serialize(node: tree),
            path: copy
        );

        return copy;
    }
    private static JsonObject Sequence(string name) => JsonNode.Parse(utf8Json: File.ReadAllBytes(path: PathOf(
        name: name,
        suffix: "sequence.json"
    )))!.AsObject();
    private static WorldDefinition Splice(JsonObject host, JsonObject sequence) {
        var source = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(
            path1: AuthoredGameFixtures.Root,
            path2: ("src/Puck.World/Assets/worlds/" + sequence[propertyName: "world"]!.GetValue<string>())
        )))!.AsObject();
        var composed = JsonNode.Parse(utf8Json: Fixtures.DefaultWorldBytes())!.AsObject();

        if (host[propertyName: "stateRows"] is JsonArray extraRows) {
            var world = ((source[propertyName: "state"] ??= new JsonObject())[propertyName: "world"] ??= new JsonArray()).AsArray();

            foreach (var row in extraRows) { world.Add(value: row!.DeepClone()); }
        }
        var sections = ((host[propertyName: "sections"] is JsonArray named)
            ? named.Select(selector: static entry => entry!.GetValue<string>())
            : ["state", "rules", "patterns", "tables", "search"]
        );

        foreach (var section in sections) {
            if (source[propertyName: section] is { } value) {
                composed[propertyName: section] = Merged(
                    addition: value,
                    existing: composed[propertyName: section]
                );
            }
        }
        // A module's rules may name a vocabulary its own document does not declare, because the island declares it
        // in a sibling district. The sequence appends those rows rather than a second host document doing it.
        if (host[propertyName: "append"] is JsonObject appended) {
            foreach (var (section, rows) in appended) {
                var target = (composed[propertyName: section] ??= new JsonArray()).AsArray();

                foreach (var row in rows!.AsArray()) { target.Add(value: row!.DeepClone()); }
            }
        }

        return WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: composed.ToJsonString()));
    }
    private static void Write(string path, byte[] bytes) {
        if (
            File.Exists(path: path) &&
            File.ReadAllBytes(path: path).AsSpan().SequenceEqual(other: bytes)
        ) {
            return;
        }

        File.WriteAllBytes(
            bytes: bytes,
            path: path
        );
    }

    /// <summary>Gets the absolute path of the baseline directory.</summary>
    public static string Directory { get; } = Path.Combine(
        path1: AuthoredGameFixtures.Root,
        path2: "tests",
        path3: "Puck.World.Tests",
        path4: "ShippedWorldStateBaselines"
    );

    /// <summary>Returns every recorded world slug, in ordinal order, from the committed sequence files.</summary>
    public static IEnumerable<string> Names() => System.IO.Directory
        .GetFiles(
            path: Directory,
            searchPattern: ("*" + SequenceSuffix)
        )
        .Select(selector: path => Path.GetFileName(path: path)[..^SequenceSuffix.Length])
        .Order(comparer: StringComparer.Ordinal);
    /// <summary>Returns the absolute path of one baseline file.</summary>
    /// <param name="name">The world slug.</param>
    /// <param name="suffix">The file suffix after the slug's dot.</param>
    /// <returns>The absolute path.</returns>
    public static string PathOf(string name, string suffix) => Path.Combine(
        path1: Directory,
        path2: $"{name}.{suffix}"
    );
    /// <summary>Replays every sequence twice, refuses on any run-to-run difference, and rewrites the export and
    /// cost baselines.</summary>
    /// <param name="wallTime">Whether to additionally rewrite the advisory wall-time record.</param>
    public static void Record(bool wallTime) {
        var walls = new List<(string Name, BaselineRun Run)>();

        foreach (var name in Names()) {
            var first = Run(name: name);
            var second = Run(name: name);

            Assert.Equal(
                expected: Text(bytes: first.Export),
                actual: Text(bytes: second.Export)
            );
            Assert.Equal(
                expected: Text(bytes: first.Cost),
                actual: Text(bytes: second.Cost)
            );
            Write(
                bytes: first.Export,
                path: PathOf(
                    name: name,
                    suffix: "state.json"
                )
            );
            Write(
                bytes: first.Cost,
                path: PathOf(
                    name: name,
                    suffix: "cost.json"
                )
            );
            walls.Add(item: (name, second));
        }

        if (!wallTime) {
            return;
        }

        var report = new StringBuilder();

        report.Append(value: "# Advisory tick wall time\n\n");
        report.Append(value: "Wall time is advisory: it is machine- and load-dependent and is not part of the\n");
        report.Append(value: "byte-for-byte baseline. The work units in each `*.cost.json` are the exact record.\n\n");
        report.Append(value: $"Machine: {MachineDescription()}\n\n");
        report.Append(value: "| World | Ticks | Median tick (us) | Mean tick (us) |\n");
        report.Append(value: "|---|---|---|---|\n");

        foreach (var (name, run) in walls) {
            report.Append(value: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"| {name} | {run.Ticks} | {run.MedianTickMicroseconds:F1} | {run.MeanTickMicroseconds:F1} |\n"
            ));
        }
        Write(
            bytes: Encoding.UTF8.GetBytes(s: report.ToString()),
            path: Path.Combine(
                path1: Directory,
                path2: "wall-time.md"
            )
        );
    }
    /// <summary>Boots one world, replays its sequence, and renders its export and cost record.</summary>
    /// <param name="name">The world slug.</param>
    /// <returns>The completed run.</returns>
    public static BaselineRun Run(string name) {
        var sequence = Sequence(name: name);
        var definition = Compose(sequence: sequence);

        using var fixture = Fixtures.FreshServer(definition: definition);

        var booted = fixture.Server.Definition;
        var rateHz = ((uint)booted.SimulationRateHz);
        var stepTicks = ((rateHz == 0U)
            ? Fixtures.StepTicks
            : EngineTicks.PerRate(ratePerSecond: rateHz)
        );
        var bootBudget = WorldRuleWorkBudget.Measure(definition: booted);
        var bootHash = WorldStateHashComposition.Hash(
            scope: WorldStateHashScope.World,
            server: fixture.Server,
            tick: (fixture.Server.NextInputTick - 1UL)
        );
        var elapsed = new List<long>();

        foreach (var node in sequence[propertyName: "steps"]!.AsArray()) {
            var step = node!.AsObject();

            if (step[propertyName: "advance"] is { } advance) {
                var count = advance.GetValue<int>();

                for (var tick = 0; (tick < count); tick++) {
                    var before = Stopwatch.GetTimestamp();

                    fixture.Server.Advance(stepTicks: stepTicks);
                    elapsed.Add(item: (Stopwatch.GetTimestamp() - before));
                }

                continue;
            }
            if (step[propertyName: "pose"] is JsonObject pose) {
                Pose(
                    fixture: fixture,
                    step: pose
                );

                continue;
            }
            if (step[propertyName: "join"] is JsonObject join) {
                Join(
                    fixture: fixture,
                    step: join
                );

                continue;
            }
            if (step[propertyName: "press"] is JsonObject press) {
                Press(
                    fixture: fixture,
                    step: press
                );

                continue;
            }

            var write = ((step[propertyName: "set"] ?? step[propertyName: "add"])
                ?? throw new InvalidOperationException(message: $"{name}: a step must carry advance, set, add, pose, join or press")).AsObject();

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: write[propertyName: "row"]!.GetValue<string>(),
                Key: write[propertyName: "key"]!.GetValue<string>(),
                Value: write[propertyName: "value"]!.GetValue<long>(),
                Kind: ((step[propertyName: "set"] is null)
                    ? WorldDocumentWriteKind.Add
                    : WorldDocumentWriteKind.Set)
            ));
        }

        var finalDefinition = fixture.Server.Definition;
        var ordered = elapsed.Order().ToArray();
        var scale = (1_000_000.0 / Stopwatch.Frequency);

        return new BaselineRun(
            BootWorldHash: bootHash,
            Cost: Puck.Abstractions.Documents.CanonicalJsonDocument.Serialize(node: CostDocument(
                boot: bootBudget,
                final: WorldRuleWorkBudget.Measure(definition: finalDefinition),
                rateHz: rateHz,
                sequence: sequence,
                ticks: ordered.Length
            )),
            ExpectsStateChange: (sequence[propertyName: "expectsStateChange"]?.GetValue<bool>() ?? true),
            Export: WorldStateExport.ToCanonicalJson(server: fixture.Server),
            FinalWorldHash: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: fixture.Server,
                tick: (fixture.Server.NextInputTick - 1UL)
            ),
            MeanTickMicroseconds: ((ordered.Length == 0)
                ? 0.0
                : ((ordered.Sum() * scale) / ordered.Length)),
            MedianTickMicroseconds: ((ordered.Length == 0)
                ? 0.0
                : (ordered[(ordered.Length / 2)] * scale)),
            Ticks: ordered.Length
        );
    }
    /// <summary>Returns a baseline file's bytes as text with its line endings normalized, so a comparison failure
    /// prints the differing JSON rather than a byte count.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The normalized text.</returns>
    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes: bytes).ReplaceLineEndings(replacementText: "\n");
}
/// <summary>Redirects a <c>.puck</c> neighbour locator to the generated <c>.world.json</c> twin
/// <c>build/WorldAssets.targets</c> writes beside it, which this project can read without the transpiler.</summary>
/// <param name="inner">The resolver the redirected locator is read through.</param>
internal sealed class GeneratedTwinNeighbourResolver(IWorldNeighbourResolver inner) : IWorldNeighbourResolver {
    private const string SourceExtension = ".puck";

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) => inner.Resolve(document: (document.EndsWith(
        comparisonType: StringComparison.Ordinal,
        value: SourceExtension
    )
        ? (document[..^SourceExtension.Length] + ".world.json")
        : document));
}
/// <summary>One replayed sequence's results.</summary>
/// <param name="BootWorldHash">The world-scope state hash before the first step.</param>
/// <param name="Cost">The canonical tick-cost record.</param>
/// <param name="ExpectsStateChange">Whether the sequence declares that it must move the world-scope state hash.</param>
/// <param name="Export">The canonical state export.</param>
/// <param name="FinalWorldHash">The world-scope state hash after the last step.</param>
/// <param name="MeanTickMicroseconds">The advisory mean wall time of one simulation tick.</param>
/// <param name="MedianTickMicroseconds">The advisory median wall time of one simulation tick.</param>
/// <param name="Ticks">How many simulation ticks the sequence advanced.</param>
internal readonly record struct BaselineRun(
    ulong BootWorldHash,
    byte[] Cost,
    bool ExpectsStateChange,
    byte[] Export,
    ulong FinalWorldHash,
    double MeanTickMicroseconds,
    double MedianTickMicroseconds,
    int Ticks
);

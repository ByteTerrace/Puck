using Puck.Commands;
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
/// <remarks>Each law writes the fresh export or cost record beside the test assembly
/// (<see cref="Puck.Testing.TestRecords"/>) before comparing. <c>puck baselines state</c> runs this class twice,
/// refuses when the two runs' records differ, and promotes them over the committed files.</remarks>
public sealed class ShippedWorldStateBaselineTests {
    public static TheoryData<string> Names() => new(values: ShippedWorldStateBaselines.Names());

    // The export and the cost record are separate laws so a state regression and a cost regression name themselves;
    // both read the one cached run.
    private static void AssertReproducesRecorded(string name, string suffix, byte[] actual) {
        var committed = ShippedWorldStateBaselines.PathOf(
            name: name,
            suffix: suffix
        );

        _ = Puck.Testing.TestRecords.Write(
            artifact: ShippedWorldStateBaselines.RecordArtifact,
            bytes: actual,
            fileName: Path.GetFileName(path: committed)
        );
        Assert.Equal(
            expected: ShippedWorldStateBaselines.Text(bytes: File.ReadAllBytes(path: committed)),
            actual: ShippedWorldStateBaselines.Text(bytes: actual)
        );
    }

    [MemberData(nameof(Names))]
    [Theory]
    public void TheRecordedExportReproduces(string name) => AssertReproducesRecorded(
        actual: ShippedWorldStateBaselines.Run(name: name).Export,
        name: name,
        suffix: "state.json"
    );
    [MemberData(nameof(Names))]
    [Theory]
    public void TheRecordedTickCostReproduces(string name) => AssertReproducesRecorded(
        actual: ShippedWorldStateBaselines.Run(name: name).Cost,
        name: name,
        suffix: "cost.json"
    );
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
}

/// <summary>Boots a shipped world, replays its committed scripted input sequence, and renders the canonical state
/// export and the tick-cost record the baselines pin.</summary>
internal static class ShippedWorldStateBaselines {
    private const string SequenceSuffix = ".sequence.json";

    /// <summary>The <c>puck baselines</c> artifact name of the export and cost records.</summary>
    public const string RecordArtifact = "state";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<BaselineRun>> Runs = new(comparer: StringComparer.Ordinal);

    private static WorldDefinition Compose(JsonObject sequence) {
        var host = sequence[propertyName: "host"]!.AsObject();

        return (host[propertyName: "kind"]!.GetValue<string>() switch {
            "document" => LoadDocument(relativePath: ("src/Puck.World/Assets/worlds/" + sequence[propertyName: "world"]!.GetValue<string>())),
            "fixture" => LoadDocument(relativePath: host[propertyName: "path"]!.GetValue<string>()),
            "source" => AuthoredGameFixtures.Load(relativePath: host[propertyName: "path"]!.GetValue<string>()),
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
    // The document is compiled when it is a `.puck` source and composed the way the host boots it, so a basis or
    // import naming a `.puck`-sourced document resolves to that source. A supplied catalog keeps the load uncached:
    // every replay boots its own definition.
    private static WorldDefinition LoadDocument(string relativePath) => AuthoredGameFixtures.Load(
        catalog: TestHookInstaller.CreateMachineCatalog(),
        relativePath: relativePath
    );
    private static JsonObject Measured(WorldRuleWorkBudget budget) => new() {
        ["ruleRows"] = budget.RuleRows,
        ["interactionRows"] = budget.InteractionRows,
        ["evaluationSlots"] = budget.EvaluationSlots,
        ["workUnitsPerTick"] = budget.WorkUnitsPerTick.Units,
        ["flockAffinityWorkUnitsPerTick"] = budget.FlockAffinityWorkUnitsPerTick.Units,
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
        var principal = Principal.Seat(slot: slot);

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
    private static JsonObject Sequence(string name) => JsonNode.Parse(utf8Json: File.ReadAllBytes(path: PathOf(
        name: name,
        suffix: "sequence.json"
    )))!.AsObject();
    private static WorldDefinition Splice(JsonObject host, JsonObject sequence) {
        var source = JsonNode.Parse(utf8Json: Puck.Testing.ShippedWorldDocuments.Read(path: Path.Combine(
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
    /// <summary>Returns one world's replay, replaying its sequence on the first request and sharing that run with
    /// every later one: a replay is a pure function of the committed sequence, so every law reading the same world
    /// reads the same run.</summary>
    /// <param name="name">The world slug.</param>
    /// <returns>The completed run.</returns>
    public static BaselineRun Run(string name) => Runs.GetOrAdd(
        key: name,
        value: new Lazy<BaselineRun>(valueFactory: () => Replay(name: name))
    ).Value;
    /// <summary>Boots one world, replays its sequence, and renders its export and cost record.</summary>
    /// <param name="name">The world slug.</param>
    /// <returns>The completed run.</returns>
    public static BaselineRun Replay(string name) {
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
        var ticks = 0;

        foreach (var node in sequence[propertyName: "steps"]!.AsArray()) {
            var step = node!.AsObject();

            if (step[propertyName: "advance"] is { } advance) {
                var count = advance.GetValue<int>();

                for (var tick = 0; (tick < count); tick++) {
                    fixture.Server.Advance(stepTicks: stepTicks);
                }

                ticks += count;

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
                Principal: Principal.Console,
                Row: write[propertyName: "row"]!.GetValue<string>(),
                Key: write[propertyName: "key"]!.GetValue<string>(),
                Value: write[propertyName: "value"]!.GetValue<long>(),
                Kind: ((step[propertyName: "set"] is null)
                    ? WorldDocumentWriteKind.Add
                    : WorldDocumentWriteKind.Set)
            ));
        }

        var finalDefinition = fixture.Server.Definition;

        return new BaselineRun(
            BootWorldHash: bootHash,
            Cost: Puck.Abstractions.Documents.CanonicalJsonDocument.Serialize(node: CostDocument(
                boot: bootBudget,
                final: WorldRuleWorkBudget.Measure(definition: finalDefinition),
                rateHz: rateHz,
                sequence: sequence,
                ticks: ticks
            )),
            ExpectsStateChange: (sequence[propertyName: "expectsStateChange"]?.GetValue<bool>() ?? true),
            Export: WorldStateExport.ToCanonicalJson(server: fixture.Server),
            FinalWorldHash: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.World,
                server: fixture.Server,
                tick: (fixture.Server.NextInputTick - 1UL)
            )
        );
    }
    /// <summary>Returns a baseline file's bytes as text with its line endings normalized, so a comparison failure
    /// prints the differing JSON rather than a byte count.</summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The normalized text.</returns>
    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes: bytes).ReplaceLineEndings(replacementText: "\n");
}
/// <summary>One replayed sequence's results.</summary>
/// <param name="BootWorldHash">The world-scope state hash before the first step.</param>
/// <param name="Cost">The canonical tick-cost record.</param>
/// <param name="ExpectsStateChange">Whether the sequence declares that it must move the world-scope state hash.</param>
/// <param name="Export">The canonical state export.</param>
/// <param name="FinalWorldHash">The world-scope state hash after the last step.</param>
internal readonly record struct BaselineRun(
    ulong BootWorldHash,
    byte[] Cost,
    bool ExpectsStateChange,
    byte[] Export,
    ulong FinalWorldHash
);

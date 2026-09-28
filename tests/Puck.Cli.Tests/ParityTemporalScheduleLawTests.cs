using System.Text.Json.Nodes;
using Puck.Cli.Parity;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Parity's temporal interval is an ordinary absolute schedule with an applied-result proof, separated
/// from every ordinary station by the document's own capture coordinates.</summary>
public sealed class ParityTemporalScheduleLawTests : IDisposable {
    private readonly string m_directory = Directory.CreateTempSubdirectory(prefix: "puck-parity-temporal-").FullName;
    private static WorldDefinition Source() => WorldDefinitionSerialization.Deserialize(utf8Json:
        File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: "tests/Puck.Parity/parity.world.json")));

    [Fact]
    public void TheToggleIsStrictlyBetweenOrdinaryAndTemporalCapturesWithoutChangingStateOrGrants() {
        var source = Source();
        var staged = ParityCommand.WithTemporalSchedule(definition: source);
        var toggle = Assert.Single(collection: staged.Schedule!.Rows);
        Assert.All(collection: source.Captures!.Rows, action: row => Assert.All(collection: row.Ticks, action: tick =>
            Assert.True(condition: ((row.Converge == 0) ? (tick < toggle.Tick) : (tick > toggle.Tick)))));
        Assert.Equal(expected: "seat1", actual: toggle.Principal);
        Assert.Equal(expected: "world.temporal on", actual: toggle.Command);
        Assert.Equal(expected: WorldDefinitionSerialization.Serialize(definition: source),
            actual: WorldDefinitionSerialization.Serialize(definition: staged with { Schedule = null }));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AnOwnedScheduleOrUnseparatedIntervalIsRefused(int defect) {
        var source = Source();
        var ordinary = source.Captures!.Rows.First(predicate: static row => (row.Converge == 0));
        var temporal = source.Captures.Rows.First(predicate: static row => (row.Converge > 0));
        source = defect switch {
            0 => source with { Schedule = new WorldScheduleSection(SettleTicks: 1, Rows: []) },
            1 => source with { Captures = new WorldCapturesSection(Rows: [ordinary with { Ticks = [10UL] }, temporal with { Ticks = [10UL] }]) },
            2 => source with { Captures = new WorldCapturesSection(Rows: [ordinary with { Ticks = [10UL] }, temporal with { Ticks = [11UL] }]) },
            _ => source with { RenderRaw = source.Render with { Temporal = true } },
        };
        Assert.Throws<InvalidDataException>(testCode: () => ParityCommand.WithTemporalSchedule(definition: source));
    }
    [Fact]
    public void MissingOrRejectedScheduleEvidenceCannotMasqueradeAsAnAppliedToggle() {
        var staged = ParityCommand.WithTemporalSchedule(definition: Source());
        var world = Path.Combine(path1: m_directory, path2: "world.world.json");
        File.WriteAllBytes(path: world, bytes: WorldDefinitionSerialization.Serialize(definition: staged));
        string? Refusal() => ParityCommand.TemporalScheduleRefusal(worldPath: world, scheduleDirectory: m_directory);
        Assert.NotNull(@object: Refusal());
        var toggle = staged.Schedule!.Rows[0];
        var manifest = new JsonObject {
            ["schema"] = WorldScheduleSection.ManifestSchemaId,
            ["truncated"] = false,
            ["authoredExportTick"] = staged.Schedule.ExportTick,
            ["exportTick"] = staged.Schedule.ExportTick,
            ["submissions"] = new JsonArray(new JsonObject {
                ["tick"] = toggle.Tick, ["principal"] = toggle.Principal, ["command"] = toggle.Command,
                ["outcome"] = WorldScheduleSection.OutcomeSubmitted, ["detail"] = "[world.temporal: on]",
            }),
            ["echoes"] = new JsonArray(),
        };
        void Write(JsonNode value) => File.WriteAllText(path: Path.Combine(path1: m_directory, path2: WorldScheduleSection.ManifestFileName), contents: value.ToJsonString());
        Write(value: manifest);
        Assert.Null(@object: Refusal());
        foreach (var defect in new Action<JsonNode>[] {
            value => value["submissions"]![0]!["tick"] = (toggle.Tick + 1UL),
            value => value["submissions"]![0]!["detail"] = "[world.temporal: off]",
            value => value["submissions"]![0]!["outcome"] = WorldScheduleSection.OutcomeRefused,
            value => value["submissions"] = new JsonArray(),
            value => value["truncated"] = true,
            value => value["echoes"] = new JsonArray(new JsonObject { ["rejected"] = true, ["message"] = "seat1 cannot mutate section:render" }),
        }) {
            var invalid = manifest.DeepClone();
            defect(obj: invalid);
            Write(value: invalid);
            Assert.NotNull(@object: Refusal());
        }
    }
    public void Dispose() => Directory.Delete(path: m_directory, recursive: true);
}

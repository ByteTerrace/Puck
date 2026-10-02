using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;

namespace Puck.Cli.Parity;

internal static partial class ParityCommand {
    // Only the staged parity copy receives this presentation schedule. The source document and every authored
    // simulation/capture row remain unchanged, and compilation still keys the final staged definition normally.
    internal static WorldDefinition WithTemporalSchedule(WorldDefinition definition) {
        var rows = (definition.Captures?.Rows ?? []);
        var temporal = rows.Where(predicate: static row => (row.Converge > 0)).ToArray();

        if (temporal.Length == 0) {
            return definition;
        }
        if (definition.Schedule is not null) {
            throw new InvalidDataException(message: "the parity world already owns a schedule; its temporal interval cannot replace authored commands");
        }
        if (definition.Render.Temporal) {
            throw new InvalidDataException(message: "ordinary parity stations must start with temporal reconstruction off");
        }
        var ordinary = rows.Where(predicate: static row => (row.Converge == 0)).SelectMany(selector: static row => row.Ticks).ToArray();
        var firstTemporal = temporal.SelectMany(selector: static row => row.Ticks).Min();
        var lastOrdinary = ordinary.DefaultIfEmpty().Max();

        if ((lastOrdinary == ulong.MaxValue) || ((lastOrdinary + 1UL) >= firstTemporal)) {
            throw new InvalidDataException(message: "the parity world's ordinary and temporal intervals overlap or leave no tick for the temporal toggle");
        }
        return definition with {
            Schedule = new WorldScheduleSection(
                SettleTicks: 1,
                Rows: [new WorldScheduleRow(Tick: (lastOrdinary + 1UL), Principal: "seat1", Command: "world.temporal on")]
            ),
        };
    }

    private static string StageTemporalTree(string tree, string runDirectory) {
        var staged = Path.Combine(path1: runDirectory, path2: "source");

        foreach (var file in Directory.EnumerateFiles(path: tree, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var destination = Path.Combine(path1: staged, path2: Path.GetRelativePath(path: file, relativeTo: tree));

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(destFileName: destination, overwrite: true, sourceFileName: file);
        }
        var path = Path.Combine(path1: staged, path2: Path.GetFileName(path: WorldPath));
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path), documentDirectory: staged);
        var prepared = WithTemporalSchedule(definition: definition);

        if (!ReferenceEquals(objA: prepared, objB: definition)) {
            // Change only the schedule member in the staged JSON. Reserializing the whole definition would expand
            // defaults and authored sugar unrelated to the presentation interval this runner owns.
            var document = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: path))!.AsObject();
            var canonical = JsonNode.Parse(utf8Json: WorldDefinitionSerialization.Serialize(definition: prepared))!;

            document[propertyName: "schedule"] = canonical[propertyName: "schedule"]!.DeepClone();
            File.WriteAllText(path: path, contents: document.ToJsonString());
        }
        return staged;
    }

    // A submitted lever can still be denied by its section grant, so the scheduler's ingress outcome alone proves
    // nothing. Require its exact post-apply echo and reject every recorded local edit refusal as well.
    internal static string? TemporalScheduleRefusal(string worldPath, string scheduleDirectory) {
        try {
            var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: worldPath));

            if (definition.Schedule is not { } schedule) {
                return null;
            }
            var path = Path.Combine(path1: scheduleDirectory, path2: WorldScheduleSection.ManifestFileName);
            using var manifest = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: path));
            var root = manifest.RootElement;

            if ((root.GetProperty(propertyName: "schema").GetString() != WorldScheduleSection.ManifestSchemaId) ||
                root.GetProperty(propertyName: "truncated").GetBoolean() ||
                (root.GetProperty(propertyName: "authoredExportTick").GetUInt64() != schedule.ExportTick) ||
                (root.GetProperty(propertyName: "exportTick").GetUInt64() != schedule.ExportTick)) {
                return "the temporal schedule did not finish at its authored export tick";
            }
            var submissions = root.GetProperty(propertyName: "submissions");

            if ((schedule.Rows.Count != 1) || (submissions.GetArrayLength() != 1)) {
                return "the temporal schedule did not record exactly its one toggle";
            }
            var row = schedule.Rows[0];
            var submitted = submissions[0];

            if ((submitted.GetProperty(propertyName: "tick").GetUInt64() != row.Tick) ||
                (submitted.GetProperty(propertyName: "principal").GetString() != row.Principal) ||
                (submitted.GetProperty(propertyName: "command").GetString() != row.Command) ||
                (submitted.GetProperty(propertyName: "outcome").GetString() != WorldScheduleSection.OutcomeSubmitted) ||
                !submitted.TryGetProperty(propertyName: "detail", value: out var detail) ||
                (detail.GetString() != "[world.temporal: on]")) {
                return "the temporal toggle did not apply at its authored tick with the exact on echo";
            }
            foreach (var echo in root.GetProperty(propertyName: "echoes").EnumerateArray()) {
                if (echo.GetProperty(propertyName: "rejected").GetBoolean()) {
                    return $"the temporal schedule recorded a rejected edit: {echo.GetProperty(propertyName: "message").GetString()}";
                }
            }
            return null;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or NotSupportedException)) {
            return $"the temporal schedule evidence could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
        }
    }
}

using System.Text.Json.Nodes;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    private readonly ConditionalWeakTable<byte[], WorldDefinitionAdmission> m_checkpointAdmissions = new();

    private WorldDefinitionAdmission CheckpointAdmission(byte[] json) => m_checkpointAdmissions.GetValue(json,
        bytes => WorldDefinitionSerialization.DeserializeForAdmission(documentDirectory: Host.Configuration.DocumentDirectory,
            utf8Json: bytes, machines: Host.Machines.ValidationCatalog));

    /// <summary>A computed identity for the host composition. Scenario rows, rules and commands are installed after restore.</summary>
    public static string ScenarioBootShape(byte[] json) => ScenarioShape(json, includeScenario: false);

    private static string ScenarioShape(byte[] json, bool includeScenario, bool includeHoldings = false) {
        using var document = JsonDocument.Parse(json);
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(mode: CryptoStreamMode.Write, stream: Stream.Null, transform: hash);

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) {
                if (property.Name == "schedule") { continue; }
                if (!includeScenario && (property.Name == "rules")) { continue; }
                if (!includeScenario && !includeHoldings && (property.Name == "placements")) {
                    // Admission derives these bits from scenario seeds. They change colliders, not native lifetimes.
                    var placements = JsonNode.Parse(property.Value.GetRawText())!.AsObject();

                    if (placements["rows"] is JsonArray rows) {
                        foreach (var row in rows) { row!.AsObject().Remove(propertyName: "holding"); }
                    }
                    writer.WritePropertyName(propertyName: property.Name);
                    placements.WriteTo(writer);
                } else if (!includeScenario && (property.Name == "state")) {
                    WriteStateComposition(writer, property.Value, document.RootElement);
                } else { property.WriteTo(writer: writer); }
            }
            writer.WriteEndObject();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(inArray: hash.Hash!);
    }
    // Body registers, enums and field lattices belong to the composed population. Ordinary document rows are
    // scenario seeds, except cells that choose the boot census: changing those requires a different population.
    private static void WriteStateComposition(Utf8JsonWriter writer, JsonElement state, JsonElement root) {
        var census = new HashSet<string>(comparer: StringComparer.Ordinal);

        if (root.TryGetProperty(propertyName: "placements", value: out var placements) && placements.TryGetProperty(propertyName: "rows", value: out var rows)) {
            foreach (var placement in rows.EnumerateArray()) {
                if (placement.TryGetProperty(propertyName: "inhabit", value: out var inhabit) && (inhabit.ValueKind == JsonValueKind.Object) &&
                    inhabit.TryGetProperty(propertyName: "count", value: out var count) && (count.ValueKind == JsonValueKind.Object) &&
                    count.TryGetProperty(propertyName: "row", value: out var row) && (row.GetString() is { } name)) { census.Add(item: name); }
            }
        }
        writer.WritePropertyName(propertyName: "state");
        writer.WriteStartObject();
        foreach (var section in state.EnumerateObject()) {
            if (section.Name != "world") { section.WriteTo(writer: writer); continue; }
            writer.WriteStartArray(propertyName: "world");
            foreach (var row in section.Value.EnumerateArray()) {
                if (row.TryGetProperty(propertyName: "name", value: out var name) && census.Contains(item: name.GetString()!)) { row.WriteTo(writer: writer); }
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    private readonly ConditionalWeakTable<byte[], BootShape> m_bootShapes = new();
    private readonly ConditionalWeakTable<WorldDefinition, BootShape> m_definitionShapes = new();

    private sealed record BootShape(string Value);

    private string CachedBootShape(byte[] json) => m_bootShapes.GetValue(json, static bytes => new BootShape(Value: ScenarioShape(includeScenario: true, json: bytes))).Value;

    private readonly ConditionalWeakTable<WorldAuthorityCheckpoint, StateArena> m_bootArenas = new();

    /// <summary>Installs scenario seeds and programs over the pristine host checkpoint, never over the preceding scenario.</summary>
    public WorldAuthorityCheckpoint ScenarioCheckpoint(WorldAuthorityCheckpoint boot, WorldDefinitionAdmission admission) {
        var json = WorldDefinitionSerialization.Serialize(definition: admission.Definition);

        if (ScenarioBootShape(json: json) != ScenarioBootShape(json: boot.Server.DefinitionJson)) {
            var expected = JsonNode.Parse(boot.Server.DefinitionJson)!.AsObject();
            var actual = JsonNode.Parse(json)!.AsObject();
            var changed = expected.Select(selector: pair => pair.Key).Union(second: actual.Select(selector: pair => pair.Key))
                .Where(predicate: key => ((key is not ("schedule" or "rules")) && !JsonNode.DeepEquals(node1: expected[key], node2: actual[key])));

            throw new InvalidOperationException(message: $"Scenario changes boot configuration ({string.Join(separator: ", ", values: changed)}); it requires its own worker.");
        }
        m_checkpointAdmissions.Add(key: json, value: admission);
        _ = m_definitionShapes.GetValue(admission.Definition, _ => new BootShape(Value: CachedBootShape(json: json)));
        var arena = boot.Server.Arena;

        if (CachedBootShape(json: json) != CachedBootShape(json: boot.Server.DefinitionJson)) {
            var pristine = m_bootArenas.GetValue(boot, image => PrepareCheckpointArena(image.Server,
                CheckpointAdmission(json: image.Server.DefinitionJson).Compilation));
            var definition = admission.Definition;
            var time = new ArenaTime(Tick: 0, EngineTick: 0, TicksPerSecond: definition.SimulationRateHz, Dynamics: definition.Dynamics);

            if (!StateArena.TryCreate(catalog: definition.StateCatalog, section: definition.StateRaw,
                options: WorldSlotLanes.Options(definition: definition), time: in time, arena: out var seeded, reason: out var reason)) {
                throw new InvalidOperationException(message: reason);
            }
            pristine.CopyLanesTo(target: seeded);
            arena = seeded.CaptureSnapshot();
        }
        return boot with {
            Server = boot.Server with { DefinitionJson = json, BaseDefinitionJson = json, Arena = arena },
        };
    }
    /// <summary>Restarts module memory and restores a checkpoint into an already composed compatible authority.</summary>
    public void RestoreScenario(WorldAuthorityCheckpoint checkpoint) {
        lock (Host.AuthorityGate) {
            var admission = CheckpointAdmission(json: checkpoint.Server.DefinitionJson);
            var definition = admission.Definition;
            // A scenario may edit a kit, screen, input policy or collision row. Restore the derived products
            // before validating and loading their checkpointed continuation into the same authority.
            var currentShape = m_definitionShapes.GetValue(Host.Configuration,
                current => new BootShape(Value: ScenarioShape(json: WorldDefinitionSerialization.Serialize(definition: current), includeScenario: true))).Value;

            if ((currentShape != CachedBootShape(json: checkpoint.Server.DefinitionJson)) &&
                (ScenarioShape(json: WorldDefinitionSerialization.Serialize(definition: Host.Configuration), includeScenario: false, includeHoldings: true) !=
                    ScenarioShape(json: checkpoint.Server.DefinitionJson, includeScenario: false, includeHoldings: true))) {
                if (!WorldDocument.TryBuildSolids(definition: definition, reason: out var solidReason, solids: out var solids)) { throw new InvalidOperationException(message: solidReason); }
                if (!Host.Machines.TryPrepare(Host.Configuration, definition, out var machines, out var machineReason)) { throw new InvalidOperationException(message: machineReason); }
                if (machines is not null) { Host.Machines.Commit(plan: machines); Host.Machines.Finish(plan: machines); }
                Host.Document.RestoreSolidField(solids, checkpoint.Server.SolidRevision);
                Host.Document.Install(definition, rebuildPopulation: true, compilation: admission.Compilation);
            }
            IWorldAddonPreparedPlan? plan = null;

            if (Host.Addons is { } addons) {
                if (!addons.TryPrepare(candidate: definition, current: null, plan: out plan, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
                if (plan is not null) { addons.Commit(plan: plan); addons.Finish(plan: plan); }
            }
            RestoreCheckpointCore(checkpoint, admission, resetSessionEpochs: true);
            // The checkpoint carries the shared boot's gate lookup; its scenario rows may declare different
            // gates or seeds. Commands arrive before the first tick can reconcile a changed cell.
            Host.GrantTable.SyncState(definition: definition, arena: Host.Arena);
            Host.ResetClockSeeds(definition: definition);
        }
    }
}

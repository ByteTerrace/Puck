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

    /// <summary>A computed identity for the entire boot state; only the command schedule may differ.</summary>
    public static string ScenarioBootShape(byte[] json) {
        using var document = JsonDocument.Parse(json);
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(mode: CryptoStreamMode.Write, stream: Stream.Null, transform: hash);

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) {
                if (property.Name != "schedule") { property.WriteTo(writer: writer); }
            }
            writer.WriteEndObject();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(inArray: hash.Hash!);
    }

    private readonly ConditionalWeakTable<byte[], BootShape> m_bootShapes = new();
    private readonly ConditionalWeakTable<WorldDefinition, BootShape> m_definitionShapes = new();

    private sealed record BootShape(string Value);

    private string CachedBootShape(byte[] json) => m_bootShapes.GetValue(json, static bytes => new BootShape(Value: ScenarioBootShape(json: bytes))).Value;

    /// <summary>Reuses a complete boot checkpoint only for an identical world with a different schedule.</summary>
    public WorldAuthorityCheckpoint ScenarioCheckpoint(WorldAuthorityCheckpoint boot, WorldDefinitionAdmission admission) {
        var json = WorldDefinitionSerialization.Serialize(definition: admission.Definition);

        if (CachedBootShape(json: json) != CachedBootShape(json: boot.Server.DefinitionJson)) {
            var expected = JsonNode.Parse(boot.Server.DefinitionJson)!.AsObject();
            var actual = JsonNode.Parse(json)!.AsObject();
            var changed = expected.Select(selector: pair => pair.Key).Union(second: actual.Select(selector: pair => pair.Key))
                .Where(predicate: key => ((key != "schedule") && !JsonNode.DeepEquals(node1: expected[key], node2: actual[key])));

            throw new InvalidOperationException(message: $"Scenario changes boot configuration ({string.Join(separator: ", ", values: changed)}); it requires its own worker.");
        }
        m_checkpointAdmissions.Add(key: json, value: admission);
        _ = m_definitionShapes.GetValue(admission.Definition, _ => new BootShape(Value: CachedBootShape(json: json)));
        return boot with {
            Server = boot.Server with { DefinitionJson = json, BaseDefinitionJson = json },
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
                current => new BootShape(Value: ScenarioBootShape(json: WorldDefinitionSerialization.Serialize(definition: current)))).Value;

            if (currentShape != CachedBootShape(json: checkpoint.Server.DefinitionJson)) {
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
        }
    }
}

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

    /// <summary>A computed identity for the boot configuration, excluding scenario state, rules and schedule.</summary>
    public static string ScenarioBootShape(byte[] json) {
        using var document = JsonDocument.Parse(json);
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(mode: CryptoStreamMode.Write, stream: Stream.Null, transform: hash);

        using (var writer = new Utf8JsonWriter(stream)) {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()) {
                if (property.Name is not ("state" or "rules" or "schedule")) { property.WriteTo(writer: writer); }
            }
            writer.WriteEndObject();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(inArray: hash.Hash!);
    }
    /// <summary>Seeds a scenario's arena over a captured boot, preserving the boot's participant lanes.</summary>
    public WorldAuthorityCheckpoint ScenarioCheckpoint(WorldAuthorityCheckpoint boot, WorldDefinition definition) {
        var json = WorldDefinitionSerialization.Serialize(definition: definition);

        if (ScenarioBootShape(json: json) != ScenarioBootShape(json: boot.Server.DefinitionJson)) {
            var expected = JsonNode.Parse(boot.Server.DefinitionJson)!.AsObject();
            var actual = JsonNode.Parse(json)!.AsObject();
            var changed = expected.Select(selector: pair => pair.Key).Union(second: actual.Select(selector: pair => pair.Key))
                .Where(predicate: key => ((key is not ("state" or "rules" or "schedule")) && !JsonNode.DeepEquals(node1: expected[key], node2: actual[key])));

            throw new InvalidOperationException(message: $"Scenario changes boot configuration ({string.Join(separator: ", ", values: changed)}); it requires its own worker.");
        }
        var admission = CheckpointAdmission(json: boot.Server.DefinitionJson);

        var lanes = PrepareCheckpointArena(boot.Server, admission.Compilation);
        var time = new ArenaTime(Tick: 0, EngineTick: 0, TicksPerSecond: definition.SimulationRateHz, Dynamics: definition.Dynamics);

        if (!StateArena.TryCreate(catalog: definition.StateCatalog, section: definition.StateRaw,
            options: WorldSlotLanes.Options(definition: definition), time: in time, arena: out var arena, reason: out var reason)) {
            throw new InvalidOperationException(message: reason);
        }
        lanes.CopyLanesTo(target: arena);
        return boot with {
            Server = boot.Server with { DefinitionJson = json, BaseDefinitionJson = json, Arena = arena.CaptureSnapshot() },
        };
    }
    /// <summary>Restarts module memory and restores a checkpoint into an already composed compatible authority.</summary>
    public void RestoreScenario(WorldAuthorityCheckpoint checkpoint) {
        lock (Host.AuthorityGate) {
            var admission = CheckpointAdmission(json: checkpoint.Server.DefinitionJson);
            var definition = admission.Definition;
            // A scenario may edit a kit, screen, input policy or collision row. Restore the derived products
            // before validating and loading their checkpointed continuation into the same authority.
            if (ScenarioBootShape(json: WorldDefinitionSerialization.Serialize(definition: Host.Configuration)) != ScenarioBootShape(json: checkpoint.Server.DefinitionJson)) {
                if (!WorldDocument.TryBuildSolids(definition: definition, reason: out var solidReason, solids: out var solids)) { throw new InvalidOperationException(message: solidReason); }
                if (!Host.Machines.TryPrepare(Host.Configuration, definition, out var machines, out var machineReason)) { throw new InvalidOperationException(message: machineReason); }
                if (machines is not null) { Host.Machines.Commit(plan: machines); Host.Machines.Finish(plan: machines); }
                Host.Document.RestoreSolidField(solids, checkpoint.Server.SolidRevision);
                Host.Document.Install(definition, rebuildPopulation: true);
            }
            IWorldAddonPreparedPlan? plan = null;

            if (Host.Addons is { } addons) {
                if (!addons.TryPrepare(candidate: definition, current: null, plan: out plan, reason: out var reason)) { throw new InvalidOperationException(message: reason); }
                if (plan is not null) { addons.Commit(plan: plan); addons.Finish(plan: plan); }
            }
            RestoreCheckpointCore(checkpoint, admission);
        }
    }
}

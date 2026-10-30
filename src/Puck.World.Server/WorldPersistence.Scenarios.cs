using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    private readonly ConditionalWeakTable<byte[], WorldDefinitionAdmission> m_checkpointAdmissions = new();

    private WorldDefinitionAdmission CheckpointAdmission(byte[] json) => m_checkpointAdmissions.GetValue(json,
        bytes => WorldDefinitionSerialization.DeserializeForAdmission(documentDirectory: Host.Configuration.DocumentDirectory,
            utf8Json: bytes, machines: Host.Machines.ValidationCatalog));

    /// <summary>A computed identity for a composed boot. Only the command schedule may differ.</summary>
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
    /// <summary>Attaches a compatible scenario's schedule to its complete pristine boot checkpoint.</summary>
    public WorldAuthorityCheckpoint ScenarioCheckpoint(WorldAuthorityCheckpoint boot, WorldDefinitionAdmission admission) {
        var json = WorldDefinitionSerialization.Serialize(definition: admission.Definition);

        // Fields, row ordinals, rule groups, latches, decisions and search all derive from this document.
        // Replacing only its arena cannot make the rest of a different boot's checkpoint compatible.
        if (ScenarioBootShape(json: json) != ScenarioBootShape(json: boot.Server.DefinitionJson)) {
            throw new InvalidOperationException(message: "Scenario changes boot configuration; it requires its own worker.");
        }
        m_checkpointAdmissions.Add(key: json, value: admission);
        return boot with {
            Server = boot.Server with { DefinitionJson = json, BaseDefinitionJson = json },
        };
    }
    /// <summary>Restarts module memory and restores a checkpoint into an already composed compatible authority.</summary>
    public void RestoreScenario(WorldAuthorityCheckpoint checkpoint) {
        lock (Host.AuthorityGate) {
            var admission = CheckpointAdmission(json: checkpoint.Server.DefinitionJson);
            var definition = admission.Definition;
            // The previous run may have edited geometry or machine configuration after boot.
            if (ScenarioBootShape(json: WorldDefinitionSerialization.Serialize(definition: Host.Configuration)) !=
                ScenarioBootShape(json: checkpoint.Server.DefinitionJson)) {
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

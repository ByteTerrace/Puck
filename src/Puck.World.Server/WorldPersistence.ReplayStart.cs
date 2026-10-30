using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    private readonly ConditionalWeakTable<byte[], WorldDefinitionAdmission> m_checkpointAdmissions = new();

    private WorldDefinitionAdmission CheckpointAdmission(byte[] json) => m_checkpointAdmissions.GetValue(json,
        bytes => WorldDefinitionSerialization.DeserializeForAdmission(documentDirectory: Host.Configuration.DocumentDirectory,
            utf8Json: bytes, machines: Host.Machines.ValidationCatalog));

    // A computed identity for a serialized definition: every member except the command schedule.
    private static string ConfigurationShape(byte[] json) {
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
    /// <summary>Restarts module memory and restores a replay's starting checkpoint into this authority, reinstalling the
    /// definition's geometry, machines and population first when the run edited its configuration.</summary>
    /// <param name="checkpoint">The checkpoint captured when the replay probe attached.</param>
    public void RestoreReplayStart(WorldAuthorityCheckpoint checkpoint) {
        lock (Host.AuthorityGate) {
            var admission = CheckpointAdmission(json: checkpoint.Server.DefinitionJson);
            var definition = admission.Definition;
            // The run may have edited geometry or machine configuration after the checkpoint.
            if (ConfigurationShape(json: WorldDefinitionSerialization.Serialize(definition: Host.Configuration)) !=
                ConfigurationShape(json: checkpoint.Server.DefinitionJson)) {
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

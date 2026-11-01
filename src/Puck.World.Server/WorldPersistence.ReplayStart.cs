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

    /// <summary>Restores a replay's starting checkpoint into this authority, reinstalling the
    /// definition's geometry, machines and population first when the run edited its configuration.</summary>
    /// <param name="checkpoint">The checkpoint captured when the replay probe attached.</param>
    /// <param name="restoreOwnedIdentities">Whether to restore owned documents; tapes retain only projected identities.</param>
    /// <param name="documentDirectory">The tape's asset directory, or null to use this authority's directory.</param>
    public void RestoreReplayStart(WorldAuthorityCheckpoint checkpoint, bool restoreOwnedIdentities = true, string? documentDirectory = null) {
        lock (Host.AuthorityGate) {
            var admission = ((documentDirectory is null) ? CheckpointAdmission(json: checkpoint.Server.DefinitionJson)
                : WorldDefinitionSerialization.DeserializeForAdmission(documentDirectory: documentDirectory,
                    utf8Json: checkpoint.Server.DefinitionJson, machines: Host.Machines.ValidationCatalog));
            var definition = admission.Definition;
            // A malformed guest image must refuse before geometry, machines, or the live definition move.
            using var addonPlan = Host.Addons?.PrepareCheckpoint(definition, (checkpoint.Addons ?? []));
            // The run may have edited geometry or machine configuration after the checkpoint.
            if (ConfigurationShape(json: WorldDefinitionSerialization.Serialize(definition: Host.Configuration)) !=
                ConfigurationShape(json: checkpoint.Server.DefinitionJson)) {
                if (!WorldDocument.TryBuildSolids(definition: definition, reason: out var solidReason, solids: out var solids)) { throw new InvalidOperationException(message: solidReason); }
                if (!Host.Machines.TryPrepare(Host.Configuration, definition, out var machines, out var machineReason)) { throw new InvalidOperationException(message: machineReason); }
                if (machines is not null) { Host.Machines.Commit(plan: machines); Host.Machines.Finish(plan: machines); }
                Host.Document.RestoreSolidField(solids, checkpoint.Server.SolidRevision);
                Host.Document.Install(definition, rebuildPopulation: true, compilation: admission.Compilation);
            }
            RestoreCheckpointCore(checkpoint, admission, resetSessionEpochs: true,
                restoreOwnedIdentities: restoreOwnedIdentities, preparedAddons: addonPlan);
        }
    }
}

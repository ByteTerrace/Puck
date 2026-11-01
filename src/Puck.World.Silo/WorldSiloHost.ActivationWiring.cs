using Puck.World.Server;
using Puck.World.Addons;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // The shared activation tail every construction path (fresh or FromCheckpoint) runs once a server exists:
    // neighbours, the pipeline-source reader and its boot bind check, the closed-group rewind constraint, and the
    // addon host attachment. Every refused or failed wiring releases its owned activation resources.
    private bool TryFinishActivationWiring(WorldAuthorityIdentity identity, WorldServer server, IWorldNeighbourResolver? neighbours, WorldAdjacencyFields adjacencies, WorldMachineHost machines) {
        var success = false;

        try {
            server.Neighbours = neighbours;

            // A hosted world's definition arrives from cloud storage, never a local file, so it has no document
            // directory: an absolute source reads, and a relative one is refused by name (WorldDocumentPaths). Attached
            // either way, so the boot bind check below runs and an override names its real refusal rather than
            // SourcesUnattached.
            server.PipelineSources = new WorldPipelineSources(documentDirectory: server.Definition.DocumentDirectory);

            // The same boot check WorldPostBuildWiring runs for the desktop host, run here for the activated document:
            // a views.graphs row naming a bad override value is refused by name at activation rather than only when
            // a live commit or a graph install eventually reaches it.
            if (!server.TryBindPipelineRows(reason: out var pipelineReason)) {
                Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (pipeline {pipelineReason})]");
                return false;
            }
            if (ClosedGroupRewind) { server.ConstrainTransferAuthorities(allowed: ContainsRewindAuthority); }
            // The desktop host uses this same mount transaction. Attachment also restores the checkpoint's guest image
            // before journal-tail replay or admission can observe the activation.
            if (!WorldAddonRuntime.TryCreate(server.Definition, server, out _, out var addonReason)) {
                Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (addon {addonReason})]");
                return false;
            }

            success = true;
            return true;
        } catch (Exception error) when ((error is InvalidOperationException or IOException or ArgumentException or System.Text.Json.JsonException)) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (activation wiring: {error.Message})]");
            return false;
        } finally {
            if (!success) { server.Addons?.Dispose(); adjacencies.Dispose(); machines.Dispose(); }
        }
    }
}

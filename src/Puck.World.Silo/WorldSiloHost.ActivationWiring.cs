using Puck.World.Server;

namespace Puck.World.Silo;

public sealed partial class WorldSiloHost {
    // The shared activation tail every construction path (fresh or FromCheckpoint) runs once a server exists:
    // neighbours, the pipeline-source reader and its boot bind check, the closed-group rewind constraint, and the
    // addon host attachment. Disposes adjacencies/machines and reports false only on a pipeline refusal.
    private bool TryFinishActivationWiring(WorldAuthorityIdentity identity, WorldServer server, IWorldNeighbourResolver? neighbours, WorldAdjacencyFields adjacencies, WorldMachineHost machines) {
        server.Neighbours = neighbours;

        // A hosted world's definition arrives from cloud storage, never a local file, so it has no document
        // directory: an absolute source reads, and a relative one is refused by name (WorldDocumentPaths). Attached
        // either way, so the boot bind check below runs and an override names its real refusal rather than
        // SourcesUnattached.
        server.PipelineSources = new WorldPipelineSources(documentDirectory: server.Definition.DocumentDirectory);

        // The same boot check WorldPostBuildWiring runs for the desktop host, run here for the activated document:
        // a views.pipelines row naming a bad override value is refused by name at activation rather than only when
        // a live commit or a graph install eventually reaches it.
        if (!server.TryBindPipelineRows(reason: out var pipelineReason)) {
            Console.Error.WriteLine(value: $"[silo.activate: '{RowKey(identity: identity)}' refused (pipeline {pipelineReason})]");
            adjacencies.Dispose();
            machines.Dispose();

            return false;
        }
        if (ClosedGroupRewind) { server.ConstrainTransferAuthorities(allowed: ContainsRewindAuthority); }
        // Attached BEFORE journal-tail replay and live admission. TryApplyMutation and ApplyRebuild both refuse an
        // addon-affecting operation outright when NO host is attached at all, so this is not what stops those two —
        // it is what closes world.undo's own gap: WorldServer.AddonsCanPrepare treats a null m_addons as vacuously
        // nothing to check, so an undo that restores an enabled addon row would otherwise install silently on a
        // server with no host attached at all. WorldNoAddonHost.TryPrepare refuses that row BY NAME instead, the
        // identical door the initial-candidate check above already used, so the two refusals can never disagree.
        server.AttachAddons(runtime: new WorldNoAddonHost());

        return true;
    }
}

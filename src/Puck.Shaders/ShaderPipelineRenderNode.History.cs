namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // Reserve a new ring instance only for a pass that records a write. The last successful instance remains the
    // previous input throughout this submission; CommitCadenceFrame advances it, and recovery cancels the reservation.
    private void PrepareHistoryWrites(RuntimePass pass) {
        foreach (var access in pass.Accesses) {
            if (access.Use.Writes && !access.PreviousFrame && (m_resources[access.Storage] is { History: true } resource)) {
                resource.HistoryWriting = true;
            }
        }
    }
}

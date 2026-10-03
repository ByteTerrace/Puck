namespace Puck.Shaders;

public sealed partial class ShaderPipelineRenderNode {
    // Reserve an instance only for a recording writer. Every previous read in this submission still sees the last
    // successful write. Submission commits the reservation; recording and submission failures cancel it.
    private void PrepareHistoryWrites(RuntimePass pass) {
        foreach (var access in pass.Accesses) {
            if (access.Use.Writes && !access.PreviousFrame && (m_resources[access.Storage] is { History: true } resource)) {
                resource.HistoryWriting = true;
            }
        }
    }
}

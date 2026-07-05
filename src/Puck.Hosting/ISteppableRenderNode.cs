namespace Puck.Hosting;

/// <summary>
/// A render node whose per-frame simulation work splits into a serial prepare and a parallelizable execute, so a
/// hosting parent can fan the expensive half out one task per node. The contract enforces the timeline-access rule:
/// <see cref="PrepareStep"/> runs SERIALLY on the render thread and may touch shared state (advance shared-timeline
/// cursors, drain shared input services); <see cref="ExecuteStep"/> touches ONLY the node's private state, so the
/// parent may run one per task. All GPU work stays behind in <see cref="IRenderNode.ProduceFrame"/> on the render
/// thread.
/// </summary>
public interface ISteppableRenderNode : IRenderNode {
    /// <summary>The serial half: samples shared inputs/timelines on the render thread and stages them as pending
    /// state for <see cref="ExecuteStep"/>. Returns whether there is stepping work, so the parent only fans out
    /// nodes that will actually run.</summary>
    /// <param name="context">The frame context (tick budget + input sampling key).</param>
    /// <returns><see langword="true"/> when the node has pending work for <see cref="ExecuteStep"/>.</returns>
    bool PrepareStep(in FrameContext context);

    /// <summary>The parallel half: consumes the inputs staged by <see cref="PrepareStep"/> and advances the node's
    /// simulation. Touches ONLY this node's private state (steppable siblings share nothing).</summary>
    void ExecuteStep();
}

using Puck.Abstractions.Presentation;

namespace Puck.Shaders.Tests;

/// <summary>A held <see cref="FakePipelineGpu.PipelineGate"/> that opens when disposed. Declared after the node it
/// holds, it is disposed first, so a law that fails while a build waits in the driver releases that build instead of
/// leaving the node's disposal waiting on it.</summary>
internal sealed class PipelineGateOpener : IDisposable {
    public ManualResetEventSlim Gate { get; } = new(initialState: false);

    public void Dispose() => Gate.Set();
}
/// <summary>
/// Drives a render node through the frames around a candidate build, which the node starts at a frame boundary and runs
/// on the thread pool, so a law's frame sequence never depends on the pool's timing. The bounds are liveness for builds
/// the fake finishes at once; they decide nothing.
/// </summary>
internal static class ShaderPipelineRenderNodeBuilds {
    // Produces until a node with nothing installed installs its first graph, and returns that frame. The frames before it
    // present nothing and submit nothing, so however many there were, the node has rendered exactly one frame.
    public static Surface ProduceUntilInstalled(this ShaderPipelineRenderNode node) {
        var surface = default(Surface);

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    surface = node.ProduceFrame(context: default);

                    return node.IsReady;
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The first graph never installed."
        );

        return surface;
    }
    // Selects an output and waits out the float preview it builds on the thread pool, without producing, so the next
    // frame installs it: the frame sequence is the one a synchronous selection gave.
    public static void SelectOutputBuilt(this ShaderPipelineRenderNode node, string name) {
        node.SelectOutput(name: name);
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => !node.IsBuildingPreview,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The float preview never finished building."
        );
    }
    // Produces the frame that starts a queued build, with the driver held so the build cannot finish within it: that
    // frame presents the installed graph, or nothing when none is installed. Then waits the build out without producing,
    // so the next frame installs it.
    public static Surface ProduceBuildStart(this ShaderPipelineRenderNode node, FakePipelineGpu gpu) {
        Surface surface;

        using (var opener = new PipelineGateOpener()) {
            gpu.PipelineGate = opener.Gate;

            try {
                surface = node.ProduceFrame(context: default);
            } finally {
                gpu.PipelineGate = null;
            }
        }

        node.WaitForBuild();

        return surface;
    }
    public static void WaitForBuild(this ShaderPipelineRenderNode node) =>
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => !node.IsBuildingCandidate,
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The candidate's build did not finish."
        );
}

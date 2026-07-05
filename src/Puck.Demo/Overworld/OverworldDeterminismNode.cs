using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Demo.Overworld;

/// <summary>
/// A one-shot root render node installed only under <c>--validate-overworld</c>. On its first frame it runs the
/// pure-CPU determinism + replay self-check (<see cref="OverworldDeterminism"/>) and asks the terminal to exit
/// (0 = pass, 1 = a determinism/replay divergence, 2 = infra-fail). It never touches the GPU and never presents — it
/// proves the day-one guarantee that the simulation is deterministic and a recording replays bit-for-bit.
/// </summary>
internal sealed class OverworldDeterminismNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "overworld-determinism",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="OverworldDeterminismNode"/> class.</summary>
    /// <param name="result">The shared result the exit code is written to.</param>
    public OverworldDeterminismNode(ParityResult result) {
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            var result = OverworldDeterminism.Run();

            if (result.Passed) {
                m_result.ExitCode = 0;
                Console.Out.WriteLine(value: $"OVERWORLD pass | {result.Message}");
            } else {
                m_result.ExitCode = 1;
                Console.Error.WriteLine(value: $"OVERWORLD fail | {result.Message}");
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"OVERWORLD infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }
}

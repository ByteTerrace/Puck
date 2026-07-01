using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Demo.Replay;

/// <summary>
/// A one-shot root render node installed only under <c>--validate-cli-determinism</c>. On its first frame it runs the
/// pure-CPU deterministic command-line self-check (<see cref="CliDeterminismGate"/>) and asks the terminal to exit
/// (0 = pass, 1 = a determinism/replay/effectiveness divergence, 2 = infra-fail). It never touches the GPU and never
/// presents — it proves a scripted console / STDIN command session drives the simulation deterministically and
/// replays bit-for-bit.
/// </summary>
internal sealed class CliDeterminismGateNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "cli-determinism-gate",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="CliDeterminismGateNode"/> class.</summary>
    /// <param name="result">The shared result the exit code is written to.</param>
    public CliDeterminismGateNode(ParityResult result) {
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
            var result = CliDeterminismGate.Run();

            if (result.Passed) {
                m_result.ExitCode = 0;
                Console.Out.WriteLine(value: $"CLI-DETERMINISM pass | {result.Message}");
            } else {
                m_result.ExitCode = 1;
                Console.Error.WriteLine(value: $"CLI-DETERMINISM fail | {result.Message}");
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"CLI-DETERMINISM infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }
}

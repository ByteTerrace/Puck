using Puck.Commands;

namespace Puck.Launcher;

/// <summary>Echoes the real result of a console command after the host applies it on its deterministic tick. Physical
/// snapshot entries carry no text and are intentionally silent.</summary>
internal sealed class SimulationCommandOutputObserver(BufferedConsoleOutput output) : ICommandObserver {
    private readonly BufferedConsoleOutput m_output = output;

    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        if ((activation.Text is null) || string.IsNullOrEmpty(value: activation.Result.Output)) {
            return;
        }

        // A tick-deferred verb's verdict arrives here rather than at the console call site (Submit returned None when it
        // injected the line), so this sink owns the same accepted/REFUSED stream split the text sink makes — otherwise
        // every Simulation-routed rejection would still read as ordinary stdout transcript.
        if (activation.Result.IsError) {
            m_output.WriteErrorLine(value: activation.Result.Output);
        } else {
            m_output.WriteLine(value: activation.Result.Output);
        }
    }
}

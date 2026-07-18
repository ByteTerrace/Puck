using Puck.Commands;

namespace Puck.Launcher;

/// <summary>Echoes the real result of a console command after the host applies it on its deterministic tick. Physical
/// snapshot entries carry no text and are intentionally silent.</summary>
internal sealed class SimulationCommandOutputObserver(BufferedConsoleOutput output) : ICommandObserver {
    private readonly BufferedConsoleOutput m_output = output;

    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        if ((activation.Text is not null) && !string.IsNullOrEmpty(value: activation.Result.Output)) {
            m_output.WriteLine(value: activation.Result.Output);
        }
    }
}

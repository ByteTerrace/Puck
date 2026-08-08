using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// Echoes every command activation that carries output to the <see cref="DemoConsole"/>, prefixed with the
/// command name — the visible feedback for key bindings and console verbs.
/// </summary>
internal sealed class DemoCommandObserver(DemoConsole console) : ICommandObserver {
    /// <inheritdoc/>
    public void OnCommand(in CommandActivation activation) {
        if (string.IsNullOrEmpty(value: activation.Result.Output)) {
            return;
        }

        console.WriteLine(message: $"> {activation.Name} {activation.Result.Output}");
    }
}

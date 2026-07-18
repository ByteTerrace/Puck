using Puck.Commands;

namespace Puck.Launcher.Commands;

/// <summary>
/// The launcher's backend-choice command surface: a single <c>backend</c> verb that toggles the active graphics backend
/// through the <see cref="BackendSwitcher"/>. Registered by <see cref="LauncherServiceRegistration.AddBackendSwitcher"/>,
/// so the verb exists only when the backend switch is composed in.
/// </summary>
internal sealed class BackendCommandModule(BackendSwitcher backendSwitcher) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.Verb(
            description: "Toggles between the available graphics backends.",
            handler: _ => {
                var from = backendSwitcher.ActiveBackendName;

                backendSwitcher.Switch();

                var to = backendSwitcher.ActiveBackendName;

                return new CommandResult((string.Equals(a: from, b: to, comparisonType: StringComparison.OrdinalIgnoreCase)
                    ? $"[backend: {from} — no alternative available]"
                    : $"[backend: {from} → {to}]"));
            },
            name: "backend",
            valueKind: CommandValueKind.Digital
        );
    }
}

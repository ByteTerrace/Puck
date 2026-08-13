using Puck.Commands;
using Puck.Input;

namespace Puck.Launcher;

/// <summary>The terminal-owned physical command plane, independent of any game's active binding page.</summary>
public sealed class TerminalInputBindings : IAlwaysActiveInputBindings {
    private static readonly CommandBinding[] ConsoleBindings = [new(
        Command: TerminalCommandNames.Console,
        ActivateOn: CommandPhase.Started
    )];

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) =>
        (source == InputSources.Keyboard.Backtick) ? ConsoleBindings : null;
}

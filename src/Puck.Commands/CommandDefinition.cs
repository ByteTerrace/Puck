using System.CommandLine;

namespace Puck.Commands;

/// <summary>
/// Defines a named, typed, invokable command. A single definition is the shared identity behind every
/// way the command can be driven.
/// </summary>
/// <remarks>
/// The same definition is resolved both when a console line is parsed into a
/// <see cref="CommandContext"/> and when a source dispatches a <see cref="CommandSignal"/> for the
/// command's <see cref="Name"/>. <see cref="Handler"/> runs on every activation; continuous consumers
/// instead poll the registry for the command's per-frame value.
/// </remarks>
/// <param name="Name">The unique name used to identify and dispatch the command.</param>
/// <param name="Description">A human-readable description shown in help output.</param>
/// <param name="ValueKind">The shape of the value the command carries.</param>
/// <param name="TextCommand">The <see cref="Command"/> used to parse the command from a text line.</param>
/// <param name="Handler">The delegate invoked on each activation.</param>
/// <param name="ValueSelector">
/// An optional delegate that maps a parsed text line to a <see cref="CommandValue"/> (for example,
/// <c>move --x 1 --y 0</c>). When <see langword="null"/>, an impulse value derived from
/// <paramref name="ValueKind"/> is used.
/// </param>
/// <param name="Map">
/// The command map that gates source-driven activation. Defaults to <see cref="CommandMaps.Global"/>,
/// which is always active.
/// </param>
public sealed record CommandDefinition(
    string Name,
    string Description,
    CommandValueKind ValueKind,
    Command TextCommand,
    Func<CommandContext, CommandResult> Handler,
    Func<ParseResult, CommandValue>? ValueSelector = null,
    string Map = CommandMaps.Global
) {
    /// <summary>Gets the alternate names that also resolve to this command, on both the text and
    /// source-driven paths. Empty by default.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>
    /// Gets the command's determinism class — whether a submitted text line runs inline or is folded into the
    /// deterministic per-tick <see cref="CommandSnapshot"/>. Defaults to <see cref="CommandRouting.Immediate"/>.
    /// </summary>
    public CommandRouting Routing { get; init; } = CommandRouting.Immediate;

    /// <summary>Creates a definition whose text command is a bare verb with no arguments or options.</summary>
    /// <param name="name">The unique name used to identify and dispatch the command.</param>
    /// <param name="description">A human-readable description shown in help output.</param>
    /// <param name="valueKind">The shape of the value the command carries.</param>
    /// <param name="handler">The delegate invoked on each activation.</param>
    /// <param name="map">
    /// The command map that gates source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.
    /// </param>
    /// <param name="aliases">Optional alternate names that also resolve to the command.</param>
    /// <param name="routing">
    /// The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>; pass
    /// <see cref="CommandRouting.Simulation"/> for a command whose effect mutates the deterministic simulation.
    /// </param>
    /// <returns>A new <see cref="CommandDefinition"/> backed by a bare-verb text command.</returns>
    public static CommandDefinition Verb(
        string name,
        string description,
        CommandValueKind valueKind,
        Func<CommandContext, CommandResult> handler,
        string map = CommandMaps.Global,
        IReadOnlyList<string>? aliases = null,
        CommandRouting routing = CommandRouting.Immediate
    ) {
        return new CommandDefinition(
            Name: name,
            Description: description,
            ValueKind: valueKind,
            TextCommand: new Command(
                name: name,
                description: description
            ),
            Handler: handler,
            Map: map
        ) {
            Aliases = (aliases ?? []),
            Routing = routing,
        };
    }
}

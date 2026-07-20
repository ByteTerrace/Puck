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

    /// <summary>
    /// Whether this verb's SUCCESS <see cref="CommandResult.Output"/> is a bare acknowledgement of a side effect —
    /// noise a flooded scripted pipe does not read — so <c>wire.ack quiet</c> may drop it. Defaults to
    /// <see langword="false"/>: the output is treated as an ANSWER (a read-back, a status line, a listing) and quiet
    /// never suppresses it. Errors are never suppressed either way.
    /// </summary>
    /// <remarks>
    /// This is the ONE discriminator behind quiet mode. It is deliberately opt-in rather than derived from the
    /// registration shape: every argument-bearing verb is wire-native, so wire-nativeness distinguishes nothing.
    /// </remarks>
    public bool AcknowledgementOnly { get; init; }

    /// <summary>
    /// The raw wire-argument handler for a command built by <see cref="WithWireArgs"/> — the same delegate wrapped into
    /// <see cref="Handler"/>, exposed so the text-dispatch fast path can hand it a zero-copy <see cref="WireArgs"/> view
    /// over the submitted line (no substrings, no argument array) instead of running the System.CommandLine parse.
    /// <see langword="null"/> only for a bare <see cref="Verb"/> or a hand-built definition, which stay on the full
    /// parse.
    /// </summary>
    internal Func<CommandContext, WireArgs, CommandResult>? WireArgsHandler { get; init; }

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

    /// <summary>Creates a WIRE-NATIVE definition whose handler receives its trailing tokens as a zero-copy
    /// <see cref="WireArgs"/> view rather than a materialized <see cref="string"/> array — the argument-bearing verb
    /// shape the stdin hot path dispatches without allocating (span tokenize → frozen alternate-lookup → this handler,
    /// see <c>CommandRegistry.Submit</c>) — THE argument-bearing verb mechanism, with no sibling. It also registers a
    /// trailing-token text command, so quoted lines, the help listing, and System.CommandLine parse-error text keep
    /// working; on that fallback path the
    /// wrapped <see cref="Handler"/> adapts the parsed <see cref="string"/> array into an array-mode <see cref="WireArgs"/>
    /// and invokes THIS handler — one wire handler is the single source of truth for both the fast and fallback paths.</summary>
    /// <param name="name">The unique name used to identify and dispatch the command.</param>
    /// <param name="description">A human-readable description shown in help output.</param>
    /// <param name="handler">The delegate invoked on each activation, given a <see cref="WireArgs"/> over the trailing
    /// tokens. A side-effecting verb MUST return <c>IsError: true</c> on every failure (so <c>wire.ack quiet</c> can
    /// safely drop only its successes) and SHOULD gate its success-echo construction on <see cref="WireArgs.Echo"/>.</param>
    /// <param name="map">The command map that gates source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.</param>
    /// <param name="routing">The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>.</param>
    /// <param name="ackOnly">Whether the verb's success output is a bare acknowledgement <c>wire.ack quiet</c> may drop
    /// (see <see cref="AcknowledgementOnly"/>). Leave <see langword="false"/> for anything a caller reads back.</param>
    /// <returns>A new wire-native <see cref="CommandDefinition"/>.</returns>
    public static CommandDefinition WithWireArgs(
        string name,
        string description,
        Func<CommandContext, WireArgs, CommandResult> handler,
        string map = CommandMaps.Global,
        CommandRouting routing = CommandRouting.Immediate,
        bool ackOnly = false
    ) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Name: name,
            Description: description,
            ValueKind: CommandValueKind.Digital,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            // Fallback path (quoted lines / help / parse errors): adapt the parsed token array into an array-mode
            // WireArgs and invoke the SAME wire handler. Echo rides the registry's live ack mode so a quiet run
            // suppresses here identically to the fast path; a registry-less invocation defaults to echoing.
            Handler: context => handler(
                arg1: context,
                arg2: new WireArgs(
                    array: (context.Parse?.GetValue(argument: rest) ?? []),
                    echo: (context.Registry?.AcksEnabled ?? true)
                )
            ),
            Map: map
        ) {
            AcknowledgementOnly = ackOnly,
            Routing = routing,
            WireArgsHandler = handler,
        };
    }
}

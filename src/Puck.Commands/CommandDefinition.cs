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
    /// Whether this verb's <see cref="CommandResult.Output"/> IS the answer the caller asked for (a query like
    /// <c>player.where</c>), rather than an acknowledgement of a side effect. Defaults to <see langword="false"/>. A
    /// data-echoing verb is NEVER suppressed by <c>wire.ack quiet</c> — quiet only drops the success ACKS of
    /// side-effecting wire verbs, so a query still returns its data on a quiet pipe.
    /// </summary>
    public bool EchoesData { get; init; }

    /// <summary>
    /// The raw trailing-token handler for a command built by <see cref="WithTrailingArgs"/> — the same delegate
    /// wrapped into <see cref="Handler"/>, exposed so a text-dispatch fast path can hand it a pre-tokenized argument
    /// array WITHOUT running the full System.CommandLine parse (see <c>CommandRegistry.Submit</c>). <see langword="null"/>
    /// for every other definition shape (a bare <see cref="Verb"/>, a hand-built definition, or a module's own trailing
    /// wrapper), which keeps those off the fast path and on the unchanged parse. The wrapped <see cref="Handler"/> stays
    /// the single source of truth for the normal path; this is only an accelerator hook the registry may or may not use.
    /// </summary>
    internal Func<CommandContext, string[], CommandResult>? TrailingArgsHandler { get; init; }

    /// <summary>
    /// The raw wire-argument handler for a command built by <see cref="WithWireArgs"/> — the same delegate wrapped into
    /// <see cref="Handler"/>, exposed so the text-dispatch fast path can hand it a zero-copy <see cref="WireArgs"/> view
    /// over the submitted line (no substrings, no argument array) instead of running the System.CommandLine parse. Its
    /// non-<see langword="null"/>-ness also marks the definition as WIRE-NATIVE: the only verbs whose success acks
    /// <c>wire.ack quiet</c> may suppress. <see langword="null"/> for every other definition shape (a bare
    /// <see cref="Verb"/>, a <see cref="WithTrailingArgs"/> command, a hand-built definition), which keeps those verbs on
    /// their existing paths and never suppresses them.
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

    /// <summary>Creates a definition whose text command takes ONE trailing token list, handed to the handler as a raw
    /// <see cref="string"/> array — the shared form behind every argument-taking console verb. This is the canonical
    /// home of the helper that had been copy-pasted per command module; new modules call this instead of re-declaring
    /// it (parse the tokens with <see cref="CommandArgs"/>).</summary>
    /// <param name="name">The unique name used to identify and dispatch the command.</param>
    /// <param name="description">A human-readable description shown in help output.</param>
    /// <param name="handler">The delegate invoked on each activation, given the trailing tokens (empty when the
    /// command was driven by a source rather than a parsed text line).</param>
    /// <param name="map">The command map that gates source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.</param>
    /// <param name="routing">The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>.</param>
    /// <returns>A new <see cref="CommandDefinition"/> backed by a trailing-token text command.</returns>
    public static CommandDefinition WithTrailingArgs(
        string name,
        string description,
        Func<CommandContext, string[], CommandResult> handler,
        string map = CommandMaps.Global,
        CommandRouting routing = CommandRouting.Immediate
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
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Map: map
        ) {
            Routing = routing,
            TrailingArgsHandler = handler,
        };
    }

    /// <summary>Creates a WIRE-NATIVE definition whose handler receives its trailing tokens as a zero-copy
    /// <see cref="WireArgs"/> view rather than a materialized <see cref="string"/> array — the argument-bearing verb
    /// shape the stdin hot path dispatches without allocating (span tokenize → frozen alternate-lookup → this handler,
    /// see <c>CommandRegistry.Submit</c>). It registers the SAME trailing-token text command as <see cref="WithTrailingArgs"/>,
    /// so quoted lines, the help listing, and System.CommandLine parse-error text keep working; on that fallback path the
    /// wrapped <see cref="Handler"/> adapts the parsed <see cref="string"/> array into an array-mode <see cref="WireArgs"/>
    /// and invokes THIS handler — one wire handler is the single source of truth for both the fast and fallback paths.</summary>
    /// <param name="name">The unique name used to identify and dispatch the command.</param>
    /// <param name="description">A human-readable description shown in help output.</param>
    /// <param name="handler">The delegate invoked on each activation, given a <see cref="WireArgs"/> over the trailing
    /// tokens. A side-effecting verb MUST return <c>IsError: true</c> on every failure (so <c>wire.ack quiet</c> can
    /// safely drop only its successes) and SHOULD gate its success-echo construction on <see cref="WireArgs.Echo"/>.</param>
    /// <param name="map">The command map that gates source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.</param>
    /// <param name="routing">The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>.</param>
    /// <param name="echoesData">Whether the verb's output is a QUERY answer (see <see cref="EchoesData"/>) that
    /// <c>wire.ack quiet</c> must never suppress — <see langword="true"/> for a read-back like <c>player.where</c>.</param>
    /// <returns>A new wire-native <see cref="CommandDefinition"/>.</returns>
    public static CommandDefinition WithWireArgs(
        string name,
        string description,
        Func<CommandContext, WireArgs, CommandResult> handler,
        string map = CommandMaps.Global,
        CommandRouting routing = CommandRouting.Immediate,
        bool echoesData = false
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
            EchoesData = echoesData,
            Routing = routing,
            WireArgsHandler = handler,
        };
    }
}

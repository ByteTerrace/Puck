using System.CommandLine;

namespace Puck.Commands;

/// <summary>
/// Defines a named, typed, invokable command. A single definition is the shared identity behind every
/// way the command can be driven.
/// </summary>
/// <remarks>
/// The same definition is resolved both when a console line is parsed into a <see cref="CommandContext"/> and when the
/// snapshot mixer dispatches the command for a bound control. <see cref="Handler"/> runs on every activation,
/// receiving that activation's value on <see cref="CommandContext.Value"/> and its stamped identity on
/// <see cref="CommandContext.Principal"/>.
/// <para>Definitions are built through <see cref="Verb"/> and <see cref="WithWireArgs"/> only, and the handler they
/// carry is internal: what a command is (its <see cref="CommandMetadata"/>) is public, what it does is reachable
/// solely through the registry's stamped dispatch.</para>
/// <para>The IDENTITY-BEARING members — <see cref="Name"/>, <see cref="TextCommand"/>, <see cref="Description"/> and
/// <see cref="Map"/> — are readable but not settable from outside this assembly, because <c>with</c> would otherwise
/// bypass both factories and split a command in half: <c>Verb(name: "jump", …) with { Name = "fly" }</c> registers,
/// answers <see cref="CommandRegistry.TryGetId"/> for <c>fly</c>, and yet dispatches only for the line <c>jump</c>,
/// which the registry reports as unknown. The declarative members beside them (routing, bindability, value kind,
/// aliases, scope) stay open: none of them can disagree with the System.CommandLine object this definition owns.</para>
/// </remarks>
public sealed record CommandDefinition {
    /// <summary>Initializes a new instance of the <see cref="CommandDefinition"/> class.</summary>
    /// <param name="Name">The unique name used to identify and dispatch the command.</param>
    /// <param name="Description">A human-readable description shown in help output.</param>
    /// <param name="ValueKind">The shape of the value the command carries.</param>
    /// <param name="TextCommand">The <see cref="Command"/> used to parse the command from a text line.</param>
    /// <param name="Bindability">Whether a binding document may name this command as a destination.</param>
    /// <param name="Handler">The delegate invoked on each activation.</param>
    /// <param name="Map">The command map that classifies source-driven activation.</param>
    internal CommandDefinition(
        string Name,
        string Description,
        CommandValueKind ValueKind,
        Command TextCommand,
        CommandBindability Bindability,
        Func<CommandContext, CommandResult> Handler,
        string Map = CommandMaps.Global
    ) {
        this.Bindability = Bindability;
        this.Description = Description;
        this.Handler = Handler;
        this.Map = Map;
        this.Name = Name;
        this.TextCommand = TextCommand;
        this.ValueKind = ValueKind;
    }

    /// <summary>Gets the delegate invoked on each activation. Internal: dispatch happens through the registry, which
    /// is what stamps the <see cref="CommandContext.Principal"/> the handler acts on.</summary>
    internal Func<CommandContext, CommandResult> Handler { get; init; }
    /// <summary>
    /// The raw wire-argument handler for a command built by <see cref="WithWireArgs"/> — the same delegate wrapped into
    /// <see cref="Handler"/>, exposed so the wire-native text path can hand it a zero-copy <see cref="WireArgs"/> view
    /// over the submitted line (no substrings, no argument array) instead of running the System.CommandLine parse.
    /// <see langword="null"/> only for a bare <see cref="Verb"/>, which stays on the full parse.
    /// </summary>
    internal Func<CommandContext, WireArgs, CommandResult>? WireArgsHandler { get; init; }

    /// <summary>
    /// Whether this verb's success <see cref="CommandResult.Output"/> is a bare acknowledgement of a side effect —
    /// noise a flooded scripted pipe does not read — so <c>wire.ack quiet</c> may drop it. Defaults to
    /// <see langword="false"/>: the output is treated as an answer (a read-back, a status line, a listing) and quiet
    /// never suppresses it. Errors are never suppressed either way.
    /// </summary>
    /// <remarks>
    /// This is the one discriminator behind quiet mode. It is deliberately opt-in rather than derived from the
    /// registration shape: every argument-bearing verb is wire-native, so wire-nativeness distinguishes nothing.
    /// </remarks>
    public bool AcknowledgementOnly { get; init; }
    /// <summary>Gets whether a binding document may name this command as a destination.</summary>
    public CommandBindability Bindability { get; init; }
    /// <summary>Gets the human-readable description shown in help output.</summary>
    public string Description { get; internal init; }
    /// <summary>Gets whether this is a HELD verb: the handler reads the phase (active on Started/Active, released
    /// on Completed/Canceled), so a plain-bound entry — no <c>activateOn</c> — delivers both edges, exactly as a
    /// channel destination does. An author binds it once; only an explicit <c>activateOn</c> narrows to one edge.</summary>
    public bool Held { get; init; }
    /// <summary>Gets the command map that classifies source-driven activation.</summary>
    public string Map { get; internal init; }
    /// <summary>Gets the publicly readable facts about this command — including whether it accepts wire arguments —
    /// that <see cref="CommandRegistry.Definitions"/> hands out.</summary>
    public CommandMetadata Metadata => new(
        Name: Name,
        ValueKind: ValueKind,
        Routing: Routing,
        Bindability: Bindability,
        InputScope: InputScope,
        Map: Map,
        Held: Held,
        AcceptsWireArgs: (WireArgsHandler is not null)
    );
    /// <summary>Gets the unique name used to identify and dispatch the command.</summary>
    public string Name { get; internal init; }
    /// <summary>Gets the <see cref="Command"/> used to parse the command from a text line.</summary>
    public Command TextCommand { get; internal init; }
    /// <summary>Gets the shape of the value the command carries.</summary>
    public CommandValueKind ValueKind { get; init; }

    /// <summary>Gets whether source-driven activation requires ordinary terminal focus.</summary>
    public CommandInputScope InputScope { get; init; } = CommandInputScope.Focused;
    /// <summary>Gets the alternate names that also resolve to this command, on both the text and
    /// snapshot-driven paths. Empty by default.</summary>
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
    /// <param name="bindability">Whether a binding document may name this command. Required — every registration
    /// declares it, and <see cref="CommandBindability.Unspecified"/> is refused by name at registry construction.</param>
    /// <param name="map">
    /// The command map that classifies source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.
    /// </param>
    /// <param name="aliases">Optional alternate names that also resolve to the command.</param>
    /// <param name="routing">
    /// The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>; pass
    /// <see cref="CommandRouting.Simulation"/> for a command whose effect mutates the deterministic simulation.
    /// </param>
    /// <param name="inputScope">Whether source-driven activation requires ordinary terminal focus.</param>
    /// <param name="held">Whether the verb is HELD (see <see cref="Held"/>): a plain-bound entry delivers both edges.</param>
    /// <returns>A new <see cref="CommandDefinition"/> backed by a bare-verb text command.</returns>
    public static CommandDefinition Verb(
        string name,
        string description,
        CommandValueKind valueKind,
        Func<CommandContext, CommandResult> handler,
        CommandBindability bindability,
        string map = CommandMaps.Global,
        IReadOnlyList<string>? aliases = null,
        CommandRouting routing = CommandRouting.Immediate,
        CommandInputScope inputScope = CommandInputScope.Focused,
        bool held = false
    ) {
        // A composition-root mistake refuses HERE, naming the parameter that was wrong. A null handler used to
        // construct and register happily and then surface as `[boom: handler threw NullReferenceException]` on the
        // first dispatch — a registration bug reported as a runtime command failure, with nothing pointing back at the
        // registration.
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: description);
        ArgumentNullException.ThrowIfNull(argument: handler);

        return new CommandDefinition(
            Name: name,
            Description: description,
            ValueKind: valueKind,
            TextCommand: new Command(
                description: description,
                name: name
            ),
            Bindability: bindability,
            Handler: handler,
            Map: map
        ) {
            Aliases = (aliases ?? []),
            Held = held,
            InputScope = inputScope,
            Routing = routing,
        };
    }
    /// <summary>Creates a wire-native definition whose handler receives its trailing tokens as a zero-copy
    /// <see cref="WireArgs"/> view rather than a materialized <see cref="string"/> array — the argument-bearing verb
    /// shape the stdin hot path dispatches without allocating (span tokenize → frozen alternate-lookup → this handler,
    /// see <c>CommandRegistry.Submit</c>) — the argument-bearing verb mechanism, with no sibling. It also registers a
    /// trailing-token text command, so quoted lines, the help listing, and System.CommandLine parse-error text keep
    /// working; on that fallback path the
    /// wrapped <see cref="Handler"/> adapts the parsed <see cref="string"/> array into an array-mode <see cref="WireArgs"/>
    /// and invokes this handler — one wire handler is the single source of truth for both the fast and fallback paths.</summary>
    /// <param name="name">The unique name used to identify and dispatch the command.</param>
    /// <param name="description">A human-readable description shown in help output.</param>
    /// <param name="handler">The delegate invoked on each activation, given a <see cref="WireArgs"/> over the trailing
    /// tokens. A side-effecting verb must return <c>IsError: true</c> on every failure (so <c>wire.ack quiet</c> can
    /// safely drop only its successes) and should gate its success-echo construction on <see cref="WireArgs.Echo"/>.</param>
    /// <param name="bindability">Whether a binding document may name this command. Required — every registration
    /// declares it, and <see cref="CommandBindability.Unspecified"/> is refused by name at registry construction.</param>
    /// <param name="map">The command map that classifies source-driven activation. Defaults to <see cref="CommandMaps.Global"/>.</param>
    /// <param name="routing">The determinism class for a submitted text line. Defaults to <see cref="CommandRouting.Immediate"/>.</param>
    /// <param name="ackOnly">Whether the verb's success output is a bare acknowledgement <c>wire.ack quiet</c> may drop
    /// (see <see cref="AcknowledgementOnly"/>). Leave <see langword="false"/> for anything a caller reads back.</param>
    /// <param name="valueKind">The value kind a bound dispatch of this verb carries. Defaults to
    /// <see cref="CommandValueKind.Digital"/> — correct for the overwhelming majority of wire-native verbs, which
    /// read their arguments from <see cref="WireArgs"/> and never look at <see cref="CommandContext.Value"/> at all.
    /// Set this to the kind a binding row's constant <see cref="CommandValue"/> actually carries when a verb folds a
    /// step/direction twin onto itself (a <c>.next</c>/<c>.prev</c>/<c>.up</c>/<c>.down</c> chord bound with no
    /// argument, reading the sign of <see cref="CommandContext.Value"/> instead — see
    /// <see cref="CommandBinding.Value"/>): <see cref="BindingVocabularyCheck"/> refuses a recompose whose dispatched
    /// kind disagrees with this declaration, so a mismatched value here silently breaks every future
    /// <c>player.bind</c>/<c>world.row.set bindingOverlays</c>/profile load, not merely the boot-time narration. A handler
    /// distinguishing a bound dispatch from a typed one must read <see cref="CommandContext.Origin"/>, never
    /// <see cref="CommandContext.Source"/> or <see cref="CommandContext.Value"/>'s kind — synthesized bindings have
    /// no physical source, while the text path computes its own impulse value from this declared kind, so a typed
    /// call carries the same kind a bound one would.</param>
    /// <param name="inputScope">Whether source-driven activation requires ordinary terminal focus.</param>
    /// <param name="held">Whether the verb is HELD (see <see cref="Held"/>): a plain-bound entry delivers both edges.</param>
    /// <returns>A new wire-native <see cref="CommandDefinition"/>.</returns>
    public static CommandDefinition WithWireArgs(
        string name,
        string description,
        Func<CommandContext, WireArgs, CommandResult> handler,
        CommandBindability bindability,
        string map = CommandMaps.Global,
        CommandRouting routing = CommandRouting.Immediate,
        bool ackOnly = false,
        CommandValueKind valueKind = CommandValueKind.Digital,
        CommandInputScope inputScope = CommandInputScope.Focused,
        bool held = false
    ) {
        // See Verb: the registration is refused where it is written rather than reported as a handler fault on the
        // first line that reaches it.
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: description);
        ArgumentNullException.ThrowIfNull(argument: handler);

        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Name: name,
            Description: description,
            ValueKind: valueKind,
            TextCommand: new Command(
                description: description,
                name: name
            ) {
                rest,
            },
            Bindability: bindability,
            // Fallback path (quoted lines / help / parse errors): adapt the parsed token array into an array-mode
            // WireArgs and invoke the SAME wire handler. Echo rides the registry's live ack mode so a quiet run
            // suppresses here identically to the wire-native path; a registry-less invocation defaults to echoing.
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
            Held = held,
            InputScope = inputScope,
            Routing = routing,
            WireArgsHandler = handler,
        };
    }
}

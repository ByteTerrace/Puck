using System.Collections.Frozen;
using System.CommandLine;
using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// Aggregates command definitions from a set of modules and provides the single surface through which
/// commands are driven and queried.
/// </summary>
/// <remarks>
/// The registry exposes three cooperating facets over the same set of definitions:
/// <list type="bullet">
/// <item><description><b>Snapshots.</b> <see cref="ApplySnapshot"/> dispatches one fixed-step tick's entries. The <see cref="InputRouter"/>'s mixer is the only producer of those snapshots, resolves each slot's active command maps before building them, and stamps every entry with a <see cref="CommandPrincipal"/>.</description></item>
/// <item><description><b>Text.</b> <see cref="Submit"/> parses a line and runs the matching handler as <see cref="CommandPrincipal.Console"/>. This path performs no I/O and is never gated by command maps.</description></item>
/// <item><description><b>Maps.</b> Definitions classify commands into immutable maps; each <see cref="InputRouter"/> owns the active modality independently for every logical slot.</description></item>
/// </list>
/// There is no fourth door: dispatch requires a <see cref="CommandContext"/>, which only this type and the mixer can
/// build, and <see cref="Definitions"/> hands out <see cref="CommandMetadata"/> rather than an invocable handler.
/// </remarks>
public sealed class CommandRegistry {
    private readonly Dictionary<string, CommandDefinition> m_byName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Command, CommandDefinition> m_byTextCommand = [];
    // The registry's own verb names, declared once because they are used TWICE — to construct the built-in Command and
    // to claim the name against module collision. Two hand-transcribed copies would let a rename guard a name nothing
    // dispatches, silently reopening the wire-path hijack the claim exists to prevent.
    private const string HelpCommandName = "help";
    private const string WireAckCommandName = "wire.ack";
    private const string WireErrorsCommandName = "wire.errors";

    private readonly Command m_helpCommand = new(
        name: HelpCommandName,
        description: "Lists the available commands."
    );
    private readonly ICommandObserver[] m_observers;
    private readonly RootCommand m_root = new(description: "Puck commands.");
    // The public read-only face of the registered set, materialized once at construction: a listing verb and the
    // binding vocabulary read these facts, and neither is handed anything invocable.
    private readonly CommandMetadata[] m_metadata;
    private readonly CommandDefinition[] m_definitionById;
    private readonly CommandMetadata[] m_metadataById;
    private readonly Dictionary<string, int> m_mapIndexByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly int[] m_mapIndexById;
    private readonly string[] m_mapNames;
    // Interned command identity: a stable ushort id per command, assigned by ordinal-sorting the canonical
    // names so the id↔name mapping is identical on every machine. This is the command's deterministic,
    // hashable, wire-compact identity in a CommandSnapshot — strings stay on the text/config side.
    private readonly Dictionary<string, ushort> m_idByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly string[] m_nameById;
    // The wire-native dispatch table (see Submit): every command built via CommandDefinition.WithWireArgs, keyed
    // ORDINAL by its name and each alias — ordinal because System.CommandLine matches command
    // names case-SENSITIVELY, so a case-insensitive key here would wire-dispatch a line the full parse would reject. Frozen
    // once at construction (read-only, read-heavy). A miss falls through to the unchanged System.CommandLine parse.
    private readonly FrozenDictionary<string, CommandDefinition> m_wirePath;
    // The span-keyed alternate view over m_wirePath: the wire path looks a verb up by the line's leading-token SPAN, so
    // the verb token never materializes as a string. StringComparer.Ordinal supplies the IAlternateEqualityComparer that
    // makes this legal; built once, reused every dispatch.
    private readonly FrozenDictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_wirePathAlt;
    // The Digital impulse most wire-native verbs carry, hoisted so those contexts do not recompute it.
    private static readonly CommandValue DigitalImpulse = CommandValue.Digital(active: true);
    // The wire acknowledgement mode: false (the default) echoes every accepted line exactly as before; true (`wire.ack
    // quiet`) drops the SUCCESS acks of wire-native verbs, so a flood of accepted commands costs no echo bytes. Errors
    // and answer-bearing verbs (anything not AcknowledgementOnly) are never suppressed. Toggled by the built-in `wire.ack` verb.
    private bool m_acksQuiet;
    // The built-in `wire.ack [on|quiet]` verb, registered beside `help`: it reports or flips m_acksQuiet. Handled inline
    // in Submit (like help), so it never enters a module or the wire-native path.
    private readonly Argument<string[]> m_wireAckArgument = new(name: "mode") {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "on | quiet",
    };
    private readonly Command m_wireAckCommand;
    // The built-in `wire.errors [reset]` verb, registered beside `help`/`wire.ack`: it reports (or clears) the count of
    // submitted lines this registry REFUSED. Every rejection — an unknown verb, a parse error, a handler's IsError
    // result on either dispatch path, a Simulation re-parse that failed to reach its handler, and a host's DEFERRED
    // refusal reported through NoteDeferredRejection — increments the same counter, so a scripted driver reads one
    // number back instead of pattern-matching free-form error text.
    private readonly Argument<string[]> m_wireErrorsArgument = new(name: "mode") {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "reset",
    };
    private readonly Command m_wireErrorsCommand;
    private int m_rejections;
    // The deterministic-input sink a Simulation-class submitted command is folded into instead of running inline;
    // null until a host wires one (the live console-driving registry), so every other registry keeps the inline path.
    // The sink carries its OWN bound principal — this field never chooses one.
    private CommandInjectionSink? m_injectionSink;
    // TextCommandSource uses this as a FIFO barrier: after it submits a deferred simulation mutation, later
    // Immediate-routed stdin lines stay queued until the mutation's snapshot has actually applied. Further
    // Simulation-routed lines keep draining — they fold into the same pending snapshot in FIFO order.
    private int m_pendingSimulationSubmissions;
    // The span-keyed alternate view over m_byName, so RoutesToSimulation classifies a line's verb token without
    // materializing it. Built once at construction, after registration completes.
    private readonly Dictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_byNameAlt;

    /// <summary>The cap on whitespace-delimited tokens the wire path handles from a <see langword="stackalloc"/> buffer;
    /// a line with more falls through to the full parse. Far above any real console verb's token count.</summary>
    private const int MaxWireTokens = 16;
    private const int MaxCommandCount = ushort.MaxValue + 1;
    // The attributed owner name for the registry's own built-in command names (help, wire.ack, wire.errors) in the
    // ClaimName ledger — so a colliding module's error message names the true owner rather than an empty module list.
    private const string BuiltInOwnerName = "CommandRegistry";

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandRegistry"/> class, registering the commands
    /// supplied by the given modules.
    /// </summary>
    /// <param name="modules">The modules whose command definitions are aggregated.</param>
    /// <param name="observers">Observers notified after each command dispatch; defaults to none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="modules"/> is <see langword="null"/>.</exception>
    public CommandRegistry(
        IEnumerable<ICommandModule> modules,
        IEnumerable<ICommandObserver>? observers = null
    ) {
        ArgumentNullException.ThrowIfNull(modules);

        m_observers = ((observers is null)
            ? []
            : ((observers as ICommandObserver[]) ?? observers.ToArray()));

        // Attribution for the loud-failure name guard below: which owner first claimed a given command
        // name/alias. Ctor-scoped — the registry is immutable once built, so nothing after this loop can
        // introduce a new collision. The registry's own built-ins claim their names FIRST, so a module that
        // declares e.g. "wire.errors" collides and throws exactly like colliding with another module.
        var claimedBy = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        ClaimName(
            name: HelpCommandName,
            owner: BuiltInOwnerName,
            claimedBy: claimedBy
        );
        ClaimName(
            name: WireAckCommandName,
            owner: BuiltInOwnerName,
            claimedBy: claimedBy
        );
        ClaimName(
            name: WireErrorsCommandName,
            owner: BuiltInOwnerName,
            claimedBy: claimedBy
        );

        var commandCount = 0;

        foreach (var module in modules) {
            var moduleName = module.GetType().Name;

            foreach (var definition in module.GetCommands()) {
                if (commandCount == MaxCommandCount) {
                    throw new InvalidOperationException(message: $"A command registry supports at most {MaxCommandCount} distinct commands because snapshot command ids are 16-bit.");
                }

                commandCount++;

                // The loud-completeness gate for the bindability axis: a registration that declared nothing would
                // otherwise land on whichever member sits at 0, silently deciding whether an authority verb is
                // reachable from a binding page. Refuse it BY NAME instead — this is a composition-root error.
                if (definition.Bindability == CommandBindability.Unspecified) {
                    throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) declares no bindability. Every registration must pass CommandBindability.Bindable or CommandBindability.Unbindable.");
                }

                if (
                    !Enum.IsDefined(value: definition.Bindability) ||
                    !Enum.IsDefined(value: definition.Routing) ||
                    !Enum.IsDefined(value: definition.ValueKind)
                ) {
                    throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) declares an invalid bindability, routing, or value kind.");
                }

                if (string.IsNullOrWhiteSpace(value: definition.Map)) {
                    throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) declares an empty command map.");
                }

                m_root.Subcommands.Add(item: definition.TextCommand);
                m_byTextCommand[definition.TextCommand] = definition;
                ClaimName(
                    name: definition.Name,
                    owner: moduleName,
                    claimedBy: claimedBy
                );
                m_byName[definition.Name] = definition;

                foreach (var alias in definition.Aliases) {
                    ClaimName(
                        name: alias,
                        owner: moduleName,
                        claimedBy: claimedBy
                    );
                    m_byName[alias] = definition;
                    definition.TextCommand.Aliases.Add(item: alias);
                }
            }
        }

        m_root.Subcommands.Add(item: m_helpCommand);

        // The wire's own control verb, beside help: `wire.ack [on|quiet]` reports or flips the acknowledgement mode.
        m_wireAckCommand = new Command(
            description: "Sets or reports the stdin acknowledgement mode: wire.ack [on|quiet] — `on` (default) echoes every accepted command; `quiet` drops the success acks of side-effecting verbs (errors and query verbs like player.where still echo); no argument reports the current mode.",
            name: WireAckCommandName
        ) {
            m_wireAckArgument,
        };
        m_root.Subcommands.Add(item: m_wireAckCommand);

        // The wire's rejection readback, beside wire.ack: `wire.errors [reset]`.
        m_wireErrorsCommand = new Command(
            description: "Reports the number of submitted lines this session REFUSED (unknown verb, parse error, a handler's failure result, or a deferred refusal a host raised a tick after accepting the line): wire.errors [reset] — no argument reports the running count; `reset` reports it and zeroes the counter. A scripted run asserts `[wire.errors: 0 rejected]` to prove no step silently no-opped.",
            name: WireErrorsCommandName
        ) {
            m_wireErrorsArgument,
        };
        m_root.Subcommands.Add(item: m_wireErrorsCommand);

        // Intern a stable id per distinct command. Ordinal-sort the canonical names so the assignment is
        // identical across machines and builds (independent of module registration order); aliases resolve to
        // their command's id. `help` is handled by the text path and is never bound to input, so it is not interned.
        m_nameById = m_byName.Values
            .Select(selector: static definition => definition.Name)
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
            .OrderBy(
            keySelector: static name => name,
            comparer: StringComparer.Ordinal
        )
            .ToArray();

        for (var id = 0; (id < m_nameById.Length); id++) {
            m_idByName[m_nameById[id]] = (ushort)id;
        }

        foreach (var (name, definition) in m_byName) {
            m_idByName[name] = m_idByName[definition.Name];
        }

        m_definitionById = new CommandDefinition[m_nameById.Length];
        m_mapIndexById = new int[m_nameById.Length];
        m_metadataById = new CommandMetadata[m_nameById.Length];
        var mapNames = new List<string> { CommandMaps.Global };

        m_mapIndexByName[CommandMaps.Global] = 0;

        for (var id = 0; (id < m_nameById.Length); id++) {
            var definition = m_byName[m_nameById[id]];

            if (!m_mapIndexByName.TryGetValue(
                key: definition.Map,
                value: out var mapIndex
            )) {
                mapIndex = mapNames.Count;
                m_mapIndexByName.Add(
                    key: definition.Map,
                    value: mapIndex
                );
                mapNames.Add(item: definition.Map);
            }

            m_definitionById[id] = definition;
            m_mapIndexById[id] = mapIndex;
            m_metadataById[id] = definition.Metadata;
        }

        m_mapNames = [.. mapNames];
        DefaultModality = CompileModality(activeMaps: []);

        // The wire-native table: every name/alias whose definition carries a WireArgs handler. Immediate commands
        // dispatch through it now; Simulation commands use the same tokenization before injection and again when the
        // tick applies, avoiding two System.CommandLine object graphs while preserving the original line as payload.
        // Ordinal-keyed to mirror
        // System.CommandLine's case-sensitive command matching. m_byName's keys carry each name and alias verbatim.
        var wirePath = new Dictionary<string, CommandDefinition>(comparer: StringComparer.Ordinal);

        foreach (var (name, definition) in m_byName) {
            if (definition.WireArgsHandler is not null) {
                wirePath[name] = definition;
            }
        }

        m_wirePath = wirePath.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        m_wirePathAlt = m_wirePath.GetAlternateLookup<ReadOnlySpan<char>>();
        m_byNameAlt = m_byName.GetAlternateLookup<ReadOnlySpan<char>>();
        m_metadata = m_byTextCommand.Values
            .Select(selector: static definition => definition.Metadata)
            .OrderBy(
            keySelector: static metadata => metadata.Name,
            comparer: StringComparer.Ordinal
        )
            .ToArray();

    }

    // Fails loudly when a command name or alias is claimed by more than one owner — a module, or the registry's
    // own built-ins (see BuiltInOwnerName). A name is a unique identity — construction is a composition-root
    // error, so a second claim (even by the same module registering itself twice) is a bug in how modules were
    // assembled.
    private static void ClaimName(string name, string owner, Dictionary<string, string> claimedBy) {
        if (claimedBy.TryGetValue(
            key: name,
            value: out var existingOwner
        )) {
            throw new InvalidOperationException(message: $"Command name '{name}' is registered by both {existingOwner} and {owner}.");
        }

        claimedBy[name] = owner;
    }

    /// <summary>Whether accepted-command acks are echoed. <see langword="false"/> once <c>wire.ack quiet</c> is set — a
    /// wire-native handler reads this (via <see cref="WireArgs.Echo"/>) to skip building a success echo it would drop.</summary>
    internal bool AcksEnabled => !m_acksQuiet;

    /// <summary>The number of distinct commands; each has an interned id in <c>[0, <see cref="CommandCount"/>)</c>.</summary>
    public int CommandCount => m_nameById.Length;

    /// <summary>Gets the stable interned id for a command name or alias.</summary>
    /// <param name="name">The command name or alias to resolve.</param>
    /// <param name="id">When this method returns, the interned id, or <c>0</c> when the name is unknown.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a known command; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetId(string name, out ushort id) {
        ArgumentNullException.ThrowIfNull(name);

        return m_idByName.TryGetValue(
            key: name,
            value: out id
        );
    }
    /// <summary>Gets the declared facts for a command name or alias — the affordance-vocabulary lookup
    /// <see cref="BindingVocabularyCheck"/> consumers resolve a binding document's <c>Command</c> strings through.
    /// Covers exactly the names <see cref="TryGetId"/> can dispatch (module-registered commands and their aliases;
    /// the registry's own text-path built-ins are never bindable and never answer here).</summary>
    /// <param name="name">The command name or alias to resolve.</param>
    /// <param name="metadata">When this method returns, the command's declared facts, or the default when the name
    /// is unknown.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a registered command; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetMetadata(string name, out CommandMetadata metadata) {
        ArgumentNullException.ThrowIfNull(name);

        if (m_idByName.TryGetValue(key: name, value: out var id)) {
            metadata = m_metadataById[id];

            return true;
        }

        metadata = default;

        return false;
    }

    /// <summary>Gets the distinct registered commands' declared facts, ordinal-sorted by name — the affordance manifest
    /// source a listing verb (e.g. <c>world.affordances</c>) emits as data. Excludes the registry's own text-path
    /// built-ins (<c>help</c>/<c>wire.ack</c>/<c>wire.errors</c>), which are never bindable.</summary>
    /// <remarks>Metadata only, never a handler. A caller that could reach a definition's handler could invoke an authority
    /// verb with a context of its own making, which would be a dispatch door beside the stamped ones; describing the
    /// vocabulary must not confer the ability to drive it.</remarks>
    public IReadOnlyList<CommandMetadata> Definitions => m_metadata;

    /// <summary>Gets the canonical name for an interned command id.</summary>
    /// <param name="id">The interned id, in <c>[0, <see cref="CommandCount"/>)</c>.</param>
    /// <returns>The command's canonical name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a valid interned id.</exception>
    public string GetName(ushort id) {
        if ((int)id >= m_nameById.Length) {
            throw new ArgumentOutOfRangeException(paramName: nameof(id));
        }

        return m_nameById[id];
    }

    /// <summary>Returns the default "fully active" value used for a text invocation that supplies no explicit value.</summary>
    /// <param name="kind">The value kind of the command being invoked.</param>
    /// <returns>An active value for digital and axis kinds; an inactive value for kinds that have no meaningful impulse.</returns>
    private static CommandValue ImpulseValue(CommandValueKind kind) {
        return kind switch {
            CommandValueKind.Digital => DigitalImpulse,
            CommandValueKind.Axis1D => CommandValue.Axis(value: 1f),
            CommandValueKind.Axis2D => CommandValue.Axis(value: Vector2.One),
            _ => CommandValue.Inactive(kind: kind),
        };
    }

    /// <summary>Gets a command line's first token under the same <see cref="char.IsWhiteSpace(char)"/> rule the full
    /// wire-native tokenizer uses.</summary>
    /// <param name="line">The command line.</param>
    /// <returns>The leading verb, or an empty span for a blank line.</returns>
    private static ReadOnlySpan<char> LeadingVerb(ReadOnlySpan<char> line) {
        line = line.TrimStart();

        for (var index = 0; (index < line.Length); index++) {
            if (char.IsWhiteSpace(c: line[index])) {
                return line[..index];
            }
        }

        return line;
    }

    /// <summary>Splits a line into whitespace-delimited token ranges without allocating. A token is a maximal run of
    /// non-whitespace characters (<see cref="char.IsWhiteSpace(char)"/>, matching <see cref="string.Split(char[], StringSplitOptions)"/>'s
    /// null-separator semantics exactly), so this reproduces the System.CommandLine tokenizer for unquoted input.
    /// Fills <paramref name="tokens"/> with one <see cref="Range"/> per token.</summary>
    /// <param name="line">The line to tokenize.</param>
    /// <param name="tokens">The destination span (capacity <see cref="MaxWireTokens"/>).</param>
    /// <returns>The token count, or <c>-1</c> when the line has more tokens than <paramref name="tokens"/> can hold
    /// (the caller then falls through to the full parse).</returns>
    private static int Tokenize(ReadOnlySpan<char> line, Span<Range> tokens) {
        var count = 0;
        var index = 0;

        while (index < line.Length) {
            while (
                (index < line.Length) &&
                char.IsWhiteSpace(c: line[index])
            ) {
                index++;
            }

            if (index >= line.Length) {
                break;
            }

            var start = index;

            while (
                (index < line.Length) &&
                !char.IsWhiteSpace(c: line[index])
            ) {
                index++;
            }

            if (count >= tokens.Length) {
                return -1;
            }

            tokens[count++] = new Range(
                start: start,
                end: index
            );
        }

        return count;
    }

    private bool TryResolveWireLine(string line, Span<Range> tokenRanges, out CommandDefinition? definition, out int tokenCount) {
        definition = null;
        tokenCount = 0;

        if (
            (line.IndexOf(value: '"') >= 0) ||
            (line.IndexOf(value: '@') >= 0)
        ) {
            return false;
        }

        tokenCount = Tokenize(
            line: line,
            tokens: tokenRanges
        );

        return (
            (tokenCount > 0) &&
            m_wirePathAlt.TryGetValue(
                key: LeadingVerb(line: line),
                value: out definition
            )
        );
    }

    private CommandResult DispatchWire(
        CommandDefinition definition,
        string line,
        ReadOnlySpan<Range> argumentRanges,
        CommandPrincipal principal,
        int slot,
        bool observe,
        string? contextText = null
    ) {
        var quiet = (m_acksQuiet && definition.AcknowledgementOnly);
        var context = new CommandContext(
            origin: CommandOrigin.Text,
            parse: null,
            phase: CommandPhase.Completed,
            principal: principal,
            registry: this,
            slot: slot,
            text: contextText,
            value: ImpulseValue(kind: definition.ValueKind)
        );
        var result = definition.WireArgsHandler!(
            arg1: context,
            arg2: new WireArgs(
                line: line,
                ranges: argumentRanges,
                echo: !quiet
            )
        );

        result = SuppressAckIfQuiet(
            definition: definition,
            result: result
        );

        if (observe) {
            NotifyObservers(
                context: in context,
                definition: definition,
                result: result
            );
        }

        return result;
    }

    /// <summary>Reports or flips the wire acknowledgement mode for the built-in <c>wire.ack</c> verb.</summary>
    /// <param name="mode">The parsed trailing tokens: empty reports the current mode; <c>on</c>/<c>quiet</c> set it.</param>
    /// <returns>A result echoing the resulting mode, or an <see cref="CommandResult.IsError"/> result for a bad argument.</returns>
    private CommandResult ApplyWireAck(string[] mode) {
        if (mode.Length == 0) {
            return new CommandResult((m_acksQuiet
                ? "[wire.ack: quiet]"
                : "[wire.ack: on]"));
        }

        if (mode.Length > 1) {
            return CommandResult.Error(output: "[wire.ack: expected one of on | quiet]");
        }

        switch (mode[0]) {
            case "on":
                m_acksQuiet = false;

                return new CommandResult(Output: "[wire.ack: on]");
            case "quiet":
                m_acksQuiet = true;

                return new CommandResult(Output: "[wire.ack: quiet]");
            default:
                return CommandResult.Error(output: $"[wire.ack: unknown mode '{mode[0]}' — expected on | quiet]");
        }
    }

    /// <summary>Reports (and optionally clears) the refused-submission count for the built-in <c>wire.errors</c> verb.</summary>
    /// <param name="mode">The parsed trailing tokens: empty reports the count; <c>reset</c> reports and zeroes it.</param>
    /// <returns>A result echoing the count, or an <see cref="CommandResult.IsError"/> result for a bad argument.</returns>
    private CommandResult ApplyWireErrors(string[] mode) {
        if (mode.Length > 1) {
            return CommandResult.Error(output: "[wire.errors: expected no argument or `reset`]");
        }

        if (
            (mode.Length == 1) &&
            !string.Equals(
            a: mode[0],
            b: "reset",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return CommandResult.Error(output: $"[wire.errors: unknown mode '{mode[0]}' — expected `reset`]");
        }

        // Read the count BEFORE `reset` zeroes it, and do not let this verb's own report count as a rejection.
        var rejected = m_rejections;

        if (mode.Length == 1) {
            m_rejections = 0;
        }

        return new CommandResult(Output: $"[wire.errors: {rejected} rejected]");
    }

    /// <summary>Counts one refusal that a submitted line's own dispatch could not report — a deferred rejection, raised
    /// after the line was accepted (a host queued the work and refused it later).</summary>
    /// <remarks>
    /// Call this only from a host's rejection tap, and only for an outcome no handler returned as
    /// <see cref="CommandResult.IsError"/>: a line that fails synchronously is already counted by
    /// <see cref="Submit"/>, so counting it here too would double-count it. The count is the one
    /// <c>wire.errors</c> reports.
    /// </remarks>
    public void NoteDeferredRejection() {
        m_rejections++;
    }

    /// <summary>Builds the help listing of every registered command and its description, ordered by name.</summary>
    /// <returns>A newline-separated list of <c>name - description</c> entries.</returns>
    private string BuildHelpText() {
        return string.Join(
            separator: '\n',
            values: m_root.Subcommands
                .OrderBy(
                comparer: StringComparer.OrdinalIgnoreCase,
                keySelector: command => command.Name
            )
                .Select(selector: command => $"{command.Name} - {command.Description}")
        );
    }

    /// <summary>Gets the registered command-map names. <see cref="CommandMaps.Global"/> is always first.</summary>
    public IReadOnlyList<string> Maps => m_mapNames;

    internal CommandModality DefaultModality { get; }

    internal CommandModality CreateModality(ReadOnlySpan<string> activeMaps) {
        return ((activeMaps.Length == 0)
            ? DefaultModality
            : CompileModality(activeMaps: activeMaps));
    }
    private CommandModality CompileModality(ReadOnlySpan<string> activeMaps) {
        var mapActivity = new bool[m_mapNames.Length];

        mapActivity[0] = true;

        foreach (var map in activeMaps) {
            if (
                (map is null) ||
                !m_mapIndexByName.TryGetValue(
                    key: map,
                    value: out var mapIndex
                )
            ) {
                throw new ArgumentException(message: $"Command map '{map ?? "(null)"}' is not registered.", paramName: nameof(activeMaps));
            }

            mapActivity[mapIndex] = true;
        }

        var commandActivity = new bool[m_mapIndexById.Length];

        for (var id = 0; (id < commandActivity.Length); id++) {
            commandActivity[id] = mapActivity[m_mapIndexById[id]];
        }

        return new CommandModality(
            activeMaps: mapActivity,
            activeCommands: commandActivity
        );
    }
    internal bool IsMapActive(CommandModality modality, string map) {
        return (
            m_mapIndexByName.TryGetValue(
                key: map,
                value: out var mapIndex
            ) &&
            modality.ActiveMaps[mapIndex]
        );
    }

    /// <summary>Whether a submitted simulation command is waiting for its fixed-step snapshot to apply.</summary>
    internal bool HasPendingSimulationSubmission => (m_pendingSimulationSubmissions != 0);
    /// <summary>Whether the line's verb resolves to a <see cref="CommandRouting.Simulation"/>-routed command. Such a
    /// line may drain behind an unapplied deferred mutation (it folds into the same pending snapshot, FIFO); an
    /// unresolved or <see cref="CommandRouting.Immediate"/> line reads applied state, so it must wait.</summary>
    /// <param name="line">The command line whose leading verb token is classified.</param>
    internal bool RoutesToSimulation(string line) {
        var verb = LeadingVerb(line: line);

        if (verb.IsEmpty) {
            return false;
        }

        return (
            m_byNameAlt.TryGetValue(
            key: verb,
            value: out var definition
        ) &&
            (definition.Routing == CommandRouting.Simulation)
        );
    }
    /// <summary>
    /// Applies one fixed-step tick's <see cref="CommandSnapshot"/>. The <see cref="InputRouter"/> has already resolved
    /// per-slot command maps and owns held-folding, so this never touches modality or held state, and
    /// each entry's <see cref="CommandEntry.Principal"/> — stamped by the mixer or by the injecting sink — becomes
    /// the handler's <see cref="CommandContext.Principal"/> verbatim.
    /// <para>This method stays public — the launcher's fixed-step pump is a different assembly — because the
    /// argument is what is closed, not the method: <see cref="CommandSnapshot"/>, <see cref="CommandLane"/>, and
    /// <see cref="CommandEntry"/> are all internal to construct, so the only snapshot a caller can obtain is one the
    /// mixer built. Narrowing this method instead would leave the forgeable value type in a caller's hands.</para>
    /// </summary>
    /// <param name="snapshot">The tick's input snapshot to apply.</param>
    /// <exception cref="ArgumentException"><paramref name="snapshot"/> was produced for a different registry.</exception>
    public void ApplySnapshot(in CommandSnapshot snapshot) {
        if (snapshot.Lanes.IsEmpty) {
            return;
        }

        if (!ReferenceEquals(
            objA: snapshot.Registry,
            objB: this
        )) {
            throw new ArgumentException(
                message: "The snapshot was produced for a different command registry.",
                paramName: nameof(snapshot)
            );
        }

        foreach (var lane in snapshot.Lanes) {
            foreach (var entry in lane.Entries) {
                if ((int)entry.CommandId >= m_nameById.Length) {
                    continue;
                }

                // A submitted text entry owns a FIFO-barrier count. Always route it through the completion helper first:
                // even a defensive name-table miss must reach that helper's finally block and release the barrier.
                if (entry.Text is { } line) {
                    if (entry.Dispatch) {
                        ApplySubmittedSimulation(
                            line: line,
                            expectedCommandId: entry.CommandId,
                            principal: entry.Principal,
                            slot: lane.Slot,
                            completesTextSubmission: entry.CompletesTextSubmission,
                            submissionBarrier: entry.SubmissionBarrier
                        );
                    } else if (
                        entry.CompletesTextSubmission
                    ) {
                        ReleaseSubmissionBarrier(completesTextSubmission: true);
                    }

                    continue;
                }

                var definition = m_definitionById[entry.CommandId];

                if (!entry.Dispatch) {
                    continue;
                }

                var context = new CommandContext(
                    assignedSlot: entry.AssignedSlot,
                    deviceId: entry.Device,
                    origin: entry.Origin,
                    parse: null,
                    phase: entry.Phase,
                    principal: entry.Principal,
                    registry: this,
                    slot: lane.Slot,
                    source: entry.Source,
                    text: null,
                    value: entry.Value
                );

                _ = Dispatch(
                    context: in context,
                    definition: definition
                );
            }
        }
    }
    // Executes a simulation-routed text command from its tick snapshot. Submit already parsed and identified the line
    // before injection; parsing again here recreates the handler's ordinary text context without re-routing it. The
    // principal rides the entry rather than being re-derived: the door that queued the line already stamped it.
    private void ApplySubmittedSimulation(string line, ushort expectedCommandId, CommandPrincipal principal, int slot, bool completesTextSubmission, TextSubmissionBarrier? submissionBarrier) {
        try {
            Span<Range> tokenRanges = stackalloc Range[MaxWireTokens];

            if (
                TryResolveWireLine(
                    line: line,
                    tokenRanges: tokenRanges,
                    definition: out var wireDefinition,
                    tokenCount: out var tokenCount
                ) &&
                (expectedCommandId < m_definitionById.Length) &&
                ReferenceEquals(
                    objA: wireDefinition,
                    objB: m_definitionById[expectedCommandId]
                )
            ) {
                var wireResult = DispatchWire(
                    definition: wireDefinition!,
                    line: line,
                    argumentRanges: tokenRanges[1..tokenCount],
                    principal: principal,
                    slot: slot,
                    observe: true,
                    contextText: line
                );

                if (wireResult.IsError) {
                    m_rejections++;
                }

                return;
            }

            var parseResult = m_root.Parse(commandLine: line);

            if (
                (parseResult.Errors.Count != 0) ||
                !m_byTextCommand.TryGetValue(
                key: parseResult.CommandResult.Command,
                value: out var definition
            ) ||
                (expectedCommandId >= m_definitionById.Length) ||
                !ReferenceEquals(
                    objA: definition,
                    objB: m_definitionById[expectedCommandId]
                )
            ) {
                // A snapshot-routed line that no longer re-parses to the command it was injected as never reaches its
                // handler. Submit already returned None for it, so this is the only place it can be counted — without
                // it a Simulation-routed rejection stays invisible to wire.errors.
                m_rejections++;

                return;
            }

            var value = ImpulseValue(kind: definition.ValueKind);
            var context = new CommandContext(
                origin: CommandOrigin.Text,
                parse: parseResult,
                phase: CommandPhase.Completed,
                principal: principal,
                registry: this,
                slot: slot,
                text: line,
                value: value
            );

            // Submit returned None when it injected this line, so its handler's verdict lands here rather than at the
            // console call site — count a failure so a deferred mutation's rejection reaches wire.errors too.
            if (Dispatch(
                context: in context,
                definition: definition,
                suppressWireAck: true
            ).IsError) {
                m_rejections++;
            }
        } finally {
            if (submissionBarrier is not null) {
                submissionBarrier.Complete();
            } else {
                ReleaseSubmissionBarrier(completesTextSubmission: completesTextSubmission);
            }
        }
    }
    // Releases one submitted simulation line's FIFO barrier exactly once. The defensive non-zero guard keeps a
    // malformed or repeated completion from underflowing and blocking every later immediate read-back forever.
    private void ReleaseSubmissionBarrier(bool completesTextSubmission) {
        if (
            completesTextSubmission &&
            (m_pendingSimulationSubmissions != 0)
        ) {
            m_pendingSimulationSubmissions--;
        }
    }
    // The one definition of the wire.ack-quiet suppression rule, applied on every text dispatch path (fast, full
    // parse, snapshot re-dispatch): in quiet mode a successful acknowledgement-only result carries no answer, so drop
    // it to None. An error (IsError) and an answer-bearing verb's output are never suppressed.
    private CommandResult SuppressAckIfQuiet(CommandResult result, CommandDefinition definition) {
        return ((m_acksQuiet && definition.AcknowledgementOnly && !result.IsError)
            ? CommandResult.None
            : result);
    }

    /// <summary>Runs a command's handler and notifies every observer of the dispatch.</summary>
    /// <param name="context">The invocation state passed to the handler.</param>
    /// <param name="definition">The command being dispatched.</param>
    /// <param name="suppressWireAck">Whether quiet wire mode may suppress a successful acknowledgement.</param>
    /// <returns>The result the handler returned.</returns>
    private CommandResult Dispatch(in CommandContext context, CommandDefinition definition, bool suppressWireAck = false) {
        var result = definition.Handler(arg: context);

        if (suppressWireAck) {
            result = SuppressAckIfQuiet(
                definition: definition,
                result: result
            );
        }

        NotifyObservers(
            context: in context,
            definition: definition,
            result: result
        );

        return result;
    }
    private void NotifyObservers(in CommandContext context, CommandDefinition definition, CommandResult result) {
        if (m_observers.Length == 0) {
            return;
        }

        var activation = new CommandActivation(
            Name: definition.Name,
            Phase: context.Phase,
            Result: result,
            Text: context.Text,
            Principal: context.Principal,
            Slot: context.Slot
        );

        for (var index = 0; (index < m_observers.Length); index++) {
            m_observers[index].OnCommand(activation: in activation);
        }
    }
    /// <summary>
    /// Routes <see cref="CommandRouting.Simulation"/>-class submitted commands to a deterministic input sink instead
    /// of running them inline — the seam that makes a console / STDIN line drive the simulation deterministically.
    /// </summary>
    /// <param name="sink">The console text door's sink (<see cref="InputRouter.ConsoleTextSink"/>), folded-into per
    /// tick; <see langword="null"/> restores inline execution.</param>
    /// <remarks>Wire this only on the host's live console-driving registry; an unwired registry runs every submitted
    /// command inline. The sink's principal is fixed at its own construction, so nothing here (or at a call site)
    /// chooses what a submitted line acts as.</remarks>
    public void RouteSimulationTo(CommandInjectionSink? sink) {
        m_injectionSink = sink;
    }
    /// <summary>Parses a command line, runs the matching handler, and returns its transcript output.</summary>
    /// <param name="line">The command line to parse and execute.</param>
    /// <returns>
    /// The handler's result; <see cref="CommandResult.None"/> for an empty or whitespace line; the help
    /// listing for the <c>help</c> command; no immediate result for a simulation command routed to the deterministic
    /// input path (its real result is produced when its tick is applied); or a message describing parse errors or an
    /// unknown command.
    /// </returns>
    /// <remarks>
    /// This path is never gated by command maps; it is the deliberate console entry point. A
    /// <see cref="CommandRouting.Simulation"/> command is injected into the per-tick <see cref="CommandSnapshot"/>
    /// (so it is tick-aligned and applied deterministically) when a sink is wired via <see cref="RouteSimulationTo"/>;
    /// otherwise — and for every <see cref="CommandRouting.Immediate"/> command — the handler runs inline.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public CommandResult Submit(string line) {
        ArgumentNullException.ThrowIfNull(line);

        return SubmitStamped(line: line, session: null);
    }

    /// <summary>Determines whether an interned command belongs to the host's focus-exempt control plane.</summary>
    internal bool IsFocusExemptCommand(ushort commandId) =>
        (commandId < m_metadataById.Length) &&
        (m_metadataById[commandId].InputScope == CommandInputScope.FocusExempt);

    internal CommandResult SubmitSession(string line, TextCommandSession session) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);

        return SubmitStamped(line: line, session: session);
    }

    private CommandResult SubmitStamped(string line, TextCommandSession? session) {
        var result = SubmitCore(line: line, session: session);

        // The one place every text-path outcome is visible: count each failure so `wire.errors` can report it. This
        // covers the registry's own refusals AND a module handler's IsError result on either dispatch path.
        if (result.IsError) {
            m_rejections++;
        }

        return result;
    }

    // Submit's body. Submit itself owns the rejection accounting so no return path here has to remember to count.
    private CommandResult SubmitCore(string line, TextCommandSession? session) {
        if (string.IsNullOrWhiteSpace(value: line)) {
            return CommandResult.None;
        }

        var principal = (session?.Principal ?? CommandPrincipal.Console);
        var slot = (session?.Slot ?? 0);

        // WIRE-NATIVE PATH for the plain `verb arg arg…` line shape — skips the System.CommandLine parse (measured
        // ~5.2 µs + ~8.6 KB per line at the World's command surface), the measured cause of stdin burst dips. Eligible
        // when the line carries neither `"` nor `@` and exactly names a WithWireArgs command. Immediate commands run
        // now; Simulation commands inject the original line and use the same path when their tick applies.
        //
        // ZERO-COPY: the line is tokenized into a stackalloc Span<Range> (Tokenize reproduces
        // Split((char[])null, RemoveEmptyEntries) whitespace semantics exactly), the verb is looked up by its SPAN via
        // the frozen alternate lookup (so the verb token never materializes), and a principal-stamped context is handed to
        // the handler, which receives a zero-copy WireArgs over the trailing token ranges — no substrings, no argument
        // array, nothing heap-allocated by the dispatch itself. The context's Parse is null but Origin remains Text;
        // Value is the command's declared-kind impulse so the few wire-native verbs that also serve value-sensitive
        // bindings observe the same kind on this path and the full parser fallback.
        Span<Range> tokenRanges = stackalloc Range[MaxWireTokens];

        if (TryResolveWireLine(
                line: line,
                tokenRanges: tokenRanges,
                definition: out var wireDefinition,
                tokenCount: out var tokenCount
            )) {
            if (
                (wireDefinition!.Routing == CommandRouting.Simulation) &&
                ((session?.SimulationSink ?? m_injectionSink) is { } wireSink) &&
                TryGetId(
                    name: wireDefinition.Name,
                    id: out var wireCommandId
                )
            ) {
                QueueSimulation(
                    commandId: wireCommandId,
                    line: line,
                    session: session,
                    sink: wireSink,
                    value: ImpulseValue(kind: wireDefinition.ValueKind)
                );

                return CommandResult.None;
            }

            return DispatchWire(
                definition: wireDefinition,
                line: line,
                argumentRanges: tokenRanges[1..tokenCount],
                principal: principal,
                slot: slot,
                observe: false
            );
        }

        var parseResult = m_root.Parse(commandLine: line);

        if (parseResult.Errors.Count > 0) {
            return CommandResult.Error(output: $"[wire.reject: {string.Join(
                separator: " | ",
                values: parseResult.Errors.Select(selector: error => error.Message)
            )}]");
        }

        var command = parseResult.CommandResult.Command;

        if (command == m_helpCommand) {
            return new CommandResult(BuildHelpText());
        }

        if (command == m_wireAckCommand) {
            return ApplyWireAck(mode: (parseResult.GetValue(argument: m_wireAckArgument) ?? []));
        }

        if (command == m_wireErrorsCommand) {
            return ApplyWireErrors(mode: (parseResult.GetValue(argument: m_wireErrorsArgument) ?? []));
        }

        if (m_byTextCommand.TryGetValue(
            key: command,
            value: out var definition
        )) {
            var value = ImpulseValue(kind: definition.ValueKind);

            // A simulation command's effect mutates the deterministic sim, so it must be tick-aligned and recorded:
            // fold it into the snapshot stream rather than run it here. The handler still runs — later, when the
            // host applies that tick's snapshot — so a recording reproduces it. Console impulses inject as a Started
            // edge (the press the snapshot dispatch fires on) on the local slot.
            if (
                (definition.Routing == CommandRouting.Simulation) &&
                ((session?.SimulationSink ?? m_injectionSink) is { } sink) &&
                TryGetId(
                name: definition.Name,
                id: out var commandId
            )
            ) {
                QueueSimulation(
                    commandId: commandId,
                    line: line,
                    session: session,
                    sink: sink,
                    value: value
                );

                return CommandResult.None;
            }

            // The text path returns its result to the caller, so it is not observed (the caller displays
            // it); observers exist for the snapshot-driven path, which has no return value to inspect.
            var result = definition.Handler(arg: new CommandContext(
                origin: CommandOrigin.Text,
                parse: parseResult,
                phase: CommandPhase.Completed,
                principal: principal,
                registry: this,
                slot: slot,
                value: value
            ));

            // A quoted or many-token wire line takes this full parse; it obeys wire.ack through the same rule the fast
            // wire-native path does.
            return SuppressAckIfQuiet(
                definition: definition,
                result: result
            );
        }

        return CommandResult.Error(output: $"[wire.reject: unknown command '{line}']");
    }

    private void QueueSimulation(
        ushort commandId,
        string line,
        TextCommandSession? session,
        CommandInjectionSink sink,
        CommandValue value
    ) {
        if (session is null) {
            m_pendingSimulationSubmissions++;
        } else {
            session.Barrier.Begin();
        }

        try {
            sink.Inject(
                commandId: commandId,
                value: value,
                phase: CommandPhase.Started,
                text: line,
                completesTextSubmission: true,
                submissionBarrier: session?.Barrier
            );
        } catch {
            if (session is null) {
                m_pendingSimulationSubmissions--;
            } else {
                session.Barrier.Complete();
            }

            throw;
        }
    }
}

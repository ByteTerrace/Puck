using System.Collections.Frozen;
using System.CommandLine;
using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// Aggregates command definitions from a set of modules and provides the single surface through which
/// commands are driven, queried, and gated.
/// </summary>
/// <remarks>
/// The registry exposes three cooperating facets over the same set of definitions:
/// <list type="bullet">
/// <item><description><b>Snapshots.</b> <see cref="ApplySnapshot"/> dispatches one fixed-step tick's entries, gated by the active command maps. The <see cref="InputRouter"/>'s mixer is the only producer of those snapshots, and every entry it produces carries a door-stamped <see cref="CommandPrincipal"/>.</description></item>
/// <item><description><b>Text.</b> <see cref="Submit"/> parses a line and runs the matching handler as <see cref="CommandPrincipal.Console"/>. This path performs no I/O and is never gated by command maps.</description></item>
/// <item><description><b>Maps.</b> <see cref="ActivateMap"/> and <see cref="DeactivateMap"/> control modality; only commands in an active map dispatch from a snapshot.</description></item>
/// </list>
/// There is no fourth door: dispatch requires a <see cref="CommandContext"/>, which only this type and the mixer can
/// build, and <see cref="Definitions"/> hands out <see cref="CommandMetadata"/> rather than an invocable handler.
/// </remarks>
public sealed class CommandRegistry {
    private readonly HashSet<string> m_activeMaps = new(comparer: StringComparer.OrdinalIgnoreCase) { CommandMaps.Global };
    private readonly Dictionary<string, CommandDefinition> m_byName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Command, CommandDefinition> m_byTextCommand = [];
    // The registry's own verb names, declared once because they are used TWICE — to construct the built-in Command and
    // to claim the name against module collision. Two hand-transcribed copies would let a rename guard a name nothing
    // dispatches, silently reopening the fast-path hijack the claim exists to prevent.
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
    private readonly FrozenDictionary<string, CommandMetadata> m_metadataByName;
    // Interned command identity: a stable ushort id per command, assigned by ordinal-sorting the canonical
    // names so the id↔name mapping is identical on every machine. This is the command's deterministic,
    // hashable, wire-compact identity in a CommandSnapshot — strings stay on the text/config side.
    private readonly Dictionary<string, ushort> m_idByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly string[] m_nameById;
    // The text-dispatch FAST PATH table (see Submit): every command built via CommandDefinition.WithWireArgs with
    // Immediate routing, keyed ORDINAL by its name and each alias — ordinal because System.CommandLine matches command
    // names case-SENSITIVELY, so a case-insensitive key here would fast-path a line the full parse would reject. Frozen
    // once at construction (read-only, read-heavy). A miss falls through to the unchanged System.CommandLine parse.
    private readonly FrozenDictionary<string, CommandDefinition> m_fastPath;
    // The span-keyed alternate view over m_fastPath: the fast path looks a verb up by the line's leading-token SPAN, so
    // the verb token never materializes as a string. StringComparer.Ordinal supplies the IAlternateEqualityComparer that
    // makes this legal; built once, reused every dispatch.
    private readonly FrozenDictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_fastPathAlt;
    // The Digital impulse most fast-path verbs carry, hoisted so those contexts do not recompute it.
    private static readonly CommandValue DigitalImpulse = CommandValue.Digital(active: true);
    // One immutable, reused context per CommandValueKind. WithWireArgs permits non-digital binding values, and the
    // fast path must expose the same declared kind as the full System.CommandLine fallback without per-line work.
    private readonly CommandContext[] m_fastContexts;
    // The wire acknowledgement mode: false (the default) echoes every accepted line exactly as before; true (`wire.ack
    // quiet`) drops the SUCCESS acks of wire-native verbs, so a flood of accepted commands costs no echo bytes. Errors
    // and answer-bearing verbs (anything not AcknowledgementOnly) are never suppressed. Toggled by the built-in `wire.ack` verb.
    private bool m_acksQuiet;
    // The built-in `wire.ack [on|quiet]` verb, registered beside `help`: it reports or flips m_acksQuiet. Handled inline
    // in Submit (like help), so it never enters a module or the fast path.
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

    /// <summary>The cap on whitespace-delimited tokens the fast path handles from a <see langword="stackalloc"/> buffer;
    /// a line with more falls through to the full parse. Far above any real console verb's token count.</summary>
    private const int MaxFastPathTokens = 16;
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

        // The fast-path table: every name/alias whose definition carries a wire handler (built via
        // CommandDefinition.WithWireArgs) AND runs inline (Immediate routing — a Simulation command must still fold
        // into the snapshot stream, so it is excluded and takes the full parse). Ordinal-keyed to mirror
        // System.CommandLine's case-sensitive command matching. m_byName's keys carry each name and alias verbatim.
        var fastPath = new Dictionary<string, CommandDefinition>(comparer: StringComparer.Ordinal);

        foreach (var (name, definition) in m_byName) {
            if (
                (definition.WireArgsHandler is not null) &&
                (definition.Routing == CommandRouting.Immediate)
            ) {
                fastPath[name] = definition;
            }
        }

        m_fastPath = fastPath.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        m_fastPathAlt = m_fastPath.GetAlternateLookup<ReadOnlySpan<char>>();
        m_byNameAlt = m_byName.GetAlternateLookup<ReadOnlySpan<char>>();
        // The fast path is the TEXT door, so every reused kind-specific context is stamped Console like every other
        // text dispatch. Enum validation above makes the direct indexed lookup safe.
        m_fastContexts = new CommandContext[Enum.GetValues<CommandValueKind>().Length];

        foreach (var kind in Enum.GetValues<CommandValueKind>()) {
            m_fastContexts[(int)kind] = new CommandContext(
                parse: null,
                phase: CommandPhase.Completed,
                principal: CommandPrincipal.Console,
                registry: this,
                value: ImpulseValue(kind: kind)
            );
        }

        m_metadata = m_byTextCommand.Values
            .Select(selector: static definition => definition.Metadata)
            .OrderBy(
            keySelector: static metadata => metadata.Name,
            comparer: StringComparer.Ordinal
        )
            .ToArray();

        var metadataByName = new Dictionary<string, CommandMetadata>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (name, definition) in m_byName) {
            metadataByName[name] = definition.Metadata;
        }

        m_metadataByName = metadataByName.ToFrozenDictionary(comparer: StringComparer.OrdinalIgnoreCase);
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

        return m_metadataByName.TryGetValue(
            key: name,
            value: out metadata
        );
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
    /// fast-path tokenizer uses.</summary>
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
    /// <param name="tokens">The destination span (capacity <see cref="MaxFastPathTokens"/>).</param>
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

    /// <summary>Adds a command map to the active set, enabling source-driven activation of its commands.</summary>
    /// <param name="map">The name of the map to activate. Activating an already-active map has no effect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public void ActivateMap(string map) {
        ArgumentNullException.ThrowIfNull(map);

        _ = m_activeMaps.Add(item: map);
    }
    /// <summary>Removes a command map from the active set.</summary>
    /// <param name="map">The name of the map to deactivate. <see cref="CommandMaps.Global"/> is always active and cannot be removed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public void DeactivateMap(string map) {
        ArgumentNullException.ThrowIfNull(map);

        if (!string.Equals(
            a: map,
            b: CommandMaps.Global,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            _ = m_activeMaps.Remove(item: map);
        }
    }
    /// <summary>Determines whether a command map is currently active.</summary>
    /// <param name="map">The name of the map to test.</param>
    /// <returns><see langword="true"/> if the map is active; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public bool IsMapActive(string map) {
        ArgumentNullException.ThrowIfNull(map);

        return m_activeMaps.Contains(item: map);
    }
    /// <summary>Determines whether a snapshot-driven command id currently belongs to an active map.</summary>
    /// <param name="commandId">The interned command id.</param>
    /// <returns><see langword="true"/> when the command exists and its map is active.</returns>
    internal bool IsSourceCommandActive(ushort commandId) {
        if (commandId >= m_nameById.Length) {
            return false;
        }

        return (
            m_byName.TryGetValue(
            key: m_nameById[commandId],
            value: out var definition
        ) &&
            m_activeMaps.Contains(item: definition.Map)
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
    /// Applies one fixed-step tick's <see cref="CommandSnapshot"/>: dispatches edge handlers, gated by the
    /// active command maps. The <see cref="InputRouter"/> owns held-folding, so this never touches held state, and
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
        if (snapshot.Lanes.IsDefaultOrEmpty) {
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

                var name = m_nameById[entry.CommandId];

                // A submitted text entry owns a FIFO-barrier count. Always route it through the completion helper first:
                // even a defensive name-table miss must reach that helper's finally block and release the barrier.
                if (entry.Text is { } line) {
                    if (entry.Dispatch) {
                        ApplySubmittedSimulation(
                            line: line,
                            expectedCommandId: entry.CommandId,
                            principal: entry.Principal,
                            slot: lane.Slot,
                            completesTextSubmission: entry.CompletesTextSubmission
                        );
                    } else if (
                        entry.CompletesTextSubmission
                    ) {
                        ReleaseSubmissionBarrier(completesTextSubmission: true);
                    }

                    continue;
                }

                if (!m_byName.TryGetValue(
                    key: name,
                    value: out var definition
                )) {
                    continue;
                }

                if (
                    !m_activeMaps.Contains(item: definition.Map) &&
                    !entry.DispatchWhenMapInactive
                ) {
                    continue;
                }

                if (!entry.Dispatch) {
                    continue;
                }

                var context = new CommandContext(
                    assignedSlot: entry.AssignedSlot,
                    deviceId: entry.Device,
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
    private void ApplySubmittedSimulation(string line, ushort expectedCommandId, CommandPrincipal principal, int slot, bool completesTextSubmission) {
        try {
            var parseResult = m_root.Parse(commandLine: line);

            if (
                (parseResult.Errors.Count != 0) ||
                !m_byTextCommand.TryGetValue(
                key: parseResult.CommandResult.Command,
                value: out var definition
            ) ||
                !TryGetId(
                name: definition.Name,
                id: out var actualCommandId
            ) ||
                (actualCommandId != expectedCommandId)
            ) {
                // A snapshot-routed line that no longer re-parses to the command it was injected as never reaches its
                // handler. Submit already returned None for it, so this is the only place it can be counted — without
                // it a Simulation-routed rejection stays invisible to wire.errors.
                m_rejections++;

                return;
            }

            var value = (definition.ValueSelector?.Invoke(arg: parseResult) ?? ImpulseValue(kind: definition.ValueKind));
            var context = new CommandContext(
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
            ReleaseSubmissionBarrier(completesTextSubmission: completesTextSubmission);
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

        if (m_observers.Length != 0) {
            var activation = new CommandActivation(
                Name: definition.Name,
                Phase: context.Phase,
                Result: result,
                Text: context.Text
            );

            for (var index = 0; (index < m_observers.Length); index++) {
                m_observers[index].OnCommand(activation: in activation);
            }
        }

        return result;
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

        var result = SubmitCore(line: line);

        // The one place every text-path outcome is visible: count each failure so `wire.errors` can report it. This
        // covers the registry's own refusals AND a module handler's IsError result on either dispatch path.
        if (result.IsError) {
            m_rejections++;
        }

        return result;
    }

    // Submit's body. Submit itself owns the rejection accounting so no return path here has to remember to count.
    private CommandResult SubmitCore(string line) {
        if (string.IsNullOrWhiteSpace(value: line)) {
            return CommandResult.None;
        }

        // FAST PATH for the plain `verb arg arg…` line shape — skips the System.CommandLine parse (measured ~5.2 µs +
        // ~8.6 KB per line at the World's ~34-verb surface), the measured cause of the stdin proof's worst-frame dips
        // when a burst of lines lands in one Collect. Eligible ONLY when the line carries neither `"` (System.CommandLine's
        // one quote char) nor `@` (its response-file sigil) AND the first whitespace-delimited token EXACTLY names a
        // command registered via WithWireArgs with Immediate routing. Anything else — an unknown/other-
        // shape first token, a quoted or response-file line, help, wire.ack — falls through to the parse below UNCHANGED,
        // so all error text and rich behavior stay byte-identical.
        //
        // ZERO-COPY: the line is tokenized into a stackalloc Span<Range> (Tokenize reproduces
        // Split((char[])null, RemoveEmptyEntries) whitespace semantics exactly), the verb is looked up by its SPAN via
        // the frozen alternate lookup (so the verb token never materializes), and the reused kind-specific context is handed to
        // the handler, which receives a zero-copy WireArgs over the trailing token ranges — no substrings, no argument
        // array, nothing heap-allocated by the dispatch itself. The context's Parse is null; Value is the command's
        // declared-kind impulse so the few wire-native verbs that also serve value-sensitive bindings observe the
        // same kind on this path and the full parser fallback.
        if (
            (line.IndexOf(value: '"') < 0) &&
            (line.IndexOf(value: '@') < 0)
        ) {
            Span<Range> tokenRanges = stackalloc Range[MaxFastPathTokens];
            var tokenCount = Tokenize(
                line: line,
                tokens: tokenRanges
            );

            if (
                (tokenCount > 0) &&
                m_fastPathAlt.TryGetValue(
                key: LeadingVerb(line: line),
                value: out var fast
            )
            ) {
                var argRanges = tokenRanges[1..tokenCount];
                // The handler reads WireArgs.Echo to skip building a success echo it would only be dropped; the final
                // suppression itself is SuppressAckIfQuiet's, so the rule stays defined in exactly one place.
                var quiet = (m_acksQuiet && fast.AcknowledgementOnly);
                var result = fast.WireArgsHandler!(
                    arg1: m_fastContexts[(int)fast.ValueKind],
                    arg2: new WireArgs(
                        line: line,
                        ranges: argRanges,
                        echo: !quiet
                    )
                );

                return SuppressAckIfQuiet(
                    definition: fast,
                    result: result
                );
            }
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
            var value = (definition.ValueSelector?.Invoke(arg: parseResult) ?? ImpulseValue(kind: definition.ValueKind));

            // A simulation command's effect mutates the deterministic sim, so it must be tick-aligned and recorded:
            // fold it into the snapshot stream rather than run it here. The handler still runs — later, when the
            // host applies that tick's snapshot — so a recording reproduces it. Console impulses inject as a Started
            // edge (the press the snapshot dispatch fires on) on the local slot.
            if (
                (definition.Routing == CommandRouting.Simulation) &&
                (m_injectionSink is { } sink) &&
                TryGetId(
                name: definition.Name,
                id: out var commandId
            )
            ) {
                m_pendingSimulationSubmissions++;

                try {
                    sink.Inject(
                        commandId: commandId,
                        value: value,
                        phase: CommandPhase.Started,
                        text: line,
                        completesTextSubmission: true
                    );
                } catch {
                    m_pendingSimulationSubmissions--;

                    throw;
                }

                return CommandResult.None;
            }

            // The text path returns its result to the caller, so it is not observed (the caller displays
            // it); observers exist for the snapshot-driven path, which has no return value to inspect.
            var result = definition.Handler(arg: new CommandContext(
                parse: parseResult,
                phase: CommandPhase.Completed,
                principal: CommandPrincipal.Console,
                registry: this,
                value: value
            ));

            // A quoted or many-token wire line takes this full parse; it obeys wire.ack through the same rule the fast
            // path does.
            return SuppressAckIfQuiet(
                definition: definition,
                result: result
            );
        }

        return CommandResult.Error(output: $"[wire.reject: unknown command '{line}']");
    }
}

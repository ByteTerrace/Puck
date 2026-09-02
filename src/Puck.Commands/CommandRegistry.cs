using System.Collections.Frozen;
using System.Collections.Immutable;
using System.CommandLine;
using System.CommandLine.Parsing;
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
/// <para>A registry is single-threaded by contract: the frame thread that drains <see cref="TextCommandSource"/> and
/// applies snapshots is its only caller. Construction is the only thread-safe operation; the acknowledgement mode, the
/// rejection count, and the re-entrancy depth are plain fields read and written on that one thread.</para>
/// </remarks>
public sealed class CommandRegistry {
    private readonly Dictionary<string, CommandDefinition> m_byName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Command, CommandDefinition> m_byTextCommand = [];
    private readonly Command m_helpCommand = new(
        description: "Lists the available commands.",
        name: HelpCommandName
    );
    private readonly RootCommand m_root = new(description: "Puck commands.");
    private readonly Dictionary<string, int> m_mapIndexByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    // Interned command identity: a stable ushort id per command, assigned by ordinal-sorting the canonical
    // names so the id↔name mapping is identical on every machine. This is the command's deterministic,
    // hashable, wire-compact identity in a CommandSnapshot — strings stay on the text/config side.
    private readonly Dictionary<string, ushort> m_idByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    // The built-in `wire.ack [on|quiet]` verb, registered beside `help`: it reports or flips m_acksQuiet. Handled inline
    // in Submit (like help), so it never enters a module or the wire-native path.
    private readonly Argument<string[]> m_wireAckArgument = new(name: "mode") {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "on | quiet",
    };
    // The built-in `wire.errors [reset]` verb, registered beside `help`/`wire.ack`: it reports (or clears) the count of
    // submitted lines this registry REFUSED. Every rejection — an unknown verb, a parse error, a handler's IsError
    // result on either dispatch path, a Simulation re-parse that failed to reach its handler, and a host's DEFERRED
    // refusal reported through NoteDeferredRejection — increments the same counter, so a driver reads one number back
    // instead of pattern-matching free-form error text. It counts the lines its CALLER submitted: a line a handler
    // submits of its own reaches this count only through the verdict that handler returns (see SubmitStamped).
    private readonly Argument<string[]> m_wireErrorsArgument = new(name: "mode") {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "reset",
    };

    // The attributed owner name for the registry's own built-in command names (help, wire.ack, wire.errors) in the
    // ClaimName ledger — so a colliding module's error message names the true owner rather than an empty module list.
    private const string BuiltInOwnerName = "CommandRegistry";
    // The registry's own verb names, declared once because they are used TWICE — to construct the built-in Command and
    // to claim the name against module collision. Two hand-transcribed copies would let a rename guard a name nothing
    // dispatches, silently reopening the wire-path hijack the claim exists to prevent.
    private const string HelpCommandName = "help";
    private const int MaxCommandCount = (ushort.MaxValue + 1);
    // How deeply a handler may re-enter Submit before the registry refuses. A handler that submits a line whose handler
    // submits again is legitimate (a macro verb), but an unbounded chain overflows the stack and takes the session with
    // it; refusing with an ordinary error result keeps the failure inside the wire's own reporting surfaces.
    private const int MaxSubmitDepth = 8;
    /// <summary>The cap on whitespace-delimited tokens the wire path handles from a <see langword="stackalloc"/> buffer;
    /// a line with more falls through to the full parse. The widest indexed argument list in the tree is eight tokens,
    /// but a free-text tail (<see cref="WireArgs.Tail"/>) — a chat line, an inline JSON row — is unbounded, so this sits
    /// far above both rather than at the arity of the widest verb.</summary>
    private const int MaxWireTokens = 64;
    private const string WireAckCommandName = "wire.ack";
    private const string WireErrorsCommandName = "wire.errors";

    // The registry's own text-path verbs, listed once so the case-insensitive verb resolution covers them exactly as
    // it covers a module's. They are not in m_byName: they are never bindable and have no interned id.
    private static readonly string[] BuiltInCommandNames = [HelpCommandName, WireAckCommandName, WireErrorsCommandName];
    // The Digital impulse most wire-native verbs carry, hoisted so those contexts do not recompute it.
    private static readonly CommandValue DigitalImpulse = CommandValue.Digital(active: true);
    // The one parser configuration BOTH full-parse sites use. System.CommandLine enables RESPONSE FILES by default, so
    // a default-configured parse of `chat.log 1 @everyone hello` reads `everyone` off the filesystem — a console line
    // performing I/O, a parse result that depends on the working directory rather than on the line, and (for a
    // simulation-routed line, parsed once at submit and again at apply) a replay that can diverge from its recording.
    // Null-ing the replacer makes an '@'-prefixed token an ordinary token, which is the only thing a Puck verb ever
    // means by one.
    private static readonly ParserConfiguration WireParserConfiguration = new() {
        ResponseFileTokenReplacer = null,
    };

    // The span-keyed alternate view over m_byName, so RoutesToSimulation classifies a line's verb token without
    // materializing it. Built once at construction, after registration completes.
    private readonly Dictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_byNameAlt;
    private readonly CommandDefinition[] m_definitionById;
    private readonly int[] m_mapIndexById;
    private readonly ImmutableArray<string> m_mapNames;
    // The public read-only face of the registered set, materialized once at construction: a listing verb and the
    // binding vocabulary read these facts, and neither is handed anything invocable. ImmutableArray rather than an
    // array behind IReadOnlyList — the affordance manifest is a fact about the registry, and a caller that can cast
    // the interface back to its backing array can rewrite it.
    private readonly ImmutableArray<CommandMetadata> m_metadata;
    private readonly CommandMetadata[] m_metadataById;
    private readonly string[] m_nameById;
    private readonly ICommandObserver[] m_observers;
    private readonly Command m_wireAckCommand;
    private readonly Command m_wireErrorsCommand;
    // The wire-native dispatch table (see Submit): every command built via CommandDefinition.WithWireArgs, keyed
    // case-INSENSITIVELY by its name and each alias, matching m_byName/m_idByName and the binding vocabulary. Command
    // identity is one thing on every surface: a binding row naming `Player.Move`, the interned id it resolves to, and
    // the line the router builds from that spelling must all reach the same handler. System.CommandLine matches
    // case-sensitively, so the full-parse fallback substitutes the canonical name (CanonicalizeVerb) rather than this
    // table narrowing to match the parser. Frozen once at construction (read-only, read-heavy).
    private readonly FrozenDictionary<string, CommandDefinition> m_wirePath;
    // The span-keyed alternate view over m_wirePath: the wire path looks a verb up by the line's leading-token SPAN, so
    // the verb token never materializes as a string. StringComparer.OrdinalIgnoreCase supplies the
    // IAlternateEqualityComparer that makes this legal; built once, reused every dispatch.
    private readonly FrozenDictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_wirePathAlt;

    // The wire acknowledgement mode: false (the default) echoes every accepted line exactly as before; true (`wire.ack
    // quiet`) drops the SUCCESS acks of wire-native verbs, so a flood of accepted commands costs no echo bytes. Errors
    // and answer-bearing verbs (anything not AcknowledgementOnly) are never suppressed. Toggled by the built-in `wire.ack` verb.
    private bool m_acksQuiet;
    // The deterministic-input sink a Simulation-class submitted command is folded into instead of running inline;
    // null until a host wires one (the live console-driving registry), so every other registry keeps the inline path.
    // The sink carries its OWN bound principal — this field never chooses one.
    private CommandInjectionSink? m_injectionSink;
    // The count wire.errors reports. Saturating rather than wrapping: a run that refused int.MaxValue lines has a
    // number that stops being useful, but a NEGATIVE count reads as a defect in the counter rather than in the run.
    private int m_rejections;
    // How many Submit calls are on the stack right now (a handler that submits a line re-enters). Guarded by
    // MaxSubmitDepth so an accidental cycle refuses instead of overflowing.
    private int m_submitDepth;

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
            : ((observers as ICommandObserver[]) ?? observers.ToArray())
        );

        // Attribution for the loud-failure name guard below: which owner first claimed a given command
        // name/alias. Ctor-scoped — the registry is immutable once built, so nothing after this loop can
        // introduce a new collision. The registry's own built-ins claim their names FIRST, so a module that
        // declares e.g. "wire.errors" collides and throws exactly like colliding with another module.
        var claimedBy = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        ClaimName(
            claimedBy: claimedBy,
            name: HelpCommandName,
            owner: BuiltInOwnerName
        );
        ClaimName(
            claimedBy: claimedBy,
            name: WireAckCommandName,
            owner: BuiltInOwnerName
        );
        ClaimName(
            claimedBy: claimedBy,
            name: WireErrorsCommandName,
            owner: BuiltInOwnerName
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

                // A command has ONE name, and these are the two halves of it: Name is what the interned id, the
                // binding vocabulary and the wire table resolve, while TextCommand.Name is what the parser matches.
                // Split them and the command registers, answers TryGetId for one spelling, and dispatches only for the
                // other — which the registry then reports as unknown. Closing CommandDefinition's identity setters
                // puts that out of a consumer's reach, so this is the guard for THIS assembly: the two factories are
                // the only builders today, and an internal `with` that set one half would otherwise be a silent
                // half-registered command rather than a composition-root refusal. Ordinally, because CanonicalizeVerb
                // rewrites a line's verb to Name before the parser, which matches VERBATIM.
                if (!string.Equals(
                    a: definition.Name,
                    b: definition.TextCommand.Name,
                    comparisonType: StringComparison.Ordinal
                )) {
                    throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) carries a text command named '{definition.TextCommand.Name}'. A command's dispatch identity and its text identity are one name; build definitions through CommandDefinition.Verb or CommandDefinition.WithWireArgs and do not rename the text command afterwards.");
                }

                // A definition owns a System.CommandLine object graph, and registering it MUTATES that graph (a root
                // parent, and one alias per declared alias). Handing the same cached instance to two registries would
                // therefore let the second registry's construction rewrite the first one's live parser state — a
                // cross-registry coupling nothing at the call site can see. Refuse it by name; a module that must serve
                // two registries yields a fresh definition per call.
                if (definition.TextCommand.Parents.Any()) {
                    throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) is already registered in another command registry. A module must yield a fresh CommandDefinition per registry; a definition's text command carries per-registry parser state.");
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
                    // Refused here rather than three frames down: an unchecked null reached the claim ledger's
                    // Dictionary and threw naming the parameter 'key', which tells a composition root nothing about
                    // which module declared which command's alias list badly.
                    if (string.IsNullOrWhiteSpace(value: alias)) {
                        throw new InvalidOperationException(message: $"Command '{definition.Name}' (registered by {moduleName}) declares a null or blank entry in its 'aliases'. Every alias is a name a line can be spelled with.");
                    }

                    ClaimName(
                        claimedBy: claimedBy,
                        name: alias,
                        owner: moduleName
                    );
                    m_byName[alias] = definition;
                    definition.TextCommand.Aliases.Add(item: alias);
                }
            }
        }

        m_root.Subcommands.Add(item: m_helpCommand);

        // The wire's own control verb, beside help: `wire.ack [on|quiet]` reports or flips the acknowledgement mode.
        m_wireAckCommand = new Command(
            description: "Sets or reports the acknowledgement mode for accepted commands: wire.ack [on|quiet] — `on` (default) echoes every accepted command; `quiet` drops the bare success acknowledgements of side-effecting verbs, while failures and any verb that answers with data still echo; no argument reports the current mode.",
            name: WireAckCommandName
        ) {
            m_wireAckArgument,
        };
        m_root.Subcommands.Add(item: m_wireAckCommand);

        // The wire's rejection readback, beside wire.ack: `wire.errors [reset]`.
        m_wireErrorsCommand = new Command(
            description: "Reports the number of submitted lines this session REFUSED — an unknown verb, a parse error, a handler's failure result, or a refusal raised after the line was accepted: wire.errors [reset] — no argument reports the running count; `reset` reports it and zeroes the counter. It is the one number that says whether any submitted line silently no-opped.",
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
            m_idByName[m_nameById[id]] = ((ushort)id);
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
        // Case-INSENSITIVE, exactly like m_byName and m_idByName, so one identity rule governs every surface a
        // command name reaches. m_byName's keys carry each name and alias verbatim.
        var wirePath = new Dictionary<string, CommandDefinition>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var (name, definition) in m_byName) {
            if (definition.WireArgsHandler is not null) {
                wirePath[name] = definition;
            }
        }

        m_wirePath = wirePath.ToFrozenDictionary(comparer: StringComparer.OrdinalIgnoreCase);
        m_wirePathAlt = m_wirePath.GetAlternateLookup<ReadOnlySpan<char>>();
        m_byNameAlt = m_byName.GetAlternateLookup<ReadOnlySpan<char>>();
        m_metadata = [.. m_byTextCommand.Values
            .Select(selector: static definition => definition.Metadata)
            .OrderBy(
            keySelector: static metadata => metadata.Name,
            comparer: StringComparer.Ordinal
        )];

    }

    internal CommandModality CreateModality(ReadOnlySpan<string> activeMaps) {
        return ((activeMaps.Length == 0)
            ? DefaultModality
            : CompileModality(activeMaps: activeMaps)
        );
    }
    /// <summary>Determines whether an interned command belongs to the host's focus-exempt control plane.</summary>
    internal bool IsFocusExemptCommand(ushort commandId) =>
        ((commandId < m_metadataById.Length) &&
        (m_metadataById[commandId].InputScope == CommandInputScope.FocusExempt));
    /// <summary>Determines whether an interned command is a HELD verb (see <see cref="CommandMetadata.Held"/>).</summary>
    internal bool IsHeldCommand(ushort commandId) =>
        ((commandId < m_metadataById.Length) &&
        m_metadataById[commandId].Held);
    internal bool IsMapActive(CommandModality modality, string map) {
        return (
            m_mapIndexByName.TryGetValue(
            key: map,
            value: out var mapIndex
        ) &&
            modality.ActiveMaps[mapIndex]
        );
    }
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
    internal CommandResult SubmitSession(string line, TextCommandSession session) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(session);

        return SubmitStamped(
            line: line,
            session: session
        );
    }

    // Applies one snapshot entry. Extracted from ApplySnapshot's loop so the loop is nothing but the per-entry
    // exception boundary and the barrier release — a body inline there would invite a future `continue` to skip past
    // one or the other, which is the exact defect the boundary exists to close.
    private void ApplyEntry(in CommandEntry entry, int slot) {
        // A submitted text entry is routed FIRST — before the defensive id-range check below, which would otherwise
        // fall through to the bound-entry path with an id that indexes nothing.
        //
        // The range check on THIS branch is unreachable from any public path and is deliberately kept: an injected
        // entry's id is interned from this very registry (CommandInjectionSink resolves it through TryGetId), and a
        // snapshot built for another registry is refused whole before the loop starts, so the two ways an id could
        // exceed m_nameById are both already closed. Nothing here decides whether the entry's barrier is released
        // either — ApplySnapshot's per-entry finally owns that — so skipping the dispatch is the whole of what this
        // guard does. It is therefore untested BY CONSTRUCTION: a test would have to forge an entry, which is exactly
        // what CommandEntry's internal construction prevents.
        if (entry.Text is { } line) {
            if (
                entry.Dispatch &&
                (((int)entry.CommandId) < m_nameById.Length)
            ) {
                ApplySubmittedSimulation(
                    line: line,
                    expectedCommandId: entry.CommandId,
                    phase: entry.Phase,
                    value: entry.Value,
                    principal: entry.Principal,
                    slot: slot
                );
            }

            return;
        }

        if (
            !entry.Dispatch ||
            (((int)entry.CommandId) >= m_nameById.Length)
        ) {
            return;
        }

        var definition = m_definitionById[entry.CommandId];
        var context = new CommandContext(
            assignedSlot: entry.AssignedSlot,
            deviceId: entry.Device,
            origin: entry.Origin,
            parse: null,
            phase: entry.Phase,
            principal: entry.Principal,
            registry: this,
            slot: slot,
            source: entry.Source,
            text: null,
            value: entry.Value
        );

        // One entry's handler must not decide whether the REST of the tick runs: Dispatch converts an escaped
        // exception into an error result observers see, and a fault is counted here so it reaches wire.errors
        // (an ordinary IsError verdict from a bound press is not a refused submission and is not counted).
        _ = Dispatch(
            context: in context,
            definition: definition,
            faulted: out var faulted
        );

        if (faulted) {
            NoteRejection();
        }
    }
    // Executes a simulation-routed text command from its tick snapshot. Submit identified the line's verb before
    // injection but did NOT parse its arguments; this is the line's one and only parse, and it recreates the handler's
    // ordinary text context without re-routing it. The entry's phase, value and principal ride the entry rather than
    // being re-derived: the door that queued the line already decided them.
    //
    // The entry's read-after-write barrier is NOT this method's to release: ApplySnapshot's per-entry boundary owns
    // that, so a throw anywhere in here — the parse, CanonicalizeVerb, a handler's exception rendering — releases it
    // exactly once and cannot strand the session.
    private void ApplySubmittedSimulation(string line, ushort expectedCommandId, CommandPhase phase, CommandValue value, CommandPrincipal principal, int slot) {
        Span<Range> tokenRanges = stackalloc Range[MaxWireTokens];

        if (
            TryResolveWireLine(
            definition: out var wireDefinition,
            line: line,
            tokenCount: out var tokenCount,
            tokenRanges: tokenRanges
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
                phase: phase,
                value: value,
                principal: principal,
                slot: slot,
                observe: true,
                faulted: out _
            );

            if (wireResult.IsError) {
                NoteRejection();
            }

            return;
        }

        var parseResult = m_root.Parse(
            commandLine: CanonicalizeVerb(line: line),
            configuration: WireParserConfiguration
        );

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
            // A snapshot-routed line that does not parse to the command it was injected as never reaches its
            // handler. Submit returned None for it, so this is the only place it can be counted OR reported —
            // without this a Simulation-routed rejection would leave the operator nothing but a wire.errors bump
            // to notice, on a line whose refusal arrives a tick after the prompt accepted it.
            NoteRejection();
            NotifyRefusal(
                errors: parseResult.Errors,
                expectedCommandId: expectedCommandId,
                line: line,
                phase: phase,
                principal: principal,
                slot: slot
            );

            return;
        }

        var context = new CommandContext(
            origin: CommandOrigin.Text,
            parse: parseResult,
            phase: phase,
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
            faulted: out _,
            suppressWireAck: true
        ).IsError) {
            NoteRejection();
        }
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

        // Case-insensitively, like WireArgs.Is — every module verb reads its mode words that way, and a built-in that
        // refused `QUIET` while `player.move UP` worked would be the odd one out.
        if (string.Equals(
            a: mode[0],
            b: "on",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            m_acksQuiet = false;

            return new CommandResult(Output: "[wire.ack: on]");
        }

        if (string.Equals(
            a: mode[0],
            b: "quiet",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            m_acksQuiet = true;

            return new CommandResult(Output: "[wire.ack: quiet]");
        }

        return CommandResult.Error(output: $"[wire.ack: unknown mode '{mode[0]}' — expected on | quiet]");
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
            comparisonType: StringComparison.OrdinalIgnoreCase
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
    /// <summary>Builds the help listing of every registered command and its description, ordinal-ordered by name — the
    /// same order <see cref="Definitions"/> and the interned id assignment use, so a listing verb and the help text
    /// never disagree about where a command sits.</summary>
    /// <returns>A newline-separated list of <c>name - description</c> entries.</returns>
    private string BuildHelpText() {
        return string.Join(
            separator: '\n',
            values: m_root.Subcommands
                .OrderBy(
                comparer: StringComparer.Ordinal,
                keySelector: command => command.Name
            )
                .Select(selector: command => $"{command.Name} - {command.Description}")
        );
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
                throw new ArgumentException(
                    message: $"Command map '{(map ?? "(null)")}' is not registered.",
                    paramName: nameof(activeMaps)
                );
            }

            mapActivity[mapIndex] = true;
        }

        var commandActivity = new bool[m_mapIndexById.Length];

        for (var id = 0; (id < commandActivity.Length); id++) {
            commandActivity[id] = mapActivity[m_mapIndexById[id]];
        }

        return new CommandModality(
            activeCommands: commandActivity,
            activeMaps: mapActivity
        );
    }
    /// <summary>Runs a command's handler behind the registry's exception boundary and notifies every observer of the
    /// dispatch.</summary>
    /// <param name="context">The invocation state passed to the handler.</param>
    /// <param name="definition">The command being dispatched.</param>
    /// <param name="faulted">When this method returns, whether the handler THREW rather than returning a verdict.</param>
    /// <param name="suppressWireAck">Whether quiet wire mode may suppress a successful acknowledgement.</param>
    /// <returns>The result the handler returned, or an error result describing the exception it threw.</returns>
    private CommandResult Dispatch(in CommandContext context, CommandDefinition definition, out bool faulted, bool suppressWireAck = false) {
        CommandResult result;

        try {
            faulted = false;
            result = definition.Handler(arg: context);
        } catch (Exception exception) when (IsContainable(exception: exception)) {
            faulted = true;
            result = HandlerFault(
                definition: definition,
                exception: exception
            );
        }

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
    // Runs a wire-native handler over the line's trailing token ranges. Phase and value are supplied by the caller
    // rather than assumed: a submitted line dispatches Completed with the command's declared impulse, while a line
    // replayed from its snapshot entry carries the phase and value that entry recorded (a HELD wire verb branching on
    // phase would otherwise always see the release branch).
    private CommandResult DispatchWire(
        CommandDefinition definition,
        string line,
        ReadOnlySpan<Range> argumentRanges,
        CommandPhase phase,
        CommandValue value,
        CommandPrincipal principal,
        int slot,
        bool observe,
        out bool faulted
    ) {
        var quiet = (m_acksQuiet && definition.AcknowledgementOnly);
        var context = new CommandContext(
            origin: CommandOrigin.Text,
            parse: null,
            phase: phase,
            principal: principal,
            registry: this,
            slot: slot,
            text: line,
            value: value
        );
        CommandResult result;

        try {
            faulted = false;
            result = definition.WireArgsHandler!(
                arg1: context,
                arg2: new WireArgs(
                    echo: !quiet,
                    line: line,
                    ranges: argumentRanges
                )
            );
        } catch (Exception exception) when (IsContainable(exception: exception)) {
            faulted = true;
            result = HandlerFault(
                definition: definition,
                exception: exception
            );
        }

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
    /// <summary>Renders a handler's escaped exception as the error result the wire reports it through.</summary>
    /// <param name="definition">The command whose handler threw.</param>
    /// <param name="exception">The escaped exception.</param>
    /// <returns>An <see cref="CommandResult.IsError"/> result naming the command, the exception type, and its message.</returns>
    private static CommandResult HandlerFault(CommandDefinition definition, Exception exception) =>
        CommandResult.Error(output: $"[{definition.Name}: handler threw {exception.GetType().Name}: {exception.Message}]");
    // The one rule every dispatch boundary in this type filters on, written once so the three handler boundaries, the
    // observer boundary and the per-entry boundary cannot drift apart.
    //
    // An OperationCanceledException is a HOST SIGNAL, not a verdict about a command: a handler raises it by observing
    // the host's own shutdown/cancellation token, so it belongs to the pump that owns that token and must unwind to it
    // — reporting it as `[verb: handler threw OperationCanceledException]` and a wire.errors bump would turn a
    // requested shutdown into a line the host has to pattern-match its way back out of, and would let the tick carry on
    // dispatching entries after the host asked it to stop. Everything else is CONTAINED: a module verb's own bug is a
    // verdict about that verb alone, and must not decide whether the rest of the tick's entries run, whether the other
    // observers hear about this dispatch, or whether a later submitted line's read-after-write barrier is released.
    private static bool IsContainable(Exception exception) => (exception is not OperationCanceledException);
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
    // Rewrites a line's leading verb to the canonical spelling of the command it names, when the two differ. Command
    // identity is case-INSENSITIVE everywhere in Puck — m_byName, the interned ids, the binding vocabulary, the wire
    // table — but System.CommandLine matches command names and aliases case-SENSITIVELY, so without this a line whose
    // verb passed every vocabulary check ('Player.Move' authored in a binding row) would reach the parser and miss.
    // Allocates only on the cold path, and only when the spelling actually differs.
    private string CanonicalizeVerb(string line) {
        var trimmed = line.AsSpan().TrimStart();
        var verb = LeadingVerb(line: trimmed);

        if (verb.IsEmpty) {
            return line;
        }

        var canonical = CanonicalNameFor(verb: verb);

        if (canonical is null) {
            return line;
        }

        return string.Concat(
            str0: canonical,
            str1: trimmed[verb.Length..]
        );
    }
    // The canonical spelling a verb token must be rewritten to before the parser sees it, or null when it needs no
    // rewrite (it already matches verbatim, or names nothing this registry knows and will be refused anyway). The
    // registry's own built-ins are covered too: `WIRE.ERRORS` is the same verb as `wire.errors` everywhere else.
    private string? CanonicalNameFor(ReadOnlySpan<char> verb) {
        if (m_byNameAlt.TryGetValue(
            key: verb,
            value: out var definition
        )) {
            return (NamesCommandExactly(
                definition: definition,
                verb: verb
            )
                ? null
                : definition.Name);
        }

        foreach (var name in BuiltInCommandNames) {
            if (verb.Equals(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                other: name
            )) {
                return (verb.Equals(
                    comparisonType: StringComparison.Ordinal,
                    other: name
                )
                    ? null
                    : name);
            }
        }

        return null;
    }
    // Whether the verb token is one of the command's spellings VERBATIM — its name or one of its aliases. A verb that
    // already matches ordinally needs no substitution, and an alias must not be rewritten to the canonical name when
    // the parser would have accepted it as written.
    private static bool NamesCommandExactly(CommandDefinition definition, ReadOnlySpan<char> verb) {
        if (verb.Equals(
            comparisonType: StringComparison.Ordinal,
            other: definition.Name
        )) {
            return true;
        }

        for (var index = 0; (index < definition.Aliases.Count); index++) {
            if (verb.Equals(
                comparisonType: StringComparison.Ordinal,
                other: definition.Aliases[index]
            )) {
                return true;
            }
        }

        return false;
    }
    // Counts one refusal, saturating at int.MaxValue. A count that wrapped negative would read as a defect in the
    // counter rather than in the run wire.errors exists to describe.
    private void NoteRejection() {
        if (m_rejections != int.MaxValue) {
            m_rejections++;
        }
    }
    // Reports one DISPATCHED command's verdict to every observer (see PublishActivation for the per-observer boundary).
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

        PublishActivation(activation: in activation);
    }
    // Reports a DEFERRED refusal — a submitted Simulation line that reached its tick and then failed to decode as the
    // command it was injected as — through the same observer surface a dispatched line's verdict travels.
    //
    // Deferring a line defers its parse, so Submit answered CommandResult.None a tick earlier and has no verdict left
    // to return; every sink that shows an operator what a submitted line did (a launcher's stdout split, a host's
    // console tape) keys on the activation's Text, so without this the refusal is invisible on every surface but the
    // wire.errors counter nobody polls. The alternative — a synchronous shape check at submit — would re-parse the
    // line at both ends, which is exactly the double parse the deferred route exists to avoid.
    private void NotifyRefusal(string line, ushort expectedCommandId, IReadOnlyList<ParseError> errors, CommandPhase phase, CommandPrincipal principal, int slot) {
        if (m_observers.Length == 0) {
            return;
        }

        // Named for the command the line was INJECTED as, which is what the operator asked for; the line's own text
        // rides Text, so a sink can show both when they disagree.
        var name = ((((int)expectedCommandId) < m_nameById.Length)
            ? m_nameById[expectedCommandId]
            : LeadingVerb(line: line).ToString()
        );
        var reason = ((errors.Count != 0)
            ? string.Join(
                separator: " | ",
                values: errors.Select(selector: static error => error.Message)
            )
            : $"'{line}' no longer names '{name}'"
        );
        var activation = new CommandActivation(
            Name: name,
            Phase: phase,
            Result: CommandResult.Error(output: $"[wire.reject: {reason}]"),
            Text: line,
            Principal: principal,
            Slot: slot
        );

        PublishActivation(activation: in activation);
    }
    // Hands one activation to every observer, each behind its own boundary. An observer is an I/O SINK (a launcher
    // writing the verdict to stdout, a host publishing a console frame), so a throw from one is a fault in a reporting
    // surface, never in the tick: one broken sink must not silence the sinks after it, abandon the rest of the tick's
    // entries, or strand a later submitted line's read-after-write barrier. A swallowed notification is counted as a
    // refusal, because the verdict it carried never reached the operator.
    private void PublishActivation(in CommandActivation activation) {
        for (var index = 0; (index < m_observers.Length); index++) {
            try {
                m_observers[index].OnCommand(activation: in activation);
            } catch (Exception exception) when (IsContainable(exception: exception)) {
                NoteRejection();
            }
        }
    }
    private static void QueueSimulation(
        ushort commandId,
        string line,
        TextCommandSession? session,
        CommandInjectionSink sink,
        CommandValue value
    ) {
        session?.Barrier.Begin();

        try {
            sink.Inject(
                commandId: commandId,
                value: value,
                phase: CommandPhase.Started,
                text: line,
                submissionBarrier: session?.Barrier
            );
        } catch {
            session?.Barrier.Complete();

            throw;
        }
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
        // when the line carries no `"` and names a WithWireArgs command. Immediate commands run now; Simulation
        // commands inject the original line and use the same path when their tick applies.
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
            definition: out var wireDefinition,
            line: line,
            tokenCount: out var tokenCount,
            tokenRanges: tokenRanges
        )) {
            if (TryQueueSimulation(
                definition: wireDefinition!,
                line: line,
                session: session
            )) {
                return CommandResult.None;
            }

            return DispatchWire(
                definition: wireDefinition!,
                line: line,
                argumentRanges: tokenRanges[1..tokenCount],
                phase: CommandPhase.Completed,
                value: ImpulseValue(kind: wireDefinition!.ValueKind),
                principal: principal,
                slot: slot,
                observe: false,
                faulted: out _
            );
        }

        // A Simulation-class line is routed by its leading VERB alone, before any parse. Submit's only decision for
        // such a line is which command it names and whether a sink is wired; its arguments are read by the handler at
        // apply time, from a parse ApplySubmittedSimulation must do anyway. Parsing here as well would parse the same
        // line twice — the cost the wire path exists to avoid, paid on the one path that cannot skip a parse — so a
        // malformed argument on a deferred line is refused a tick later (through wire.errors) rather than synchronously.
        var verb = LeadingVerb(line: line);

        if (
            !verb.IsEmpty &&
            m_byNameAlt.TryGetValue(
            key: verb,
            value: out var routed
        ) &&
            TryQueueSimulation(
            definition: routed,
            line: line,
            session: session
        )
        ) {
            return CommandResult.None;
        }

        var parseResult = m_root.Parse(
            commandLine: CanonicalizeVerb(line: line),
            configuration: WireParserConfiguration
        );

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
            var context = new CommandContext(
                origin: CommandOrigin.Text,
                parse: parseResult,
                phase: CommandPhase.Completed,
                principal: principal,
                registry: this,
                slot: slot,
                text: line,
                value: ImpulseValue(kind: definition.ValueKind)
            );

            // The text path returns its result to the caller, so it is not observed (the caller displays
            // it); observers exist for the snapshot-driven path, which has no return value to inspect. The handler
            // runs behind the same exception boundary every other dispatch path uses: a throwing handler becomes an
            // error result the caller sees and wire.errors counts.
            CommandResult result;

            try {
                result = definition.Handler(arg: context);
            } catch (Exception exception) when (IsContainable(exception: exception)) {
                result = HandlerFault(
                    definition: definition,
                    exception: exception
                );
            }

            // A quoted or many-token wire line takes this full parse; it obeys wire.ack through the same rule the fast
            // wire-native path does.
            return SuppressAckIfQuiet(
                definition: definition,
                result: result
            );
        }

        return CommandResult.Error(output: $"[wire.reject: unknown command '{line}']");
    }
    private CommandResult SubmitStamped(string line, TextCommandSession? session) {
        // A handler may submit a line of its own (a macro verb); an accidental cycle would otherwise recurse until the
        // stack gave out and took the session with it. Refuse past the bound with an ordinary error result, counted by
        // the outermost frame like every other refusal as it unwinds.
        if (m_submitDepth >= MaxSubmitDepth) {
            return CommandResult.Error(output: $"[wire.reject: command submission nested more than {MaxSubmitDepth} deep — '{line}' refused]");
        }

        m_submitDepth++;

        CommandResult result;

        try {
            result = SubmitCore(
                line: line,
                session: session
            );
        } finally {
            m_submitDepth--;
        }

        // The one place every text-path outcome is visible: count each failure so `wire.errors` can report it. This
        // covers the registry's own refusals AND a module handler's IsError result (or escaped exception) on either
        // dispatch path.
        //
        // ONLY THE OUTERMOST FRAME COUNTS. wire.errors answers "how many of the lines I submitted were refused", so
        // it must be a function of the driver's own lines and not of how deeply a handler re-entered: a refused
        // re-entrant chain returns ONE error result that every unwinding frame observes, and counting per frame
        // reported nine refusals for one console line. A macro verb's internal submissions are the handler's business
        // — what it makes of a nested failure is its verdict to return, and that verdict is what is counted.
        if (
            result.IsError &&
            (m_submitDepth == 0)
        ) {
            NoteRejection();
        }

        return result;
    }
    // Folds a Simulation-class line into the deterministic per-tick snapshot instead of running it inline, when the
    // command defers and a sink is wired. The handler still runs — later, when the host applies that tick — so a
    // recording reproduces it. Console impulses inject as a Started edge (the press the snapshot dispatch fires on) on
    // the session's slot.
    private bool TryQueueSimulation(CommandDefinition definition, string line, TextCommandSession? session) {
        if (
            (definition.Routing != CommandRouting.Simulation) ||
            ((session?.SimulationSink ?? m_injectionSink) is not { } sink) ||
            !TryGetId(
            name: definition.Name,
            id: out var commandId
        )
        ) {
            return false;
        }

        QueueSimulation(
            commandId: commandId,
            line: line,
            session: session,
            sink: sink,
            value: ImpulseValue(kind: definition.ValueKind)
        );

        return true;
    }
    // The one definition of the wire.ack-quiet suppression rule, applied on every text dispatch path (fast, full
    // parse, snapshot re-dispatch): in quiet mode a successful acknowledgement-only result carries no answer, so drop
    // it to None. An error (IsError) and an answer-bearing verb's output are never suppressed.
    private CommandResult SuppressAckIfQuiet(CommandResult result, CommandDefinition definition) {
        return ((m_acksQuiet && definition.AcknowledgementOnly && !result.IsError)
            ? CommandResult.None
            : result
        );
    }
    /// <summary>Splits a line into whitespace-delimited token ranges without allocating. A token is a maximal run of
    /// non-whitespace characters (<see cref="char.IsWhiteSpace(char)"/>, matching <see cref="string.Split(char[], StringSplitOptions)"/>'s
    /// null-separator semantics), which agrees with the System.CommandLine splitter on every unquoted line: both split
    /// on space, tab, vertical tab and form feed alike.
    /// Fills <paramref name="tokens"/> with one <see cref="Range"/> per token.</summary>
    /// <remarks>
    /// TWO grammars still decide a line, and they agree on everything except these, verified against System.CommandLine
    /// 2.0.11:
    /// <list type="bullet">
    /// <item><description><b>Quotes.</b> A line containing <c>"</c> never reaches this tokenizer — the wire path
    /// refuses it up front so the parser's quote handling is the only one in play. Not a divergence, an exclusion.</description></item>
    /// <item><description><b>A bare <c>--</c>.</b> The parser CONSUMES it as the end-of-options marker; this tokenizer
    /// passes it through as an ordinary token. A verb whose argument is literally <c>--</c> therefore sees it on the
    /// wire path and not on the fallback. No verb in the tree takes one.</description></item>
    /// </list>
    /// <c>-x</c>/<c>--flag</c> tokens are NOT a divergence: the wire commands declare a single trailing
    /// <c>ZeroOrMore</c> argument and no options, so the parser hands them through verbatim too. Neither is
    /// <c>--help</c>/<c>--version</c>: this version's <see cref="RootCommand"/> contributes neither option, so both are
    /// refused as unknown commands on both paths.
    /// </remarks>
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
                end: index,
                start: start
            );
        }

        return count;
    }
    // Resolves a plain `verb arg arg…` line to its wire-native definition and token ranges. Only a QUOTE sends a line
    // to the full parse: an '@'-prefixed token used to as well, because System.CommandLine would have expanded it from
    // a file on disk, but response files are off (WireParserConfiguration) so '@everyone' is an ordinary token on both
    // paths and belongs on the fast one.
    private bool TryResolveWireLine(string line, Span<Range> tokenRanges, out CommandDefinition? definition, out int tokenCount) {
        definition = null;
        tokenCount = 0;

        if (line.IndexOf(value: '"') >= 0) {
            return false;
        }

        tokenCount = Tokenize(
            line: line,
            tokens: tokenRanges
        );

        return (
            (tokenCount > 0) &&
            m_wirePathAlt.TryGetValue(
            key: line.AsSpan()[tokenRanges[0]],
            value: out definition
        )
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
    /// <para>ONCE THE SNAPSHOT IS ACCEPTED, this method propagates nothing but a cancellation: every entry runs inside
    /// its own boundary, so no exception raised while applying one entry — a handler's, an observer's, or one the
    /// registry's own decoding of a submitted line raised — decides whether the rest of the tick's entries run, and each
    /// entry's submitted-line read-after-write barrier is released whether its body completed or threw. A contained
    /// fault is counted in the <c>wire.errors</c> total, and a handler fault additionally becomes an error result
    /// observers see. The single exception is an <see cref="OperationCanceledException"/>, which is a HOST SIGNAL rather
    /// than a verdict about a command: it unwinds to the pump that owns the cancelled token, releasing its own entry's
    /// barrier on the way out and leaving the tick's remaining entries unapplied, which is what a requested shutdown
    /// asks for.</para>
    /// <para>The registry-mismatch refusal below is the one throw this method still makes, and it happens BEFORE any
    /// entry is examined: a snapshot built for another registry carries entries this one cannot decode at all, so it is
    /// refused whole rather than half-applied. Nothing has been dispatched and no barrier has been touched when it
    /// throws — the caller that mixed two registries owns the stranded queue, and the fix is at that call site.</para>
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
                // The boundary is the ENTRY, not the handler: the handler's own catch cannot cover the code that
                // decodes a submitted line (the parse, CanonicalizeVerb) or renders a fault, and an escape from either
                // would abandon the rest of the lane and strand every later entry's barrier. Releasing the barrier in
                // the finally is what makes that guarantee structural rather than a rule each branch remembers.
                try {
                    ApplyEntry(
                        entry: in entry,
                        slot: lane.Slot
                    );
                } catch (Exception exception) when (IsContainable(exception: exception)) {
                    NoteRejection();
                } finally {
                    entry.SubmissionBarrier?.Complete();
                }
            }
        }
    }
    /// <summary>Gets the canonical name for an interned command id.</summary>
    /// <param name="id">The interned id, in <c>[0, <see cref="CommandCount"/>)</c>.</param>
    /// <returns>The command's canonical name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a valid interned id.</exception>
    public string GetName(ushort id) {
        if (((int)id) >= m_nameById.Length) {
            throw new ArgumentOutOfRangeException(paramName: nameof(id));
        }

        return m_nameById[id];
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
        NoteRejection();
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
    /// input path (its real result is produced when its tick is applied); or a message describing parse errors, an
    /// unknown command, or an exception the handler let escape.
    /// </returns>
    /// <remarks>
    /// This path is never gated by command maps; it is the deliberate console entry point. A
    /// <see cref="CommandRouting.Simulation"/> command is injected into the per-tick <see cref="CommandSnapshot"/>
    /// (so it is tick-aligned and applied deterministically) when a sink is wired via <see cref="RouteSimulationTo"/>;
    /// otherwise — and for every <see cref="CommandRouting.Immediate"/> command — the handler runs inline.
    /// <para>The line is data: it names a command and supplies its tokens, and nothing about it reaches the
    /// filesystem. System.CommandLine's response-file expansion is switched OFF for both of this type's parse sites,
    /// so an <c>@</c>-prefixed token is an ordinary token rather than a file to splice in.</para>
    /// <para>Deferring a Simulation command DEFERS ITS ARGUMENTS TOO: only the leading verb is resolved here, and the
    /// line's own parse happens once, when its tick applies. A malformed argument on such a line is therefore refused
    /// a tick later — through the <c>wire.errors</c> count — rather than in this call's return value.</para>
    /// <para>An exception a handler lets escape never leaves this method: it becomes an
    /// <see cref="CommandResult.IsError"/> result naming the exception, counted like any other refusal. The one
    /// exception is an <see cref="OperationCanceledException"/> — a handler raises that by observing the HOST's
    /// cancellation token, so it is a signal to the caller rather than a verdict about the command, and it propagates
    /// unchanged and uncounted. The same rule governs <see cref="ApplySnapshot"/>, where containment additionally
    /// guarantees that one throwing handler cannot skip the rest of the tick's entries. Read-after-write ordering across
    /// submitted lines is not this method's to give — it is a <see cref="TextCommandSession"/> guarantee, honored by
    /// <see cref="TextCommandSource.Collect"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">A handler observed the host's cancellation token.</exception>
    public CommandResult Submit(string line) {
        ArgumentNullException.ThrowIfNull(line);

        return SubmitStamped(
            line: line,
            session: null
        );
    }
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

        if (m_idByName.TryGetValue(
            key: name,
            value: out var id
        )) {
            metadata = m_metadataById[id];

            return true;
        }

        metadata = default;

        return false;
    }

    /// <summary>Whether accepted-command acks are echoed. <see langword="false"/> once <c>wire.ack quiet</c> is set — a
    /// wire-native handler reads this (via <see cref="WireArgs.Echo"/>) to skip building a success echo it would drop.</summary>
    internal bool AcksEnabled => !m_acksQuiet;
    internal CommandModality DefaultModality { get; }

    /// <summary>The number of distinct commands; each has an interned id in <c>[0, <see cref="CommandCount"/>)</c>.</summary>
    public int CommandCount => m_nameById.Length;
    /// <summary>Gets the distinct registered commands' declared facts, ordinal-sorted by name — the affordance manifest
    /// source a listing verb (e.g. <c>world.affordances</c>) emits as data. Excludes the registry's own text-path
    /// built-ins (<c>help</c>/<c>wire.ack</c>/<c>wire.errors</c>), which are never bindable.</summary>
    /// <remarks>Metadata only, never a handler. A caller that could reach a definition's handler could invoke an authority
    /// verb with a context of its own making, which would be a dispatch door beside the stamped ones; describing the
    /// vocabulary must not confer the ability to drive it. <see cref="ImmutableArray{T}"/> rather than
    /// <see cref="IReadOnlyList{T}"/>: the manifest is a fact, and an interface over an array is a cast away from
    /// being rewritten under the registry's feet.</remarks>
    public ImmutableArray<CommandMetadata> Definitions => m_metadata;
    /// <summary>Gets the registered command-map names. <see cref="CommandMaps.Global"/> is always first.</summary>
    public ImmutableArray<string> Maps => m_mapNames;
}

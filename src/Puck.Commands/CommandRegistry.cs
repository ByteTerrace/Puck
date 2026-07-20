using System.Collections.Frozen;
using System.CommandLine;
using System.Numerics;

namespace Puck.Commands;

/// <summary>
/// Aggregates command definitions from a set of modules and provides the single surface through which
/// commands are driven, queried, and gated.
/// </summary>
/// <remarks>
/// The registry exposes four cooperating facets over the same set of definitions:
/// <list type="bullet">
/// <item><description><b>Sources.</b> Producers registered with <see cref="AddSource"/> are pulled each frame by <see cref="Collect"/>, pushing per-frame <see cref="CommandSignal"/>s that are gated by the active command maps.</description></item>
/// <item><description><b>Text.</b> <see cref="Submit"/> parses a line and runs the matching handler. This path performs no I/O and is never gated by command maps.</description></item>
/// <item><description><b>Polling.</b> <see cref="GetValue"/> returns the current frame's value for a command, for continuous consumers.</description></item>
/// <item><description><b>Maps.</b> <see cref="ActivateMap"/> and <see cref="DeactivateMap"/> control modality; only commands in an active map accept pushed signals.</description></item>
/// </list>
/// A typical frame calls <see cref="BeginFrame"/> to clear the previous frame's values, then
/// <see cref="Collect"/> to pull every registered source.
/// </remarks>
public sealed class CommandRegistry : ICommandSink {
    private readonly HashSet<string> m_activeMaps = new(comparer: StringComparer.OrdinalIgnoreCase) { CommandMaps.Global };
    private readonly Dictionary<string, CommandDefinition> m_byName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Command, CommandDefinition> m_byTextCommand = [];
    private readonly Command m_helpCommand = new(
        name: "help",
        description: "Lists the available commands."
    );
    private readonly ICommandObserver[] m_observers;
    private readonly RootCommand m_root = new(description: "Puck commands.");
    private readonly List<ICommandSource> m_sources = [];
    private readonly Dictionary<string, CommandValue> m_state = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommandValue> m_held = new(comparer: StringComparer.OrdinalIgnoreCase);
    // Interned command identity: a stable ushort id per command, assigned by ordinal-sorting the canonical
    // names so the id↔name mapping is identical on every machine. This is the command's deterministic,
    // hashable, wire-compact identity in a CommandSnapshot — strings stay on the text/config side.
    private readonly Dictionary<string, ushort> m_idByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly string[] m_nameById;
    // The text-dispatch FAST PATH table (see Submit): every command built via CommandDefinition.WithTrailingArgs with
    // Immediate routing, keyed ORDINAL by its name and each alias — ordinal because System.CommandLine matches command
    // names case-SENSITIVELY, so a case-insensitive key here would fast-path a line the full parse would reject. Frozen
    // once at construction (read-only, read-heavy). A miss falls through to the unchanged System.CommandLine parse.
    private readonly FrozenDictionary<string, CommandDefinition> m_fastPath;
    // The span-keyed alternate view over m_fastPath: the fast path looks a verb up by the line's leading-token SPAN, so
    // the verb token never materializes as a string. StringComparer.Ordinal supplies the IAlternateEqualityComparer that
    // makes this legal; built once, reused every dispatch.
    private readonly FrozenDictionary<string, CommandDefinition>.AlternateLookup<ReadOnlySpan<char>> m_fastPathAlt;
    // The Digital impulse every fast-path verb carries (WithTrailingArgs and WithWireArgs are always Digital), hoisted to
    // a constant so a fast-path dispatch neither recomputes it nor rebuilds the CommandContext around it.
    private static readonly CommandValue s_digitalImpulse = CommandValue.Digital(active: true);
    // The ONE reused context for every fast-path dispatch: Parse/Phase/Registry are constant on this path and Value is
    // always s_digitalImpulse, so a single immutable instance serves every wire-native and trailing-args fast dispatch —
    // no per-line context construction. (CommandContext is a readonly record struct, so reusing it is safe.)
    private readonly CommandContext m_fastContext;
    // The wire acknowledgement mode: false (the default) echoes every accepted line exactly as before; true (`wire.ack
    // quiet`) drops the SUCCESS acks of wire-native verbs, so a flood of accepted commands costs no echo bytes. Errors
    // and query (EchoesData) verbs are never suppressed. Toggled by the built-in `wire.ack` verb.
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
    // result on either dispatch path, and a Simulation re-parse that failed to reach its handler — increments the same
    // counter, so a scripted driver reads one number back instead of pattern-matching free-form error text.
    private readonly Argument<string[]> m_wireErrorsArgument = new(name: "mode") {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "reset",
    };
    private readonly Command m_wireErrorsCommand;
    private int m_rejections;
    // The deterministic-input sink a Simulation-class submitted command is folded into instead of running inline;
    // null until a host wires one (the live console-driving registry), so every other registry keeps the inline path.
    private ICommandInjectionSink? m_injectionSink;
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

        foreach (var module in modules) {
            foreach (var definition in module.GetCommands()) {
                m_root.Subcommands.Add(item: definition.TextCommand);
                m_byTextCommand[definition.TextCommand] = definition;
                m_byName[definition.Name] = definition;

                foreach (var alias in definition.Aliases) {
                    m_byName[alias] = definition;
                    definition.TextCommand.Aliases.Add(item: alias);
                }
            }
        }

        m_root.Subcommands.Add(item: m_helpCommand);

        // The wire's own control verb, beside help: `wire.ack [on|quiet]` reports or flips the acknowledgement mode.
        m_wireAckCommand = new Command(
            description: "Sets or reports the stdin acknowledgement mode: wire.ack [on|quiet] — `on` (default) echoes every accepted command; `quiet` drops the success acks of side-effecting verbs (errors and query verbs like player.where still echo); no argument reports the current mode.",
            name: "wire.ack"
        ) {
            m_wireAckArgument,
        };
        m_root.Subcommands.Add(item: m_wireAckCommand);

        // The wire's rejection readback, beside wire.ack: `wire.errors [reset]`.
        m_wireErrorsCommand = new Command(
            description: "Reports the number of submitted lines this session REFUSED (unknown verb, parse error, or a handler's failure result): wire.errors [reset] — no argument reports the running count; `reset` reports it and zeroes the counter. A scripted run asserts `[wire.errors: 0 rejected]` to prove no step silently no-opped.",
            name: "wire.errors"
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
            .OrderBy(keySelector: static name => name, comparer: StringComparer.Ordinal)
            .ToArray();

        for (var id = 0; (id < m_nameById.Length); id++) {
            m_idByName[m_nameById[id]] = (ushort)id;
        }

        foreach (var (name, definition) in m_byName) {
            m_idByName[name] = m_idByName[definition.Name];
        }

        // The fast-path table: every name/alias whose definition carries a trailing-args handler (built via
        // CommandDefinition.WithTrailingArgs) AND runs inline (Immediate routing — a Simulation command must still fold
        // into the snapshot stream, so it is excluded and takes the full parse). Ordinal-keyed to mirror
        // System.CommandLine's case-sensitive command matching. m_byName's keys carry each name and alias verbatim.
        var fastPath = new Dictionary<string, CommandDefinition>(comparer: StringComparer.Ordinal);

        foreach (var (name, definition) in m_byName) {
            if ((definition.TrailingArgsHandler is not null) && (definition.Routing == CommandRouting.Immediate)) {
                fastPath[name] = definition;
            }
        }

        m_fastPath = fastPath.ToFrozenDictionary(comparer: StringComparer.Ordinal);
        m_fastPathAlt = m_fastPath.GetAlternateLookup<ReadOnlySpan<char>>();
        m_byNameAlt = m_byName.GetAlternateLookup<ReadOnlySpan<char>>();
        m_fastContext = new CommandContext(
            Parse: null,
            Phase: CommandPhase.Completed,
            Registry: this,
            Value: s_digitalImpulse
        );
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

        return m_idByName.TryGetValue(key: name, value: out id);
    }
    /// <summary>Gets the canonical name for an interned command id.</summary>
    /// <param name="id">The interned id, in <c>[0, <see cref="CommandCount"/>)</c>.</param>
    /// <returns>The command's canonical name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> is not a valid interned id.</exception>
    public string GetName(ushort id) {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: id, other: (ushort)m_nameById.Length);

        return m_nameById[id];
    }

    /// <summary>Returns the default "fully active" value used for a text invocation that supplies no explicit value.</summary>
    /// <param name="kind">The value kind of the command being invoked.</param>
    /// <returns>An active value for digital and axis kinds; an inactive value for kinds that have no meaningful impulse.</returns>
    private static CommandValue ImpulseValue(CommandValueKind kind) {
        return kind switch {
            CommandValueKind.Digital => CommandValue.Digital(active: true),
            CommandValueKind.Axis1D => CommandValue.Axis(value: 1f),
            CommandValueKind.Axis2D => CommandValue.Axis(value: Vector2.One),
            _ => CommandValue.Inactive(kind: kind),
        };
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
            while ((index < line.Length) && char.IsWhiteSpace(c: line[index])) {
                index++;
            }

            if (index >= line.Length) {
                break;
            }

            var start = index;

            while ((index < line.Length) && !char.IsWhiteSpace(c: line[index])) {
                index++;
            }

            if (count >= tokens.Length) {
                return -1;
            }

            tokens[count++] = new Range(start: start, end: index);
        }

        return count;
    }

    /// <summary>Reports or flips the wire acknowledgement mode for the built-in <c>wire.ack</c> verb.</summary>
    /// <param name="mode">The parsed trailing tokens: empty reports the current mode; <c>on</c>/<c>quiet</c> set it.</param>
    /// <returns>A result echoing the resulting mode, or an <see cref="CommandResult.IsError"/> result for a bad argument.</returns>
    private CommandResult ApplyWireAck(string[] mode) {
        if (mode.Length == 0) {
            return new CommandResult((m_acksQuiet ? "[wire.ack: quiet]" : "[wire.ack: on]"));
        }

        if (mode.Length > 1) {
            return new CommandResult(Output: "[wire.ack: expected one of on | quiet]") {
                IsError = true,
            };
        }

        switch (mode[0]) {
            case "on":
                m_acksQuiet = false;

                return new CommandResult(Output: "[wire.ack: on]");
            case "quiet":
                m_acksQuiet = true;

                return new CommandResult(Output: "[wire.ack: quiet]");
            default:
                return new CommandResult(Output: $"[wire.ack: unknown mode '{mode[0]}' — expected on | quiet]") {
                    IsError = true,
                };
        }
    }

    /// <summary>Reports (and optionally clears) the refused-submission count for the built-in <c>wire.errors</c> verb.</summary>
    /// <param name="mode">The parsed trailing tokens: empty reports the count; <c>reset</c> reports and zeroes it.</param>
    /// <returns>A result echoing the count, or an <see cref="CommandResult.IsError"/> result for a bad argument.</returns>
    private CommandResult ApplyWireErrors(string[] mode) {
        if (mode.Length > 1) {
            return Reject(text: "[wire.errors: expected no argument or `reset`]");
        }

        if ((mode.Length == 1) && !string.Equals(a: mode[0], b: "reset", comparisonType: StringComparison.Ordinal)) {
            return Reject(text: $"[wire.errors: unknown mode '{mode[0]}' — expected `reset`]");
        }

        // Read the count BEFORE `reset` zeroes it, and do not let this verb's own report count as a rejection.
        var rejected = m_rejections;

        if (mode.Length == 1) {
            m_rejections = 0;
        }

        return new CommandResult(Output: $"[wire.errors: {rejected} rejected]");
    }

    // A refused submission. Counting happens once, in Submit, over every error result from either dispatch path, so
    // this helper only shapes the result — nothing here double-counts.
    private static CommandResult Reject(string text) => new(Output: text) {
        IsError = true,
    };

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
    /// <summary>Clears all transient per-frame command values, then re-seeds the held ones.</summary>
    /// <remarks>Call once at the start of each frame, before <see cref="Collect"/>.</remarks>
    public void BeginFrame() {
        m_state.Clear();

        // Re-seed held digital inputs so a continuous consumer polling GetValue sees them asserted every frame
        // they remain down, without the source re-pushing them. Handlers do not re-run — only the value is set.
        foreach (var held in m_held) {
            m_state[held.Key] = held.Value;
        }
    }
    /// <summary>Clears all held digital values so nothing stays asserted.</summary>
    /// <remarks>
    /// Call on focus loss: when the window is not focused, key/button releases are not delivered, so a value
    /// held down at the moment focus was lost would otherwise stay stuck until the input is pressed and
    /// released again.
    /// </remarks>
    public void ReleaseHeld() {
        m_held.Clear();
    }
    /// <summary>Pulls the current frame's activations from every registered source, in registration order.</summary>
    public void Collect() {
        foreach (var source in m_sources) {
            source.Collect(sink: this);
        }
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
    /// <summary>Gets the current frame's value for a command.</summary>
    /// <param name="name">The name of the command to read.</param>
    /// <param name="kind">The value kind to assign to the result when the command has no value this frame.</param>
    /// <returns>The command's value for the current frame, or an inactive value of <paramref name="kind"/> if none was recorded.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public CommandValue GetValue(string name, CommandValueKind kind) {
        ArgumentNullException.ThrowIfNull(name);

        return (m_state.TryGetValue(
            key: name,
            value: out var value
        )
            ? value
            : CommandValue.Inactive(kind: kind));
    }
    /// <summary>Determines whether a command map is currently active.</summary>
    /// <param name="map">The name of the map to test.</param>
    /// <returns><see langword="true"/> if the map is active; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="map"/> is <see langword="null"/>.</exception>
    public bool IsMapActive(string map) {
        ArgumentNullException.ThrowIfNull(map);

        return m_activeMaps.Contains(item: map);
    }
    /// <summary>Determines whether a source-driven command id currently belongs to an active map.</summary>
    /// <param name="commandId">The interned command id.</param>
    /// <returns><see langword="true"/> when the command exists and its map is active.</returns>
    internal bool IsSourceCommandActive(ushort commandId) {
        if (commandId >= m_nameById.Length) {
            return false;
        }

        return (m_byName.TryGetValue(key: m_nameById[commandId], value: out var definition) &&
            m_activeMaps.Contains(item: definition.Map));
    }

    /// <summary>Whether a submitted simulation command is waiting for its fixed-step snapshot to apply.</summary>
    internal bool HasPendingSimulationSubmission => (m_pendingSimulationSubmissions != 0);
    /// <summary>Whether the line's verb resolves to a <see cref="CommandRouting.Simulation"/>-routed command. Such a
    /// line may drain behind an unapplied deferred mutation (it folds into the same pending snapshot, FIFO); an
    /// unresolved or <see cref="CommandRouting.Immediate"/> line reads applied state, so it must wait.</summary>
    /// <param name="line">The command line whose leading verb token is classified.</param>
    internal bool RoutesToSimulation(string line) {
        var content = line.AsSpan().Trim();

        if (content.IsEmpty) {
            return false;
        }

        var separator = content.IndexOfAny(values: " \t");
        var verb = ((separator < 0) ? content : content[..separator]);

        return (m_byNameAlt.TryGetValue(key: verb, value: out var definition) &&
            (definition.Routing == CommandRouting.Simulation));
    }
    /// <summary>
    /// Applies one fixed-step tick's <see cref="CommandSnapshot"/>: records each command's value for polling
    /// (<see cref="GetValue"/>) and dispatches edge handlers, gated by the active command maps. This is the
    /// snapshot-driven peer of the per-render-frame <see cref="Collect"/> path; the <see cref="InputRouter"/>
    /// owns held-folding, so this never touches the registry's own held state.
    /// </summary>
    /// <param name="snapshot">The tick's input snapshot to apply.</param>
    public void ApplySnapshot(in CommandSnapshot snapshot) {
        m_state.Clear();

        if (snapshot.Lanes.IsDefaultOrEmpty) {
            return;
        }

        foreach (var lane in snapshot.Lanes) {
            foreach (var entry in lane.Entries) {
                var name = m_nameById[entry.CommandId];

                // A submitted text entry owns a FIFO-barrier count. Always route it through the completion helper first:
                // even a defensive name-table miss must reach that helper's finally block and release the barrier.
                if (entry.Text is { } line) {
                    if (entry.Dispatch) {
                        ApplySubmittedSimulation(
                            line: line,
                            expectedCommandId: entry.CommandId,
                            slot: lane.Slot,
                            completesTextSubmission: entry.CompletesTextSubmission
                        );
                    } else if (entry.CompletesTextSubmission && (m_pendingSimulationSubmissions != 0)) {
                        m_pendingSimulationSubmissions--;
                    }

                    continue;
                }

                if (!m_byName.TryGetValue(
                    key: name,
                    value: out var definition
                )) {
                    continue;
                }

                if (!m_activeMaps.Contains(item: definition.Map)) {
                    continue;
                }

                m_state[name] = entry.Value;

                if (!entry.Dispatch) {
                    continue;
                }

                var context = new CommandContext(
                    DeviceId: entry.Device,
                    Parse: null,
                    Phase: entry.Phase,
                    Registry: this,
                    Slot: lane.Slot,
                    Text: null,
                    Value: entry.Value,
                    AssignedSlot: entry.AssignedSlot
                );

                _ = Dispatch(
                    context: in context,
                    definition: definition
                );
            }
        }
    }
    // Executes a simulation-routed text command from its tick snapshot. Submit already parsed and identified the line
    // before injection; parsing again here recreates the handler's ordinary text context without re-routing it.
    private void ApplySubmittedSimulation(string line, ushort expectedCommandId, int slot, bool completesTextSubmission) {
        try {
            var parseResult = m_root.Parse(commandLine: line);

            if ((parseResult.Errors.Count != 0) ||
                !m_byTextCommand.TryGetValue(key: parseResult.CommandResult.Command, value: out var definition) ||
                !TryGetId(name: definition.Name, id: out var actualCommandId) ||
                (actualCommandId != expectedCommandId)) {
                // A snapshot-routed line that no longer re-parses to the command it was injected as never reaches its
                // handler. Submit already returned None for it, so this is the only place it can be counted — without
                // it a Simulation-routed rejection stays invisible to wire.errors.
                m_rejections++;

                return;
            }

            var value = (definition.ValueSelector?.Invoke(arg: parseResult) ?? ImpulseValue(kind: definition.ValueKind));
            var context = new CommandContext(
                Parse: parseResult,
                Phase: CommandPhase.Completed,
                Registry: this,
                Slot: slot,
                Text: line,
                Value: value
            );

            // Submit returned None when it injected this line, so its handler's verdict lands here rather than at the
            // console call site — count a failure so a deferred mutation's rejection reaches wire.errors too.
            if (Dispatch(context: in context, definition: definition, suppressWireAck: true).IsError) {
                m_rejections++;
            }
        } finally {
            if (completesTextSubmission && (m_pendingSimulationSubmissions != 0)) {
                m_pendingSimulationSubmissions--;
            }
        }
    }
    /// <summary>
    /// Records a signal's value as the command's value for the current frame and runs the command's
    /// handler.
    /// </summary>
    /// <param name="signal">The activation to process.</param>
    /// <remarks>
    /// The signal is ignored if it names an unknown command or if the command's map is not active. This
    /// map check is the modality gate that distinguishes source-driven activation from the text path.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The signal's <see cref="CommandSignal.Name"/> is <see langword="null"/>.</exception>
    public void Push(CommandSignal signal) {
        ArgumentNullException.ThrowIfNull(signal.Name);

        if (!m_byName.TryGetValue(
            key: signal.Name,
            value: out var definition
        )) {
            return;
        }

        if (!m_activeMaps.Contains(item: definition.Map)) {
            return;
        }

        m_state[signal.Name] = signal.Value;

        // A held digital input persists its polled value until released; a release (or cancel) clears it, so a
        // continuous consumer can poll "is it down" across frames. The dispatch gate below keeps the handler
        // firing only on the edges the binding answers, so a held key never re-runs a press-driven handler.
        if (
            (signal.Phase == CommandPhase.Started) &&
            signal.Value.IsActive &&
            (signal.Text is null)
        ) {
            m_held[signal.Name] = signal.Value;
        } else if (signal.Phase is CommandPhase.Completed or CommandPhase.Canceled) {
            _ = m_held.Remove(key: signal.Name);
        }

        if (!signal.Dispatch) {
            return;
        }

        var context = new CommandContext(
            DeviceId: signal.DeviceId,
            Parse: null,
            Phase: signal.Phase,
            Registry: this,
            Text: signal.Text,
            Value: signal.Value
        );

        _ = Dispatch(
            context: in context,
            definition: definition
        );
    }

    /// <summary>Runs a command's handler and notifies every observer of the dispatch.</summary>
    /// <param name="context">The invocation state passed to the handler.</param>
    /// <param name="definition">The command being dispatched.</param>
    /// <param name="suppressWireAck">Whether quiet wire mode may suppress a successful acknowledgement.</param>
    /// <returns>The result the handler returned.</returns>
    private CommandResult Dispatch(in CommandContext context, CommandDefinition definition, bool suppressWireAck = false) {
        var result = definition.Handler(arg: context);

        if (suppressWireAck && m_acksQuiet && (definition.WireArgsHandler is not null) && !definition.EchoesData && !result.IsError) {
            result = CommandResult.None;
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
    /// <summary>Registers a producer to be pulled on each <see cref="Collect"/>.</summary>
    /// <param name="source">The source to register. Sources are pulled in registration order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public void AddSource(ICommandSource source) {
        ArgumentNullException.ThrowIfNull(source);

        m_sources.Add(item: source);
    }
    /// <summary>
    /// Routes <see cref="CommandRouting.Simulation"/>-class submitted commands to a deterministic input sink instead
    /// of running them inline — the seam that makes a console / STDIN line drive the simulation deterministically.
    /// </summary>
    /// <param name="sink">The sink (the host's <see cref="InputRouter"/>) folded-into per tick; <see langword="null"/> restores inline execution.</param>
    /// <remarks>Wire this only on the host's live console-driving registry; an unwired registry runs every submitted command inline.</remarks>
    public void RouteSimulationTo(ICommandInjectionSink? sink) {
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
    /// (so it is tick-aligned, recorded, and replayed) when a sink is wired via <see cref="RouteSimulationTo"/>;
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
        // command registered via WithTrailingArgs/WithWireArgs with Immediate routing. Anything else — an unknown/other-
        // shape first token, a quoted or response-file line, help, wire.ack — falls through to the parse below UNCHANGED,
        // so all error text and rich behavior stay byte-identical.
        //
        // ZERO-COPY: the line is tokenized into a stackalloc Span<Range> (Tokenize reproduces
        // Split((char[])null, RemoveEmptyEntries) whitespace semantics exactly), the verb is looked up by its SPAN via
        // the frozen alternate lookup (so the verb token never materializes), and the reused m_fastContext is handed to
        // the handler. A WIRE-NATIVE verb (WithWireArgs) receives a zero-copy WireArgs over the trailing token ranges —
        // no substrings, no argument array; a trailing-args verb (Demo) gets a materialized string[], built
        // only now, at dispatch. The context's Parse is null: no fast-path handler reads context.Parse/Value/Phase/
        // DeviceId (they read only their args) — a Verb like the movement keys does, but a Verb never enters this path.
        if ((line.IndexOf(value: '"') < 0) && (line.IndexOf(value: '@') < 0)) {
            Span<Range> tokenRanges = stackalloc Range[MaxFastPathTokens];
            var tokenCount = Tokenize(line: line, tokens: tokenRanges);

            if ((tokenCount > 0) && m_fastPathAlt.TryGetValue(key: line.AsSpan(range: tokenRanges[0]), value: out var fast)) {
                var argRanges = tokenRanges[1..tokenCount];

                m_state[fast.Name] = s_digitalImpulse;

                if (fast.WireArgsHandler is { } wireHandler) {
                    var result = wireHandler(
                        arg1: m_fastContext,
                        arg2: new WireArgs(line: line, ranges: argRanges, echo: !m_acksQuiet)
                    );

                    // Quiet mode drops a SUCCESSFUL wire-native ack (the handler already skipped building it via
                    // WireArgs.Echo, so this is the contract backstop): a query verb's data (EchoesData) and every error
                    // (IsError) always survive, so a scripted run still reads back poses and still sees its failures.
                    return ((m_acksQuiet && !fast.EchoesData && !result.IsError)
                        ? CommandResult.None
                        : result);
                }

                // Legacy trailing-args verb: materialize the argument strings now (only the trailing tokens, and only on
                // an actual dispatch — the verb token is never substringed).
                var args = new string[argRanges.Length];

                for (var index = 0; (index < args.Length); index++) {
                    args[index] = line[argRanges[index]];
                }

                return fast.TrailingArgsHandler!(
                    arg1: m_fastContext,
                    arg2: args
                );
            }
        }

        var parseResult = m_root.Parse(commandLine: line);

        if (parseResult.Errors.Count > 0) {
            return Reject(text: $"[wire.reject: {string.Join(
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
            if ((definition.Routing == CommandRouting.Simulation) &&
                (m_injectionSink is { } sink) &&
                TryGetId(name: definition.Name, id: out var commandId)) {
                m_pendingSimulationSubmissions++;

                try {
                    sink.Inject(injection: new CommandInjection(CommandId: commandId, Value: value, Phase: CommandPhase.Started, Text: line) {
                        CompletesTextSubmission = true,
                    });
                } catch {
                    m_pendingSimulationSubmissions--;

                    throw;
                }

                return CommandResult.None;
            }

            m_state[definition.Name] = value;

            // The text path returns its result to the caller, so it is not observed (the caller displays
            // it); observers exist for the source-driven path, which has no return value to inspect.
            var result = definition.Handler(arg: new CommandContext(
                Parse: parseResult,
                Phase: CommandPhase.Completed,
                Registry: this,
                Value: value
            ));

            // Apply the same quiet-mode ack suppression the fast path does, so a quoted or many-token wire line (which
            // takes this full parse) obeys wire.ack identically: a successful side-effecting wire-native ack is dropped,
            // while a query verb's data and every error survive. Non-wire verbs are never suppressed.
            return ((m_acksQuiet && (definition.WireArgsHandler is not null) && !definition.EchoesData && !result.IsError)
                ? CommandResult.None
                : result);
        }

        return Reject(text: $"[wire.reject: unknown command '{line}']");
    }
}

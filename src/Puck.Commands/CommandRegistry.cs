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
            : (observers as ICommandObserver[]) ?? observers.ToArray());

        foreach (var module in modules) {
            foreach (var definition in module.GetCommands()) {
                m_root.Subcommands.Add(definition.TextCommand);
                m_byTextCommand[definition.TextCommand] = definition;
                m_byName[definition.Name] = definition;

                foreach (var alias in definition.Aliases) {
                    m_byName[alias] = definition;
                    definition.TextCommand.Aliases.Add(item: alias);
                }
            }
        }

        m_root.Subcommands.Add(m_helpCommand);
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
    /// <returns>The result the handler returned.</returns>
    private CommandResult Dispatch(in CommandContext context, CommandDefinition definition) {
        var result = definition.Handler(arg: context);

        if (m_observers.Length != 0) {
            var activation = new CommandActivation(
                Name: definition.Name,
                Phase: context.Phase,
                Result: result,
                Text: context.Text
            );

            for (var index = 0; index < m_observers.Length; index++) {
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
    /// <summary>Parses a command line, runs the matching handler, and returns its transcript output.</summary>
    /// <param name="line">The command line to parse and execute.</param>
    /// <returns>
    /// The handler's result; <see cref="CommandResult.None"/> for an empty or whitespace line; the help
    /// listing for the <c>help</c> command; or a message describing parse errors or an unknown command.
    /// </returns>
    /// <remarks>This path is never gated by command maps; it is the deliberate console entry point.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is <see langword="null"/>.</exception>
    public CommandResult Submit(string line) {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(value: line)) {
            return CommandResult.None;
        }

        var parseResult = m_root.Parse(commandLine: line);

        if (parseResult.Errors.Count > 0) {
            return new CommandResult(string.Join(
                separator: '\n',
                values: parseResult.Errors.Select(selector: error => error.Message)
            ));
        }

        var command = parseResult.CommandResult.Command;

        if (command == m_helpCommand) {
            return new CommandResult(BuildHelpText());
        }

        if (m_byTextCommand.TryGetValue(
            key: command,
            value: out var definition
        )) {
            var value = (definition.ValueSelector?.Invoke(arg: parseResult) ?? ImpulseValue(kind: definition.ValueKind));

            m_state[definition.Name] = value;

            // The text path returns its result to the caller, so it is not observed (the caller displays
            // it); observers exist for the source-driven path, which has no return value to inspect.
            return definition.Handler(arg: new CommandContext(
                Parse: parseResult,
                Phase: CommandPhase.Completed,
                Registry: this,
                Value: value
            ));
        }

        return new CommandResult($"Unknown command: {line}");
    }
}

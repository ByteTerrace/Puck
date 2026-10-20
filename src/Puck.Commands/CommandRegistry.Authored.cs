using System.Text;

namespace Puck.Commands;

public sealed partial class CommandRegistry {
    // The registry's own verb that dispatches every authored command. It is interned like help or wire.errors, so a
    // bound press on an authored command carries this id and the command's name in its line.
    private const string RunCommandName = "run";

    private readonly CommandDefinition m_runner;
    private Func<AuthoredCommandSet>? m_authoredSource;

    /// <summary>Gets the authored commands the registry currently answers: the set its source supplies now, or
    /// <see cref="AuthoredCommandSet.Empty"/> when no source is wired.</summary>
    /// <remarks>Read on every resolution, so a source that follows a live document (a reload, a row edit) changes
    /// what the registry answers without rebuilding it.</remarks>
    public AuthoredCommandSet AuthoredCommands => (m_authoredSource?.Invoke() ?? AuthoredCommandSet.Empty);

    /// <summary>Returns the facts every authored command declares: a bindable, Simulation-routed, wire-argument verb
    /// that answers anyone, carrying a digital press when bound.</summary>
    /// <param name="name">The authored command's name.</param>
    /// <returns>The authored command's metadata.</returns>
    public static CommandMetadata AuthoredMetadata(string name) => new(
        AcceptsWireArgs: true,
        Audience: CommandAudience.Anyone,
        Bindability: CommandBindability.Bindable,
        Name: name,
        Routing: CommandRouting.Simulation,
        ValueKind: CommandValueKind.Digital
    );
    /// <summary>Checks a set of authored commands against this registry: no command takes a name a registered verb
    /// or alias holds, every line calls a registered verb a binding may name (bindable, answering anyone) or another
    /// command of the set, and no command reaches itself through the set.</summary>
    /// <param name="set">The authored commands to check.</param>
    /// <param name="errors">The list each refusal is appended to, naming the command and line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> or <paramref name="errors"/> is
    /// <see langword="null"/>.</exception>
    public void CheckAuthored(AuthoredCommandSet set, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: set);
        ArgumentNullException.ThrowIfNull(argument: errors);

        foreach (var command in set.Commands) {
            if (m_byName.TryGetValue(
                key: command.Name,
                value: out var shadowed
            )) {
                errors.Add(item: $"command '{command.Name}' takes the name of the engine verb '{shadowed.Name}'; an authored command never shadows a registered verb or alias.");
            }

            for (var index = 0; (index < command.Lines.Length); index++) {
                var verb = command.Lines[index].Verb;

                if (set.TryGet(
                    command: out _,
                    name: verb
                )) {
                    continue;
                }

                if (!m_byName.TryGetValue(
                    key: verb,
                    value: out var called
                )) {
                    errors.Add(item: $"command '{command.Name}' line {(index + 1)} calls '{verb}', which no registered verb or authored command answers.");
                } else if (RefusesAuthoredCall(definition: called) is { } refusal) {
                    errors.Add(item: $"command '{command.Name}' line {(index + 1)} calls '{verb}', {refusal}.");
                }
            }
        }

        foreach (var command in set.Commands) {
            if (FindCycle(path: [command.Name], set: set) is { } cycle) {
                errors.Add(item: $"command '{command.Name}' calls itself: {string.Join(separator: " -> ", values: cycle)}.");
            }
        }
    }
    /// <summary>Whether <paramref name="name"/> names an authored command this registry currently answers.</summary>
    /// <param name="name">The command name.</param>
    /// <returns><see langword="true"/> when the name is authored.</returns>
    public bool NamesAuthoredCommand(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return AuthoredCommands.TryGet(command: out _, name: name);
    }
    /// <summary>Wires the source of the authored commands this registry answers, alongside the verbs its modules
    /// register.</summary>
    /// <param name="source">Returns the current set on every call; <see langword="null"/> answers none.</param>
    /// <remarks>The registry never caches the set, so the source decides when it changes; a host that compiles the
    /// set from a document keeps it for as long as that document is live.</remarks>
    public void UseAuthoredCommands(Func<AuthoredCommandSet>? source) {
        m_authoredSource = source;
    }

    private static List<string>? FindCycle(List<string> path, AuthoredCommandSet set) {
        if (!set.TryGet(command: out var command, name: path[^1])) {
            return null;
        }

        foreach (var line in command!.Lines) {
            if (!set.TryGet(command: out var callee, name: line.Verb)) {
                continue;
            }

            if (string.Equals(a: callee!.Name, b: path[0], comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return [.. path, callee.Name];
            }

            if (path.Contains(value: callee.Name, comparer: StringComparer.OrdinalIgnoreCase)) {
                continue;
            }

            if (FindCycle(path: [.. path, callee.Name], set: set) is { } cycle) {
                return cycle;
            }
        }

        return null;
    }
    // A line submitted by a macro dispatches in the phase a typed line of its routing would carry when it runs: a
    // Simulation verb applies as the Started edge its queued injection carries, an Immediate one as Completed.
    private static CommandPhase InlinePhase(CommandDefinition definition, bool queue) => ((!queue && (definition.Routing == CommandRouting.Simulation))
        ? CommandPhase.Started
        : CommandPhase.Completed
    );
    // What an authored line may call is what a binding document may name: a bindable verb answering anyone. The
    // registry's own `run` is admitted beside them, since it dispatches only authored commands.
    private string? RefusesAuthoredCall(CommandDefinition definition) {
        if (ReferenceEquals(objA: definition, objB: m_runner)) {
            return null;
        }

        if (definition.Audience != CommandAudience.Anyone) {
            return "which answers the operator alone; an authored command calls only what a binding may name";
        }

        return ((definition.Bindability == CommandBindability.Bindable)
            ? null
            : "which is not bindable; an authored command calls only what a binding may name");
    }
    // `run`'s handler: names the authored command (the line's own verb, or run's first argument), expands it, and
    // submits each line under the caller's principal, slot and session policy. The first refused line stops the
    // command and is its verdict.
    private CommandResult RunAuthored(CommandContext context, WireArgs args) {
        // A release edge of a bound press carries no line, and there is nothing to run for it.
        if (context.Text is not { } line) {
            return CommandResult.None;
        }

        var verb = LeadingVerb(line: line);
        var first = 0;

        if (verb.Equals(other: RunCommandName, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (args.Count == 0) {
                return CommandResult.Usage(form: "run <command> [args…]", verb: RunCommandName);
            }

            verb = args[0];
            first = 1;
        }

        if (!AuthoredCommands.TryGet(command: out var command, name: verb)) {
            return CommandResult.Error(output: $"[{RunCommandName}: no authored command is named '{verb}' — help lists the world's commands]");
        }

        var arguments = new string[(args.Count - first)];

        for (var index = first; (index < args.Count); index++) {
            arguments[(index - first)] = args[index].ToString();
        }

        if (!command!.TryExpand(arguments: arguments, lines: out var lines, reason: out var reason)) {
            return CommandResult.Error(output: $"[{command.Name}: {reason}]");
        }

        var output = new StringBuilder();

        for (var index = 0; (index < lines.Length); index++) {
            var expanded = lines[index];
            var refusal = RefuseNestedLine(context: in context, line: expanded);
            var result = (refusal ?? SubmitNested(caller: in context, line: expanded));

            if (result.IsError) {
                return new CommandResult(Output: $"[{command.Name}: line {(index + 1)} '{expanded}' refused — {result.Output}]") {
                    Faulted = result.Faulted,
                    IsError = true,
                };
            }

            if (result.Output.Length != 0) {
                _ = ((output.Length == 0) ? output : output.Append(value: '\n')).Append(value: result.Output);
            }
        }

        return new CommandResult(Output: ((output.Length == 0)
            ? $"[{command.Name}: ran {lines.Length} line{((lines.Length == 1) ? string.Empty : "s")}]"
            : output.ToString()));
    }
    // The dispatch-time half of the authored-call rule, enforced on every expanded line whatever validated the set:
    // the verb must be one a binding may name, and one the caller's session may submit.
    private CommandResult? RefuseNestedLine(in CommandContext context, string line) {
        if (!TryResolveVerb(definition: out var definition, metadata: out var metadata, verb: LeadingVerb(line: line))) {
            return null;
        }

        if (RefusesAuthoredCall(definition: definition!) is { } refusal) {
            return CommandResult.Error(output: $"[{metadata.Name}: refused, {refusal}]");
        }

        return (((context.Authorize is { } authorize) && !authorize(metadata))
            ? CommandResult.Error(output: $"[{metadata.Name}: this session is not authorized for that command]")
            : null);
    }
    // Submits one line an authored command expanded, as its caller: the same principal, slot, session and session
    // policy, counted against the same nesting bound every re-entrant submission shares.
    private CommandResult SubmitNested(string line, in CommandContext caller) {
        if (m_submitDepth >= MaxSubmitDepth) {
            return CommandResult.Error(output: $"[wire.reject: command submission nested more than {MaxSubmitDepth} deep — '{line}' refused]");
        }

        m_submitDepth++;

        try {
            return SubmitCore(
                authorize: caller.Authorize,
                line: line,
                principal: caller.Principal,
                queue: false,
                session: caller.TextSession,
                slot: caller.Slot
            );
        } finally {
            m_submitDepth--;
        }
    }
    // Resolves a verb token to the definition that dispatches it and the facts it declares: a registered verb or
    // alias first, then an authored command, which `run` dispatches under the command's own metadata.
    private bool TryResolveVerb(ReadOnlySpan<char> verb, out CommandDefinition? definition, out CommandMetadata metadata) {
        if (m_byNameAlt.TryGetValue(
            key: verb,
            value: out definition
        )) {
            metadata = definition.Metadata;

            return true;
        }

        if (!verb.IsEmpty && AuthoredCommands.TryGet(command: out var authored, name: verb)) {
            definition = m_runner;
            metadata = AuthoredMetadata(name: authored!.Name);

            return true;
        }

        definition = null;
        metadata = default;

        return false;
    }
}

using System.Text;

namespace Puck.Commands;

public sealed partial class CommandRegistry {
    // The registry's own verb that dispatches every authored command. It is interned like help or wire.errors, so a
    // bound press on an authored command carries this id and the command's name in its line.
    private const string RunCommandName = "run";

    // Every authored name this registry has resolved, interned past the registered ids in first-resolution order and
    // never reused, so one name keeps one id for the registry's lifetime whatever sets come and go. A bound press
    // therefore holds per command: two keys bound to two authored commands are two buttons.
    private readonly Dictionary<string, ushort> m_authoredIdByName = new(comparer: StringComparer.OrdinalIgnoreCase);
    private readonly List<string> m_authoredNameById = [];
    private readonly CommandDefinition m_runner;
    private Func<int, AuthoredCommandSet>? m_authoredSource;
    private Func<string, CommandMetadata?>? m_verbCatalog;

    /// <summary>Gets the authored commands the registry answers outside any one line: the set its source supplies for
    /// slot <c>0</c>, which help, bindings and the affordance manifest read.</summary>
    public AuthoredCommandSet AuthoredCommands => AuthoredCommandsFor(slot: 0);

    /// <summary>Returns the authored commands the registry answers for a line on <paramref name="slot"/>: the set its
    /// source supplies now, or <see cref="AuthoredCommandSet.Empty"/> when no source is wired.</summary>
    /// <param name="slot">The slot of the line being resolved; a host whose slots address different documents (the
    /// silo's rows) answers each its own.</param>
    /// <returns>The current set for that slot.</returns>
    /// <remarks>Read on every resolution, so a source that follows a live document (a reload, a row edit) changes
    /// what the registry answers without rebuilding it.</remarks>
    public AuthoredCommandSet AuthoredCommandsFor(int slot) => (m_authoredSource?.Invoke(arg: slot) ?? AuthoredCommandSet.Empty);

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
    /// <remarks>A verb this registry lacks but the wired verb catalog holds (<see cref="UseVerbCatalog"/>) is one
    /// another boot shape registers: it is judged by the catalog's facts and admitted, and refused at dispatch as not
    /// available in this host instead.</remarks>
    public void CheckAuthored(AuthoredCommandSet set, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: set);

        set.CheckAgainst(
            errors: errors,
            refuseUnknown: true,
            registered: name => (m_byName.TryGetValue(key: name, value: out var definition)
                ? definition.Metadata
                : m_verbCatalog?.Invoke(arg: name))
        );
    }
    /// <summary>Wires the engine's full verb catalog: every verb any boot shape registers, so an authored line may
    /// name a verb only another shape (a rendered World) registers. Such a line passes validation here and is refused
    /// by name when it runs in a host that lacks the verb.</summary>
    /// <param name="catalog">Returns a catalogued verb's facts by name, or <see langword="null"/>; <see langword="null"/>
    /// unwires it, so a verb this registry lacks is refused at validation.</param>
    public void UseVerbCatalog(Func<string, CommandMetadata?>? catalog) {
        m_verbCatalog = catalog;
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
    /// <param name="source">Returns the current set for the slot of the line being resolved (<c>0</c> outside any
    /// line) on every call; <see langword="null"/> answers none.</param>
    /// <remarks>The registry never caches the set, so the source decides when it changes; a host that compiles the
    /// set from a document keeps it for as long as that document is live.</remarks>
    public void UseAuthoredCommands(Func<int, AuthoredCommandSet>? source) {
        m_authoredSource = source;
    }

    // The definition an interned id dispatches through: a registered command's own, `run` for an authored id, or
    // null for an id this registry never issued.
    private CommandDefinition? DefinitionForId(ushort id) {
        if (id < m_definitionById.Length) {
            return m_definitionById[id];
        }

        return (((id - m_definitionById.Length) < m_authoredNameById.Count)
            ? m_runner
            : null);
    }
    // The name an interned id stands for, registered or authored, or null for an id this registry never issued.
    private string? NameForId(ushort id) {
        if (id < m_nameById.Length) {
            return m_nameById[id];
        }

        var authored = (id - m_nameById.Length);

        return ((authored < m_authoredNameById.Count)
            ? m_authoredNameById[authored]
            : null);
    }
    // Whether a line injected under an authored command's own id still names that command; a registered id leaves the
    // check to the definition comparison beside it.
    private bool LineNamesAuthoredId(ushort id, string line) => ((id < m_nameById.Length) || string.Equals(
        a: NameForId(id: id),
        b: LeadingVerb(line: line).ToString(),
        comparisonType: StringComparison.OrdinalIgnoreCase
    ));
    // Interns an authored command's name, issuing the next id past every one already issued the first time it is seen.
    private bool TryInternAuthored(string name, out ushort id) {
        if (!AuthoredCommands.TryGet(command: out var command, name: name)) {
            id = 0;

            return false;
        }

        if (m_authoredIdByName.TryGetValue(key: command!.Name, value: out id)) {
            return true;
        }

        var next = (m_nameById.Length + m_authoredNameById.Count);

        if (next >= MaxCommandCount) {
            throw new InvalidOperationException(message: $"A command registry supports at most {MaxCommandCount} distinct commands because snapshot command ids are 16-bit; authored command '{command.Name}' would exceed it.");
        }

        id = ((ushort)next);
        m_authoredIdByName.Add(key: command.Name, value: id);
        m_authoredNameById.Add(item: command.Name);

        return true;
    }

    // A line submitted by a macro dispatches in the phase a typed line of its routing would carry when it runs: a
    // Simulation verb applies as the Started edge its queued injection carries, an Immediate one as Completed.
    private static CommandPhase InlinePhase(CommandDefinition definition, bool queue) => ((!queue && (definition.Routing == CommandRouting.Simulation))
        ? CommandPhase.Started
        : CommandPhase.Completed
    );
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

        if (!AuthoredCommandsFor(slot: context.Slot).TryGet(command: out var command, name: verb)) {
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
        if (!TryResolveVerb(definition: out _, metadata: out var metadata, slot: context.Slot, verb: LeadingVerb(line: line))) {
            var verb = LeadingVerb(line: line).ToString();

            return ((m_verbCatalog?.Invoke(arg: verb) is { } absent)
                ? CommandResult.Error(output: $"[{absent.Name}: not available in this host — another boot shape (a rendered World) registers it]")
                : null);
        }

        if (AuthoredCommandSet.RefusesCall(metadata: metadata) is { } refusal) {
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
    private bool TryResolveVerb(ReadOnlySpan<char> verb, int slot, out CommandDefinition? definition, out CommandMetadata metadata) {
        if (m_byNameAlt.TryGetValue(
            key: verb,
            value: out definition
        )) {
            metadata = definition.Metadata;

            return true;
        }

        if (!verb.IsEmpty && AuthoredCommandsFor(slot: slot).TryGet(command: out var authored, name: verb)) {
            definition = m_runner;
            metadata = AuthoredMetadata(name: authored!.Name);

            return true;
        }

        definition = null;
        metadata = default;

        return false;
    }
}

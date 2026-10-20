using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

namespace Puck.Commands;

/// <summary>Checks one argument an authored command's invoker typed for a parameter, and names the text that
/// substitutes for it.</summary>
/// <param name="value">The token the invoker typed, or the parameter's default.</param>
/// <param name="canonical">The text substituted into the command's lines: <paramref name="value"/> itself, or the
/// spelling the parameter declares when it matched another case.</param>
/// <returns><see langword="null"/> when the value is admitted; otherwise why it is refused.</returns>
public delegate string? AuthoredArgumentCheck(string value, out string canonical);

/// <summary>One parameter of an authored command: its name, the default a missing argument takes, and the check
/// every argument for it passes.</summary>
/// <param name="Name">The parameter's name, which a line's <c>{name}</c> placeholder substitutes.</param>
/// <param name="Kind">The parameter's kind as the help listing names it (<c>text</c>, <c>spawn</c>, …).</param>
/// <param name="Default">The value a missing argument takes, or <see langword="null"/> when the argument is
/// required.</param>
/// <param name="Check">The check each argument for this parameter passes; <see langword="null"/> admits any
/// token.</param>
public sealed record AuthoredCommandParameter(string Name, string Kind, string? Default, AuthoredArgumentCheck? Check);

/// <summary>A console command a document authors from existing verbs: a name, help text, typed parameters and an
/// ordered list of console lines whose <c>{parameter}</c> placeholders take the invoker's arguments.</summary>
/// <remarks>
/// An authored command is data, never code: running one submits each of its lines, with the arguments substituted,
/// through the registry exactly as the invoker could have typed them, under the invoker's own principal, slot and
/// session policy. A line's verb is the literal first token of its template, so no argument can choose which verb a
/// line reaches; an argument is one whitespace-free token, so none can add a line either. What a line may call is
/// what a binding document may name (<see cref="CommandRegistry.CheckAuthored"/>).
/// </remarks>
public sealed class AuthoredCommand {
    /// <summary>The most lines one authored command carries.</summary>
    public const int MaxLines = 32;
    /// <summary>The most parameters one authored command declares.</summary>
    public const int MaxParameters = 16;

    private AuthoredCommand(string name, string description, ImmutableArray<AuthoredCommandParameter> parameters, ImmutableArray<AuthoredCommandLine> lines) {
        Description = description;
        Lines = lines;
        Name = name;
        Parameters = parameters;
    }

    /// <summary>Gets the help text the listing shows: the author's own, then the usage and the lines it runs.</summary>
    public string Description { get; }
    /// <summary>Gets the lines the command submits, in order.</summary>
    public ImmutableArray<AuthoredCommandLine> Lines { get; }
    /// <summary>Gets the command's name, the leading token a line invokes it by.</summary>
    public string Name { get; }
    /// <summary>Gets the command's parameters, in argument order.</summary>
    public ImmutableArray<AuthoredCommandParameter> Parameters { get; }
    /// <summary>Gets the command's usage form: <c>name &lt;required:kind&gt; [optional:kind=default]</c>.</summary>
    public string Usage => DescribeUsage(name: Name, parameters: Parameters);

    private static string DescribeUsage(string name, ImmutableArray<AuthoredCommandParameter> parameters) {
        var builder = new StringBuilder(value: name);

        foreach (var parameter in parameters) {
            _ = ((parameter.Default is { } fallback)
                ? builder.Append(value: $" [{parameter.Name}:{parameter.Kind}={fallback}]")
                : builder.Append(value: $" <{parameter.Name}:{parameter.Kind}>"));
        }

        return builder.ToString();
    }
    /// <summary>Whether <paramref name="name"/> is a legal authored command name: a namespaced token such as
    /// <c>debug.go</c>, at least two dot-separated segments of letters, digits, <c>-</c> and <c>_</c>, each starting
    /// with a letter.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is legal.</returns>
    public static bool IsLegalName(string? name) {
        if (string.IsNullOrEmpty(value: name)) {
            return false;
        }

        var segments = name.Split(separator: '.');

        return ((segments.Length >= 2) && segments.All(predicate: IsLegalIdentifier));
    }
    /// <summary>Whether <paramref name="name"/> is a legal parameter name: a letter, then letters, digits, <c>-</c>
    /// and <c>_</c>.</summary>
    /// <param name="name">The candidate name.</param>
    /// <returns><see langword="true"/> when the name is legal.</returns>
    public static bool IsLegalIdentifier(string? name) {
        if (string.IsNullOrEmpty(value: name) || !char.IsAsciiLetter(c: name[0])) {
            return false;
        }

        foreach (var character in name) {
            if (!(char.IsAsciiLetterOrDigit(c: character) || (character is '-' or '_'))) {
                return false;
            }
        }

        return true;
    }
    /// <summary>Compiles an authored command, refusing by name every way its rows can be wrong.</summary>
    /// <param name="name">The command's name (<see cref="IsLegalName"/>).</param>
    /// <param name="help">The author's help text, or <see langword="null"/>.</param>
    /// <param name="parameters">The parameters, in argument order; a required one never follows an optional
    /// one.</param>
    /// <param name="lines">The line templates, in order (<see cref="AuthoredCommandLine.TryParse"/>).</param>
    /// <param name="errors">The list each refusal is appended to.</param>
    /// <returns>The compiled command, or <see langword="null"/> when anything was refused.</returns>
    public static AuthoredCommand? TryCreate(string? name, string? help, IReadOnlyList<AuthoredCommandParameter> parameters, IReadOnlyList<string?> lines, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: parameters);
        ArgumentNullException.ThrowIfNull(argument: lines);
        ArgumentNullException.ThrowIfNull(argument: errors);

        var before = errors.Count;
        var label = $"command '{name}'";

        if (!IsLegalName(name: name)) {
            errors.Add(item: $"{label}: a name is a namespaced token such as 'debug.go': two or more dot-separated segments, each a letter then letters, digits, '-' or '_'.");
        }

        if (parameters.Count > MaxParameters) {
            errors.Add(item: $"{label} declares {parameters.Count} parameters; at most {MaxParameters}.");
        }

        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var optional = false;

        foreach (var parameter in parameters) {
            if (!IsLegalIdentifier(name: parameter.Name)) {
                errors.Add(item: $"{label}: parameter '{parameter.Name}' is not a name (a letter, then letters, digits, '-' or '_').");
            } else if (!names.Add(item: parameter.Name)) {
                errors.Add(item: $"{label}: parameter '{parameter.Name}' is declared more than once.");
            }

            if (parameter.Default is { } fallback) {
                optional = true;

                if (CheckArgument(parameter: parameter, value: fallback, canonical: out _) is { } reason) {
                    errors.Add(item: $"{label}: parameter '{parameter.Name}' defaults to '{fallback}', which {reason}.");
                }
            } else if (optional) {
                errors.Add(item: $"{label}: required parameter '{parameter.Name}' follows an optional one; arguments are positional.");
            }
        }

        if (lines.Count == 0) {
            errors.Add(item: $"{label} runs no lines; author at least one.");
        } else if (lines.Count > MaxLines) {
            errors.Add(item: $"{label} runs {lines.Count} lines; at most {MaxLines}.");
        }

        var compiled = ImmutableArray.CreateBuilder<AuthoredCommandLine>(initialCapacity: lines.Count);
        var used = new HashSet<string>(comparer: StringComparer.Ordinal);
        var declared = parameters.Select(selector: static parameter => parameter.Name).ToArray();

        for (var index = 0; (index < lines.Count); index++) {
            if (!AuthoredCommandLine.TryParse(
                line: out var line,
                parameters: declared,
                reason: out var reason,
                template: lines[index]
            )) {
                errors.Add(item: $"{label}: line {(index + 1)} {reason}.");

                continue;
            }

            foreach (var slot in line!.Placeholders) {
                _ = used.Add(item: declared[slot]);
            }

            compiled.Add(item: line);
        }

        foreach (var parameter in parameters) {
            if (IsLegalIdentifier(name: parameter.Name) && !used.Contains(item: parameter.Name)) {
                errors.Add(item: $"{label}: parameter '{parameter.Name}' appears in no line; a line names it as {{{parameter.Name}}}.");
            }
        }

        if (errors.Count != before) {
            return null;
        }

        var parameterArray = parameters.ToImmutableArray();
        var linesArray = compiled.ToImmutable();
        var description = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(value: help)) {
            _ = description.Append(value: help.Trim()).Append(value: ' ');
        }

        _ = description.Append(value: $"Usage: {DescribeUsage(name: name!, parameters: parameterArray)}. Authored by the world; runs ")
            .AppendJoin(separator: " | ", values: linesArray.Select(selector: static line => line.Template))
            .Append(value: '.');

        return new AuthoredCommand(
            description: description.ToString(),
            lines: linesArray,
            name: name!,
            parameters: parameterArray
        );
    }

    private static string? CheckArgument(AuthoredCommandParameter parameter, string value, out string canonical) {
        canonical = value;

        if (
            (value.Length == 0) ||
            value.Any(predicate: static character => (char.IsWhiteSpace(c: character) || char.IsControl(c: character) || (character == '"')))
        ) {
            return "is not one plain token (no whitespace, no quotes)";
        }

        return parameter.Check?.Invoke(canonical: out canonical, value: value);
    }

    /// <summary>Binds the invoker's arguments to the parameters and expands every line.</summary>
    /// <param name="arguments">The invoker's argument tokens, in order.</param>
    /// <param name="lines">When this method returns <see langword="true"/>, the expanded lines in order.</param>
    /// <param name="reason">When this method returns <see langword="false"/>, why the arguments were refused.</param>
    /// <returns><see langword="true"/> when every argument was admitted.</returns>
    public bool TryExpand(IReadOnlyList<string> arguments, out string[] lines, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: arguments);

        lines = [];

        if (arguments.Count > Parameters.Length) {
            reason = $"takes at most {Parameters.Length} argument{((Parameters.Length == 1) ? string.Empty : "s")} — usage: {Usage}";

            return false;
        }

        var values = new string[Parameters.Length];

        for (var index = 0; (index < Parameters.Length); index++) {
            var parameter = Parameters[index];
            var value = ((index < arguments.Count)
                ? arguments[index]
                : parameter.Default
            );

            if (value is null) {
                reason = $"needs <{parameter.Name}:{parameter.Kind}> — usage: {Usage}";

                return false;
            }

            if (CheckArgument(parameter: parameter, value: value, canonical: out var canonical) is { } refusal) {
                reason = $"{parameter.Name} '{value}' {refusal} — usage: {Usage}";

                return false;
            }

            values[index] = canonical;
        }

        lines = Lines.Select(selector: line => line.Expand(values: values)).ToArray();
        reason = null;

        return true;
    }
}

/// <summary>One line template of an authored command: literal text with <c>{parameter}</c> placeholders, whose
/// first token, the verb, is always literal.</summary>
public sealed class AuthoredCommandLine {
    private readonly ImmutableArray<(string? Literal, int Slot)> m_segments;

    private AuthoredCommandLine(string template, string verb, ImmutableArray<(string? Literal, int Slot)> segments) {
        m_segments = segments;
        Template = template;
        Verb = verb;
    }

    /// <summary>Gets the parameter indices the template substitutes, in order of appearance.</summary>
    public IEnumerable<int> Placeholders => m_segments.Where(predicate: static segment => (segment.Literal is null)).Select(selector: static segment => segment.Slot);
    /// <summary>Gets the template as authored.</summary>
    public string Template { get; }
    /// <summary>Gets the literal verb the line calls: its first whitespace-delimited token.</summary>
    public string Verb { get; }

    /// <summary>Parses a line template: one line of console text whose <c>{name}</c> placeholders name declared
    /// parameters, <c>{{</c> and <c>}}</c> standing for literal braces. The first token is the verb and carries no
    /// placeholder, so an argument can never decide which verb a line reaches.</summary>
    /// <param name="template">The authored template.</param>
    /// <param name="parameters">The declared parameter names, in argument order.</param>
    /// <param name="line">When this method returns <see langword="true"/>, the parsed line.</param>
    /// <param name="reason">When this method returns <see langword="false"/>, why the template was refused.</param>
    /// <returns><see langword="true"/> when the template parsed.</returns>
    public static bool TryParse(string? template, IReadOnlyList<string> parameters, out AuthoredCommandLine? line, out string? reason) {
        ArgumentNullException.ThrowIfNull(argument: parameters);

        line = null;

        if (string.IsNullOrWhiteSpace(value: template)) {
            reason = "is blank; a line is one console command";

            return false;
        }

        if (template.Any(predicate: char.IsControl)) {
            reason = "carries a control character or a line break; a line is one console command";

            return false;
        }

        var trimmed = template.Trim();

        if (trimmed[0] == '#') {
            reason = "is a comment; a line is one console command";

            return false;
        }

        var verbEnd = 0;

        while ((verbEnd < trimmed.Length) && !char.IsWhiteSpace(c: trimmed[verbEnd])) {
            verbEnd++;
        }

        var verb = trimmed[..verbEnd];

        if (verb.AsSpan().IndexOfAny(values: "{}\"") >= 0) {
            reason = $"'{trimmed}' starts with '{verb}'; the verb is literal text, never a placeholder or a quoted token";

            return false;
        }

        var segments = ImmutableArray.CreateBuilder<(string? Literal, int Slot)>();
        var literal = new StringBuilder();
        var index = 0;

        while (index < trimmed.Length) {
            var character = trimmed[index];

            if ((character == '{') && ((index + 1) < trimmed.Length) && (trimmed[index + 1] == '{')) {
                _ = literal.Append(value: '{');
                index += 2;

                continue;
            }

            if ((character == '}') && ((index + 1) < trimmed.Length) && (trimmed[index + 1] == '}')) {
                _ = literal.Append(value: '}');
                index += 2;

                continue;
            }

            if (character == '}') {
                reason = $"'{trimmed}' has a '}}' that closes nothing; write '}}}}' for a literal brace";

                return false;
            }

            if (character != '{') {
                _ = literal.Append(value: character);
                index++;

                continue;
            }

            var close = trimmed.IndexOf(value: '}', startIndex: (index + 1));

            if (close < 0) {
                reason = $"'{trimmed}' opens a placeholder it never closes; write '{{{{' for a literal brace";

                return false;
            }

            var name = trimmed[(index + 1)..close];
            var slot = -1;

            for (var parameter = 0; (parameter < parameters.Count); parameter++) {
                if (string.Equals(a: parameters[parameter], b: name, comparisonType: StringComparison.Ordinal)) {
                    slot = parameter;

                    break;
                }
            }

            if (slot < 0) {
                reason = $"'{trimmed}' names {{{name}}}, which is no declared parameter";

                return false;
            }

            if (literal.Length != 0) {
                segments.Add(item: (literal.ToString(), -1));
                _ = literal.Clear();
            }

            segments.Add(item: (null, slot));
            index = (close + 1);
        }

        if (literal.Length != 0) {
            segments.Add(item: (literal.ToString(), -1));
        }

        line = new AuthoredCommandLine(segments: segments.ToImmutable(), template: trimmed, verb: verb);
        reason = null;

        return true;
    }

    internal string Expand(IReadOnlyList<string> values) {
        var builder = new StringBuilder();

        foreach (var (literal, slot) in m_segments) {
            _ = builder.Append(value: (literal ?? values[slot]));
        }

        return builder.ToString();
    }
}

/// <summary>The authored commands one document declares, compiled and keyed by name the way the registry resolves a
/// verb: ignoring case.</summary>
public sealed class AuthoredCommandSet {
    private readonly FrozenDictionary<string, AuthoredCommand> m_byName;
    private readonly FrozenDictionary<string, AuthoredCommand>.AlternateLookup<ReadOnlySpan<char>> m_byNameAlt;

    /// <summary>Initializes a set over <paramref name="commands"/>.</summary>
    /// <param name="commands">The compiled commands.</param>
    /// <exception cref="ArgumentException">Two commands share a name, ignoring case.</exception>
    public AuthoredCommandSet(IEnumerable<AuthoredCommand> commands) {
        ArgumentNullException.ThrowIfNull(argument: commands);

        var byName = new Dictionary<string, AuthoredCommand>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var command in commands) {
            if (!byName.TryAdd(key: command.Name, value: command)) {
                throw new ArgumentException(message: $"Authored command '{command.Name}' is declared more than once.", paramName: nameof(commands));
            }
        }

        m_byName = byName.ToFrozenDictionary(comparer: StringComparer.OrdinalIgnoreCase);
        m_byNameAlt = m_byName.GetAlternateLookup<ReadOnlySpan<char>>();
        Commands = [.. byName.Values.OrderBy(keySelector: static command => command.Name, comparer: StringComparer.Ordinal)];
    }

    /// <summary>Gets the set that authors nothing.</summary>
    public static AuthoredCommandSet Empty { get; } = new(commands: []);

    /// <summary>Gets the commands, ordinal-ordered by name.</summary>
    public ImmutableArray<AuthoredCommand> Commands { get; }

    /// <summary>Returns why an authored line may not call a verb with these facts, or <see langword="null"/> when it
    /// may: an authored line calls only what a binding document may name, a bindable verb answering anyone.</summary>
    /// <param name="metadata">The called verb's facts.</param>
    /// <returns>The refusal clause, or <see langword="null"/>.</returns>
    public static string? RefusesCall(CommandMetadata metadata) {
        if (metadata.Audience != CommandAudience.Anyone) {
            return "which answers the operator alone; an authored command calls only what a binding may name";
        }

        return ((metadata.Bindability == CommandBindability.Bindable)
            ? null
            : "which is not bindable; an authored command calls only what a binding may name");
    }
    /// <summary>Checks this set against a vocabulary of registered verbs: no command takes a registered verb's name,
    /// every line calls a registered verb a binding may name (<see cref="RefusesCall"/>) or another command of the
    /// set, and no command reaches itself through the set.</summary>
    /// <param name="registered">Returns a registered verb's facts by name or alias, ignoring case, or
    /// <see langword="null"/> for a name it does not know.</param>
    /// <param name="refuseUnknown">Whether a line calling a name <paramref name="registered"/> does not know is
    /// refused: true against a live registry, false against a catalog that may not hold every verb.</param>
    /// <param name="errors">The list each refusal is appended to, naming the command and line.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registered"/> or <paramref name="errors"/> is
    /// <see langword="null"/>.</exception>
    public void CheckAgainst(Func<string, CommandMetadata?> registered, bool refuseUnknown, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: registered);
        ArgumentNullException.ThrowIfNull(argument: errors);

        foreach (var command in Commands) {
            if (registered(arg: command.Name) is { } shadowed) {
                errors.Add(item: $"command '{command.Name}' takes the name of the engine verb '{shadowed.Name}'; an authored command never shadows a registered verb or alias.");
            }

            for (var index = 0; (index < command.Lines.Length); index++) {
                var verb = command.Lines[index].Verb;

                if (TryGet(command: out _, name: verb)) {
                    continue;
                }

                if (registered(arg: verb) is not { } called) {
                    if (refuseUnknown) {
                        errors.Add(item: $"command '{command.Name}' line {(index + 1)} calls '{verb}', which no registered verb or authored command answers.");
                    }
                } else if (RefusesCall(metadata: called) is { } refusal) {
                    errors.Add(item: $"command '{command.Name}' line {(index + 1)} calls '{verb}', {refusal}.");
                }
            }

            if (FindCycle(path: [command.Name]) is { } cycle) {
                errors.Add(item: $"command '{command.Name}' calls itself: {string.Join(separator: " -> ", values: cycle)}.");
            }
        }
    }

    private List<string>? FindCycle(List<string> path) {
        _ = TryGet(command: out var command, name: path[^1]);

        foreach (var line in command!.Lines) {
            if (!TryGet(command: out var callee, name: line.Verb)) {
                continue;
            }

            if (string.Equals(a: callee!.Name, b: path[0], comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return [.. path, callee.Name];
            }

            if (!path.Contains(value: callee.Name, comparer: StringComparer.OrdinalIgnoreCase) && (FindCycle(path: [.. path, callee.Name]) is { } cycle)) {
                return cycle;
            }
        }

        return null;
    }
    /// <summary>Finds the command a verb token names, ignoring case.</summary>
    /// <param name="name">The verb token.</param>
    /// <param name="command">When this method returns <see langword="true"/>, the command.</param>
    /// <returns><see langword="true"/> when the set authors a command of that name.</returns>
    public bool TryGet(ReadOnlySpan<char> name, out AuthoredCommand? command) => m_byNameAlt.TryGetValue(key: name, value: out command);
}

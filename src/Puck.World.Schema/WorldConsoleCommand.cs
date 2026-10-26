using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Commands;

namespace Puck.World;

/// <summary>One console command a world document authors from existing verbs: <c>commands</c> rows. The registry
/// answers it beside the engine's verbs — in <c>help</c>, from a binding row or wheel sector naming it, typed at any
/// console — and runs it by submitting its lines, the arguments substituted, under the caller's own authority.</summary>
/// <param name="Name">The command's namespaced name (<c>debug.go</c>), unique within the document and never a
/// registered verb's.</param>
/// <param name="Help">The help text the listing shows before the command's usage, or <see langword="null"/>.</param>
/// <param name="Parameters">The parameters, in argument order; a required one never follows an optional one.</param>
/// <param name="Lines">The console lines the command submits, in order. A <c>{name}</c> placeholder takes a
/// parameter's argument and <c>{{</c>/<c>}}</c> stand for literal braces; the first token, the verb, is literal.</param>
public sealed record WorldConsoleCommand(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("help"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Help = null,
    [property: JsonPropertyName("parameters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldConsoleCommandParameter>? Parameters = null,
    [property: JsonPropertyName("lines")] IReadOnlyList<string>? Lines = null
);
/// <summary>One typed parameter of a <see cref="WorldConsoleCommand"/>.</summary>
/// <param name="Name">The parameter's name, which a line's <c>{name}</c> placeholder substitutes.</param>
/// <param name="Kind">What an argument for it must be.</param>
/// <param name="Default">The argument a caller who omits it gets, written as it would be typed; <see langword="null"/>
/// makes the argument required.</param>
/// <param name="Values">The admitted values of a <see cref="WorldConsoleCommandParameterKind.Choice"/> parameter, each one
/// plain token; no other kind carries any.</param>
public sealed record WorldConsoleCommandParameter(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] WorldConsoleCommandParameterKind Kind = WorldConsoleCommandParameterKind.Text,
    [property: JsonPropertyName("default"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Default = null,
    [property: JsonPropertyName("values"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Values = null
);
/// <summary>What an argument for a <see cref="WorldConsoleCommandParameter"/> must be.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldConsoleCommandParameterKind>))]
public enum WorldConsoleCommandParameterKind {
    /// <summary>Any one plain token.</summary>
    Text = 0,
    /// <summary>An invariant-culture decimal number.</summary>
    Number,
    /// <summary>An invariant-culture integer.</summary>
    Integer,
    /// <summary>One of the parameter's own <see cref="WorldConsoleCommandParameter.Values"/>, matched ignoring case and
    /// substituted as declared.</summary>
    Choice,
    /// <summary>A body index, 0 up to the body capacity ceiling.</summary>
    Body,
    /// <summary>The id of one of the document's <c>spawnPoints</c>, matched ignoring case and substituted as
    /// declared.</summary>
    Spawn,
}
/// <summary>Compiles a document's <c>commands</c> rows into the set a <see cref="CommandRegistry"/> answers.</summary>
public static class WorldConsoleCommands {
    private static readonly ConditionalWeakTable<IReadOnlyList<WorldConsoleCommand>, ConditionalWeakTable<IReadOnlyList<WorldSpawnPoint>, AuthoredCommandSet>> Compiled = new();
    private static readonly IReadOnlyList<WorldSpawnPoint> ImplicitSpawns = [WorldSpawnPointDefaults.ImplicitOrigin];

    /// <summary>Returns the authored command set of <paramref name="definition"/>, compiled once for its command
    /// and spawn declarations; a document whose rows do not compile answers the commands that do.</summary>
    /// <param name="definition">The live document.</param>
    /// <returns>The compiled set, empty when the document authors no commands.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static AuthoredCommandSet For(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var rows = definition.Commands;

        if (rows.Count == 0) { return AuthoredCommandSet.Empty; }
        var spawns = (definition.SpawnPointsRaw ?? ImplicitSpawns);
        var cache = Compiled.GetValue(rows, static _ => new());

        return (cache.TryGetValue(key: spawns, value: out var commands) ? commands : CompileMissing(cache: cache, definition: definition, spawns: spawns));
    }

    private static AuthoredCommandSet CompileMissing(WorldDefinition definition, IReadOnlyList<WorldSpawnPoint> spawns,
        ConditionalWeakTable<IReadOnlyList<WorldSpawnPoint>, AuthoredCommandSet> cache) =>
        cache.GetValue(spawns, _ => Compile(definition: definition, errors: []));

    /// <summary>Compiles <paramref name="definition"/>'s <c>commands</c> rows, appending every refusal.</summary>
    /// <param name="definition">The document.</param>
    /// <param name="errors">The list each refusal is appended to, naming the row.</param>
    /// <returns>The commands that compiled; a duplicate name keeps its first row.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static AuthoredCommandSet Compile(WorldDefinition definition, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: errors);

        if (definition.Commands.Count == 0) {
            return AuthoredCommandSet.Empty;
        }

        var spawns = definition.SpawnPoints.Select(selector: static point => point.Id).ToArray();
        var commands = new List<AuthoredCommand>(capacity: definition.Commands.Count);
        var names = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        for (var index = 0; (index < definition.Commands.Count); index++) {
            if (definition.Commands[index] is not { } row) {
                errors.Add(item: $"commands[{index}] is required.");

                continue;
            }

            var before = errors.Count;
            var parameters = new List<AuthoredCommandParameter>();

            foreach (var parameter in (row.Parameters ?? [])) {
                if (parameter is null) {
                    errors.Add(item: $"commands[{index}] ('{row.Name}') carries a null parameter.");

                    continue;
                }

                if (Parameter(errors: errors, label: $"commands[{index}] ('{row.Name}') parameter '{parameter.Name}'", parameter: parameter, spawns: spawns) is { } compiled) {
                    parameters.Add(item: compiled);
                }
            }

            if (errors.Count != before) {
                continue;
            }

            var lineErrors = new List<string>();
            var command = AuthoredCommand.TryCreate(
                errors: lineErrors,
                help: row.Help,
                lines: (row.Lines ?? []),
                name: row.Name,
                parameters: parameters
            );

            errors.AddRange(collection: lineErrors.Select(selector: error => $"commands[{index}]: {error}"));

            if (command is null) {
                continue;
            }

            if (!names.Add(item: command.Name)) {
                errors.Add(item: $"commands[{index}]: command '{command.Name}' is declared more than once (names ignore case).");

                continue;
            }

            commands.Add(item: command);
        }

        return new AuthoredCommandSet(commands: commands);
    }

    private static string? Choose(string value, IReadOnlyList<string> admitted, string noun, out string canonical) {
        foreach (var candidate in admitted) {
            if (string.Equals(a: candidate, b: value, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                canonical = candidate;

                return null;
            }
        }

        canonical = value;

        return $"is no {noun} (admitted: {string.Join(separator: ", ", values: admitted)})";
    }
    private static AuthoredCommandParameter? Parameter(WorldConsoleCommandParameter parameter, string[] spawns, string label, List<string> errors) {
        if (!Enum.IsDefined(value: parameter.Kind)) {
            errors.Add(item: $"{label} declares no known kind.");

            return null;
        }

        if ((parameter.Kind == WorldConsoleCommandParameterKind.Choice) != (parameter.Values is { Count: > 0 })) {
            errors.Add(item: ((parameter.Kind == WorldConsoleCommandParameterKind.Choice)
                ? $"{label} is a Choice and lists no values."
                : $"{label} lists values, which only a Choice parameter takes."));

            return null;
        }

        var values = (parameter.Values ?? []).ToArray();

        foreach (var value in values) {
            if (string.IsNullOrEmpty(value: value) || value.Any(predicate: static character => (char.IsWhiteSpace(c: character) || char.IsControl(c: character) || (character is '"' or '{' or '}')))) {
                errors.Add(item: $"{label} lists '{value}', which is not one plain token.");

                return null;
            }
        }

        AuthoredArgumentCheck? check = parameter.Kind switch {
            WorldConsoleCommandParameterKind.Number => static (string value, out string canonical) => {
                canonical = value;

                return ((CommandArgs.TryParseFloat(text: value, value: out var number) && float.IsFinite(f: number))
                    ? null
                    : "is not a number");
            }
            ,
            WorldConsoleCommandParameterKind.Integer => static (string value, out string canonical) => {
                canonical = value;

                return (CommandArgs.TryParseInt(text: value, value: out _)
                    ? null
                    : "is not an integer");
            }
            ,
            WorldConsoleCommandParameterKind.Body => static (string value, out string canonical) => {
                canonical = value;

                return ((CommandArgs.TryParseInt(text: value, value: out var body) && (body >= 0) && (body < WorldBodiesLimits.CapacityCeiling))
                    ? null
                    : $"is not a body index (0..{(WorldBodiesLimits.CapacityCeiling - 1)})");
            }
            ,
            WorldConsoleCommandParameterKind.Choice => (string value, out string canonical) => Choose(admitted: values, canonical: out canonical, noun: "choice", value: value),
            WorldConsoleCommandParameterKind.Spawn => (string value, out string canonical) => Choose(admitted: spawns, canonical: out canonical, noun: "spawn point", value: value),
            _ => null,
        };

        return new AuthoredCommandParameter(
            Check: check,
            Default: parameter.Default,
            Kind: parameter.Kind.ToString().ToLowerInvariant(),
            Name: parameter.Name
        );
    }
}

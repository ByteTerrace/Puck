using System.Collections.Frozen;
using System.Text.Json;
using Puck.Commands;

namespace Puck.World;

/// <summary>The engine's console verbs as a headless World registers them, read from the generated catalog this
/// assembly embeds (<c>Assets/puck.console.verbs.json</c>, written by <c>puck verbs</c>). It answers for the registry
/// where none exists, so <c>puck compile --validate</c> refuses an authored command that takes an engine verb's name
/// or calls an operator or unbindable verb before any World boots.</summary>
/// <remarks>A verb only a windowed World registers is absent from the catalog, so a check against it never refuses a
/// line for calling a name it does not know; the live registry's check at boot and reload does.</remarks>
public static class WorldConsoleVerbCatalog {
    /// <summary>The catalog's schema id.</summary>
    public const string SchemaId = "puck.console.verbs.v1";
    /// <summary>The embedded catalog's manifest resource name.</summary>
    public const string ResourceName = "Puck.World.Vocabulary.ConsoleVerbs";

    private static readonly Lazy<FrozenDictionary<string, CommandMetadata>> Verbs = new(valueFactory: static () => {
        using var stream = typeof(WorldConsoleVerbCatalog).Assembly.GetManifestResourceStream(name: ResourceName)!;

        return Parse(json: JsonDocument.Parse(utf8Json: stream));
    });

    /// <summary>Gets the number of verbs the catalog holds.</summary>
    public static int Count => Verbs.Value.Count;

    /// <summary>Returns a catalogued verb's facts by name, ignoring case.</summary>
    /// <param name="name">The verb name.</param>
    /// <returns>The verb's facts, or <see langword="null"/> when the catalog does not hold it.</returns>
    public static CommandMetadata? Lookup(string name) => (Verbs.Value.TryGetValue(key: name, value: out var metadata)
        ? metadata
        : null);
    /// <summary>Renders a catalog document: one row per verb, ordinal-ordered by name, carrying the facts an authored
    /// command's check reads.</summary>
    /// <param name="verbs">The registered verbs' facts.</param>
    /// <returns>The <c>puck.console.verbs.v1</c> text, LF line endings and one trailing newline.</returns>
    public static string Render(IEnumerable<CommandMetadata> verbs) {
        ArgumentNullException.ThrowIfNull(argument: verbs);

        var rows = verbs
            .OrderBy(keySelector: static verb => verb.Name, comparer: StringComparer.Ordinal)
            .Select(selector: static verb => $"    {{ \"name\": \"{verb.Name}\", \"bindable\": {((verb.Bindability == CommandBindability.Bindable) ? "true" : "false")}, \"audience\": \"{((verb.Audience == CommandAudience.Operator) ? "operator" : "anyone")}\" }}");

        return $"{{\n  \"schema\": \"{SchemaId}\",\n  \"verbs\": [\n{string.Join(separator: ",\n", values: rows)}\n  ]\n}}\n";
    }
    /// <summary>Reads a catalog document.</summary>
    /// <param name="json">The parsed <c>puck.console.verbs.v1</c> document.</param>
    /// <returns>Each verb's facts keyed by name, ignoring case.</returns>
    /// <exception cref="InvalidDataException">The document is not a <c>puck.console.verbs.v1</c> catalog.</exception>
    public static FrozenDictionary<string, CommandMetadata> Parse(JsonDocument json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        var root = json.RootElement;

        if (!root.TryGetProperty(propertyName: "schema", value: out var schema) || (schema.GetString() != SchemaId) || !root.TryGetProperty(propertyName: "verbs", value: out var verbs)) {
            throw new InvalidDataException(message: $"The console verb catalog is not a {SchemaId} document.");
        }

        return verbs.EnumerateArray().ToFrozenDictionary(
            comparer: StringComparer.OrdinalIgnoreCase,
            elementSelector: static verb => new CommandMetadata(
                Audience: ((verb.GetProperty(propertyName: "audience").GetString() == "operator") ? CommandAudience.Operator : CommandAudience.Anyone),
                Bindability: (verb.GetProperty(propertyName: "bindable").GetBoolean() ? CommandBindability.Bindable : CommandBindability.Unbindable),
                Name: verb.GetProperty(propertyName: "name").GetString()!,
                Routing: CommandRouting.Immediate,
                ValueKind: CommandValueKind.Digital
            ),
            keySelector: static verb => verb.GetProperty(propertyName: "name").GetString()!
        );
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.World;

namespace Puck.State.Rebuild.Corpus;

/// <summary>One counted construct of the corpus inventory.</summary>
/// <param name="Section">The inventory table the construct belongs to.</param>
/// <param name="Construct">The construct's own spelling: a discriminator, a trait, a mode, a channel or a syntax
/// node.</param>
/// <param name="Worlds">How many times the shipped world sources use it.</param>
/// <param name="Fixtures">How many times the transpiler's fixtures use it.</param>
/// <param name="Users">The shipped world sources that use it, as their paths below the worlds directory without the
/// extension, in ordinal order.</param>
public sealed record CorpusInventoryRow(string Section, string Construct, int Worlds, int Fixtures, IReadOnlyList<string> Users);
/// <summary>Counts every construct the author-expression corpus uses, structurally: each source's compiled document
/// is walked by the position a construct occupies, never by text, and its parsed source tree by node type.</summary>
/// <remarks>The universes of the four discriminated unions are read from the serializer the documents are read
/// through, so an arm no source uses still has a row, counted zero. The inventory is generated: what it holds is what
/// the code and the corpus hold, and the committed copy is held to it by
/// <c>CorpusInventoryTests</c>.</remarks>
public static class CorpusInventory {
    /// <summary>The path of the committed inventory, relative to the repository root.</summary>
    public const string CommittedPath = "tests/Puck.State.Rebuild.Corpus/inventory.md";
    /// <summary>The <c>puck baselines</c> artifact name of the inventory.</summary>
    public const string RecordArtifact = "corpus-inventory";
    /// <summary>The command that records the committed inventory from a fresh run.</summary>
    public const string RecordCommand = ("puck baselines " + RecordArtifact);

    private const string WorldPrefix = "src/Puck.World/Assets/worlds/";

    private static readonly string[] SectionOrder = [
        "Effects",
        "Predicates",
        "Transforms",
        "Domains",
        "Traits",
        "Overflow",
        "Draw sources",
        "Draw modes",
        "Rule groups",
        "Operand channels",
        "Document sections",
        "Syntax nodes",
    ];
    private static readonly string[] Traits = ["advance", "cycle", "draw", "dynamics", "evicts", "inverse", "knowledge", "overflow", "phase", "phaseOf", "visibility"];

    private sealed class Tally {
        private readonly Dictionary<(string Section, string Construct), (int Worlds, int Fixtures, SortedSet<string> Users)> m_counts = [];

        public void Declare(string section, string construct) => _ = m_counts.TryAdd(key: (section, construct), value: (0, 0, new SortedSet<string>(comparer: StringComparer.Ordinal)));
        public void Add(string section, string construct, string? world) {
            Declare(construct: construct, section: section);

            var (worlds, fixtures, users) = m_counts[(section, construct)];

            if (world is null) {
                fixtures++;
            } else {
                worlds++;
                _ = users.Add(item: world);
            }

            m_counts[(section, construct)] = (worlds, fixtures, users);
        }
        public IReadOnlyList<CorpusInventoryRow> Rows() => [.. m_counts
            .Select(selector: static entry => new CorpusInventoryRow(
                Construct: entry.Key.Construct,
                Fixtures: entry.Value.Fixtures,
                Section: entry.Key.Section,
                Users: [.. entry.Value.Users],
                Worlds: entry.Value.Worlds
            ))
            .OrderBy(keySelector: static row => Array.IndexOf(array: SectionOrder, value: row.Section))
            .ThenBy(keySelector: static row => row.Construct, comparer: StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Arms(Type union) {
        var info = WorldJsonContext.Default.Options.GetTypeInfo(type: union);

        return ((info.PolymorphismOptions is { } polymorphism)
            ? polymorphism.DerivedTypes.Select(selector: static arm => arm.TypeDiscriminator?.ToString()).OfType<string>()
            : []);
    }
    private static string? Type(JsonNode? node) => (((((node as JsonObject)?["$type"] as JsonValue) is { } value) && value.TryGetValue<string>(value: out var type)) ? type : null);
    private static string? Text(JsonNode? node) => ((((node as JsonValue) is { } value) && value.TryGetValue<string>(value: out var text)) ? text : null);
    private static IEnumerable<JsonObject> Objects(JsonNode? node) => ((node as JsonArray)?.OfType<JsonObject>() ?? []);
    private static void Predicate(Tally tally, JsonNode? node, string? world) {
        if ((node is not JsonObject predicate) || (Type(node: predicate) is not { } type)) {
            return;
        }

        tally.Add(construct: type, section: "Predicates", world: world);

        foreach (var nested in Objects(node: predicate["predicates"])) {
            Predicate(node: nested, tally: tally, world: world);
        }
        Predicate(node: predicate["predicate"], tally: tally, world: world);
    }
    private static void Effects(Tally tally, JsonNode? node, string? world) {
        foreach (var effect in Objects(node: node)) {
            if (Type(node: effect) is not { } type) {
                continue;
            }

            tally.Add(construct: type, section: "Effects", world: world);

            if (Type(node: effect["transform"]) is { } transform) {
                tally.Add(construct: transform, section: "Transforms", world: world);
            }
            Predicate(node: effect["condition"], tally: tally, world: world);
            foreach (var branch in new[] { "then", "else", "effects", "onFailure" }) {
                Effects(node: effect[branch], tally: tally, world: world);
            }
        }
    }
    // Every reserved channel a rule reads is an object carrying its channel's name, wherever in the rule it stands.
    private static void Channels(Tally tally, JsonNode? node, string? world) {
        switch (node) {
            case JsonObject item:
                if (Text(node: item["channel"]) is { } channel) {
                    tally.Add(construct: channel, section: "Operand channels", world: world);
                }
                foreach (var (_, value) in item) {
                    Channels(node: value, tally: tally, world: world);
                }
                break;
            case JsonArray items:
                foreach (var value in items) {
                    Channels(node: value, tally: tally, world: world);
                }
                break;
        }
    }
    private static void Rows(Tally tally, JsonObject document, string? world) {
        if ((document["state"] as JsonObject)?["world"] is not JsonArray rows) {
            return;
        }

        foreach (var row in rows.OfType<JsonObject>()) {
            if (Type(node: row["domain"]) is { } domain) {
                tally.Add(construct: domain, section: "Domains", world: world);
            }

            var cells = Objects(node: row["cells"]).ToArray();

            foreach (var trait in Traits) {
                if (row.ContainsKey(propertyName: trait) || cells.Any(predicate: cell => cell.ContainsKey(propertyName: trait))) {
                    tally.Add(construct: trait, section: "Traits", world: world);
                }
            }
            if (Text(node: row["overflow"]) is { } overflow) {
                tally.Add(construct: overflow, section: "Overflow", world: world);
            }
            if (row["draw"] is JsonObject draw) {
                var generator = (draw["generator"] as JsonObject);

                tally.Add(construct: (Text(node: generator?["source"]) ?? "named"), section: "Draw sources", world: world);
                tally.Add(construct: (Text(node: generator?["mode"]) ?? "WithReplacement"), section: "Draw modes", world: world);
            }
        }
    }
    private static void Syntax(Tally tally, object? node, string? world) {
        switch (node) {
            case SyntaxNode syntax: {
                    var type = syntax.GetType();

                    tally.Add(construct: type.Name, section: "Syntax nodes", world: world);
                    foreach (var property in type.GetProperties()) {
                        if ((property.GetIndexParameters().Length == 0) && (property.Name != nameof(SyntaxNode.Trivia))) {
                            Syntax(node: property.GetValue(obj: syntax), tally: tally, world: world);
                        }
                    }
                    break;
                }
            case string:
                break;
            case System.Collections.IEnumerable items:
                foreach (var item in items) {
                    if (item is SyntaxNode or System.Collections.IEnumerable) {
                        Syntax(node: item, tally: tally, world: world);
                    }
                }
                break;
        }
    }

    /// <summary>Counts every construct over the corpus.</summary>
    /// <param name="corpus">Each source's repository-relative path, its compiled document and its parsed tree.</param>
    /// <returns>One row per construct, in section order and then ordinal construct order.</returns>
    public static IReadOnlyList<CorpusInventoryRow> Count(IEnumerable<(string Path, JsonObject Document, DocumentNode Source)> corpus) {
        ArgumentNullException.ThrowIfNull(argument: corpus);

        var tally = new Tally();

        foreach (var (section, union) in new[] { ("Effects", typeof(ActionEffect)), ("Predicates", typeof(ActionPredicate)), ("Transforms", typeof(StateTransform)), ("Domains", typeof(StateDomain)) }) {
            foreach (var arm in Arms(union: union)) {
                tally.Declare(construct: arm, section: section);
            }
        }
        foreach (var trait in Traits) {
            tally.Declare(construct: trait, section: "Traits");
        }
        foreach (var (path, document, source) in corpus) {
            var world = (path.StartsWith(comparisonType: StringComparison.Ordinal, value: WorldPrefix)
                ? Path.ChangeExtension(path: path[WorldPrefix.Length..], extension: null)
                : null);

            foreach (var rule in Objects(node: document["rules"])) {
                Predicate(node: rule["gate"], tally: tally, world: world);
                Effects(node: rule["effects"], tally: tally, world: world);
                foreach (var option in Objects(node: (rule["decision"] as JsonObject)?["options"])) {
                    Predicate(node: option["gate"], tally: tally, world: world);
                    Effects(node: option["effects"], tally: tally, world: world);
                }
                Effects(node: (rule["decision"] as JsonObject)?["onNoChoice"], tally: tally, world: world);
                Channels(node: rule, tally: tally, world: world);
            }
            foreach (var group in Objects(node: document["ruleGroups"])) {
                tally.Add(construct: (Text(node: group["shape"]) ?? "Fixpoint"), section: "Rule groups", world: world);
                if (group.ContainsKey(propertyName: "undo")) {
                    tally.Add(construct: "undo", section: "Rule groups", world: world);
                }
                Predicate(node: group["trigger"], tally: tally, world: world);
            }
            Rows(document: document, tally: tally, world: world);
            foreach (var generator in Objects(node: document["generators"])) {
                tally.Add(construct: (Text(node: (generator["generator"] as JsonObject)?["source"]) ?? "unknown"), section: "Draw sources", world: world);
            }
            foreach (var member in new[] { "patterns", "sets", "tables" }) {
                foreach (var _ in Objects(node: (document[member] ?? (document["state"] as JsonObject)?[member]))) {
                    tally.Add(construct: member, section: "Document sections", world: world);
                }
            }
            Syntax(node: source, tally: tally, world: world);
        }

        return tally.Rows();
    }
    /// <summary>Renders the inventory as the committed Markdown page.</summary>
    /// <param name="rows">The counted rows.</param>
    /// <returns>The page's text.</returns>
    public static string Render(IReadOnlyList<CorpusInventoryRow> rows) {
        ArgumentNullException.ThrowIfNull(argument: rows);

        var page = new StringBuilder();

        page.Append(value: "# Corpus inventory\n\n");
        page.Append(value: "Generated by `CorpusInventory` from the author-expression corpus; do not hand-edit. Re-record with\n");
        page.Append(value: $"`{RecordCommand}`, and `CorpusInventoryTests` fails when this page disagrees with the corpus.\n\n");
        page.Append(value: "Each row counts one construct's uses: in the shipped world sources under `src/Puck.World/Assets/worlds`,\n");
        page.Append(value: "and in the transpiler's top-level fixtures under `src/Puck.World.Transpiler/Samples`. Every section but the\n");
        page.Append(value: "last walks each source's compiled document by the position a construct occupies; the last counts each parsed\n");
        page.Append(value: "source's syntax nodes by type. A discriminated union's arms are listed whether or not a source uses them.\n");

        foreach (var section in SectionOrder) {
            var sectionRows = rows.Where(predicate: row => (row.Section == section)).ToArray();

            if (sectionRows.Length == 0) {
                continue;
            }

            page.Append(value: $"\n## {section}\n\n");
            page.Append(value: "| Construct | Shipped worlds | Fixtures | Used by |\n");
            page.Append(value: "|---|---:|---:|---|\n");
            foreach (var row in sectionRows) {
                var users = ((row.Users.Count <= 6)
                    ? string.Join(separator: ", ", values: row.Users.Select(selector: static user => $"`{user}`"))
                    : $"{string.Join(separator: ", ", values: row.Users.Take(count: 5).Select(selector: static user => $"`{user}`"))} and {(row.Users.Count - 5).ToString(provider: CultureInfo.InvariantCulture)} more"
                );

                page.Append(value: $"| `{row.Construct}` | {row.Worlds.ToString(provider: CultureInfo.InvariantCulture)} | {row.Fixtures.ToString(provider: CultureInfo.InvariantCulture)} | {users} |\n");
            }
        }

        return page.ToString();
    }
}

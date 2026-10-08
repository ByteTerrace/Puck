using System.Text.Json.Nodes;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;
using Xunit;

namespace Puck.World.Games.Tests;

// THE LAW (members): a list or object a document holds empty stays empty through decompile and compile. Composition
// replaces a list that is present and keeps the layers beneath one that is absent, so a printer that drops `[]` changes
// what the document composes to. The cases are every array and object member the generated world schema declares,
// each held empty inside a document that carries only the parents on the way to it.
public sealed partial class WorldDecompileRoundTripLawTests {
    private const int MaxDepth = 6;
    private const string SchemaFile = "src/Puck.World/Assets/worlds/puck.world.definition.v1.schema.json";

    // One step from the document down to a member: its name, and when the member is a list that the path goes on into,
    // the identity member of the row it goes into.
    private readonly record struct Step(string Name, string? RowKey, bool IntoRow);

    private static JsonNode? ReadSchema(string file, Dictionary<string, JsonNode?> cache) {
        if (!cache.TryGetValue(
            key: file,
            value: out var schema
        )) {
            schema = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: file));
            cache[file] = schema;
        }

        return schema;
    }
    // Follows `$ref` to the schema it names: a file, a fragment, or both.
    private static (JsonNode? Node, string File) Resolve(JsonNode? node, string file, Dictionary<string, JsonNode?> cache) {
        while ((node is JsonObject holder) && (holder["$ref"]?.GetValue<string>() is { } reference)) {
            var hash = reference.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: '#'
            );
            var target = ((hash < 0) ? reference : reference[..hash]);
            var fragment = ((hash < 0) ? string.Empty : reference[(hash + 1)..]);

            if (target.Length > 0) {
                file = Path.GetFullPath(
                    basePath: Path.GetDirectoryName(path: file)!,
                    path: target
                );
            }

            node = ReadSchema(
                cache: cache,
                file: file
            );

            foreach (var part in fragment.Split(
                options: StringSplitOptions.RemoveEmptyEntries,
                separator: '/'
            )) {
                node = node?[part];
            }
        }

        return (node, file);
    }
    private static bool Has(JsonNode? schema, string type) => schema?["type"] switch {
        JsonArray types => types.Any(predicate: item => string.Equals(
            a: item?.GetValue<string>(),
            b: type,
            comparisonType: StringComparison.Ordinal
        )),
        JsonValue single => string.Equals(
            a: single.GetValue<string>(),
            b: type,
            comparisonType: StringComparison.Ordinal
        ),
        _ => false,
    };
    // The identity member a row of this schema is merged by, so the row on the way to a member is the row a composition
    // matches.
    private static string? IdentityKey(JsonNode? row) {
        foreach (var key in ((ReadOnlySpan<string>)["id", "name", "key", "index"])) {
            if (row?["properties"]?[key] is not null) {
                return key;
            }
        }

        return null;
    }
    // The document that holds the member `path` ends at empty, with only the parents on the way to it.
    private static JsonObject Build(List<Step> path, JsonNode? empty) {
        var document = new JsonObject { ["schema"] = "puck.world.definition.v1" };
        var holder = document;

        for (var index = 0; (index < path.Count); ++index) {
            var step = path[index];

            if (index == (path.Count - 1)) {
                holder[step.Name] = empty;

                break;
            }
            if (step.IntoRow) {
                var row = new JsonObject();

                if (step.RowKey is not null) {
                    row[step.RowKey] = "x";
                }

                holder[step.Name] = new JsonArray { row };
                holder = row;
            } else {
                var child = new JsonObject();

                holder[step.Name] = child;
                holder = child;
            }
        }

        return document;
    }
    // Collects, for every array and object member under `schema`, the document that holds it empty.
    private static void Walk(JsonNode? schema, string file, List<Step> path, Dictionary<string, JsonNode?> cache, SortedDictionary<string, (List<Step> Path, JsonNode? Empty)> cases) {
        if ((path.Count >= MaxDepth) || !Has(
            schema: schema,
            type: "object"
        ) || (schema!["properties"] is not JsonObject properties)) {
            return;
        }

        foreach (var (name, member) in properties) {
            var (resolved, memberFile) = Resolve(
                cache: cache,
                file: file,
                node: member
            );
            var isArray = Has(
                schema: resolved,
                type: "array"
            );
            var isObject = (Has(
                schema: resolved,
                type: "object"
            ) && (resolved!["properties"] is JsonObject { Count: > 0 }));

            var pointer = string.Join(
                separator: "/",
                values: path.Select(selector: static step => step.Name).Append(element: name)
            );

            cases[pointer] = (
                [.. path, new Step(IntoRow: false, Name: name, RowKey: null)],
                (isArray ? new JsonArray() : (isObject ? new JsonObject() : null))
            );

            if (!isArray && !isObject) {
                continue;
            }

            if (isObject) {
                Walk(
                    cache: cache,
                    cases: cases,
                    file: memberFile,
                    path: [.. path, new Step(IntoRow: false, Name: name, RowKey: null)],
                    schema: resolved
                );
            } else {
                var (items, itemsFile) = Resolve(
                    cache: cache,
                    file: memberFile,
                    node: resolved!["items"]
                );

                Walk(
                    cache: cache,
                    cases: cases,
                    file: itemsFile,
                    path: [.. path, new Step(Name: name, RowKey: IdentityKey(row: items), IntoRow: true)],
                    schema: items
                );
            }
        }
    }
    // Members the case document cannot hold alone. A rule row the walk builds carries no effect and a decision no period,
    // which the compiler refuses by name (PUCK026, PUCK029), so the members beneath a rule are held to the laws of the
    // rule sugar; and `verdict` is a row a test block generates, which is open item S8's grammar gap.
    private static bool IsHeldElsewhere(string pointer) =>
        (pointer.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "rules/"
        ) || pointer.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "state/world/verdict"
        ));
    // Every member the schema declares, with the document that holds it empty (a list or an object) or null (a scalar).
    private static SortedDictionary<string, (List<Step> Path, JsonNode? Empty)> Members() {
        var cache = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);
        var file = RepositoryPaths.Resolve(relativePath: SchemaFile);
        var cases = new SortedDictionary<string, (List<Step> Path, JsonNode? Empty)>(comparer: StringComparer.Ordinal);

        Walk(
            cache: cache,
            cases: cases,
            file: file,
            path: [],
            schema: ReadSchema(
                cache: cache,
                file: file
            )
        );

        return cases;
    }

    [Fact]
    public void EveryEmptyListAndObjectTheSchemaDeclaresSurvivesDecompileAndCompile() {
        var cases = Members();
        var losses = new List<string>();

        foreach (var (pointer, member) in cases) {
            if (IsHeldElsewhere(pointer: pointer) || (member.Empty is null)) {
                continue;
            }

            var document = Build(
                empty: member.Empty.DeepClone(),
                path: member.Path
            );

            string source;

            try {
                source = WorldDecompiler.Decompile(root: document);
            } catch (WorldDecompileRefusedException refusal) {
                losses.Add(item: $"{pointer}: the decompiler refuses it: {refusal.Message}");

                continue;
            } catch (Exception exception) when ((exception is not OperationCanceledException)) {
                losses.Add(item: $"{pointer}: the decompiler throws {exception.GetType().Name}: {exception.Message.Split(separator: '\n')[0]}");

                continue;
            }

            var compilation = WorldCompiler.Compile(
                cancellationToken: TestContext.Current.CancellationToken,
                source: source
            );

            if (!compilation.Success || (compilation.Json is null)) {
                losses.Add(item: $"{pointer}: the printed source does not compile: {string.Join(separator: " | ", values: compilation.Diagnostics.Take(count: 1).Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message.Split(separator: '\n')[0]}"))}");
            } else if (FirstDifference(
                actual: compilation.Json,
                expected: document,
                path: string.Empty
            ) is { } loss) {
                losses.Add(item: $"{pointer}: {loss}");
            }
        }

        Assert.True(
            condition: (cases.Count > 100),
            userMessage: $"the schema walk found only {cases.Count} members"
        );
        Assert.True(
            condition: (losses.Count == 0),
            userMessage: $"{losses.Count} of {cases.Count} empty members are lost:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: losses)}"
        );
    }
    [Fact]
    public void EveryMemberHeldNullSurvivesDecompileAndCompile() {
        var cases = Members();
        var losses = new List<string>();

        foreach (var (pointer, member) in cases) {
            // A member is held null to clear what a basis brings. The header members are not members of that kind: `schema`
            // names the document and `basis` names where it stands, so a null there says what its absence says.
            if ((pointer is "basis" or "schema") || IsHeldElsewhere(pointer: pointer)) {
                continue;
            }

            var document = Build(
                empty: null,
                path: member.Path
            );
            string source;

            try {
                source = WorldDecompiler.Decompile(root: document);
            } catch (WorldDecompileRefusedException refusal) {
                losses.Add(item: $"{pointer}: the decompiler refuses it: {refusal.Message}");

                continue;
            } catch (Exception exception) when ((exception is not OperationCanceledException)) {
                losses.Add(item: $"{pointer}: the decompiler throws {exception.GetType().Name}: {exception.Message.Split(separator: '\n')[0]}");

                continue;
            }

            var compilation = WorldCompiler.Compile(
                cancellationToken: TestContext.Current.CancellationToken,
                source: source
            );

            if (!compilation.Success || (compilation.Json is null)) {
                losses.Add(item: $"{pointer}: the printed source does not compile: {string.Join(separator: " | ", values: compilation.Diagnostics.Take(count: 1).Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message.Split(separator: '\n')[0]}"))}");
            } else if (FirstDifference(
                actual: compilation.Json,
                expected: document,
                path: string.Empty
            ) is { } loss) {
                losses.Add(item: $"{pointer}: {loss}");
            }
        }

        Assert.True(
            condition: (losses.Count == 0),
            userMessage: $"{losses.Count} members held null are lost:{Environment.NewLine}{string.Join(separator: Environment.NewLine, values: losses)}"
        );
    }
}

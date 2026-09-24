using System.Text.Json.Nodes;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A described member's <c>DocumentKeys</c> are the keys the lowering actually writes, and a document node
/// carries no key no description names: every construct is compiled from an authored probe and its node read
/// back.</summary>
/// <remarks><c>$type</c> is exempt — the polymorphic discriminator is the document axis's, registered by
/// <see cref="ConstructRegistry"/> from the arms' own attributes, never a member an author writes. A construct whose
/// node IS one member's value (a <c>when</c> lowers to the predicate its <c>gate</c> key holds) is proved by that
/// node existing, since the key it names sits on the parent.</remarks>
public class ConstructLoweringLawTests {
    // The document member every construct writes that this law reads a node back from. The compile-time layer
    // reaches no document member at all, so it has no node to read.
    private const string NoDocumentMember = "(nothing)";

    private static JsonNode? Resolve(JsonObject document, string pointer) {
        JsonNode? node = document;

        foreach (var segment in pointer.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '/')) {
            node = (node switch {
                JsonObject obj => obj[propertyName: segment],
                JsonArray array when (int.TryParse(result: out var index, s: segment) && (index >= 0) && (index < array.Count)) => array[index],
                _ => null,
            });

            if (node is null) {
                return null;
            }
        }

        return node;
    }
    private static IReadOnlySet<string> KeysOf(JsonNode? node) => ((node is JsonObject obj)
        ? obj.Select(selector: static pair => pair.Key).ToHashSet(comparer: StringComparer.Ordinal)
        : new HashSet<string>(comparer: StringComparer.Ordinal)
    );
    // Every key the entries of a construct's cell body carry, read from the array its own cell-body member fills.
    private static IReadOnlySet<string> CellEntryKeysOf(WorldConstruct construct, JsonNode? node) {
        var keys = new HashSet<string>(comparer: StringComparer.Ordinal);
        var bodyKeys = construct.Members
            .Where(predicate: static member => (member.Position == WorldMemberPosition.Body))
            .SelectMany(selector: static member => member.DocumentKeys);

        foreach (var bodyKey in bodyKeys) {
            if (Resolve(
                document: ((node as JsonObject) ?? []),
                pointer: bodyKey
            ) is not JsonArray entries) {
                continue;
            }

            foreach (var entry in entries) {
                keys.UnionWith(other: KeysOf(node: entry));
            }
        }

        return keys;
    }
    // Whether the construct's own document node is the value one of its members names, in which case that member's
    // key sits on the node's parent and the node's own keys belong to whatever shape the value takes.
    private static bool IsOneMembersValue(WorldConstruct construct) {
        var segments = construct.DocumentMember.Split(separator: '.');
        var last = segments[^1];

        return construct.Members.Any(predicate: member => (
            (member.DocumentKeys.Count == 1) &&
            string.Equals(
            a: member.DocumentKeys[0],
            b: last,
            comparisonType: StringComparison.Ordinal
        )
        ));
    }
    // Whether any description accounts for `key` on the node at `documentMember`: a construct writing there names
    // it, or a construct of its own occupies that key.
    private static bool IsAccountedFor(WorldConstructTable table, string documentMember, string key) {
        var occupied = $"{documentMember}.{key}";

        return (
            string.Equals(
            a: key,
            b: "$type",
            comparisonType: StringComparison.Ordinal
        ) ||
            table.Constructs.Any(predicate: construct => (
            string.Equals(
            a: construct.DocumentMember,
            b: documentMember,
            comparisonType: StringComparison.Ordinal
        ) &&
            construct.NodeKeys.Contains(
            comparer: StringComparer.Ordinal,
            value: key
        )
        )) ||
            table.Constructs.Any(predicate: construct => (
            string.Equals(
            a: construct.DocumentMember,
            b: occupied,
            comparisonType: StringComparison.Ordinal
        ) ||
            construct.DocumentMember.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (occupied + ".")
        ) ||
            construct.DocumentMember.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: (occupied + "[")
        )
        ))
        );
    }
    private static JsonObject Compile(string source, List<string> failures, string context, string? generatedWorld = null) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            embeddings: ConstructProbes.Lock,
            source: source
        );

        if (compilation.Diagnostics.HasErrors) {
            failures.Add(item: $"{context}: the probe does not compile: {compilation.Diagnostics.FormatReport(source).ReplaceLineEndings(replacementText: " ")}");

            return [];
        }

        if (generatedWorld is null) {
            return compilation.RequireJson();
        }

        var generated = compilation.TestWorlds.FirstOrDefault(predicate: world => string.Equals(
            a: world.Name,
            b: generatedWorld,
            comparisonType: StringComparison.Ordinal
        ));

        if (generated is null) {
            failures.Add(item: $"{context}: the probe generated no world named '{generatedWorld}' (it generated {string.Join(
                separator: ", ",
                values: compilation.TestWorlds.Select(selector: static world => world.Name)
            )})");

            return [];
        }

        return generated.Json;
    }

    public static TheoryData<string> Described() => new(values: WorldConstructs.Table.Keywords);
    [Fact]
    public void EveryDescribedConstructThatReachesADocumentCarriesAProbe() {
        var table = WorldConstructs.Table;
        var missing = table.Constructs
            .Where(predicate: static construct => !string.Equals(
            a: construct.DocumentMember,
            b: NoDocumentMember,
            comparisonType: StringComparison.Ordinal
        ))
            .Select(selector: static construct => construct.Keyword)
            .Where(predicate: static keyword => !ConstructProbes.Sources.ContainsKey(key: keyword))
            .Order(comparer: StringComparer.Ordinal);
        var unknown = ConstructProbes.Sources.Keys
            .Where(predicate: keyword => !table.TryGet(
            construct: out _,
            keyword: keyword
        ))
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: missing.Concat(second: unknown)
            ),
            expected: ""
        );
    }
    [Fact]
    public void AMemberFillingNoKeySaysHowTheLoweringConsumesIt() {
        var inert = WorldConstructs.Table.Constructs
            .SelectMany(selector: static construct => construct.Members.Select(selector: member => (construct.Keyword, Member: member)))
            .Where(predicate: static pair => (
                (pair.Member.DocumentKeys.Count == 0) &&
                string.IsNullOrWhiteSpace(value: pair.Member.Lowering)
            ))
            .Select(selector: static pair => $"{pair.Keyword}.{pair.Member.Name}")
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: inert
            ),
            expected: ""
        );
    }
    [MemberData(nameof(Described))]
    [Theory]
    public void ADescribedMemberFillsTheKeysItNamesAndTheNodeCarriesNoOther(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));

        if (!ConstructProbes.Sources.TryGetValue(
            key: keyword,
            value: out var probes
        )) {
            return;
        }

        var failures = new List<string>();
        var ownKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        var cellKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        var elsewhereKeys = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        var probeIndex = 0;

        foreach (var probe in probes) {
            var context = $"{keyword}[{probeIndex++}]";
            var document = Compile(
                context: context,
                failures: failures,
                generatedWorld: probe.GeneratedWorld,
                source: probe.Source
            );
            var node = Resolve(
                document: document,
                pointer: probe.Pointer
            );

            if (node is null) {
                failures.Add(item: $"{context}: {probe.Pointer} resolves to nothing in the compiled document");

                continue;
            }
            ownKeys.UnionWith(other: KeysOf(node: node));
            cellKeys.UnionWith(other: CellEntryKeysOf(
                construct: construct!,
                node: node
            ));
            foreach (var (member, pointer) in (probe.Elsewhere ?? new Dictionary<string, string>(comparer: StringComparer.Ordinal))) {
                if (!elsewhereKeys.TryGetValue(
                    key: member,
                    value: out var seen
                )) {
                    seen = new HashSet<string>(comparer: StringComparer.Ordinal);
                    elsewhereKeys[member] = seen;
                }
                seen.UnionWith(other: KeysOf(node: Resolve(
                    document: document,
                    pointer: pointer
                )));
            }

            // A construct of the compile-time layer reaches no document member of its own, so its members name no
            // key: the node its probe reads back belongs to whatever the expansion produced, and its keys are
            // accounted for by the constructs that own them.
            if (
                IsOneMembersValue(construct: construct!) ||
                (node is not JsonObject) ||
                string.Equals(
                a: construct!.DocumentMember,
                b: NoDocumentMember,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                continue;
            }

            foreach (var key in KeysOf(node: node)) {
                if (!IsAccountedFor(
                    documentMember: construct!.DocumentMember,
                    key: key,
                    table: table
                )) {
                    failures.Add(item: $"{context}: {probe.Pointer} carries '{key}', which no description of '{construct.DocumentMember}' names");
                }
            }
        }
        if (!IsOneMembersValue(construct: construct!) && !string.Equals(
            a: construct!.DocumentMember,
            b: NoDocumentMember,
            comparisonType: StringComparison.Ordinal
        )) {
            foreach (var member in construct!.Members) {
                var seen = ((member.DocumentNode is { } node)
                    ? (elsewhereKeys.TryGetValue(
                        key: node,
                        value: out var found
                    )
                        ? found
                        : [])
                    : ((member.Position == WorldMemberPosition.Cell)
                        ? cellKeys
                        : ownKeys)
                );

                foreach (var key in member.DocumentKeys) {
                    if (!seen.Contains(item: key)) {
                        failures.Add(item: $"{keyword}: no probe puts '{member.Name}''s key '{key}' on {(member.DocumentNode ?? construct.DocumentMember)}");
                    }
                }
            }
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: failures
            )
        );
    }
}

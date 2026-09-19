using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A refusal about a described construct names its <c>.puck</c> line: the emitter registers every
/// described construct's own document node in the <see cref="SourceMap"/>, and the span it registers is the line
/// the construct was written on.</summary>
/// <remarks>Run over every shipped source, so a construct that stops registering its node fails on the document
/// that authors it. A refusal raised after lowering carries a JSON pointer and nothing else — that is what
/// <c>WorldSemanticValidator</c> and the reference lint hand to <see cref="SourceMap.TryGetSpan"/> — so an
/// unregistered node reports at the nearest ancestor that is registered, which is a different construct's
/// line.</remarks>
public class ConstructSpanLawTests {
    // A document member path this law can walk: dotted segments, a trailing `[]` for each element of an array.
    // An arm selector (`[transaction]`) names a `$type` inside an effects array, and one arm carries more than one
    // surface spelling (`draw`, `deal` and `shuffle` all lower to `transformState`), so selecting elements by arm
    // cannot attribute a line to one construct; `ConstructEffectLineLawTests` pins the effect arms' own lines
    // instead. `(nothing)` is the compile-time layer, which reaches no document member at all.
    private static bool IsWalkable(string documentMember) {
        if (documentMember.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "("
        )) {
            return false;
        }
        for (var index = documentMember.IndexOf(value: '['); (index >= 0); index = documentMember.IndexOf(startIndex: (index + 1), value: '[')) {
            if (documentMember[index + 1] != ']') {
                return false;
            }
        }

        return true;
    }
    // Every JSON pointer the path reaches in `document`, as `/a/b/0`.
    private static IEnumerable<string> Pointers(JsonNode? node, string pointer, IReadOnlyList<string> segments) {
        if (node is null) {
            yield break;
        }
        if (segments.Count == 0) {
            yield return pointer;

            yield break;
        }

        var segment = segments[0];
        var rest = segments.Skip(count: 1).ToArray();
        var each = segment.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: "[]"
        );
        var name = (each
            ? segment[..^2]
            : segment
        );

        if (node is not JsonObject obj) {
            yield break;
        }

        var child = obj[propertyName: name];
        var childPointer = $"{pointer}/{name}";

        if (!each) {
            foreach (var found in Pointers(
                node: child,
                pointer: childPointer,
                segments: rest
            )) {
                yield return found;
            }

            yield break;
        }
        if (child is not JsonArray array) {
            yield break;
        }
        for (var index = 0; (index < array.Count); index++) {
            foreach (var found in Pointers(
                node: array[index],
                pointer: $"{childPointer}/{index}",
                segments: rest
            )) {
                yield return found;
            }
        }
    }
    // The words a line carrying this document member may open with: the keyword of any construct that writes
    // there, or of any construct enclosing it, since an unregistered node resolves at the nearest enclosing one
    // that is registered; the section's own array name, which is how the explicit array form opens; the
    // compile-time layer that may have expanded the statement; and an embedded dialect's own statements.
    private static IReadOnlySet<string> OpeningWords(WorldConstructTable table, string documentMember) {
        var words = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var construct in table.Constructs) {
            if (construct.DocumentMembers.Any(predicate: member => (
                string.Equals(
                a: member,
                b: documentMember,
                comparisonType: StringComparison.Ordinal
            ) ||
                documentMember.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: (member + ".")
            ) ||
                documentMember.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: (member + "[")
            )
            ))) {
                _ = words.Add(item: construct.Keyword);
            }
            if (construct.Shape == WorldConstructShape.EmbeddedLanguage) {
                foreach (var statement in new[] { "CREATE", "DECLARE", "INSERT", "UPDATE", "DELETE" }) {
                    _ = words.Add(item: statement);
                }
            }
        }
        foreach (var segment in documentMember.Split(separator: '.')) {
            _ = words.Add(item: segment.TrimEnd(trimChars: ['[', ']']));
        }
        foreach (var exclusion in table.Excluded) {
            _ = words.Add(item: exclusion.Keyword);
        }

        return words;
    }
    private static string OpeningWordAt(string source, int line) {
        var lines = source.ReplaceLineEndings(replacementText: "\n").Split(separator: '\n');
        var text = (((line >= 1) && (line <= lines.Length))
            ? lines[line - 1].TrimStart()
            : ""
        );
        var end = text.AsSpan().IndexOfAny(values: " \t({[:\"");

        return ((end < 0)
            ? text
            : text[..end]
        );
    }

    // The keywords of the constructs that write `documentMember` itself, which are the only words a line carrying
    // a node registered AT that member may open with. Narrower than <see cref="OpeningWords"/>, which also admits
    // every enclosing construct's keyword because an unregistered node resolves at the nearest ancestor: a probe
    // authored for one construct has no reason to resolve anywhere else.
    private static IReadOnlySet<string> WritersOf(WorldConstructTable table, string documentMember) => table.Constructs
        .Where(predicate: construct => construct.DocumentMembers.Contains(
        comparer: StringComparer.Ordinal,
        value: documentMember
    ))
        .Select(selector: static construct => construct.Keyword)
        .ToHashSet(comparer: StringComparer.Ordinal);

    public static TheoryData<string> Described() => new(values: WorldConstructs.Table.Keywords);
    public static TheoryData<string> ShippedSources() => ShippedWorlds.Sources();

    [MemberData(nameof(Described))]
    [Theory]
    public void EveryConstructRegisteringASpanRegistersItOverItsOwnProbe(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));
        if (!IsWalkable(documentMember: construct!.DocumentMember)) {
            return;
        }

        // Driven from the table rather than from a corpus, so a construct whose registration goes away fails by
        // name instead of going unexercised: the shipped worlds author five of these nowhere at all.
        Assert.True(
            condition: ConstructProbes.Sources.TryGetValue(
                key: keyword,
                value: out var probes
            ),
            userMessage: $"'{keyword}' lowers to '{construct.DocumentMember}' and carries no probe, so nothing would fail if it stopped registering its span"
        );

        var words = WritersOf(
            documentMember: construct.DocumentMember,
            table: table
        );
        var failures = new List<string>();
        var probeIndex = 0;

        foreach (var probe in probes!) {
            var context = $"{keyword}[{probeIndex++}]";
            var sourceMap = new SourceMap();
            var compilation = WorldCompiler.Compile(
                cancellationToken: TestContext.Current.CancellationToken,
                source: probe.Source,
                sourceMap: sourceMap
            );

            if (compilation.Diagnostics.HasErrors) {
                failures.Add(item: $"{context}: the probe does not compile: {compilation.Diagnostics.FormatReport(probe.Source).ReplaceLineEndings(replacementText: " ")}");

                continue;
            }
            if (!sourceMap.TryGetSpan(
                jsonPointer: probe.Pointer,
                span: out var span
            )) {
                failures.Add(item: $"{context}: {probe.Pointer} resolves to no source span");

                continue;
            }

            var word = OpeningWordAt(
                line: span.Line,
                source: probe.Source
            );

            if (!words.Contains(item: word)) {
                failures.Add(item: $"{context}: {probe.Pointer} resolves to line {span.Line}, which opens with '{word}' — expected one of {string.Join(
                    separator: ", ",
                    values: words.Order(comparer: StringComparer.Ordinal)
                )}");
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

    [Fact]
    public void EveryDescribedConstructNamesADocumentMemberThisLawCanWalk() {
        var unwalkable = WorldConstructs.Table.Constructs
            .Where(predicate: static construct => !IsWalkable(documentMember: construct.DocumentMember))
            .Select(selector: static construct => construct.Keyword)
            .Order(comparer: StringComparer.Ordinal);

        // The effect arms and the compile-time layer, and nothing else: an effect is one element of a
        // `$type`-discriminated array this walk cannot attribute to one construct, and a compile-time construct
        // reaches no document member at all. A construct joining this list loses its own line on a refusal unless
        // `ConstructEffectLineLawTests` covers it.
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: unwalkable
            ),
            expected: "countdown, deal, derive, draw, enum, if, onFailure, push, record, remove, schedule, shuffle, test, transaction, transform"
        );
    }
    [MemberData(nameof(ShippedSources))]
    [Theory]
    public void ADescribedConstructRegistersItsOwnLine(string relativePath) {
        var sourcePath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var source = File.ReadAllText(path: sourcePath);
        var sourceMap = new SourceMap();
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourceMap: sourceMap,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        var document = compilation.RequireJson();
        var table = WorldConstructs.Table;
        var failures = new List<string>();

        foreach (var construct in table.Constructs) {
            if (!IsWalkable(documentMember: construct.DocumentMember)) {
                continue;
            }

            var words = OpeningWords(
                documentMember: construct.DocumentMember,
                table: table
            );

            foreach (var pointer in Pointers(
                node: document,
                pointer: "",
                segments: construct.DocumentMember.Split(separator: '.')
            )) {
                if (!sourceMap.TryGetSpan(
                    jsonPointer: pointer,
                    span: out var span
                )) {
                    failures.Add(item: $"{construct.Keyword}: {pointer} resolves to no source span");

                    continue;
                }

                var word = OpeningWordAt(
                    line: span.Line,
                    source: source
                );

                if (!words.Contains(item: word)) {
                    failures.Add(item: $"{construct.Keyword}: {pointer} resolves to line {span.Line}, which opens with '{word}'");
                }
            }
        }

        Assert.Equal(
            actual: string.Join(
                separator: Environment.NewLine,
                values: failures
            ),
            expected: ""
        );
    }
}

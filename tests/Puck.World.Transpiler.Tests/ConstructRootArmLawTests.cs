using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The lowering's and the printer's root dispatch read the table: every construct written at the
/// document's own root names the arm both take, and a document the construct lowers to prints back through
/// it.</summary>
/// <remarks>Enumerated from the table, so a root construct added without an arm fails by its own keyword rather
/// than falling silently through to the generic block or the generic field. Both dispatchers name every arm of
/// <see cref="WorldRootArm"/> and throw on one they have no case for, so this law's per-construct probe is what
/// reaches that throw.</remarks>
public class ConstructRootArmLawTests {
    // The document key a root construct's section occupies, from the member it lowers to.
    private static string RootKey(WorldConstruct construct) {
        var member = construct.DocumentMember;
        var dot = member.IndexOf(value: '.');

        return (dot < 0
            ? member.TrimEnd(trimChars: ['[', ']'])
            : member[..dot]
        );
    }

    public static TheoryData<string> Roots() => new(values: WorldConstructs.Table
        .Inside(enclosing: null)
        .Select(selector: static construct => construct.Keyword)
        .Order(comparer: StringComparer.Ordinal));

    [Fact]
    public void EveryRootConstructNamesTheArmBothDispatchersTake() {
        var table = WorldConstructs.Table;
        var missing = table
            .Inside(enclosing: null)
            .Where(predicate: static construct => (construct.RootArm is null))
            .Select(selector: static construct => construct.Keyword)
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: missing
            ),
            expected: ""
        );

        // A construct written inside another names no arm: the root dispatch never sees it.
        var nested = table.Constructs
            .Where(predicate: static construct => ((construct.Enclosing is not null) && (construct.RootArm is not null)))
            .Select(selector: static construct => $"{construct.Enclosing}.{construct.Keyword}")
            .Order(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: nested
            ),
            expected: ""
        );
    }
    [MemberData(nameof(Roots))]
    [Theory]
    public void EveryRootConstructsArmReachesBothDispatchers(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            enclosing: null,
            keyword: keyword
        ));
        Assert.Equal(
            actual: table.RootArmOf(keyword: keyword),
            expected: construct!.RootArm
        );

        if (construct.RootArm == WorldRootArm.CompileTime) {
            // The compile-time layer reaches no document member, so neither dispatcher has a key to resolve.
            Assert.Equal(
                actual: construct.DocumentMember,
                expected: "(nothing)"
            );

            return;
        }

        // The printer resolves the arm off the document key alone, which is the only thing it has in hand.
        Assert.Equal(
            actual: table.RootArmWriting(documentKey: RootKey(construct: construct)),
            expected: construct.RootArm
        );
    }
    [MemberData(nameof(Roots))]
    [Theory]
    public void ARootConstructsProbeLowersToItsSectionAndPrintsBackAsItsKeyword(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            enclosing: null,
            keyword: keyword
        ));

        if (construct!.RootArm == WorldRootArm.CompileTime) {
            return;
        }
        Assert.True(
            condition: ConstructProbes.Sources.TryGetValue(
                key: keyword,
                value: out var probes
            ),
            userMessage: $"'{keyword}' is a described root construct with no probe, so nothing would fail if its arm stopped being taken"
        );

        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: probes![0].Source
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(probes[0].Source)
        );

        var document = compilation.RequireJson();
        var rootKey = RootKey(construct: construct);

        Assert.True(
            condition: (document[propertyName: rootKey] is not null),
            userMessage: $"'{keyword}' lowers to '{construct.DocumentMember}', and its probe's document carries no '{rootKey}': {string.Join(
                separator: ", ",
                values: document.Select(selector: static entry => entry.Key)
            )}"
        );

        // The printer's own dispatch, over the document the lowering just wrote. A construct the printer never
        // prints back names its fallback instead, and the sugar-guard law owns that case.
        if (!construct.Sugar.Printed) {
            return;
        }

        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(
            actualString: printed,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: keyword
        );
    }
}

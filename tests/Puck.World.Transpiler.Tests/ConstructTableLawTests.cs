using Puck.Transpiler.Ast;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>One description per construct: the table is well formed, and every statement shape the language can
/// parse has a verdict on it — a described construct, a member of one, core grammar the vocabulary never
/// interprets, or a refusal.</summary>
/// <remarks>The verdict table is keyed by syntax-node type and checked complete by reflection, so a statement
/// shape added to <c>Puck.Transpiler.Ast</c> without a decision here fails by the node's own name.</remarks>
public class ConstructTableLawTests {
    private enum Verdict {
        /// <summary>A described construct; <see cref="Node.Keyword"/> names it.</summary>
        Construct,
        /// <summary>A described member of the construct <see cref="Node.Keyword"/> names.</summary>
        Member,
        /// <summary>Core grammar with no keyword of this vocabulary's own.</summary>
        Core,
        /// <summary>A shape this vocabulary refuses; <see cref="Node.Keyword"/> says why.</summary>
        Refused,
    }
    private sealed record Node(Verdict Verdict, string Keyword, string? Member = null);

    private static readonly Dictionary<string, Node> Verdicts = new(comparer: StringComparer.Ordinal) {
        ["AddCellStatementNode"] = new(Keyword: "+=", Verdict: Verdict.Core),
        ["AddonMemoryWatchNode"] = new(Keyword: "watchMemory", Verdict: Verdict.Construct),
        ["AddonRequestNode"] = new(Keyword: "request", Verdict: Verdict.Construct),
        ["BlockNode"] = new(Keyword: "", Verdict: Verdict.Core),
        ["BreakStatementNode"] = new(Keyword: "PUCK037 — a staged rule body carries no loop to break out of", Verdict: Verdict.Refused),
        ["CellSetDeclarationNode"] = new(Keyword: "set", Verdict: Verdict.Construct),
        ["ClaimPairStatementNode"] = new(Keyword: "claim pair", Verdict: Verdict.Construct),
        ["ClaimStatementNode"] = new(Keyword: "claim", Verdict: Verdict.Construct),
        ["CompoundAssignStatementNode"] = new(Keyword: "PUCK039 — this vocabulary's effects carry only setState and addState", Verdict: Verdict.Refused),
        ["DealStatementNode"] = new(Keyword: "deal", Verdict: Verdict.Construct),
        ["DecisionBlockNode"] = new(Keyword: "decision", Verdict: Verdict.Construct),
        ["DerivedStateNode"] = new(Keyword: "derive", Verdict: Verdict.Construct),
        ["DrawStatementNode"] = new(Keyword: "draw", Verdict: Verdict.Construct),
        ["EmbeddedBlockNode"] = new(Keyword: "sql", Verdict: Verdict.Construct),
        ["ErrorStatementNode"] = new(Keyword: "", Verdict: Verdict.Core),
        ["EnumDeclarationNode"] = new(Keyword: "enum", Verdict: Verdict.Construct),
        ["ExportNode"] = new(Keyword: "export", Verdict: Verdict.Core),
        ["ExpressionStatementNode"] = new(Keyword: "", Verdict: Verdict.Core),
        ["FlagStatementNode"] = new(Keyword: "placement", Member: "solid", Verdict: Verdict.Member),
        ["ForStatementNode"] = new(Keyword: "for", Verdict: Verdict.Core),
        ["IfStatementNode"] = new(Keyword: "if", Verdict: Verdict.Construct),
        ["ImportNode"] = new(Keyword: "import", Verdict: Verdict.Core),
        ["InterruptStatementNode"] = new(Keyword: "interrupt", Verdict: Verdict.Construct),
        ["LetNode"] = new(Keyword: "let", Verdict: Verdict.Core),
        ["LocalStatementNode"] = new(Keyword: "local", Verdict: Verdict.Construct),
        ["OnNoChoiceBlockNode"] = new(Keyword: "onNoChoice", Verdict: Verdict.Construct),
        ["OptionBlockNode"] = new(Keyword: "option", Verdict: Verdict.Construct),
        ["PatternDeclarationNode"] = new(Keyword: "pattern", Verdict: Verdict.Construct),
        ["PoolForEachStatementNode"] = new(Keyword: "for each", Verdict: Verdict.Construct),
        ["PropertyNode"] = new(Keyword: "", Verdict: Verdict.Core),
        ["PushStatementNode"] = new(Keyword: "push", Verdict: Verdict.Construct),
        ["RecordDeclarationNode"] = new(Keyword: "record", Verdict: Verdict.Construct),
        ["ReleaseStatementNode"] = new(Keyword: "release", Verdict: Verdict.Construct),
        ["RemoveCellStatementNode"] = new(Keyword: "remove", Verdict: Verdict.Construct),
        ["RepeatStatementNode"] = new(Keyword: "PUCK037 — a straight-line rule body has nothing to lower a loop onto", Verdict: Verdict.Refused),
        ["RuleBlockNode"] = new(Keyword: "rule", Verdict: Verdict.Construct),
        ["RuleScopeNode"] = new(Keyword: "rules", Verdict: Verdict.Construct),
        ["ScheduleStatementNode"] = new(Keyword: "schedule", Verdict: Verdict.Construct),
        ["ScoreStatementNode"] = new(Keyword: "option", Member: "score", Verdict: Verdict.Member),
        ["SetCellStatementNode"] = new(Keyword: "=", Verdict: Verdict.Core),
        ["ShuffleStatementNode"] = new(Keyword: "shuffle", Verdict: Verdict.Construct),
        ["StabilizeGroupNode"] = new(Keyword: "stabilize", Verdict: Verdict.Construct),
        ["StateGridDeclarationNode"] = new(Keyword: "grid", Verdict: Verdict.Construct),
        ["StatePileDeclarationNode"] = new(Keyword: "pile", Verdict: Verdict.Construct),
        ["StateSlotDeclarationNode"] = new(Keyword: "slot", Verdict: Verdict.Construct),
        ["StatePoolDeclarationNode"] = new(Keyword: "pool", Verdict: Verdict.Construct),
        ["StatePairPoolDeclarationNode"] = new(Keyword: "pairPool", Verdict: Verdict.Construct),
        ["StateTableDeclarationNode"] = new(Keyword: "table", Verdict: Verdict.Construct),
        ["TemplateNode"] = new(Keyword: "template", Verdict: Verdict.Core),
        ["TestDeclarationNode"] = new(Keyword: "test", Verdict: Verdict.Construct),
        ["TransactionStatementNode"] = new(Keyword: "transaction", Verdict: Verdict.Construct),
        ["TransformStatementNode"] = new(Keyword: "transform", Verdict: Verdict.Construct),
        ["WhenStatementNode"] = new(Keyword: "when", Verdict: Verdict.Construct),
        ["WorkflowNode"] = new(Keyword: "workflow", Verdict: Verdict.Construct),
        ["WorkflowStepNode"] = new(Keyword: "step", Verdict: Verdict.Construct),
        ["WorldDeclarationNode"] = new(Keyword: "world", Verdict: Verdict.Construct),
        ["WorldLinkNode"] = new(Keyword: "border", Verdict: Verdict.Construct),
    };

    private static IReadOnlyList<string> StatementNodeTypes() => [.. typeof(StatementNode).Assembly
        .GetTypes()
        .Where(predicate: static type => (!type.IsAbstract && typeof(StatementNode).IsAssignableFrom(c: type)))
        .Select(selector: static type => type.Name)
        .Order(comparer: StringComparer.Ordinal)];

    public static TheoryData<string> Described() => new(values: WorldConstructs.Table.Keywords);
    [Fact]
    public void EveryStatementShapeHasAVerdict() {
        var shapes = StatementNodeTypes().ToHashSet(comparer: StringComparer.Ordinal);

        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: shapes.Except(second: Verdicts.Keys).Order(comparer: StringComparer.Ordinal)
            ),
            expected: ""
        );
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: Verdicts.Keys.Except(second: shapes).Order(comparer: StringComparer.Ordinal)
            ),
            expected: ""
        );
    }
    [Fact]
    public void EveryVerdictNamingAConstructFindsItOnTheTable() {
        var table = WorldConstructs.Table;

        foreach (var (node, verdict) in Verdicts.OrderBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)) {
            switch (verdict.Verdict) {
                case Verdict.Construct:
                    Assert.True(
                        condition: table.TryGet(
                        construct: out _,
                        keyword: verdict.Keyword
                    ),
                        userMessage: $"{node} is a '{verdict.Keyword}' statement, which the table does not describe"
                    );
                    break;

                case Verdict.Member:
                    Assert.True(
                        condition: (table.TryGet(
                        construct: out var owner,
                        keyword: verdict.Keyword
                    ) && owner!.TryGetMember(
                        member: out _,
                        name: verdict.Member!
                    )),
                        userMessage: $"{node} is '{verdict.Keyword}'s '{verdict.Member}' member, which the table does not describe"
                    );
                    break;

                case Verdict.Core:
                    Assert.True(
                        condition: ((verdict.Keyword.Length == 0) || table.Excluded.Any(predicate: exclusion => string.Equals(
                        a: exclusion.Keyword,
                        b: verdict.Keyword,
                        comparisonType: StringComparison.Ordinal
                    ))),
                        userMessage: $"{node} is core grammar spelled '{verdict.Keyword}', which the table neither describes nor records a reason for"
                    );
                    break;

                default:
                    Assert.NotEqual(
                        actual: verdict.Keyword,
                        expected: ""
                    );
                    break;
            }
        }
    }
    [Fact]
    public void TheEmbeddedLanguageRowIsWhatTheVocabularyDelegatesOn() {
        Assert.True(condition: WorldConstructs.Table.IsEmbeddedLanguage(identifier: "sql"));
        Assert.False(condition: WorldConstructs.Table.IsEmbeddedLanguage(identifier: "state"));
        Assert.False(condition: WorldConstructs.Table.IsEmbeddedLanguage(identifier: "nothing"));
    }
    [MemberData(nameof(Described))]
    [Theory]
    public void ADescribedConstructIsWellFormed(string keyword) {
        var table = WorldConstructs.Table;

        Assert.True(condition: table.TryGet(
            construct: out var construct,
            keyword: keyword
        ));
        Assert.StartsWith(
            comparisonType: StringComparison.Ordinal,
            actualString: construct!.Grammar,
            expectedStartString: keyword
        );
        Assert.EndsWith(
            comparisonType: StringComparison.Ordinal,
            actualString: construct.Summary,
            expectedEndString: "."
        );
        Assert.NotEqual(
            actual: construct.DocumentMember,
            expected: ""
        );
        Assert.NotEqual(
            actual: construct.Sugar.Fallback,
            expected: ""
        );
        if (construct.Snippet is { } snippet) {
            Assert.StartsWith(
                actualString: snippet,
                comparisonType: StringComparison.Ordinal,
                expectedStartString: keyword
            );
        }
        if (!construct.Sugar.Open) {
            // A closed spelling refuses a node carrying anything outside its own keys, so an empty key set would
            // refuse every node and an empty required set would sugar every one.
            Assert.NotEmpty(collection: construct.NodeKeys);
            Assert.NotEmpty(collection: construct.RequiredKeys);
        }
        if (construct.Enclosing is { } enclosing) {
            Assert.True(
                condition: table.TryGet(
                construct: out _,
                keyword: enclosing
            ),
                userMessage: $"'{keyword}' is written inside '{enclosing}', which the table does not describe"
            );
        }

        var seen = new HashSet<(WorldMemberPosition, string)>();

        foreach (var member in construct.Members) {
            Assert.True(
                condition: seen.Add(item: (member.Position, member.Name)),
                userMessage: $"'{keyword}' describes '{member.Name}' twice in the same position"
            );
            Assert.EndsWith(
                comparisonType: StringComparison.Ordinal,
                actualString: member.Summary,
                expectedEndString: "."
            );
            Assert.Equal(
                actual: (member.Choices.Count > 0),
                expected: (member.Kind is WorldMemberKind.Enumeration or WorldMemberKind.CellKind or WorldMemberKind.Parameters)
            );
            Assert.Equal(
                actual: (member.Parameters.Count > 0),
                expected: (member.Kind == WorldMemberKind.Parameters)
            );
            foreach (var parameter in member.Parameters) {
                Assert.Contains(
                    collection: member.Choices,
                    expected: parameter.Type
                );
                Assert.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    actualString: parameter.Summary,
                    expectedEndString: "."
                );
            }
        }
    }
    [Fact]
    public void OneKeywordNamesTwoConstructsWhenTheyAreWrittenInDifferentPlaces() {
        var shipped = WorldConstructs.Table;

        Assert.True(condition: shipped.TryGet(
            construct: out var root,
            keyword: "world"
        ));

        var nested = (root! with { DocumentMember = "modules[].world", Enclosing = "module" });
        var table = new WorldConstructTable(
            constructs: [.. shipped.Constructs, nested],
            excluded: shipped.Excluded
        );

        // Each resolves where it is written, and the context-free lookup prefers the root spelling.
        Assert.True(condition: table.TryGet(
            construct: out var atRoot,
            enclosing: root.Enclosing,
            keyword: "world"
        ));
        Assert.Equal(
            actual: atRoot!.DocumentMember,
            expected: root.DocumentMember
        );
        Assert.True(condition: table.TryGet(
            construct: out var inModule,
            enclosing: "module",
            keyword: "world"
        ));
        Assert.Equal(
            actual: inModule!.DocumentMember,
            expected: "modules[].world"
        );
        Assert.True(condition: table.TryGet(
            construct: out var withoutContext,
            keyword: "world"
        ));
        Assert.Same(actual: withoutContext, expected: root);

        // The pair is the identity, so the same pair twice is still refused.
        _ = Assert.Throws<ArgumentException>(testCode: () => new WorldConstructTable(constructs: [.. shipped.Constructs, root]));
        Assert.Contains(
            collection: table.Keywords,
            filter: keyword => string.Equals(
            a: keyword,
            b: "world",
            comparisonType: StringComparison.Ordinal
        )
        );
        Assert.Equal(
            actual: table.Keywords.Count,
            expected: shipped.Keywords.Count
        );
    }
    [Fact]
    public void ATypedParameterListPairsEachParameterWithItsKind() {
        var member = new WorldConstructMember(
            Choices: ["Row", "Angle"],
            DocumentKeys: [],
            Kind: WorldMemberKind.Parameters,
            Lowering: "binds the body's parameters at expansion",
            Name: "parameters",
            Parameters: [
                new(
                    Name: "seat",
                    Summary: "The row the body writes seats to.",
                    Type: "Row"
                ),
                new(
                    Name: "aim",
                    Required: false,
                    Summary: "The heading the body starts at.",
                    Type: "Angle"
                ),
            ],
            Position: WorldMemberPosition.Header,
            Summary: "The parameters an invocation supplies."
        );

        // A name paired with its own kind, which the member's admitted words alone cannot say.
        Assert.Equal(
            actual: member.Spelling(),
            expected: "parameters(seat: Row, aim: Angle)"
        );
        Assert.Equal(
            actual: string.Join(
                separator: ", ",
                values: member.Parameters.Select(selector: static parameter => $"{parameter.Name}={parameter.Type}/{parameter.Required}")
            ),
            expected: "seat=Row/True, aim=Angle/False"
        );
        foreach (var parameter in member.Parameters) {
            Assert.Contains(
                collection: member.Choices,
                expected: parameter.Type
            );
        }
    }
}

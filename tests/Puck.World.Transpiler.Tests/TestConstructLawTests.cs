using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The <c>test</c> construct's own grammar: the spelling the table describes, what each block admits, what
/// it refuses by name, and the printer reproducing a test exactly.</summary>
public class TestConstructLawTests {
    private static WorldCompilation Compile(string source) => WorldCompiler.Compile(
        cancellationToken: TestContext.Current.CancellationToken,
        source: source
    );
    internal static string Doc(string body) => $"schema: \"puck.world.definition.v1\"\ndocumentId: \"probe\"\n\n{TestWorldFixtures.Grants}\n\n{TestWorldFixtures.Rows}\n\n{body}\n";
    internal static string Refusal(string source, string code) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );
        var refusal = compilation.Diagnostics.FirstOrDefault(predicate: diagnostic => (diagnostic.Code == code));

        Assert.NotNull(@object: refusal);

        return refusal!.Message;
    }

    public static TheoryData<string, string> InadmissibleLines() => new() {
        { "given {\n        hp += 1\n    }\n    expect {\n        hp == 1\n    }", "carries no '='" },
        { "when {\n        wait 3\n    }\n    expect {\n        hp == 1\n    }", "is not a 'when' step" },
        { "when {\n        console: world.state.cell.set hp $value 2\n    }\n    expect {\n        hp == 1\n    }", "is not a seat" },
        { "when {\n        seat1:\n    }\n    expect {\n        hp == 1\n    }", "carries no command line" },
        { "when {\n        ticks 2s\n    }\n    expect {\n        hp == 1\n    }", "and no unit" },
    };

    [Fact]
    public void TheConstructTableDescribesTheTestKeywordOnce() {
        Assert.True(condition: WorldConstructs.Table.TryGet(
            construct: out var construct,
            keyword: "test"
        ));
        Assert.Equal(
            actual: construct!.RootArm,
            expected: WorldRootArm.CompileTime
        );
        Assert.Equal(
            actual: string.Join(
                separator: " ",
                values: construct.Members.Select(selector: static member => member.Name)
            ),
            expected: "name given when expect"
        );
    }
    [Fact]
    public void AModuleSubjectIsRefusedByName() => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestShapeInadmissible,
            source: Doc(body: """
                test "a module's own" with arcade(origin: [0, 0, 0]) {
                    when {
                        ticks 1
                    }
                    expect {
                        hp == 1
                    }
                }
                """)
        ),
        expectedSubstring: "a test of a module arrives with modules"
    );
    [Fact]
    public void ATestWithNoExpectationIsRefusedByName() => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestShapeInadmissible,
            source: Doc(body: "test \"nothing claimed\" {\n    when {\n        ticks 1\n    }\n}")
        ),
        expectedSubstring: "states no expectation"
    );
    [Fact]
    public void BlocksOutOfOrderAreRefusedByName() => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestShapeInadmissible,
            source: Doc(body: "test \"backwards\" {\n    expect {\n        hp == 1\n    }\n    when {\n        ticks 1\n    }\n}")
        ),
        expectedSubstring: "twice or out of order"
    );
    [MemberData(nameof(InadmissibleLines))]
    [Theory]
    public void AnInadmissibleLineInsideATestIsRefusedByName(string body, string expected) => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            source: Doc(body: $"test \"a probe\" {{\n    {body}\n}}")
        ),
        expectedSubstring: expected
    );
    [Fact]
    public void FormattingASourceCarryingATestIsAFixedPointAndKeepsItsComments() {
        var formatted = PuckFormat.Format(source: TestWorldFixtures.Commented);

        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "// what the test is for"
        );
        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "// the boot value"
        );
    }
    [Fact]
    public void ARewriteThatChangesNothingKeepsATestExactly() {
        var parsed = Compile(source: TestWorldFixtures.Commented).Document;

        Assert.NotNull(@object: parsed);
        Assert.Equal(
            actual: PuckPrinter.Print(document: new Identity().Rewrite(document: parsed!)),
            expected: PuckPrinter.Print(document: parsed!)
        );
    }

    private sealed class Identity : PuckSyntaxRewriter {
    }
}

/// <summary>The one authored world the <c>test</c> construct's laws read.</summary>
internal static class TestWorldFixtures {
    public const string Grants = """
        grants [
            {
                capability: "Edit"
                principal: "seat1"
                subject: "all"
            }
            {
                capability: "Mutate"
                principal: "seat1"
                subject: "section:state"
            }
        ]
        """;
    public const string Rows = """
        state {
            world {
                slot hp : Int = 1
                slot armour : Int = 0
            }
        }
        """;

    public static readonly string Commented = TestConstructLawTests.Doc(body: """
        // what the test is for
        test "armour arrives" {
            given {
                // the boot value
                hp = 4
            }
            when {
                ticks 2
                seat1: world.state.cell.set armour $value 2
            }
            expect {
                armour == 2
            }
        }
        """);
}

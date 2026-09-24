using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Validation;
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

    public static TheoryData<string, string, string> InadmissibleSubjects() => new() {
        { "nowhere(plating: 1)", PuckDiagnosticCodes.InvalidValue, "Unknown module or template 'nowhere'" },
        { "armoury()", PuckDiagnosticCodes.InvalidValue, "Module 'armoury' requires argument 'plating'" },
        { "armoury(plating: 1, extra: 2)", PuckDiagnosticCodes.InvalidValue, "received an unknown or duplicate argument" },
        { "tower(corner: 3)", PuckDiagnosticCodes.InvalidValue, "must be a Point" },
        { "stamp(plating: 1)", PuckDiagnosticCodes.TestShapeInadmissible, "which is a template rather than a module" },
    };
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
            expected: "name subject given when expect world block"
        );
    }
    [Fact]
    public void AModuleSubjectParsesAsTheCallItIsWritten() {
        var declaration = Assert.Single(collection: Compile(source: TestWorldFixtures.ModuleSource).Document!.Statements.OfType<TestDeclarationNode>());

        Assert.Equal(
            actual: declaration.Subject!.Name,
            expected: "armoury"
        );
        Assert.Equal(
            actual: Assert.Single(collection: declaration.Subject.Arguments).Name,
            expected: "plating"
        );
    }
    [Fact]
    public void FormattingASourceCarryingAModuleSubjectKeepsTheSubjectAndIsAFixedPoint() {
        var formatted = PuckFormat.Format(source: TestWorldFixtures.ModuleSource);

        Assert.Contains(
            actualString: formatted,
            expectedSubstring: "test \"the plating arrives\" with armoury(plating: 3) {"
        );
        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );
    }
    [Fact]
    public void ARewriteThatChangesNothingKeepsAModuleSubjectExactly() {
        var parsed = Compile(source: TestWorldFixtures.ModuleSource).Document;

        Assert.NotNull(@object: parsed);
        Assert.Equal(
            actual: PuckPrinter.Print(document: new Identity().Rewrite(document: parsed!)),
            expected: PuckPrinter.Print(document: parsed!)
        );
    }
    // A test's subject instantiates the module it names, so the linter does not report that module as unused.
    [Fact]
    public void TheLintSeesAModuleSubjectAsAnInstantiation() {
        var diagnostics = new DiagnosticBag();

        PuckLinter.Lint(
            diagnostics: diagnostics,
            document: PuckParser.ParseDocument(
            source: TestWorldFixtures.ModuleSource,
            vocabulary: WorldDocumentVocabulary.Instance
        )
        );
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.LintUnusedLet)
        );
    }
    [MemberData(nameof(InadmissibleSubjects))]
    [Theory]
    public void ASubjectTheExpansionCannotStandUpIsRefusedByName(string subject, string code, string expected) => Assert.Contains(
        actualString: Refusal(
            code: code,
            source: $"{TestWorldFixtures.ModuleAndTemplate}\n\ntest \"a probe\" with {subject} {{\n    expect {{\n        armour == 1\n    }}\n}}\n"
        ),
        expectedSubstring: expected
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
    /// <summary>Returns the world row named <paramref name="name"/> of a generated test world.</summary>
    /// <param name="world">A generated test world.</param>
    /// <param name="name">The row's name.</param>
    /// <returns>The one row of that name.</returns>
    public static JsonObject Row(JsonObject world, string name) => world["state"]!["world"]!.AsArray()
        .OfType<JsonObject>()
        .Single(predicate: candidate => (candidate["name"]!.GetValue<string>() == name));

    public const string Grants = """
        grants [
            {
                capability: Edit
                principal: "seat1"
                subject: "all"
            }
            {
                capability: Mutate
                principal: "seat1"
                subject: "section:state"
            }
        ]
        """;
    public const string Rows = """
        state {
            world {
                slot hp = 1
                slot armour = 0
            }
        }
        """;
    /// <summary>A module whose one row is its one parameter, and a test whose subject is that module. The module
    /// carries a test of its own, so a source that uses it runs that test too.</summary>
    public const string ModuleSource = """
        module armoury(plating) {
            state {
                world {
                    slot armour = plating
                }
            }

            test "a seat's write reaches the plating" {
                when {
                    seat1: world.state.cell.set armour $value 7
                    ticks 1
                }
                expect {
                    armour == 7
                }
            }
        }

        test "the plating arrives" with armoury(plating: 3) {
            expect {
                armour == 3
            }
        }

        """;
    /// <summary>The same module, written to be interpolated into a document that declares its own schema.</summary>
    public const string InlineArmoury = """
        module armoury(plating) {
            state {
                world {
                    slot armour = plating
                }
            }

            test "a seat's write reaches the plating" {
                when {
                    seat1: world.state.cell.set armour $value 7
                    ticks 1
                }
                expect {
                    armour == 7
                }
            }
        }
        """;
    /// <summary>One module, one template of the same shape, and one module with a typed parameter — the three
    /// subjects a refusal law needs to tell apart.</summary>
    public const string ModuleAndTemplate = """
        schema: "puck.world.definition.v1"
        documentId: "subjects"

        module armoury(plating) {
            state {
                world {
                    slot armour = plating
                }
            }
        }

        module tower(corner: Point) {
            spawn arrival {
                at: corner
            }
        }

        template stamp(plating) {
            state {
                world {
                    slot armour = plating
                }
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

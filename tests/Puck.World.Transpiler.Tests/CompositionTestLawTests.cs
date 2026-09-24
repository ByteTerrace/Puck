using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Rewriting;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A subjectless <c>test</c> at the root of a source that emits several worlds: which document each line
/// lands in, the schedule that arms the rest, the tick a far world's verdict is decided at, and what the spelling
/// refuses by name.</summary>
public class CompositionTestLawTests {
    private const string Composed = $$"""
        {{Module}}

        test "a composed claim" {
          given {
            beta {
              armour = 5
            }
          }
          when {
            alpha {
              seat1: world.state.cell.set armour $value 2
            }
            ticks 3
          }
          expect {
            alpha {
              armour == 2
            }
            beta {
              armour == 5
            }
          }
        }

        """;
    private const string Module = """
        module plot(seed) {
          grants [
            {
              capability: Mutate
              principal: "seat1"
              subject: "section:state"
            }
            {
              capability: Edit
              principal: "seat1"
              subject: "all"
            }
          ]

          state {
            world {
              slot hp = seed
              slot armour = 0
            }
          }
        }

        entry world alpha = plot(seed: 1)
        world beta = plot(seed: 2)
        """;

    private static string Canonical(JsonNode node) => Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node));
    private static WorldCompilation Compile(string source) => WorldCompiler.Compile(
        allowMultiple: true,
        cancellationToken: TestContext.Current.CancellationToken,
        source: source
    );
    private static WorldCompilation Green(string source) {
        var compilation = Compile(source: source);

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation;
    }
    private static WorldTestWorld Lowered(string source) => Assert.Single(collection: Green(source: source).TestWorlds);
    private static string Refusal(string source, string code) {
        var compilation = Compile(source: source);
        var refusal = compilation.Diagnostics.FirstOrDefault(predicate: diagnostic => (diagnostic.Code == code));

        Assert.NotNull(@object: refusal);

        return refusal!.Message;
    }
    private static ulong VerdictTick(JsonObject world, string row) => world["rules"]!.AsArray()
        .OfType<JsonObject>()
        .Single(predicate: candidate => (candidate["name"]!.GetValue<string>() == row))["gate"]!["value"]!.GetValue<ulong>();
    // A source that emits several worlds and writes no test at all: what the byte-identity law compares against.
    private static string Untested(string source) {
        var index = source.IndexOf(
            comparisonType: StringComparison.Ordinal,
            value: "\ntest \""
        );

        return ((index < 0)
            ? source
            : source[..(index + 1)]
        );
    }

    public static TheoryData<string, string> InadmissibleLines() => new() {
        { "expect {\n    armour == 2\n  }", "names no world" },
        { "expect {\n    gamma {\n      armour == 2\n    }\n  }", "which this source declares no world by" },
        { "when {\n    beta {\n      seat1: world.state.cell.set armour $value 2\n    }\n  }\n  expect {\n    alpha {\n      armour == 0\n    }\n  }", "and its grammar carries no world token" },
        { "when {\n    alpha {\n      ticks 2\n    }\n  }\n  expect {\n    alpha {\n      armour == 0\n    }\n  }", "one tick grid carries the whole composed run" },
        { "given {\n    alpha {\n      beta {\n        armour = 1\n      }\n    }\n  }\n  expect {\n    alpha {\n      armour == 0\n    }\n  }", "opens a world block inside" },
    };
    // One generated document per world, the entry world carrying the schedule that arms the rest and naming each
    // sibling by the document the run writes beside it.
    [Fact]
    public void ATestAtACompositionRootGeneratesOneDocumentPerWorld() {
        var test = Lowered(source: Composed);

        Assert.Equal(
            actual: test.Name,
            expected: "world~a-composed-claim"
        );

        var sibling = Assert.Single(collection: test.Siblings);

        Assert.Equal(
            actual: sibling.Name,
            expected: "world~a-composed-claim~beta"
        );
        Assert.Equal(
            actual: test.Json["documentId"]!.GetValue<string>(),
            expected: "alpha"
        );
        Assert.Equal(
            actual: sibling.Json["documentId"]!.GetValue<string>(),
            expected: "beta"
        );

        var instance = Assert.Single(collection: test.Json["schedule"]!["instances"]!.AsArray());

        Assert.Equal(
            actual: instance!["name"]!.GetValue<string>(),
            expected: "beta"
        );
        Assert.Equal(
            actual: instance["document"]!.GetValue<string>(),
            expected: "world~a-composed-claim~beta"
        );
        // One grid arms the run, so the far world carries no schedule of its own.
        Assert.Null(@object: sibling.Json["schedule"]);
    }
    // The composed run boots the entry world wherever it is declared, as a boot of the source does: an entry declared
    // second carries the schedule, and the world declared first is armed beside it.
    [Fact]
    public void TheComposedRunBootsTheEntryWorldNotTheFirstDeclared() {
        var source = (Module
            .Replace(
                newValue: "world alpha = plot(seed: 1)",
                oldValue: "entry world alpha = plot(seed: 1)"
            )
            .Replace(
                newValue: "entry world beta = plot(seed: 2)",
                oldValue: "world beta = plot(seed: 2)"
            ) + "\n\ntest \"the entry boots\" {\n  when {\n    beta {\n      seat1: world.state.cell.set armour $value 2\n    }\n    ticks 3\n  }\n  expect {\n    beta {\n      armour == 2\n    }\n    alpha {\n      armour == 0\n    }\n  }\n}\n");

        Assert.Contains(
            actualString: source,
            expectedSubstring: "entry world beta"
        );

        var test = Lowered(source: source);

        Assert.Equal(
            actual: test.Json["documentId"]!.GetValue<string>(),
            expected: "beta"
        );
        Assert.Equal(
            actual: Assert.Single(collection: test.Siblings).Json["documentId"]!.GetValue<string>(),
            expected: "alpha"
        );
        Assert.Equal(
            actual: Assert.Single(collection: test.Json["schedule"]!["instances"]!.AsArray())!["name"]!.GetValue<string>(),
            expected: "alpha"
        );
    }
    // A composed run needs a world to boot, so a composition that declares no entry refuses its root test by name.
    [Fact]
    public void AComposedTestInACompositionWithoutAnEntryIsRefusedByName() {
        Assert.Contains(
            actualString: Refusal(
                code: "PUCK104",
                source: Composed.Replace(
                    newValue: "world alpha = plot(seed: 1)",
                    oldValue: "entry world alpha = plot(seed: 1)"
                )
            ),
            expectedSubstring: "declares no entry world to boot them from"
        );
    }
    // A `given` line writes the boot cell of the world its block named, and nothing in the other world's document.
    [Fact]
    public void AnAddressedGivenLineWritesTheWorldItNamed() {
        var test = Lowered(source: Composed);

        Assert.Equal(
            actual: TestWorldFixtures.Row(
                name: "armour",
                world: Assert.Single(collection: test.Siblings).Json
            )["value"]!.GetValue<long>(),
            expected: 5L
        );
        Assert.Equal(
            actual: TestWorldFixtures.Row(
                name: "armour",
                world: test.Json
            )["value"]!.GetValue<long>(),
            expected: 0L
        );
    }
    // A `when` step reaches the world its block named: a step in the booted world carries no world member, and a
    // step in a far world carries that world's name, which is what the verb's own trailing token is built from.
    [Fact]
    public void AnAddressedWhenStepCarriesTheWorldItNamed() {
        var rows = Lowered(source: Composed).Json["schedule"]!["rows"]!.AsArray();
        var row = Assert.Single(collection: rows)!.AsObject();

        Assert.Equal(
            actual: row["command"]!.GetValue<string>(),
            expected: "world.state.cell.set armour $value 2"
        );
        Assert.Null(@object: row["world"]);

        var far = Lowered(source: $"{Module}\n\ntest \"a far step\" {{\n  when {{\n    beta {{\n      seat1: body.stop 1\n    }}\n  }}\n  expect {{\n    beta {{\n      armour == 0\n    }}\n  }}\n}}\n");

        Assert.Equal(
            actual: far.Json["schedule"]!["rows"]!.AsArray()[0]!["world"]!.GetValue<string>(),
            expected: "beta"
        );
    }
    // An expectation becomes a verdict row and a rule in the document of the world it addressed, and nowhere else.
    [Fact]
    public void AnAddressedExpectationBecomesARuleInTheWorldItNamed() {
        var test = Lowered(source: Composed);
        var sibling = Assert.Single(collection: test.Siblings).Json;

        Assert.NotNull(@object: TestWorldFixtures.Row(
            name: "expect$1",
            world: test.Json
        )["verdict"]);
        Assert.NotNull(@object: TestWorldFixtures.Row(
            name: "expect$2",
            world: sibling
        )["verdict"]);
        Assert.DoesNotContain(
            collection: test.Json["state"]!["world"]!.AsArray().OfType<JsonObject>(),
            filter: static row => (row["name"]!.GetValue<string>() == "expect$2")
        );
        Assert.DoesNotContain(
            collection: sibling["state"]!["world"]!.AsArray().OfType<JsonObject>(),
            filter: static row => (row["name"]!.GetValue<string>() == "expect$1")
        );
    }
    // Every world is exported at the one host step the booted world's export tick falls on, and a world armed at
    // boot counts from its own zero — so a far world's verdict rule fires one tick earlier. The lowering derives
    // that, which is what keeps the arithmetic out of the source.
    [Fact]
    public void AFarWorldsVerdictFiresOneTickBeforeTheBootedWorldsExportTick() {
        var test = Lowered(source: Composed);
        var schedule = test.Json["schedule"]!.AsObject();
        var exportTick = (schedule["rows"]!.AsArray()[0]!["tick"]!.GetValue<ulong>() + schedule["settleTicks"]!.GetValue<ulong>());

        Assert.Equal(
            actual: VerdictTick(
                row: "expect$1",
                world: test.Json
            ),
            expected: exportTick
        );
        Assert.Equal(
            actual: VerdictTick(
                row: "expect$2",
                world: Assert.Single(collection: test.Siblings).Json
            ),
            expected: (exportTick - 1UL)
        );
    }
    // A composed test with no scheduled step still has to reach a tick the far world publishes, so the grid runs to
    // at least two.
    [Fact]
    public void AComposedTestWithNoStepStillReachesATickTheFarWorldPublishes() {
        var test = Lowered(source: $"{Module}\n\ntest \"a quiet claim\" {{\n  expect {{\n    beta {{\n      armour == 0\n    }}\n  }}\n}}\n");

        Assert.Equal(
            actual: VerdictTick(
                row: "expect$1",
                world: Assert.Single(collection: test.Siblings).Json
            ),
            expected: 1UL
        );
    }
    [MemberData(nameof(InadmissibleLines))]
    [Theory]
    public void AnInadmissibleLineInsideAComposedTestIsRefusedByName(string body, string expected) => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            source: $"{Module}\n\ntest \"a probe\" {{\n  {body}\n}}\n"
        ),
        expectedSubstring: expected
    );
    // A world block only ever addresses a world of a composition; a test about one world has no such name to reach.
    [Fact]
    public void AWorldBlockInASingleWorldSourceIsRefusedByName() => Assert.Contains(
        actualString: Refusal(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            source: TestConstructLawTests.Doc(body: "test \"a probe\" {\n    expect {\n        alpha {\n            hp == 1\n        }\n    }\n}")
        ),
        expectedSubstring: "this test is about a single world"
    );
    // The documents a composition publishes are the same bytes whether or not its root writes a test.
    [Fact]
    public void ACompositionPublishesTheSameDocumentsWithItsTestDeleted() {
        var tested = Green(source: Composed);
        var untested = Green(source: Untested(source: Composed));

        Assert.NotEmpty(collection: tested.TestWorlds);
        Assert.Empty(collection: untested.TestWorlds);
        Assert.Equal(
            actual: tested.Worlds.Select(selector: static world => (world.Name, Canonical(node: world.Json))),
            expected: untested.Worlds.Select(selector: static world => (world.Name, Canonical(node: world.Json)))
        );
    }
    [Fact]
    public void FormattingAComposedTestIsAFixedPointAndKeepsItsWorldBlocks() {
        var formatted = PuckFormat.Format(source: Composed);

        Assert.Equal(
            actual: formatted,
            expected: Composed
        );
        Assert.Equal(
            actual: PuckFormat.Format(source: formatted),
            expected: formatted
        );
    }
    [Fact]
    public void ARewriteThatChangesNothingKeepsAComposedTestExactly() {
        var parsed = Green(source: Composed).Document;

        Assert.NotNull(@object: parsed);
        Assert.Equal(
            actual: PuckPrinter.Print(document: new Identity().Rewrite(document: parsed!)),
            expected: PuckPrinter.Print(document: parsed!)
        );
    }

    private sealed class Identity : PuckSyntaxRewriter {
    }
}

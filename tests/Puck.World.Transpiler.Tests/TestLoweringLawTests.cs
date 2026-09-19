using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>What a <c>test</c> block lowers to, and the one thing the enclosing world must never carry — a trace of
/// its own tests.</summary>
public class TestLoweringLawTests {
    private static string Canonical(JsonNode node) =>
        Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node));
    private static WorldCompilation Green(string source, string? sourcePath = null) {
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation;
    }
    private static JsonObject OneWorld(string source) => Assert.Single(collection: Green(source: source).TestWorlds).Json;
    // The source with every `test` block cut out, brace by brace — what an author would be left with if they
    // deleted the tests by hand.
    private static string StripTests(string source) {
        var builder = new StringBuilder();
        var index = 0;

        while (index < source.Length) {
            var found = source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                startIndex: index,
                value: "\ntest \""
            );

            if (found < 0) {
                _ = builder.Append(value: source[index..]);

                break;
            }

            _ = builder.Append(value: source[index..(found + 1)]);

            var depth = 0;
            var cursor = source.IndexOf(
                startIndex: found,
                value: '{'
            );

            while (cursor < source.Length) {
                if (source[cursor] == '{') {
                    depth++;
                } else if (source[cursor] == '}') {
                    depth--;

                    if (depth == 0) {
                        break;
                    }
                }
                cursor++;
            }

            index = Math.Min(
                val1: source.Length,
                val2: (cursor + 2)
            );
        }

        return builder.ToString();
    }

    /// <summary>Returns every shipped world source that authors a test, so the byte-identity law names the source
    /// it read.</summary>
    /// <returns>The tested sources as xUnit theory data.</returns>
    public static TheoryData<string> TestedSources() => new(values: ShippedWorlds.SourcePaths().Where(predicate: static relativePath => File.ReadAllText(path: Path.Combine(
        path1: ShippedWorlds.FindDirectory(),
        path2: relativePath
    )).Contains(
        comparisonType: StringComparison.Ordinal,
        value: "\ntest \""
    )));

    public static TheoryData<string, string> InadmissibleLines() => new() {
        { "given {\n        hp = notALiteral\n    }\n    expect {\n        hp == 1\n    }", "from something other than a literal" },
        { "given {\n        missingRow = 1\n    }\n    expect {\n        hp == 1\n    }", "which this world's state section does not declare" },
        { "when {\n        seat1: world.save out.json\n    }\n    expect {\n        hp == 1\n    }", "is not a scheduled step verb" },
    };

    [Fact]
    public void AWorldWithTestsCompilesToTheDocumentItWouldWithoutThem() {
        var tested = Green(source: TestConstructLawTests.Doc(body: """
            test "the first" {
                given {
                    hp = 4
                }
                when {
                    seat1: world.state.cell.set armour $value 2
                    ticks 3
                }
                expect {
                    armour == 2
                }
            }

            test "the second" {
                when {
                    ticks 1
                }
                expect {
                    hp == 1
                }
            }
            """));

        Assert.Equal(
            actual: Canonical(node: tested.RequireJson()),
            expected: Canonical(node: Green(source: TestConstructLawTests.Doc(body: "// no tests here")).RequireJson())
        );
        Assert.Equal(
            actual: tested.TestWorlds.Count,
            expected: 2
        );
    }
    [MemberData(nameof(TestedSources))]
    [Theory]
    public void AShippedSourceCompilesToTheDocumentItWouldWithItsTestsDeleted(string relativePath) {
        var sourcePath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var source = File.ReadAllText(path: sourcePath);
        var stripped = StripTests(source: source);

        Assert.NotEqual(
            actual: stripped,
            expected: source
        );

        var tested = Green(
            source: source,
            sourcePath: sourcePath
        );

        Assert.NotEmpty(collection: tested.TestWorlds);
        Assert.Equal(
            actual: Canonical(node: tested.RequireJson()),
            expected: Canonical(node: Green(
                source: stripped,
                sourcePath: sourcePath
            ).RequireJson())
        );
    }
    private const string KindsDoc = """
        schema: "puck.world.definition.v1"
        documentId: "kinds"

        state {
          world {
            slot speed: Fixed = 1.5
            slot open: Bool = true
            slot label: Text = "a"
            slot hp: Int = 3
          }
        }

        test "kinds" {
          given {
            speed = 2.25
            open = false
            label = "b"
          }
          expect {
            speed > 2.0 and hp == 3
          }
        }

        """;

    private static JsonObject Row(JsonObject world, string name) => world["state"]!["world"]!.AsArray()
        .OfType<JsonObject>()
        .Single(predicate: candidate => (candidate["name"]!.GetValue<string>() == name));

    // A given value is spelled the way an authored cell of the row's kind is: a Fixed row's as the decimal string
    // the emitter writes, a Bool row's as a JSON boolean, a Text row's as a string.
    [Fact]
    public void AGivenLineWritesTheValueAnAuthoredCellOfThatKindWould() {
        var world = OneWorld(source: KindsDoc);
        var authored = Green(source: KindsDoc).RequireJson();

        Assert.Equal(
            actual: Row(
                name: "speed",
                world: world
            )["value"]!.GetValueKind(),
            expected: Row(
                name: "speed",
                world: authored
            )["value"]!.GetValueKind()
        );
        Assert.Equal(
            actual: Row(
                name: "speed",
                world: world
            )["value"]!.GetValue<string>(),
            expected: "2.25"
        );
        Assert.False(condition: Row(
            name: "open",
            world: world
        )["value"]!.GetValue<bool>());
        Assert.Equal(
            actual: Row(
                name: "label",
                world: world
            )["value"]!.GetValue<string>(),
            expected: "b"
        );
    }
    // The verdict row is an Int row, so the rule folds what its gate read of an Int row and nothing of any other:
    // a fold out of a Fixed row is a write the validator refuses, and the world would never boot.
    [Fact]
    public void AnExpectationFoldsOnlyWhatItReadOfAnIntRow() {
        var world = OneWorld(source: KindsDoc);
        var keys = Row(
            name: "kinds-1",
            world: world
        )["cells"]!.AsArray()
            .Select(selector: static cell => cell!["key"]!.GetValue<string>())
            .ToArray();

        Assert.Equal(
            actual: keys,
            expected: ["status", "hp"]
        );
        Assert.DoesNotContain(
            collection: world["rules"]!.AsArray()
                .Single()!["effects"]!.AsArray()
                .OfType<JsonObject>(),
            filter: static effect => (effect["fromState"]?.GetValue<string>() == "speed")
        );
    }
    [Fact]
    public void ATestLowersToASchedulePlusOneVerdictRowAndRulePerExpectation() {
        var world = OneWorld(source: TestConstructLawTests.Doc(body: """
            test "armour arrives" {
                given {
                    hp = 4
                }
                when {
                    ticks 2
                    seat1: world.state.cell.set armour $value 2
                    ticks 3
                }
                expect {
                    armour == 2 and hp == 4
                }
            }
            """));
        var schedule = world["schedule"]!.AsObject();
        var row = schedule["rows"]!.AsArray()[0]!.AsObject();

        // The cursor opens at tick 1 and `ticks 2` carries it to 3, so the step submits at 3. The trailing
        // `ticks 3` is under the settle floor a submitted command needs, so the export tick — which is also the
        // firing tick — is 3 plus that floor.
        Assert.Equal(
            actual: row["tick"]!.GetValue<ulong>(),
            expected: 3UL
        );
        Assert.Equal(
            actual: row["principal"]!.GetValue<string>(),
            expected: "seat1"
        );
        Assert.Equal(
            actual: schedule["settleTicks"]!.GetValue<ulong>(),
            expected: 6UL
        );

        var verdict = world["state"]!["world"]!.AsArray()
            .OfType<JsonObject>()
            .Single(predicate: static candidate => (candidate["verdict"] is not null));

        Assert.Equal(
            actual: verdict["name"]!.GetValue<string>(),
            expected: "armour-arrives-1"
        );
        Assert.Equal(
            actual: verdict["verdict"]!["gate"]!.GetValue<string>(),
            expected: "armour == 2 and hp == 4"
        );
        Assert.Equal(
            actual: string.Join(
                separator: " ",
                values: verdict["cells"]!.AsArray().OfType<JsonObject>().Select(selector: static cell => cell["key"]!.GetValue<string>())
            ),
            expected: "status armour hp"
        );

        // A given line lands on the row's own boot value rather than on a schedule row — through `value` for a
        // slot-shaped row, since a document authoring both `value` and `cells` is refused.
        Assert.Equal(
            actual: world["state"]!["world"]!.AsArray()
                .OfType<JsonObject>()
                .Single(predicate: static candidate => (candidate["name"]!.GetValue<string>() == "hp"))["value"]!
                .GetValue<long>(),
            expected: 4L
        );

        var rule = world["rules"]!.AsArray().OfType<JsonObject>().Single(predicate: static candidate => (candidate["name"]!.GetValue<string>() == "test-armour-arrives-1"));

        // The gate is the firing tick and nothing else: the expectation is the effect's own condition, so the rule
        // fires once and a verdict that would be false before the last tick is never written as a failure.
        Assert.Equal(
            actual: rule["gate"]!.ToJsonString(),
            expected: """{"$type":"compareState","comparison":"Equal","state":"$tick","value":9}"""
        );
        Assert.Equal(
            actual: rule["effects"]!.AsArray()[0]!["$type"]!.GetValue<string>(),
            expected: "if"
        );
    }
    [Fact]
    public void ATestInsideAnotherConstructIsRefusedByName() => Assert.Contains(
        actualString: TestConstructLawTests.Refusal(
            code: PuckDiagnosticCodes.TestShapeInadmissible,
            source: $"schema: \"puck.world.definition.v1\"\n\n{TestWorldFixtures.Rows}\n\nhost {{\n    test \"nested\" {{\n        expect {{\n            hp == 1\n        }}\n    }}\n}}\n"
        ),
        expectedSubstring: "stands at the document's own root"
    );
    [Fact]
    public void TwoTestsGeneratingOneWorldAreRefusedByName() => Assert.Contains(
        actualString: TestConstructLawTests.Refusal(
            code: PuckDiagnosticCodes.TestShapeInadmissible,
            source: TestConstructLawTests.Doc(body: "test \"one claim\" {\n    expect {\n        hp == 1\n    }\n}\n\ntest \"one, claim!\" {\n    expect {\n        hp == 1\n    }\n}")
        ),
        expectedSubstring: "cannot share a generated world"
    );
    [MemberData(nameof(InadmissibleLines))]
    [Theory]
    public void ALineTheGeneratedWorldCannotCarryIsRefusedByName(string body, string expected) => Assert.Contains(
        actualString: TestConstructLawTests.Refusal(
            code: PuckDiagnosticCodes.TestStepInadmissible,
            source: TestConstructLawTests.Doc(body: $"test \"a probe\" {{\n    {body}\n}}")
        ),
        expectedSubstring: expected
    );
    [Fact]
    public void FormattingASourceCarryingATestKeepsItsGeneratedWorlds() => Assert.Equal(
        actual: Canonical(node: OneWorld(source: PuckFormat.Format(source: TestWorldFixtures.Commented))),
        expected: Canonical(node: OneWorld(source: TestWorldFixtures.Commented))
    );
}

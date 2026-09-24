using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

using Puck.State;

namespace Puck.GamingBricks.Transpiler.Tests;

public class CartridgeRoundTripTests {
    // The shipped tetromino cartridge, which exercises every action kind, both condition kinds, all eight arithmetic
    // operations and nested control flow.
    private static readonly string TetrominoPath = RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/cartridges/tetromino.cgb.cartridge.json");

    // Both sides go through the emitter's own canonicalizer, so the comparison is about content rather than about
    // which order the two producers happened to write their keys in.
    private static string Canonical(JsonObject document) =>
        Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: DocumentLowering.Canonicalize(node: document)!));
    private static JsonObject Compile(string source) {
        var diagnostics = new DiagnosticBag();
        var document = PuckParser.ParseDocumentWithDiagnostics(
            source: source,
            diagnostics: diagnostics
        ).Value;

        Assert.NotNull(@object: document);

        var lowered = CartridgeDocumentEmitter.LowerWithDiagnostics(
            document: document,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: string.Join(
                separator: "\n",
                values: diagnostics.Select(selector: d => $"{d.Code}: {d.Message}")
            )
        );
        Assert.NotNull(@object: lowered.Value);

        return lowered.Value;
    }

    public static IEnumerable<object[]> CommittedSources() => Directory.EnumerateFiles(
        Path.GetDirectoryName(path: TetrominoPath)!,
        "*.puck",
        SearchOption.AllDirectories
    )
        .Order(comparer: StringComparer.Ordinal).Select(selector: path => new object[] { path });
    [Fact]
    public void TestACartridgeRuleBodyRefusesAColonBeforeAContainer() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "probe" {
                when ph == 1
                tags: ["a"]
                ph = 2
            }
            """;

        var diagnostics = new DiagnosticBag();

        PuckParser.ParseDocumentWithDiagnostics(
            source: Source,
            diagnostics: diagnostics
        );

        var refusal = Assert.Single(
            collection: diagnostics,
            predicate: d => (d.Code == PuckDiagnosticCodes.ColonBeforeContainer)
        );

        Assert.Contains(
            "'tags: [' - a array is written without the ':': use 'tags ['",
            refusal.Message,
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void TestAForGeneratesOneRulePerElementAndBindsItsItemAsAConstant() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            for phase in [3, 5] {
                rule "advance" {
                    when ph == phase
                    ph = phase
                }
            }
            """;

        var rules = Assert.IsType<JsonArray>(@object: Compile(source: Source)["rules"]);

        Assert.Equal(
            2,
            rules.Count
        );

        for (var index = 0; (index < rules.Count); ++index) {
            var rule = Assert.IsType<JsonObject>(@object: rules[index]);
            var expected = ((index == 0)
                ? 3
                : 5
            );
            var gate = Assert.IsType<JsonObject>(@object: rule["when"]);
            var step = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["body"])[0]);

            // The bound name is a compile-time value, so it lands as a constant operand. Lowering it as a machine
            // variable named `phase` would compile, run, and read whatever byte happened to live there.
            Assert.Equal(
                expected.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                gate["right"]?.GetValue<string>()
            );
            Assert.Equal(
                expected.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                step["value"]?.GetValue<string>()
            );
        }
    }
    [Fact]
    public void TestAForInsideARuleBodyUnrollsToTheStepsWrittenOut() {
        const string Looped = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "paint" {
                when ph == 1
                for column in [2, 4] {
                    map(row: cr, column: column, tile: 0, palette: 1)
                }
                if ph == 1 {
                    for step in [1] {
                        ph = ph + step
                    }
                }
            }
            """;
        const string Written = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "paint" {
                when ph == 1
                map(row: cr, column: 2, tile: 0, palette: 1)
                map(row: cr, column: 4, tile: 0, palette: 1)
                if ph == 1 {
                    ph = ph + 1
                }
            }
            """;

        Assert.Equal(
            actual: Canonical(document: Compile(source: Looped)),
            expected: Canonical(document: Compile(source: Written))
        );
    }
    [Fact]
    public void TestAForGeneratesOneSectionRowPerElement() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            for (level, index) in [4, 9, 16] {
                variable $"speed{index}" {
                    initial: level
                }
            }

            rule "probe" {
                when ph == 1
                ph = 2
            }
            """;

        var variables = Assert.IsType<JsonArray>(@object: Compile(source: Source)["variables"]);

        // Three written rows and three generated ones are indistinguishable: the document carries the rows, never
        // the loop that produced them.
        Assert.Equal(
            3,
            variables.Count
        );
        Assert.Equal(
            ["speed0", "speed1", "speed2"],
            variables.OfType<JsonObject>().Select(selector: row => (row["name"]?.GetValue<string>() ?? string.Empty)).ToArray()
        );
        Assert.Equal(
            [4L, 9L, 16L],
            variables.OfType<JsonObject>().Select(selector: row => (row["initial"]?.GetValue<long>() ?? 0L)).ToArray()
        );
    }
    [Fact]
    public void TestAStatementWithNoCartridgeMeaningIsRefused() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            solid

            rule "probe" {
                when ph == 1
                ph = 2
            }
            """;

        var diagnostics = new DiagnosticBag();
        var document = PuckParser.ParseDocumentWithDiagnostics(
            source: Source,
            diagnostics: diagnostics
        ).Value;

        Assert.NotNull(@object: document);

        CartridgeDocumentEmitter.LowerWithDiagnostics(
            document: document,
            diagnostics: diagnostics,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Dropping it silently is the failure this refusal exists to prevent: a misspelled row keyword would lose
        // the row and everything nested in it without a word.
        var refusal = Assert.Single(
            collection: diagnostics,
            predicate: d => d.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "no place in a cartridge document"
            )
        );

        Assert.Equal(
            PuckDiagnosticCodes.SemanticValidation,
            refusal.Code
        );
        Assert.Contains(
            "solid",
            refusal.Message,
            StringComparison.Ordinal
        );
    }
    [Fact]
    public void TestArrayElementsReadAndWriteAsOperands() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "arrays" {
                when ph == 1
                field[si] = shapes[k]
            }
            """;

        var step = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Compile(source: Source)["rules"])[0])["body"])[0]);
        var target = Assert.IsType<JsonObject>(@object: step["target"]);

        Assert.Equal(
            "field",
            target["state"]?.GetValue<string>()
        );
        Assert.Equal(
            "si",
            target["key"]?.GetValue<string>()
        );
        Assert.Equal(
            "shapes[k]",
            step["value"]?.GetValue<string>()
        );
    }
    [MemberData(nameof(CommittedSources))]
    [Theory]
    public void TestCommittedSourceCompilesToTheCommittedDocument(string sourcePath) {
        var compiled = CanonicalJsonDocument.Serialize(node: Compile(source: File.ReadAllText(path: sourcePath)));

        // Both artifacts are committed, so this is the regeneration gate: the source and the document it generates
        // can never drift apart without failing here.
        Assert.Equal(
            File.ReadAllBytes(path: Path.ChangeExtension(
                extension: ".cartridge.json",
                path: sourcePath
            )),
            compiled
        );
    }
    [Fact]
    public void TestCompilingTwiceProducesTheSameBytes() {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse(File.ReadAllText(path: TetrominoPath)));
        var source = CartridgeDecompiler.Decompile(document: original);

        Assert.Equal(
            Canonical(document: Compile(source: source)),
            Canonical(document: Compile(source: source))
        );
    }
    [Fact]
    public void TestConstantsAndTemplatesAreSourceOnly() {
        const string WithConstants = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            let wellFloor = 17

            rule "probe" {
                when cr == wellFloor
                ph = 3
            }
            """;
        const string WithoutConstants = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "probe" {
                when cr == 17
                ph = 3
            }
            """;

        Assert.Equal(
            Canonical(document: Compile(source: WithoutConstants)),
            Canonical(document: Compile(source: WithConstants))
        );
    }
    [Fact]
    public void TestEveryArithmeticOperationRoundTrips() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "arithmetic" {
                when ph == 1
                a = 2
                a += 3
                a -= 4
                a *= 5
                a /= 6
                a %= 7
                a &= 8
                a |= 9
                a ^= 10
                a <<= 1
                a >>= 2
            }
            """;

        var body = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Compile(source: Source)["rules"])[0])["body"]);
        var operations = body.OfType<JsonObject>().Select(selector: step => (step["operation"]?.GetValue<string>() ?? string.Empty)).ToArray();

        // A plain `=` carries no operation: assignment is the one combination no opcode spells.
        Assert.Equal(
            [
                string.Empty,
                nameof(ExpressionOp.Add),
                nameof(ExpressionOp.Subtract),
                nameof(ExpressionOp.Multiply),
                nameof(ExpressionOp.Divide),
                nameof(ExpressionOp.Remainder),
                nameof(ExpressionOp.BitAnd),
                nameof(ExpressionOp.BitOr),
                nameof(ExpressionOp.BitXor),
                nameof(ExpressionOp.ShiftLeft),
                nameof(ExpressionOp.ShiftRight),
            ],
            operations
        );
    }
    [Fact]
    public void TestKeyConditionsAndComparisonsShareOneGate() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "input" {
                when key(left, held) and ph == 3 and das >= 16
                want = 1
            }
            """;

        var gate = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Compile(source: Source)["rules"])[0])["when"]);
        var conditions = Assert.IsType<JsonArray>(@object: gate["predicates"]);

        Assert.Equal(
            "all",
            gate["$type"]?.GetValue<string>()
        );

        Assert.Equal(
            3,
            conditions.Count
        );
        Assert.Equal(
            "compareValue",
            Assert.IsType<JsonObject>(@object: conditions[0])["$type"]?.GetValue<string>()
        );
        Assert.Equal(
            "$key:left:held",
            Assert.IsType<JsonObject>(@object: conditions[0])["left"]?.GetValue<string>()
        );
        Assert.Equal(
            "1",
            Assert.IsType<JsonObject>(@object: conditions[0])["right"]?.GetValue<string>()
        );
        Assert.Equal(
            nameof(ExpressionOp.Equal),
            Assert.IsType<JsonObject>(@object: conditions[1])["comparison"]?.GetValue<string>()
        );
        Assert.Equal(
            nameof(ExpressionOp.GreaterOrEqual),
            Assert.IsType<JsonObject>(@object: conditions[2])["comparison"]?.GetValue<string>()
        );
    }
    [Fact]
    public void TestNestedControlFlowLowersToNestedSteps() {
        const string Source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "clear" {
                when ph == 19
                repeat 4 as k {
                    if k >= nfull {
                        break
                    } else {
                        map(row: cr, column: cc, tile: 0, palette: 1)
                    }
                }
            }
            """;

        var loop = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Compile(source: Source)["rules"])[0])["body"])[0]);

        Assert.Equal(
            "repeat",
            loop["kind"]?.GetValue<string>()
        );
        Assert.Equal(
            4,
            loop["count"]?.GetValue<int>()
        );
        Assert.Equal(
            "k",
            loop["index"]?.GetValue<string>()
        );

        var branch = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: loop["body"])[0]);

        Assert.Equal(
            "break",
            Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: branch["then"])[0])["kind"]?.GetValue<string>()
        );

        var painted = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: branch["else"])[0]);

        Assert.Equal(
            "map",
            painted["kind"]?.GetValue<string>()
        );
        Assert.Equal(
            "cr",
            painted["row"]?.GetValue<string>()
        );
        Assert.Equal(
            "0",
            painted["tile"]?.GetValue<string>()
        );
    }
    // Every committed cartridge is held to the round trip, not one of them: a cartridge source is shipped the way a
    // world source is, and the world corpus (tests/Puck.State.Rebuild.Corpus) holds every shipped world source.
    [MemberData(nameof(CommittedSources))]
    [Theory]
    public void TestEveryCommittedCartridgeSurvivesADecompileAndRecompile(string sourcePath) {
        var original = Assert.IsType<JsonObject>(@object: JsonNode.Parse(File.ReadAllText(path: Path.ChangeExtension(
            extension: ".cartridge.json",
            path: sourcePath
        ))));
        var canonicalOriginal = Canonical(document: original);

        var source = CartridgeDecompiler.Decompile(document: original);
        var recompiled = Compile(source: source);

        // Byte identity is the whole claim: the source the decompiler wrote means exactly the document it read.
        Assert.Equal(
            canonicalOriginal,
            Canonical(document: recompiled)
        );
    }
}

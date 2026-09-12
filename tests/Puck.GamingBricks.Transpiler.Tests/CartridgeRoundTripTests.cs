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
    // The shipped Tetris cartridge, which exercises every action kind, both condition kinds, all eight arithmetic
    // operations and nested control flow.
    private static readonly string s_tetrisPath = Path.Combine(
        RepositoryRoot(), "src", "Puck.World", "Assets", "cartridges", "tetris.cgb.cartridge.json");

    private static string RepositoryRoot() {
        var directory = AppContext.BaseDirectory;

        while ((directory is not null) && !File.Exists(Path.Combine(directory, "Puck.slnx"))) {
            directory = Path.GetDirectoryName(directory);
        }

        Assert.NotNull(directory);

        return directory;
    }

    private static JsonObject Compile(string source) {
        var diagnostics = new DiagnosticBag();
        var document = PuckParser.ParseDocumentWithDiagnostics(source: source, diagnostics: diagnostics).Value;

        Assert.NotNull(document);

        var lowered = CartridgeDocumentEmitter.LowerWithDiagnostics(document: document, diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(diagnostics.HasErrors, string.Join("\n", diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        Assert.NotNull(lowered.Value);

        return lowered.Value;
    }

    // Both sides go through the emitter's own canonicalizer, so the comparison is about content rather than about
    // which order the two producers happened to write their keys in.
    private static string Canonical(JsonObject document) =>
        Encoding.UTF8.GetString(CanonicalJsonDocument.Serialize(DocumentLowering.Canonicalize(document)!));

    [Fact]
    public void TestShippedTetrisCartridgeSurvivesADecompileAndRecompile() {
        var original = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(s_tetrisPath)));
        var canonicalOriginal = Canonical(original);

        var source = CartridgeDecompiler.Decompile(document: original);
        var recompiled = Compile(source);

        // Byte identity is the whole claim: the source the decompiler wrote means exactly the document it read.
        Assert.Equal(canonicalOriginal, Canonical(recompiled));
    }

    public static IEnumerable<object[]> CommittedSources() => Directory.EnumerateFiles(Path.GetDirectoryName(s_tetrisPath)!, "*.puck", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(path => new object[] { path });

    [Theory]
    [MemberData(nameof(CommittedSources))]
    public void TestCommittedSourceCompilesToTheCommittedDocument(string sourcePath) {
        var compiled = CanonicalJsonDocument.Serialize(Compile(File.ReadAllText(sourcePath)));

        // Both artifacts are committed, so this is the regeneration gate: the source and the document it generates
        // can never drift apart without failing here.
        Assert.Equal(File.ReadAllBytes(Path.ChangeExtension(sourcePath, ".cartridge.json")), compiled);
    }

    [Fact]
    public void TestCompilingTwiceProducesTheSameBytes() {
        var original = Assert.IsType<JsonObject>(JsonNode.Parse(File.ReadAllText(s_tetrisPath)));
        var source = CartridgeDecompiler.Decompile(document: original);

        Assert.Equal(Canonical(Compile(source)), Canonical(Compile(source)));
    }

    [Fact]
    public void TestEveryArithmeticOperationRoundTrips() {
        const string source = """
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

        var body = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Compile(source)["rules"])[0])["body"]);
        var operations = body.OfType<JsonObject>().Select(step => (step["operation"]?.GetValue<string>() ?? string.Empty)).ToArray();

        // A plain `=` carries no operation: assignment is the one combination no opcode spells.
        Assert.Equal(
            [
                string.Empty,
                nameof(ExpressionOp.Add),
                nameof(ExpressionOp.Subtract),
                nameof(ExpressionOp.Multiply),
                nameof(ExpressionOp.Divide),
                nameof(ExpressionOp.Modulo),
                nameof(ExpressionOp.BitAnd),
                nameof(ExpressionOp.BitOr),
                nameof(ExpressionOp.BitXor),
                nameof(ExpressionOp.ShiftLeft),
                nameof(ExpressionOp.ShiftRight),
            ],
            operations);
    }

    [Fact]
    public void TestKeyConditionsAndComparisonsShareOneGate() {
        const string source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "input" {
                when key(left, held) and ph == 3 and das >= 16
                want = 1
            }
            """;

        var gate = Assert.IsType<JsonObject>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Compile(source)["rules"])[0])["when"]);
        var conditions = Assert.IsType<JsonArray>(gate["predicates"]);

        Assert.Equal("all", gate["$type"]?.GetValue<string>());

        Assert.Equal(3, conditions.Count);
        Assert.Equal("compareValue", Assert.IsType<JsonObject>(conditions[0])["$type"]?.GetValue<string>());
        Assert.Equal("$key:left:held", Assert.IsType<JsonObject>(conditions[0])["left"]?.GetValue<string>());
        Assert.Equal("1", Assert.IsType<JsonObject>(conditions[0])["right"]?.GetValue<string>());
        Assert.Equal(nameof(ActionStateComparison.Equal), Assert.IsType<JsonObject>(conditions[1])["comparison"]?.GetValue<string>());
        Assert.Equal(nameof(ActionStateComparison.GreaterOrEqual), Assert.IsType<JsonObject>(conditions[2])["comparison"]?.GetValue<string>());
    }

    [Fact]
    public void TestArrayElementsReadAndWriteAsOperands() {
        const string source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "arrays" {
                when ph == 1
                field[si] = shapes[k]
            }
            """;

        var step = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Compile(source)["rules"])[0])["body"])[0]);
        var target = Assert.IsType<JsonObject>(step["target"]);

        Assert.Equal("field", target["state"]?.GetValue<string>());
        Assert.Equal("si", target["key"]?.GetValue<string>());
        Assert.Equal("shapes[k]", step["value"]?.GetValue<string>());
    }

    [Fact]
    public void TestNestedControlFlowLowersToNestedSteps() {
        const string source = """
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

        var loop = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Compile(source)["rules"])[0])["body"])[0]);

        Assert.Equal("repeat", loop["kind"]?.GetValue<string>());
        Assert.Equal(4, loop["count"]?.GetValue<int>());
        Assert.Equal("k", loop["index"]?.GetValue<string>());

        var branch = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(loop["body"])[0]);

        Assert.Equal("break", Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(branch["then"])[0])["kind"]?.GetValue<string>());

        var painted = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(branch["else"])[0]);

        Assert.Equal("map", painted["kind"]?.GetValue<string>());
        Assert.Equal("cr", painted["row"]?.GetValue<string>());
        Assert.Equal("0", painted["tile"]?.GetValue<string>());
    }

    [Fact]
    public void TestAForGeneratesOneSectionRowPerElement() {
        const string source = """
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

        var variables = Assert.IsType<JsonArray>(Compile(source)["variables"]);

        // Three written rows and three generated ones are indistinguishable: the document carries the rows, never
        // the loop that produced them.
        Assert.Equal(3, variables.Count);
        Assert.Equal(
            ["speed0", "speed1", "speed2"],
            variables.OfType<JsonObject>().Select(row => (row["name"]?.GetValue<string>() ?? string.Empty)).ToArray());
        Assert.Equal(
            [4L, 9L, 16L],
            variables.OfType<JsonObject>().Select(row => (row["initial"]?.GetValue<long>() ?? 0L)).ToArray());
    }

    [Fact]
    public void TestAForGeneratesOneRulePerElementAndBindsItsItemAsAConstant() {
        const string source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            for phase in [3, 5] {
                rule "advance" {
                    when ph == phase
                    ph = phase
                }
            }
            """;

        var rules = Assert.IsType<JsonArray>(Compile(source)["rules"]);

        Assert.Equal(2, rules.Count);

        for (var index = 0; (index < rules.Count); ++index) {
            var rule = Assert.IsType<JsonObject>(rules[index]);
            var expected = ((index == 0) ? 3 : 5);
            var gate = Assert.IsType<JsonObject>(rule["when"]);
            var step = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(rule["body"])[0]);

            // The bound name is a compile-time value, so it lands as a constant operand. Lowering it as a machine
            // variable named `phase` would compile, run, and read whatever byte happened to live there.
            Assert.Equal(expected.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), gate["right"]?.GetValue<string>());
            Assert.Equal(expected.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), step["value"]?.GetValue<string>());
        }
    }

    [Fact]
    public void TestAStatementWithNoCartridgeMeaningIsRefused() {
        const string source = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            solid

            rule "probe" {
                when ph == 1
                ph = 2
            }
            """;

        var diagnostics = new DiagnosticBag();
        var document = PuckParser.ParseDocumentWithDiagnostics(source: source, diagnostics: diagnostics).Value;

        Assert.NotNull(document);

        CartridgeDocumentEmitter.LowerWithDiagnostics(document: document, diagnostics: diagnostics, cancellationToken: TestContext.Current.CancellationToken);

        // Dropping it silently is the failure this refusal exists to prevent: a misspelled row keyword would lose
        // the row and everything nested in it without a word.
        var refusal = Assert.Single(diagnostics, d => d.Message.Contains("no place in a cartridge document", StringComparison.Ordinal));

        Assert.Equal(PuckDiagnosticCodes.SemanticValidation, refusal.Code);
        Assert.Contains("solid", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TestConstantsAndTemplatesAreSourceOnly() {
        const string withConstants = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            let wellFloor = 17

            rule "probe" {
                when cr == wellFloor
                ph = 3
            }
            """;
        const string withoutConstants = """
            schema: "puck.cartridge.v1"
            target: "cgb"

            rule "probe" {
                when cr == 17
                ph = 3
            }
            """;

        Assert.Equal(Canonical(Compile(withoutConstants)), Canonical(Compile(withConstants)));
    }
}

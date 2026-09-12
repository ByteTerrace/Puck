using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Xunit;

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

        var lowered = CartridgeDocumentEmitter.LowerWithDiagnostics(document: document, diagnostics: diagnostics);

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

    [Fact]
    public void TestCommittedSourceCompilesToTheCommittedDocument() {
        var sourcePath = Path.ChangeExtension(s_tetrisPath.Replace(".cartridge.json", ".json"), ".puck");
        var compiled = CanonicalJsonDocument.Serialize(Compile(File.ReadAllText(sourcePath)));

        // Both artifacts are committed, so this is the regeneration gate: the source and the document it generates
        // can never drift apart without failing here.
        Assert.Equal(File.ReadAllBytes(s_tetrisPath), compiled);
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

        Assert.Equal(
            ["set", "add", "subtract", "mul", "div", "mod", "and", "or", "xor", "shl", "shr"],
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

        var conditions = Assert.IsType<JsonArray>(Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(Compile(source)["rules"])[0])["when"]);

        Assert.Equal(3, conditions.Count);
        Assert.Equal("key", Assert.IsType<JsonObject>(conditions[0])["kind"]?.GetValue<string>());
        Assert.Equal("left", Assert.IsType<JsonObject>(conditions[0])["key"]?.GetValue<string>());
        Assert.Equal("held", Assert.IsType<JsonObject>(conditions[0])["mode"]?.GetValue<string>());
        Assert.Equal("eq", Assert.IsType<JsonObject>(conditions[1])["comparison"]?.GetValue<string>());
        Assert.Equal("ge", Assert.IsType<JsonObject>(conditions[2])["comparison"]?.GetValue<string>());
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
        var value = Assert.IsType<JsonObject>(step["value"]);

        Assert.Equal("field", target["array"]?.GetValue<string>());
        Assert.Equal("si", Assert.IsType<JsonObject>(target["index"])["variable"]?.GetValue<string>());
        Assert.Equal("shapes", value["array"]?.GetValue<string>());
        Assert.Equal("k", Assert.IsType<JsonObject>(value["index"])["variable"]?.GetValue<string>());
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
        Assert.Equal("cr", Assert.IsType<JsonObject>(painted["row"])["variable"]?.GetValue<string>());
        Assert.Equal(0, Assert.IsType<JsonObject>(painted["tile"])["constant"]?.GetValue<int>());
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

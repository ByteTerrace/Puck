using Parlot;
using Parlot.Fluent;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ParserTests {
    [Fact]
    public void TestTermsWithPuckWhiteSpaceParser() {
        var ws = new PuckWhiteSpaceParser();
        var context = new ParseContext(new Scanner("// comment\n\"hello world\" /* comment */ 1234")) {
            WhiteSpaceParser = ws
        };

        var strParser = Terms.String();
        var intParser = Terms.Integer();

        var strResult = new ParseResult<TextSpan>();
        Assert.True(strParser.Parse(context, ref strResult));
        Assert.Equal("hello world", strResult.Value.ToString());

        var intResult = new ParseResult<long>();
        Assert.True(intParser.Parse(context, ref intResult));
        Assert.Equal(1234, intResult.Value);
    }

    [Fact]
    public void TestDollarIdentifier() {
        var context = new ParseContext(new Scanner("$type _id standard-name")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser()
        };
        var identParser = Terms.Identifier();
        var result = new ParseResult<TextSpan>();
        Assert.True(identParser.Parse(context, ref result));
        Assert.Equal("$type", result.Value.ToString());
    }

    [Fact]
    public void TestNegativeDecimal() {
        var context = new ParseContext(new Scanner("-9.81 -10 0.25s")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser()
        };
        var decParser = Terms.Decimal();
        var result = new ParseResult<decimal>();
        Assert.True(decParser.Parse(context, ref result));
        Assert.Equal(-9.81m, result.Value);
    }

    [Fact]
    public void TestEscapedString() {
        var context = new ParseContext(new Scanner("\"hello \\\"world\\\" \\n test\"")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser()
        };
        var strParser = Terms.String();
        var result = new ParseResult<TextSpan>();
        Assert.True(strParser.Parse(context, ref result));
        var decoded = Character.DecodeString(result.Value).ToString();
        Assert.Equal("hello \"world\" \n test", decoded);
    }

    [Fact]
    public void TestMinimalSyntheticWorldParsing() {
        const string source = """
            schema: "puck.world.def.v1"
            basis: "worlds/standard.basis.json"

            host {
                width: 1280
                height: 720
                fullscreen: false
                targetHertz: 60
            }
            """;

        var doc = PuckParser.ParseDocument(source);
        Assert.Equal("puck.world.def.v1", doc.Schema);
        Assert.Equal("worlds/standard.basis.json", doc.Basis);
        Assert.Single(doc.Statements);

        var hostBlock = Assert.IsType<BlockNode>(doc.Statements[0]);
        Assert.Equal("host", hostBlock.Identifier);
        Assert.Equal(4, hostBlock.Statements.Count);

        var widthProp = Assert.IsType<PropertyNode>(hostBlock.Statements[0]);
        Assert.Equal("width", widthProp.Name);
        var widthLit = Assert.IsType<LiteralExpressionNode>(widthProp.Value);
        Assert.Equal(1280L, widthLit.Value);
    }

    [Fact]
    public void TestLanguageFeaturesParsing() {
        const string source = """
            schema: "puck.world.def.v1"

            let defaultGravity = [0, -9.81, 0]
            let tickInterval = 0.25s
            let primaryColor = #ffaa00

            import "extensions/rules.world.puck" as rules
            export read state, scores
            export action step
            export binding input

            template seat(id, angle = 0rad) {
                seatRig id {
                    pose: [0, 0, 0]
                    azimuth: angle
                }
            }

            host {
                tickInterval: tickInterval
                physics: {
                    gravity: defaultGravity
                }
            }

            views {
                layout "tabletop" {
                    center: [0, 1.2, 0]
                    orbit: orbit(radius: 2.5m, pitch: 45deg, yaw: 0rad)
                }
            }

            seat("seat-north", 180deg)
            """;

        var doc = PuckParser.ParseDocument(source);
        Assert.Equal("puck.world.def.v1", doc.Schema);
        Assert.Equal(11, doc.Statements.Count);

        // Verify let statements
        var letGravity = Assert.IsType<LetNode>(doc.Statements[0]);
        Assert.Equal("defaultGravity", letGravity.Name);
        var gravArr = Assert.IsType<ArrayExpressionNode>(letGravity.Value);
        Assert.Equal(3, gravArr.Elements.Count);

        var letTick = Assert.IsType<LetNode>(doc.Statements[1]);
        Assert.Equal("tickInterval", letTick.Name);
        var tickLit = Assert.IsType<LiteralExpressionNode>(letTick.Value);
        Assert.Equal(0.25, (double)tickLit.Value!, 3);
        Assert.Equal("s", tickLit.Unit);

        var letColor = Assert.IsType<LetNode>(doc.Statements[2]);
        Assert.Equal("primaryColor", letColor.Name);
        var colorExpr = Assert.IsType<ColorExpressionNode>(letColor.Value);
        Assert.Equal("#ffaa00", colorExpr.Hex);

        // Verify import & export
        var importNode = Assert.IsType<ImportNode>(doc.Statements[3]);
        Assert.Equal("extensions/rules.world.puck", importNode.Path);
        Assert.Equal("rules", importNode.Alias);

        var exportRead = Assert.IsType<ExportNode>(doc.Statements[4]);
        Assert.Equal("read", exportRead.Facet);
        Assert.Equal(["state", "scores"], exportRead.Names);

        // Verify template
        var templateNode = Assert.IsType<TemplateNode>(doc.Statements[7]);
        Assert.Equal("seat", templateNode.Name);
        Assert.Equal(2, templateNode.Parameters.Count);
        Assert.Equal("id", templateNode.Parameters[0].Name);
        Assert.Null(templateNode.Parameters[0].DefaultValue);
        Assert.Equal("angle", templateNode.Parameters[1].Name);
        Assert.NotNull(templateNode.Parameters[1].DefaultValue);

        // Verify invocation
        var invocation = Assert.IsType<ExpressionStatementNode>(doc.Statements[10]);
        var call = Assert.IsType<CallExpressionNode>(invocation.Expression);
        Assert.Equal("seat", call.Name);
        Assert.Equal(2, call.Arguments.Count);
    }
}

using Parlot;
using Parlot.Fluent;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ParserTests {
    [Fact]
    public void TestDollarIdentifier() {
        var context = new ParseContext(new Scanner(buffer: "$type _id standard-name")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser(),
        };
        var identParser = Terms.Identifier();
        var result = new ParseResult<TextSpan>();

        Assert.True(condition: identParser.Parse(
            context: context,
            result: ref result
        ));
        Assert.Equal(
            "$type",
            result.Value.ToString()
        );
    }
    [Fact]
    public void TestEscapedString() {
        var context = new ParseContext(new Scanner(buffer: "\"hello \\\"world\\\" \\n test\"")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser(),
        };
        var strParser = Terms.String();
        var result = new ParseResult<TextSpan>();

        Assert.True(condition: strParser.Parse(
            context: context,
            result: ref result
        ));
        var decoded = Character.DecodeString(textSpan: result.Value).ToString();

        Assert.Equal(
            actual: decoded,
            expected: "hello \"world\" \n test"
        );
    }
    [Fact]
    public void TestLanguageFeaturesParsing() {
        const string Source = """
            schema: "puck.world.definition.v1"

            let defaultGravity = [0, -9.81, 0]
            let tickInterval = 0.25s
            let primaryColor = #ffaa00

            import "extensions/rules.world.puck" as rules
            export read state, scores
            export action step
            export binding input

            template seat(id, angle = 0rad) {
                seatRig id {
                    pose [0, 0, 0]
                    azimuth: angle
                }
            }

            host {
                tickInterval: tickInterval
                physics {
                    gravity: defaultGravity
                }
            }

            views {
                layout "tabletop" {
                    center [0, 1.2, 0]
                    orbit: orbit(radius: 2.5m, pitch: 45deg, yaw: 0rad)
                }
            }

            seat("seat-north", 180deg)
            """;

        var doc = PuckParser.ParseDocument(Source);

        Assert.Equal(
            "puck.world.definition.v1",
            doc.Schema
        );
        Assert.Equal(
            11,
            doc.Statements.Count
        );

        // Verify let statements
        var letGravity = Assert.IsType<LetNode>(@object: doc.Statements[0]);

        Assert.Equal(
            "defaultGravity",
            letGravity.Name
        );
        var gravArr = Assert.IsType<ArrayExpressionNode>(@object: letGravity.Value);

        Assert.Equal(
            3,
            gravArr.Elements.Count
        );

        var letTick = Assert.IsType<LetNode>(@object: doc.Statements[1]);

        Assert.Equal(
            "tickInterval",
            letTick.Name
        );
        var tickLit = Assert.IsType<LiteralExpressionNode>(@object: letTick.Value);

        Assert.Equal(
            0.25,
            ((double)tickLit.Value!),
            3
        );
        Assert.Equal(
            "s",
            tickLit.Unit
        );

        var letColor = Assert.IsType<LetNode>(@object: doc.Statements[2]);

        Assert.Equal(
            "primaryColor",
            letColor.Name
        );
        var colorExpr = Assert.IsType<ColorExpressionNode>(@object: letColor.Value);

        Assert.Equal(
            "#ffaa00",
            colorExpr.Hex
        );

        // Verify import & export
        var importNode = Assert.IsType<ImportNode>(@object: doc.Statements[3]);

        Assert.Equal(
            "extensions/rules.world.puck",
            importNode.Path
        );
        Assert.Equal(
            "rules",
            importNode.Alias
        );

        var exportRead = Assert.IsType<ExportNode>(@object: doc.Statements[4]);

        Assert.Equal(
            "read",
            exportRead.Facet
        );
        Assert.Equal(
            ["state", "scores"],
            exportRead.Names
        );

        // Verify template
        var templateNode = Assert.IsType<TemplateNode>(@object: doc.Statements[7]);

        Assert.Equal(
            "seat",
            templateNode.Name
        );
        Assert.Equal(
            2,
            templateNode.Parameters.Count
        );
        Assert.Equal(
            "id",
            templateNode.Parameters[0].Name
        );
        Assert.Null(@object: templateNode.Parameters[0].DefaultValue);
        Assert.Equal(
            "angle",
            templateNode.Parameters[1].Name
        );
        Assert.NotNull(@object: templateNode.Parameters[1].DefaultValue);

        // Verify invocation
        var invocation = Assert.IsType<ExpressionStatementNode>(@object: doc.Statements[10]);
        var call = Assert.IsType<CallExpressionNode>(@object: invocation.Expression);

        Assert.Equal(
            "seat",
            call.Name
        );
        Assert.Equal(
            2,
            call.Arguments.Count
        );
    }
    [Fact]
    public void TestMinimalSyntheticWorldParsing() {
        const string Source = """
            schema: "puck.world.definition.v1"
            basis: "worlds/standard.basis.json"

            host {
                width: 1280
                height: 720
                fullscreen: false
                targetHertz: 60
            }
            """;

        var doc = PuckParser.ParseDocument(Source);

        Assert.Equal(
            "puck.world.definition.v1",
            doc.Schema
        );
        Assert.Equal(
            "worlds/standard.basis.json",
            doc.Basis
        );
        Assert.Single(collection: doc.Statements);

        var hostBlock = Assert.IsType<BlockNode>(@object: doc.Statements[0]);

        Assert.Equal(
            "host",
            hostBlock.Identifier
        );
        Assert.Equal(
            4,
            hostBlock.Statements.Count
        );

        var widthProp = Assert.IsType<PropertyNode>(@object: hostBlock.Statements[0]);

        Assert.Equal(
            "width",
            widthProp.Name
        );
        var widthLit = Assert.IsType<LiteralExpressionNode>(@object: widthProp.Value);

        Assert.Equal(
            1280L,
            widthLit.Value
        );
    }
    [Fact]
    public void TestNegativeDecimal() {
        var context = new ParseContext(new Scanner(buffer: "-9.81 -10 0.25s")) {
            WhiteSpaceParser = new PuckWhiteSpaceParser(),
        };
        var decParser = Terms.Decimal();
        var result = new ParseResult<decimal>();

        Assert.True(condition: decParser.Parse(
            context: context,
            result: ref result
        ));
        Assert.Equal(
            actual: result.Value,
            expected: -9.81m
        );
    }
    [Fact]
    public void TestTermsWithPuckWhiteSpaceParser() {
        var ws = new PuckWhiteSpaceParser();
        var context = new ParseContext(new Scanner(buffer: "// comment\n\"hello world\" /* comment */ 1234")) {
            WhiteSpaceParser = ws,
        };

        var strParser = Terms.String();
        var intParser = Terms.Integer();

        var strResult = new ParseResult<TextSpan>();

        Assert.True(condition: strParser.Parse(
            context: context,
            result: ref strResult
        ));
        Assert.Equal(
            "hello world",
            strResult.Value.ToString()
        );

        var intResult = new ParseResult<long>();

        Assert.True(condition: intParser.Parse(
            context: context,
            result: ref intResult
        ));
        Assert.Equal(
            actual: intResult.Value,
            expected: 1234
        );
    }
}

using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Clock keys use the vocabulary's shared trailing-array syntax, printer and source-save path.</summary>
public sealed class KeyGrammarLawTests {
    private const string Source = (WorldSources.Header + """
        let pale = #DDEEFF
        theme {
          color {
            // Keep the authored binding and both comments.
            accent: keys(clock: day) /* key header */ [
              { at: 0s, value: pale, ease: smooth } // night key
              { at: 4s, value: #FF8800 } // sunset key
            ]
            warning: #FFDD00
          }
        }

        """);

    [Fact]
    public void Typed_keys_lower_and_print_through_the_shared_call_and_array_paths() {
        var compiled = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken);
        var document = compiled.RequireJson();
        var keys = document["theme"]!["color"]!["accent"]!;

        Assert.Equal("day", keys["clock"]!.GetValue<string>());
        Assert.Equal(4L, keys["keys"]![1]!["at"]!.GetValue<long>());
        Assert.Equal("#DDEEFF", keys["keys"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("smooth", keys["keys"]![0]!["ease"]!.GetValue<string>());
        Assert.Equal(Source, PuckFormat.Format(source: Source));
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.Contains(actualString: printed, expectedSubstring: "keys(clock: day)");
        Assert.True(condition: JsonNode.DeepEquals(node1: document, node2: WorldCompiler.Compile(source: printed, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [Fact]
    public void Named_clocks_and_section_keys_keep_their_document_shape() {
        var source = (WorldSources.Header + """
            timeline {
              clock day { periodSeconds: 20min, spanSeconds: 24h }
              clock pulse { phase: keys(clock: day) [ { at: 0h, value: 0 } { at: 12h, value: 1 } ] }
            }
            render { sky { keys(clock: day) [
              { at: 0h, layers { air { opacity: 0 } } }
              { at: 12h, layers { air { opacity: 1 } } }
            ] } }
            """);
        var document = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken).RequireJson();

        Assert.Equal("day", document["timeline"]!["clocks"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(1200L, document["timeline"]!["clocks"]![0]!["periodSeconds"]!.GetValue<long>());
        Assert.Equal(43200L, document["render"]!["sky"]!["keys"]!["keys"]![1]!["at"]!.GetValue<long>());
        var printed = WorldDecompiler.Decompile(root: document);

        Assert.True(condition: JsonNode.DeepEquals(node1: document, node2: WorldCompiler.Compile(source: printed, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [Fact]
    public void Trailing_array_ownership_comes_from_the_supplied_vocabulary() {
        var original = WorldConstructs.Table.Constructs.Single(predicate: construct => (construct.Keyword == "keys"));
        var vocabulary = new WorldDocumentVocabulary(constructs: new WorldConstructTable(
            constructs: [.. WorldConstructs.Table.Constructs, original with { Keyword = "fixtureCurve" }],
            excluded: WorldConstructs.Table.Excluded));
        var expression = Assert.IsType<CallExpressionNode>(@object: PuckParser.ParseExpression(source: "fixtureCurve(clock: 1) [2, 3]", vocabulary: vocabulary));

        Assert.True(condition: expression.Arguments[^1].TrailingBody);
        Assert.Equal("keys", expression.Arguments[^1].Name);
        Assert.Equal(2, Assert.IsType<ArrayExpressionNode>(@object: expression.Arguments[^1].Value).Elements.Count);
        Assert.Throws<PuckParseException>(testCode: () => PuckParser.ParseExpression(source: "fixtureCurve(clock: 1) [2, 3]", vocabulary: WorldDocumentVocabulary.Instance));
    }
    [Fact]
    public void Saving_an_ordinary_leaf_preserves_unchanged_keyed_source() {
        var compiled = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken);
        var target = compiled.RequireJson().DeepClone().AsObject();

        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var unchanged, reason: out var reason), userMessage: reason);
        Assert.Equal(actual: unchanged, expected: Source);
        target["theme"]!["color"]!["warning"] = "#FF0000";
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var warning, reason: out reason), userMessage: reason);
        Assert.Equal(Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"#FF0000\"", oldValue: "#FFDD00"), warning);
    }
    [Fact]
    public void An_existing_template_call_with_another_header_keeps_its_ordinary_call_shape() {
        var source = (WorldSources.Header + """
            template keys(seat) {
                host { width: seat }
            }
            keys(seat: 640)
            """);
        var compiled = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(640L, compiled.RequireJson()["host"]!["width"]!.GetValue<long>());
        var formatted = PuckFormat.Format(source: source);

        Assert.Contains(actualString: formatted, expectedSubstring: "keys(seat: 640)");
        Assert.True(condition: JsonNode.DeepEquals(node1: compiled.RequireJson(), node2: WorldCompiler.Compile(source: formatted, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [Fact]
    public void An_ordinary_call_keeps_adjacent_indexing() {
        var indexed = Assert.IsType<IndexExpressionNode>(@object: PuckParser.ParseExpression(source: "keys(seat: 1)[0]", vocabulary: WorldDocumentVocabulary.Instance));

        Assert.DoesNotContain(collection: Assert.IsType<CallExpressionNode>(@object: indexed.Target).Arguments, filter: argument => argument.TrailingBody);
    }
    [Fact]
    public void An_ordinary_call_keeps_a_following_array_element() {
        var array = Assert.IsType<ArrayExpressionNode>(@object: PuckParser.ParseExpression(source: "[\n keys(seat: 1)\n [0]\n]", vocabulary: WorldDocumentVocabulary.Instance));

        Assert.Equal(2, array.Elements.Count);
        Assert.IsType<CallExpressionNode>(@object: array.Elements[0]);
        Assert.IsType<ArrayExpressionNode>(@object: array.Elements[1]);
    }
    [InlineData(0, "0.2", "0.3")]
    [InlineData(1, "0.8", "0.9")]
    [Theory]
    public void Saving_an_appended_array_leaf_keeps_both_authored_property_spans(int row, string before, string after) {
        var source = (WorldSources.Header + """
            views {
              // First authored array stays separate.
              post [ { name: a, package: "sdf.film-grain", parameters { strength: 0.2 } } ]
              // The appended row owns a different nested value span.
              post [ { name: b, package: "sdf.film-grain", parameters { strength: 0.8 } } ]
            }
            """);
        var target = WorldCompiler.Compile(source: source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["views"]!["post"]![row]!["parameters"]!["strength"] = double.Parse(after, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: source, target: target, rewritten: out var saved, reason: out var reason), userMessage: reason);
        Assert.Equal(source.Replace(comparisonType: StringComparison.Ordinal, newValue: after, oldValue: before), saved);
    }
    [Fact]
    public void Saving_a_key_leaf_preserves_neighboring_expressions_and_comments() {
        var target = WorldCompiler.Compile(source: Source, cancellationToken: TestContext.Current.CancellationToken).RequireJson().DeepClone().AsObject();

        target["theme"]!["color"]!["accent"]!["keys"]![1]!["value"] = "#112233";
        Assert.True(condition: WorldSourceEdits.TryRewrite(source: Source, target: target, rewritten: out var keyed, reason: out var reason), userMessage: reason);
        Assert.Equal(Source.Replace(comparisonType: StringComparison.Ordinal, newValue: "\"#112233\"", oldValue: "#FF8800"), keyed);
        Assert.True(condition: JsonNode.DeepEquals(node1: target, node2: WorldCompiler.Compile(source: keyed, cancellationToken: TestContext.Current.CancellationToken).RequireJson()));
    }
    [InlineData("keys(clock: day)", "opening")]
    [InlineData("keys(clock: day, keys: []) []", "header argument")]
    [InlineData("keys(clock: day, typo: 3) []", "typo")]
    [InlineData("keys(clock: day) [{ at: 0s, value: keys(clock: night) [] }]", "cannot contain keys")]
    [Theory]
    public void Misspelled_or_nested_keys_are_refused_by_name(string expression, string reason) {
        var result = WorldCompiler.Compile(source: (((WorldSources.Header + "theme { color { accent: ") + expression) + " } }"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: result.Diagnostics.HasErrors);
        Assert.Contains(reason, result.Diagnostics.FormatReport());
    }
}

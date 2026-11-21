using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.State.Rules;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Constant folding has one home, the rule compiler: a constant expression lowers to the same compiled rule
/// whether a <c>.puck</c> source or a JSON document spells it, and costs what the literal of its value costs. The
/// transpiler substitutes a value only where every numeric kind agrees on it (RuleCompiler.FoldKindFree), and
/// CompiledValueSource.FromExpression turns a program that folded to one constant, or that is one plain read, into
/// that literal or that read.</summary>
public class ConstantFoldLawTests {
    private const string State = """
        schema: "puck.world.definition.v1"
        documentId: "fold"

        let limit = 5
        let even = 6

        state {
          world {
            slot count = 0
            slot level = 0
            slot speed = 0.0
          }
        }

        """;

    private static (WorldDefinition Definition, JsonObject Json) Compile(string rules, string state = State) {
        var source = (state + rules);
        var compiled = WorldCompileSession.Shared.Compile(cancellationToken: TestContext.Current.CancellationToken, source: source);

        Assert.False(condition: compiled.Diagnostics.HasErrors, userMessage: compiled.Diagnostics.FormatReport(sourceText: source));

        return (Read(json: compiled.Json!), compiled.Json!);
    }
    private static WorldDefinition Read(JsonObject json) => JsonSerializer.Deserialize<WorldDefinition>(
        json: json.ToJsonString(),
        options: WorldJsonContext.Default.Options
    )!;
    // The same document with its first rule's first effect, or its gate, replaced by a JSON spelling.
    private static WorldDefinition Respell(JsonObject json, JsonObject? effect = null, JsonObject? gate = null) {
        var copy = json.DeepClone().AsObject();
        var rule = copy["rules"]![0]!.AsObject();

        if (effect is not null) { rule["effects"]![0] = effect; }
        if (gate is not null) { rule["gate"] = gate; }
        return Read(json: copy);
    }
    // A document's postfix program for an infix expression.
    private static JsonObject Program(string text) {
        Assert.True(condition: ExpressionSpelling.TryParse(error: out var error, program: out var program, text: text), userMessage: error);

        return ExpressionProgramJsonConverter.ToNode(program: program);
    }
    private static JsonObject CompareState(int value) => new() { ["$type"] = "compareState", ["state"] = "level", ["comparison"] = "Less", ["value"] = value };
    private static JsonObject SetState(string row, string expression) => new() {
        ["$type"] = "setState",
        ["state"] = row,
        ["expression"] = Program(text: expression),
    };
    private static (CompiledRule Rule, RuleCompileContext Context) Lower(WorldDefinition definition, int index = 0) {
        var context = WorldFactsCompiler.Context(definition: definition);

        return (RuleCompiler.Compile(context: context, rule: definition.Rules![index]), context);
    }
    private static CompiledValueSource Source(CompiledRule rule) => ((IValueSourcedEffect)rule.Effects[0]).Source;
    private static void AssertSame(WorldDefinition left, WorldDefinition right) {
        var (a, aContext) = Lower(definition: left);
        var (b, bContext) = Lower(definition: right);

        Assert.Equal(expected: a.CostBreakdown(context: aContext), actual: b.CostBreakdown(context: bContext));
        Assert.Equal(expected: a.Effects.Length, actual: b.Effects.Length);
        Assert.Equal(expected: Source(rule: a).IsLiteral, actual: Source(rule: b).IsLiteral);
        Assert.Equal(expected: Source(rule: a).RawValue, actual: Source(rule: b).RawValue);
        Assert.Equal(expected: a.Gate.Length, actual: b.Gate.Length);
        for (var index = 0; (index < a.Gate.Length); index++) {
            Assert.Equal(expected: a.Gate[index].Comparison, actual: b.Gate[index].Comparison);
            Assert.Equal(expected: a.Gate[index].LeftSource.IsOperand, actual: b.Gate[index].LeftSource.IsOperand);
            Assert.Equal(expected: a.Gate[index].RightSource.IsLiteral, actual: b.Gate[index].RightSource.IsLiteral);
            Assert.Equal(expected: a.Gate[index].RightSource.RawValue, actual: b.Gate[index].RightSource.RawValue);
        }
    }

    [Theory]
    // A fraction rounds to even in an Int destination before the arithmetic, so 0.5 + 0.5 is 0 + 0 and 2.5 * 2 is 2 * 2.
    [InlineData("count = 0.5 + 0.5", "0.5 + 0.5", 0L)]
    [InlineData("count = 2.5 * 2", "2.5 * 2", 4L)]
    // An Int quotient truncates; a Fixed one keeps its fraction.
    [InlineData("count = limit / 2", "5 / 2", 2L)]
    [InlineData("speed = limit / 2", "5 / 2", (5L << 16) / 2L)]
    [InlineData("count = limit * 2 - 1", "5 * 2 - 1", 9L)]
    public void APuckAndAJsonSpellingOfOneConstantLowerToOneLiteral(string effect, string expression, long raw) {
        var (definition, json) = Compile(rules: $$"""
            rule fold {
              {{effect}}
            }
            """);
        var row = effect.Split(separator: ' ')[0];
        var respelled = Respell(effect: SetState(expression: expression, row: row), json: json);

        AssertSame(left: definition, right: respelled);
        Assert.True(condition: Source(rule: Lower(definition: definition).Rule).IsLiteral);
        Assert.Equal(expected: raw, actual: Source(rule: Lower(definition: definition).Rule).RawValue);
    }
    [Fact]
    public void AConstantSpellingCostsWhatItsLiteralCosts() {
        var (expression, _) = Compile(rules: """
            rule fold {
              count = limit * 2 - 1
            }
            """);
        var (literal, _) = Compile(rules: """
            rule fold {
              count = 9
            }
            """);

        AssertSame(left: expression, right: literal);
    }
    // A gate's constant side is a literal and a lone read is that read, so compareValue(level, 6 / 2) compiles and
    // prices as compareState(level, 3), and so does the folded source spelling.
    [Fact]
    public void AConstantComparandGatesLikeItsLiteral() {
        var (folded, json) = Compile(rules: """
            rule fold {
              when level < even / 2
              count += 1
            }
            """);
        var value = Respell(gate: new JsonObject { ["$type"] = "compareValue", ["comparison"] = "Less", ["left"] = Program(text: "level"), ["right"] = Program(text: "6 / 2") }, json: json);
        var state = Respell(gate: CompareState(value: 3), json: json);

        AssertSame(left: folded, right: value);
        AssertSame(left: state, right: value);
        Assert.True(condition: Lower(definition: value).Rule.Gate[0].LeftSource.IsOperand);
        Assert.True(condition: Lower(definition: value).Rule.Gate[0].RightSource.IsLiteral);
    }
    // An Int quotient a kind decides stays for the rule compiler, which still folds it at the comparison's kind.
    [Fact]
    public void AComparandAKindDecidesFoldsInTheRuleCompiler() {
        var (folded, json) = Compile(rules: """
            rule fold {
              when level < limit / 2
              count += 1
            }
            """);
        var state = Respell(gate: CompareState(value: 2), json: json);

        Assert.Equal(expected: "compareValue", actual: json["rules"]![0]!["gate"]!["$type"]!.GetValue<string>());
        AssertSame(left: folded, right: state);
    }
    // Expressions are eager, so a refusal in a choice's unselected arm is kept wherever the choice is spelled.
    [Fact]
    public void ARefusalInAnUnselectedArmIsKept() {
        var (definition, json) = Compile(rules: """
            rule fold {
              count = (1 == 1) ? 3 : (1 / 0)
            }
            """);
        var respelled = Respell(effect: SetState(expression: "(1 == 1) ? 3 : (1 / 0)", row: "count"), json: json);

        AssertSame(left: definition, right: respelled);
        Assert.True(condition: Source(rule: Lower(definition: definition).Rule).IsExpression);
    }
    [Fact]
    public void AConstantLocalReadsAsItsLiteral() {
        var (definition, _) = Compile(rules: """
            rule fold {
              local k = limit * 2
              count = k + 1
            }
            """);
        var source = Source(rule: Lower(definition: definition).Rule);

        Assert.True(condition: source.IsLiteral);
        Assert.Equal(expected: 11L, actual: source.RawValue);
    }
    [Fact]
    public void AnIfConstantsDecideIsTheBranchItChooses() {
        var (_, json) = Compile(rules: """
            rule fold {
              when limit > 3
              if limit > 3 {
                count = 1
              } else {
                level = 1
              }
            }
            """);
        var rule = json["rules"]![0]!.AsObject();

        Assert.False(condition: rule.ContainsKey(propertyName: "gate"));
        Assert.Equal(expected: "setState", actual: rule["effects"]![0]!["$type"]!.GetValue<string>());
        Assert.Equal(expected: "count", actual: rule["effects"]![0]!["state"]!.GetValue<string>());
        Assert.Single(collection: rule["effects"]!.AsArray());
    }
    // A family a template declares registers as the same lines written in place do, whether the template is called
    // among the document's statements or inside a state section.
    [InlineData("""
        template seats(n) {
          state {
            world {
              slot score[n] = 0
            }
          }
        }

        seats(3)
        """)]
    [InlineData("""
        template seats(n) {
          slot score[n] = 0
        }

        state {
          world {
            seats(3)
          }
        }
        """)]
    [Theory]
    public void AFamilyATemplateDeclaresIsRegistered(string declaration) {
        var (_, json) = Compile(rules: """

            rule fold {
              score[2] += 1
            }
            """, state: $$"""
            schema: "puck.world.definition.v1"
            documentId: "fold"

            {{declaration}}
            """);

        Assert.Contains(collection: json["state"]!["families"]!.AsArray(), filter: static family => (family!["name"]!.GetValue<string>() == "score"));
        Assert.NotEqual(expected: "score", actual: json["rules"]![0]!["effects"]![0]!["state"]!.GetValue<string>());
    }
}

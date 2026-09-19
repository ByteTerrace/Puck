using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class AuthoringSugarTests {
    private static (JsonObject Json, DiagnosticBag Diagnostics) Lower(string body) {
        var source = $"schema: \"puck.world.definition.v1\"\n\n{body}";
        var compilation = WorldCompiler.Compile(
            cancellationToken: TestContext.Current.CancellationToken,
            source: source
        );

        Assert.NotNull(@object: compilation.Json);

        return (compilation.Json, compilation.Diagnostics);
    }
    private static JsonArray WorldRows(JsonObject json) =>
        Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["world"]);

    [Fact]
    public void TableFamilyExpandsToNumberedTables() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table scores[4] : Int
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rows = WorldRows(json: json);
        var rowNames = rows.Select(selector: r => r?["name"]?.ToString()).ToHashSet();

        Assert.Contains(expected: "scores0", set: rowNames);
        Assert.Contains(expected: "scores1", set: rowNames);
        Assert.Contains(expected: "scores2", set: rowNames);
        Assert.Contains(expected: "scores3", set: rowNames);
        Assert.DoesNotContain(expected: "scores4", set: rowNames);
    }
    [Fact]
    public void PileFamilyExpandsAndDeclaresAFamilyWithLiveRowSpellingsKeptVerbatim() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table cardNames : Int {
                        c0 = 0
                        c1 = 1
                    }
                    pile tableau[4] of cardNames capacity(52)
                }
            }

            rule "move" {
                draw tableau[from] to tableau[to]
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rows = WorldRows(json: json);
        var rowNames = rows.Select(selector: r => r?["name"]?.ToString()).ToHashSet();

        Assert.Contains(expected: "tableau0", set: rowNames);
        Assert.Contains(expected: "tableau3", set: rowNames);

        var families = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["families"]);
        var family = Assert.Single(collection: families);
        var familyObj = Assert.IsType<JsonObject>(@object: family);

        Assert.Equal("tableau", familyObj["name"]?.ToString());
        Assert.Equal(4, familyObj["size"]?.GetValue<int>());

        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);

        Assert.Null(@object: rule["zones"]);

        var effects = Assert.IsType<JsonArray>(@object: rule["effects"]);

        Assert.Single(collection: effects);
        var eff = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("transformState", eff["$type"]?.ToString());
        var transform = Assert.IsType<JsonObject>(@object: eff["transform"]);

        Assert.Equal("transfer", transform["$type"]?.ToString());
        Assert.Equal("tableau[from]", transform["from"]?.ToString());
        Assert.Equal("tableau[to]", transform["to"]?.ToString());
    }
    [Fact]
    public void ConstantFamilyIndexLowersDirectly() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table meter[3] : Int
                }
            }

            rule "resetZero" {
                meter[0] = 100
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);
        var effects = Assert.IsType<JsonArray>(@object: rule["effects"]);
        var eff = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("meter0", eff["state"]?.ToString());
    }
    [Fact]
    public void RuleScopeInheritsPrefixAndWhenGate() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table phase : Int {
                        current = 0
                    }
                }
            }

            rules dealing when phase == 1 {
                rule "start" {
                    phase = 2
                }
                rule "step" when phase > 0 {
                    phase = 3
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);

        Assert.Equal(2, rules.Count);

        var r0 = Assert.IsType<JsonObject>(@object: rules[0]);

        Assert.Equal("dealing_start", r0["name"]?.ToString());
        var r0Gate = Assert.IsType<JsonObject>(@object: r0["gate"]);

        Assert.Equal("Equal", r0Gate["comparison"]?.ToString());

        var r1 = Assert.IsType<JsonObject>(@object: rules[1]);

        Assert.Equal("dealing_step", r1["name"]?.ToString());
        var r1Gate = Assert.IsType<JsonObject>(@object: r1["gate"]);

        Assert.Equal("all", r1Gate["$type"]?.ToString());
        var preds = Assert.IsType<JsonArray>(@object: r1Gate["predicates"]);

        Assert.Equal(2, preds.Count);
    }
    [Fact]
    public void StaticCollectionInitializersLowerCells() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table cards : Int = range(0, 5)
                    table cardNames : Int {
                        c0 = 0
                        c1 = 1
                    }
                    pile stock of cardNames = ["c0", "c1"]
                    table weights : Int = [1, 2, 4, 8]
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rows = WorldRows(json: json);
        var tableCards = rows.First(predicate: r => (r?["name"]?.ToString() == "cards"));
        var cardsCells = Assert.IsType<JsonArray>(@object: tableCards?["cells"]);

        Assert.Equal(5, cardsCells.Count);

        var tableWeights = rows.First(predicate: r => (r?["name"]?.ToString() == "weights"));
        var weightsCells = Assert.IsType<JsonArray>(@object: tableWeights?["cells"]);

        Assert.Equal(4, weightsCells.Count);
        Assert.Equal(1L, weightsCells[0]?["value"]?.GetValue<long>());
        Assert.Equal(8L, weightsCells[3]?["value"]?.GetValue<long>());

        var pileStock = rows.First(predicate: r => (r?["name"]?.ToString() == "stock"));
        var stockCells = Assert.IsType<JsonArray>(@object: pileStock?["cells"]);

        Assert.Equal(2, stockCells.Count);
    }
    [Fact]
    public void StabilizeLowersToAFixpointRuleGroupAndItsMemberRules() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table board : Int {
                        unstable = 1
                    }
                }
            }

            stabilize settleBoard maxPasses(32) until board.unstable == 0 {
                rule "collapse" {
                    board.unstable = 0
                }
            }
            """);

        Assert.DoesNotContain(collection: diag, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var group = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: json["ruleGroups"])));

        Assert.Equal("settleBoard", group["name"]?.ToString());
        Assert.Equal("Fixpoint", group["shape"]?.ToString());
        Assert.Equal("32", group["passes"]?.ToString());

        // `until` arms the group while its gate reads false, so it lowers as that gate's negation.
        Assert.Equal("not", Assert.IsType<JsonObject>(@object: group["trigger"])["$type"]?.ToString());

        var step = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: group["steps"])));

        Assert.Equal("settleBoard_collapse", step["rule"]?.ToString());

        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: json["rules"])));

        Assert.Equal("settleBoard_collapse", rule["name"]?.ToString());
    }
    [Fact]
    public void WorkflowLowersToAStagedRuleGroupWhoseStepsAreItsRules() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table combat : Int {
                        active = 1
                    }
                }
            }

            workflow turn {
                step beginTurn {
                    combat.active = 1
                }
                step endTurn skip {
                    combat.active = 0
                }
            }
            """);

        Assert.DoesNotContain(collection: diag, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var group = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: json["ruleGroups"])));

        Assert.Equal("turn", group["name"]?.ToString());
        Assert.Equal("Staged", group["shape"]?.ToString());
        Assert.Null(@object: group["passes"]);

        var steps = Assert.IsType<JsonArray>(@object: group["steps"]);

        Assert.Equal(2, steps.Count);
        Assert.Equal("turn_beginTurn", steps[0]?["rule"]?.ToString());
        Assert.Null(@object: steps[0]?["onRefusal"]);
        Assert.Equal("turn_endTurn", steps[1]?["rule"]?.ToString());
        Assert.Equal("Skip", steps[1]?["onRefusal"]?.ToString());

        var ruleNames = Assert.IsType<JsonArray>(@object: json["rules"]).Select(selector: r => r?["name"]?.ToString()).ToList();

        Assert.Equal(actual: ruleNames, expected: ["turn_beginTurn", "turn_endTurn"]);
    }
    [Fact]
    public void AWorkflowRepeatStepIsRefusedBecauseAStagedCursorCarriesNoRepeat() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table combat : Int {
                        active = 1
                    }
                }
            }

            workflow turn {
                repeatStep resolveCombat until combat.active == 0 {
                    combat.active = 0
                }
            }
            """);

        var error = Assert.Single(collection: diag, predicate: d => (d.Severity == DiagnosticSeverity.Error));

        Assert.Equal(PuckDiagnosticCodes.RuleGroupShapeInadmissible, error.Code);
        Assert.Contains("repeatStep", error.Message, StringComparison.Ordinal);
        Assert.Null(@object: json["ruleGroups"]);
    }
    [Fact]
    public void AFamilyMemberListCarriesItsGapsIntoTheDocument() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table Pile[0, 2..4] : Int
                }
            }
            """);

        Assert.DoesNotContain(collection: diag, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var rowNames = WorldRows(json: json).Select(selector: r => r?["name"]?.ToString()).ToList();

        Assert.Equal(actual: rowNames, expected: ["Pile0", "Pile2", "Pile3", "Pile4"]);

        var family = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["families"])));

        Assert.Equal("Pile", family["name"]?.ToString());
        Assert.Equal("4", family["size"]?.ToString());
        Assert.Equal([0, 2, 3, 4], Assert.IsType<JsonArray>(@object: family["indices"]).Select(selector: i => ((int)i!)).ToList());
    }
    [Fact]
    public void ABareFamilyCountKeepsTheNumberedRowLoweringAndDeclaresAFamily() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table scores[3] : Int
                }
            }
            """);

        Assert.DoesNotContain(collection: diag, filter: d => (d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(["scores0", "scores1", "scores2"], WorldRows(json: json).Select(selector: r => r?["name"]?.ToString()).ToList());

        var families = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["families"]);
        var family = Assert.IsType<JsonObject>(@object: Assert.Single(collection: families));

        Assert.Equal("scores", family["name"]?.ToString());
        Assert.Equal(3, family["size"]?.GetValue<int>());
        Assert.Null(@object: family["indices"]);
        Assert.Null(@object: family["members"]);
    }
    [Fact]
    public void EnumLowersToIntegerConstants() {
        var (json, diag) = Lower(body: """
            state {
                enum Suit {
                    Clubs,
                    Diamonds,
                    Hearts,
                    Spades
                }
                world {
                    table trump : Int {
                        suit = Suit.Diamonds
                    }
                }
            }

            rule "setSuit" when trump.suit == Suit.Diamonds {
                trump.suit = Suit.Spades
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rows = WorldRows(json: json);
        var trumpTable = rows.First(predicate: r => (r?["name"]?.ToString() == "trump"));
        var cells = Assert.IsType<JsonArray>(@object: trumpTable?["cells"]);

        Assert.Equal(1L, cells[0]?["value"]?.GetValue<long>());

        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);
        var gate = Assert.IsType<JsonObject>(@object: rule["gate"]);

        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("trump", gate["state"]?.ToString());
        Assert.Equal("suit", gate["key"]?.ToString());
        Assert.Equal(1m, gate["value"]?.GetValue<decimal>());

        var effects = Assert.IsType<JsonArray>(@object: rule["effects"]);
        var eff = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal(3m, eff["value"]?.GetValue<decimal>());
    }
    [Fact]
    public void RecordExpandsToSoATablesAndRewritesFieldAccesses() {
        var (json, diag) = Lower(body: """
            state {
                enum Suit {
                    Clubs,
                    Diamonds
                }
                record Card {
                    suit: Suit
                    rank: Int
                }
                world {
                    table cards[4] : Card
                }
            }

            rule "play" when cards[0].suit == Suit.Diamonds {
                cards[0].rank = 10
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rows = WorldRows(json: json);
        var rowNames = rows.Select(selector: r => r?["name"]?.ToString()).ToHashSet();

        Assert.Contains(expected: "cards_suit", set: rowNames);
        Assert.Contains(expected: "cards_rank", set: rowNames);

        var cardsSuit = rows.First(predicate: r => (r?["name"]?.ToString() == "cards_suit"));
        var suitCells = Assert.IsType<JsonArray>(@object: cardsSuit?["cells"]);

        Assert.Equal(4, suitCells.Count);

        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);
        var gate = Assert.IsType<JsonObject>(@object: rule["gate"]);

        Assert.Equal("compareState", gate["$type"]?.ToString());
        Assert.Equal("cards_suit", gate["state"]?.ToString());
        Assert.Equal("0", gate["key"]?.ToString());
        Assert.Equal(1m, gate["value"]?.GetValue<decimal>());

        var effects = Assert.IsType<JsonArray>(@object: rule["effects"]);
        var eff = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("cards_rank", eff["state"]?.ToString());
        Assert.Equal("0", eff["key"]?.ToString());
        Assert.Equal(10m, eff["value"]?.GetValue<decimal>());
    }
    [Fact]
    public void DerivedStateInlinesMacroExpressions() {
        var (json, diag) = Lower(body: """
            state {
                world {
                    table board : Int {
                        cell0 = 5
                    }
                }
                derive hasTokens = board.cell0 > 0
            }

            rule "claim" when hasTokens == true {
                board.cell0 = 0
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);
        var gate = Assert.IsType<JsonObject>(@object: rule["gate"]);

        Assert.Equal("board[cell0] > 0", WorldExpressionJson.Text(node: gate["left"]));
    }
    // A collection operation over a family unrolls into gate text (`and`-joined), which the expression grammar a
    // comparison operand is read with does not spell. Lowering refuses it rather than writing an operand no rule
    // can compile.
    [Fact]
    public void CollectionOperationsUnrollIntoTextAnOperandCannotCarry() {
        var (_, diag) = Lower(body: """
            state {
                world {
                    table scores[3] : Int
                }
            }

            rule "checkAllZero" when all(scores, s -> s == 0) == true {
                scores[0] = 1
            }
            """);

        Assert.True(condition: diag.HasErrors);
        Assert.Contains(
            actualString: diag.FormatReport(""),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "PUCK002"
        );
    }
}


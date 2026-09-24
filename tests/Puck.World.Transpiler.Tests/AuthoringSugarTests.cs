using System.Text.Json.Nodes;
using Puck.World.Transpiler.Decompiler;
using Puck.State;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class AuthoringSugarTests {
    [Fact]
    public void RuntimeRecordEnumIsMaterializedAtItsFieldDeclaration() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                enum Facing { North South }
                record Unit { facing: Facing = North }
                pool units of Unit capacity(1)
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var state = Assert.IsType<JsonObject>(@object: json["state"]);
        var runtimeEnum = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: state["enums"])));

        Assert.Equal("Facing", runtimeEnum["name"]?.ToString());
        Assert.Equal(["North", "South"], Assert.IsType<JsonArray>(@object: runtimeEnum["members"]).Select(selector: static member => member!.ToString()));
    }
    [Fact]
    public void PoolNamedPairUsesOrdinaryClaimSyntax() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                record Item { value: Int }
                pool pair of Item capacity(1)
            }
            rule "take" { claim pair as item { item.value = 1 } }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);

        Assert.Equal("claim", Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["effects"])[0])["$type"]?.ToString());
    }
    [Fact]
    public void BoundsAdmitOnlyTheCanonicalRangeSpelling() {
        var (closed, closedDiagnostics) = WorldSources.Lower(body: "state { world { slot score = 0 bounds(0..10) } }");
        var (minimumOnly, minimumDiagnostics) = WorldSources.Lower(body: "state { world { slot score = 0 bounds(0..) } }");
        var (maximumOnly, maximumDiagnostics) = WorldSources.Lower(body: "state { world { slot score = 0 bounds(..10) } }");
        var (_, namedDiagnostics) = WorldSources.Lower(body: "state { world { slot score = 0 bounds(minimum: 0, maximum: 10) } }");

        Assert.False(condition: closedDiagnostics.HasErrors, userMessage: closedDiagnostics.FormatReport(""));
        Assert.False(condition: minimumDiagnostics.HasErrors, userMessage: minimumDiagnostics.FormatReport(""));
        Assert.False(condition: maximumDiagnostics.HasErrors, userMessage: maximumDiagnostics.FormatReport(""));
        Assert.Equal(0L, WorldRows(json: closed)[0]!["min"]!.GetValue<long>());
        Assert.Equal(10L, WorldRows(json: closed)[0]!["max"]!.GetValue<long>());
        Assert.Null(@object: WorldRows(json: minimumOnly)[0]!["max"]);
        Assert.Null(@object: WorldRows(json: maximumOnly)[0]!["min"]);
        Assert.True(condition: namedDiagnostics.HasErrors);
    }
    [Fact]
    public void FractionalBoundsInferAFixedRow() {
        var (json, diagnostics) = WorldSources.Lower(body: "state { world { table recalled capacity(3) bounds(-1.0..1.0) } }");

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        Assert.Equal("Fixed", WorldRows(json: json)[0]!["kind"]?.ToString());
    }

    private static JsonArray WorldRows(JsonObject json) =>
        Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["state"])["world"]);

    [Fact]
    public void RecordAndPoolDeclarationsLowerTypedSeeds() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                record Player {
                    name: Text = "guest"
                    score: Int bounds(0..10) advance(perSecond: -1)
                }
                pool players of Player capacity(2) = [{ name: "Ada", score: 3 }]
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var state = Assert.IsType<JsonObject>(@object: json["state"]);
        var records = Assert.IsType<JsonArray>(@object: state["records"]);
        var fields = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: records[0])["fields"]);

        Assert.Equal("Text", Assert.IsType<JsonObject>(@object: fields[0])["kind"]?.ToString());
        var defaultValue = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: fields[0])["default"]);

        Assert.Equal("Text", defaultValue["kind"]?.ToString());
        Assert.Equal("guest", defaultValue["value"]?.ToString());
        Assert.Equal(-1L, Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: fields[1])["advance"])["perSecondNumerator"]?.GetValue<long>());
        var pools = Assert.IsType<JsonArray>(@object: state["pools"]);
        var pool = Assert.IsType<JsonObject>(@object: pools[0]);

        Assert.Equal("Player", pool["record"]?.ToString());
        Assert.Equal(2, pool["capacity"]?.GetValue<int>());
        Assert.Equal("name", Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: pool["initial"])[0])["values"])[0])["field"]?.ToString());
        var seedValue = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: pool["initial"])[0])["values"])[0])["value"]);

        Assert.Equal("Text", seedValue["kind"]?.ToString());
        Assert.Equal("Ada", seedValue["value"]?.ToString());
    }
    [Fact]
    public void PoolEffectsKeepLexicalAliasFieldReferences() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                record Player { score: Int }
                pool players of Player capacity(1)
            }
            rule "award" {
                claim players as player {
                    player.score = 1
                    release player
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var effects = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0])["effects"]);
        var claim = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("claim", claim["$type"]?.ToString());
        var nested = Assert.IsType<JsonArray>(@object: claim["effects"]);

        Assert.Equal("player.score", StateChannelRefJsonConverter.FromNode(node: Assert.IsType<JsonObject>(@object: nested[0])["state"]).Spelling);
        Assert.Equal("release", Assert.IsType<JsonObject>(@object: nested[1])["$type"]?.ToString());
    }
    [Fact]
    public void RuleHeaderIteratesPoolAndCountReadsItsLiveDomain() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                record Player { score: Int }
                pool players of Player capacity(2)
            }
            rule "award" for each player in players {
                local live = count(players)
                player.score = live
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);

        Assert.Equal("players", Assert.IsType<JsonObject>(@object: rule["poolForEach"])["pool"]?.ToString());
        Assert.Equal("player", Assert.IsType<JsonObject>(@object: rule["poolForEach"])["binding"]?.ToString());
        var expression = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["locals"])[0])["expression"]);
        var program = ExpressionProgramJsonConverter.FromNode(node: expression);
        var operand = Assert.IsType<InstructionPayload.State>(@object: Assert.Single(collection: program.Instructions).Payload);

        Assert.Equal("$reduce:count:players", operand.Name.Spelling);
    }
    [Fact]
    public void NamedPoolForeachLowersNestedEffects() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                record Player { score: Int }
                pool players of Player capacity(2)
            }
            rule "reset" {
                for each player in players {
                    player.score = 0
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var effects = Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0])["effects"]);
        var each = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("forEachPool", each["$type"]?.ToString());
        Assert.Equal("players", each["pool"]?.ToString());
    }
    [Fact]
    public void NestedPoolIterationGateRetainsItsLexicalFieldOperand() {
        var (json, diagnostics) = WorldSources.Lower(body: """
            state {
                record Piece { mover: Int }
                pool pieces of Piece capacity(1)
            }
            rule "move" {
                for each piece in pieces {
                    if piece.mover == 1 {
                        release piece
                    }
                }
            }
            """);

        Assert.False(condition: diagnostics.HasErrors, userMessage: diagnostics.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);
        var each = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: rule["effects"])[0]);
        var branch = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: each["effects"])[0]);
        var condition = Assert.IsType<JsonObject>(@object: branch["condition"]);
        var left = Assert.IsType<JsonObject>(@object: condition["left"]);
        var instruction = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: left["instructions"])));

        Assert.Equal("piece.mover", StateChannelRefJsonConverter.FromNode(node: instruction["name"]).Spelling);
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: json.ToJsonString()));

        var (compiled, _) = WorldFactsCompiler.CompileDocument(definition: definition);

        Assert.Single(collection: compiled);
    }
    [Fact]
    public void NestedClaimAndForeachKeepEachLexicalBinding() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                record Player { score: Int }
                pool players of Player capacity(2)
            }
            rule "awardAll" {
                claim players as winner {
                    for each player in players {
                        player.score = 0
                        winner.score = 1
                    }
                }
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var claim = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0])["effects"])[0]);
        var each = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: claim["effects"])[0]);
        var nested = Assert.IsType<JsonArray>(@object: each["effects"]);

        Assert.Equal("player.score", StateChannelRefJsonConverter.FromNode(node: Assert.IsType<JsonObject>(@object: nested[0])["state"]).Spelling);
        Assert.Equal("winner.score", StateChannelRefJsonConverter.FromNode(node: Assert.IsType<JsonObject>(@object: nested[1])["state"]).Spelling);
    }
    [Fact]
    public void StaticPoolFieldAccessUsesSlotThenFieldWithoutRewritingTextLiterals() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                record Fighter { frags: Int = 0 }
                pool fighters of Fighter capacity(2) = [{ frags: 0 }, { frags: 0 }]
            }
            rule "score" when fighters[0].frags == 0 {
                fighters[1].frags = 1
                total = fighters[0].frags + 1
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rule = Assert.IsType<JsonObject>(@object: Assert.IsType<JsonArray>(@object: json["rules"])[0]);
        var gate = Assert.IsType<JsonObject>(@object: rule["gate"]);

        var left = Assert.IsType<JsonObject>(@object: gate["left"]);
        var instruction = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: left["instructions"])));

        Assert.Equal("fighters[0].frags", StateChannelRefJsonConverter.FromNode(node: instruction["name"]).Spelling);
        var effects = Assert.IsType<JsonArray>(@object: rule["effects"]);

        var write = Assert.IsType<JsonObject>(@object: effects[0]);

        Assert.Equal("fighters[1].frags", StateChannelRefJsonConverter.FromNode(node: write["state"]).Spelling);
        Assert.Null(@object: write["key"]);
        var expression = ExpressionProgramJsonConverter.FromNode(node: Assert.IsType<JsonObject>(@object: effects[1])["expression"]);
        var state = Assert.IsType<InstructionPayload.State>(@object: expression.Instructions[0].Payload);

        Assert.Equal("fighters[0].frags", state.Name.Spelling);
        Assert.Contains(
            actualString: WorldDecompiler.Decompile(root: json),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "fighters[1].frags = 1"
        );
    }
    [Fact]
    public void IdentitySelectsNamespacedCapacityOneRecordPools() {
        var (json, diag) = WorldSources.Lower(body: """
            identity {
                id: "hero"
                name: "Hero"
                color: "#ffffff"
                moveSpeedState: motion.move
                turnSpeedState: motion.turn
                records [identity.profile]
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        Assert.Equal("identity.profile", Assert.IsType<JsonArray>(@object: Assert.IsType<JsonObject>(@object: json["identity"])["records"])[0]?.ToString());
    }
    [Fact]
    public void TableFamilyExpandsToNumberedTables() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table scores[4]
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table cardNames {
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table meter[3]
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table phase {
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

        Assert.Equal("dealing$start", r0["name"]?.ToString());
        var r0Gate = Assert.IsType<JsonObject>(@object: r0["gate"]);

        Assert.Equal("Equal", r0Gate["comparison"]?.ToString());

        var r1 = Assert.IsType<JsonObject>(@object: rules[1]);

        Assert.Equal("dealing$step", r1["name"]?.ToString());
        var r1Gate = Assert.IsType<JsonObject>(@object: r1["gate"]);

        Assert.Equal("all", r1Gate["$type"]?.ToString());
        var preds = Assert.IsType<JsonArray>(@object: r1Gate["predicates"]);

        Assert.Equal(2, preds.Count);
    }
    [Fact]
    public void StaticCollectionInitializersLowerCells() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table cards = range(0, 5)
                    table cardNames {
                        c0 = 0
                        c1 = 1
                    }
                    pile stock of cardNames = ["c0", "c1"]
                    table weights = [1, 2, 4, 8]
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table board {
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

        Assert.Equal("settleBoard$collapse", step["rule"]?.ToString());

        var rule = Assert.IsType<JsonObject>(@object: Assert.Single(collection: Assert.IsType<JsonArray>(@object: json["rules"])));

        Assert.Equal("settleBoard$collapse", rule["name"]?.ToString());
    }
    // A group declared between rules decompiles where its members stand, so the source it prints compiles back to the
    // same rules array rather than one with the group's members moved to the front.
    [Fact]
    public void AGroupBetweenRulesDecompilesWhereItStood() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    slot before = 0
                    slot inside = 0
                    slot after = 0
                }
            }

            rule "first" {
                before = 1
            }

            stabilize settle {
                rule "middle" {
                    inside = 1
                }
            }

            rule "last" {
                after = 1
            }
            """);

        Assert.DoesNotContain(collection: diag, filter: d => (d.Severity == DiagnosticSeverity.Error));

        var printed = WorldDecompiler.Decompile(root: json);
        var again = WorldSources.LowerSourceClean(source: printed);

        Assert.Equal(
            expected: json["rules"]!.AsArray().Select(selector: static rule => rule!["name"]!.ToString()),
            actual: again["rules"]!.AsArray().Select(selector: static rule => rule!["name"]!.ToString())
        );
        Assert.True(condition: (printed.IndexOf(comparisonType: StringComparison.Ordinal, value: "rule \"first\"") < printed.IndexOf(comparisonType: StringComparison.Ordinal, value: "stabilize settle")), userMessage: printed);
    }
    [Fact]
    public void WorkflowLowersToAStagedRuleGroupWhoseStepsAreItsRules() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table combat {
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
        Assert.Equal("turn$beginTurn", steps[0]?["rule"]?.ToString());
        Assert.Null(@object: steps[0]?["onRefusal"]);
        Assert.Equal("turn$endTurn", steps[1]?["rule"]?.ToString());
        Assert.Equal("Skip", steps[1]?["onRefusal"]?.ToString());

        var ruleNames = Assert.IsType<JsonArray>(@object: json["rules"]).Select(selector: r => r?["name"]?.ToString()).ToList();

        Assert.Equal(actual: ruleNames, expected: ["turn$beginTurn", "turn$endTurn"]);
    }
    [Fact]
    public void AWorkflowRepeatStepIsRefusedBecauseAStagedCursorCarriesNoRepeat() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table combat {
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table Pile[0, 2..4]
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table scores[3]
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
        var (json, diag) = WorldSources.Lower(body: """
            state {
                enum Suit {
                    Clubs,
                    Diamonds,
                    Hearts,
                    Spades
                }
                world {
                    table trump {
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
    public void DerivedStateInlinesMacroExpressions() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table board {
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
    // Family folds retain their runtime subprogram instead of expanding into gate-language text.
    [Fact]
    public void CollectionOperationsRetainTheirFoldSubprogram() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                world {
                    table scores[3]
                }
            }

            rule "checkAllZero" when all(scores, s -> s == 0) == true {
                scores[0] = 1
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var program = json["rules"]![0]!["gate"]!["left"]!;

        Assert.Equal("All", program["instructions"]![0]!["op"]!.GetValue<string>());
        Assert.Single(collection: program["subprograms"]!.AsArray());
    }
    [Fact]
    public void EnumResolvesInRuleLocalExpressions() {
        var (json, diag) = WorldSources.Lower(body: """
            state {
                enum MoveKind {
                    None
                    Quiet
                    Capture
                }
                world {
                    slot kind = 0
                }
            }

            rule "setKind" {
                local k = MoveKind.Capture
                kind = k
            }
            """);

        Assert.False(condition: diag.HasErrors, userMessage: diag.FormatReport(""));
        var rules = Assert.IsType<JsonArray>(@object: json["rules"]);
        var rule = Assert.IsType<JsonObject>(@object: rules[0]);
        var locals = Assert.IsType<JsonArray>(@object: rule["locals"]);
        var local = Assert.IsType<JsonObject>(@object: locals[0]);
        var expr = Assert.IsType<JsonObject>(@object: local["expression"]);
        var instructions = Assert.IsType<JsonArray>(@object: expr["instructions"]);
        var instr = Assert.IsType<JsonObject>(@object: instructions[0]);

        Assert.Equal("Constant", instr["op"]?.ToString());
        Assert.Equal(2m, instr["value"]?.GetValue<decimal>());
    }
}

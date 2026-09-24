using Puck.Transpiler.Ast;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ParserSugarTests {
    private static RuleBlockNode FirstRule(DocumentNode doc) => Assert.IsType<RuleBlockNode>(@object: doc.Statements[0]);

    [Fact]
    public void AddCellStatement() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                solitaireFreecell[moves] += 1
            }
            """);
        var add = Assert.IsType<AddCellStatementNode>(@object: FirstRule(doc: doc).Statements[0]);

        Assert.Equal(
            "solitaireFreecell",
            add.Target.Name
        );
        Assert.Equal(
            "moves",
            add.Target.Key
        );
        Assert.IsType<RhsOperandNode>(@object: add.Rhs);
    }

    private static readonly Dictionary<string, Refusal> Refusals = new(comparer: StringComparer.Ordinal) {
        ["PUCK004: a chained comparison"] = new(
            Body: "rule \"r\" {\n    when a == 1 == 2\n    flag = 1\n}\n",
            Code: "PUCK004",
            Needle: "when a == 1 == 2"
        ),
        ["PUCK009: text added to a cell"] = new(
            Body: "rule \"r\" {\n    hp += \"oops\"\n}\n",
            Code: "PUCK009",
            Needle: "hp += \"oops\""
        ),
        ["PUCK010: a schedule delay without its seconds suffix"] = new(
            Body: "rule \"r\" {\n    schedule respawnAt in 2\n}\n",
            Code: "PUCK010",
            Needle: "schedule respawnAt in 2"
        ),
        ["PUCK012: a second when clause"] = new(
            Body: "rule \"r\" {\n    when a == 1\n    when b == 2\n    flag = 1\n}\n",
            Code: "PUCK012",
            Needle: "when b == 2"
        ),
        ["PUCK013: an option without a score"] = new(
            Body: "rule \"r\" {\n    decision {\n        periodSeconds: 1s\n        option \"a\" {\n            when hp == 1\n        }\n    }\n    flag = 1\n}\n",
            Code: "PUCK013",
            Needle: "option \"a\""
        ),
        ["PUCK014: a second onFailure"] = new(
            Body: "rule \"r\" {\n    transaction {\n        flag = 1\n    }\n    onFailure {\n        a = 1\n    }\n    onFailure {\n        b = 1\n    }\n}\n",
            Code: "PUCK014",
            Needle: "onFailure"
        ),
        ["PUCK019: a transaction inside a transaction"] = new(
            Body: "rule \"r\" {\n    transaction {\n        flag = 1\n        transaction {\n            other = 2\n        }\n    }\n}\n",
            Code: "PUCK019",
            Needle: "transaction"
        ),
        ["PUCK026: a rule with no effects"] = new(
            Body: "rule \"empty\" {\n    when a == 1\n}\n",
            Code: "PUCK026",
            Needle: "rule \"empty\""
        ),
        ["PUCK028: a bare and an explicit solid"] = new(
            Body: "placements {\n    placement \"debugRoom\" {\n        prototype: debugRoom\n        solid\n        solid { margin: 0.2 }\n    }\n}\n",
            Code: "PUCK028",
            Needle: "placement \"debugRoom\""
        ),
        ["PUCK029: a decision without periodSeconds"] = new(
            Body: "rule \"r\" {\n    decision {\n        option \"a\" {\n            score: 1\n        }\n    }\n    flag = 1\n}\n",
            Code: "PUCK029",
            Needle: "decision"
        ),
        ["PUCK106: a colon channel written in code"] = new(
            Body: "rule \"r\" {\n    when $physics:quiescent == 1\n    hp += 1\n}\n",
            Code: "PUCK106",
            Needle: "$physics:quiescent"
        ),
        ["PUCK108: a local that spells its kind"] = new(
            Body: "rule \"r\" {\n    local dx : Int = 5\n    flag = dx\n}\n",
            Code: "PUCK108",
            Needle: "local dx : Int = 5"
        ),
    };

    public static TheoryData<string> RefusalNames() => new(values: Refusals.Keys);
    [MemberData(nameof(RefusalNames))]
    [Theory]
    public void ARefusedSugarStatementNamesItsCodeAndLine(string name) {
        var refusal = Refusals[name];

        var (document, diagnostics) = WorldSources.Parse(body: refusal.Body);

        // A refusal is drawn about a tree the parser still recovered.
        Assert.NotNull(@object: document);
        WorldSources.AssertRefusedBy(
            diagnostics: diagnostics,
            label: name,
            refusal: refusal
        );
    }
    [Fact]
    public void AColonChannelInsideAStringLiteralIsNotRefused() {
        var (_, admitted) = WorldSources.Parse(body: """
            rule "r" {
                when similarity(mood, embed("$physics:quiescent")) > 0
                hp += 1
            }
            """);

        Assert.DoesNotContain(
            collection: admitted,
            filter: d => (d.Code == "PUCK106")
        );
    }
    [Fact]
    public void AScheduleWithoutItsSecondsSuffixStillReadsItsDelay() {
        var (document, _) = WorldSources.Parse(body: """
            rule "r" {
                schedule respawnAt in 2
            }
            """);
        var schedule = Assert.IsType<ScheduleStatementNode>(@object: FirstRule(doc: document!).Statements[0]);

        Assert.Equal(
            2m,
            schedule.DelaySeconds
        );
    }
    // `: Int` and `as Int` are one annotation: it forces compareValue and is stripped from the operand it follows.
    [InlineData(": Int")]
    [InlineData("as Int")]
    [Theory]
    public void AKindSuffixForcesCompareValueAndIsStrippedFromItsOperand(string suffix) {
        var doc = WorldSources.ParseClean(body: $$"""
            rule "solitaireFreecell-move" {
                when solitaireFreecell[from] != solitaireFreecell[to] {{suffix}}
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            "solitaireFreecell[to]",
            cmp.Right.Text
        );
        Assert.Equal(
            "Int",
            cmp.Kind
        );
    }
    // Inside a rule body a bracket after a name is its cell key, and a `$zones[...]` row reference keeps its own
    // brackets while the key after it folds to a cell channel.
    [InlineData("hp[$each] = 1", "hp", "$each")]
    [InlineData("$zones[solitaireFreecell[from]][solitaireFreecell[card]] = 1", "$zones[solitaireFreecell[from]]", "$cell:solitaireFreecell:card")]
    [Theory]
    public void ABracketAfterANameInsideARuleBodyIsItsCellKey(string statement, string name, string key) {
        var doc = WorldSources.ParseClean(body: $$"""
            rule "r" {
                {{statement}}
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(@object: FirstRule(doc: doc).Statements[0]);

        Assert.Equal(
            name,
            set.Target.Name
        );
        Assert.Equal(
            key,
            set.Target.Key
        );
    }
    [Fact]
    public void BackquotedReplacePropertyUsesGenericPropertyPath() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                `$replace`: true
                flag = 1
            }
            """);
        var rule = FirstRule(doc: doc);
        var prop = Assert.IsType<PropertyNode>(@object: rule.Statements[0]);

        Assert.Equal(
            "$replace",
            prop.Name
        );
        var literal = Assert.IsType<LiteralExpressionNode>(@object: prop.Value);

        Assert.Equal(
            true,
            literal.Value
        );
    }
    // §1 — gates

    [Fact]
    public void BareComparisonGate() {
        var doc = WorldSources.ParseClean(body: """
            rule "tabletop-settle-hold-reset" {
                when physics(quiescent) != 1
                flag = 1
            }
            """);
        var rule = FirstRule(doc: doc);
        var when = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            "physics(quiescent)",
            cmp.Left.Text
        );
        Assert.Equal(
            "!=",
            cmp.Comparator
        );
        Assert.Equal(
            "1",
            cmp.Right.Text
        );
        Assert.Null(@object: cmp.Kind);
    }
    [Fact]
    public void BarePlacementSolidFlagBecomesFlagStatement() {
        var doc = WorldSources.ParseClean(body: """
            placements {
                placement "debugRoom" {
                    prototype: debugRoom
                    parent: provingCourt
                    position [0, 0, 0]
                    solid
                    grip { holdable: true }
                }
            }
            """);
        var placements = Assert.IsType<BlockNode>(@object: doc.Statements[0]);
        var placement = Assert.IsType<BlockNode>(@object: placements.Statements[0]);
        var flag = Assert.IsType<FlagStatementNode>(@object: placement.Statements[3]);

        Assert.Equal(
            "solid",
            flag.Name
        );
        var grip = Assert.IsType<BlockNode>(@object: placement.Statements[4]);

        Assert.Equal(
            "grip",
            grip.Identifier
        );
    }
    // §3 — rule / decision / option / local

    [Fact]
    public void LocalCapturesExpressionVerbatim() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                local dx = hp - 1
                flag = dx
            }
            """);
        var local = Assert.IsType<LocalStatementNode>(@object: FirstRule(doc: doc).Statements[0]);

        Assert.Equal(
            "dx",
            local.Name
        );
        Assert.Equal(
            "hp - 1",
            local.Expression.Text
        );
    }
    [Fact]
    public void ComparandRowGateStaysCompareStateShaped() {
        var doc = WorldSources.ParseClean(body: """
            rule "solitaireFreecell-source" {
                when solitaireFreecell[request] != solitaireFreecell[applied]
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            "solitaireFreecell[request]",
            cmp.Left.Text
        );
        Assert.Equal(
            "solitaireFreecell[applied]",
            cmp.Right.Text
        );
        Assert.Null(@object: cmp.Kind);
    }
    [Fact]
    public void DecisionWithOptionInterruptAndOnNoChoice() {
        var doc = WorldSources.ParseClean(body: """
            rule "choose-companion" {
                forEach: hound
                decision {
                    periodSeconds: 1s
                    commitmentSeconds: 3s
                    interrupt hp == 0
                    option "follow" {
                        when hound[$right] == 1
                        score: trust * 2
                    }
                    onNoChoice {
                        idle = 1
                    }
                }
                flag = 1
            }
            """);
        var rule = FirstRule(doc: doc);
        var decision = Assert.IsType<DecisionBlockNode>(@object: rule.Statements.OfType<DecisionBlockNode>().Single());

        var periodSeconds = Assert.IsType<PropertyNode>(@object: decision.Statements[0]);

        Assert.Equal(
            "periodSeconds",
            periodSeconds.Name
        );

        var interrupt = decision.Statements.OfType<InterruptStatementNode>().Single();

        Assert.IsType<ComparisonPredicateNode>(@object: interrupt.Predicate);

        var option = decision.Statements.OfType<OptionBlockNode>().Single();

        Assert.Equal(
            "follow",
            option.Name
        );
        var optionWhen = Assert.IsType<WhenStatementNode>(@object: option.Statements[0]);

        Assert.IsType<ComparisonPredicateNode>(@object: optionWhen.Predicate);
        var score = Assert.IsType<ScoreStatementNode>(@object: option.Statements[1]);

        Assert.Equal(
            "trust * 2",
            score.Expression.Text
        );

        var onNoChoice = decision.Statements.OfType<OnNoChoiceBlockNode>().Single();

        Assert.Single(collection: onNoChoice.Effects);
    }
    [Fact]
    public void ExpressionOperandKeepsFullTextWithKindSuffixStripped() {
        var doc = WorldSources.ParseClean(body: """
            rule "rumor" {
                when boneHolderClaimMark[$each] != boneHolder[0] << 32 | claimSeq : Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            "boneHolderClaimMark[$each]",
            cmp.Left.Text
        );
        Assert.Equal(
            "boneHolder[0] << 32 | claimSeq",
            cmp.Right.Text
        );
        Assert.Equal(
            "Int",
            cmp.Kind
        );
    }
    [Fact]
    public void FlatFourWayConjunctionStaysOneAndNode() {
        var doc = WorldSources.ParseClean(body: """
            rule "solitaireFreecell-move" {
                when solitaire[table] == 3 and solitaireFreecell[request] != solitaireFreecell[applied] and solitaireFreecell[stage] == 0 and solitaireFreecell[from] != solitaireFreecell[to] : Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            4,
            and.Operands.Count
        );
        Assert.All(
            and.Operands,
            static o => Assert.IsType<ComparisonPredicateNode>(@object: o)
        );
    }
    // Ambiguities named in the spec

    [Fact]
    public void InlineArrayPropertySugarStillWorksOutsideRuleBody() {
        var doc = WorldSources.ParseClean(body: """
            host {
                tags [1, 2, 3]
            }
            """);
        var host = Assert.IsType<BlockNode>(@object: doc.Statements[0]);
        var prop = Assert.IsType<PropertyNode>(@object: host.Statements[0]);

        Assert.Equal(
            "tags",
            prop.Name
        );
        Assert.IsType<ArrayExpressionNode>(@object: prop.Value);
    }
    [Fact]
    public void NotWrappingComparisonInsideAll() {
        var doc = WorldSources.ParseClean(body: """
            rule "tabletop-derive-cell-tilted" {
                when settleHold == 60 and not upright(placement, $each) >= 0.5
                flag = 1
            }
            """);
        var rule = FirstRule(doc: doc);
        var when = Assert.IsType<WhenStatementNode>(@object: rule.Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            2,
            and.Operands.Count
        );
        var left = Assert.IsType<ComparisonPredicateNode>(@object: and.Operands[0]);

        Assert.Equal(
            "settleHold",
            left.Left.Text
        );
        var not = Assert.IsType<NotPredicateNode>(@object: and.Operands[1]);
        var negated = Assert.IsType<ComparisonPredicateNode>(@object: not.Operand);

        Assert.Equal(
            "upright(placement, $each)",
            negated.Left.Text
        );
        Assert.Equal(
            ">=",
            negated.Comparator
        );
    }
    [Fact]
    public void ParenthesizedSubgateStaysOneOpaqueChild() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                when (a == 1 and b == 2) or c == 3
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var or = Assert.IsType<OrPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            2,
            or.Operands.Count
        );
        var nested = Assert.IsType<AndPredicateNode>(@object: or.Operands[0]);

        Assert.Equal(
            2,
            nested.Operands.Count
        );
        Assert.IsType<ComparisonPredicateNode>(@object: or.Operands[1]);
    }
    [Fact]
    public void PushRemoveSchedule() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                push queue = 5
                remove buffer[slot]
                schedule respawnAt in 2s
            }
            """);
        var rule = FirstRule(doc: doc);
        var push = Assert.IsType<PushStatementNode>(@object: rule.Statements[0]);

        Assert.Equal(
            "queue",
            push.RowName
        );
        Assert.IsType<RhsOperandNode>(@object: push.Rhs);

        var remove = Assert.IsType<RemoveCellStatementNode>(@object: rule.Statements[1]);

        Assert.Equal(
            "buffer",
            remove.Target.Name
        );
        Assert.Equal(
            "slot",
            remove.Target.Key
        );

        var schedule = Assert.IsType<ScheduleStatementNode>(@object: rule.Statements[2]);

        Assert.Equal(
            "respawnAt",
            schedule.Target.Name
        );
        Assert.Equal(
            2m,
            schedule.DelaySeconds
        );
    }
    // §2 — effect statements

    [Fact]
    public void SetCellWithOperandRhs() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                pieceCell[$each] = board(cellOf, board, placement, $each)
            }
            """);
        var rule = FirstRule(doc: doc);
        var set = Assert.IsType<SetCellStatementNode>(@object: rule.Statements[0]);

        Assert.Equal(
            "pieceCell",
            set.Target.Name
        );
        Assert.Equal(
            "$each",
            set.Target.Key
        );
        var rhs = Assert.IsType<RhsOperandNode>(@object: set.Rhs);

        Assert.Equal(
            "board(cellOf, board, placement, $each)",
            rhs.Expression.Text
        );
    }
    [Fact]
    public void SetCellWithSecondsRhs() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                cooldown = 3s
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var rhs = Assert.IsType<RhsSecondsNode>(@object: set.Rhs);

        Assert.Equal(
            3m,
            rhs.Seconds
        );
    }
    [Fact]
    public void SetCellWithStringRhs() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                label = "hello"
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var rhs = Assert.IsType<RhsTextNode>(@object: set.Rhs);

        Assert.Equal(
            "hello",
            rhs.Text
        );
    }
    // Shape / placement row shorthand (§4) — existing block grammar, plus the new bare-flag statement.

    [Fact]
    public void ShapeBlockParsesAsOrdinaryTargetedNamedBlock() {
        var doc = WorldSources.ParseClean(body: """
            shape Cylinder "meadow" {
                id: 0
                position [0, -2.5, 0]
                scale [32, 2.0, 32]
            }
            """);
        var block = Assert.IsType<BlockNode>(@object: doc.Statements[0]);

        Assert.Equal(
            "shape",
            block.Identifier
        );
        Assert.Equal(
            "Cylinder",
            block.Target
        );
        Assert.Equal(
            "meadow",
            block.Name
        );
    }
    [Fact]
    public void TransactionWithOnFailure() {
        var doc = WorldSources.ParseClean(body: """
            rule "solitaireFreecell-move" {
                transaction {
                    solitaireFreecell[moves] += 1
                    solitaireFreecell[busy] = 1
                }
                onFailure {
                    solitaireFreecell[result] = 0
                }
            }
            """);
        var tx = Assert.IsType<TransactionStatementNode>(@object: FirstRule(doc: doc).Statements[0]);

        Assert.Equal(
            2,
            tx.MainEffects.Count
        );
        Assert.NotNull(@object: tx.OnFailureEffects);
        Assert.Single(collection: tx.OnFailureEffects!);
    }
    [Fact]
    public void TransformWrapsCallExpression() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                transform boardCombine(row: board, operation: Shift)
            }
            """);
        var transform = Assert.IsType<TransformStatementNode>(@object: FirstRule(doc: doc).Statements[0]);

        Assert.Equal(
            "boardCombine",
            transform.Transform.Name
        );
        Assert.Equal(
            2,
            transform.Transform.Arguments.Count
        );
        Assert.Equal(
            "row",
            transform.Transform.Arguments[0].Name
        );
    }
    [Fact]
    public void UnaryMinusInsideGateOperandsValidatesCleanly() {
        var (doc, diagnostics) = WorldSources.Parse(body: """
            rule "r" {
                when hp - 1 == -1
                flag = 1
            }
            """);
        Assert.NotNull(@object: doc);
        Assert.False(
            condition: diagnostics.HasErrors,
            userMessage: diagnostics.FormatReport()
        );
        var when = Assert.IsType<WhenStatementNode>(@object: FirstRule(doc: doc!).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(@object: when.Predicate);

        Assert.Equal(
            "hp - 1",
            cmp.Left.Text
        );
        Assert.Equal(
            "-1",
            cmp.Right.Text
        );
    }
    [Fact]
    public void ZoneChannelCallArgumentParsesAsOneIdentifier() {
        var doc = WorldSources.ParseClean(body: """
            rule "solitaireFreecell-move" {
                transaction {
                    transform transfer(from: $zones[solitaireFreecell[from]], to: $zones[solitaireFreecell[to]], selector: Slice, key: $cell:solitaireFreecell:card)
                }
            }
            """);
        var tx = Assert.IsType<TransactionStatementNode>(@object: FirstRule(doc: doc).Statements[0]);
        var transform = Assert.IsType<TransformStatementNode>(@object: tx.MainEffects[0]);

        Assert.Equal(
            "transfer",
            transform.Transform.Name
        );
        Assert.Equal(
            4,
            transform.Transform.Arguments.Count
        );

        var from = transform.Transform.Arguments[0];

        Assert.Equal(
            "from",
            from.Name
        );
        var fromIdent = Assert.IsType<IdentifierExpressionNode>(@object: from.Value);

        Assert.Equal(
            "$zones[solitaireFreecell[from]]",
            fromIdent.Name
        );

        var key = transform.Transform.Arguments[3];

        Assert.Equal(
            "key",
            key.Name
        );
        var keyIdent = Assert.IsType<IdentifierExpressionNode>(@object: key.Value);

        Assert.Equal(
            "$cell:solitaireFreecell:card",
            keyIdent.Name
        );
    }
    [Fact]
    public void DrawDealShuffleStatements_ParseSuccessfully() {
        var doc = WorldSources.ParseClean(body: """
            rule "r" {
                draw deck to hand
                deal 5 from deck to hand
                shuffle deck with rng
            }
            """);
        var rule = FirstRule(doc: doc);

        Assert.Equal(3, rule.Statements.Count);

        var draw = Assert.IsType<DrawStatementNode>(@object: rule.Statements[0]);

        Assert.Equal("deck", draw.From);
        Assert.Equal("hand", draw.To);

        var deal = Assert.IsType<DealStatementNode>(@object: rule.Statements[1]);

        Assert.Equal(5, deal.Count);
        Assert.Equal("deck", deal.From);
        Assert.Equal("hand", deal.To);

        var shuffle = Assert.IsType<ShuffleStatementNode>(@object: rule.Statements[2]);

        Assert.Equal("deck", shuffle.Row);
        Assert.Equal("rng", shuffle.Draw);
    }
}

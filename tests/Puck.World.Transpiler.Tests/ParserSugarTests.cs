using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Parsing;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class ParserSugarTests {
    private static DocumentNode ParseClean(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var (doc, diagnostics) = PuckParser.ParseDocumentWithDiagnostics(source);
        Assert.NotNull(doc);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport(source));
        return doc!;
    }

    private static (DocumentNode? Document, DiagnosticBag Diagnostics) ParseWithDiagnostics(string body) {
        var source = $"schema: \"puck.world.def.v1\"\n\n{body}";
        var result = PuckParser.ParseDocumentWithDiagnostics(source);
        return (result.Value, result.Diagnostics);
    }

    private static RuleBlockNode FirstRule(DocumentNode doc) => Assert.IsType<RuleBlockNode>(doc.Statements[0]);

    // §1 — gates

    [Fact]
    public void BareComparisonGate() {
        var doc = ParseClean("""
            rule "tabletop-settle-hold-reset" {
                when $physics:quiescent != 1
                flag = 1
            }
            """);
        var rule = FirstRule(doc);
        var when = Assert.IsType<WhenStatementNode>(rule.Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("$physics:quiescent", cmp.LeftText);
        Assert.Equal("!=", cmp.Comparator);
        Assert.Equal("1", cmp.RightText);
        Assert.Null(cmp.Kind);
    }

    [Fact]
    public void NotWrappingComparisonInsideAll() {
        var doc = ParseClean("""
            rule "tabletop-derive-cell-tilted" {
                when settleHold == 60 and not $upright:placement:$each >= 0.5
                flag = 1
            }
            """);
        var rule = FirstRule(doc);
        var when = Assert.IsType<WhenStatementNode>(rule.Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(when.Predicate);
        Assert.Equal(2, and.Operands.Count);
        var left = Assert.IsType<ComparisonPredicateNode>(and.Operands[0]);
        Assert.Equal("settleHold", left.LeftText);
        var not = Assert.IsType<NotPredicateNode>(and.Operands[1]);
        var negated = Assert.IsType<ComparisonPredicateNode>(not.Operand);
        Assert.Equal("$upright:placement:$each", negated.LeftText);
        Assert.Equal(">=", negated.Comparator);
    }

    [Fact]
    public void ComparandRowGateStaysCompareStateShaped() {
        var doc = ParseClean("""
            rule "solitaireFreecell-source" {
                when solitaireFreecell[request] != solitaireFreecell[applied]
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("solitaireFreecell[request]", cmp.LeftText);
        Assert.Equal("solitaireFreecell[applied]", cmp.RightText);
        Assert.Null(cmp.Kind);
    }

    [Fact]
    public void ColonKindSuffixForcesCompareValueAndStripsFromOperand() {
        var doc = ParseClean("""
            rule "solitaireFreecell-move" {
                when solitaireFreecell[from] != solitaireFreecell[to] : Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("solitaireFreecell[to]", cmp.RightText);
        Assert.Equal("Int", cmp.Kind);
    }

    [Fact]
    public void AsKindSuffixIsEquivalentToColonForm() {
        var doc = ParseClean("""
            rule "r" {
                when solitaireFreecell[from] != solitaireFreecell[to] as Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("solitaireFreecell[to]", cmp.RightText);
        Assert.Equal("Int", cmp.Kind);
    }

    [Fact]
    public void ExpressionOperandKeepsFullTextWithKindSuffixStripped() {
        var doc = ParseClean("""
            rule "rumor" {
                when boneHolderClaimMark[$each] != boneHolder[0] << 32 | claimSeq : Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("boneHolderClaimMark[$each]", cmp.LeftText);
        Assert.Equal("boneHolder[0] << 32 | claimSeq", cmp.RightText);
        Assert.Equal("Int", cmp.Kind);
    }

    [Fact]
    public void FlatFourWayConjunctionStaysOneAndNode() {
        var doc = ParseClean("""
            rule "solitaireFreecell-move" {
                when solitaire[table] == 3 and solitaireFreecell[request] != solitaireFreecell[applied] and solitaireFreecell[stage] == 0 and solitaireFreecell[from] != solitaireFreecell[to] : Int
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var and = Assert.IsType<AndPredicateNode>(when.Predicate);
        Assert.Equal(4, and.Operands.Count);
        Assert.All(and.Operands, static o => Assert.IsType<ComparisonPredicateNode>(o));
    }

    [Fact]
    public void ParenthesizedSubgateStaysOneOpaqueChild() {
        var doc = ParseClean("""
            rule "r" {
                when (a == 1 and b == 2) or c == 3
                flag = 1
            }
            """);
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc).Statements[0]);
        var or = Assert.IsType<OrPredicateNode>(when.Predicate);
        Assert.Equal(2, or.Operands.Count);
        var nested = Assert.IsType<AndPredicateNode>(or.Operands[0]);
        Assert.Equal(2, nested.Operands.Count);
        Assert.IsType<ComparisonPredicateNode>(or.Operands[1]);
    }

    [Fact]
    public void ChainedComparisonReportsPuck004() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                when a == 1 == 2
                flag = 1
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK004");
    }

    // §2 — effect statements

    [Fact]
    public void SetCellWithOperandRhs() {
        var doc = ParseClean("""
            rule "r" {
                pieceCell[$each] = $board:cellOf:board:placement:$each
            }
            """);
        var rule = FirstRule(doc);
        var set = Assert.IsType<SetCellStatementNode>(rule.Statements[0]);
        Assert.Equal("pieceCell", set.Target.Name);
        Assert.Equal("$each", set.Target.Key);
        var rhs = Assert.IsType<RhsOperandNode>(set.Rhs);
        Assert.Equal("$board:cellOf:board:placement:$each", rhs.Text);
    }

    [Fact]
    public void SetCellWithStringRhs() {
        var doc = ParseClean("""
            rule "r" {
                label = "hello"
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(FirstRule(doc).Statements[0]);
        var rhs = Assert.IsType<RhsTextNode>(set.Rhs);
        Assert.Equal("hello", rhs.Text);
    }

    [Fact]
    public void SetCellWithSecondsRhs() {
        var doc = ParseClean("""
            rule "r" {
                cooldown = 3s
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(FirstRule(doc).Statements[0]);
        var rhs = Assert.IsType<RhsSecondsNode>(set.Rhs);
        Assert.Equal(3m, rhs.Seconds);
    }

    [Fact]
    public void AddCellStatement() {
        var doc = ParseClean("""
            rule "r" {
                solitaireFreecell[moves] += 1
            }
            """);
        var add = Assert.IsType<AddCellStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal("solitaireFreecell", add.Target.Name);
        Assert.Equal("moves", add.Target.Key);
        Assert.IsType<RhsOperandNode>(add.Rhs);
    }

    [Fact]
    public void AddCellWithTextRhsReportsPuck009() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                hp += "oops"
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK009");
    }

    [Fact]
    public void PushCountdownRemoveSchedule() {
        var doc = ParseClean("""
            rule "r" {
                push queue = 5
                countdown timer
                remove buffer[slot]
                schedule respawnAt in 2s
            }
            """);
        var rule = FirstRule(doc);
        var push = Assert.IsType<PushStatementNode>(rule.Statements[0]);
        Assert.Equal("queue", push.RowName);
        Assert.IsType<RhsOperandNode>(push.Rhs);

        var countdown = Assert.IsType<CountdownStatementNode>(rule.Statements[1]);
        Assert.Equal("timer", countdown.Target.Name);
        Assert.Null(countdown.Target.Key);

        var remove = Assert.IsType<RemoveCellStatementNode>(rule.Statements[2]);
        Assert.Equal("buffer", remove.Target.Name);
        Assert.Equal("slot", remove.Target.Key);

        var schedule = Assert.IsType<ScheduleStatementNode>(rule.Statements[3]);
        Assert.Equal("respawnAt", schedule.Target.Name);
        Assert.Equal(2m, schedule.DelaySeconds);
    }

    [Fact]
    public void ScheduleWithoutSecondsSuffixReportsPuck010() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                schedule respawnAt in 2
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK010");
        var schedule = Assert.IsType<ScheduleStatementNode>(FirstRule(doc!).Statements[0]);
        Assert.Equal(2m, schedule.DelaySeconds);
    }

    [Fact]
    public void TransformWrapsCallExpression() {
        var doc = ParseClean("""
            rule "r" {
                transform t = boardCombine(row: "board", operation: Shift)
            }
            """);
        var transform = Assert.IsType<TransformStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal("t", transform.RowName);
        Assert.Equal("boardCombine", transform.Transform.Name);
        Assert.Equal(2, transform.Transform.Arguments.Count);
        Assert.Equal("row", transform.Transform.Arguments[0].Name);
    }

    [Fact]
    public void TransactionWithOnFailure() {
        var doc = ParseClean("""
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
        var tx = Assert.IsType<TransactionStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal(2, tx.MainEffects.Count);
        Assert.NotNull(tx.OnFailureEffects);
        Assert.Single(tx.OnFailureEffects!);
    }

    [Fact]
    public void NestedTransactionReportsPuck019() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                transaction {
                    flag = 1
                    transaction {
                        other = 2
                    }
                }
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK019");
    }

    [Fact]
    public void DuplicateOnFailureReportsPuck014() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                transaction {
                    flag = 1
                }
                onFailure {
                    a = 1
                }
                onFailure {
                    b = 1
                }
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK014");
    }

    [Fact]
    public void ZonesFoldedRowRefSplitsNameAndCellKey() {
        var doc = ParseClean("""
            rule "r" {
                $zones[solitaireFreecell[from]][solitaireFreecell[card]] = 1
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal("$zones[solitaireFreecell[from]]", set.Target.Name);
        Assert.Equal("$cell:solitaireFreecell:card", set.Target.Key);
    }

    [Fact]
    public void ZoneChannelCallArgumentParsesAsOneIdentifier() {
        var doc = ParseClean("""
            rule "solitaireFreecell-move" {
                transaction {
                    transform t = transfer(from: $zones[solitaireFreecell[from]], to: $zones[solitaireFreecell[to]], selector: Slice, key: $cell:solitaireFreecell:card)
                }
            }
            """);
        var tx = Assert.IsType<TransactionStatementNode>(FirstRule(doc).Statements[0]);
        var transform = Assert.IsType<TransformStatementNode>(tx.MainEffects[0]);
        Assert.Equal("transfer", transform.Transform.Name);
        Assert.Equal(4, transform.Transform.Arguments.Count);

        var from = transform.Transform.Arguments[0];
        Assert.Equal("from", from.Name);
        var fromIdent = Assert.IsType<IdentifierExpressionNode>(from.Value);
        Assert.Equal("$zones[solitaireFreecell[from]]", fromIdent.Name);

        var key = transform.Transform.Arguments[3];
        Assert.Equal("key", key.Name);
        var keyIdent = Assert.IsType<IdentifierExpressionNode>(key.Value);
        Assert.Equal("$cell:solitaireFreecell:card", keyIdent.Name);
    }

    // §3 — rule / decision / option / bind

    [Fact]
    public void BindRequiresExplicitKindAndReportsPuck006WhenMissing() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                bind dx = 5
                flag = dx
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK006");
    }

    [Fact]
    public void BindWithExplicitKindCapturesExpressionVerbatim() {
        var doc = ParseClean("""
            rule "r" {
                bind dx : Int = hp - 1
                flag = dx
            }
            """);
        var bind = Assert.IsType<BindStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal("dx", bind.Name);
        Assert.Equal("Int", bind.Kind);
        Assert.Equal("hp - 1", bind.ExpressionText);
    }

    [Fact]
    public void RuleWithNoEffectsReportsPuck026() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "empty" {
                when a == 1
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK026");
    }

    [Fact]
    public void SecondWhenClauseReportsPuck012() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                when a == 1
                when b == 2
                flag = 1
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK012");
    }

    [Fact]
    public void BackquotedReplacePropertyUsesGenericPropertyPath() {
        var doc = ParseClean("""
            rule "r" {
                `$replace`: true
                flag = 1
            }
            """);
        var rule = FirstRule(doc);
        var prop = Assert.IsType<PropertyNode>(rule.Statements[0]);
        Assert.Equal("$replace", prop.Name);
        var literal = Assert.IsType<LiteralExpressionNode>(prop.Value);
        Assert.Equal(true, literal.Value);
    }

    [Fact]
    public void DecisionWithOptionInterruptAndOnNoChoice() {
        var doc = ParseClean("""
            rule "choose-companion" {
                forEach: "hound"
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
        var rule = FirstRule(doc);
        var decision = Assert.IsType<DecisionBlockNode>(
            rule.Statements.OfType<DecisionBlockNode>().Single());

        var periodSeconds = Assert.IsType<PropertyNode>(decision.Statements[0]);
        Assert.Equal("periodSeconds", periodSeconds.Name);

        var interrupt = decision.Statements.OfType<InterruptStatementNode>().Single();
        Assert.IsType<ComparisonPredicateNode>(interrupt.Predicate);

        var option = decision.Statements.OfType<OptionBlockNode>().Single();
        Assert.Equal("follow", option.Name);
        var optionWhen = Assert.IsType<WhenStatementNode>(option.Statements[0]);
        Assert.IsType<ComparisonPredicateNode>(optionWhen.Predicate);
        var score = Assert.IsType<ScoreStatementNode>(option.Statements[1]);
        Assert.Equal("trust * 2", score.Text);

        var onNoChoice = decision.Statements.OfType<OnNoChoiceBlockNode>().Single();
        Assert.Single(onNoChoice.Effects);
    }

    [Fact]
    public void DecisionMissingPeriodSecondsReportsPuck029() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                decision {
                    option "a" {
                        score: 1
                    }
                }
                flag = 1
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK029");
    }

    [Fact]
    public void OptionMissingScoreReportsPuck013() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                decision {
                    periodSeconds: 1s
                    option "a" {
                        when hp == 1
                    }
                }
                flag = 1
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK013");
    }

    // Shape / placement row shorthand (§4) — existing block grammar, plus the new bare-flag statement.

    [Fact]
    public void ShapeBlockParsesAsOrdinaryTargetedNamedBlock() {
        var doc = ParseClean("""
            shape Cylinder "meadow" {
                id: 0
                position: [0, -2.5, 0]
                scale: [32, 2.0, 32]
            }
            """);
        var block = Assert.IsType<BlockNode>(doc.Statements[0]);
        Assert.Equal("shape", block.Identifier);
        Assert.Equal("Cylinder", block.Target);
        Assert.Equal("meadow", block.Name);
    }

    [Fact]
    public void BarePlacementSolidFlagBecomesFlagStatement() {
        var doc = ParseClean("""
            placements {
                placement "debugRoom" {
                    prototype: debugRoom
                    parent: provingCourt
                    position: [0, 0, 0]
                    solid
                    grip: { holdable: true }
                }
            }
            """);
        var placements = Assert.IsType<BlockNode>(doc.Statements[0]);
        var placement = Assert.IsType<BlockNode>(placements.Statements[0]);
        var flag = Assert.IsType<FlagStatementNode>(placement.Statements[3]);
        Assert.Equal("solid", flag.Name);
        var grip = Assert.IsType<PropertyNode>(placement.Statements[4]);
        Assert.Equal("grip", grip.Name);
    }

    [Fact]
    public void BothBareAndExplicitSolidReportsPuck028() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            placements {
                placement "debugRoom" {
                    prototype: debugRoom
                    solid
                    solid: { margin: 0.2 }
                }
            }
            """);
        Assert.NotNull(doc);
        Assert.Contains(diagnostics, d => d.Code == "PUCK028");
    }

    // Ambiguities named in the spec

    [Fact]
    public void InlineArrayPropertySugarStillWorksOutsideRuleBody() {
        var doc = ParseClean("""
            host {
                tags [1, 2, 3]
            }
            """);
        var host = Assert.IsType<BlockNode>(doc.Statements[0]);
        var prop = Assert.IsType<PropertyNode>(host.Statements[0]);
        Assert.Equal("tags", prop.Name);
        Assert.IsType<ArrayExpressionNode>(prop.Value);
    }

    [Fact]
    public void BracketAfterIdentifierMeansCellKeyInsideRuleBody() {
        var doc = ParseClean("""
            rule "r" {
                hp[$each] = 1
            }
            """);
        var set = Assert.IsType<SetCellStatementNode>(FirstRule(doc).Statements[0]);
        Assert.Equal("hp", set.Target.Name);
        Assert.Equal("$each", set.Target.Key);
    }

    [Fact]
    public void UnaryMinusInsideGateOperandsValidatesCleanly() {
        var (doc, diagnostics) = ParseWithDiagnostics("""
            rule "r" {
                when hp - 1 == -1
                flag = 1
            }
            """);
        Assert.NotNull(doc);
        Assert.False(diagnostics.HasErrors, diagnostics.FormatReport());
        var when = Assert.IsType<WhenStatementNode>(FirstRule(doc!).Statements[0]);
        var cmp = Assert.IsType<ComparisonPredicateNode>(when.Predicate);
        Assert.Equal("hp - 1", cmp.LeftText);
        Assert.Equal("-1", cmp.RightText);
    }
}

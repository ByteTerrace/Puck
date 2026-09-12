using Puck.GamingBricks.Forge;

using Puck.State;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the estimate's treatment of rules that cannot share a frame. Charging the dearest arm instead of the sum is
/// only honest while nothing can change the guard mid-frame, so most of this is about when the saving is refused.
/// </summary>
public sealed class CartridgeExclusiveGuardTests {
    [Fact]
    public void RulesGuardedOnDifferentValuesOfOneVariableChargeOnlyTheDearest() {
        var apart = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 1, work: 40),
            Guarded(name: "c", phase: 2, work: 40),
        ]);
        var together = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 0, work: 40),
            Guarded(name: "c", phase: 0, work: 40),
        ]);

        // Three arms of one guard cost about one arm; three rules sharing an arm cost all three.
        Assert.True(condition: Cost(apart) < Cost(together));
        Assert.True(condition: Cost(together) > Cost(apart) * 2);
    }

    [Fact]
    public void AGuardWrittenWhileItsRulesAreStillBeingEvaluatedGivesUpTheSaving() {
        // The written guard is the same shape as the exclusive one, except a rule between the arms moves it, so the
        // later arms can run in the same frame after all.
        var settled = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 1, work: 40),
        ]);
        var moved = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40, advance: 1),
            Guarded(name: "b", phase: 1, work: 40),
        ]);

        Assert.True(condition: Cost(moved) > Cost(settled));
    }

    [Fact]
    public void AGuardWrittenOnlyAfterItsLastArmKeepsTheSaving() {
        // This is what a phase machine's own advance step is: it names the next phase once every arm has been passed.
        var staged = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 1, work: 40),
            new CartridgeRule(Name: "advance", When: [], Body: [
                new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "ph"), Operation: null, Value: new CartridgeValue(Variable: "nph")),
            ]),
        ]);
        var inline = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40, advance: 1),
            Guarded(name: "b", phase: 1, work: 40),
        ]);

        Assert.True(condition: Cost(staged) < Cost(inline));
    }

    [Fact]
    public void ALoopIndexCountsAsAWriteToTheGuardItShares() {
        // A counted loop assigns its index every iteration, which is as much a write as a set step is.
        var clean = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 1, work: 40),
        ]);
        var borrowed = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40, loopIndex: "ph"),
            Guarded(name: "b", phase: 1, work: 40),
        ]);

        Assert.True(condition: Cost(borrowed) > Cost(clean));
    }

    [Fact]
    public void AGuardTestIsChargedEvenWhenItsArmCannotRun() {
        // The comparison happens every frame whichever way it falls, so arms are never free to declare.
        var one = Document(rules: [Guarded(name: "a", phase: 0, work: 40)]);
        var many = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40),
            Guarded(name: "b", phase: 1, work: 40),
            Guarded(name: "c", phase: 2, work: 40),
            Guarded(name: "d", phase: 3, work: 40),
        ]);

        Assert.True(condition: Cost(many) > Cost(one));
    }

    [Fact]
    public void ADeclaredSceneKeepsTheSavingWhereverItIsWritten() {
        // The same shape that gives the saving up when the guard is merely inferred: a rule moves it between the arms.
        // Declaring it as the scene makes the partition structural, so the estimate charges the dearest arm regardless.
        var inferred = Document(rules: [
            Guarded(name: "a", phase: 0, work: 40, advance: 1),
            Guarded(name: "b", phase: 1, work: 40),
        ]);
        var declared = inferred with { Scene = "ph" };

        Assert.True(condition: Cost(declared) < Cost(inferred));
    }

    [Fact]
    public void ASceneNamingNoVariableIsRefused() {
        var document = Document(rules: [Guarded(name: "a", phase: 0, work: 4)]) with { Scene = "nowhere" };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => error.Path == "scene");
    }

    private static long Cost(CartridgeDocument document) {
        var frame = CartridgeCost.Frame(document: document, profile: CartridgeCostProfile.Humble);
        Assert.True(condition: frame.IsKnown, userMessage: frame.Reason);

        return frame.Cycles;
    }

    private static CartridgeRule Guarded(string name, int phase, int work, int? advance = null, string? loopIndex = null) {
        var body = new List<CartridgeStatement> {
            new(Kind: "repeat", Count: work, Index: loopIndex ?? "i", Body: [
                new(Kind: "set", Target: new CartridgeTarget(Variable: "sink"), Operation: ExpressionOp.Add, Value: new CartridgeValue(Constant: 1)),
            ]),
        };
        if (advance is { } next) {
            body.Add(item: new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "ph"), Operation: null, Value: new CartridgeValue(Constant: next)));
        }

        return new CartridgeRule(
            Name: name,
            When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "ph"), Comparison: ActionStateComparison.Equal, Right: new CartridgeValue(Constant: phase))],
            Body: [.. body]);
    }

    private static CartridgeDocument Document(CartridgeRule[] rules) =>
        CartridgeDocuments.Create(target: "cgb", title: "GUARD") with {
            Variables = [
                new CartridgeVariable(Name: "ph", Initial: 0),
                new CartridgeVariable(Name: "nph", Initial: 0),
                new CartridgeVariable(Name: "i", Initial: 0),
                new CartridgeVariable(Name: "sink", Initial: 0),
            ],
            Rules = rules,
        };
}

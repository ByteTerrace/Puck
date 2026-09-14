using Puck.GamingBricks.Forge;


namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Covers the estimate's treatment of rules that cannot share a frame. Charging the dearest arm instead of the sum is
/// only honest while nothing can change the guard mid-frame, so most of this is about when the saving is refused.
/// </summary>
public sealed class CartridgeExclusiveGuardTests {
    private static long Cost(CartridgeDocument document) {
        var frame = CartridgeCost.Frame(
            document: document,
            profile: CartridgeCostProfile.Humble
        );

        Assert.True(
            condition: frame.IsKnown,
            userMessage: frame.Reason
        );

        return frame.Cycles;
    }
    private static CartridgeDocument Document(CartridgeRule[] rules) =>
        CartridgeDocuments.Create(
            target: "cgb",
            title: "GUARD"
        ) with {
            Variables = [
                new CartridgeVariable(
                Name: "ph",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "nph",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "i",
                Initial: 0
            ),
                new CartridgeVariable(
                Name: "sink",
                Initial: 0
            ),
            ],
            Rules = rules,
        };
    private static CartridgeRule Guarded(string name, int phase, int work, int? advance = null, string? loopIndex = null) {
        var body = new List<CartridgeStatement> {
            new(
            Kind: "repeat",
            Count: work,
            Index: (loopIndex ?? "i"),
            Body: [
                new(
                    Kind: "set",
                    Target: new CartridgeTarget(State: "sink"),
                    Operation: ExpressionOp.Add,
                    Value: CartridgeExpressions.Of(constant: 1)
                ),
            ]
        ),
        };

        if (advance is { } next) {
            body.Add(item: new CartridgeStatement(
                Kind: "set",
                Target: new CartridgeTarget(State: "ph"),
                Operation: null,
                Value: CartridgeExpressions.Of(constant: next)
            ));
        }

        return new CartridgeRule(
            Name: name,
            When: CartridgeExpressions.Gate(
                left: CartridgeExpressions.Of(state: "ph"),
                comparison: ActionStateComparison.Equal,
                right: CartridgeExpressions.Of(constant: phase)
            ),
            Body: [.. body]
        );
    }

    [Fact]
    public void ADeclaredSceneKeepsTheSavingWhereverItIsWritten() {
        // The same shape that gives the saving up when the guard is merely inferred: a rule moves it between the arms.
        // Declaring it as the scene makes the partition structural, so the estimate charges the dearest arm regardless.
        var inferred = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40,
                advance: 1
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);
        var declared = inferred with { Scene = "ph" };

        Assert.True(condition: (Cost(document: declared) < Cost(document: inferred)));
    }
    [Fact]
    public void AGuardTestIsChargedEvenWhenItsArmCannotRun() {
        // The comparison happens every frame whichever way it falls, so arms are never free to declare.
        var one = Document(rules: [Guarded(
                name: "a",
                phase: 0,
                work: 40
            )]);
        var many = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
            Guarded(
                name: "c",
                phase: 2,
                work: 40
            ),
            Guarded(
                name: "d",
                phase: 3,
                work: 40
            ),
        ]);

        Assert.True(condition: (Cost(document: many) > Cost(document: one)));
    }
    [Fact]
    public void AGuardWrittenOnlyAfterItsLastArmKeepsTheSaving() {
        // This is what a phase machine's own advance step is: it names the next phase once every arm has been passed.
        var staged = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
            new CartridgeRule(
                Name: "advance",
                Body: [
                new CartridgeStatement(
                        Kind: "set",
                        Target: new CartridgeTarget(State: "ph"),
                        Operation: null,
                        Value: CartridgeExpressions.Of(state: "nph")
                    ),
            ]
            ),
        ]);
        var inline = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40,
                advance: 1
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);

        Assert.True(condition: (Cost(document: staged) < Cost(document: inline)));
    }
    [Fact]
    public void AGuardWrittenWhileItsRulesAreStillBeingEvaluatedGivesUpTheSaving() {
        // The written guard is the same shape as the exclusive one, except a rule between the arms moves it, so the
        // later arms can run in the same frame after all.
        var settled = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);
        var moved = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40,
                advance: 1
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);

        Assert.True(condition: (Cost(document: moved) > Cost(document: settled)));
    }
    [Fact]
    public void ALoopIndexCountsAsAWriteToTheGuardItShares() {
        // A counted loop assigns its index every iteration, which is as much a write as a set step is.
        var clean = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);
        var borrowed = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40,
                loopIndex: "ph"
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
        ]);

        Assert.True(condition: (Cost(document: borrowed) > Cost(document: clean)));
    }
    [Fact]
    public void ASceneNamingNoVariableIsRefused() {
        var document = Document(rules: [Guarded(
                name: "a",
                phase: 0,
                work: 4
            )]) with { Scene = "nowhere" };

        Assert.Contains(
            collection: CartridgeDocuments.Validate(document: document),
            filter: error => (error.Path == "scene")
        );
    }
    [Fact]
    public void RulesGuardedOnDifferentValuesOfOneVariableChargeOnlyTheDearest() {
        var apart = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 1,
                work: 40
            ),
            Guarded(
                name: "c",
                phase: 2,
                work: 40
            ),
        ]);
        var together = Document(rules: [
            Guarded(
                name: "a",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "b",
                phase: 0,
                work: 40
            ),
            Guarded(
                name: "c",
                phase: 0,
                work: 40
            ),
        ]);

        // Three arms of one guard cost about one arm; three rules sharing an arm cost all three.
        Assert.True(condition: (Cost(document: apart) < Cost(document: together)));
        Assert.True(condition: (Cost(document: together) > (Cost(document: apart) * 2)));
    }
}

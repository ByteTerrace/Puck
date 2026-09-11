using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.Maths;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>Covers the abstract work model: exact accumulation, and refusal to price a primitive nothing measured.</summary>
public sealed class CartridgeCostTests {
    [Fact]
    public void LoopsMultiplyTheirBodyAndBranchesTakeTheCostlierArm() {
        var cheap = Set(operation: "set", value: new CartridgeValue(Constant: 1));
        var dear = Set(operation: "div", value: new CartridgeValue(Constant: 3));
        var one = Frame(body: [cheap]);
        var ten = Frame(body: [new CartridgeStatement(Kind: "repeat", Count: 10, Index: "i", Body: [cheap])]);
        var hundred = Frame(body: [new CartridgeStatement(Kind: "repeat", Count: 100, Index: "i", Body: [cheap])]);
        Assert.True(condition: one.IsKnown && ten.IsKnown && hundred.IsKnown);
        // Ten times the body plus ten loop steps, over a single setup: the growth is exactly linear in the count.
        Assert.Equal(expected: (hundred.Cycles - ten.Cycles) / 90, actual: (ten.Cycles - Frame(body: [new CartridgeStatement(Kind: "repeat", Count: 1, Index: "i", Body: [cheap])]).Cycles) / 9);

        var branch = Frame(body: [new CartridgeStatement(
            Kind: "if",
            When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "i"), Comparison: "lt", Right: new CartridgeValue(Constant: 1))],
            Then: [cheap],
            Else: [dear])]);
        var costlier = Frame(body: [new CartridgeStatement(
            Kind: "if",
            When: [new CartridgeCondition(Kind: "compare", Left: new CartridgeValue(Variable: "i"), Comparison: "lt", Right: new CartridgeValue(Constant: 1))],
            Then: [dear],
            Else: [cheap])]);
        Assert.Equal(expected: branch.Cycles, actual: costlier.Cycles);
    }

    [Fact]
    public void AnUnmeasuredPrimitiveIsUnmodeledAndNeverAdmitted() {
        var unknown = Frame(body: [new CartridgeStatement(Kind: "scroll")]);
        Assert.True(condition: unknown.IsUnmodeled);
        
        // Nesting cannot launder an unmodeled step into a number.
        var buried = Frame(body: [new CartridgeStatement(Kind: "repeat", Count: 4, Index: "i", Body: [
            new CartridgeStatement(Kind: "if", When: [], Then: [new CartridgeStatement(Kind: "scroll")]),
        ])]);
        Assert.True(condition: buried.IsUnmodeled);

        var unknownOperation = Frame(body: [Set(operation: "sqrt", value: new CartridgeValue(Constant: 1))]);
        Assert.True(condition: unknownOperation.IsUnmodeled);

        var unknownCondition = Frame(body: [new CartridgeStatement(Kind: "if", When: [new CartridgeCondition(Kind: "touch")], Then: [Set(operation: "set", value: new CartridgeValue(Constant: 1))])]);
        Assert.True(condition: unknownCondition.IsUnmodeled);
    }

    [Theory]
    [InlineData("cgb")]
    [InlineData("agb")]
    public void EachMachineCarriesItsOwnWeightsAndReservation(string target) {
        var profile = CartridgeCostProfile.For(target: target);
        Assert.True(condition: profile.FrameUnits > 0L);

        // The unit's anchor is a set step, and it is the same on both so a document reads the same way on either.
        Assert.Equal(expected: CartridgeCostProfile.Humble.StepSet, actual: profile.StepSet);
    }

    [Fact]
    public void TheMachinesWeightsDifferWhereTheirHardwareDoes() {
        // A shared table would have to take the worse of the two for every shape, which fits neither machine. Divide
        // is the clearest case: one machine has no divider at all and the other's is far cheaper per step.
        Assert.NotEqual(expected: CartridgeCostProfile.Humble.StepDivide, actual: CartridgeCostProfile.Advanced.StepDivide);
        Assert.True(condition: CartridgeCostProfile.Advanced.FrameUnits > CartridgeCostProfile.Humble.FrameUnits);
    }

    [Fact]
    public void AnEstimatePastTheReservationIsReportedRatherThanRefused() {
        // A slow cartridge still runs, and the machine absorbs that case already, so cost is advice and not a gate.
        var heavy = CartridgeDocuments.Create(target: "cgb", title: "HEAVY") with {
            Variables = [new CartridgeVariable(Name: "i", Initial: 0), new CartridgeVariable(Name: "j", Initial: 0), new CartridgeVariable(Name: "x", Initial: 0)],
            Rules = [new CartridgeRule(Name: "work", When: [], Body: [
                new CartridgeStatement(Kind: "repeat", Count: 255, Index: "j", Body: [
                    new CartridgeStatement(Kind: "repeat", Count: 255, Index: "i", Body: [
                        new CartridgeStatement(Kind: "set", Target: new CartridgeTarget(Variable: "x"), Operation: "add", Value: new CartridgeValue(Constant: 1)),
                    ]),
                ]),
            ])],
        };

        Assert.Empty(collection: CartridgeDocuments.Validate(document: heavy));

        var (frame, reservation) = CartridgeDocuments.Estimate(document: heavy);
        Assert.True(condition: frame.IsKnown);
        Assert.True(condition: frame.Cycles > reservation);
        Assert.NotEmpty(collection: new HgbCartridgeCompiler().Compile(document: heavy).Rom);
    }

    [Fact]
    public void SpritesAndIndexOperandsAreCharged() {
        var plain = Frame(body: [Set(operation: "add", value: new CartridgeValue(Constant: 1))]);
        var indexed = Frame(body: [Set(operation: "add", value: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "i")))]);
        var nested = Frame(body: [Set(operation: "add", value: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Array: "cells", Index: new CartridgeValue(Variable: "i"))))]);
        Assert.True(condition: plain.Cycles < indexed.Cycles);
        Assert.True(condition: indexed.Cycles < nested.Cycles);

        var document = Document(body: [Set(operation: "set", value: new CartridgeValue(Constant: 1))]);
        var bare = CartridgeCost.Frame(document: document, profile: CartridgeCostProfile.Humble);
        var withSprites = CartridgeCost.Frame(profile: CartridgeCostProfile.Humble, document: document with {
            Sprites = [.. Enumerable.Range(start: 0, count: 8).Select(selector: index => new CartridgeSprite(
                Name: $"s{index}", Tile: new CartridgeValue(Constant: 0), X: new CartridgeValue(Constant: 0),
                Y: new CartridgeValue(Constant: 0), Visible: new CartridgeValue(Constant: 1)))],
        });
        Assert.True(condition: withSprites.Cycles > bare.Cycles);
    }

    private static CostBound Frame(CartridgeStatement[] body) => CartridgeCost.Frame(document: Document(body: body), profile: CartridgeCostProfile.Humble);

    private static CartridgeDocument Document(CartridgeStatement[] body) => CartridgeDocuments.Create(target: "cgb", title: "COST") with {
        Variables = [new CartridgeVariable(Name: "i", Initial: 0), new CartridgeVariable(Name: "x", Initial: 0)],
        Arrays = [new CartridgeArray(Name: "cells", Initial: [0, 0, 0, 0])],
        Rules = [new CartridgeRule(Name: "rule", When: [], Body: body)],
    };

    private static CartridgeStatement Set(string operation, CartridgeValue value) =>
        new(Kind: "set", Target: new CartridgeTarget(Variable: "x"), Operation: operation, Value: value);
}

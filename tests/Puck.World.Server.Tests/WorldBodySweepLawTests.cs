using Puck.Commands;

using Xunit;

using Puck.World.Protocol;

namespace Puck.World.Server.Tests;

/// <summary>
/// THE LAW: a rule declaring <c>forEach: $bodies</c> evaluates once per live body, with <c>each</c> bound to that
/// body, so a gate over <c>$bodyPosition:each:y</c> fires for every body below the bound and for no body above it;
/// the rules read-back and the work sheet name the sweep.
/// </summary>
public sealed class WorldBodySweepLawTests {
    private const string FellRow = "fell";

    private static WorldDefinition SweepDocument() => Fixtures.BuildGradientUpDocument(gradientUp: false) with {
        StateRaw = new WorldStateSection(World: [
            StateFixtures.IntSlot(name: FellRow),
        ]),
        Rules = [
            new WorldRule(
                Name: CellName.Parse(candidate: "fell-count"),
                Effects: [new ActionEffect.AddState(
                    State: FellRow,
                    Value: 1m
                )],
                Gate: StateFixtures.Compare(
                    comparison: ExpressionOp.Less,
                    state: $"{WorldRuleFacts.BodyPositionPrefix}each:y",
                    value: -40m
                ),
                ForEach: RuleFacts.ForEachBodies
            ),
        ],
    };

    [Fact]
    public void EveryLiveBodyBelowTheBoundFiresOnce_ControlABodyAboveItDoesNot() {
        var definition = SweepDocument();

        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var slot = 0; (slot < 3); slot++) {
            var actor = Principal.Seat(slot: slot);

            Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
                Principal: actor,
                Slot: actor.Index,
                IdentityName: null,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }

        Pose(fixture: fixture, index: 0, y: -60f);
        Pose(fixture: fixture, index: 1, y: -60f);
        Pose(fixture: fixture, index: 2, y: -10f);
        fixture.Step();

        Assert.Equal(
            expected: 2L,
            actual: fixture.SlotValue(row: FellRow)
        );
        Assert.Contains(
            expectedSubstring: $"forEach {RuleFacts.ForEachBodies}",
            actualString: fixture.Server.DescribeRules(),
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: $"forEach {RuleFacts.ForEachBodies} body capacity=",
            actualString: WorldRuleWorkBudget.DescribeMultiplier(
                definition: definition,
                rule: WorldFactsCompiler.CompileAll(definition: definition).Single()
            ),
            comparisonType: StringComparison.Ordinal
        );
    }

    private static void Pose(WorldFixture fixture, int index, float y) => fixture.Server.Body(index: index)!.Pose(
        pitchRadians: 0f,
        rollRadians: 0f,
        x: (index * 2f),
        y: y,
        yawRadians: 0f,
        z: -5f
    );
}

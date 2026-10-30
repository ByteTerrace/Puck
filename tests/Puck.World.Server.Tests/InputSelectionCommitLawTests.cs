using Puck.Commands;
using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Laws for a rule firing's commit and input selection: a <c>setBodyState</c> of a register an input mode
/// family selects by switches the written body's routing owner at that firing's commit, and leaves every other owner's
/// selection where it stood.</summary>
public sealed class InputSelectionCommitLawTests {
    [Fact]
    public void ARuleWriteOfASelectorRegisterSwitchesOnlyTheWrittenBodysOwner() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            StateRaw = (basis.StateRaw ?? new WorldStateSection()) with {
                Body = [.. (basis.StateRaw?.Body ?? []),
                    new("inputMode", ActionStateKind.Counter, Initial: 0, Envelope: new ActionStateEnvelope.Set(Values: [0, 1]))],
            },
            InputModes = new([.. basis.Channels.Select(selector: row => row.Name)], [
                new("mode", new(Kind: WorldInputModeSelectorKind.BodyState, Name: "inputMode"), "before", [
                    new("before", [], ["0"]), new("after", [], ["1"]),
                ]),
            ]),
            Rules = [new WorldRule(
                Name: CellName.Parse(candidate: "switch-body-0"),
                Effects: [new WorldEffect.SetBodyState(Key: "0", State: "inputMode", Value: 1m)],
                Gate: new ActionPredicate.CompareState(State: WorldRuleFacts.Population, Comparison: ExpressionOp.GreaterOrEqual, Value: 2m),
                Mode: ActionTriggerMode.Edge
            )],
        };
        using var fixture = Fixtures.FreshServer(definition);

        fixture.JoinSeat(slot: 0);
        fixture.JoinSeat(slot: 1);
        var first = fixture.Server.Input.Admit(bodyIndex: 0, principal: Principal.Seat(slot: 0), sources: []);
        var second = fixture.Server.Input.Admit(bodyIndex: 1, principal: Principal.Seat(slot: 1), sources: []);
        var secondMapping = second.Mapping;

        Assert.Contains("before", first.Mapping.Name);
        fixture.Step();
        Assert.True(condition: WorldBodyStateReads.TryResolveSlot(definition: fixture.Server.Definition, kind: out _, name: "inputMode", slot: out var slot));
        Assert.Equal(Puck.Maths.FixedQ4816.One.Value, fixture.Server.Population.ActionStateLane.Read(ordinal: 0, slot: slot));
        Assert.Equal(0L, fixture.Server.Population.ActionStateLane.Read(ordinal: 1, slot: slot));
        Assert.Contains("after", first.Mapping.Name);
        Assert.Same(secondMapping, second.Mapping);
    }
}

using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Pins the authority's answer to an owner's input (<see cref="WorldInputAnswer"/>), which ends a seat's guess on
/// its own client: every batch the owner sends is answered in the tick it arrives, whichever check refuses it, and only a
/// batch of the owner's current epoch for its own route can write the answer (<see cref="WorldInputRuntime.Answer"/>).</summary>
public sealed class InputAnswerLawTests {
    private static WorldDefinition Document(long stunned) {
        var basis = Fixtures.BuildDocument();

        return basis with {
            ChannelsRaw = [.. basis.Channels, new("answerPress", ChannelShape.Binary, Composition: true)],
            StateRaw = ((basis.StateRaw ?? new WorldStateSection()) with {
                World = [
                    .. (basis.StateRaw?.World ?? []),
                    new WorldStateRow(CellName.Parse(candidate: "stunned"), CellKind.Int, Capacity: 4,
                        Cells: [new StateCell(CellName.Parse(candidate: "0"), CellValue.Int(value: stunned))], GatesDrive: true),
                ],
            }),
        };
    }
    // The seat's admitted owner and a producer over one press source, and a batch carrying one press.
    private static (WorldInputBatch Batch, WorldInputOwner Owner) Press(WorldFixture fixture) {
        var catalog = fixture.Server.Population.InputCatalog;

        Assert.True(condition: catalog.TryGetLogicalId(id: out var channel, name: "answerPress"));
        WorldInputSource[] sources = [new(0, channel, WorldInputContributionKind.Level, "key:press")];
        var owner = fixture.Server.Input.Admit(bodyIndex: 0, principal: Principal.Seat(slot: 0), sources: sources);
        var producer = new WorldInputProducer(catalog, owner.Mapping, sources, 128, owner.Revision, owner.Epoch);
        var tick = fixture.Server.Input.Interval.Tick;

        Assert.True(condition: producer.Observe(0, FixedQ4816.One.Value, tick, default, 1));

        return (producer.Publish(tick), owner);
    }

    [Fact]
    public void ABatchADriveGateRefusesIsAnsweredInTheTickItArrives() {
        using var fixture = Fixtures.FreshServer(definition: Document(stunned: 1L));

        fixture.JoinSeat();
        fixture.Step();
        var (batch, _) = Press(fixture: fixture);
        var admission = fixture.Server.ApplyIntentSubmission(body: fixture.Server.Body(index: 0)!,
            submission: new IntentSubmission(fixture.Server.Input.Interval.Tick, 0, batch.Image, Principal.Seat(slot: 0), Input: batch));

        Assert.False(condition: admission.IsAccepted);
        var answer = Assert.Single(collection: fixture.Server.Input.Answers().ToArray());

        Assert.Equal(expected: Principal.Seat(slot: 0), actual: answer.Owner);
        Assert.Equal(expected: batch.Epoch, actual: answer.Epoch);
        Assert.Equal(expected: batch.Events.Span[^1].Sequence, actual: answer.Sequence);
        Assert.Equal(expected: fixture.Server.Input.Interval.Tick, actual: answer.Tick);
    }
    // The control: with the gate open the same batch is taken, and answered the same way.
    [Fact]
    public void ABatchTheAuthorityTakesIsAnsweredTheSameWay() {
        using var fixture = Fixtures.FreshServer(definition: Document(stunned: 0L));

        fixture.JoinSeat();
        fixture.Step();
        var (batch, _) = Press(fixture: fixture);
        var admission = fixture.Server.ApplyIntentSubmission(body: fixture.Server.Body(index: 0)!,
            submission: new IntentSubmission(fixture.Server.Input.Interval.Tick, 0, batch.Image, Principal.Seat(slot: 0), Input: batch));

        Assert.True(condition: admission.IsAccepted, userMessage: admission.InputRefusal);
        Assert.Equal(expected: batch.Events.Span[^1].Sequence, actual: Assert.Single(collection: fixture.Server.Input.Answers().ToArray()).Sequence);
    }
    [Fact]
    public void AStaleEpochOrAnotherBodysBatchLeavesTheCurrentAnswer() {
        using var fixture = Fixtures.FreshServer(definition: Document(stunned: 0L));

        fixture.JoinSeat();
        fixture.Step();
        var (batch, owner) = Press(fixture: fixture);
        var principal = Principal.Seat(slot: 0);

        fixture.Server.Input.Answer(batch: batch, bodyIndex: 0, principal: principal);
        var answered = Assert.Single(collection: fixture.Server.Input.Answers().ToArray());

        fixture.Server.Input.Answer(batch: (batch with { Epoch = (owner.Epoch + 1UL) }), bodyIndex: 0, principal: principal);
        fixture.Server.Input.Answer(batch: batch, bodyIndex: 1, principal: principal);
        Assert.Equal(expected: answered, actual: Assert.Single(collection: fixture.Server.Input.Answers().ToArray()));
    }
}

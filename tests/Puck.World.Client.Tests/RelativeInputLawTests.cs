using Puck.Maths;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Client.Tests;

public sealed class RelativeInputLawTests {
    private static SeatController Seat(double limit = 10) => new() {
        Channels = WorldChannelTable.Compile(channels: [new WorldChannel("delta", ChannelShape.Relative, Composition: true, Relative: new(MaximumPerStep: limit, Sensitivity: 1, Unit: "radians"))]),
    };

    [Fact]
    public void OrderedSamplesConserveDisplacementAndAreConsumedOnce() {
        var seat = Seat();

        for (var index = 0; (index < 16); index++) { seat.AccumulateRelative(ordinal: 0, sample: (1d / 1048576)); }
        var intent = seat.ConsumeRelative(intent: default);

        Assert.Equal(FixedQ3232.FromFixedQ4816(value: FixedQ4816.Epsilon), seat.Channels.ReadRelative(intent: intent, ordinal: 0));
        Assert.Equal(default, seat.ConsumeRelative(intent: default));
        Assert.Throws<ArgumentException>(testCode: () => seat.HoldChannel(controlId: "mouse", ordinal: 0, scale: FixedQ4816.One));
    }
    [Fact]
    public void BoundIsAppliedToTheCompletedSumAndClearingDropsPendingInput() {
        var seat = Seat(limit: 1);

        seat.AccumulateRelative(ordinal: 0, sample: 100);
        seat.AccumulateRelative(ordinal: 0, sample: -100);
        Assert.Equal(default, seat.ConsumeRelative(intent: default));
        seat.AccumulateRelative(ordinal: 0, sample: 3);
        Assert.Equal(FixedQ3232.One, seat.ConsumeRelative(intent: default).Channels.Relative(ordinal: 0));
        seat.AccumulateRelative(ordinal: 0, sample: 1);
        seat.ReleaseAllHeld();
        Assert.Equal(default, seat.ConsumeRelative(intent: default));
    }
    [Fact]
    public void RepeatedAccumulationAndConsumptionAllocateNothing() {
        var seat = Seat();

        for (var index = 0; (index < 1024); index++) { seat.AccumulateRelative(ordinal: 0, sample: 0.001); _ = seat.ConsumeRelative(intent: default); }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 1024); index++) { seat.AccumulateRelative(ordinal: 0, sample: 0.001); _ = seat.ConsumeRelative(intent: default); }
        Assert.Equal(0, (GC.GetAllocatedBytesForCurrentThread() - before));
    }
}

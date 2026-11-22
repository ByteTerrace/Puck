using Puck.Abstractions.Waiting;
using Puck.Hosting.Jobs;

namespace Puck.Hosting.Tests;

/// <summary>
/// Laws for <see cref="JobLane"/>, the hand-off a host's step thread runs on: a unit runs on the lane's thread in its
/// owner's execution context, what it throws is rethrown to the owner with its type, a lane holds one unit at a time
/// and keeps it in flight across a collect whose deadline passes, and a stopped lane ends its thread and refuses
/// further units.
/// </summary>
public sealed class JobLaneLawTests {
    private static readonly WaitReason Reason = new(Subsystem: "laws", WaitsFor: "a lane's unit");
    private static readonly AsyncLocal<string?> Ambient = new();

    private sealed class Unit(Action run) : IJobLaneUnit {
        public void Run() => run();
    }

    private static WaitDeadline Soon => WaitDeadline.In(budget: TimeSpan.FromSeconds(value: 30));

    [Fact]
    public void AUnitRunsOnTheLanesThreadInItsOwnersExecutionContext() {
        using var lane = new JobLane(name: "lane law");
        var owner = Environment.CurrentManagedThreadId;
        var ranOn = 0;
        string? seen = null;

        Ambient.Value = "the owner's";
        lane.Post(unit: new Unit(run: () => {
            ranOn = Environment.CurrentManagedThreadId;
            seen = Ambient.Value;
            Ambient.Value = "the unit's";
        }));
        Assert.True(condition: lane.InFlight);
        Assert.Equal(expected: WaitOutcome.Completed, actual: lane.Collect(deadline: Soon, reason: in Reason));
        Assert.False(condition: lane.InFlight);
        Assert.NotEqual(expected: 0, actual: ranOn);
        Assert.NotEqual(expected: owner, actual: ranOn);
        Assert.Equal(expected: "the owner's", actual: seen);
        // The unit's own change to an ambient value stays with the unit.
        Assert.Equal(expected: "the owner's", actual: Ambient.Value);

        // The same thread runs the next unit, in the context that unit is posted from.
        var second = 0;

        Ambient.Value = "changed";
        lane.Post(unit: new Unit(run: () => {
            second = Environment.CurrentManagedThreadId;
            seen = Ambient.Value;
        }));
        Assert.Equal(expected: WaitOutcome.Completed, actual: lane.Collect(deadline: Soon, reason: in Reason));
        Assert.Equal(expected: ranOn, actual: second);
        Assert.Equal(expected: "changed", actual: seen);
    }
    [Fact]
    public void WhatAUnitThrowsIsRethrownToItsOwnerAndTheLaneRunsTheNext() {
        using var lane = new JobLane(name: "lane law");

        lane.Post(unit: new Unit(run: () => throw new TimeoutException(message: "the unit's")));

        var thrown = Assert.Throws<TimeoutException>(testCode: () => _ = lane.Collect(deadline: Soon, reason: in Reason));

        Assert.Equal(expected: "the unit's", actual: thrown.Message);
        Assert.False(condition: lane.InFlight);

        var ran = false;

        lane.Post(unit: new Unit(run: () => ran = true));
        Assert.Equal(expected: WaitOutcome.Completed, actual: lane.Collect(deadline: Soon, reason: in Reason));
        Assert.True(condition: ran);
    }
    [Fact]
    public void ALaneHoldsOneUnitAndKeepsItInFlightPastACollectsDeadline() {
        using var lane = new JobLane(name: "lane law");
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);

        _ = Assert.Throws<InvalidOperationException>(testCode: () => _ = lane.Collect(deadline: Soon, reason: in Reason));

        lane.Post(unit: new Unit(run: () => {
            entered.Set();
            Assert.True(condition: release.Wait(timeout: TimeSpan.FromSeconds(value: 30)));
        }));
        Assert.True(condition: entered.Wait(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(value: 30)));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => lane.Post(unit: new Unit(run: static () => { })));
        // A deadline already passed polls, and a short one expires: the unit stays the lane's either way.
        Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: lane.Collect(deadline: default, reason: in Reason));
        Assert.Equal(expected: WaitOutcome.DeadlinePassed, actual: lane.Collect(deadline: WaitDeadline.In(budget: TimeSpan.FromMilliseconds(value: 20)), reason: in Reason));
        Assert.True(condition: lane.InFlight);

        release.Set();
        Assert.Equal(expected: WaitOutcome.Completed, actual: lane.Collect(deadline: Soon, reason: in Reason));
        Assert.False(condition: lane.InFlight);
    }
    [Fact]
    public void AStoppedLaneFinishesItsUnitEndsItsThreadAndRefusesAnother() {
        var lane = new JobLane(name: "lane law");
        var ran = false;

        // A lane that never ran a unit has no thread to end.
        using (var idle = new JobLane(name: "lane law")) {
            Assert.True(condition: idle.Stop(deadline: default));
        }

        lane.Post(unit: new Unit(run: () => ran = true));
        Assert.True(condition: lane.Stop(deadline: Soon));
        Assert.True(condition: ran);
        _ = Assert.Throws<ObjectDisposedException>(testCode: () => lane.Post(unit: new Unit(run: static () => { })));
    }
}

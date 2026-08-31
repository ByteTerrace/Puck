using Puck.Audio.Simulation;

namespace Puck.Audio.Tests;

public sealed class MusicClockTests {
    [Fact]
    public void AdvanceReportsNoBoundaryMidBeat() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 100);

        var boundary = clock.Advance(stepTicks: 50);

        Assert.Equal(actual: boundary, expected: MusicClockBoundary.None);
        Assert.Equal(expected: 0UL, actual: clock.CurrentBeat);
    }
    [Fact]
    public void AdvanceReportsBeatExactlyAtTheBoundaryTick() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 100);

        _ = clock.Advance(stepTicks: 99);
        var atBoundary = clock.Advance(stepTicks: 1);

        Assert.Equal(actual: atBoundary, expected: MusicClockBoundary.Beat);
        Assert.Equal(expected: 1UL, actual: clock.CurrentBeat);
    }
    [Fact]
    public void AdvanceReportsBarOnlyOnTheFourthBeat() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 100);

        for (var beat = 0; (beat < 3); beat++) {
            Assert.Equal(expected: MusicClockBoundary.Beat, actual: clock.Advance(stepTicks: 100));
        }

        var bar = clock.Advance(stepTicks: 100);

        Assert.Equal(actual: bar, expected: MusicClockBoundary.Beat | MusicClockBoundary.Bar);
        Assert.Equal(expected: 1UL, actual: clock.CurrentBar);
    }
    [Fact]
    public void TwoFreshClocksAdvancedIdenticallyStayBitIdentical() {
        var first = new MusicClock(beatsPerBar: 3, ticksPerBeat: 2520);
        var second = new MusicClock(beatsPerBar: 3, ticksPerBeat: 2520);
        var steps = new ulong[] { 1680, 840, 1680, 1680, 1680, 840 };

        foreach (var step in steps) {
            Assert.Equal(expected: first.Advance(stepTicks: step), actual: second.Advance(stepTicks: step));
            Assert.Equal(expected: first.ElapsedTicks, actual: second.ElapsedTicks);
            Assert.Equal(expected: first.CurrentBeat, actual: second.CurrentBeat);
            Assert.Equal(expected: first.CurrentBar, actual: second.CurrentBar);
        }
    }
}
public sealed class MusicDirectorTests {
    private static MusicSegmentGraph CalmAlertGraph() => new(Segments: [
        new MusicSegment(Id: "calm", Transitions: [
            new MusicTransition(At: MusicTransitionBoundary.BarEnd, ToSegmentId: "alert", When: MusicSenseFamily.RegionEnter),
        ]),
        new MusicSegment(Id: "alert", Transitions: [
            new MusicTransition(At: MusicTransitionBoundary.BarEnd, ToSegmentId: "calm", When: MusicSenseFamily.RegionExit),
        ]),
    ]);

    [Fact]
    public void StartsOnTheFirstDeclaredSegment() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        Assert.Equal(expected: "calm", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 0UL, actual: director.TransitionCount);
    }
    [Fact]
    public void ArmedTransitionDoesNotCommitBeforeItsBoundary() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);

        Assert.Equal(expected: "calm", actual: director.CurrentSegmentId);
        Assert.Equal(expected: "alert", actual: director.PendingSegmentId);
    }
    [Fact]
    public void ArmedTransitionCommitsExactlyOnTheNextBarBoundary() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);
        director.Step(boundary: MusicClockBoundary.Beat, edges: [], tick: 2);
        director.Step(boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar, edges: [], tick: 3);

        Assert.Equal(expected: "alert", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 1UL, actual: director.TransitionCount);
        Assert.Equal(expected: 3UL, actual: director.LastTransitionTick);
        Assert.Equal(expected: "calm", actual: director.LastTransitionFromSegmentId);
        Assert.Equal(expected: "alert", actual: director.LastTransitionToSegmentId);
        Assert.Null(@object: director.PendingSegmentId);
    }
    [Fact]
    public void ArmingAndSatisfyingTheSameTickCommitsThatTick() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(tick: 5, boundary: MusicClockBoundary.Bar, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);

        Assert.Equal(expected: "alert", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 5UL, actual: director.LastTransitionTick);
    }
    [Fact]
    public void ImmediateBoundaryCommitsTheSameTickItArms() {
        var graph = new MusicSegmentGraph(Segments: [
            new MusicSegment(Id: "calm", Transitions: [
                new MusicTransition(At: MusicTransitionBoundary.Immediate, ToSegmentId: "alert", When: MusicSenseFamily.RegionEnter),
            ]),
            new MusicSegment(Id: "alert", Transitions: []),
        ]);
        var director = new MusicDirector(graph: graph);

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);

        Assert.Equal(expected: "alert", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 1UL, actual: director.LastTransitionTick);
    }
    [Fact]
    public void NonMatchingEdgesNeverArm() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.SeatJoin)]);

        Assert.Equal(expected: "calm", actual: director.CurrentSegmentId);
        Assert.Null(@object: director.PendingSegmentId);
    }

    private static MusicSegmentGraph LayeredGraph() => new(Segments: [
        new MusicSegment(
            Id: "calm",
            Transitions: [
                new MusicTransition(At: MusicTransitionBoundary.BarEnd, ToSegmentId: "alert", When: MusicSenseFamily.RegionEnter),
            ],
            Layers: [
                new MusicLayer(TuneId: "ambient-bed", When: null),
                new MusicLayer(TuneId: "danger-bed", When: MusicSenseFamily.CollisionBegin),
            ],
            Embellishments: [
                new MusicEmbellishment(PatchId: "stinger", When: MusicSenseFamily.SeatJoin),
            ]
        ),
        new MusicSegment(Id: "alert", Transitions: [
            new MusicTransition(At: MusicTransitionBoundary.BarEnd, ToSegmentId: "calm", When: MusicSenseFamily.RegionExit),
        ]),
    ]);
    private static MusicSegmentGraph ImmediateTransitionWithEmbellishmentGraph() => new(Segments: [
        new MusicSegment(
            Id: "calm",
            Transitions: [
                new MusicTransition(At: MusicTransitionBoundary.Immediate, ToSegmentId: "alert", When: MusicSenseFamily.RegionEnter),
            ],
            Embellishments: [
                new MusicEmbellishment(PatchId: "stinger", When: MusicSenseFamily.RegionEnter),
            ]
        ),
        new MusicSegment(Id: "alert", Transitions: []),
    ]);

    [Fact]
    public void ConditionalLayerIsInactiveWithoutItsEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: []);

        Assert.DoesNotContain(expected: "danger-bed", collection: director.ActiveLayerTuneIds);
    }
    [Fact]
    public void ConditionalLayerIsActiveTheTickItsEdgeAppears() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.CollisionBegin)]);

        Assert.Contains(expected: "danger-bed", collection: director.ActiveLayerTuneIds);
    }
    [Fact]
    public void ConditionalLayerStaysInactiveOnANonMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.SeatLeave)]);

        Assert.DoesNotContain(expected: "danger-bed", collection: director.ActiveLayerTuneIds);
    }
    [Fact]
    public void UnconditionalLayerIsActiveWhileItsSegmentIsCurrent() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: []);
        Assert.Contains(expected: "ambient-bed", collection: director.ActiveLayerTuneIds);

        director.Step(tick: 2, boundary: MusicClockBoundary.Beat, edges: []);
        Assert.Contains(expected: "ambient-bed", collection: director.ActiveLayerTuneIds);
    }
    [Fact]
    public void UnconditionalLayerLeavesWhenItsSegmentDoes() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: []);
        director.Step(tick: 2, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);
        director.Step(tick: 3, boundary: MusicClockBoundary.Bar, edges: []);

        Assert.Equal(expected: "alert", actual: director.CurrentSegmentId);
        Assert.DoesNotContain(expected: "ambient-bed", collection: director.ActiveLayerTuneIds);
    }
    [Fact]
    public void EmbellishmentFiresOnceOnAMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.SeatJoin)]);

        Assert.Equal(expected: "stinger", actual: director.LastEmbellishmentPatchId);
        Assert.Equal(expected: 1UL, actual: director.LastEmbellishmentTick);
    }
    [Fact]
    public void EmbellishmentNeverFiresOnANonMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.CollisionEnd)]);

        Assert.Null(@object: director.LastEmbellishmentPatchId);
        Assert.Null(@object: director.LastEmbellishmentTick);
    }
    [Fact]
    public void EmbellishmentDoesNotReFireWithoutAFreshEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.SeatJoin)]);
        director.Step(tick: 2, boundary: MusicClockBoundary.None, edges: []);

        Assert.Equal(expected: 1UL, actual: director.LastEmbellishmentTick);
    }
    [Fact]
    public void EmbellishmentFiringNeverBlocksASameTickTransitionCommit() {
        var director = new MusicDirector(graph: ImmediateTransitionWithEmbellishmentGraph());

        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(A: 0, B: 0, Family: MusicSenseFamily.RegionEnter)]);

        Assert.Equal(expected: "alert", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 1UL, actual: director.LastTransitionTick);
        Assert.Equal(expected: "stinger", actual: director.LastEmbellishmentPatchId);
        Assert.Equal(expected: 1UL, actual: director.LastEmbellishmentTick);
    }
}
public sealed class RhythmJudgeTests {
    private static readonly JudgeWindow[] Windows = [
        new JudgeWindow(Grade: "perfect", ToleranceTicks: 100),
        new JudgeWindow(Grade: "good", ToleranceTicks: 300),
    ];

    [Fact]
    public void ExactBeatTickIsPerfect() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 2100);

        Assert.Equal(expected: "perfect", actual: RhythmJudge.Evaluate(clock: clock, tick: 2100, windows: Windows)?.Grade);
    }
    [Fact]
    public void ToleranceBoundaryTickStillMatches() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 2100);

        Assert.Equal(expected: "perfect", actual: RhythmJudge.Evaluate(clock: clock, tick: (2100 - 100), windows: Windows)?.Grade);
        Assert.Equal(expected: "perfect", actual: RhythmJudge.Evaluate(clock: clock, tick: (2100 + 100), windows: Windows)?.Grade);
    }
    [Fact]
    public void OneTickPastToleranceFallsToTheNextWindow() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 2100);

        Assert.Equal(expected: "good", actual: RhythmJudge.Evaluate(clock: clock, tick: (2100 - 101), windows: Windows)?.Grade);
        Assert.Equal(expected: "good", actual: RhythmJudge.Evaluate(clock: clock, tick: (2100 + 101), windows: Windows)?.Grade);
    }
    [Fact]
    public void PastEveryWindowIsAMiss() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 2100);

        Assert.Null(@object: RhythmJudge.Evaluate(clock: clock, tick: (2100 - 301), windows: Windows));
    }
    [Fact]
    public void EvaluateIsPureAcrossRepeatedCalls() {
        var clock = new MusicClock(beatsPerBar: 4, ticksPerBeat: 2100);

        var first = RhythmJudge.Evaluate(clock: clock, tick: 2050, windows: Windows);
        var second = RhythmJudge.Evaluate(clock: clock, tick: 2050, windows: Windows);

        Assert.Equal(actual: second, expected: first);
        Assert.Equal(expected: 0UL, actual: clock.ElapsedTicks);
    }
}

using Puck.Audio.Simulation;

namespace Puck.Audio.Tests;

public sealed class MusicClockTests {
    [Fact]
    public void AdvanceReportsBarOnlyOnTheFourthBeat() {
        var clock = new MusicClock(
            beatsPerBar: 4,
            ticksPerBeat: 100
        );

        for (var beat = 0; (beat < 3); beat++) {
            Assert.Equal(
                expected: MusicClockBoundary.Beat,
                actual: clock.Advance(stepTicks: 100)
            );
        }

        var bar = clock.Advance(stepTicks: 100);

        Assert.Equal(
            actual: bar,
            expected: MusicClockBoundary.Beat | MusicClockBoundary.Bar
        );
        Assert.Equal(
            expected: 1UL,
            actual: clock.CurrentBar
        );
    }
    [Fact]
    public void AdvanceReportsBeatExactlyAtTheBoundaryTick() {
        var clock = new MusicClock(
            beatsPerBar: 4,
            ticksPerBeat: 100
        );

        _ = clock.Advance(stepTicks: 99);
        var atBoundary = clock.Advance(stepTicks: 1);

        Assert.Equal(
            actual: atBoundary,
            expected: MusicClockBoundary.Beat
        );
        Assert.Equal(
            expected: 1UL,
            actual: clock.CurrentBeat
        );
    }
    [Fact]
    public void AdvanceReportsNoBoundaryMidBeat() {
        var clock = new MusicClock(
            beatsPerBar: 4,
            ticksPerBeat: 100
        );

        var boundary = clock.Advance(stepTicks: 50);

        Assert.Equal(
            actual: boundary,
            expected: MusicClockBoundary.None
        );
        Assert.Equal(
            expected: 0UL,
            actual: clock.CurrentBeat
        );
    }
    [Fact]
    public void TwoFreshClocksAdvancedIdenticallyStayBitIdentical() {
        var first = new MusicClock(
            beatsPerBar: 3,
            ticksPerBeat: 2520
        );
        var second = new MusicClock(
            beatsPerBar: 3,
            ticksPerBeat: 2520
        );
        var steps = new ulong[] { 1680, 840, 1680, 1680, 1680, 840 };

        foreach (var step in steps) {
            Assert.Equal(
                expected: first.Advance(stepTicks: step),
                actual: second.Advance(stepTicks: step)
            );
            Assert.Equal(
                expected: first.ElapsedTicks,
                actual: second.ElapsedTicks
            );
            Assert.Equal(
                expected: first.CurrentBeat,
                actual: second.CurrentBeat
            );
            Assert.Equal(
                expected: first.CurrentBar,
                actual: second.CurrentBar
            );
        }
    }
}
public sealed class MusicDirectorTests {
    private static MusicSegmentGraph CalmAlertGraph() => new(Segments: [
        new MusicSegment(
            Id: "calm",
            Transitions: [
            new MusicTransition(
                    At: MusicTransitionBoundary.BarEnd,
                    ToSegmentId: "alert",
                    When: MusicSenseFamily.RegionEnter
                ),
        ]
        ),
        new MusicSegment(
            Id: "alert",
            Transitions: [
            new MusicTransition(
                    At: MusicTransitionBoundary.BarEnd,
                    ToSegmentId: "calm",
                    When: MusicSenseFamily.RegionExit
                ),
        ]
        ),
    ]);
    private static MusicSegmentGraph ImmediateTransitionWithEmbellishmentGraph() => new(Segments: [
        new MusicSegment(
            Id: "calm",
            Transitions: [
                new MusicTransition(
                    At: MusicTransitionBoundary.Immediate,
                    ToSegmentId: "alert",
                    When: MusicSenseFamily.RegionEnter
                ),
            ],
            Embellishments: [
                new MusicEmbellishment(
                    PatchId: "stinger",
                    When: MusicSenseFamily.RegionEnter
                ),
            ]
        ),
        new MusicSegment(
            Id: "alert",
            Transitions: []
        ),
    ]);
    private static MusicSegmentGraph LayeredGraph() => new(Segments: [
        new MusicSegment(
            Id: "calm",
            Transitions: [
                new MusicTransition(
                    At: MusicTransitionBoundary.BarEnd,
                    ToSegmentId: "alert",
                    When: MusicSenseFamily.RegionEnter
                ),
            ],
            Layers: [
                new MusicLayer(
                    TuneId: "ambient-bed",
                    When: null
                ),
                new MusicLayer(
                    TuneId: "danger-bed",
                    When: MusicSenseFamily.RuleCue,
                    Cue: "danger"
                ),
            ],
            Embellishments: [
                new MusicEmbellishment(
                    PatchId: "stinger",
                    When: MusicSenseFamily.SeatJoin
                ),
            ]
        ),
        new MusicSegment(
            Id: "alert",
            Transitions: [
            new MusicTransition(
                    At: MusicTransitionBoundary.BarEnd,
                    ToSegmentId: "calm",
                    When: MusicSenseFamily.RegionExit
                ),
        ]
        ),
    ]);

    [Fact]
    public void ArmedTransitionCommitsExactlyOnTheNextBarBoundary() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );
        director.Step(
            boundary: MusicClockBoundary.Beat,
            edges: [],
            tick: 2
        );
        director.Step(
            boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar,
            edges: [],
            tick: 3
        );

        Assert.Equal(
            expected: "alert",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: 1UL,
            actual: director.TransitionCount
        );
        Assert.Equal(
            expected: 3UL,
            actual: director.LastTransitionTick
        );
        Assert.Equal(
            expected: "calm",
            actual: director.LastTransitionFromSegmentId
        );
        Assert.Equal(
            expected: "alert",
            actual: director.LastTransitionToSegmentId
        );
        Assert.Null(@object: director.PendingSegmentId);
    }
    [Fact]
    public void ArmedTransitionDoesNotCommitBeforeItsBoundary() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );

        Assert.Equal(
            expected: "calm",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: "alert",
            actual: director.PendingSegmentId
        );
    }
    [Fact]
    public void ArmingAndSatisfyingTheSameTickCommitsThatTick() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(
            tick: 5,
            boundary: MusicClockBoundary.Bar,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );

        Assert.Equal(
            expected: "alert",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: 5UL,
            actual: director.LastTransitionTick
        );
    }
    [Fact]
    public void ConditionalLayerIsActiveTheTickItsEdgeAppears() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RuleCue, Cue: "danger")]
        );

        Assert.Contains(
            expected: "danger-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );
    }
    [Fact]
    public void ConditionalLayerIsInactiveWithoutItsEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            boundary: MusicClockBoundary.None,
            edges: [],
            tick: 1
        );

        Assert.DoesNotContain(
            expected: "danger-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );
    }
    [Fact]
    public void ConditionalLayerStaysInactiveOnANonMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RuleCue, Cue: "calm")]
        );

        Assert.DoesNotContain(
            expected: "danger-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );
    }
    [Fact]
    public void EmbellishmentDoesNotReFireWithoutAFreshEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.SeatJoin)]
        );
        director.Step(
            boundary: MusicClockBoundary.None,
            edges: [],
            tick: 2
        );

        Assert.Equal(
            expected: 1UL,
            actual: director.LastEmbellishmentTick
        );
    }
    [Fact]
    public void EmbellishmentFiresOnceOnAMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.SeatJoin)]
        );

        Assert.Equal(
            expected: "stinger",
            actual: director.LastEmbellishmentPatchId
        );
        Assert.Equal(
            expected: 1UL,
            actual: director.LastEmbellishmentTick
        );
    }
    [Fact]
    public void EmbellishmentFiringNeverBlocksASameTickTransitionCommit() {
        var director = new MusicDirector(graph: ImmediateTransitionWithEmbellishmentGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );

        Assert.Equal(
            expected: "alert",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: 1UL,
            actual: director.LastTransitionTick
        );
        Assert.Equal(
            expected: "stinger",
            actual: director.LastEmbellishmentPatchId
        );
        Assert.Equal(
            expected: 1UL,
            actual: director.LastEmbellishmentTick
        );
    }
    [Fact]
    public void EmbellishmentNeverFiresOnANonMatchingEdge() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionExit)]
        );

        Assert.Null(@object: director.LastEmbellishmentPatchId);
        Assert.Null(@object: director.LastEmbellishmentTick);
    }
    [Fact]
    public void ImmediateBoundaryCommitsTheSameTickItArms() {
        var graph = new MusicSegmentGraph(Segments: [
            new MusicSegment(
                Id: "calm",
                Transitions: [
                new MusicTransition(
                        At: MusicTransitionBoundary.Immediate,
                        ToSegmentId: "alert",
                        When: MusicSenseFamily.RegionEnter
                    ),
            ]
            ),
            new MusicSegment(
                Id: "alert",
                Transitions: []
            ),
        ]);
        var director = new MusicDirector(graph: graph);

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );

        Assert.Equal(
            expected: "alert",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: 1UL,
            actual: director.LastTransitionTick
        );
    }
    [Fact]
    public void NonMatchingEdgesNeverArm() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        director.Step(
            tick: 1,
            boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.SeatJoin)]
        );

        Assert.Equal(
            expected: "calm",
            actual: director.CurrentSegmentId
        );
        Assert.Null(@object: director.PendingSegmentId);
    }
    [Fact]
    public void StartsOnTheFirstDeclaredSegment() {
        var director = new MusicDirector(graph: CalmAlertGraph());

        Assert.Equal(
            expected: "calm",
            actual: director.CurrentSegmentId
        );
        Assert.Equal(
            expected: 0UL,
            actual: director.TransitionCount
        );
    }
    [Fact]
    public void UnconditionalLayerIsActiveWhileItsSegmentIsCurrent() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            boundary: MusicClockBoundary.None,
            edges: [],
            tick: 1
        );
        Assert.Contains(
            expected: "ambient-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );

        director.Step(
            boundary: MusicClockBoundary.Beat,
            edges: [],
            tick: 2
        );
        Assert.Contains(
            expected: "ambient-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );
    }
    [Fact]
    public void UnconditionalLayerLeavesWhenItsSegmentDoes() {
        var director = new MusicDirector(graph: LayeredGraph());

        director.Step(
            boundary: MusicClockBoundary.None,
            edges: [],
            tick: 1
        );
        director.Step(
            tick: 2,
            boundary: MusicClockBoundary.None,
            edges: [new MusicSenseEdge(Family: MusicSenseFamily.RegionEnter)]
        );
        director.Step(
            boundary: MusicClockBoundary.Bar,
            edges: [],
            tick: 3
        );

        Assert.Equal(
            expected: "alert",
            actual: director.CurrentSegmentId
        );
        Assert.DoesNotContain(
            expected: "ambient-bed",
            collection: director.ActiveLayers.Select(selector: static active => active.Layer.TuneId)
        );
    }
    [Fact]
    public void AGriddedLayerJoinsOnItsBoundaryKeepsThatJoinTickAndRejoinsOnTheNextOne() {
        var held = new MusicSenseEdge(Family: MusicSenseFamily.RuleCue, Cue: "heat");
        var director = new MusicDirector(graph: new MusicSegmentGraph(Segments: [
            new MusicSegment(
                Id: "run",
                Transitions: [],
                Layers: [new MusicLayer(TuneId: "heat-bed", When: MusicSenseFamily.RuleCue, Cue: "heat", At: MusicTransitionBoundary.BarEnd)]
            ),
        ]));
        ulong? Joined() => director.ActiveLayers.Count == 0 ? null : director.ActiveLayers[0].JoinedTick;

        // The condition holds from tick 1, but the layer waits through a beat for the bar at tick 3.
        director.Step(tick: 1, boundary: MusicClockBoundary.None, edges: [held]);
        Assert.Null(@object: Joined());
        director.Step(tick: 2, boundary: MusicClockBoundary.Beat, edges: [held]);
        Assert.Null(@object: Joined());
        director.Step(tick: 3, boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar, edges: [held]);
        Assert.Equal(expected: 3UL, actual: Joined());
        director.Step(tick: 4, boundary: MusicClockBoundary.None, edges: [held]);
        Assert.Equal(expected: 3UL, actual: Joined());

        // It leaves the tick the condition stops holding, and a renewed hold waits for the next bar again.
        director.Step(tick: 5, boundary: MusicClockBoundary.None, edges: []);
        Assert.Null(@object: Joined());
        director.Step(tick: 6, boundary: MusicClockBoundary.Beat, edges: [held]);
        Assert.Null(@object: Joined());
        director.Step(tick: 7, boundary: MusicClockBoundary.Beat | MusicClockBoundary.Bar, edges: [held]);
        Assert.Equal(expected: 7UL, actual: Joined());
    }
    [Fact]
    public void ALayerSoundingAcrossASegmentChangeKeepsItsJoinTick() {
        var bed = new MusicLayer(TuneId: "bed", When: null, At: MusicTransitionBoundary.BarEnd);
        var director = new MusicDirector(graph: new MusicSegmentGraph(Segments: [
            new MusicSegment(
                Id: "first",
                Transitions: [new MusicTransition(ToSegmentId: "second", When: MusicSenseFamily.RuleCue, At: MusicTransitionBoundary.BeatEnd, Cue: "next")],
                Layers: [bed]
            ),
            new MusicSegment(Id: "second", Transitions: [], Layers: [bed]),
        ]));

        director.Step(tick: 1, boundary: MusicClockBoundary.Bar, edges: []);
        director.Step(tick: 2, boundary: MusicClockBoundary.None, edges: [new MusicSenseEdge(Family: MusicSenseFamily.RuleCue, Cue: "next")]);
        director.Step(tick: 3, boundary: MusicClockBoundary.Beat, edges: []);

        Assert.Equal(expected: "second", actual: director.CurrentSegmentId);
        Assert.Equal(expected: 1UL, actual: Assert.Single(collection: director.ActiveLayers).JoinedTick);
    }
}

using Puck.Commands;
using Puck.Launcher;
using Puck.SdfVm;
using Puck.World.Client;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// <c>world.patrol</c> over a fake rendered view and a clock the test advances: the grid it walks and the lines it arms,
/// the reads that turn picks into verdicts (on screen, occluded, off the body, no answer), the stalls the host timeline
/// charges a point, and every refusal by name.
/// </summary>
public sealed class WorldPatrolCommandModuleLawTests {
    // A view answering each request from a rule over its pixel, so a test chooses what the centre and the ring draw.
    private sealed class FakeView(Func<float, float, SdfPickResult?> answer) : IWorldPatrolView {
        private SdfPickResult? m_result;

        public string Instance => "world";
        public long Requests { get; private set; }
        public SdfPickResult? Result => m_result;

        public long Request(float x, float y) {
            Requests++;
            m_result = (answer(arg1: x, arg2: y) is { } picked ? (picked with { Request = Requests }) : null);

            return Requests;
        }
    }
    private sealed class Map : ISdfPickMap {
        public object? Resolve(uint identity) => ((SdfVisibility.KindOf(identity: identity) != SdfVisibilityKind.Mesh)
            ? null
            : ((SdfVisibility.SourceOf(identity: identity) == 1)
                ? new WorldPickTarget(BodyIndex: 0, Placement: null)
                : new WorldPickTarget(BodyIndex: null, Placement: "exhibit")));
    }
    private sealed class StepClock : TimeProvider {
        private long m_now = 1L;

        public override long TimestampFrequency => 1_000_000L;

        public void Advance(long microseconds) => m_now += microseconds;
        public override long GetTimestamp() => m_now;
    }

    private static readonly Map Picks = new();

    private static SdfPickResult? Body() => Hit(source: 1);
    private static SdfPickResult? Exhibit() => Hit(source: 2);
    private static SdfPickResult Hit(uint source) => new(
        Request: 0L, X: 0u, Y: 0u, Width: 1280u, Height: 900u,
        Identity: SdfVisibility.IdentityOf(kind: SdfVisibilityKind.Mesh, source: source),
        Distance: 1f, Material: 0, Program: null!, MeshRevision: 0L, Map: Picks);
    private static (CommandRegistry Registry, WorldTickScript Script) Compose(IWorldPatrolView? view, HostFrameTimeline? timeline = null) {
        var script = new WorldTickScript(completedTick: static () => 10UL, registry: static () => throw new InvalidOperationException(), source: static () => throw new InvalidOperationException());
        var module = new WorldPatrolCommandModule(script: script, timeline: timeline, view: () => view);

        return (new CommandRegistry(modules: [module]), script);
    }
    private static string Run(CommandRegistry registry, string line) => registry.Submit(line: line).Output;
    // Asks and reads the five samples of one point as the armed script does.
    private static string Visit(CommandRegistry registry, int point) {
        for (var sample = 0; (sample < WorldPatrolCommandModule.Samples); sample++) {
            if (sample > 0) { _ = Run(registry: registry, line: $"world.patrol read {point} {sample - 1}"); }
            _ = Run(registry: registry, line: $"world.patrol ask {point} {sample}");
        }

        return Run(registry: registry, line: $"world.patrol read {point} {WorldPatrolCommandModule.Samples - 1}");
    }

    [Fact]
    public void AGridIsWalkedInRowsThatAlternateAndArmedAsOneScript() {
        var (registry, script) = Compose(view: new FakeView(answer: static (_, _) => Body()));
        var armed = Run(registry: registry, line: "world.patrol 0 0 1 1 1 90 4");

        Assert.Contains(expectedSubstring: "armed 4 points (2x2), settle 4 ticks and 6 picks each", actualString: armed);
        Assert.Equal(expected: (4 * ((1 + (2 * WorldPatrolCommandModule.Samples)) + 0)), actual: script.PendingCount);

        var armedLines = script.Describe();

        Assert.Contains(expectedSubstring: "body.pose 0 - 0 90 - -", actualString: armedLines);
        Assert.Contains(expectedSubstring: "body.pose 1 - 1 90 - -", actualString: armedLines);
        Assert.True(condition: (armedLines.IndexOf(value: "body.pose 1 - 1", comparisonType: StringComparison.Ordinal) < armedLines.IndexOf(value: "body.pose 0 - 1", comparisonType: StringComparison.Ordinal)), userMessage: armedLines);
        Assert.Contains(expectedSubstring: "a patrol runs", actualString: Run(registry: registry, line: "world.patrol 0 0 1 1 1"));
        Assert.Contains(expectedSubstring: $"dropped {script.PendingCount} armed", actualString: Run(registry: registry, line: "world.patrol stop"));
        Assert.Contains(expectedSubstring: "armed 6 points (3x2)", actualString: Run(registry: registry, line: "world.patrol 4 3 2 2 1"));
        Assert.Contains(expectedSubstring: "dropped", actualString: Run(registry: registry, line: "world.patrol stop"));
        Assert.Contains(expectedSubstring: "armed 2 points", actualString: Run(registry: registry, line: "world.patrol 0 0 1 0 1 - 4 4.65"));
        Assert.Contains(expectedSubstring: "body.pose 1 4.65 0 - - -", actualString: script.Describe());
        Assert.True(condition: registry.Submit(line: "world.patrol 0 0 1 1 1 - 4 up").IsError);
    }
    [Fact]
    public void AReadNamesTheVerdictOfEachPoint() {
        var view = new FakeView(answer: static (_, _) => Body());
        var (registry, _) = Compose(view: view);

        _ = Run(registry: registry, line: "world.patrol 0 0 3 0 1");

        Assert.Contains(expectedSubstring: "point 0 on screen (centre on body)", actualString: Visit(registry: registry, point: 0));
        Assert.Equal(expected: 1L, actual: view.Requests);
    }
    [Fact]
    public void ACentreBehindAnExhibitIsOccludedAndNoPickOnTheBodyIsOffBody() {
        // The first point's ring finds the body behind an exhibit the centre hits; the second finds only the exhibit; the
        // third completes no pick, and the fourth sees only sky.
        var visits = 0;
        var view = new FakeView(answer: (x, _) => ((visits, (x < 0.44f)) switch {
            (0, false) => Exhibit(),
            (0, true) => Body(),
            (1, _) => Exhibit(),
            (3, _) => Hit(source: 0) with { Identity = 0u },
            _ => null,
        }));
        var (registry, _) = Compose(view: view);

        _ = Run(registry: registry, line: "world.patrol 0 0 3 0 1");

        Assert.Contains(expectedSubstring: "OCCLUDED (body at 1/6 picks; centre drew kind=2 source=2 placement=exhibit body=none)", actualString: Visit(registry: registry, point: 0));
        visits = 1;
        Assert.Contains(expectedSubstring: "OFF BODY (body at 0/6 picks; centre drew kind=2 source=2 placement=exhibit body=none)", actualString: Visit(registry: registry, point: 1));
        visits = 2;
        Assert.Contains(expectedSubstring: "NO ANSWER", actualString: Visit(registry: registry, point: 2));
        visits = 3;
        Assert.Contains(expectedSubstring: "OFF BODY (body at 0/6 picks; centre drew sky)", actualString: Visit(registry: registry, point: 3));

        var summary = Run(registry: registry, line: "world.patrol");

        Assert.Contains(expectedSubstring: "points=4 read=4 off-body=3 occluded=1 stalled-points=0", actualString: summary);
        Assert.Contains(expectedSubstring: "#0 (0, 0)", actualString: summary);
        Assert.Contains(expectedSubstring: "flagged points only", actualString: summary);
        Assert.Contains(expectedSubstring: "#3 (3, 0)", actualString: Run(registry: registry, line: "world.patrol all"));
    }
    [Fact]
    public void AStallIsAnIterationWhoseWorkPassedTheBudgetDuringThePoint() {
        var clock = new StepClock();
        var timeline = new HostFrameTimeline(time: clock);
        var (registry, _) = Compose(timeline: timeline, view: new FakeView(answer: static (_, _) => Body()));

        _ = Run(registry: registry, line: "world.patrol 0 0 1 0 1");

        // Two iterations closed since the patrol armed: one of 30 ms work, one of 5 ms work and a long wait.
        timeline.BeginIteration();
        clock.Advance(microseconds: 30_000L);
        timeline.Mark(phase: HostFramePhase.Produce);
        timeline.BeginIteration();
        clock.Advance(microseconds: 5_000L);
        timeline.Mark(phase: HostFramePhase.Produce);
        clock.Advance(microseconds: 20_000L);
        timeline.Mark(phase: HostFramePhase.Wait);
        timeline.BeginIteration();

        Assert.Contains(expectedSubstring: "on screen (centre on body) STALL 1/2 frames, worst work 30.0 ms", actualString: Visit(registry: registry, point: 0));
        Assert.Contains(expectedSubstring: "stalled-points=1 worst-work=30.0 ms", actualString: Run(registry: registry, line: "world.patrol"));
        Assert.Equal(expected: (2, 1, 30.0, 30.0), actual: timeline.Scan(from: 0L, to: timeline.Recorded, budgetMilliseconds: 16.7));
        Assert.Equal(expected: (0, 0, 0.0, 0.0), actual: timeline.Scan(from: 5L, to: 9L, budgetMilliseconds: 16.7));
    }
    [Fact]
    public void EveryRefusalNamesItsCause() {
        var (without, _) = Compose(view: null);
        var (registry, script) = Compose(view: new FakeView(answer: static (_, _) => Body()));

        Assert.Contains(expectedSubstring: "needs a rendered seat view", actualString: Run(registry: without, line: "world.patrol 0 0 1 1 1"));
        Assert.Contains(expectedSubstring: "ask names no armed sample", actualString: Run(registry: without, line: "world.patrol ask 0 0"));
        Assert.Contains(expectedSubstring: "none armed", actualString: Run(registry: registry, line: "world.patrol"));
        Assert.Contains(expectedSubstring: "ask names no armed sample", actualString: Run(registry: registry, line: "world.patrol ask 0 0"));
        Assert.Contains(expectedSubstring: "read names no armed sample", actualString: Run(registry: registry, line: "world.patrol read 0 0"));
        Assert.Contains(expectedSubstring: "world.patrol", actualString: Run(registry: registry, line: "world.patrol 0 0 1 1"));
        Assert.True(condition: registry.Submit(line: "world.patrol 0 0 1 1 0").IsError);
        Assert.True(condition: registry.Submit(line: "world.patrol 0 0 1 1 1 left").IsError);
        Assert.True(condition: registry.Submit(line: "world.patrol 0 0 1 1 1 90 0").IsError);
        Assert.Contains(expectedSubstring: "exceed the 200", actualString: Run(registry: registry, line: "world.patrol 0 0 20 20 1"));

        var crowd = Enumerable.Range(start: 0, count: 4000).Select(selector: static index => (((ulong)(100 + index)), "world.status")).ToArray();

        Assert.Null(@object: script.Arm(lines: crowd));
        Assert.Contains(expectedSubstring: "refused, nothing armed", actualString: Run(registry: registry, line: "world.patrol 0 0 9 9 1"));
        Assert.Contains(expectedSubstring: "none armed", actualString: Run(registry: registry, line: "world.patrol"));
    }
}

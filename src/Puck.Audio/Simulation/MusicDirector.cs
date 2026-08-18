namespace Puck.Audio.Simulation;

/// <summary>When a matched transition actually swaps segments, relative to <see cref="MusicClock"/>'s own boundary
/// crossings — never a tune's playback position.</summary>
public enum MusicTransitionBoundary : byte {
    /// <summary>Swaps on the same tick the condition is observed.</summary>
    Immediate,
    /// <summary>Swaps on the next beat boundary at or after the condition is observed.</summary>
    BeatEnd,
    /// <summary>Swaps on the next bar boundary at or after the condition is observed.</summary>
    BarEnd,
}
/// <summary>One compiled segment transition: the destination segment, the sense family that arms it, and the
/// boundary it waits for before committing.</summary>
/// <param name="ToSegmentId">The segment to switch to.</param>
/// <param name="When">The sense family that arms this transition.</param>
/// <param name="At">The boundary the armed transition waits for.</param>
public readonly record struct MusicTransition(string ToSegmentId, MusicSenseFamily When, MusicTransitionBoundary At);
/// <summary>One compiled segment: its stable id and the transitions it can arm, evaluated in declared order (the
/// first whose <see cref="MusicTransition.When"/> matches this tick's edges arms; a later match this same tick is
/// not evaluated once one has armed).</summary>
/// <param name="Id">The segment's stable id.</param>
/// <param name="Transitions">The segment's outgoing transitions.</param>
public sealed record MusicSegment(string Id, IReadOnlyList<MusicTransition> Transitions);
/// <summary>A compiled segment graph: every declared segment plus which one a director starts on (the first entry).
/// A pure value — no document parsing, no I/O.</summary>
/// <param name="Segments">Every declared segment, in authored order.</param>
public sealed record MusicSegmentGraph(IReadOnlyList<MusicSegment> Segments) {
    /// <summary>Gets the segment a new <see cref="MusicDirector"/> starts on — the first authored segment.</summary>
    public string InitialSegmentId => Segments[0].Id;

    /// <summary>Finds a declared segment by id.</summary>
    /// <param name="id">The segment id.</param>
    /// <returns>The segment, or <see langword="null"/> when no segment carries that id.</returns>
    public MusicSegment? Find(string id) {
        foreach (var segment in Segments) {
            if (string.Equals(a: segment.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return segment;
            }
        }

        return null;
    }
}
/// <summary>The event-driven segment/cue state machine: a sim-side pure function of (compiled graph, tick-ordered
/// sense edges, clock boundary crossings), ticked once per <c>WorldServer.Step</c>. Never references the project
/// that computes the world's own event feed — the host projects edges into <see cref="MusicSenseEdge"/> at its own
/// call site and hands them here. A transition ARMS the tick its <see cref="MusicTransition.When"/> family appears
/// among this tick's edges, then COMMITS the first tick at or after arming that <see cref="MusicClock"/> reports the
/// matching boundary crossed — an iMUSE-style queued transition, never a mid-bar hard cut for a
/// <see cref="MusicTransitionBoundary.BeatEnd"/>/<see cref="MusicTransitionBoundary.BarEnd"/> transition.</summary>
public sealed class MusicDirector {
    private readonly MusicSegmentGraph m_graph;

    private MusicTransition? m_armed;
    private string m_currentSegmentId;

    /// <summary>Initializes a director on the graph's initial segment, no transition armed.</summary>
    /// <param name="graph">The compiled segment graph. Must declare at least one segment.</param>
    public MusicDirector(MusicSegmentGraph graph) {
        ArgumentNullException.ThrowIfNull(argument: graph);

        if (graph.Segments.Count == 0) {
            throw new ArgumentException(message: "a music segment graph must declare at least one segment.", paramName: nameof(graph));
        }

        m_currentSegmentId = graph.InitialSegmentId;
        m_graph = graph;
    }

    /// <summary>Gets the currently active segment's id.</summary>
    public string CurrentSegmentId => m_currentSegmentId;
    /// <summary>Gets the segment id a currently armed transition will switch to, or <see langword="null"/> when
    /// nothing is armed.</summary>
    public string? PendingSegmentId => m_armed?.ToSegmentId;
    /// <summary>Gets the tick the most recent transition committed on, or <see langword="null"/> before the first
    /// one.</summary>
    public ulong? LastTransitionTick { get; private set; }
    /// <summary>Gets the segment the most recent transition switched away from, or <see langword="null"/> before the
    /// first one.</summary>
    public string? LastTransitionFromSegmentId { get; private set; }
    /// <summary>Gets the segment the most recent transition switched to, or <see langword="null"/> before the first
    /// one.</summary>
    public string? LastTransitionToSegmentId { get; private set; }
    /// <summary>Gets how many transitions have committed since construction.</summary>
    public ulong TransitionCount { get; private set; }

    /// <summary>Overwrites this director's whole live state — a checkpoint restore's one write door. Never called
    /// from ordinary simulation, which only ever advances through <see cref="Step"/>.</summary>
    /// <param name="currentSegmentId">The active segment id to resume on.</param>
    /// <param name="armed">The currently armed transition, or <see langword="null"/>.</param>
    /// <param name="transitionCount">The committed-transition count to resume from.</param>
    /// <param name="lastTransitionTick">The tick the most recent transition committed on, or <see langword="null"/>.</param>
    /// <param name="lastTransitionFromSegmentId">The segment the most recent transition left, or <see langword="null"/>.</param>
    /// <param name="lastTransitionToSegmentId">The segment the most recent transition entered, or <see langword="null"/>.</param>
    public void Restore(string currentSegmentId, MusicTransition? armed, ulong transitionCount, ulong? lastTransitionTick, string? lastTransitionFromSegmentId, string? lastTransitionToSegmentId) {
        ArgumentException.ThrowIfNullOrEmpty(argument: currentSegmentId);

        m_currentSegmentId = currentSegmentId;
        m_armed = armed;
        TransitionCount = transitionCount;
        LastTransitionTick = lastTransitionTick;
        LastTransitionFromSegmentId = lastTransitionFromSegmentId;
        LastTransitionToSegmentId = lastTransitionToSegmentId;
    }
    /// <summary>Evaluates one tick: arms a matching transition off this tick's edges (if none is already armed),
    /// then commits an armed transition whose boundary this tick's <paramref name="boundary"/> crossed.</summary>
    /// <param name="tick">The tick this step reports — recorded on commit.</param>
    /// <param name="boundary">The boundaries <see cref="MusicClock.Advance"/> crossed this same step.</param>
    /// <param name="edges">This tick's projected sense edges, in the host's own pinned order.</param>
    public void Step(ulong tick, MusicClockBoundary boundary, IReadOnlyList<MusicSenseEdge> edges) {
        if (m_armed is null) {
            Arm(edges: edges);
        }

        if (
            (m_armed is { } armed) &&
            Satisfied(at: armed.At, boundary: boundary)
        ) {
            Commit(tick: tick, transition: armed);
        }
    }

    private void Arm(IReadOnlyList<MusicSenseEdge> edges) {
        if (m_graph.Find(id: m_currentSegmentId) is not { } segment) {
            return;
        }

        foreach (var transition in segment.Transitions) {
            if (Matches(family: transition.When, edges: edges)) {
                m_armed = transition;

                return;
            }
        }
    }
    private void Commit(ulong tick, MusicTransition transition) {
        LastTransitionFromSegmentId = m_currentSegmentId;
        LastTransitionToSegmentId = transition.ToSegmentId;
        LastTransitionTick = tick;
        TransitionCount++;
        m_armed = null;
        m_currentSegmentId = transition.ToSegmentId;
    }
    private static bool Matches(MusicSenseFamily family, IReadOnlyList<MusicSenseEdge> edges) {
        foreach (var edge in edges) {
            if (edge.Family == family) {
                return true;
            }
        }

        return false;
    }
    private static bool Satisfied(MusicTransitionBoundary at, MusicClockBoundary boundary) => (at switch {
        MusicTransitionBoundary.Immediate => true,
        MusicTransitionBoundary.BeatEnd => boundary.HasFlag(flag: MusicClockBoundary.Beat),
        MusicTransitionBoundary.BarEnd => boundary.HasFlag(flag: MusicClockBoundary.Bar),
        _ => false,
    });
}

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
/// <summary>One compiled conditional audio layer: a tune id, and the sense family that gates it on top of segment
/// membership — never queued, level-triggered every tick.</summary>
/// <param name="TuneId">The tune the layer plays while active.</param>
/// <param name="When">The gating sense family, or <see langword="null"/> for "active whenever the owning segment is
/// current" (the unconditional case).</param>
public readonly record struct MusicLayer(string TuneId, MusicSenseFamily? When);
/// <summary>One compiled director embellishment: a patch id, and the sense family that fires it — instantaneous, no
/// boundary wait.</summary>
/// <param name="PatchId">The patch voiced when this embellishment fires.</param>
/// <param name="When">The firing sense family.</param>
public readonly record struct MusicEmbellishment(string PatchId, MusicSenseFamily When);
/// <summary>One compiled segment: its stable id, the transitions it can arm, its conditional audio layers, and its
/// director embellishments — each list evaluated in declared order (for transitions, the first whose
/// <see cref="MusicTransition.When"/> matches this tick's edges arms; a later match this same tick is not evaluated
/// once one has armed).</summary>
/// <param name="Id">The segment's stable id.</param>
/// <param name="Transitions">The segment's outgoing transitions.</param>
/// <param name="Layers">The segment's conditional audio layers (null = none).</param>
/// <param name="Embellishments">The segment's director embellishments (null = none).</param>
public sealed record MusicSegment(string Id, IReadOnlyList<MusicTransition> Transitions, IReadOnlyList<MusicLayer>? Layers = null, IReadOnlyList<MusicEmbellishment>? Embellishments = null) {
    private readonly IReadOnlyList<MusicLayer> m_layers = (Layers ?? []);
    private readonly IReadOnlyList<MusicEmbellishment> m_embellishments = (Embellishments ?? []);

    /// <summary>Gets the segment's conditional audio layers — never null, regardless of what the compiler passed.</summary>
    public IReadOnlyList<MusicLayer> Layers {
        get => m_layers;
        init => m_layers = (value ?? []);
    }
    /// <summary>Gets the segment's director embellishments. See <see cref="Layers"/>'s remarks.</summary>
    public IReadOnlyList<MusicEmbellishment> Embellishments {
        get => m_embellishments;
        init => m_embellishments = (value ?? []);
    }
}
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
/// matching boundary crossed — a queued transition, never a mid-bar hard cut for a
/// <see cref="MusicTransitionBoundary.BeatEnd"/>/<see cref="MusicTransitionBoundary.BarEnd"/> transition. A layer
/// never queues: its membership in <see cref="ActiveLayerTuneIds"/> is recomputed whole every <see cref="Step"/>. An
/// embellishment never queues either, but unlike a layer it is edge-triggered, not level-triggered — it fires once
/// per matching edge, recorded in <see cref="LastEmbellishmentPatchId"/>/<see cref="LastEmbellishmentTick"/>.</summary>
public sealed class MusicDirector {
    private readonly MusicSegmentGraph m_graph;
    private readonly List<string> m_activeLayerTuneIds = [];

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
    /// <summary>Gets the tune ids of every conditional audio layer active THIS tick — the current segment's
    /// unconditional layers plus every conditional layer whose <see cref="MusicLayer.When"/> matched this tick's
    /// edges. Recomputed whole by every <see cref="Step"/> call (level-triggered, never queued — unlike
    /// <see cref="PendingSegmentId"/>, nothing here survives to a tick that does not re-satisfy it), in the owning
    /// segment's declared order. Empty before the first <see cref="Step"/>.</summary>
    public IReadOnlyList<string> ActiveLayerTuneIds => m_activeLayerTuneIds;
    /// <summary>Gets the patch id the most recently fired embellishment voiced, or <see langword="null"/> before the
    /// first one.</summary>
    public string? LastEmbellishmentPatchId { get; private set; }
    /// <summary>Gets the tick the most recent embellishment fired on, or <see langword="null"/> before the first
    /// one.</summary>
    public ulong? LastEmbellishmentTick { get; private set; }

    /// <summary>Overwrites this director's whole live state — a checkpoint restore's one write door. Never called
    /// from ordinary simulation, which only ever advances through <see cref="Step"/>.</summary>
    /// <param name="currentSegmentId">The active segment id to resume on.</param>
    /// <param name="armed">The currently armed transition, or <see langword="null"/>.</param>
    /// <param name="transitionCount">The committed-transition count to resume from.</param>
    /// <param name="lastTransitionTick">The tick the most recent transition committed on, or <see langword="null"/>.</param>
    /// <param name="lastTransitionFromSegmentId">The segment the most recent transition left, or <see langword="null"/>.</param>
    /// <param name="lastTransitionToSegmentId">The segment the most recent transition entered, or <see langword="null"/>.</param>
    /// <param name="lastEmbellishmentPatchId">The patch the most recent embellishment voiced, or <see langword="null"/>.</param>
    /// <param name="lastEmbellishmentTick">The tick the most recent embellishment fired on, or <see langword="null"/>.</param>
    public void Restore(string currentSegmentId, MusicTransition? armed, ulong transitionCount, ulong? lastTransitionTick, string? lastTransitionFromSegmentId, string? lastTransitionToSegmentId, string? lastEmbellishmentPatchId = null, ulong? lastEmbellishmentTick = null) {
        ArgumentException.ThrowIfNullOrEmpty(argument: currentSegmentId);

        m_currentSegmentId = currentSegmentId;
        m_armed = armed;
        TransitionCount = transitionCount;
        LastTransitionTick = lastTransitionTick;
        LastTransitionFromSegmentId = lastTransitionFromSegmentId;
        LastTransitionToSegmentId = lastTransitionToSegmentId;
        LastEmbellishmentPatchId = lastEmbellishmentPatchId;
        LastEmbellishmentTick = lastEmbellishmentTick;
        // Active layers are purely level-triggered off this tick's edges (see ActiveLayerTuneIds' remarks) — never
        // sticky state, so a restore leaves the list empty until the next Step recomputes it, the same posture
        // PendingSegmentId already takes before its first Arm.
        m_activeLayerTuneIds.Clear();
    }
    /// <summary>Evaluates one tick: arms a matching transition off this tick's edges (if none is already armed),
    /// fires any embellishment whose condition this tick's edges satisfy, commits an armed transition whose boundary
    /// this tick's <paramref name="boundary"/> crossed, then recomputes the active layer set off the (possibly just
    /// switched) current segment.</summary>
    /// <param name="tick">The tick this step reports — recorded on commit and on an embellishment firing.</param>
    /// <param name="boundary">The boundaries <see cref="MusicClock.Advance"/> crossed this same step.</param>
    /// <param name="edges">This tick's projected sense edges, in the host's own pinned order.</param>
    public void Step(ulong tick, MusicClockBoundary boundary, IReadOnlyList<MusicSenseEdge> edges) {
        if (m_armed is null) {
            Arm(edges: edges);
        }

        FireEmbellishments(
            edges: edges,
            tick: tick
        );

        if (
            (m_armed is { } armed) &&
            Satisfied(at: armed.At, boundary: boundary)
        ) {
            Commit(tick: tick, transition: armed);
        }

        RecomputeActiveLayers(edges: edges);
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
    // Evaluated against the segment current BEFORE this tick's possible Commit — an embellishment belongs to the
    // segment that was playing when its edge arrived, the same pre-commit reading Arm takes. The first matching
    // embellishment in declared order fires; a later match this same tick is not evaluated, mirroring Arm's own
    // first-match rule.
    private void FireEmbellishments(IReadOnlyList<MusicSenseEdge> edges, ulong tick) {
        if (m_graph.Find(id: m_currentSegmentId) is not { } segment) {
            return;
        }

        foreach (var embellishment in segment.Embellishments) {
            if (Matches(family: embellishment.When, edges: edges)) {
                LastEmbellishmentPatchId = embellishment.PatchId;
                LastEmbellishmentTick = tick;

                return;
            }
        }
    }
    // Evaluated against the FINAL current segment for this tick (after any Commit above) — the active set always
    // describes the segment now playing. Rebuilt in place (clear + refill) so a director that never changes its
    // active set allocates nothing steady-state.
    private void RecomputeActiveLayers(IReadOnlyList<MusicSenseEdge> edges) {
        m_activeLayerTuneIds.Clear();

        if (m_graph.Find(id: m_currentSegmentId) is not { } segment) {
            return;
        }

        foreach (var layer in segment.Layers) {
            if (
                (layer.When is not { } when) ||
                Matches(family: when, edges: edges)
            ) {
                m_activeLayerTuneIds.Add(item: layer.TuneId);
            }
        }
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

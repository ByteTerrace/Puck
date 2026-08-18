using Puck.Audio.Simulation;

namespace Puck.World.Server;

/// <summary>Compiles authored music/judge documents into the sim-side <c>Puck.Audio.Simulation</c> shapes, and
/// projects one tick's <see cref="WorldEventEdge"/> list into <see cref="MusicSenseEdge"/>s — the two document-
/// and-vocabulary-aware conversions <c>Puck.Audio</c> itself cannot perform (it parses no document and cannot
/// reference the project that declares <see cref="WorldEventEdge"/>).</summary>
internal static class MusicDirectorFactory {
    /// <summary>Compiles an authored score into a sim-side segment graph. A transition whose <c>when</c> token has
    /// no sense-family mapping is dropped — the world schema validator already refuses such a token before this
    /// ever runs, so this is a defensive skip, not a silent policy.</summary>
    /// <param name="document">The validated, normalized music document.</param>
    /// <returns>The compiled segment graph.</returns>
    public static MusicSegmentGraph CompileGraph(Puck.Forge.Authoring.MusicDocument document) {
        var segments = new List<MusicSegment>(capacity: document.Segments.Count);

        foreach (var segment in document.Segments) {
            var transitions = new List<MusicTransition>();

            foreach (var transition in (segment.Transitions ?? [])) {
                if (ParseFamily(token: transition.When) is not { } family) {
                    continue;
                }

                transitions.Add(item: new MusicTransition(
                    At: CompileBoundary(boundary: (transition.At ?? Puck.Forge.Authoring.MusicTransitionBoundary.BarEnd)),
                    ToSegmentId: transition.To,
                    When: family
                ));
            }

            segments.Add(item: new MusicSegment(
                Id: segment.Id,
                Transitions: transitions
            ));
        }

        return new MusicSegmentGraph(Segments: segments);
    }
    /// <summary>Compiles an authored judge document into a sim-side window list.</summary>
    /// <param name="document">The validated, normalized judge document.</param>
    /// <returns>The compiled windows, in authored order.</returns>
    public static IReadOnlyList<JudgeWindow> CompileWindows(Puck.Forge.Authoring.JudgeDocument document) {
        var windows = new List<JudgeWindow>(capacity: document.Windows.Count);

        foreach (var window in document.Windows) {
            windows.Add(item: new JudgeWindow(
                Grade: window.Grade,
                ToleranceTicks: window.ToleranceTicks
            ));
        }

        return windows;
    }
    /// <summary>Projects one tick's world-scoped event edges into the audio-owned sense-edge shape, dropping the
    /// grant-gating fields (music state is never addon-observation-filtered).</summary>
    /// <param name="edges">This tick's <see cref="WorldEventFeed.Edges"/>.</param>
    /// <returns>The projected edges, in the same pinned order.</returns>
    public static List<MusicSenseEdge> ProjectSenseEdges(IReadOnlyList<WorldEventEdge> edges) {
        var projected = new List<MusicSenseEdge>(capacity: edges.Count);

        foreach (var edge in edges) {
            projected.Add(item: new MusicSenseEdge(
                A: edge.A,
                B: edge.B,
                Family: CompileFamily(family: edge.Family)
            ));
        }

        return projected;
    }

    private static MusicTransitionBoundary CompileBoundary(Puck.Forge.Authoring.MusicTransitionBoundary boundary) => (boundary switch {
        Puck.Forge.Authoring.MusicTransitionBoundary.Immediate => MusicTransitionBoundary.Immediate,
        Puck.Forge.Authoring.MusicTransitionBoundary.BeatEnd => MusicTransitionBoundary.BeatEnd,
        _ => MusicTransitionBoundary.BarEnd,
    });
    private static MusicSenseFamily CompileFamily(WorldEventFamily family) => (family switch {
        WorldEventFamily.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldEventFamily.RegionExit => MusicSenseFamily.RegionExit,
        WorldEventFamily.SeatJoin => MusicSenseFamily.SeatJoin,
        WorldEventFamily.SeatLeave => MusicSenseFamily.SeatLeave,
        WorldEventFamily.CollisionBegin => MusicSenseFamily.CollisionBegin,
        WorldEventFamily.CollisionEnd => MusicSenseFamily.CollisionEnd,
        WorldEventFamily.RouteEngaged => MusicSenseFamily.RouteEngaged,
        WorldEventFamily.RouteDisengaged => MusicSenseFamily.RouteDisengaged,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(family), actualValue: family, message: $"no sense-family mapping for '{family}'."),
    });
    // Only the sense-mappable subset of WorldAudioCue.EventTokens names a MusicSenseFamily; the rest (mutation.*,
    // grant.denied, screen.*, player.*, music.transition) are cue-only tokens with no corresponding event edge.
    private static MusicSenseFamily? ParseFamily(string token) => (token switch {
        WorldAudioCue.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldAudioCue.RegionExit => MusicSenseFamily.RegionExit,
        WorldAudioCue.SeatJoin => MusicSenseFamily.SeatJoin,
        _ => null,
    });
}

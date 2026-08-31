using Puck.Audio.Simulation;

namespace Puck.World.Server;

/// <summary>Compiles authored music/judge documents into the sim-side <c>Puck.Audio.Simulation</c> shapes, and
/// projects one tick's <see cref="WorldEventEdge"/> list into <see cref="MusicSenseEdge"/>s — the two document-
/// and-vocabulary-aware conversions <c>Puck.Audio</c> itself cannot perform (it parses no document and cannot
/// reference the project that declares <see cref="WorldEventEdge"/>).</summary>
public static class MusicDirectorFactory {
    /// <summary>Compiles an authored score into a sim-side segment graph. Every non-null <c>when</c> token maps to a
    /// sense family by construction: the world schema validator refuses any token outside
    /// <see cref="WorldAudioCue.MusicWhenTokens"/>, the single source <see cref="ParseFamily"/> mirrors. Neither a
    /// layer's authored <c>gainThousandths</c> nor an embellishment's is compiled here — see
    /// <c>Puck.Forge.Authoring.MusicLayerDocument.GainThousandths</c>'s remarks.</summary>
    /// <param name="document">The validated, normalized music document.</param>
    /// <returns>The compiled segment graph.</returns>
    public static MusicSegmentGraph CompileGraph(Puck.Forge.Authoring.MusicDocument document) {
        var segments = new List<MusicSegment>(capacity: document.Segments.Count);

        foreach (var segment in document.Segments) {
            var transitions = new List<MusicTransition>();

            foreach (var transition in (segment.Transitions ?? [])) {
                transitions.Add(item: new MusicTransition(
                    At: CompileBoundary(boundary: (transition.At ?? Puck.Forge.Authoring.MusicTransitionBoundary.BarEnd)),
                    ToSegmentId: transition.To,
                    When: ParseFamily(token: transition.When)
                ));
            }

            var layers = new List<MusicLayer>();

            foreach (var layer in (segment.Layers ?? [])) {
                // A null When is the unconditional case (see MusicLayer's own remarks).
                layers.Add(item: new MusicLayer(
                    TuneId: layer.TuneId,
                    When: ((layer.When is { } when) ? ParseFamily(token: when) : null)
                ));
            }

            var embellishments = new List<MusicEmbellishment>();

            foreach (var embellishment in (segment.Embellishments ?? [])) {
                embellishments.Add(item: new MusicEmbellishment(PatchId: embellishment.PatchId, When: ParseFamily(token: embellishment.When)));
            }

            segments.Add(item: new MusicSegment(
                Id: segment.Id,
                Transitions: transitions,
                Layers: layers,
                Embellishments: embellishments
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
    /// <remarks><see cref="MusicSenseFamily"/> covers a SUBSET of <see cref="WorldEventFamily"/>: an edge family the
    /// music vocabulary does not name is skipped, never thrown on. A world-event family added without a music
    /// counterpart must not be able to kill the tick that emits it.</remarks>
    /// <param name="edges">This tick's <see cref="WorldEventFeed.Edges"/>.</param>
    /// <returns>The projected edges the music vocabulary names, in the same pinned order.</returns>
    public static List<MusicSenseEdge> ProjectSenseEdges(IReadOnlyList<WorldEventEdge> edges) {
        var projected = new List<MusicSenseEdge>(capacity: edges.Count);

        foreach (var edge in edges) {
            if (CompileFamily(family: edge.Family) is not { } family) {
                continue;
            }

            projected.Add(item: new MusicSenseEdge(
                A: edge.A,
                B: edge.B,
                Family: family
            ));
        }

        return projected;
    }

    private static MusicTransitionBoundary CompileBoundary(Puck.Forge.Authoring.MusicTransitionBoundary boundary) => (boundary switch {
        Puck.Forge.Authoring.MusicTransitionBoundary.Immediate => MusicTransitionBoundary.Immediate,
        Puck.Forge.Authoring.MusicTransitionBoundary.BeatEnd => MusicTransitionBoundary.BeatEnd,
        _ => MusicTransitionBoundary.BarEnd,
    });
    // Null for a world-event family the music sense vocabulary does not name — the same open-set posture ParseFamily
    // takes for a cue token with no edge behind it.
    private static MusicSenseFamily? CompileFamily(WorldEventFamily family) => (family switch {
        WorldEventFamily.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldEventFamily.RegionExit => MusicSenseFamily.RegionExit,
        WorldEventFamily.SeatJoin => MusicSenseFamily.SeatJoin,
        WorldEventFamily.SeatLeave => MusicSenseFamily.SeatLeave,
        WorldEventFamily.CollisionBegin => MusicSenseFamily.CollisionBegin,
        WorldEventFamily.CollisionEnd => MusicSenseFamily.CollisionEnd,
        WorldEventFamily.RouteEngaged => MusicSenseFamily.RouteEngaged,
        WorldEventFamily.RouteDisengaged => MusicSenseFamily.RouteDisengaged,
        _ => null,
    });
    /// <summary>Maps a <see cref="WorldAudioCue.MusicWhenTokens"/> token to its sense family.</summary>
    /// <remarks>KEEP IN SYNC with <see cref="WorldAudioCue.MusicWhenTokens"/> — the arms here and that list are the
    /// same set (<c>MusicWhenTokenLawTests</c> pins the closure). Cue-only tokens never reach this method: the world
    /// schema validator refuses them, so an unmapped token can only mean the list and this mapping drifted.</remarks>
    /// <param name="token">The authored <c>when</c> token.</param>
    /// <returns>The sense family the token names.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="token"/> has no sense-family mapping.</exception>
    public static MusicSenseFamily ParseFamily(string token) => (token switch {
        WorldAudioCue.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldAudioCue.RegionExit => MusicSenseFamily.RegionExit,
        WorldAudioCue.SeatJoin => MusicSenseFamily.SeatJoin,
        _ => throw new InvalidOperationException(message: $"'{token}' has no sense-family mapping — extend ParseFamily in the change that grows WorldAudioCue.MusicWhenTokens."),
    });
}

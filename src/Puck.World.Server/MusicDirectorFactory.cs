using Puck.Audio.Simulation;

namespace Puck.World.Server;

/// <summary>Compiles an authored music document into the <c>Puck.Audio.Simulation</c> segment graph a listener's
/// <see cref="MusicDirector"/> steps, and names the ambient world events a recipient is told about as sense families
/// — the document- and vocabulary-aware conversions <c>Puck.Audio</c> itself cannot perform (it parses no document
/// and cannot reference the project that declares <see cref="WorldEventFamily"/>).</summary>
public static class MusicDirectorFactory {
    private static MusicTransitionBoundary CompileBoundary(Puck.World.Authoring.MusicTransitionBoundary boundary) => (boundary switch {
        Puck.World.Authoring.MusicTransitionBoundary.Immediate => MusicTransitionBoundary.Immediate,
        Puck.World.Authoring.MusicTransitionBoundary.BeatEnd => MusicTransitionBoundary.BeatEnd,
        _ => MusicTransitionBoundary.BarEnd,
    });

    /// <summary>Gets the sense family a world event delivers to a score as, or <see langword="null"/> for an event the
    /// score vocabulary does not name.</summary>
    /// <param name="family">The world event's family.</param>
    /// <returns>The sense family, or <see langword="null"/>.</returns>
    public static MusicSenseFamily? SenseOf(WorldEventFamily family) => (family switch {
        WorldEventFamily.RegionEnter => MusicSenseFamily.RegionEnter,
        WorldEventFamily.RegionExit => MusicSenseFamily.RegionExit,
        WorldEventFamily.SeatJoin => MusicSenseFamily.SeatJoin,
        _ => null,
    });

    /// <summary>Compiles an authored score into a segment graph. Every non-null <c>when</c> token maps to a sense
    /// family by construction: the world schema validator refuses any token outside
    /// <see cref="WorldAudioCue.IsMusicWhenToken"/>. The compiled layers carry their mix gains and join grid points;
    /// a layer that authors none joins immediately.</summary>
    /// <param name="document">The validated, normalized music document.</param>
    /// <returns>The compiled segment graph.</returns>
    public static MusicSegmentGraph CompileGraph(Puck.World.Authoring.MusicDocument document) {
        var segments = new List<MusicSegment>(capacity: document.Segments.Count);

        foreach (var segment in document.Segments) {
            var transitions = new List<MusicTransition>();

            foreach (var transition in (segment.Transitions ?? [])) {
                transitions.Add(item: new MusicTransition(
                    At: CompileBoundary(boundary: (transition.At ?? Puck.World.Authoring.MusicTransitionBoundary.BarEnd)),
                    ToSegmentId: transition.To,
                    When: ParseFamily(token: transition.When),
                    Cue: WorldAudioCue.MusicCueName(transition.When)
                ));
            }

            var layers = new List<MusicLayer>();

            foreach (var layer in (segment.Layers ?? [])) {
                // A null When is the unconditional case (see MusicLayer's own remarks).
                layers.Add(item: new MusicLayer(
                    TuneId: layer.TuneId,
                    GainThousandths: layer.GainThousandths ?? 1000,
                    Cue: WorldAudioCue.MusicCueName(layer.When),
                    When: ((layer.When is { } when)
                    ? ParseFamily(token: when)
                    : null),
                    At: CompileBoundary(boundary: (layer.At ?? Puck.World.Authoring.MusicTransitionBoundary.Immediate))
                ));
            }

            var embellishments = new List<MusicEmbellishment>();

            foreach (var embellishment in (segment.Embellishments ?? [])) {
                embellishments.Add(item: new MusicEmbellishment(
                    PatchId: embellishment.PatchId,
                    When: ParseFamily(token: embellishment.When),
                    Cue: WorldAudioCue.MusicCueName(embellishment.When)
                ));
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
        _ when WorldAudioCue.MusicCueName(token) is not null => MusicSenseFamily.RuleCue,
        _ => throw new InvalidOperationException(message: $"'{token}' has no sense-family mapping."),
    });
}

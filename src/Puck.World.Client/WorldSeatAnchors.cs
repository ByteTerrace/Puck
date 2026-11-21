namespace Puck.World.Client;

/// <summary>Selects a camera's anchor for a seat, names the body a seat-relative anchor kind rides —
/// <see cref="WorldAnchor.Seat"/> (through the seat's perceived body, so possession follows) and
/// <see cref="WorldAnchor.RecentSpeaker"/> (through <see cref="WorldSpeechClock"/>) — and names the view registration a
/// camera renders under for a seat. <see cref="WorldAnchorPoses"/> resolves the pose.</summary>
public static class WorldSeatAnchors {
    /// <summary>Returns the body a seat-relative anchor rides for <paramref name="slot"/>, or -1 when it resolves
    /// nothing (an unjoined explicit seat still resolves its perceived body; a recent speaker resolves only once
    /// something has spoken). A non-seat-relative anchor returns -1.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <param name="slot">The 0-based enclosing seat a <c>Seat</c> anchor with no number resolves against.</param>
    /// <param name="perception">The per-seat perception anchor.</param>
    /// <param name="speech">The speech clock.</param>
    public static int BodyOf(WorldAnchor? anchor, int slot, WorldPerceptionAnchor perception, WorldSpeechClock speech) {
        ArgumentNullException.ThrowIfNull(argument: perception);
        ArgumentNullException.ThrowIfNull(argument: speech);

        return anchor switch {
            WorldAnchor.Seat seat => perception.PerceivedBody(slot: ((seat.Number is { } number)
            ? (number - 1)
            : slot)),
            WorldAnchor.RecentSpeaker => speech.RecentSpeakerBody,
            _ => -1,
        };
    }
    /// <summary>Returns the part id a seat-relative anchor names, or <see langword="null"/> for its root.</summary>
    /// <param name="anchor">The anchor.</param>
    public static string? PartOf(WorldAnchor? anchor) => anchor switch {
        WorldAnchor.Seat seat => seat.PartId,
        WorldAnchor.RecentSpeaker speaker => speaker.PartId,
        _ => null,
    };
    /// <summary>Returns the view registration name a camera renders under for <paramref name="seat"/>: a
    /// seat-relative camera (<see cref="WorldCamera.IsSeatRelative"/>) registers one view per seat under its generated
    /// seat view name (<see cref="WorldViewNames.Seat"/>); any other camera registers once under its own name.</summary>
    /// <param name="camera">The camera row.</param>
    /// <param name="seat">The 1-based seat.</param>
    public static string RegistrationName(WorldCamera camera, int seat) {
        ArgumentNullException.ThrowIfNull(argument: camera);

        return (camera.IsSeatRelative
            ? WorldViewNames.Seat(
                camera: camera.Name,
                seat: seat
            )
            : camera.Name
        );
    }
    /// <summary>Selects the anchor a camera rides this frame for <paramref name="slot"/>: the bare
    /// <see cref="WorldCamera.Anchor"/>, or the first <see cref="WorldCamera.Anchors"/> candidate whose condition
    /// holds. <paramref name="candidateIndex"/> is that candidate's index, or -1 (bare anchor, or none holding —
    /// which returns <see langword="null"/>, the world frame).</summary>
    /// <param name="camera">The camera row.</param>
    /// <param name="slot">The 0-based seat the view is resolved for.</param>
    /// <param name="evaluator">The predicate evaluator; <see langword="null"/> treats every condition as holding.</param>
    /// <param name="candidateIndex">The winning candidate's index, or -1.</param>
    public static WorldAnchor? SelectAnchor(WorldCamera camera, int slot, IOverlayPredicateEvaluator? evaluator, out int candidateIndex) {
        ArgumentNullException.ThrowIfNull(argument: camera);

        if (camera.Anchors is not { Count: > 0 } candidates) {
            candidateIndex = -1;

            return camera.Anchor;
        }

        candidateIndex = OverlayRanking.FirstHolding(
            candidates: candidates,
            evaluator: evaluator,
            slot: slot,
            when: static candidate => candidate.When
        );

        return ((candidateIndex >= 0)
            ? candidates[candidateIndex].Anchor
            : null
        );
    }
}

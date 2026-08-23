using System.Globalization;
using Puck.SdfVm;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>Resolves the seat-relative anchor kinds — <see cref="WorldAnchor.Seat"/> (through the seat's perceived
/// body, so possession follows) and <see cref="WorldAnchor.RecentSpeaker"/> (through <see cref="WorldSpeechClock"/>)
/// — and names the per-seat view registration a seat-relative camera renders under. The one resolver the binder's
/// offscreen views, the main-window presenter, and the audio director share.</summary>
public static class WorldSeatAnchors {
    private const string SeatQualifier = "@seat:";

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
            WorldAnchor.Seat seat => perception.PerceivedBody(slot: ((seat.Number is { } number) ? (number - 1) : slot)),
            WorldAnchor.RecentSpeaker => speech.RecentSpeakerBody,
            _ => -1,
        };
    }
    /// <summary>Returns a value indicating whether <paramref name="anchor"/> is a seat-relative kind.</summary>
    /// <param name="anchor">The anchor.</param>
    public static bool IsSeatRelative(WorldAnchor? anchor) => anchor is WorldAnchor.Seat or WorldAnchor.RecentSpeaker;
    /// <summary>Returns the part id a seat-relative anchor names, or <see langword="null"/> for its root.</summary>
    /// <param name="anchor">The anchor.</param>
    public static string? PartOf(WorldAnchor? anchor) => anchor switch {
        WorldAnchor.Seat seat => seat.PartId,
        WorldAnchor.RecentSpeaker speaker => speaker.PartId,
        _ => null,
    };
    /// <summary>Returns the view registration name a camera renders under for <paramref name="seat"/>: a
    /// seat-relative camera (<see cref="WorldCamera.IsSeatRelative"/>) registers one view per seat under a
    /// seat-qualified name; any other camera registers once under its own name.</summary>
    /// <param name="camera">The camera row.</param>
    /// <param name="seat">The 1-based seat.</param>
    public static string RegistrationName(WorldCamera camera, int seat) {
        ArgumentNullException.ThrowIfNull(argument: camera);

        return (camera.IsSeatRelative
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"{camera.Name}{SeatQualifier}{seat}"
            )
            : camera.Name
        );
    }
    /// <summary>Resolves a seat-relative anchor's pose for <paramref name="slot"/> — the body's root pose, or its
    /// named part from the packed transforms.</summary>
    /// <param name="anchor">The anchor; a non-seat-relative kind resolves nothing.</param>
    /// <param name="slot">The 0-based enclosing seat.</param>
    /// <param name="client">The client supplying body poses.</param>
    /// <param name="stamps">The stamp pool supplying part tables.</param>
    /// <param name="perception">The per-seat perception anchor.</param>
    /// <param name="speech">The speech clock.</param>
    /// <param name="transforms">The current packed transforms (parts only).</param>
    /// <param name="pose">The resolved pose, or default.</param>
    public static bool TryResolve(WorldAnchor? anchor, int slot, WorldClient client, WorldStampPool stamps, WorldPerceptionAnchor perception, WorldSpeechClock speech, ReadOnlySpan<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: stamps);

        if (!TryResolveBody(
            anchor: anchor,
            body: out var body,
            client: client,
            perception: perception,
            slot: slot,
            speech: speech
        )) {
            pose = default;

            return false;
        }

        if (PartOf(anchor: anchor) is { } partId) {
            return WorldEntityPartResolver.TryPackedPose(
                client: client,
                entityIndex: body,
                partId: partId,
                pose: out pose,
                stamps: stamps,
                transforms: transforms
            );
        }

        pose = RootPose(
            body: body,
            client: client
        );

        return true;
    }
    /// <summary>Resolves a seat-relative anchor's pose from a list-backed transform buffer.</summary>
    /// <inheritdoc cref="TryResolve(WorldAnchor?, int, WorldClient, WorldStampPool, WorldPerceptionAnchor, WorldSpeechClock, ReadOnlySpan{DynamicTransform}, out SdfAnchor)"/>
    public static bool TryResolve(WorldAnchor? anchor, int slot, WorldClient client, WorldStampPool stamps, WorldPerceptionAnchor perception, WorldSpeechClock speech, IReadOnlyList<DynamicTransform> transforms, out SdfAnchor pose) {
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: stamps);

        if (!TryResolveBody(
            anchor: anchor,
            body: out var body,
            client: client,
            perception: perception,
            slot: slot,
            speech: speech
        )) {
            pose = default;

            return false;
        }

        if (PartOf(anchor: anchor) is { } partId) {
            return WorldEntityPartResolver.TryPackedPose(
                client: client,
                entityIndex: body,
                partId: partId,
                pose: out pose,
                stamps: stamps,
                transforms: transforms
            );
        }

        pose = RootPose(
            body: body,
            client: client
        );

        return true;
    }
    private static SdfAnchor RootPose(int body, WorldClient client) => new(
        Orientation: client.Orientation(index: body),
        Position: client.Position(index: body)
    );
    private static bool TryResolveBody(WorldAnchor? anchor, int slot, WorldClient client, WorldPerceptionAnchor perception, WorldSpeechClock speech, out int body) {
        body = BodyOf(
            anchor: anchor,
            perception: perception,
            slot: slot,
            speech: speech
        );

        return ((((uint)body) < ((uint)WorldClient.EntityCapacity)) && client.IsActive(index: body));
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

using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.World.Server;

/// <summary>One body's last step crossing one authored adjacency's ownership face.</summary>
/// <param name="Adjacency">The authored adjacency row whose face was crossed.</param>
/// <param name="Seat">The 0-based body index.</param>
/// <param name="Frame">The face's compiled frame.</param>
/// <param name="SeamU">The crossing's fixed-point position across the face.</param>
/// <param name="SeamV">The crossing's fixed-point position up the face.</param>
/// <param name="Parameter">Where along the step the face was crossed, from 0 at its start to 1 at its end.</param>
public readonly record struct WorldAdjacencyFaceHit(WorldAdjacency Adjacency, int Seat, WorldFaceFrame Frame, FixedQ4816 SeamU, FixedQ4816 SeamV, FixedQ4816 Parameter);
/// <summary>The ownership sweep an authority runs against its own authored adjacency faces. It reads only the
/// authority's own definition, population and arrival-border latches, so the authority runs it itself: the host's
/// post-step scan selects one winning face per body and mints its crossing, and the authority's own step settles
/// every pending adjacency continuum before it advances, whether a host drives it or a replay does.</summary>
public static class WorldAdjacencyOwnership {
    /// <summary>Reads the active body at <paramref name="index"/> that can cross a seam or a door: a local seat, an
    /// admitted peer's traveler, or a body the world's own program authors.</summary>
    /// <param name="population">The population.</param>
    /// <param name="index">The 0-based body index.</param>
    /// <param name="body">The body, when the index holds one that can travel.</param>
    /// <returns><see langword="true"/> when the index holds a body that can travel.</returns>
    public static bool TryTraveller(WorldPopulation population, int index, [NotNullWhen(returnValue: true)] out WorldBody? body) {
        ArgumentNullException.ThrowIfNull(argument: population);

        body = (population.IsActive(index: index)
            ? population.EntryBody(index: index)
            : null);

        return (body is not null);
    }
    /// <summary>Collects every ownership face each eligible body's last step crossed, per body. A committed arrival's
    /// authenticated source border stays latched until both ends of a step lie past that border's ownership threshold,
    /// and the latch is released here when they do. The caller holds the authority gate.</summary>
    /// <param name="server">The authority whose faces are swept.</param>
    /// <param name="pendingOnly">Whether only bodies holding a pending adjacency continuum are swept.</param>
    /// <param name="held">Whether a body index is held out of the sweep, or <see langword="null"/> to hold none.</param>
    /// <returns>The crossings per body index; an entry is <see langword="null"/> where the body crossed nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static List<WorldAdjacencyFaceHit>?[] Sweep(WorldServer server, bool pendingOnly, Func<int, bool>? held = null) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var population = server.Population;
        var candidates = new List<WorldAdjacencyFaceHit>?[population.Capacity];

        if (server.Definition.Adjacencies is not { Count: > 0 } adjacencies) {
            return candidates;
        }

        _ = WorldAdjacencyPolicy.TryReciprocalHysteresis(
            definition: server.Definition,
            depth: out var reciprocalHysteresis,
            reason: out _
        );
        _ = WorldAdjacencyPolicy.TryVerticalOwnershipDeadband(
            definition: server.Definition,
            depth: out var verticalOwnershipDeadband,
            reason: out _
        );

        foreach (var adjacency in adjacencies) {
            if (adjacency is null) {
                continue;
            }

            var frame = adjacency.Boundary.CompileFrame();
            var ownershipThreshold = FixedQ4816.Max(
                x: FixedQ4816.FromDouble(value: adjacency.Hysteresis),
                y: WorldAdjacencyPolicy.OwnershipThreshold(
                    frame: in frame,
                    reciprocalHysteresis: reciprocalHysteresis,
                    verticalOwnershipDeadband: verticalOwnershipDeadband
                )
            );

            for (var seat = 0; (seat < population.Capacity); seat++) {
                if (!TryTraveller(
                    body: out var body,
                    index: seat,
                    population: population
                )) {
                    continue;
                }
                if (
                    pendingOnly &&
                    (body.PendingContinuum is null)
                ) {
                    continue;
                }
                if (held?.Invoke(arg: seat) == true) {
                    continue;
                }

                // A remotely committed arrival bypasses the host's publication path, but escrow retains the
                // authenticated source border on the destination authority. Handoff occurs at the far side of the
                // boundary's own ownership threshold, so a mapped arrival starts at least that far inside the new
                // owner: a wall carries the reciprocal contact hysteresis, a floor/ceiling the vertical ownership
                // deadband. Test both ends of this step: a genuine reversal may cross the whole deadband in one tick
                // and must not be stranded outside its owner merely because its settled endpoint is outward again.
                if (
                    server.TryTransferArrivalBorder(
                    bodyIndex: seat,
                    border: out var arrivalBorder
                ) &&
                    string.Equals(
                    a: arrivalBorder,
                    b: $"adjacency/{adjacency.Counterpart}",
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    var previousOutward = FixedVector3.Dot(
                        left: (body.FixedPreviousPosition - frame.Origin),
                        right: frame.Normal
                    );
                    var outward = FixedVector3.Dot(
                        left: (body.FixedPosition - frame.Origin),
                        right: frame.Normal
                    );

                    if (
                        (previousOutward > -ownershipThreshold) &&
                        (outward > -ownershipThreshold)
                    ) {
                        continue;
                    }

                    _ = server.ClearTransferArrivalBorder(
                        bodyIndex: seat,
                        expectedBorder: arrivalBorder
                    );
                }

                var crossing = WorldAdjacencyRegion.Sweep(
                    frame: frame,
                    from: body.FixedPreviousPosition,
                    to: body.FixedPosition,
                    outwardThreshold: ownershipThreshold
                );

                if (!crossing.Crossed) {
                    continue;
                }

                (candidates[seat] ??= []).Add(item: new WorldAdjacencyFaceHit(
                    Adjacency: adjacency,
                    Seat: seat,
                    Frame: frame,
                    SeamU: crossing.SeamU,
                    SeamV: crossing.SeamV,
                    Parameter: crossing.Parameter
                ));
            }
        }

        return candidates;
    }
    /// <summary>Settles the already-evaluated adjacency continuations this authority holds before it advances its
    /// population: a pending continuum whose remaining image crosses no further ownership face here is consumed, and
    /// one that does stays pending for the host's post-step scan to carry onward. The caller holds the authority
    /// gate.</summary>
    /// <param name="server">The authority about to step.</param>
    /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
    public static void ResolveContinuations(WorldServer server) {
        ArgumentNullException.ThrowIfNull(argument: server);

        var population = server.Population;
        var pending = false;

        // Almost every step holds no continuation; finding that out allocates nothing.
        for (var seat = 0; (!pending && (seat < population.Capacity)); seat++) {
            pending = (population.EntryBody(index: seat) is { PendingContinuum: not null });
        }
        if (!pending) {
            return;
        }

        var crossings = Sweep(
            pendingOnly: true,
            server: server
        );

        for (var seat = 0; (seat < population.Capacity); seat++) {
            if (
                (crossings[seat] is not { Count: > 0 }) &&
                (population.EntryBody(index: seat) is { PendingContinuum: not null } body)
            ) {
                body.ClearPendingContinuum();
            }
        }
    }
}

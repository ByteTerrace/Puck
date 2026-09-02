using Puck.Assets.Documents;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.World;

/// <summary>What happens when an adjacent authority cannot be observed or accept a handoff.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldAdjacencyUnavailable>))]
public enum WorldAdjacencyUnavailable : byte {
    /// <summary>The boundary behaves as closed terrain. The local authority remains authoritative and never lets a
    /// body fall through an unowned seam.</summary>
    Closed,
}
/// <summary>
/// One invisible, rectangular ownership boundary. The authored yaw and pitch point from this authority into its
/// neighbour; the owned half-space is therefore on the non-positive side of the boundary plane.
/// </summary>
/// <param name="Center">The boundary rectangle's center in this world's coordinates.</param>
/// <param name="OutwardYawDegrees">The outward heading, in degrees (0 = +Z, 90 = +X).</param>
/// <param name="OutwardPitchDegrees">The outward elevation, in degrees (+90 = +Y, -90 = -Y).</param>
/// <param name="Width">The full span along the boundary's local right axis.</param>
/// <param name="Height">The full span along the boundary's local up axis.</param>
public sealed record WorldAdjacencyBoundary(DocumentVector3 Center, float OutwardYawDegrees, float OutwardPitchDegrees, float Width, float Height) {
    private static readonly FixedVector3 X = new(
        X: FixedQ4816.One,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 Y = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static readonly FixedVector3 Z = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.One
    );

    /// <summary>Compiles the authored rectangle into the same fixed-point frame used by crossing, mapping, contact,
    /// and presentation. Cardinal headings preserve exact axes.</summary>
    public WorldFaceFrame CompileFrame() {
        var yaw = FixedQ4816.FromDouble(value: OutwardYawDegrees);
        FixedVector3 right;
        FixedVector3 normal;
        const long QuarterTurn = (90L << FixedQ4816.FractionBitCount);
        const long FullTurn = (QuarterTurn * 4L);
        var cardinal = (yaw.Value % FullTurn);

        if (cardinal < 0L) {
            cardinal += FullTurn;
        }

        switch (cardinal) {
            case 0L:
                right = X;
                normal = Z;
                break;
            case QuarterTurn:
                right = -Z;
                normal = X;
                break;
            case (QuarterTurn * 2L):
                right = -X;
                normal = -Z;
                break;
            case (QuarterTurn * 3L):
                right = Z;
                normal = -X;
                break;
            default: {
                    var rotation = FixedQuaternion.FromAxisAngle(
                        angle: (yaw * WorldAngles.DegreesToRadians),
                        axis: Y
                    );

                    right = rotation.Rotate(vector: X).Normalize();
                    normal = rotation.Rotate(vector: Z).Normalize();
                    break;
                }
        }

        var planarNormal = normal;
        var pitch = FixedQ4816.FromDouble(value: OutwardPitchDegrees);
        var pitchCardinal = (pitch.Value % FullTurn);

        if (pitchCardinal < 0L) {
            pitchCardinal += FullTurn;
        }

        FixedVector3 up;

        switch (pitchCardinal) {
            case 0L:
                up = Y;
                break;
            case QuarterTurn:
                normal = Y;
                up = -planarNormal;
                break;
            case (QuarterTurn * 2L):
                normal = -planarNormal;
                up = -Y;
                break;
            case (QuarterTurn * 3L):
                up = planarNormal;
                normal = -Y;
                break;
            default: {
                    var rotation = FixedQuaternion.FromAxisAngle(
                        angle: (pitch * WorldAngles.DegreesToRadians),
                        axis: right
                    );

                    up = rotation.Rotate(vector: Y).Normalize();
                    normal = rotation.Rotate(vector: planarNormal).Normalize();
                    break;
                }
        }

        return new WorldFaceFrame(
            Origin: FixedVector3.FromVector3(value: Center),
            Right: right,
            Up: up,
            Normal: normal,
            HalfWidth: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: (Width * 0.5f))),
            HalfHeight: FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: (Height * 0.5f))),
            HalfDepth: FixedQ4816.Zero
        );
    }
}
/// <summary>
/// One intentional adjacency between two authority-owned regions. It is topology, not visible furniture: no
/// placement, screen, or portal is required. The compiler derives the overlap needed for observation, interaction,
/// grounding, and handoff from the two delivered documents.
/// </summary>
/// <param name="Name">This document's stable name for the edge.</param>
/// <param name="Destination">A global persisted destination naming the neighbouring authority.</param>
/// <param name="Counterpart">The reciprocal adjacency row in the destination document.</param>
/// <param name="Boundary">The invisible source-side ownership boundary.</param>
/// <param name="Unavailable">The authored failure treatment.</param>
/// <param name="OnUnavailable">Optional declared channel pressed once on the body after the engine applies the
/// failure treatment. Use a kit action on this channel for authored sound, animation, state, or other feedback;
/// ownership safety never depends on the binding.</param>
/// <param name="Capacity">The maximum number of live bodies and outstanding reservations admitted through this
/// border at once, or <see langword="null"/> to use the destination population's remaining capacity — the same
/// policy <see cref="WorldPlacementPortal.Capacity"/> gives portal furniture. A full border refuses the current
/// attempt immediately; it never queues.</param>
/// <param name="LivenessGraceSeconds">How long this edge may go without a delivered neighbour refresh before the
/// world calls the link dropped — the authored threshold behind the <c>linkEstablished</c>/<c>linkDropped</c> world
/// event family and the <c>$link:&lt;name&gt;</c> reserved rule channel (<see cref="WorldRuleFacts.LinkPrefix"/>).
/// Authored in seconds — a physical unit, so a world's <see cref="WorldDefinition.SimulationRateHz"/> can change
/// without silently retuning the window — and compiled per document through
/// <see cref="WorldDefinition.AdjacencyLivenessGraceTicks"/>, exactly the
/// <c>population.reconnectGraceSeconds</c> idiom.
/// <para><c>0</c> (the default) disables liveness sensing for this edge outright: no link edge is emitted for it
/// and <c>$link:</c> reads <c>0</c> forever, so a world authoring none is unchanged. Per row, not per world: each
/// seam carries its own tolerance.</para>
/// <para>A world whose rate is 0 has no tick mapping for a positive value (see <see cref="CompiledTickDuration"/>),
/// which reads as never dropped.</para></param>
public sealed record WorldAdjacency(
    WorldSafeName Name,
    string Destination,
    string Counterpart,
    WorldAdjacencyBoundary Boundary,
    WorldAdjacencyUnavailable Unavailable = WorldAdjacencyUnavailable.Closed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OnUnavailable = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Capacity = null,
    float LivenessGraceSeconds = 0f
);
/// <summary>One body's deterministic sweep through an invisible ownership boundary.</summary>
/// <param name="Crossed">Whether the segment left the boundary's owned (non-positive) half-space through the
/// authored rectangle. A body already beyond the plane also reports a crossing so a multi-edge corner traversal
/// can continue on the next authority step rather than becoming stranded outside its new owner.</param>
/// <param name="Parameter">The earliest crossing parameter in <c>[0,1]</c>; zero for an already-outside body.</param>
/// <param name="SeamU">Horizontal coordinate on the boundary frame.</param>
/// <param name="SeamV">Vertical coordinate on the boundary frame.</param>
public readonly record struct WorldAdjacencyCrossing(bool Crossed, FixedQ4816 Parameter, FixedQ4816 SeamU, FixedQ4816 SeamV);
/// <summary>Fixed-point ownership-boundary crossing shared by handoff and verification.</summary>
public static class WorldAdjacencyRegion {
    /// <summary>Tests a body-center segment against an adjacency rectangle, from owned to neighbouring space.</summary>
    public static WorldAdjacencyCrossing Sweep(WorldFaceFrame frame, FixedVector3 from, FixedVector3 to) {
        return Sweep(
            frame: frame,
            from: from,
            to: to,
            outwardThreshold: FixedQ4816.Zero
        );
    }
    /// <summary>Tests a body-center segment against an adjacency rectangle after it has crossed an explicit
    /// outward ownership threshold. A reciprocal pair using the same positive threshold forms a closed deadband:
    /// the current authority keeps writing throughout the overlap, and a transfer lands at least that far inside
    /// the destination before its reciprocal edge can become eligible.</summary>
    /// <param name="frame">The authored boundary frame. Seam coordinates remain anchored to this plane.</param>
    /// <param name="from">The segment start.</param>
    /// <param name="to">The segment end.</param>
    /// <param name="outwardThreshold">The non-negative distance beyond the authored plane at which ownership
    /// changes.</param>
    /// <returns>The earliest qualifying ownership-boundary crossing, or the default value when the segment does not
    /// leave through the threshold rectangle.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outwardThreshold"/> is negative.</exception>
    public static WorldAdjacencyCrossing Sweep(WorldFaceFrame frame, FixedVector3 from, FixedVector3 to, FixedQ4816 outwardThreshold) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: outwardThreshold.Value);

        var fromDelta = (from - frame.Origin);
        var toDelta = (to - frame.Origin);
        var fromNormal = FixedVector3.Dot(
            left: fromDelta,
            right: frame.Normal
        );
        var toNormal = FixedVector3.Dot(
            left: toDelta,
            right: frame.Normal
        );

        if (toNormal <= outwardThreshold) {
            return default;
        }

        // A newly arrived body may already lie beyond a second boundary after one diagonal step. It still belongs
        // to exactly one authority; reporting parameter zero lets that authority forward it deterministically on
        // its next step instead of requiring an impossible reconstructed pre-arrival segment.
        var parameter = ((fromNormal > outwardThreshold)
            ? FixedQ4816.Zero
            : ((outwardThreshold - fromNormal) / (toNormal - fromNormal))
        );
        var point = (from + ((to - from) * parameter));
        var seam = (point - frame.Origin);
        var u = FixedVector3.Dot(
            left: seam,
            right: frame.Right
        );
        var v = FixedVector3.Dot(
            left: seam,
            right: frame.Up
        );

        // A yaw-only ownership plane offset outward by T is one face of the owner's T-expanded horizontal
        // half-space. Its horizontal ends must expand by the same T or two perpendicular faces leave an unowned
        // T-by-T corner: a diagonal body reaches (T,T), lies beyond both original face rectangles, and neither
        // authority can claim it. Keep the authored vertical aperture exact — this is horizontal ownership
        // topology, not permission to cross above or below the boundary.
        var ownershipHalfWidth = (frame.HalfWidth + (frame.IsYawOnly
            ? outwardThreshold
            : FixedQ4816.Zero));

        return new WorldAdjacencyCrossing(
            Crossed: ((FixedQ4816.Abs(value: u) <= ownershipHalfWidth) && (FixedQ4816.Abs(value: v) <= frame.HalfHeight)),
            Parameter: parameter,
            SeamU: u,
            SeamV: v
        );
    }
}
/// <summary>One adjacency edge, whether read off a resolved document's own row or projected from an attested
/// neighbour's declared edge — the shape corner discovery and the corner proof need from either representation.</summary>
/// <param name="Name">The edge's own stable name.</param>
/// <param name="Counterpart">The reciprocal row name this edge points back at across the seam.</param>
/// <param name="Document">The resolved document this edge's destination names, or <see langword="null"/> when that
/// destination does not resolve.</param>
/// <param name="Boundary">The authored boundary rectangle and its outward orientation.</param>
public readonly record struct WorldAdjacencyEdgeView(WorldSafeName Name, string Counterpart, string? Document, WorldAdjacencyBoundary Boundary) {
    internal static WorldAdjacencyEdgeView From(WorldAdjacency row, string? document) => new(
        Boundary: row.Boundary,
        Counterpart: row.Counterpart,
        Document: document,
        Name: row.Name
    );
    internal static WorldAdjacencyEdgeView From(WorldAttestedEdge edge) => new(
        Boundary: edge.Boundary,
        Counterpart: edge.Counterpart,
        Document: edge.Document,
        Name: edge.Name
    );
}
/// <summary>A neighbouring authority's edges and overlap terms, regardless of whether they arrived as a whole
/// <see cref="WorldDefinition"/> or a <see cref="WorldCounterpartAttestation"/> — the one shape
/// <see cref="WorldAdjacencyPolicy.TrySharedCorner"/> and the derived-corner validator consume, so a corner proof
/// reads identically whichever way the neighbour proved its half.</summary>
public readonly struct WorldAdjacencyDocumentView {
    private readonly WorldCounterpartAttestation? m_attestation;
    private readonly WorldDefinition? m_definition;

    private WorldAdjacencyDocumentView(WorldDefinition? definition, WorldCounterpartAttestation? attestation) {
        m_definition = definition;
        m_attestation = attestation;
    }

    /// <summary>Every edge whose destination resolves to a document — the shape corner discovery needs to compare
    /// two neighbours' edge lists. Drops a row whose destination does not resolve; <see cref="FindEdge"/> never
    /// does, because a name lookup does not need a document to answer.</summary>
    public IEnumerable<WorldAdjacencyEdgeView> Edges {
        get {
            if (m_definition is { } definition) {
                foreach (var row in (definition.Adjacencies ?? [])) {
                    if (row is null) {
                        continue;
                    }
                    if (WorldAdjacencyPolicy.DestinationNeighbourKey(
                        definition: definition,
                        destinationName: row.Destination
                    ) is not { } document) {
                        continue;
                    }

                    yield return WorldAdjacencyEdgeView.From(
                        document: document,
                        row: row
                    );
                }
            } else if (m_attestation is { } attestation) {
                foreach (var edge in attestation.Edges) {
                    if (edge.Document is null) {
                        continue;
                    }

                    yield return WorldAdjacencyEdgeView.From(edge: edge);
                }
            }
        }
    }

    /// <summary>Finds an edge by its own stable name — never by the other side's counterpart spelling, matching
    /// <see cref="WorldDefinitionRows.FindAdjacency"/> and <see cref="WorldCounterpartAttestation.FindEdge"/>
    /// exactly. Never drops a row whose destination fails to resolve — only <see cref="Edges"/> does that.</summary>
    public WorldAdjacencyEdgeView? FindEdge(string name) {
        if (m_definition is { } definition) {
            if (WorldDefinitionRows.FindAdjacency(
                adjacencies: definition.Adjacencies,
                name: name
            ) is not { } row) {
                return null;
            }

            return WorldAdjacencyEdgeView.From(
                document: WorldAdjacencyPolicy.DestinationNeighbourKey(
                    definition: definition,
                    destinationName: row.Destination
                ),
                row: row
            );
        }
        if (m_attestation is { } attestation) {
            return ((attestation.FindEdge(name: name) is { } edge)
                ? WorldAdjacencyEdgeView.From(edge: edge)
                : null
            );
        }

        return null;
    }
    /// <summary>Builds a view over a neighbour's signed attestation.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="attestation"/> is <see langword="null"/>.</exception>
    public static WorldAdjacencyDocumentView FromAttestation(WorldCounterpartAttestation attestation) {
        ArgumentNullException.ThrowIfNull(argument: attestation);

        return new WorldAdjacencyDocumentView(
            attestation: attestation,
            definition: null
        );
    }
    /// <summary>Builds a view over a neighbour's whole document.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldAdjacencyDocumentView FromDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldAdjacencyDocumentView(
            attestation: null,
            definition: definition
        );
    }
    /// <summary>Derives this neighbour's own overlap terms — attested directly when this view is backed by an
    /// attestation, derived from the document otherwise.</summary>
    public bool TryOverlapTerms(out WorldOverlapTerms? terms, out string reason) {
        if (m_attestation is { } attestation) {
            terms = attestation.Overlap;
            reason = string.Empty;

            return true;
        }
        if (m_definition is { } definition) {
            return WorldOverlapTerms.TryDerive(
                definition: definition,
                reason: out reason,
                terms: out terms
            );
        }

        terms = null;
        reason = "no neighbour data";

        return false;
    }
}
/// <summary>The compiler-owned safety envelope for a reciprocal adjacency pair.</summary>
public static class WorldAdjacencyPolicy {
    /// <summary>Number of slower-side tick periods reserved for delivery and tick-start installation. One period is
    /// in flight while the other is the consumer's pinned tick-start image.</summary>
    public const int DeliveryPeriods = 2;

    private static FixedQ4816 CeilingFixed(float value) {
        var fixedValue = FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value));

        return ((((double)fixedValue) < Math.Abs(value: value))
            ? FixedQ4816.FromRawBits(value: checked((fixedValue.Value + 1L)))
            : fixedValue
        );
    }

    /// <summary>Resolves a destination row to its authored reference's neighbour key without opening it.</summary>
    public static string? DestinationNeighbourKey(WorldDefinition definition, string destinationName) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var destination = WorldDefinitionRows.FindDestination(
            destinations: definition.Destinations,
            name: destinationName
        );

        return ((destination is null)
            ? null
            : WorldDefinitionRows.FindReference(
                references: definition.References,
                name: destination.Reference
            )?.NeighbourKey
        );
    }
    /// <summary>Finds the local global-persisted destination that gives a derived corner its direct observation
    /// route. The edge topology is compiler-derived; the authority route remains explicit authoring.</summary>
    public static string? GlobalDestinationForNeighbourKey(WorldDefinition definition, string neighbourKey) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        foreach (var destination in (definition.Destinations ?? [])) {
            if (
                (destination is null) ||
                (destination.Scope != WorldDestinationScope.Global) ||
                (destination.Durability != WorldDestinationDurability.Persisted)
            ) {
                continue;
            }

            var candidate = DestinationNeighbourKey(
                definition: definition,
                destinationName: destination.Name.Value
            );

            if (
                (candidate is not null) &&
                string.Equals(
                a: candidate,
                b: neighbourKey,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return destination.Name.Value;
            }
        }

        return null;
    }
    /// <summary>Derives the greatest interaction/targeting reach a document declares — one of the overlap terms.</summary>
    /// <param name="definition">The document.</param>
    /// <returns>The reach.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedQ4816 InteractionReach(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var reach = FixedQ4816.Zero;

        foreach (var interaction in (definition.Interactions ?? WorldInteractionsSection.Empty).Interactions) {
            if (
                (interaction is not null) &&
                (interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance)
            ) {
                reach = FixedQ4816.Max(
                    x: reach,
                    y: CeilingFixed(value: interaction.Range)
                );
            }
        }
        foreach (var register in definition.TargetRegisters) {
            if (register is not null) {
                reach = FixedQ4816.Max(
                    x: reach,
                    y: CeilingFixed(value: register.MaximumRange)
                );
            }
        }
        return reach;
    }
    /// <summary>Returns the ownership threshold appropriate to a boundary's traversal geometry. A vertical wall
    /// (world-up in its plane) carries the full reciprocal contact hysteresis: ordinary grounded travel is horizontal,
    /// so a deadband that wide costs nothing. A floor/ceiling boundary cannot carry that much — one body radius of
    /// delayed ownership would put handoff after solid destination terrain, or past the end of a held ascent — so it
    /// carries the much smaller <paramref name="verticalSettleDeadband"/> instead.</summary>
    /// <param name="frame">The compiled local boundary frame.</param>
    /// <param name="reciprocalHysteresis">The non-negative two-body contact hysteresis.</param>
    /// <param name="verticalSettleDeadband">The non-negative vertical settle deadband
    /// (<see cref="TryVerticalSettleDeadband"/>).</param>
    /// <returns><paramref name="reciprocalHysteresis"/> for a vertical wall;
    /// <paramref name="verticalSettleDeadband"/> for a floor/ceiling boundary.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either depth is negative.</exception>
    public static FixedQ4816 OwnershipThreshold(in WorldFaceFrame frame, FixedQ4816 reciprocalHysteresis, FixedQ4816 verticalSettleDeadband) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: reciprocalHysteresis.Value);
        ArgumentOutOfRangeException.ThrowIfNegative(value: verticalSettleDeadband.Value);
        return (frame.IsYawOnly
            ? reciprocalHysteresis
            : verticalSettleDeadband
        );
    }
    /// <summary>Derives the greatest collider-center reach in a document for overlap and reciprocal-contact proofs.</summary>
    public static bool TryBodyReach(WorldDefinition definition, out FixedQ4816 reach, out string reason) {
        reach = FixedQ4816.Zero;
        foreach (var kit in definition.Kits) {
            if ((kit?.Collider) is not { } collider) {
                continue;
            }

            FixedQ4816 candidate;

            switch (collider) {
                case WorldCollider.Sphere sphere:
                    candidate = CeilingFixed(value: MathF.Abs(x: sphere.Radius));
                    break;
                case WorldCollider.Capsule capsule:
                    candidate = CeilingFixed(value: MathF.Abs(x: capsule.Radius));
                    break;
                case WorldCollider.Box box: {
                        var x = CeilingFixed(value: MathF.Abs(x: box.HalfExtents.X)).Value;
                        var y = CeilingFixed(value: MathF.Abs(x: box.HalfExtents.Y)).Value;
                        var z = CeilingFixed(value: MathF.Abs(x: box.HalfExtents.Z)).Value;

                        if (!FixedDirectedRounding.TryCeilingMagnitude(
                            result: out var magnitude,
                            x: x,
                            y: y,
                            z: z
                        )) {
                            reason = $"kit '{kit.Name}' collider magnitude exceeds the fixed-point range";
                            return false;
                        }
                        candidate = new FixedQ4816(Value: magnitude);
                        break;
                    }
                default:
                    reason = $"kit '{kit.Name}' uses a collider whose border reach cannot be proven";
                    return false;
            }
            reach = FixedQ4816.Max(
                x: reach,
                y: candidate
            );
        }

        reason = string.Empty;
        return true;
    }
    /// <summary>Derives the overlap depth both sides must retain. The result is symmetric in the two documents and
    /// rounds every authored lower bound upward.</summary>
    public static bool TryDeriveOverlap(WorldDefinition local, WorldDefinition neighbour, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: neighbour);

        depth = FixedQ4816.Zero;

        return (
            WorldOverlapTerms.TryDerive(
            definition: neighbour,
            reason: out reason,
            terms: out var terms
        ) &&
            TryDeriveOverlap(
            depth: out depth,
            local: local,
            neighbour: terms!,
            reason: out reason
        )
        );
    }
    /// <summary>Derives the overlap depth from a neighbour's attested terms rather than its whole document — the
    /// same arithmetic, over the only facts it needs, so a neighbour across a trust boundary can prove a seam
    /// without handing over what it is made of. Symmetric in the two sides, exactly as the document overload
    /// is.</summary>
    /// <param name="local">This authority's document.</param>
    /// <param name="neighbour">The neighbour's attested overlap terms.</param>
    /// <param name="depth">The derived depth on success.</param>
    /// <param name="reason">The named reason on failure.</param>
    /// <returns><see langword="true"/> when the depth derives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> or <paramref name="neighbour"/> is <see langword="null"/>.</exception>
    public static bool TryDeriveOverlap(WorldDefinition local, WorldOverlapTerms neighbour, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: neighbour);

        depth = FixedQ4816.Zero;

        return (
            WorldOverlapTerms.TryDerive(
            definition: local,
            reason: out reason,
            terms: out var localTerms
        ) &&
            (localTerms is not null) &&
            TryDeriveOverlap(
            depth: out depth,
            local: localTerms,
            neighbour: neighbour,
            reason: out reason
        )
        );
    }
    /// <summary>Derives the overlap depth from both sides' already-attested terms — the same arithmetic the document
    /// overloads run, over the only facts either one ever reads once both sides' terms are in hand. The one
    /// primitive a fully attested corner peer, with neither side's whole document available, proves its geometry
    /// through.</summary>
    /// <param name="local">This authority's own overlap terms.</param>
    /// <param name="neighbour">The neighbour's overlap terms.</param>
    /// <param name="depth">The derived depth on success.</param>
    /// <param name="reason">The named reason on failure.</param>
    /// <returns><see langword="true"/> when the depth derives.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="local"/> or <paramref name="neighbour"/> is <see langword="null"/>.</exception>
    public static bool TryDeriveOverlap(WorldOverlapTerms local, WorldOverlapTerms neighbour, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: neighbour);

        depth = FixedQ4816.Zero;

        var bodyReach = FixedQ4816.Max(
            x: local.BodyReach,
            y: neighbour.BodyReach
        );
        var interactionReach = FixedQ4816.Max(
            x: local.InteractionReach,
            y: neighbour.InteractionReach
        );
        var closingSpeed = (local.SpeedCeiling + neighbour.SpeedCeiling);
        var slowestRate = Math.Min(
            val1: Math.Max(
                val1: local.SimulationRateHz,
                val2: 1
            ),
            val2: Math.Max(
                val1: neighbour.SimulationRateHz,
                val2: 1
            )
        );

        if (
            !FixedDirectedRounding.TryCeilingQuotient(
            numerator: (FixedQ4816.One.Value * DeliveryPeriods),
            fractionBitsNumerator: FixedQ4816.FractionBitCount,
            denominator: slowestRate,
            fractionBitsDenominator: 0,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var latencyRaw
        ) ||
            !FixedDirectedRounding.TryCeilingProductSum(
            a: closingSpeed.Value,
            fractionBitsA: FixedQ4816.FractionBitCount,
            b: latencyRaw,
            fractionBitsB: FixedQ4816.FractionBitCount,
            addend: (bodyReach + interactionReach).Value,
            fractionBitsAddend: FixedQ4816.FractionBitCount,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var depthRaw
        )
        ) {
            reason = "the overlap envelope exceeds the fixed-point range";
            return false;
        }

        // Handoff occurs at the far side of whichever threshold the boundary's geometry selects, not at the authored
        // plane. Contact and observation must therefore cover the larger side's threshold even when both worlds have
        // low speed/reach settings whose delivery-latency term alone would derive a shallower overlap.
        depth = FixedQ4816.Max(
            x: new FixedQ4816(Value: depthRaw),
            y: FixedQ4816.Max(
                x: FixedQ4816.Max(
                    x: local.Hysteresis,
                    y: neighbour.Hysteresis
                ),
                y: FixedQ4816.Max(
                    x: local.SettleDeadband,
                    y: neighbour.SettleDeadband
                )
            )
        );
        reason = string.Empty;
        return true;
    }
    /// <summary>Derives the reciprocal handoff hysteresis needed to survive the strongest local contact correction:
    /// two maximum-radius body colliders separated beside the seam, plus the authored contact skin. A one-radius
    /// wall deadband (or arrival latch at a plane-based boundary) is insufficient for the intended seam melee:
    /// another body can legally push an arrival by the sum of both radii.</summary>
    public static bool TryReciprocalHysteresis(WorldDefinition definition, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        depth = FixedQ4816.Zero;
        if (!TryBodyReach(
            definition: definition,
            reach: out var reach,
            reason: out reason
        )) {
            return false;
        }

        try {
            var skin = CeilingFixed(value: MathF.Abs(x: definition.Collision.ContactSkin));

            depth = new FixedQ4816(Value: checked(((reach.Value * 2L) + skin.Value)));
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            reason = "the reciprocal contact hysteresis exceeds the fixed-point range";
            return false;
        }
    }
    /// <summary>Finds the diagonal document shared by two direct neighbours at a junction. Both neighbours must
    /// independently name the same third document through an edge other than the edge returning to the source;
    /// an arbitrary two-hop chain is therefore never promoted into local interest.</summary>
    public static bool TrySharedCorner(
        WorldAdjacencyDocumentView left,
        string leftBack,
        WorldAdjacencyDocumentView right,
        string rightBack,
        out string document,
        out WorldAdjacencyEdgeView leftEdge,
        out WorldAdjacencyEdgeView rightEdge
    ) {
        document = string.Empty;
        leftEdge = default;
        rightEdge = default;

        foreach (var candidate in left.Edges) {
            if (string.Equals(
                a: candidate.Name.Value,
                b: leftBack,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            foreach (var other in right.Edges) {
                if (
                    string.Equals(
                    a: other.Name.Value,
                    b: rightBack,
                    comparisonType: StringComparison.Ordinal
                ) ||
                    !string.Equals(
                    a: candidate.Document,
                    b: other.Document,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    continue;
                }

                document = candidate.Document!;
                leftEdge = candidate;
                rightEdge = other;
                return true;
            }
        }

        return false;
    }
    /// <summary>Derives the settle deadband a floor/ceiling ownership boundary must carry: strictly more than the
    /// distance a body at rest can fall back through the authored plane in one of its own authority steps, plus the
    /// contact skin the solver keeps between that body and every surface.</summary>
    /// <remarks>
    /// <para>The separating invariant: the deadband is larger than any uncommanded descent and smaller than any
    /// commanded one. A settling body sags at most one step of gravity from rest and therefore never re-crosses; a
    /// body driven or already falling downward clears the deadband inside one step and transfers. The two-body
    /// contact envelope <see cref="TryReciprocalHysteresis"/> derives for a wall breaks the second half.</para>
    /// <para>Per kit: the arm's gravity over one step, capped by its own terminal speed, carried over one more step
    /// to a distance. Every quotient rounds outward and one raw unit is added last, so the result strictly exceeds
    /// the sag.</para>
    /// </remarks>
    /// <param name="definition">The document whose kits, contact skin, and authority rate bound the sag.</param>
    /// <param name="depth">The derived deadband; zero when this returns <see langword="false"/>.</param>
    /// <param name="reason">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the deadband is representable.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static bool TryVerticalSettleDeadband(WorldDefinition definition, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        depth = FixedQ4816.Zero;
        var rate = Math.Max(
            val1: definition.SimulationRateHz,
            val2: 1
        );
        var sagRaw = 0L;

        foreach (var kit in definition.Kits) {
            if (kit?.Motion is not { } motion) {
                continue;
            }

            var (acceleration, terminalSpeed) = motion switch {
                WorldMotionModel.Grounded grounded => (CeilingFixed(value: MathF.Abs(x: grounded.FallGravity)), CeilingFixed(value: MathF.Abs(x: grounded.MaxFallSpeed))),
                WorldMotionModel.Vehicle vehicle => (CeilingFixed(value: MathF.Abs(x: vehicle.FallGravity)), CeilingFixed(value: MathF.Abs(x: vehicle.MaxFallSpeed))),
                _ => (FixedQ4816.Zero, FixedQ4816.Zero),
            };

            // An arm declaring no acceleration has its terminal speed as the one-step speed directly.
            var stepSpeed = terminalSpeed;

            if (acceleration > FixedQ4816.Zero) {
                if (!FixedDirectedRounding.TryCeilingQuotient(
                    numerator: acceleration.Value,
                    fractionBitsNumerator: FixedQ4816.FractionBitCount,
                    denominator: rate,
                    fractionBitsDenominator: 0,
                    fractionBitsOut: FixedQ4816.FractionBitCount,
                    result: out var acceleratedRaw
                )) {
                    reason = $"kit '{kit.Name}' vertical acceleration exceeds the fixed-point range at {rate}Hz";
                    return false;
                }

                stepSpeed = FixedQ4816.Min(
                    x: terminalSpeed,
                    y: new FixedQ4816(Value: acceleratedRaw)
                );
            }

            if (!FixedDirectedRounding.TryCeilingQuotient(
                numerator: stepSpeed.Value,
                fractionBitsNumerator: FixedQ4816.FractionBitCount,
                denominator: rate,
                fractionBitsDenominator: 0,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var kitSagRaw
            )) {
                reason = $"kit '{kit.Name}' one-step vertical sag exceeds the fixed-point range at {rate}Hz";
                return false;
            }

            sagRaw = Math.Max(
                val1: sagRaw,
                val2: kitSagRaw
            );
        }

        try {
            var skin = CeilingFixed(value: MathF.Abs(x: definition.Collision.ContactSkin));

            depth = new FixedQ4816(Value: checked(((sagRaw + skin.Value) + 1L)));
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            reason = "the vertical settle deadband exceeds the fixed-point range";
            return false;
        }
    }
}

using System.Numerics;
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
public sealed record WorldAdjacencyBoundary(Vector3 Center, float OutwardYawDegrees, float OutwardPitchDegrees, float Width, float Height) {
    private static readonly FixedVector3 s_x = new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_y = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_z = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
    private static readonly FixedQ4816 s_degreesToRadians = FixedQ4816.FromDouble(value: (Math.PI / 180.0));

    /// <summary>Compiles the authored rectangle into the same fixed-point frame used by crossing, mapping, contact,
    /// and presentation. Cardinal headings preserve exact axes.</summary>
    public WorldFaceFrame CompileFrame() {
        var yaw = FixedQ4816.FromDouble(value: OutwardYawDegrees);
        FixedVector3 right;
        FixedVector3 normal;
        const long quarterTurn = (90L << FixedQ4816.FractionBitCount);
        const long fullTurn = (quarterTurn * 4L);
        var cardinal = (yaw.Value % fullTurn);
        if (cardinal < 0L) {
            cardinal += fullTurn;
        }

        switch (cardinal) {
            case 0L:
                right = s_x;
                normal = s_z;
                break;
            case quarterTurn:
                right = -s_z;
                normal = s_x;
                break;
            case (quarterTurn * 2L):
                right = -s_x;
                normal = -s_z;
                break;
            case (quarterTurn * 3L):
                right = s_z;
                normal = -s_x;
                break;
            default: {
                    var rotation = FixedQuaternion.FromAxisAngle(axis: s_y, angle: (yaw * s_degreesToRadians));
                    right = rotation.Rotate(vector: s_x).Normalize();
                    normal = rotation.Rotate(vector: s_z).Normalize();
                    break;
                }
        }

        var planarNormal = normal;
        var pitch = FixedQ4816.FromDouble(value: OutwardPitchDegrees);
        var pitchCardinal = (pitch.Value % fullTurn);
        if (pitchCardinal < 0L) {
            pitchCardinal += fullTurn;
        }

        FixedVector3 up;
        switch (pitchCardinal) {
            case 0L:
                up = s_y;
                break;
            case quarterTurn:
                normal = s_y;
                up = -planarNormal;
                break;
            case (quarterTurn * 2L):
                normal = -planarNormal;
                up = -s_y;
                break;
            case (quarterTurn * 3L):
                up = planarNormal;
                normal = -s_y;
                break;
            default: {
                    var rotation = FixedQuaternion.FromAxisAngle(axis: right, angle: (pitch * s_degreesToRadians));
                    up = rotation.Rotate(vector: s_y).Normalize();
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
public sealed record WorldAdjacency(
    WorldSafeName Name,
    string Destination,
    string Counterpart,
    WorldAdjacencyBoundary Boundary,
    WorldAdjacencyUnavailable Unavailable = WorldAdjacencyUnavailable.Closed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OnUnavailable = null
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
        return Sweep(frame: frame, from: from, to: to, outwardThreshold: FixedQ4816.Zero);
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
        var fromNormal = FixedVector3.Dot(left: fromDelta, right: frame.Normal);
        var toNormal = FixedVector3.Dot(left: toDelta, right: frame.Normal);

        if (toNormal <= outwardThreshold) {
            return default;
        }

        // A newly arrived body may already lie beyond a second boundary after one diagonal step. It still belongs
        // to exactly one authority; reporting parameter zero lets that authority forward it deterministically on
        // its next step instead of requiring an impossible reconstructed pre-arrival segment.
        var parameter = ((fromNormal > outwardThreshold) ? FixedQ4816.Zero :
            ((outwardThreshold - fromNormal) / (toNormal - fromNormal)));
        var point = (from + ((to - from) * parameter));
        var seam = (point - frame.Origin);
        var u = FixedVector3.Dot(left: seam, right: frame.Right);
        var v = FixedVector3.Dot(left: seam, right: frame.Up);

        // A yaw-only ownership plane offset outward by T is one face of the owner's T-expanded horizontal
        // half-space. Its horizontal ends must expand by the same T or two perpendicular faces leave an unowned
        // T-by-T corner: a diagonal body reaches (T,T), lies beyond both original face rectangles, and neither
        // authority can claim it. Keep the authored vertical aperture exact — this is horizontal ownership
        // topology, not permission to cross above or below the boundary.
        var ownershipHalfWidth = (frame.HalfWidth + (frame.IsYawOnly ? outwardThreshold : FixedQ4816.Zero));

        return new WorldAdjacencyCrossing(
            Crossed: ((FixedQ4816.Abs(value: u) <= ownershipHalfWidth) && (FixedQ4816.Abs(value: v) <= frame.HalfHeight)),
            Parameter: parameter,
            SeamU: u,
            SeamV: v
        );
    }
}

/// <summary>The compiler-owned safety envelope for a reciprocal adjacency pair.</summary>
public static class WorldAdjacencyPolicy {
    /// <summary>Number of slower-side tick periods reserved for delivery and tick-start installation. One period is
    /// in flight while the other is the consumer's pinned tick-start image.</summary>
    public const int DeliveryPeriods = 2;

    /// <summary>Returns the ownership threshold appropriate to a boundary's traversal geometry. A vertical wall
    /// (world-up in its plane) can carry the full reciprocal deadband without obstructing ordinary grounded travel.
    /// A floor/ceiling boundary must transfer at its authored plane: offsetting that threshold would delay ownership
    /// until after an ascent has crossed solid destination terrain or exhausted its held traversal input.</summary>
    /// <param name="frame">The compiled local boundary frame.</param>
    /// <param name="reciprocalHysteresis">The non-negative two-body contact hysteresis.</param>
    /// <returns><paramref name="reciprocalHysteresis"/> for a vertical wall; zero for a floor/ceiling boundary.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reciprocalHysteresis"/> is negative.</exception>
    public static FixedQ4816 OwnershipThreshold(in WorldFaceFrame frame, FixedQ4816 reciprocalHysteresis) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: reciprocalHysteresis.Value);
        return (frame.IsYawOnly ? reciprocalHysteresis : FixedQ4816.Zero);
    }

    /// <summary>Finds the diagonal document shared by two direct neighbours at a junction. Both neighbours must
    /// independently name the same third document through an edge other than the edge returning to the source;
    /// an arbitrary two-hop chain is therefore never promoted into local interest.</summary>
    public static bool TrySharedCorner(
        WorldDefinition left,
        string leftBack,
        WorldDefinition right,
        string rightBack,
        out string document,
        out WorldAdjacency? leftEdge,
        out WorldAdjacency? rightEdge
    ) {
        ArgumentNullException.ThrowIfNull(argument: left);
        ArgumentNullException.ThrowIfNull(argument: right);

        document = string.Empty;
        leftEdge = null;
        rightEdge = null;

        foreach (var candidate in (left.Adjacencies ?? [])) {
            if ((candidate is null) || string.Equals(a: candidate.Name.Value, b: leftBack, comparisonType: StringComparison.Ordinal) ||
                (DestinationDocument(definition: left, destinationName: candidate.Destination) is not { } leftDocument)) {
                continue;
            }

            foreach (var other in (right.Adjacencies ?? [])) {
                if ((other is null) || string.Equals(a: other.Name.Value, b: rightBack, comparisonType: StringComparison.Ordinal) ||
                    (DestinationDocument(definition: right, destinationName: other.Destination) is not { } rightDocument) ||
                    !string.Equals(a: leftDocument, b: rightDocument, comparisonType: StringComparison.Ordinal)) {
                    continue;
                }

                document = leftDocument;
                leftEdge = candidate;
                rightEdge = other;
                return true;
            }
        }

        return false;
    }

    /// <summary>Finds the local global-persisted destination that gives a derived corner its direct observation
    /// route. The edge topology is compiler-derived; the authority route remains explicit authoring.</summary>
    public static string? GlobalDestinationForDocument(WorldDefinition definition, string document) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        foreach (var destination in (definition.Destinations ?? [])) {
            if ((destination is null) || (destination.Scope != WorldDestinationScope.Global) ||
                (destination.Durability != WorldDestinationDurability.Persisted)) {
                continue;
            }

            var candidate = DestinationDocument(definition: definition, destinationName: destination.Name.Value);
            if ((candidate is not null) && string.Equals(a: candidate, b: document, comparisonType: StringComparison.Ordinal)) {
                return destination.Name.Value;
            }
        }

        return null;
    }

    /// <summary>Resolves a destination row to its authored reference document without opening it.</summary>
    public static string? DestinationDocument(WorldDefinition definition, string destinationName) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var destination = WorldDefinitionRows.FindDestination(destinations: definition.Destinations, name: destinationName);
        return ((destination is null) ? null : WorldDefinitionRows.FindReference(references: definition.References, name: destination.Reference)?.Document);
    }

    /// <summary>Derives the overlap depth both sides must retain. The result is symmetric in the two documents and
    /// rounds every authored lower bound upward.</summary>
    public static bool TryDeriveOverlap(WorldDefinition local, WorldDefinition neighbour, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: local);
        ArgumentNullException.ThrowIfNull(argument: neighbour);

        depth = FixedQ4816.Zero;
        if (!TryBodyReach(definition: local, reach: out var localBody, reason: out reason) ||
            !TryBodyReach(definition: neighbour, reach: out var neighbourBody, reason: out reason)) {
            return false;
        }

        var bodyReach = FixedQ4816.Max(x: localBody, y: neighbourBody);
        var interactionReach = FixedQ4816.Max(x: InteractionReach(definition: local), y: InteractionReach(definition: neighbour));
        var closingSpeed = (WorldFacePortalPolicy.SpeedCeiling(definition: local) + WorldFacePortalPolicy.SpeedCeiling(definition: neighbour));
        var slowestRate = Math.Min(val1: Math.Max(val1: local.SimulationRateHz, val2: 1), val2: Math.Max(val1: neighbour.SimulationRateHz, val2: 1));

        if (!FixedDirectedRounding.TryCeilingQuotient(
            numerator: (FixedQ4816.One.Value * DeliveryPeriods),
            fractionBitsNumerator: FixedQ4816.FractionBitCount,
            denominator: slowestRate,
            fractionBitsDenominator: 0,
            fractionBitsOut: FixedQ4816.FractionBitCount,
            result: out var latencyRaw) ||
            !FixedDirectedRounding.TryCeilingProductSum(
                a: closingSpeed.Value,
                fractionBitsA: FixedQ4816.FractionBitCount,
                b: latencyRaw,
                fractionBitsB: FixedQ4816.FractionBitCount,
                addend: (bodyReach + interactionReach).Value,
                fractionBitsAddend: FixedQ4816.FractionBitCount,
                fractionBitsOut: FixedQ4816.FractionBitCount,
                result: out var depthRaw)) {
            reason = "the overlap envelope exceeds the fixed-point range";
            return false;
        }

        if (!TryReciprocalHysteresis(definition: local, depth: out var localHysteresis, reason: out reason) ||
            !TryReciprocalHysteresis(definition: neighbour, depth: out var neighbourHysteresis, reason: out reason)) {
            return false;
        }

        // Handoff occurs at the far side of this deadband, not at the authored plane. Contact and observation must
        // therefore cover at least the larger side's threshold even when both worlds have low speed/reach settings
        // whose delivery-latency term alone would derive a shallower overlap.
        depth = FixedQ4816.Max(
            x: new FixedQ4816(Value: depthRaw),
            y: FixedQ4816.Max(x: localHysteresis, y: neighbourHysteresis));
        reason = string.Empty;
        return true;
    }

    private static FixedQ4816 InteractionReach(WorldDefinition definition) {
        var reach = FixedQ4816.Zero;
        foreach (var interaction in (definition.Interactions ?? WorldInteractionsSection.Empty).Interactions) {
            if ((interaction is not null) && (interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance)) {
                reach = FixedQ4816.Max(x: reach, y: CeilingFixed(value: interaction.Range));
            }
        }
        foreach (var register in definition.TargetRegisters) {
            if (register is not null) {
                reach = FixedQ4816.Max(x: reach, y: CeilingFixed(value: register.MaximumRange));
            }
        }
        return reach;
    }

    private static FixedQ4816 CeilingFixed(float value) {
        var fixedValue = FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value));
        return (((double)fixedValue < Math.Abs(value)) ? FixedQ4816.FromRawBits(value: checked(fixedValue.Value + 1L)) : fixedValue);
    }

    /// <summary>Derives the reciprocal handoff hysteresis needed to survive the strongest local contact correction:
    /// two maximum-radius body colliders separated beside the seam, plus the authored contact skin. A one-radius
    /// wall deadband (or arrival latch at a plane-based boundary) is insufficient for the intended seam melee:
    /// another body can legally push an arrival by the sum of both radii.</summary>
    public static bool TryReciprocalHysteresis(WorldDefinition definition, out FixedQ4816 depth, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        depth = FixedQ4816.Zero;
        if (!TryBodyReach(definition: definition, reach: out var reach, reason: out reason)) {
            return false;
        }

        try {
            var skin = CeilingFixed(value: MathF.Abs(x: definition.Collision.ContactSkin));
            depth = new FixedQ4816(Value: checked((reach.Value * 2L) + skin.Value));
            reason = string.Empty;
            return true;
        } catch (OverflowException) {
            reason = "the reciprocal contact hysteresis exceeds the fixed-point range";
            return false;
        }
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
                        if (!FixedDirectedRounding.TryCeilingMagnitude(x: x, y: y, z: z, result: out var magnitude)) {
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
            reach = FixedQ4816.Max(x: reach, y: candidate);
        }

        reason = string.Empty;
        return true;
    }
}

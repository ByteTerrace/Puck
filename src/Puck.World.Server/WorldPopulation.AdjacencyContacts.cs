using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    // Headroom kept under an ownership threshold for the rounding of a projected displacement and of the dot product
    // the crossing sweep measures its end with; the exact check after projection is the guarantee, this only keeps
    // it from rejecting an honest push at the threshold.
    private static readonly FixedQ4816 OwnershipGuard = new(Value: 8L);

    // Tick scratch, shared by every projection and sweep retry for a body. Rebuilding the contact field does not
    // replenish it. Different body lanes own different slots; the tick owner resets it before dispatching them.
    private FixedQ4816[] m_adjacencyCorrectionSpent = [];
    private ulong m_adjacencyTick = 1;
    private ulong? m_adjacencyImageTick;
    // The ownership faces of the definition the contact field composes over, rederived when that definition changes.
    private WorldOwnershipFace[] m_ownershipFaces = [];
    private WorldDefinition? m_ownershipFacesDefinition;

    private void BeginAdjacencyContacts() {
        if ((m_adjacencies is null) || (m_adjacencyImageTick == m_adjacencyTick)) { return; }
        if (m_adjacencyCorrectionSpent.Length != Capacity) {
            m_adjacencyCorrectionSpent = new FixedQ4816[Capacity];
        } else {
            Array.Clear(m_adjacencyCorrectionSpent);
        }
        m_adjacencies.BeginTick(tick: m_adjacencyTick);
        m_adjacencyImageTick = m_adjacencyTick;
    }
    internal void BeginAdjacencyContacts(ulong tick) {
        m_adjacencyTick = tick;
        BeginAdjacencyContacts();
    }
    private WorldOwnershipFace[] OwnershipFaces() {
        if (!ReferenceEquals(objA: m_ownershipFacesDefinition, objB: m_adjacencyDefinition)) {
            m_ownershipFaces = ((m_adjacencyDefinition is { } definition)
                ? WorldAdjacencyPolicy.OwnershipFaces(definition: definition)
                : []);
            m_ownershipFacesDefinition = m_adjacencyDefinition;
        }
        return m_ownershipFaces;
    }

    // The sole door through which a neighbour's reports move a local body: its bodies' depenetration and its solid
    // geometry's resolution arrive here as one proposed displacement and velocity per consulted projection.
    // Invariants, per local body per tick:
    // - The admitted displacement lengths sum to at most the body's locally owned collider reach, and each is at most
    //   the consulted overlap depth. Remote collider sizes, poses, body counts and speed claims cannot enlarge either.
    // - For every ownership face the body is still owned behind (WorldOwnershipFace.Owns, the test the crossing sweep
    //   applies to a step's end), the admitted end is owned behind it too, and the body's outward speed through it does
    //   not grow. A crossing the sweep reports therefore comes from the body's own motion, never from a neighbour's push.
    //   While its incoming velocity retreats from a face, no outward correction or reduction of that retreat is admitted.
    // A body index with no local body admits nothing. No budget is refunded on a failed retry.
    internal void AdmitAdjacencyResponse(int entityIndex, FixedQ4816 overlapDepth, ref FixedVector3 position, ref FixedVector3 velocity,
        FixedVector3 displacement, FixedVector3 proposedVelocity) {
        if ((((uint)entityIndex) >= ((uint)m_adjacencyCorrectionSpent.Length)) ||
            (m_entries[entityIndex].Body is not { } body)) { return; }

        var faces = OwnershipFaces();
        var start = position;
        var admitted = displacement;
        var admittedVelocity = proposedVelocity;

        foreach (var face in faces) {
            if (!face.Owns(point: start)) { continue; }
            var normal = face.Frame.Normal;
            var outwardSpeed = FixedVector3.Dot(left: velocity, right: normal);

            admitted = Below(
                normal: normal,
                room: ((outwardSpeed < FixedQ4816.Zero) ? FixedQ4816.Zero
                    : FixedQ4816.Max(x: FixedQ4816.Zero, y: ((face.Threshold - face.Outward(point: start)) - OwnershipGuard))),
                value: admitted
            );
            admittedVelocity = Below(
                normal: normal,
                room: outwardSpeed,
                value: admittedVelocity
            );
        }

        ref var spent = ref m_adjacencyCorrectionSpent[entityIndex];
        var ceiling = FixedQ4816.Max(FixedQ4816.Zero, FixedQ4816.Min(body.BroadphaseRadius, overlapDepth));
        admitted = admitted.ClampLength(maximum: FixedQ4816.Max(FixedQ4816.Zero, ceiling - spent));

        foreach (var face in faces) {
            if (!face.Owns(point: start)) { continue; }
            var outwardSpeed = FixedVector3.Dot(velocity, face.Frame.Normal);
            if (!face.Owns(point: (start + admitted)) ||
                ((outwardSpeed < FixedQ4816.Zero) && (FixedVector3.Dot(admitted, face.Frame.Normal) > FixedQ4816.Zero))) {
                admitted = FixedVector3.Zero;
            }
            // Quantized normals need not have exact unit length. The final predicate, rather than projection
            // arithmetic alone, enforces the velocity inequality on every owned face.
            if (FixedVector3.Dot(admittedVelocity, face.Frame.Normal) > outwardSpeed) { admittedVelocity = velocity; }
        }
        // No positional response was admitted: the same rejected report cannot still steer or launch the body.
        if (admitted == FixedVector3.Zero) { admittedVelocity = velocity; }
        if ((admitted != displacement) || (admittedVelocity != proposedVelocity)) {
            WorldBodyWork.Process.Add(kind: WorldBodyWork.AdjacencyCorrectionClamps, amount: 1);
        }
        // Length rounds to nearest. Debit its ceiling, so repeated sub-raw diagonal corrections cannot exceed the
        // budget through accumulated rounding. IsWithin compares exact squares; ClampLength rounds toward zero.
        var length = admitted.Length;
        if (!admitted.IsWithin(radius: length)) { length += FixedQ4816.Epsilon; }
        spent += length;
        position = (start + admitted);
        velocity = admittedVelocity;
    }
    internal void ApplyCarriedAdjacencyResponse(WorldBody carrier, FixedQ4816 overlapDepth, FixedVector3 displacement) {
        if (carrier.SweepRefusedThisTick) { return; }
        var position = carrier.FixedPosition;
        var velocity = carrier.ApproximateWorldVelocity();
        var proposedVelocity = velocity;
        var normal = displacement.Normalize();
        var inward = FixedVector3.Dot(velocity, normal);
        if (inward < FixedQ4816.Zero) { proposedVelocity -= normal * inward; }
        AdmitAdjacencyResponse(entityIndex: carrier.EntityIndex, overlapDepth: overlapDepth, position: ref position,
            velocity: ref velocity, displacement: displacement, proposedVelocity: proposedVelocity);
        carrier.ApplyAdmittedContactResponse(position: position, velocity: velocity);
    }
    // Removes the part of value's component along a unit normal that exceeds room.
    private static FixedVector3 Below(FixedVector3 normal, FixedQ4816 room, FixedVector3 value) {
        var along = FixedVector3.Dot(left: value, right: normal);

        return ((along > room)
            ? (value - (normal * (along - room)))
            : value);
    }
}

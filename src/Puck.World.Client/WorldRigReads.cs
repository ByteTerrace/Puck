using System.Numerics;
using Puck.Physics.Motion;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The one view a registration's pose pass reads the world through: it answers exactly the reads its rig compiled
/// (<see cref="CreationRig.Reads"/>) and nothing else. A <see cref="CreationRigRead"/> is made only by a rig, and the view
/// refuses one another rig made, so a driver advance or an effector solve cannot read what the rig did not list, and the
/// wake test (<see cref="Revision"/>) and the presentation manifest see every read the pose depends on.
/// </summary>
/// <remarks>Presentation-only, on the thread that presents frames: the view reads the client's state mirror through the
/// registration's lease and the client's interpolated body poses, and nothing it answers re-enters the
/// simulation.</remarks>
public sealed class WorldRigReads {
    private readonly CreationRig m_rig;
    private readonly WorldStateLease m_lease;
    private readonly CreationRigRead[] m_bodies;
    private IWorldStampSource? m_client;
    private int m_body = -1;
    private Func<int, int>? m_poseSerial;

    /// <summary>Initializes a new instance of the <see cref="WorldRigReads"/> class over a rig's reads.</summary>
    /// <param name="rig">The rig whose reads the view answers.</param>
    /// <param name="lease">The registration's lease of the state mirror, bound to the wearing body, which state reads go
    /// through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> or <paramref name="lease"/> is
    /// <see langword="null"/>.</exception>
    public WorldRigReads(CreationRig rig, WorldStateLease lease) {
        ArgumentNullException.ThrowIfNull(argument: rig);
        ArgumentNullException.ThrowIfNull(argument: lease);

        m_rig = rig;
        m_lease = lease;
        m_bodies = [.. rig.Reads.Where(predicate: static read => (read.Kind == CreationRigReadKind.Body))];
    }

    /// <summary>Gets the rig whose reads the view answers.</summary>
    public CreationRig Rig => m_rig;
    /// <summary>Gets the wearing body's simulation facts this frame, or <see cref="BodyFacts.None"/> for a registration
    /// with no body or before the first <see cref="Bind"/>.</summary>
    public BodyFacts Facts => (((m_client is { } client) && (m_body >= 0)) ? client.Facts(index: m_body) : BodyFacts.None);
    /// <summary>Gets a revision of everything the view answers that can change between frames: the latest change of a
    /// state cell the lease holds, and for each other body the rig reads, its identity, its root pose and the serial its
    /// own registration moves each time its pose changes. Equal revisions mean the reads answer what they answered.</summary>
    public int Revision {
        get {
            var hash = new HashCode();

            hash.Add(value: m_lease.Changed);
            foreach (var read in m_bodies) {
                if ((m_client is not { } client) || (((uint)read.Body) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: read.Body)) {
                    hash.Add(value: -1);

                    continue;
                }

                hash.Add(value: client.EntityAddress(index: read.Body));
                hash.Add(value: client.Position(index: read.Body));
                hash.Add(value: client.Orientation(index: read.Body));
                hash.Add(value: (m_poseSerial?.Invoke(arg: read.Body) ?? 0));
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>Points the view at this frame's client and wearing body.</summary>
    /// <param name="client">The client whose bodies, facts and query field the view reads.</param>
    /// <param name="body">The wearing body's population index, or -1 for a registration with none.</param>
    /// <param name="poseSerial">Returns the pose serial of a body's registration, zero for a body with none; or
    /// <see langword="null"/>, which reads zero for every body.</param>
    public void Bind(IWorldStampSource? client, int body, Func<int, int>? poseSerial = null) {
        m_client = client;
        m_body = body;
        m_poseSerial = poseSerial;
    }
    /// <summary>Reads a state read's presented number.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.State"/> reads.</param>
    /// <param name="value">The number; zero when the cell is absent or holds none.</param>
    /// <returns><see langword="true"/> when the cell holds a number.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's state reads.</exception>
    public bool TryNumber(CreationRigRead read, out float value) {
        Require(kind: CreationRigReadKind.State, read: read);

        return m_lease.TryNumber(reference: read.Reference!, truth: read.Truth, value: out value);
    }
    /// <summary>Returns a state read's current sample.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.State"/> reads.</param>
    /// <returns>The sample; its value holds no case when the cell does not resolve.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's state reads.</exception>
    public WorldStateSample Sample(CreationRigRead read) {
        Require(kind: CreationRigReadKind.State, read: read);

        return m_lease.Sample(slot: m_lease.Slot(reference: read.Reference!, truth: read.Truth));
    }
    /// <summary>Reads a state read whose cell spells a world-space <c>[x, y, z]</c>.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.State"/> reads.</param>
    /// <param name="value">The point; zero when the cell is absent, not text, or not three numbers.</param>
    /// <returns><see langword="true"/> when the cell parsed as three numbers.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's state reads.</exception>
    public bool TryPoint(CreationRigRead read, out Vector3 value) {
        Require(kind: CreationRigReadKind.State, read: read);

        return WorldGaitDrivers.TryReadStateVector(reads: m_lease, reference: read.Reference!, value: out value);
    }
    /// <summary>Reads another body's root pose as the client presents it this frame.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Body"/> reads.</param>
    /// <param name="position">The body's position; zero when it is not active.</param>
    /// <param name="orientation">The body's attitude; identity when it is not active.</param>
    /// <returns><see langword="true"/> when the body is active.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's body reads.</exception>
    public bool TryBody(CreationRigRead read, out Vector3 position, out Quaternion orientation) {
        Require(kind: CreationRigReadKind.Body, read: read);

        if ((m_client is not { } client) || (((uint)read.Body) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: read.Body)) {
            (position, orientation) = (Vector3.Zero, Quaternion.Identity);

            return false;
        }

        (position, orientation) = (client.Position(index: read.Body), client.Orientation(index: read.Body));

        return true;
    }
    /// <summary>Probes the presentation query field for a surface (<see cref="WorldEffectorSolver.TryProbeSurface"/>).</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Probe"/> reads.</param>
    /// <param name="answer">The probe's held answer, which a bit-identical next question reuses.</param>
    /// <param name="origin">The probe's world origin.</param>
    /// <param name="towards">The probe direction, in the creation's frame.</param>
    /// <param name="rootRotation">The creation root's world attitude.</param>
    /// <param name="reach">The probe's reach, metres.</param>
    /// <param name="standoff">The distance the target stands off the surface, metres.</param>
    /// <param name="target">The target point; <paramref name="origin"/> when nothing answers.</param>
    /// <param name="normal">The surface normal at the target; world up when nothing answers.</param>
    /// <returns><see langword="true"/> when the probe found a surface.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's probe reads.</exception>
    public bool TryProbe(CreationRigRead read, ref WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer> answer, Vector3 origin, Vector3 towards, Quaternion rootRotation, float reach, float standoff, out Vector3 target, out Vector3 normal) {
        Require(kind: CreationRigReadKind.Probe, read: read);

        return WorldEffectorSolver.TryProbeSurface(
            answer: ref answer,
            field: m_client?.StaticField,
            normal: out normal,
            origin: origin,
            reach: reach,
            rootRotation: rootRotation,
            standoff: standoff,
            target: out target,
            towards: towards
        );
    }

    private void Require(CreationRigRead read, CreationRigReadKind kind) {
        ArgumentNullException.ThrowIfNull(argument: read);

        if ((read.Kind != kind) || (((uint)read.Index) >= ((uint)m_rig.Reads.Count)) || !ReferenceEquals(objA: m_rig.Reads[read.Index], objB: read)) {
            throw new ArgumentException(message: $"The read '{read}' is not one of this rig's {kind.ToString().ToLowerInvariant()} reads.", paramName: nameof(read));
        }
    }
}

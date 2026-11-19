using System.Numerics;
using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
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
    private IWorldBodyPoses? m_poses;
    private IWorldSeatGuess? m_guess;
    private readonly CreationRigDriver[] m_leads;

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
        m_leads = [.. rig.Drivers.Where(predicate: static driver => (driver.Lead is not null))];
    }

    /// <summary>Gets whether a guess was in force for any of the rig's leads when <see cref="Revision"/> was last read: a
    /// registration with one is evaluated in full that frame, whatever its rate tier.</summary>
    public bool Guessing { get; private set; }

    /// <summary>Gets the rig whose reads the view answers.</summary>
    public CreationRig Rig => m_rig;
    /// <summary>Gets the wearing body's simulation facts this frame, or <see cref="BodyFacts.None"/> for a registration
    /// with no body or before the first <see cref="Bind"/>.</summary>
    public BodyFacts Facts => (((m_client is { } client) && (m_body >= 0)) ? client.Facts(index: m_body) : BodyFacts.None);
    /// <summary>Gets a revision of everything the view answers that can change between frames: the latest change of a
    /// state cell the lease holds (a cell that selects a body among them), and for each other body the rig reads, its
    /// identity, its root pose and the serial its own registration moves each time its pose changes. Equal revisions mean
    /// the reads answer what they answered.</summary>
    public int Revision {
        get {
            var hash = new HashCode();

            hash.Add(value: m_lease.Changed);
            foreach (var read in m_bodies) {
                if ((m_client is not { } client) || !TryBodyIndex(index: out var body, read: read)) {
                    hash.Add(value: -1);

                    continue;
                }

                hash.Add(value: client.EntityAddress(index: body));
                hash.Add(value: client.Position(index: body));
                hash.Add(value: client.Orientation(index: body));
                hash.Add(value: (m_poses?.PoseSerial(body: body) ?? 0));
            }

            // A guess moves the revision when it starts, changes level or ends.
            var guessing = false;

            foreach (var driver in m_leads) {
                var guessed = TryLead(level: out var level, read: driver.Lead!, seconds: driver.LeadSeconds);

                guessing |= guessed;
                hash.Add(value: guessed);
                hash.Add(value: level);
            }
            Guessing = guessing;

            return hash.ToHashCode();
        }
    }

    /// <summary>Points the view at this frame's client and wearing body.</summary>
    /// <param name="client">The client whose bodies, facts and query field the view reads.</param>
    /// <param name="body">The wearing body's population index, or -1 for a registration with none.</param>
    /// <param name="poses">The registrations' drawn poses another body's part and pose serial are read from; or
    /// <see langword="null"/>, which reads every body's serial as zero and every part as its body's root.</param>
    /// <param name="guess">The presenting seat's guess (<see cref="IWorldStampSource.SeatGuess"/>), which the view answers
    /// leads from only when its body is the wearing body; or <see langword="null"/> for none.</param>
    public void Bind(IWorldStampSource? client, int body, IWorldBodyPoses? poses = null, IWorldSeatGuess? guess = null) {
        m_client = client;
        m_body = body;
        m_poses = poses;
        m_guess = (((guess is not null) && (body >= 0) && (guess.Body == body)) ? guess : null);
    }
    /// <summary>Reads a driver's lead: the presenting seat's own level on the lead's channel, while a guess is in force for
    /// the wearing body (<see cref="WorldSeatInput.TryLead"/>).</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Lead"/> reads.</param>
    /// <param name="seconds">How long, in seconds, an unanswered guess may show.</param>
    /// <param name="level">The level; zero when there is no guess.</param>
    /// <returns><see langword="true"/> while a guess is in force; the driver then reads it in place of its cell.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's lead reads.</exception>
    public bool TryLead(CreationRigRead read, float seconds, out float level) {
        Require(kind: CreationRigReadKind.Lead, read: read);
        level = 0f;

        return ((m_guess is { } guess) && guess.TryLead(channel: read.Reference!, leadSeconds: seconds, level: out level));
    }
    /// <summary>Resolves which body a body read names this frame: its fixed index, or the index its selecting cell holds
    /// as stored truth. A cell selects a body only when it holds a whole number naming an active body.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Body"/> reads.</param>
    /// <param name="index">The body's population index; -1 when the read names no active body.</param>
    /// <returns><see langword="true"/> when the read names an active body.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's body reads.</exception>
    public bool TryBodyIndex(CreationRigRead read, out int index) {
        Require(kind: CreationRigReadKind.Body, read: read);

        index = read.Body;
        if (read.Reference is { } selector) {
            index = -1;
            if (!m_lease.TryNumber(reference: selector, truth: true, value: out var held) || (held != MathF.Floor(x: held)) || (held < 0f) || (held >= WorldClient.EntityCapacity)) {
                return false;
            }
            index = ((int)held);
        }
        if ((m_client is not { } client) || (((uint)index) >= ((uint)WorldClient.EntityCapacity)) || !client.IsActive(index: index)) {
            index = -1;

            return false;
        }

        return true;
    }
    /// <summary>Reads the identity of the body a body read names this frame: its slot and the generation of its
    /// occupant, so a slot reused by another occupant reads as another target.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Body"/> reads.</param>
    /// <param name="address">The body's address; default when the read names no active body.</param>
    /// <returns><see langword="true"/> when the read names an active body.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's body reads.</exception>
    public bool TryBodyAddress(CreationRigRead read, out WorldEntityAddress address) {
        address = default;
        if (!TryBodyIndex(index: out var body, read: read)) { return false; }
        address = m_client!.EntityAddress(index: body);

        return true;
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
    /// <summary>Reads the pose a body read names this frame: the named part's anchor as its registration last drew it,
    /// or the body's root as the client presents it when the read names no part or the body's look publishes no such
    /// part.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Body"/> reads.</param>
    /// <param name="position">The pose's position; zero when the read names no active body.</param>
    /// <param name="orientation">The pose's attitude; identity when the read names no active body.</param>
    /// <returns><see langword="true"/> when the read names an active body.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's body reads.</exception>
    public bool TryBody(CreationRigRead read, out Vector3 position, out Quaternion orientation) {
        if (!TryBodyIndex(index: out var body, read: read)) {
            (position, orientation) = (Vector3.Zero, Quaternion.Identity);

            return false;
        }

        var client = m_client!;

        if ((read.Part is { } part) && (m_poses is { } poses) && poses.TryBodyPartAuthoredPose(bodyIndex: body, client: client, partId: part, pose: out var anchor)) {
            (position, orientation) = (anchor.Position, anchor.Orientation);

            return true;
        }

        (position, orientation) = (client.Position(index: body), client.Orientation(index: body));

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

    /// <summary>Returns the field a probe read asks this frame, which stays the same field through a frame in which one
    /// probe misses.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Probe"/> reads.</param>
    /// <returns>The presentation query field, or <see langword="null"/> when the client has none.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's probe reads.</exception>
    public SdfFieldEvaluator? ProbeField(CreationRigRead read) {
        Require(kind: CreationRigReadKind.Probe, read: read);

        return m_client?.StaticField;
    }

    private void Require(CreationRigRead read, CreationRigReadKind kind) {
        ArgumentNullException.ThrowIfNull(argument: read);

        if ((read.Kind != kind) || (((uint)read.Index) >= ((uint)m_rig.Reads.Count)) || !ReferenceEquals(objA: m_rig.Reads[read.Index], objB: read)) {
            throw new ArgumentException(message: $"The read '{read}' is not one of this rig's {kind.ToString().ToLowerInvariant()} reads.", paramName: nameof(read));
        }
    }
}
/// <summary>The presenting seat's guess on its own input (<c>CreationDriverDocument.Lead</c>): offered only by a client that
/// presents for exactly one seat, for the body that seat controls, so no other viewer's picture can carry it.</summary>
public interface IWorldSeatGuess {
    /// <summary>Gets the population index of the body the seat controls.</summary>
    int Body { get; }
    /// <summary>Answers a lead through the seat's one guess function (<see cref="WorldSeatInput.TryLead"/>).</summary>
    /// <param name="channel">The input channel's name.</param>
    /// <param name="leadSeconds">How long, in seconds, an unanswered guess may show.</param>
    /// <param name="level">The seat's own level on the channel; zero when there is no guess.</param>
    /// <returns><see langword="true"/> while a guess is in force.</returns>
    bool TryLead(string channel, float leadSeconds, out float level);
}
/// <summary>The drawn poses of the bodies a rig may read (<see cref="WorldRigReads.Bind"/>): another body's part and the
/// serial its registration moves each time its pose changes.</summary>
public interface IWorldBodyPoses {
    /// <summary>Returns the pose serial of a body's registration.</summary>
    /// <param name="body">The body's population index.</param>
    /// <returns>The serial; zero for a body with no registration.</returns>
    int PoseSerial(int body);
    /// <summary>Resolves a body's part as its registration last drew it, and records that something read it.</summary>
    /// <param name="bodyIndex">The body's population index.</param>
    /// <param name="partId">The ordinal, case-sensitive part id.</param>
    /// <param name="client">The client supplying the body's root pose.</param>
    /// <param name="pose">The part's world pose; default when unresolved.</param>
    /// <returns><see langword="true"/> when the body's look publishes the part.</returns>
    bool TryBodyPartAuthoredPose(int bodyIndex, string partId, IWorldStampSource client, out SdfAnchor pose);
}

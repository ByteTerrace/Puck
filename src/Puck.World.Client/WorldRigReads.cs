using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SdfVm;
using Puck.World.Authoring;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The one view a registration's pose pass reads the world through: it answers exactly the reads its rig compiled
/// (<see cref="CreationRig.Reads"/>) and nothing else. A <see cref="CreationRigRead"/> is made only by a rig, and the view
/// refuses one another rig made, so a driver advance or an effector solve cannot read what the rig did not list, and the
/// wake test (<see cref="Revision"/>) and the presentation manifest see every read the pose depends on.
/// </summary>
/// <remarks>Presentation-only, on whichever thread poses the registration (a partition of the pose fork, or the frame
/// thread for a rig that reads another body): the view reads the client's state mirror through the registration's lease
/// and the client's interpolated body poses, and nothing it answers re-enters the simulation.</remarks>
public sealed class WorldRigReads {
    private readonly CreationRig m_rig;
    private readonly WorldStateLease m_lease;
    private readonly CreationRigRead[] m_bodies;
    private IWorldStampSource? m_client;
    private int m_body = -1;
    private IWorldBodyPoses? m_poses;
    private readonly CreationRigDriver[] m_leads;
    // A guessing view's guess, presentation clock and, for each of the rig's lead reads, the edge it last saw outstanding
    // and the clock reading it first saw that edge at; null for a view of the shared pose.
    private readonly LeadAges? m_ages;
    private IWorldSeatGuess? m_guess;

    /// <summary>Initializes a new instance of the <see cref="WorldRigReads"/> class over a rig's reads: a view of the
    /// shared pose, every reader's, which answers no lead (<see cref="TryLead"/> reads the cell's place empty).</summary>
    /// <param name="rig">The rig whose reads the view answers.</param>
    /// <param name="lease">The registration's lease of the state mirror, bound to the wearing body, which state reads go
    /// through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> or <paramref name="lease"/> is
    /// <see langword="null"/>.</exception>
    public WorldRigReads(CreationRig rig, WorldStateLease lease) : this(guessing: false, lease: lease, rig: rig) { }
    private WorldRigReads(CreationRig rig, WorldStateLease lease, bool guessing) {
        ArgumentNullException.ThrowIfNull(argument: rig);
        ArgumentNullException.ThrowIfNull(argument: lease);

        m_rig = rig;
        m_lease = lease;
        m_bodies = [.. rig.Reads.Where(predicate: static read => (read.Kind == CreationRigReadKind.Body))];
        m_leads = [.. rig.Drivers.Where(predicate: static driver => (driver.Lead is not null))];
        m_ages = (guessing ? new LeadAges(reads: rig.Reads.Count) : null);
    }
    /// <summary>Creates a guessing view over a rig's reads: the view of a presenting seat's own guess layer, the one kind of
    /// view <see cref="BindGuess"/> accepts. Only the guess layer of a stamp pool makes one, for the pose drawn in the
    /// owning seat's own view alone (<see cref="WorldCreationInstances.GuessOverlay"/>); every registration any other
    /// reader resolves carries a view of the shared pose.</summary>
    /// <param name="rig">The rig whose reads the view answers.</param>
    /// <param name="lease">The guess layer's lease of the state mirror, bound to the wearing body.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> or <paramref name="lease"/> is
    /// <see langword="null"/>.</exception>
    public static WorldRigReads ForGuess(CreationRig rig, WorldStateLease lease) => new(guessing: true, lease: lease, rig: rig);

    /// <summary>Gets whether a guess was in force for any of the rig's leads when <see cref="Revision"/> was last read;
    /// always <see langword="false"/> on a view of the shared pose.</summary>
    public bool Guessing { get; private set; }
    /// <summary>Gets whether this is a guessing view (<see cref="ForGuess"/>).</summary>
    public bool AnswersLeads => (m_ages is not null);

    /// <summary>Gets the wearing body's simulation facts this frame, or <see cref="BodyFacts.None"/> for a registration
    /// with no body or before the first <see cref="Bind"/>.</summary>
    public BodyFacts Facts => (((m_client is { } client) && (m_body >= 0)) ? client.Facts(index: m_body) : BodyFacts.None);
    /// <summary>Gets a revision of everything the view answers that can change between frames: the latest change of a
    /// state cell the lease holds (a cell that selects a body among them), and for each other body the rig reads, its
    /// identity, its root pose and the serial its own registration moves each time its pose changes. Equal revisions mean
    /// the reads answer what they answered: the revision is a 64-bit digest of every bit it names.</summary>
    public ulong Revision {
        get {
            var hash = Fnv1aHash.Create();

            hash.Add(value: unchecked((uint)m_lease.Changed));
            foreach (var read in m_bodies) {
                if ((m_client is not { } client) || !TryBodyIndex(index: out var body, read: read)) {
                    hash.Add(value: 0U);

                    continue;
                }

                var address = client.EntityAddress(index: body);

                hash.Add(value: 1U);
                hash.Add(value: address.Authority);
                hash.Add(value: unchecked((uint)address.Index));
                hash.Add(value: unchecked((uint)address.Generation));
                hash.Add(value: (address.Titled ? 1U : 0U));
                hash.AddBits(value: client.Position(index: body));
                hash.AddBits(value: client.Orientation(index: body));
                hash.Add(value: unchecked((uint)(m_poses?.PoseSerial(body: body) ?? 0)));
            }

            // A guess moves the revision when it starts, changes level or ends.
            var guessing = false;

            if (m_ages is not null) {
                foreach (var driver in m_leads) {
                    var guessed = TryLead(level: out var level, read: driver.Lead!, seconds: driver.LeadSeconds);

                    guessing |= guessed;
                    hash.Add(value: (guessed ? 1U : 0U));
                    hash.AddBits(value: level);
                }
            }
            Guessing = guessing;

            return hash.Value;
        }
    }

    /// <summary>Points the view at this frame's client and wearing body.</summary>
    /// <param name="client">The client whose bodies, facts and query field the view reads.</param>
    /// <param name="body">The wearing body's population index, or -1 for a registration with none.</param>
    /// <param name="poses">The registrations' drawn poses another body's part and pose serial are read from; or
    /// <see langword="null"/>, which reads every body's serial as zero and every part as its body's root.</param>
    public void Bind(IWorldStampSource? client, int body, IWorldBodyPoses? poses = null) {
        m_client = client;
        m_body = body;
        m_poses = poses;
    }
    /// <summary>Points a guessing view at this frame's guess and presentation clock.</summary>
    /// <param name="guess">The presenting seat's guess (<see cref="IWorldStampSource.SeatGuess"/>), which the view answers
    /// leads from only when its body is the wearing body; or <see langword="null"/> for none.</param>
    /// <param name="clockSeconds">The guess layer's presentation clock, in seconds: the frames' presented time summed,
    /// which advances by the completed ticks under lockstep and by the frames' own time otherwise, never the wall
    /// clock, so a guess's age (<c>leadSeconds</c>) repeats wherever the frames repeat.</param>
    /// <exception cref="InvalidOperationException">The view is a view of the shared pose, which no guess may reach.</exception>
    public void BindGuess(IWorldSeatGuess? guess, float clockSeconds) {
        if (m_ages is not { } ages) { throw new InvalidOperationException(message: "Only a guess layer's view (WorldRigReads.ForGuess) reads a guess; the shared pose every reader resolves never does."); }

        m_guess = (((guess is not null) && (m_body >= 0) && (guess.Body == m_body)) ? guess : null);
        ages.Clock = clockSeconds;
    }
    /// <summary>Reads a driver's lead: the presenting seat's own level on the lead's channel, while one of its edges on the
    /// channel is outstanding (<see cref="WorldSeatInput.TryLead"/>) and no older on the presentation clock than
    /// <paramref name="seconds"/>. A view of the shared pose answers none.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Lead"/> reads.</param>
    /// <param name="seconds">How long, in seconds of presented time, an unanswered guess may show.</param>
    /// <param name="level">The level; zero when there is no guess.</param>
    /// <returns><see langword="true"/> while a guess is in force; the driver then reads it in place of its cell.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's lead reads.</exception>
    public bool TryLead(CreationRigRead read, float seconds, out float level) {
        Require(kind: CreationRigReadKind.Lead, read: read);
        level = 0f;
        if ((m_ages is not { } ages) || (m_guess is not { } guess)) { return false; }
        if (!guess.TryLead(channel: read.Reference!, edge: out var edge, level: out var guessed)) {
            ages.Edge[read.Index] = 0UL;

            return false;
        }
        if (ages.Edge[read.Index] != edge) {
            ages.Edge[read.Index] = edge;
            ages.Since[read.Index] = ages.Clock;
        }
        if ((ages.Clock - ages.Since[read.Index]) > seconds) { return false; }
        level = guessed;

        return true;
    }
    // The ages of a guessing view's leads, indexed by read: the outstanding edge each last saw and the clock reading it
    // was first seen at.
    private sealed class LeadAges(int reads) {
        public float Clock;
        public readonly ulong[] Edge = new ulong[reads];
        public readonly float[] Since = new float[reads];
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
            if (!m_lease.TryNumber(reference: selector, truth: true, value: out var held) || (held != MathF.Floor(x: held)) || (held < 0f) || (held >= WorldBodiesLimits.IndexLimit)) {
                return false;
            }
            index = ((int)held);
        }
        if ((m_client is not { } client) || !WorldBodiesLimits.IsBodyIndex(index: index) || !client.IsActive(index: index)) {
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
    /// <summary>Asks whether a resting effector's latched stance has drifted past its replant distance
    /// (<see cref="WorldEffectorSolver.StanceDrifted"/>).</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Probe"/> reads.</param>
    /// <param name="answer">The probe's held answer.</param>
    /// <param name="origin">The probe's world origin.</param>
    /// <param name="towards">The probe direction, in the creation's frame.</param>
    /// <param name="rootRotation">The creation root's world attitude.</param>
    /// <param name="reach">The probe's reach, metres.</param>
    /// <param name="latch">The latched stance's surface point, world units.</param>
    /// <param name="distance">The replant distance, metres.</param>
    /// <returns><see langword="true"/> when the probe found a surface point farther than
    /// <paramref name="distance"/> from <paramref name="latch"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's probe reads.</exception>
    public bool StanceDrifted(CreationRigRead read, ref WorldFieldAnswer<WorldSurfaceQuery, WorldSurfaceAnswer> answer, Vector3 origin, Vector3 towards, Quaternion rootRotation, float reach, Vector3 latch, float distance) {
        Require(kind: CreationRigReadKind.Probe, read: read);

        return WorldEffectorSolver.StanceDrifted(answer: ref answer, distance: distance, field: m_client?.StaticField, latch: latch, origin: origin, reach: reach,
            rootRotation: rootRotation, towards: towards);
    }

    /// <summary>Returns the field a probe read asks this frame, which stays the same field through a frame in which one
    /// probe misses.</summary>
    /// <param name="read">One of the rig's <see cref="CreationRigReadKind.Probe"/> reads.</param>
    /// <returns>The presentation query field, or <see langword="null"/> when the client has none.</returns>
    /// <exception cref="ArgumentException"><paramref name="read"/> is not one of the rig's probe reads.</exception>
    public WorldQueryField? ProbeField(CreationRigRead read) {
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
/// <summary>The presenting seat's guess on its own input (<c>CreationDriverDocument.Lead</c>), offered to the scene of the
/// world the seat's body is in (<see cref="WorldClient.SeatGuessAt"/>). Only that scene's guess layer reads it
/// (<see cref="WorldRigReads.ForGuess"/>), into rows only the seat's own view reads
/// (<see cref="WorldCreationInstances.GuessOverlay"/>), so no camera, anchor, pointer, other view or read-back of the
/// shared pose can carry it.</summary>
public interface IWorldSeatGuess {
    /// <summary>Gets the population index, in the scene's world, of the body the seat controls.</summary>
    int Body { get; }
    /// <summary>Gets the seat's 0-based roster slot, whose own view alone draws the guess.</summary>
    int Slot { get; }
    /// <summary>Answers whether one of the seat's edges on a channel is outstanding, through the seat's one guess function
    /// (<see cref="WorldSeatInput.TryLead"/>).</summary>
    /// <param name="channel">The input channel's name.</param>
    /// <param name="level">The seat's own level on the channel; zero when no edge is outstanding.</param>
    /// <param name="edge">The outstanding edge's identity, which a later edge never shares; zero when none is.</param>
    /// <returns><see langword="true"/> while an edge is outstanding.</returns>
    bool TryLead(string channel, out float level, out ulong edge);
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

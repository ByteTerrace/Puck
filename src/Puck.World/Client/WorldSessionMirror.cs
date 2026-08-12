using System.Diagnostics;
using System.Numerics;
using Puck.Hosting;
using Puck.World.Server;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session projection's client-side mirror — the "minimal client-side pose/state mirror" docs/vision.md's
/// "Observation and display" names, attached to a destination instance's <c>WorldServer</c> under an
/// <see cref="Server.WorldServer.AttachSink"/> lease exactly like any other client. It is deliberately not
/// <see cref="WorldClient"/>: that type also carries a <c>PlayerRoster</c>/seat table this observation-only mirror
/// has no use for (the destination owns any arrived bodies; this observer owns no local seat table),
/// grants/machine/action state it never
/// decodes, and a correction-error easer this mirror does not reproduce — so it keeps only what a session screen's
/// projection graph actually renders: the destination's live document (its static authored geometry) plus a bounded
/// per-entity render-pose table (position, attitude, body color, and look index) for every active body the
/// destination's own tick reports.
/// </summary>
/// <remarks>
/// <para><b>Live pose mirroring.</b> <see cref="DeliverSnapshot"/> keeps a double-buffered previous/current pose per
/// entity index — the same shape <see cref="WorldClient"/> keeps — so <see cref="WorldSessionSceneEmitter"/> can
/// interpolate avatars exactly like the boot avatar path does. It deliberately drops what avatar rendering does not
/// need: <see cref="EntitySnapshot.Kit"/> (render selection is index/look-keyed only; nothing branches on kit yet,
/// exactly as <see cref="WorldClient"/> itself notes), <see cref="EntitySnapshot.PlacementId"/> (a body-rooted
/// creation stamp — a driven vehicle's own geometry riding the body's pose — needs <c>WorldStampPool</c>, whose
/// <c>PackTransforms</c>/<c>RootPose</c> are hard-typed against a concrete <see cref="WorldClient"/> instance rather
/// than an abstraction this mirror could satisfy; widening that pool's contract is out of this type's owned scope, so
/// an inhabited/driven body still mirrors — and moves — as its catalog avatar rather than the vehicle's creation
/// geometry), and the correction-error easer (<see cref="EntityContinuity.Kind"/> still selects snap-vs-interpolate,
/// it just never arms a decaying offset).</para>
/// <para><b>Borrowed-snapshot contract.</b> <see cref="DeliverSnapshot"/> is called synchronously on the
/// destination's own fixed-step thread, and <see cref="WorldSnapshot.Entries"/> wraps a reused server-owned array
/// (see <c>Server.WorldOutputHub</c>'s own remarks) that the destination's NEXT tick overwrites — every field this
/// method keeps is copied into this mirror's own arrays before the call returns, never retained by reference.</para>
/// <para><b>Publication.</b> An embedded destination delivers on its fixed-step thread; a federated observer delivers
/// on its socket task while the presentation thread reads this mirror. Definition and scalar clock/revision fields
/// therefore publish through <see cref="Volatile"/>/<see cref="Interlocked"/>. Each entity record is copied before
/// its active flag's release write; readers acquire that flag before consuming the copied pose fields.</para>
/// </remarks>
internal sealed class WorldSessionMirror : IClientSink {
    // The bounded per-entity table width — tied directly to the avatar catalog this mirror's poses are stamped
    // through (WorldSessionSceneEmitter reads WorldAvatarCatalog.Capacity for the exact same reason), rather than to
    // WorldClient.EntityCapacity: both trace to the SAME WorldPopulationLimits.CapacityCeiling source today (see that
    // constant's own single-sourcing remarks), but this mirror has no other reason to depend on WorldClient at all.
    private const int EntityCapacity = WorldAvatarCatalog.Capacity;

    private readonly Vector3[] m_previousPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_previousOrientation = new Quaternion[EntityCapacity];
    private readonly Vector3[] m_currentPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_currentOrientation = new Quaternion[EntityCapacity];
    private readonly Vector3[] m_bodyColor = new Vector3[EntityCapacity];
    private readonly byte[] m_look = new byte[EntityCapacity];
    private readonly byte[] m_catalogRig = new byte[EntityCapacity];
    private readonly byte[] m_kit = new byte[EntityCapacity];
    private readonly int[] m_generation = new int[EntityCapacity];
    private readonly bool[] m_active = new bool[EntityCapacity];
    private readonly bool[] m_seen = new bool[EntityCapacity];
    // A route seed is written by the transfer/route thread while ordinary snapshots arrive on the observer task.
    // Serialize those two writers: the seqlock below protects readers from a torn image, but by itself does not
    // prevent two writers from interleaving their odd/even sequence increments and publishing a mixture.
    private readonly object m_snapshotWriteGate = new();

    // Attach replays the authority's most recently completed snapshot. A transfer committed after that snapshot
    // can therefore seed an entity at the SAME tick immediately before attach replays an image in which the entity
    // is still absent. Keep the causally newer commit image until an ordinary snapshot advances beyond its tick or
    // explicitly contains the committed generation.
    private WorldEntityAddress? m_seededRouteEntity;
    private ulong m_seededRouteTick;

    private WorldDefinition m_definition;
    private FixedWorldCollider?[] m_kitColliders;
    private WorldBodyContactMode[] m_kitBodyContacts;
    private string m_authority = string.Empty;
    private int m_definitionRevision;
    private long m_tickBits;
    private long m_stepTicksBits;
    private int m_snapshotRevision;
    // Seqlock guarding a coherent copy of the complete delivered entity image. Odd means the socket/server delivery
    // is writing; even means stable. Per-field publication remains for render reads that do not require a whole-tick
    // record, while adjacency simulation uses CopySnapshotTo below.
    private int m_snapshotSequence;
    // The destination's own step duration in seconds, derived from the latest snapshot's StepTicks — the
    // presentation clock WorldSessionSceneEmitter normalizes real elapsed time against (see its own remarks on why
    // it cannot read a host alpha/delta for a session view).
    private int m_stepSecondsBits;
    // A real (wall-clock) timestamp — Stopwatch.GetTimestamp() — captured the instant the CURRENT snapshot's poses
    // were copied in. Presentation-only, exactly like WorldClient's own render-pose interpolation is presentation
    // floats never fed back into simulation state (rule 4's carve-out): the destination's tick thread and the render
    // thread calling WorldSessionSceneEmitter both live in this SAME process, so a real elapsed-time read between
    // them is an honest measure, not a cross-machine clock assumption.
    private long m_snapshotArrivalTimestamp = Stopwatch.GetTimestamp();

    /// <summary>Initializes the mirror with an empty placeholder definition — <c>Server.WorldServer.AttachSink</c>
    /// always delivers the live definition synchronously before returning its lease, so no caller ever observes this
    /// placeholder.</summary>
    /// <param name="placeholder">The definition to hold until the first delivery.</param>
    public WorldSessionMirror(WorldDefinition placeholder) {
        ArgumentNullException.ThrowIfNull(argument: placeholder);

        m_definition = placeholder;
        m_kitColliders = CompileColliders(definition: placeholder);
        m_kitBodyContacts = CompileBodyContacts(definition: placeholder);

        for (var index = 0; (index < EntityCapacity); index++) {
            m_previousOrientation[index] = Quaternion.Identity;
            m_currentOrientation[index] = Quaternion.Identity;
        }
    }

    /// <summary>The destination's live world definition — the boot/attach definition until a later mutation batch or
    /// swap delivers a new one.</summary>
    public WorldDefinition Definition => Volatile.Read(ref m_definition);

    /// <summary>The monotonic definition-delivery counter — bumped each time the destination delivers a new
    /// definition, the rebuild-watch component <see cref="WorldSessionSceneEmitter"/> reads.</summary>
    public int DefinitionRevision => Volatile.Read(ref m_definitionRevision);

    /// <summary>The destination's latest completed simulation tick — read-back (<c>world.faces</c>'s session echo)
    /// AND the presentation clock <see cref="WorldSessionSceneEmitter"/> resolves its render alpha against (via
    /// <see cref="StepSeconds"/>/<see cref="SnapshotArrivalTimestamp"/>).</summary>
    public ulong Tick => unchecked((ulong)Interlocked.Read(ref m_tickBits));

    /// <summary>The destination's own step width (engine ticks per its authored simulation step) at the latest
    /// delivered snapshot — the destination presentation clock docs/vision.md's "Observation and display"
    /// names.</summary>
    public ulong StepTicks => unchecked((ulong)Interlocked.Read(ref m_stepTicksBits));

    /// <summary><see cref="StepTicks"/> converted to seconds — the denominator
    /// <see cref="WorldSessionSceneEmitter"/>'s self-derived render alpha divides real elapsed time by.</summary>
    public float StepSeconds => BitConverter.Int32BitsToSingle(Volatile.Read(ref m_stepSecondsBits));

    /// <summary>The wall-clock timestamp (<see cref="Stopwatch.GetTimestamp"/> units) the current snapshot's poses
    /// were copied in at — the "render call's arrival" baseline <see cref="WorldSessionSceneEmitter"/> measures
    /// elapsed real time against, since a session view supplies neither a host delta nor a host alpha (see that
    /// type's own remarks).</summary>
    public long SnapshotArrivalTimestamp => Interlocked.Read(ref m_snapshotArrivalTimestamp);

    /// <summary>The honest presentation fraction through the currently delivered snapshot interval. Remote and
    /// colocated mirrors derive it from the snapshot's own step width and arrival time, never from whichever world
    /// happens to be drawing them.</summary>
    public float InterpolationAlpha => ResolveInterpolationAlpha(stepSeconds: StepSeconds, arrivalTimestamp: SnapshotArrivalTimestamp);

    /// <summary>The declared-set/palette revision from the latest snapshot — a separate rebuild-watch component from
    /// <see cref="DefinitionRevision"/> (never summed with it: this one is assigned from the wire and can move down,
    /// exactly like <see cref="WorldClient.WriteRevision"/>'s own server-revision component, for the identical
    /// reason).</summary>
    public int SnapshotRevision => Volatile.Read(ref m_snapshotRevision);

    /// <summary>The authority named by the latest delivered snapshot.</summary>
    public string Authority => Volatile.Read(ref m_authority);

    /// <summary>Whether the entity at <paramref name="index"/> was active (drawn) in the latest snapshot.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => Volatile.Read(ref m_active[index]);

    /// <summary>The durable address of the entity currently occupying a slot.</summary>
    public WorldEntityAddress Address(int index) => new(Authority: Authority, Index: index, Generation: Volatile.Read(ref m_generation[index]));

    /// <summary>The entity's previous-tick render position (one interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 PreviousPosition(int index) => m_previousPosition[index];

    /// <summary>The entity's previous-tick render attitude (one interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion PreviousOrientation(int index) => m_previousOrientation[index];

    /// <summary>The entity's latest-tick render position (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 CurrentPosition(int index) => m_currentPosition[index];

    /// <summary>The entity's latest-tick render attitude (the other interpolation endpoint).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion CurrentOrientation(int index) => m_currentOrientation[index];

    /// <summary>The entity's render body color, mirrored verbatim from the snapshot (a pending seat's is already
    /// gray-lerped server-side).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 BodyColor(int index) => m_bodyColor[index];

    /// <summary>The look row an entity wears: the delivered look table indexed by the entity's mirrored look index,
    /// or the implicit single catalog look when the world authors no <c>looks</c> section, and for an index the
    /// delivered table cannot cover. The same resolve <see cref="WorldClient.Look"/> performs, over this mirror's own
    /// table instead.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The entity's look row.</returns>
    public WorldLook Look(int index) {
        var rows = m_definition.Looks;

        if (rows.Count == 0) {
            return WorldLook.Implicit;
        }

        var lookIndex = m_look[index];

        return ((lookIndex < rows.Count) ? rows[lookIndex] : WorldLook.Implicit);
    }

    /// <summary>The entity-owned procedural rig mirrored from the authoritative snapshot.</summary>
    public byte CatalogRig(int index) => m_catalogRig[index];

    public FixedWorldCollider? Collider(int index) {
        var colliders = Volatile.Read(ref m_kitColliders);
        var kit = m_kit[index];
        return ((kit < colliders.Length) ? colliders[kit] : null);
    }

    public WorldBodyContactMode BodyContact(int index) {
        var contacts = Volatile.Read(ref m_kitBodyContacts);
        var kit = m_kit[index];
        return ((kit < contacts.Length) ? contacts[kit] : WorldBodyContactMode.Overlap);
    }

    /// <summary>Publishes the exact committed head of a traveler route before its observation socket can deliver the
    /// destination's first ordinary snapshot. This is not prediction: the route answer was read under the final
    /// authority's operation gate after commit. The subsequent snapshot replaces the seed normally.</summary>
    public void SeedRoute(in WorldAuthorityRouteDescription route) {
        var index = route.Entity.Index;
        if ((uint)index >= EntityCapacity) {
            throw new ArgumentOutOfRangeException(paramName: nameof(route), message: $"route entity {index} exceeds mirror capacity {EntityCapacity}");
        }

        lock (m_snapshotWriteGate) {
            DeliverDefinition(definition: route.Definition);
            _ = Interlocked.Increment(ref m_snapshotSequence);
            var position = route.Position.ToVector3();
            var orientation = route.Orientation.ToQuaternion();
            m_previousPosition[index] = position;
            m_currentPosition[index] = position;
            m_previousOrientation[index] = orientation;
            m_currentOrientation[index] = orientation;
            m_bodyColor[index] = route.BodyColor;
            m_kit[index] = route.Kit;
            m_look[index] = route.Look;
            m_catalogRig[index] = route.CatalogRig;
            Volatile.Write(ref m_generation[index], route.Entity.Generation);
            Volatile.Write(ref m_authority, route.Entity.Authority);
            _ = Interlocked.Exchange(location1: ref m_tickBits, value: unchecked((long)route.Tick));
            _ = Interlocked.Exchange(location1: ref m_snapshotArrivalTimestamp, value: Stopwatch.GetTimestamp());
            Volatile.Write(ref m_active[index], value: true);
            m_seededRouteEntity = route.Entity;
            m_seededRouteTick = route.Tick;
            _ = Interlocked.Increment(ref m_snapshotSequence);
        }
    }

    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        Volatile.Write(ref m_kitColliders, CompileColliders(definition: definition));
        Volatile.Write(ref m_kitBodyContacts, CompileBodyContacts(definition: definition));
        Volatile.Write(ref m_definition, definition);
        _ = Interlocked.Increment(ref m_definitionRevision);
    }

    /// <inheritdoc/>
    /// <remarks>Copies every kept field out of the borrowed <paramref name="snapshot"/> before returning (see this
    /// type's own borrowed-snapshot remarks). Continuity mirrors <see cref="WorldClient.DeliverSnapshot"/>'s
    /// snap-vs-interpolate split only — a newly active entity or a <see cref="EntityContinuityKind.Teleport"/> resets
    /// both interpolation endpoints to the fresh pose so the first/next frame never streaks; every other case
    /// (including <see cref="EntityContinuityKind.Correction"/>, which the boot client eases with a decaying render-
    /// error offset this mirror does not reproduce) shifts current into previous, ordinary double-buffering.</remarks>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        lock (m_snapshotWriteGate) {
            var seeded = m_seededRouteEntity;
            var containsSeed = ((seeded is { } address) && SnapshotContains(snapshot: in snapshot, entity: in address));
            var preserveSeed = ((seeded is not null) && !containsSeed && (snapshot.Tick <= m_seededRouteTick));
            if (containsSeed || ((seeded is not null) && !preserveSeed)) {
                m_seededRouteEntity = null;
            }

            _ = Interlocked.Increment(ref m_snapshotSequence);
            Array.Clear(array: m_seen);

            foreach (ref readonly var entry in snapshot.Entries.Span) {
                var index = entry.Index;

                if ((uint)index >= EntityCapacity ||
                    (preserveSeed && (seeded is { } preserved) && (index == preserved.Index) &&
                        (!string.Equals(a: snapshot.Authority, b: preserved.Authority, comparisonType: StringComparison.Ordinal) || (entry.Generation != preserved.Generation)))) {
                    continue;
                }

                m_seen[index] = true;
                m_bodyColor[index] = entry.BodyColor;
                m_kit[index] = entry.Kit;
                m_look[index] = entry.Look;
                m_catalogRig[index] = entry.CatalogRig;
                Volatile.Write(ref m_generation[index], entry.Generation);

                if (!Volatile.Read(ref m_active[index]) || (entry.Continuity.Kind == EntityContinuityKind.Teleport)) {
                    m_previousPosition[index] = entry.Position;
                    m_previousOrientation[index] = entry.Orientation;
                } else {
                    m_previousPosition[index] = m_currentPosition[index];
                    m_previousOrientation[index] = m_currentOrientation[index];
                }

                m_currentPosition[index] = entry.Position;
                m_currentOrientation[index] = entry.Orientation;
                // The release write is the publication edge for every pose/color/look field above. A local instance
                // delivers and renders on one thread, but a federated observer necessarily writes from its socket task;
                // IsActive's acquire read makes the completed record visible before an emitter consumes it.
                Volatile.Write(ref m_active[index], value: entry.Active);
            }

            for (var index = 0; (index < EntityCapacity); index++) {
                if (!m_seen[index] && (!preserveSeed || (seeded is not { } preserved) || (index != preserved.Index))) {
                    Volatile.Write(ref m_active[index], value: false);
                }
            }

            _ = Interlocked.Exchange(location1: ref m_tickBits, value: unchecked((long)Math.Max(snapshot.Tick, preserveSeed ? m_seededRouteTick : 0UL)));
            if (!preserveSeed) {
                Volatile.Write(ref m_authority, snapshot.Authority);
            }
            _ = Interlocked.Exchange(location1: ref m_stepTicksBits, value: unchecked((long)snapshot.StepTicks));
            Volatile.Write(location: ref m_snapshotRevision, value: snapshot.Revision);
            Volatile.Write(location: ref m_stepSecondsBits, value: BitConverter.SingleToInt32Bits((float)EngineTicks.ToSeconds(ticks: snapshot.StepTicks)));
            _ = Interlocked.Exchange(location1: ref m_snapshotArrivalTimestamp, value: Stopwatch.GetTimestamp());
            _ = Interlocked.Increment(ref m_snapshotSequence);
        }
    }

    private static bool SnapshotContains(in WorldSnapshot snapshot, in WorldEntityAddress entity) {
        if (!string.Equals(a: snapshot.Authority, b: entity.Authority, comparisonType: StringComparison.Ordinal)) {
            return false;
        }

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            if ((entry.Index == entity.Index) && (entry.Generation == entity.Generation) && entry.Active) {
                return true;
            }
        }
        return false;
    }

    /// <summary>Copies one coherent delivered entity record for simulation pinning. If a socket delivery overlaps
    /// the copy, the seqlock retries rather than exposing a mixture of two remote ticks.</summary>
    internal void CopySnapshotTo(
        bool[] active,
        WorldEntityAddress[] addresses,
        Vector3[] previousPositions,
        Quaternion[] previousOrientations,
        Vector3[] currentPositions,
        Quaternion[] currentOrientations,
        Vector3[] colors,
        WorldLook[] looks,
        byte[] catalogRigs,
        FixedWorldCollider?[] colliders,
        WorldBodyContactMode[] bodyContacts,
        out ulong tick,
        out int revision,
        out float stepSeconds,
        out long arrivalTimestamp
    ) {
        for (;;) {
            var sequence = Volatile.Read(ref m_snapshotSequence);
            if ((sequence & 1) != 0) {
                Thread.SpinWait(iterations: 1);
                continue;
            }

            for (var index = 0; index < EntityCapacity; index++) {
                active[index] = IsActive(index: index);
                addresses[index] = Address(index: index);
                previousPositions[index] = PreviousPosition(index: index);
                previousOrientations[index] = PreviousOrientation(index: index);
                currentPositions[index] = CurrentPosition(index: index);
                currentOrientations[index] = CurrentOrientation(index: index);
                colors[index] = BodyColor(index: index);
                looks[index] = Look(index: index);
                catalogRigs[index] = CatalogRig(index: index);
                colliders[index] = Collider(index: index);
                bodyContacts[index] = BodyContact(index: index);
            }

            tick = Tick;
            revision = SnapshotRevision;
            stepSeconds = StepSeconds;
            arrivalTimestamp = SnapshotArrivalTimestamp;
            if (sequence == Volatile.Read(ref m_snapshotSequence)) {
                return;
            }
        }
    }

    internal static float ResolveInterpolationAlpha(float stepSeconds, long arrivalTimestamp) {
        if (stepSeconds <= 0f) {
            return 1f;
        }

        var elapsedSeconds = (float)Stopwatch.GetElapsedTime(startingTimestamp: arrivalTimestamp).TotalSeconds;
        return Math.Clamp(value: (elapsedSeconds / stepSeconds), min: 0f, max: 1f);
    }

    private static FixedWorldCollider?[] CompileColliders(WorldDefinition definition) {
        var colliders = new FixedWorldCollider?[definition.Kits.Count];
        for (var index = 0; index < colliders.Length; index++) {
            colliders[index] = FixedWorldCollider.Compile(collider: definition.Kits[index].Collider, creations: definition.Creations);
        }
        return colliders;
    }

    private static WorldBodyContactMode[] CompileBodyContacts(WorldDefinition definition) =>
        definition.Kits.Select(selector: static kit => kit.BodyContact).ToArray();

    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) {
        // The mirror drives no console — nothing here ever queries the destination.
    }

    /// <inheritdoc/>
    public void DeliverComposition(WorldComposition composition) {
        // No window composer observes a destination through this seam.
    }

    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        // No presentation service of the OBSERVING world is reachable from inside a destination's own delivery.
    }
}

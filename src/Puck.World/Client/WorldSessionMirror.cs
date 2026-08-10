using System.Diagnostics;
using System.Numerics;
using Puck.Hosting;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The session projection's client-side mirror — the "minimal client-side pose/state mirror" docs/world-model.md's
/// "Observation and display" names, attached to a destination instance's <c>WorldServer</c> under an
/// <see cref="Server.WorldServer.AttachSink"/> lease exactly like any other client. It is deliberately not
/// <see cref="WorldClient"/>: that type also carries a <c>PlayerRoster</c>/seat table this observation-only mirror
/// has no use for (nobody is seated in a destination through this seam), grants/machine/action state it never
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
/// <para>Single-threaded like every other <see cref="IClientSink"/>: the destination's own fixed-step thread is the
/// only caller of <see cref="DeliverDefinition"/>/<see cref="DeliverSnapshot"/>, and the render thread that reads
/// <see cref="Definition"/>/<see cref="DefinitionRevision"/> is the same thread that later calls
/// <see cref="WorldSessionSceneEmitter.Emit"/>/<see cref="WorldSessionSceneEmitter.PackDynamicTransforms"/> against
/// it inside one produced host frame — exactly the invariant <see cref="WorldClient"/> already relies on.</para>
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
    private readonly bool[] m_active = new bool[EntityCapacity];
    private readonly bool[] m_seen = new bool[EntityCapacity];

    private WorldDefinition m_definition;
    private int m_definitionRevision;
    // The destination's own step duration in seconds, derived from the latest snapshot's StepTicks — the
    // presentation clock WorldSessionSceneEmitter normalizes real elapsed time against (see its own remarks on why
    // it cannot read a host alpha/delta for a session view).
    private float m_stepSeconds;
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

        for (var index = 0; (index < EntityCapacity); index++) {
            m_previousOrientation[index] = Quaternion.Identity;
            m_currentOrientation[index] = Quaternion.Identity;
        }
    }

    /// <summary>The destination's live world definition — the boot/attach definition until a later mutation batch or
    /// swap delivers a new one.</summary>
    public WorldDefinition Definition => m_definition;

    /// <summary>The monotonic definition-delivery counter — bumped each time the destination delivers a new
    /// definition, the rebuild-watch component <see cref="WorldSessionSceneEmitter"/> reads.</summary>
    public int DefinitionRevision => m_definitionRevision;

    /// <summary>The destination's latest completed simulation tick — read-back (<c>world.faces</c>'s session echo)
    /// AND the presentation clock <see cref="WorldSessionSceneEmitter"/> resolves its render alpha against (via
    /// <see cref="StepSeconds"/>/<see cref="SnapshotArrivalTimestamp"/>).</summary>
    public ulong Tick { get; private set; }

    /// <summary>The destination's own step width (engine ticks per its authored simulation step) at the latest
    /// delivered snapshot — the destination presentation clock docs/world-model.md's "Observation and display"
    /// names.</summary>
    public ulong StepTicks { get; private set; }

    /// <summary><see cref="StepTicks"/> converted to seconds — the denominator
    /// <see cref="WorldSessionSceneEmitter"/>'s self-derived render alpha divides real elapsed time by.</summary>
    public float StepSeconds => m_stepSeconds;

    /// <summary>The wall-clock timestamp (<see cref="Stopwatch.GetTimestamp"/> units) the current snapshot's poses
    /// were copied in at — the "render call's arrival" baseline <see cref="WorldSessionSceneEmitter"/> measures
    /// elapsed real time against, since a session view supplies neither a host delta nor a host alpha (see that
    /// type's own remarks).</summary>
    public long SnapshotArrivalTimestamp => m_snapshotArrivalTimestamp;

    /// <summary>The declared-set/palette revision from the latest snapshot — a separate rebuild-watch component from
    /// <see cref="DefinitionRevision"/> (never summed with it: this one is assigned from the wire and can move down,
    /// exactly like <see cref="WorldClient.WriteRevision"/>'s own server-revision component, for the identical
    /// reason).</summary>
    public int SnapshotRevision { get; private set; }

    /// <summary>Whether the entity at <paramref name="index"/> was active (drawn) in the latest snapshot.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_active[index];

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

    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_definition = definition;
        m_definitionRevision++;
    }

    /// <inheritdoc/>
    /// <remarks>Copies every kept field out of the borrowed <paramref name="snapshot"/> before returning (see this
    /// type's own borrowed-snapshot remarks). Continuity mirrors <see cref="WorldClient.DeliverSnapshot"/>'s
    /// snap-vs-interpolate split only — a newly active entity or a <see cref="EntityContinuityKind.Teleport"/> resets
    /// both interpolation endpoints to the fresh pose so the first/next frame never streaks; every other case
    /// (including <see cref="EntityContinuityKind.Correction"/>, which the boot client eases with a decaying render-
    /// error offset this mirror does not reproduce) shifts current into previous, ordinary double-buffering.</remarks>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Array.Clear(array: m_seen);

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            var index = entry.Index;

            if ((uint)index >= EntityCapacity) {
                continue;
            }

            m_seen[index] = true;
            m_bodyColor[index] = entry.BodyColor;
            m_look[index] = entry.Look;

            if (!m_active[index] || (entry.Continuity.Kind == EntityContinuityKind.Teleport)) {
                m_previousPosition[index] = entry.Position;
                m_previousOrientation[index] = entry.Orientation;
            } else {
                m_previousPosition[index] = m_currentPosition[index];
                m_previousOrientation[index] = m_currentOrientation[index];
            }

            m_currentPosition[index] = entry.Position;
            m_currentOrientation[index] = entry.Orientation;
            m_active[index] = true;
        }

        for (var index = 0; (index < EntityCapacity); index++) {
            if (!m_seen[index]) {
                m_active[index] = false;
            }
        }

        Tick = snapshot.Tick;
        StepTicks = snapshot.StepTicks;
        SnapshotRevision = snapshot.Revision;
        m_stepSeconds = (float)EngineTicks.ToSeconds(ticks: snapshot.StepTicks);
        m_snapshotArrivalTimestamp = Stopwatch.GetTimestamp();
    }

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

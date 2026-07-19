using System.Numerics;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The per-machine client half: consumes the server's per-tick <see cref="WorldSnapshot"/> into a double-buffered
/// entity view (previous/current pose per entity, colors, archetypes, per-entity correction easers), submits each
/// joined seat's device intent over the server link every tick, and resolves the per-frame render poses the frame
/// source and the SDF anchor consumers read (position <c>Lerp</c> + orientation shortest-path nlerp at the frame's
/// interpolation alpha, plus the eased correction offset). Poses flow IN via snapshots only; intents, commands, and
/// session requests flow OUT over the link.
/// </summary>
/// <remarks>Single-threaded on the launcher's window-pump thread: snapshots arrive synchronously inside the server
/// step, submissions run immediately before it, and the render-pose refresh runs during frame produce.</remarks>
internal sealed class WorldClient : IClientSink, ISdfAnchorSource {
    private readonly PlayerRoster m_roster;
    private readonly IServerLink m_link;
    // The double-buffered per-entity tick poses (the interpolation endpoints), the tick's palette/archetype image, and
    // the per-entity correction easers. Sized to the table ceiling; inactive slots are simply unseen.
    private readonly Vector3[] m_previousPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_previousOrientation = new Quaternion[EntityCapacity];
    private readonly Vector3[] m_currentPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_currentOrientation = new Quaternion[EntityCapacity];
    private readonly Vector3[] m_color = new Vector3[EntityCapacity];
    // The kit row index per entity — carried for kit-keyed render selection (today's rig visuals are index-keyed via
    // the avatar catalog, so nothing branches on it yet).
    private readonly byte[] m_kit = new byte[EntityCapacity];
    private readonly bool[] m_active = new bool[EntityCapacity];
    private readonly bool[] m_seen = new bool[EntityCapacity];
    private readonly RenderErrorEaser[] m_easers = new RenderErrorEaser[EntityCapacity];
    // The per-frame resolved render poses (alpha-interpolated + eased) — what the frame source and anchors read.
    private readonly Vector3[] m_renderPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_renderOrientation = new Quaternion[EntityCapacity];
    private int m_serverRevision;
    private int m_definitionRevision;
    private int m_activePeerCount;
    private ulong m_tick;
    // The server's live world definition — the boot definition at construction, replaced by DeliverDefinition after an
    // applied mutation batch or a swap. The frame source re-reads scene/screens from this behind the revision check.
    private WorldDefinition m_definition;
    // The shared live composition-override store — the frame source's composer reads it; DeliverComposition writes it.
    private readonly WorldCompositionState m_composition;

    /// <summary>The entity-view capacity — the server table's hard ceiling.</summary>
    public const int EntityCapacity = 128;

    /// <summary>Initializes a new instance of the <see cref="WorldClient"/> class over the seat table it submits for
    /// and the link it submits on.</summary>
    /// <param name="roster">The client seat table (device metadata, seat controllers, pending state).</param>
    /// <param name="link">The client→server link intents ride.</param>
    /// <param name="definition">The boot world definition — the initial live definition the frame source reads.</param>
    /// <param name="composition">The shared live composition-override store (also read by the frame source's composer);
    /// <see cref="DeliverComposition"/> applies accepted overrides into it.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldClient(PlayerRoster roster, IServerLink link, WorldDefinition definition, WorldCompositionState composition) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: composition);

        m_roster = roster;
        m_link = link;
        m_definition = definition;
        m_composition = composition;

        for (var index = 0; (index < EntityCapacity); index++) {
            m_previousOrientation[index] = Quaternion.Identity;
            m_currentOrientation[index] = Quaternion.Identity;
            m_renderOrientation[index] = Quaternion.Identity;
            m_easers[index].Reset();
        }
    }

    /// <summary>The client seat table.</summary>
    public PlayerRoster Roster => m_roster;

    /// <summary>The latest snapshot's tick.</summary>
    public ulong Tick => m_tick;

    /// <summary>The server's live world definition — the boot definition, then whatever the server last delivered after
    /// an applied mutation batch or swap. The frame source reads scene/screens from here on its next rebuild.</summary>
    public WorldDefinition Definition => m_definition;

    /// <summary>The monotonic definition-delivery counter — bumped each time the server delivers a new definition. The
    /// frame source watches it to know a scene/screen change landed (distinct from a population/roster change).</summary>
    public int DefinitionRevision => m_definitionRevision;

    /// <summary>The combined program-rebuild watch counter: the client's seat-metadata revision (colors, pending
    /// state), the server's declared-set revision from the latest snapshot, and the definition-delivery revision. All
    /// three are monotonic, so the sum only stalls when none has changed.</summary>
    public int Revision => (m_roster.Revision + m_serverRevision + m_definitionRevision);

    /// <summary>The number of active non-seat entities in the latest snapshot — the client's view of the simulated
    /// census (drives the fleet-tier auto quality levers).</summary>
    public int ActivePeerCount => m_activePeerCount;

    /// <summary>Whether the entity is drawn this frame (present in the latest snapshot).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_active[index];

    /// <summary>The entity's per-frame render position (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 Position(int index) => m_renderPosition[index];

    /// <summary>The entity's per-frame render attitude (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion Orientation(int index) => m_renderOrientation[index];

    /// <summary>The entity's render body color: a joined seat composes client-side (profile color with the
    /// pending-gray desaturation folded in); every other entity carries the snapshot's color.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 BodyColor(int index) {
        return (((index < PlayerRoster.MaxSlots) && m_roster.IsJoined(slot: index))
            ? m_roster.BodyColor(slot: index)
            : m_color[index]);
    }

    /// <summary>Submits each joined, active seat's device intent (and live-held lane image) for this tick — the
    /// client's per-tick outbound half, run immediately before the server step. A pending seat submits nothing (its
    /// inputs drive the profile picker, not locomotion), and a seat submits only under
    /// <see cref="IntentSource.Live"/> — off-Live the devices are inert and the server-side source fills the gaps.</summary>
    /// <param name="tick">The tick the submissions are for.</param>
    public void SubmitSeatIntents(ulong tick) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.IsPending(slot: slot) || (m_roster.Seat(slot: slot) is not { Source: IntentSource.Live } seat)) {
                continue;
            }

            m_link.SubmitIntent(submission: new IntentSubmission(
                Tick: tick,
                EntityIndex: slot,
                Intent: seat.HeldIntent(),
                Principal: WorldPrincipal.Seat(slot: slot),
                HeldLanes: seat.HeldLanes
            ));
        }
    }

    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Array.Clear(array: m_seen);

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            var index = entry.Index;

            m_seen[index] = true;
            m_color[index] = entry.BodyColor;
            m_kit[index] = entry.Kit;

            if (!m_active[index]) {
                // Newly active: both interpolation endpoints start at the spawn pose so the first frame never streaks.
                m_previousPosition[index] = entry.Position;
                m_previousOrientation[index] = entry.Orientation;
                m_easers[index].Reset();
            } else {
                switch (entry.Continuity.Kind) {
                    case EntityContinuityKind.Teleport:
                        // Never interpolate across the jump; any in-flight correction offset is dropped with it.
                        m_previousPosition[index] = entry.Position;
                        m_previousOrientation[index] = entry.Orientation;
                        m_easers[index].Reset();

                        break;
                    case EntityContinuityKind.Correction: {
                        // Authority snapped: ease the render error (last drawn tick pose minus authority) to zero over
                        // the window. Over-threshold corrections snap instead: the easer basis is the previous
                        // snapshot, which may lag same-tick multi-pose batches past the server's own snap-escape check.
                        var positionError = (m_currentPosition[index] - entry.Position);

                        if (positionError.Length() > EntityContinuity.MaxSmoothError) {
                            m_easers[index].Reset();
                        } else {
                            m_easers[index].Begin(
                                positionError: positionError,
                                orientationError: Quaternion.Multiply(
                                    value1: m_currentOrientation[index],
                                    value2: Quaternion.Conjugate(value: entry.Orientation)
                                ),
                                seconds: entry.Continuity.Seconds
                            );
                        }

                        m_previousPosition[index] = entry.Position;
                        m_previousOrientation[index] = entry.Orientation;

                        break;
                    }
                    default:
                        m_previousPosition[index] = m_currentPosition[index];
                        m_previousOrientation[index] = m_currentOrientation[index];

                        break;
                }
            }

            m_currentPosition[index] = entry.Position;
            m_currentOrientation[index] = entry.Orientation;
            m_active[index] = true;
        }

        var peers = 0;
        var stepSeconds = (float)EngineTicks.ToSeconds(ticks: snapshot.StepTicks);

        for (var index = 0; (index < EntityCapacity); index++) {
            if (!m_seen[index]) {
                m_active[index] = false;

                continue;
            }

            if (index >= PlayerRoster.MaxSlots) {
                peers++;
            }

            // Bleed the correction offsets by the sub-step delta (not the frame delta) — frame-rate independent.
            m_easers[index].Decay(deltaSeconds: stepSeconds);
        }

        m_activePeerCount = peers;
        m_serverRevision = snapshot.Revision;
        m_tick = snapshot.Tick;
    }

    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) {
        // Queries are synchronous over the loopback (the link returns the answer); nothing arrives on this lane yet.
    }

    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // Store the live definition and bump the delivery revision (folded into Revision), so the frame source rebuilds
        // its program and re-reads scene/screens on its next capture. Poses still flow only through snapshots.
        m_definition = definition;
        m_definitionRevision++;
    }

    /// <inheritdoc/>
    public void DeliverComposition(WorldComposition composition) {
        ArgumentNullException.ThrowIfNull(argument: composition);

        // Apply the accepted override into the shared store the composer reads next frame. A null name clears it (auto).
        switch (composition) {
            case WorldComposition.SetActiveLayout layout:
                m_composition.ActiveLayout = layout.Name;

                break;
            case WorldComposition.SelectCamera camera:
                m_composition.SelectedCamera = camera.Name;

                break;
        }
    }

    /// <summary>Resolves this frame's render pose for every active entity: position <c>Lerp(previous, current,
    /// alpha)</c>, orientation shortest-path nlerp, then the eased correction offset folded in. Called once per
    /// captured frame before anything reads <see cref="Position"/>/<see cref="Orientation"/>. On a frame that banked
    /// zero sub-steps previous == current, so both hold stably (no snap-back). Presentation only: <c>player.where</c>
    /// reports the server sim pose, never this.</summary>
    /// <param name="alpha">How far this frame sits between the last and next fixed sim step, in <c>[0, 1)</c>.</param>
    public void UpdateRenderPoses(float alpha) {
        for (var index = 0; (index < EntityCapacity); index++) {
            if (!m_active[index]) {
                continue;
            }

            var position = Vector3.Lerp(value1: m_previousPosition[index], value2: m_currentPosition[index], amount: alpha);
            // Quaternion.Lerp is the nlerp: shortest-path dot-sign flip and renormalize.
            var orientation = Quaternion.Lerp(quaternion1: m_previousOrientation[index], quaternion2: m_currentOrientation[index], amount: alpha);

            m_easers[index].Apply(position: ref position, orientation: ref orientation);
            m_renderPosition[index] = position;
            m_renderOrientation[index] = orientation;
        }
    }

    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        if (((uint)anchorId >= EntityCapacity) || !m_active[anchorId]) {
            anchor = default;

            return false;
        }

        anchor = new SdfAnchor(Position: m_renderPosition[anchorId], Orientation: m_renderOrientation[anchorId]);

        return true;
    }

    // The correction error-smoothing state, one per entity, with a Begin/Decay/Apply/Reset lifecycle. Presentation
    // only — the sim never reads it, and it is never part of the pose flowing out to player.where. A
    // default-constructed easer has a zero (non-identity) m_orientation, but Apply is guarded on m_remaining > 0 and
    // Begin always sets the orientation before arming it (and construction calls Reset), so the zero is never observed.
    private struct RenderErrorEaser {
        // The old-minus-new position delta, the world-space orientation error quaternion E = qOld·conj(qNew) that decays
        // to identity, and the total/remaining smoothing seconds.
        private Vector3 m_position;
        private Quaternion m_orientation;
        private float m_window;
        private float m_remaining;

        // Arm the easer with a fresh correction error over a smoothing window — the Correction continuity path.
        public void Begin(Vector3 positionError, Quaternion orientationError, float seconds) {
            m_position = positionError;
            m_orientation = orientationError;
            m_window = seconds;
            m_remaining = seconds;
        }

        // Drop any in-flight offset (a hard teleport) so it never drags the avatar off an authoritative jump.
        public void Reset() {
            m_position = default;
            m_orientation = Quaternion.Identity;
            m_window = 0f;
            m_remaining = 0f;
        }

        // Bleed the offset toward zero by the sub-step delta (frame-rate independent — same wall-clock window regardless
        // of how many sub-steps a frame banks).
        public void Decay(float deltaSeconds) {
            if (m_remaining > 0f) {
                m_remaining -= deltaSeconds;

                if (m_remaining < 0f) {
                    m_remaining = 0f;
                }
            }
        }

        // Fold the current (smoothstep-eased) offset into an interpolated render pose: the position error adds in and the
        // orientation error Slerp(identity, E, weight) is applied outermost (world space) to the attitude, so the
        // on-screen craft glides from where it was to authority. A no-op once the window drains.
        public readonly void Apply(ref Vector3 position, ref Quaternion orientation) {
            if ((m_remaining > 0f) && (m_window > 0f)) {
                // Smoothstep ease: weight is 1 at receipt (craft sits at its old pose) and eases to 0 as the window drains
                // (craft arrives at authority), with a soft start and a soft settle. fraction = remaining/window runs 1→0.
                var fraction = (m_remaining / m_window);
                var weight = ((fraction * fraction) * (3f - (2f * fraction)));

                position += (m_position * weight);
                // At weight 1 the render attitude is E · interpolated = the old attitude; at weight 0 it is the
                // interpolated (authoritative) attitude — the angular twin of the position offset's decay.
                orientation = (Quaternion.Slerp(quaternion1: Quaternion.Identity, quaternion2: m_orientation, amount: weight) * orientation);
            }
        }
    }
}

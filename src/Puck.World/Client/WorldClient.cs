using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>
/// The per-machine client half: consumes the server's per-tick <see cref="WorldSnapshot"/> into a double-buffered
/// entity view (previous/current pose per entity, colors, archetypes, per-entity correction easers), submits each
/// joined seat's device intent over the server link every tick, and resolves the per-frame render poses the frame
/// source and the SDF anchor consumers read (position <c>Lerp</c> + orientation shortest-path nlerp at the frame's
/// interpolation alpha, plus the eased correction offset). Poses flow in via snapshots only; intents, commands, and
/// session requests flow out over the link.
/// </summary>
/// <remarks>Single-threaded on the launcher's window-pump thread: snapshots arrive synchronously inside the server
/// step, submissions run immediately before it, and the render-pose refresh runs during frame produce.</remarks>
internal sealed class WorldClient : IClientSink, ISdfAnchorSource {
    private readonly PlayerRoster m_roster;
    private readonly IServerLink m_link;
    // The traveler-follow router (stage 1) — every seat submission below resolves its own current presenting
    // location through it: boot-bound (the ordinary default) or away, in a running WorldInstanceHost instance.
    // Presentation-side only, same as WorldPerceptionAnchor's own anchor table.
    private readonly WorldSeatInstanceRouter m_seatRouter;
    // The shared per-seat live orbit. Camera-relative movement reads only its already-integrated yaw while composing
    // a world-frame intent; the deterministic simulation still receives ordinary fixed-point role channels and has
    // no camera dependency of its own.
    // De-dups the away-claimed-seat stderr narration (see SubmitAwaySeatIntents) so a seat that stays claimed while
    // away does not spam one line per tick — cleared the moment that seat is no longer both claimed and away.
    private readonly bool[] m_awayClaimWarned = new bool[PlayerRoster.MaxSlots];
    // The accepted-lever applier (see WorldSessionLeverSink). Optional so a client composed without the presentation
    // services — a headless or test host — simply drops accepted levers rather than failing to construct.
    private WorldSessionLeverSink? m_levers;
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
    // The LOOK row index per entity — the frame source reads it to resolve each body's appearance (catalog rig vs.
    // creation stamp), scale, and gait amplitude. PRESENTATION-ONLY.
    private readonly byte[] m_look = new byte[EntityCapacity];
    private readonly string?[] m_placementId = new string?[EntityCapacity];
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
    private WorldChannelTable m_channels;
    private WorldTargetRegisterTable m_targets;
    // The shared live composition-override store — the frame source's composer reads it; DeliverComposition writes it.
    private readonly WorldCompositionState m_composition;

    /// <summary>The entity-view capacity — single-sourced from <see cref="WorldPopulationLimits.CapacityCeiling"/>
    /// so the validator's admitted population.capacity and this client's fixed
    /// per-entity arrays can never again drift apart: an over-capacity document refuses at load instead of booting
    /// into a latent out-of-bounds throw here.</summary>
    public const int EntityCapacity = WorldPopulationLimits.CapacityCeiling;

    /// <summary>Initializes a new instance of the <see cref="WorldClient"/> class over the seat table it submits for
    /// and the link it submits on.</summary>
    /// <param name="roster">The client seat table (device metadata, seat controllers, pending state).</param>
    /// <param name="link">The client→server link intents ride.</param>
    /// <param name="definition">The boot world definition — the initial live definition the frame source reads.</param>
    /// <param name="composition">The shared live composition-override store (also read by the frame source's composer);
    /// <see cref="DeliverComposition"/> applies accepted overrides into it.</param>
    /// <param name="seatRouter">The traveler-follow router (stage 1) — <see cref="SubmitSeatIntents"/> and
    /// <see cref="SubmitAwaySeatIntents"/> both resolve a seat's current presenting location through it.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldClient(PlayerRoster roster, IServerLink link, WorldDefinition definition, WorldCompositionState composition, WorldSeatInstanceRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: composition);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);

        m_roster = roster;
        m_link = link;
        m_seatRouter = seatRouter;
        m_definition = definition;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_targets = WorldTargetRegisterTable.Compile(registers: definition.TargetRegisters, channelCount: m_channels.ChannelCount);
        m_composition = composition;
        m_levers = null;

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

    /// <summary>How many counters <see cref="WriteRevision"/> reports — the component count
    /// <see cref="WorldSceneEmitter"/> folds into its own when it lays out its revision vector.</summary>
    public const int RevisionComponentCount = 3;

    /// <summary>Writes this client's program-rebuild watch counters side by side, never added together: the seat-metadata
    /// revision (colors, pending state), the server's declared-set revision from the latest snapshot, and the
    /// definition-delivery revision.
    /// <para>
    /// The server term is not monotonic — it is assigned from <c>snapshot.Revision</c>, so it can move down. A sum of
    /// these three would therefore stall on a real change whenever the server revision fell by exactly as much as
    /// another term rose, holding a stale program with nothing to report it. Keeping them apart lets the composition
    /// host's componentwise compare see each one move on its own (see <see cref="ISdfSceneEmitter.WriteRevision"/>).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_roster.Revision;
        destination[1] = m_serverRevision;
        destination[2] = m_definitionRevision;
    }

    /// <summary>The number of active non-seat entities in the latest snapshot — the client's view of the simulated
    /// census (drives the fleet-tier auto quality levers).</summary>
    public int ActivePeerCount => m_activePeerCount;

    /// <summary>Whether the entity is drawn this frame (present in the latest snapshot).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_active[index];

    /// <summary>The entity's resolved look row index from the latest snapshot — the frame source's appearance selector
    /// (presentation-only).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public byte LookIndex(int index) => m_look[index];

    /// <summary>The look row an entity wears: the delivered look table indexed by <see cref="LookIndex"/>, or the
    /// implicit single catalog look when the world authors no <c>looks</c> section, and for an index the delivered
    /// table cannot cover. The scene emitter and the part resolver read appearance through this one resolve, so a
    /// stamped part and the body it hangs off can never disagree about which row they are wearing.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The entity's look row.</returns>
    public WorldLook Look(int index) {
        var rows = Definition.Looks;

        if (rows.Count == 0) {
            return WorldLook.Implicit;
        }

        var lookIndex = LookIndex(index: index);

        return ((lookIndex < rows.Count) ? rows[lookIndex] : WorldLook.Implicit);
    }

    /// <summary>The placement row this entity inhabits, or <see langword="null"/> for a seat/peer — the frame source
    /// renders an inhabitant's creation geometry (a body-rooted stamp) instead of a catalog avatar.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public string? PlacementId(int index) => m_placementId[index];

    /// <summary>Resolves the active entity index a placement's first inhabited body occupies (the audio anchor / stamp
    /// pose lookup), or <see langword="false"/> when no active entity inhabits it.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The resolved 0-based entity index.</param>
    public bool TryInhabitantBody(string placementId, out int index) {
        for (var candidate = 0; (candidate < EntityCapacity); candidate++) {
            if (m_active[candidate] && string.Equals(a: m_placementId[candidate], b: placementId, comparisonType: StringComparison.Ordinal)) {
                index = candidate;

                return true;
            }
        }

        index = -1;

        return false;
    }

    /// <summary>The entity's per-frame render position (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 Position(int index) => m_renderPosition[index];

    /// <summary>The entity's per-frame render attitude (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion Orientation(int index) => m_renderOrientation[index];

    /// <summary>Resolves the nearest snapshot subject inside a source body's clamped designation cone. This is a
    /// proposal only; the server re-resolves the returned subject and owns the register write.</summary>
    public bool TryFindDesignationSubject(int sourceBody, string registerName, out GrantSubject subject) {
        subject = default;

        if (!IsActive(index: sourceBody) || !m_targets.TryGetIndex(name: registerName, index: out var registerIndex)) {
            return false;
        }

        var register = m_definition.TargetRegisters[registerIndex];
        var halfAngle = register.MaximumHalfAngleDegrees;
        var range = FixedQ4816.FromDouble(value: register.MaximumRange);
        var minimumDot = FixedQ4816.FromDouble(value: Math.Cos(d: (halfAngle * (Math.PI / 180.0))));
        var origin = FixedVector3.FromVector3(value: m_currentPosition[sourceBody]);
        var forward = FixedVector3.FromVector3(value: Vector3.Transform(value: -Vector3.UnitZ, rotation: m_currentOrientation[sourceBody]));
        var nearest = FixedQ4816.MaxValue;
        var found = -1;

        for (var index = 0; (index < EntityCapacity); index++) {
            if ((index == sourceBody) || !m_active[index]) {
                continue;
            }

            var candidate = FixedVector3.FromVector3(value: m_currentPosition[index]);

            if (BodyTargetConeSense.Contains(origin: in origin, forward: in forward, candidate: in candidate, range: range, minimumDot: minimumDot, distanceSquared: out var squared)
                && (squared < nearest)) {
                nearest = squared;
                found = index;
            }
        }

        if (found < 0) {
            return false;
        }

        subject = GrantSubject.Body(index: found);
        return true;
    }

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
    /// <see cref="IntentSource.Live"/> — off-Live the devices are inert and the server-side source fills the gaps.
    /// The submission's acting identity is <see cref="PlayerRoster.PrincipalOf"/> — the slot's own
    /// <see cref="WorldPrincipal.Seat"/> ordinarily, or whatever identity a <see cref="PlayerRoster.TryClaimSlot"/> call
    /// overrode it to (e.g. a replay device's) — so a claimed slot's submission is checked under its own principal, never
    /// silently promoted to the seat's. The submission's target is <see cref="PlayerRoster.DriveTarget"/> — the slot
    /// itself for an ordinary unclaimed seat, or a claimed slot's principal's own granted body (or
    /// <see cref="PlayerRoster.NoBody"/> when the claimant has never named one) — so a claimed slot's roster index
    /// never decides which body moves, not even as a fallback: the server's Drive check on
    /// <see cref="IntentSubmission.Principal"/> against <see cref="IntentSubmission.EntityIndex"/> is what actually
    /// decides whether the submission moves anything, and it never sees the slot at all for a claim that resolved no
    /// body.
    /// <para>Every live slot's target is resolved first, in one pass, before anything is sent. An unclaimed seat whose
    /// held intent is empty and held channels are all zero is background plumbing: when a different,
    /// claimed slot's grant-resolved target names that exact body this tick, the empty submission yields and only the
    /// claimed submission is sent. An input-producing unclaimed seat is a co-driver, not background plumbing, so both
    /// submissions go out. The server's existing per-body contention reporter then makes that different-principal
    /// collision loud and attributed, exactly as it does when two claimed slots resolve to one body; no winner is
    /// selected here.</para></summary>
    /// <param name="tick">The tick the submissions are for.</param>
    /// <remarks><b>Traveler-follow stage 1.</b> Scoped to boot-bound seats only — a seat
    /// <see cref="WorldSeatInstanceRouter"/> currently routes away is skipped here entirely (its device intent
    /// submits instead through <see cref="SubmitAwaySeatIntents"/>, called from
    /// <see cref="WorldInstanceHost.StepInstancesBesideBoot"/> at that instance's own next-tick coordinate). The rest
    /// of this method — target resolution, the co-driver/background-plumbing yield, the submission itself — is
    /// unchanged: a boot-bound seat's own path is exactly what it was before this router existed.</remarks>
    public void SubmitSeatIntents(ulong tick) {
        Span<bool> live = stackalloc bool[PlayerRoster.MaxSlots];
        Span<int> targets = stackalloc int[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.IsPending(slot: slot) || (m_roster.Seat(slot: slot) is not { } seat) || !seat.Source.IsLive) {
                continue;
            }

            if (!string.Equals(a: m_seatRouter.Location(slot: slot).InstanceName, b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            live[slot] = true;
            targets[slot] = m_roster.DriveTarget(slot: slot);
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!live[slot]) {
                continue;
            }

            var seat = m_roster.Seat(slot: slot)!;
            var definition = m_definition;
            var intent = ComposeMoveFrame(
                slot: slot,
                bodyIndex: targets[slot],
                definition: definition,
                intent: seat.HeldIntent()
            );
            var heldChannels = seat.HeldChannels;

            if (!m_roster.IsClaimed(slot: slot)
                && (intent == default)
                && (heldChannels == default)
                && ClaimedElsewhereTargets(live: live, targets: targets, body: targets[slot], exceptSlot: slot)) {
                continue;
            }

            m_link.SubmitIntent(submission: new IntentSubmission(
                Tick: tick,
                EntityIndex: targets[slot],
                Intent: intent,
                Principal: m_roster.PrincipalOf(slot: slot),
                HeldChannels: heldChannels
            ));
        }
    }

    /// <summary>Submits every seat <see cref="WorldSeatInstanceRouter"/> currently routes to
    /// <paramref name="instanceName"/> — <see cref="WorldInstanceHost.StepInstancesBesideBoot"/>'s own door, called
    /// immediately before that instance's <c>Server.Step</c>, at that instance's own next-tick coordinate. Mirrors
    /// <see cref="SubmitSeatIntents"/>'s boot-bound path with one deliberate stage-1 narrowing: the entity index is
    /// always the router's own <see cref="SeatLocation.InstanceSlot"/> (never a claim-resolved target — a claimed
    /// seat away from boot is refused below, since claim resolution reads boot's own grant table, which a
    /// cross-instance claim has no way to honor yet). Absolute-orbit world-frame movement composes from the routed
    /// definition and the seat's shared live orbit, so it remains camera-relative across a crossing without reading
    /// boot-scoped body poses. Every kit that stays on the default heading frame is unaffected.</summary>
    /// <param name="instanceName">The instance about to step.</param>
    /// <param name="tick">The tick the submissions are for — that instance's own next tick.</param>
    /// <param name="link">That instance's own transport (<see cref="WorldInstanceHost.TryGetLink"/>).</param>
    /// <param name="definition">That instance's current definition.</param>
    public void SubmitAwaySeatIntents(string instanceName, ulong tick, IServerLink link, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: link);
        ArgumentNullException.ThrowIfNull(argument: definition);

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.IsPending(slot: slot) || (m_roster.Seat(slot: slot) is not { } seat) || !seat.Source.IsLive) {
                continue;
            }

            var location = m_seatRouter.Location(slot: slot);

            if (!string.Equals(a: location.InstanceName, b: instanceName, comparisonType: StringComparison.Ordinal)) {
                continue;
            }

            // V1 RESTRICTION: a claimed (co-drive) seat away from boot is refused, not silently wrong — claim
            // resolution (PlayerRoster.DriveTarget) reads BOOT's own grant table over the boot loopback view, which
            // has no way to answer for a body that is not even in boot's population any more. Narrated once per
            // (slot, instance) rather than every tick.
            if (m_roster.IsClaimed(slot: slot)) {
                if (!m_awayClaimWarned[slot]) {
                    m_awayClaimWarned[slot] = true;

                    Console.Error.WriteLine(value: $"[world.view: seat {(slot + 1)} is claimed (co-drive) and away in '{instanceName}' — cross-instance claim resolution is a stage-1 residue; its submission is refused until it returns to boot or is reclaimed there]");
                }

                continue;
            }

            m_awayClaimWarned[slot] = false;

            var preference = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
            var look = seat.AnalogLook;
            seat.View.Nudge(
                input: new Vector2(x: (float)(double)look.X, y: (float)(double)look.Y),
                yawScale: preference.StickLookRate / Math.Max(1, definition.SimulationRateHz),
                pitchScale: preference.StickLookRate / Math.Max(1, definition.SimulationRateHz),
                preference: preference,
                control: definition.Views.SeatControl
            );

            var intent = ComposeMoveFrame(
                slot: slot,
                bodyIndex: location.InstanceSlot,
                definition: definition,
                intent: seat.HeldIntent(),
                permitBodyRelative: false
            );

            link.SubmitIntent(submission: new IntentSubmission(
                Tick: tick,
                EntityIndex: location.InstanceSlot,
                Intent: intent,
                Principal: m_roster.PrincipalOf(slot: slot),
                HeldChannels: seat.HeldChannels
            ));
        }
    }

    /// <summary>The camera-relative move composition — the determinism seam <c>WorldMotionModel</c>'s
    /// <c>MoveFrame</c> remarks (both arms) promise: when the world's seat kit (<see cref="WorldDefinition.DefaultSeatKit"/>) opts into
    /// <see cref="MotionMoveFrame.World"/>, rotates the raw analog <c>MoveForward</c>/<c>MoveStrafe</c> pair by the
    /// seat's currently rendered camera yaw before it ever reaches the wire — the sim itself only ever reads an
    /// already-world-frame vector, never a camera pose. An absolute-orbit seat composes authored orbit yaw plus its
    /// live right-stick/pointer yaw; an explicitly body-relative seat includes rendered body yaw. A no-op for
    /// every kit that stays on the default
    /// <see cref="MotionMoveFrame.Heading"/> frame (tank controls) — every world that never opts in submits the raw
    /// pair unchanged, byte-identical to before this composition existed.</summary>
    /// <param name="slot">The 0-based local seat whose camera frames the movement.</param>
    /// <param name="bodyIndex">The 0-based entity index this submission will drive.</param>
    /// <param name="definition">The definition of the instance receiving the intent.</param>
    /// <param name="intent">The seat's raw held intent.</param>
    /// <param name="permitBodyRelative">Whether this caller can resolve <paramref name="bodyIndex"/>'s rendered
    /// orientation. Away-instance submissions set this false; absolute-orbit composition needs no body pose.</param>
    /// <returns><paramref name="intent"/> with <c>MoveForward</c>/<c>MoveStrafe</c> rotated into world axes, or
    /// <paramref name="intent"/> unchanged when the seat kit is not camera-relative, the body is inactive, or the
    /// commanded vector is already zero (rotating zero is zero — skipped purely to avoid the trig).</returns>
    private PlayerIntent ComposeMoveFrame(int slot, int bodyIndex, WorldDefinition definition, PlayerIntent intent, bool permitBodyRelative = true) {
        var channels = (ReferenceEquals(objA: definition, objB: m_definition)
            ? m_channels
            : WorldChannelTable.Compile(channels: definition.Channels));
        var roles = channels.RoleOrdinals;
        var rawForwardValue = roles.Read(intent: in intent, role: ChannelRole.MoveForward);
        var rawStrafeValue = roles.Read(intent: in intent, role: ChannelRole.MoveStrafe);

        if (((rawForwardValue == FixedQ4816.Zero) && (rawStrafeValue == FixedQ4816.Zero))
            || (ResolveSeatKit(definition: definition) is not { } kit)
            || (kit.Motion.DeclaredMoveFrame != MotionMoveFrame.World)) {
            return intent;
        }

        if ((definition.Views.SeatControl.YawReference == WorldSeatYawReference.Body)
            && (!permitBodyRelative || !IsActive(index: bodyIndex))) {
            return intent;
        }

        var orientation = ((definition.Views.SeatControl.YawReference == WorldSeatYawReference.Body)
            ? Orientation(index: bodyIndex)
            : Quaternion.Identity);
        var yaw = (m_roster.Seat(slot: slot)?.View.LogicalYaw(views: definition.Views, bodyOrientation: orientation) ?? 0f);
        var sin = MathF.Sin(x: yaw);
        var cos = MathF.Cos(x: yaw);
        var facing = new Vector3(x: -sin, y: 0f, z: -cos);
        var length = 1f;
        var rawForward = (float)(double)rawForwardValue;
        var rawStrafe = (float)(double)rawStrafeValue;

        // The swim arm's second composition: the aim's ELEVATION splits the commanded forward into a planar part and
        // a vertical (MoveUp) contribution, so aim-directed diving reaches the sim as ordinary role channels — the
        // pitch never does. Under the default chase rig the rendered facing carries no elevation (a swim body's
        // attitude is pure yaw), so this composes to identity until a pitched aim source exists; the seam is here so
        // that day is a rig change, not a client rewrite.
        if (kit.Motion is WorldMotionModel.Swim) {
            var elevation = facing.Y;

            if (MathF.Abs(x: elevation) >= 1e-6f) {
                var rawUp = (float)(double)roles.Read(intent: in intent, role: ChannelRole.MoveUp);
                var composedUp = (rawUp + (rawForward * elevation));

                rawForward *= length;
                intent = roles.Write(intent: intent, role: ChannelRole.MoveUp, value: FixedQ4816.Clamp(value: FixedQ4816.FromDouble(value: composedUp), minimum: -FixedQ4816.One, maximum: FixedQ4816.One));
            }
        }

        // The inverse of WorldBody's world-frame read (planarTarget = (MoveStrafe, -MoveForward) in world X/Z):
        // rotate (rawForward, rawStrafe) — the camera-relative pair the seat's own devices staged — by the camera
        // yaw into that same world-frame pair.
        var moveForward = ((rawForward * cos) + (rawStrafe * sin));
        var moveStrafe = ((rawStrafe * cos) - (rawForward * sin));
        var negativeOne = -FixedQ4816.One;

        intent = roles.Write(intent: intent, role: ChannelRole.MoveForward, value: FixedQ4816.Clamp(value: FixedQ4816.FromDouble(value: moveForward), minimum: negativeOne, maximum: FixedQ4816.One));

        return roles.Write(intent: intent, role: ChannelRole.MoveStrafe, value: FixedQ4816.Clamp(value: FixedQ4816.FromDouble(value: moveStrafe), minimum: negativeOne, maximum: FixedQ4816.One));
    }

    // Resolves the world's designated seat kit row (every local seat shares this one kit) — a small linear scan over
    // the document's own (small) kit list, cheap enough to repeat per live seat per tick rather than caching a
    // pointer that would need its own revision-tracked invalidation.
    /// <summary>Consumes each seat's right-stick sample into its seat-owned view state exactly once this tick.</summary>
    public void AdvanceSeatViews(float deltaSeconds) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is not { } seat) {
                continue;
            }

            var definition = ((string.Equals(a: m_seatRouter.Location(slot: slot).InstanceName,
                b: WorldInstanceHost.BootInstanceName, comparisonType: StringComparison.Ordinal))
                ? m_definition
                : null);
            // The instance host owns away definitions; callers already integrate at the host tick. A routed away
            // seat is advanced by its emitter/pointer path until the host exposes that definition here.
            if (definition is null) {
                continue;
            }

            var preference = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
            var look = seat.AnalogLook;
            seat.View.Nudge(
                input: new Vector2(x: (float)(double)look.X, y: (float)(double)look.Y),
                yawScale: preference.StickLookRate * deltaSeconds,
                pitchScale: preference.StickLookRate * deltaSeconds,
                preference: preference,
                control: definition.Views.SeatControl
            );
        }
    }

    private static WorldKit? ResolveSeatKit(WorldDefinition definition) {
        var kits = definition.Kits;

        for (var index = 0; (index < kits.Count); index++) {
            if (string.Equals(a: kits[index].Name, b: definition.DefaultSeatKit, comparisonType: StringComparison.Ordinal)) {
                return kits[index];
            }
        }

        return null;
    }

    /// <summary>Whether another live, claimed slot resolves to <paramref name="body"/> this tick. Used only after
    /// <see cref="SubmitSeatIntents"/> has established that the unclaimed seat's own submission carries no input, so
    /// only background plumbing yields to the deliberate claim.</summary>
    /// <param name="live">Which roster slots are live for this tick.</param>
    /// <param name="targets">Each live slot's resolved drive target.</param>
    /// <param name="body">The unclaimed seat's target body.</param>
    /// <param name="exceptSlot">The unclaimed seat to exclude from the search.</param>
    /// <returns><see langword="true"/> when another live, claimed slot targets the same body; otherwise
    /// <see langword="false"/>.</returns>
    private bool ClaimedElsewhereTargets(ReadOnlySpan<bool> live, ReadOnlySpan<int> targets, int body, int exceptSlot) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if ((slot != exceptSlot) && live[slot] && (targets[slot] == body) && m_roster.IsClaimed(slot: slot)) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Array.Clear(array: m_seen);

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            var index = entry.Index;

            m_seen[index] = true;
            m_color[index] = entry.BodyColor;
            m_kit[index] = entry.Kit;
            m_look[index] = entry.Look;
            m_placementId[index] = entry.PlacementId;

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

                            if (positionError.Length() > m_definition.Motion.MaxSmoothError) {
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
                m_placementId[index] = null;

                continue;
            }

            if (index >= PlayerRoster.MaxSlots) {
                peers++;
            }

            // Bleed the correction offsets by the sub-step delta (not the frame delta) — frame-rate independent.
            m_easers[index].Decay(deltaSeconds: stepSeconds);
        }

        m_activePeerCount = peers;
        // ASSIGNED, never incremented — this mirrors the server's own declared-set revision, so it can move DOWN as
        // well as up. That is why WriteRevision reports it as its own component instead of adding it to anything.
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

        // Store the live definition and bump the delivery revision (one component of WriteRevision), so the frame source rebuilds
        // its program and re-reads scene/screens on its next capture. Poses still flow only through snapshots.
        m_definition = definition;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_targets = WorldTargetRegisterTable.Compile(registers: definition.TargetRegisters, channelCount: m_channels.ChannelCount);
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

    /// <summary>Attaches the accepted-lever applier. Composition-root wiring, done after construction because the
    /// presentation services it writes are themselves built around the client.</summary>
    /// <param name="levers">The applier accepted levers dispatch through.</param>
    public void AttachSessionLevers(WorldSessionLeverSink levers) {
        ArgumentNullException.ThrowIfNull(argument: levers);

        m_levers = levers;
    }

    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        // Already past the server's Mutate check on the lever's folded-into section (see WorldServer.ApplySessionLever),
        // so this only dispatches the write onto the presentation service the lever names.
        m_levers?.Apply(lever: lever);
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

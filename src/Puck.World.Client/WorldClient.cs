using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.SdfVm;
using Puck.World.Protocol;
using Puck.Physics.Motion;

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
public sealed class WorldClient : IClientSink, ISdfAnchorSource {
    /// <summary>The entity-view capacity — single-sourced from <see cref="WorldPopulationLimits.CapacityCeiling"/>
    /// so the validator's admitted population.capacity and this client's fixed
    /// per-entity arrays can never again drift apart: an over-capacity document refuses at load instead of booting
    /// into a latent out-of-bounds throw here.</summary>
    public const int EntityCapacity = WorldPopulationLimits.CapacityCeiling;
    /// <summary>How many counters <see cref="WriteRevision"/> reports — the component count
    /// <c>WorldSceneEmitter</c> folds into its own when it lays out its revision vector.</summary>
    public const int RevisionComponentCount = 3;

    // The shared live composition-override store — the frame source's composer reads it; DeliverComposition writes it.
    private readonly WorldCompositionState m_composition;
    private readonly PlayerRoster m_roster;
    // The authority claim is the sole source for both the entity an input drives and the endpoint that accepts it.
    // There is no boot/away distinction in this client.
    private readonly WorldSeatAuthorityRouter m_seatRouter;

    private int m_activePeerCount;
    private WorldChannelTable m_channels;
    // The server's live world definition — the boot definition at construction, replaced by DeliverDefinition after an
    // applied mutation batch or a swap. The frame source re-reads scene/screens from this behind the revision check.
    private WorldDefinition m_definition;
    private WorldClientFieldLattice? m_fields;

    /// <summary>Gets the mirror of the authority's field lattice, or <see langword="null"/> for a world without a
    /// <c>fields</c> section.</summary>
    public WorldClientFieldLattice? Fields => m_fields;
    private int m_definitionRevision;
    // The accepted-lever applier (see WorldSessionLeverSink). Optional so a client composed without the presentation
    // services — a headless or test host — simply drops accepted levers rather than failing to construct.
    private WorldSessionLeverSink? m_levers;
    private int m_serverRevision;
    private WorldTargetRegisterTable m_targets;
    private ulong m_tick;

    // The shared per-seat live orbit. Camera-relative movement reads only its already-integrated yaw while composing
    // a world-frame intent; the deterministic simulation still receives ordinary fixed-point role channels and has
    // no camera dependency of its own.
    private readonly ulong[] m_lastSubmissionEpoch = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_lastSubmissionTick = new ulong[PlayerRoster.MaxSlots];
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
    // The occupant-owned procedural rig. This is deliberately distinct from the authority-local entity index: a
    // seamless transfer may move the occupant to another slot without changing its implicit shape.
    private readonly byte[] m_catalogRig = new byte[EntityCapacity];
    private readonly int[] m_generation = new int[EntityCapacity];
    private readonly string?[] m_placementId = new string?[EntityCapacity];
    private readonly bool[] m_active = new bool[EntityCapacity];
    private readonly bool[] m_seen = new bool[EntityCapacity];
    private readonly RenderErrorEaser[] m_easers = new RenderErrorEaser[EntityCapacity];
    // Bumped on activation, a Teleport continuity, and an over-threshold Correction snap — every discontinuity a
    // presentation-side follower (a root/part second-order lag) must reseed across rather than chase.
    private readonly int[] m_poseEpoch = new int[EntityCapacity];
    // The per-frame resolved render poses (alpha-interpolated + eased) — what the frame source and anchors read.
    private readonly Vector3[] m_renderPosition = new Vector3[EntityCapacity];
    private readonly Quaternion[] m_renderOrientation = new Quaternion[EntityCapacity];
    private string m_authority = string.Empty;

    /// <summary>Initializes a new instance of the <see cref="WorldClient"/> class over the seat table it submits for
    /// through the authority table.</summary>
    /// <param name="roster">The client seat table (device metadata, seat controllers, pending state).</param>
    /// <param name="definition">The boot world definition — the initial live definition the frame source reads.</param>
    /// <param name="composition">The shared live composition-override store (also read by the frame source's composer);
    /// <see cref="DeliverComposition"/> applies accepted overrides into it.</param>
    /// <param name="seatRouter">The CAS-published authority table every seat-facing consumer shares.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldClient(PlayerRoster roster, WorldDefinition definition, WorldCompositionState composition, WorldSeatAuthorityRouter seatRouter) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: composition);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);

        m_roster = roster;
        m_seatRouter = seatRouter;
        m_definition = definition;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_targets = WorldTargetRegisterTable.Compile(
            registers: definition.TargetRegisters,
            channelCount: m_channels.ChannelCount
        );
        m_composition = composition;
        m_levers = null;

        for (var index = 0; (index < EntityCapacity); index++) {
            m_previousOrientation[index] = Quaternion.Identity;
            m_currentOrientation[index] = Quaternion.Identity;
            m_renderOrientation[index] = Quaternion.Identity;
            m_easers[index].Reset();
        }
    }

    /// <summary>The number of active non-seat entities in the latest snapshot — the client's view of the simulated
    /// census (drives the fleet-tier auto quality levers).</summary>
    public int ActivePeerCount => m_activePeerCount;
    /// <summary>The authority stamped on the latest delivered entity image.</summary>
    public string Authority => m_authority;
    /// <summary>The server's live world definition — the boot definition, then whatever the server last delivered after
    /// an applied mutation batch or swap. The frame source reads scene/screens from here on its next rebuild.</summary>
    public WorldDefinition Definition => m_definition;
    /// <summary>The monotonic definition-delivery counter — bumped each time the server delivers a new definition. The
    /// frame source watches it to know a scene/screen change landed (distinct from a population/roster change).</summary>
    public int DefinitionRevision => m_definitionRevision;
    /// <summary>The client seat table.</summary>
    public PlayerRoster Roster => m_roster;
    /// <summary>The latest snapshot's tick.</summary>
    public ulong Tick => m_tick;

    /// <summary>Whether another live, claimed slot resolves to <paramref name="body"/> this tick. Used only after
    /// <see cref="SubmitAuthorityIntents"/> has established that the unclaimed seat's own submission carries no input, so
    /// only background plumbing yields to the deliberate claim.</summary>
    /// <param name="live">Which roster slots are live for this tick.</param>
    /// <param name="targets">Each live slot's resolved drive target.</param>
    /// <param name="body">The unclaimed seat's target body.</param>
    /// <param name="exceptSlot">The unclaimed seat to exclude from the search.</param>
    /// <returns><see langword="true"/> when another live, claimed slot targets the same body; otherwise
    /// <see langword="false"/>.</returns>
    private bool ClaimedElsewhereTargets(ReadOnlySpan<bool> live, ReadOnlySpan<int> targets, int body, int exceptSlot) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                (slot != exceptSlot) &&
                live[slot] &&
                (targets[slot] == body) &&
                m_roster.IsClaimed(slot: slot)
            ) {
                return true;
            }
        }

        return false;
    }
    /// <summary>The move composition — the determinism seam <c>WorldMotionModel</c>'s <c>MoveFrame</c> remarks
    /// promise: every movement contribution is rotated into WORLD axes here, before it reaches the wire, so the sim
    /// only ever reads a world-frame vector and never a camera pose. Two producers fold: the seat's held channel
    /// rows (already summed by <see cref="SeatController.HeldIntent"/>), rotated by the frame the world declared on
    /// its MoveAdvance/MoveStrafe pair (<see cref="WorldChannelTable.MoveFrame"/> — <see cref="ChannelFrame.Camera"/>
    /// by the camera yaw, <see cref="ChannelFrame.Heading"/> by the body's authoritative HEADING off the wire
    /// (<see cref="WorldAuthorityEndpoint.TryEntityHeading"/> — never the drawn attitude, which the sim's facing snap
    /// angles toward the travel), <see cref="ChannelFrame.World"/> not at all), and the stick's <c>player.move</c> or
    /// <c>player.move.strafe</c> sample, camera-framed by those verbs' definition. A movement-facing stick or
    /// camera-framed channel row latches its camera yaw when movement begins (<see cref="SeatController.CameraFrameYaw"/>),
    /// while action-strafe deliberately follows the live look yaw every tick so holding forward and turning bends
    /// the trajectory with the character. Held free look temporarily resolves that stick against authoritative body
    /// heading instead, fully decoupling camera orbit from locomotion. Each pair is clamped to the
    /// unit disc before its rotation (two full keys are one direction at full speed), the two are summed and clamped
    /// once per axis (the fold's rule). A movement-facing camera contribution also turns the body's HEADING to the way
    /// it moves: <c>player.move</c> writes its world direction to FaceX/FaceZ, while <c>player.move.strafe</c> preserves
    /// heading; camera-framed channel rows retain their authored movement-facing behavior;
    /// <c>player.steer</c> or <c>player.look.steer</c> composes after this and wins. Under a kit on the sim's own
    /// <see cref="MotionMoveFrame.Heading"/> arm nothing rotates (the validator keeps such a world's pair
    /// World-framed) — the stick folds in raw beside the rows.</summary>
    /// <param name="slot">The 0-based local seat whose camera and body frame the movement.</param>
    /// <param name="bodyIndex">The 0-based entity index this submission will drive.</param>
    /// <param name="endpoint">The authority that owns the body and supplies its current orientation.</param>
    /// <param name="definition">The definition of the instance receiving the intent.</param>
    /// <param name="intent">The seat's held-row intent (no stick sample in it).</param>
    /// <returns><paramref name="intent"/> with <c>MoveAdvance</c>/<c>MoveStrafe</c> (and, for a camera-framed
    /// movement-facing contribution, <c>FaceX</c>/<c>FaceZ</c>) composed into world axes; unchanged when nothing moves or the body
    /// pose the frame needs is unavailable.</returns>
    private PlayerIntent ComposeMoveFrame(int slot, int bodyIndex, WorldAuthorityEndpoint endpoint, WorldDefinition definition, PlayerIntent intent) {
        if (m_roster.Seat(slot: slot) is not { } seat) {
            return intent;
        }

        var channels = (ReferenceEquals(
            objA: definition,
            objB: m_definition
        )
            ? m_channels
            : WorldChannelTable.Compile(channels: definition.Channels)
        );
        var roles = channels.RoleOrdinals;
        var rowForward = ((float)((double)roles.Read(
            intent: in intent,
            role: ChannelRole.MoveAdvance
        )));
        var rowStrafe = ((float)((double)roles.Read(
            intent: in intent,
            role: ChannelRole.MoveStrafe
        )));
        var move = seat.Move;
        var stick = move.Value;
        var stickForward = ((float)((double)stick.Y));
        var stickStrafe = ((float)((double)stick.X));
        var rowsMove = ((rowForward != 0f) || (rowStrafe != 0f));
        var stickMoves = ((stickForward != 0f) || (stickStrafe != 0f));
        var facesTravel = (stickMoves && (move.Behavior == SeatMoveBehavior.FaceTravel));

        if (!rowsMove && !stickMoves) {
            // Nothing moves: the camera-frame latch releases, so the next movement re-reads the camera.
            seat.CameraFrameYaw = null;

            return intent;
        }

        if (
            (ResolveSeatKit(definition: definition) is not { } kit) ||
            (kit.Motion.DeclaredMoveFrame != MotionMoveFrame.World)
        ) {
            return WriteMove(
                forward: (rowForward + stickForward),
                intent: intent,
                roles: roles,
                strafe: (rowStrafe + stickStrafe)
            );
        }

        var frame = channels.MoveFrame;
        var bodyOrientation = Quaternion.Identity;
        var bodyHeading = 0f;

        if (
            ((definition.Views.SeatControl.YawReference == WorldSeatYawReference.Body) || roles.HasMoveDirection) &&
            !endpoint.TryEntityPose(
                index: bodyIndex,
                orientation: out bodyOrientation,
                position: out _
            )
        ) {
            return intent;
        }
        if (
            ((frame == ChannelFrame.Heading) || (seat.FreeLooking && stickMoves)) &&
            !endpoint.TryEntityHeading(
                heading: out bodyHeading,
                index: bodyIndex
            )
        ) {
            return intent;
        }

        var liveCameraYaw = seat.View.LogicalYaw(
            views: definition.Views,
            bodyOrientation: bodyOrientation
        );
        var latchedCameraYaw = (seat.CameraFrameYaw ?? liveCameraYaw);
        var latchesCameraYaw = (
            ((frame == ChannelFrame.Camera) && rowsMove) ||
            facesTravel
        );

        seat.CameraFrameYaw = (latchesCameraYaw
            ? latchedCameraYaw
            : null
        );

        var rowYaw = (frame switch {
            ChannelFrame.Camera => latchedCameraYaw,
            ChannelFrame.Heading => bodyHeading,
            _ => 0f,
        });
        var stickYaw = (seat.FreeLooking
            ? bodyHeading
            : (facesTravel
                ? latchedCameraYaw
                : liveCameraYaw
            )
        );

        UnitDisc(
            forward: ref rowForward,
            strafe: ref rowStrafe
        );
        UnitDisc(
            forward: ref stickForward,
            strafe: ref stickStrafe
        );

        var (rowWorldForward, rowWorldStrafe) = Rotate(
            forward: rowForward,
            strafe: rowStrafe,
            yaw: rowYaw
        );
        var (stickWorldForward, stickWorldStrafe) = Rotate(
            forward: stickForward,
            strafe: stickStrafe,
            yaw: stickYaw
        );

        intent = WriteMove(
            forward: (rowWorldForward + stickWorldForward),
            intent: intent,
            roles: roles,
            strafe: (rowWorldStrafe + stickWorldStrafe)
        );

        // The full world direction, when the world declares the triple. The planar pair above stays written — the
        // gates that ask merely WHETHER movement was commanded read it, and it remains the answer for a body whose
        // up is world up — but on any other surface it cannot carry the direction, so the sim prefers this.
        //
        // Composed from what the player is LOOKING at, not from a world axis: the camera's forward laid onto the
        // body's own tangent plane, and the stick resolved against that. This is the only frame that stays meaningful
        // when the body's up is arbitrary, and it is one the player can predict, because both terms are on screen.
        // Where the body stands on world up and the camera yaws about it, it reproduces the planar pair exactly.
        if (roles.HasMoveDirection) {
            intent = WriteMoveDirection(
                bodyOrientation: bodyOrientation,
                intent: intent,
                roles: roles,
                view: seat.View,
                rowForward: rowForward,
                rowStrafe: rowStrafe,
                rowYaw: rowYaw,
                stickForward: stickForward,
                stickStrafe: stickStrafe,
                stickYaw: stickYaw
            );
        }

        // A camera-framed contribution turns the heading to the way it moves — the stick's direction when it moves,
        // else the rows' when THEY are camera-framed. Heading-framed rows never turn the heading: the Turn role owns
        // that (the sim's facing snap still angles the drawn attitude toward the travel).
        var (faceForward, faceStrafe) = (facesTravel
            ? (stickWorldForward, stickWorldStrafe)
            : (((frame == ChannelFrame.Camera) && rowsMove)
                ? (rowWorldForward, rowWorldStrafe)
                : (0f, 0f))
        );

        if (
            ((faceForward != 0f) || (faceStrafe != 0f)) &&
            (roles.FaceX >= 0) &&
            (roles.FaceZ >= 0)
        ) {
            // The sim reads planar movement as (MoveStrafe, -MoveAdvance) in world X/Z and a facing as the direction
            // (FaceX, FaceZ) — the same vector, so the body faces its own travel exactly.
            var length = MathF.Sqrt(x: ((faceForward * faceForward) + (faceStrafe * faceStrafe)));

            intent = roles.Write(
                intent: intent,
                role: ChannelRole.FaceX,
                value: FixedQ4816.FromDouble(value: (faceStrafe / length))
            );
            intent = roles.Write(
                intent: intent,
                role: ChannelRole.FaceY,
                value: FixedQ4816.Zero
            );
            intent = roles.Write(
                intent: intent,
                role: ChannelRole.FaceZ,
                value: FixedQ4816.FromDouble(value: (-faceForward / length))
            );
        }

        return intent;
    }
    // Clamps a (forward, strafe) pair to the unit disc BEFORE its rotation (the sim's own PlanarIntent rule): two full
    // keys are one direction at full speed, and a rotated component then never leaves [-1, 1] — a per-axis clamp after
    // the rotation would bend the direction by up to ~8° with the yaw, and the facing would show it.
    private static void UnitDisc(ref float forward, ref float strafe) {
        var length = MathF.Sqrt(x: ((forward * forward) + (strafe * strafe)));

        if (length > 1f) {
            forward /= length;
            strafe /= length;
        }
    }
    // The inverse of WorldBody's world-frame read (planarTarget = (MoveStrafe, -MoveAdvance) in world X/Z): rotate a
    // frame-relative (forward, strafe) pair by the frame's yaw into that same world-frame pair.
    private static (float Forward, float Strafe) Rotate(float forward, float strafe, float yaw) {
        if (yaw == 0f) {
            return (forward, strafe);
        }

        var sin = MathF.Sin(x: yaw);
        var cos = MathF.Cos(x: yaw);

        return (((forward * cos) + (strafe * sin)), ((strafe * cos) - (forward * sin)));
    }
    // The world direction a (forward, strafe) pair commands, laid onto the plane the body stands on.
    //
    // The reference is the CAMERA's own heading in world axes — the same yaw the planar pair is rotated by — never
    // the body's facing. The two are independent whenever the body does not turn to its travel, and resolving the
    // stick against the body would add its heading a second time: a seat looking one way while running another would
    // be sent a direction wrong by exactly the angle between them.
    //
    // The whole camera frame is then carried onto the surface by the shortest arc from world up to the body's up,
    // rather than either camera axis being projected onto it. Projecting ANCHORS the basis to one axis, and every
    // such basis dies on a whole RING — wherever the anchor lines up with the surface normal, its projection
    // vanishes and its direction is decided by rounding, which reverses the commanded direction between ticks on a
    // wobble of a hundredth. Rotating the frame instead leaves one singular POINT, the body's up exactly opposite
    // world up, where the shortest arc is ambiguous; a ring is reachable by walking, a point is not.
    private static Vector3 ComposeMoveDirection(float forward, float strafe, float yaw, Quaternion alignment) {
        UnitDisc(
            forward: ref forward,
            strafe: ref strafe
        );

        if ((forward == 0f) && (strafe == 0f)) {
            return Vector3.Zero;
        }

        var sin = MathF.Sin(x: yaw);
        var cos = MathF.Cos(x: yaw);
        // The camera's world heading and the right that goes with it: the pair path's Rotate written as two axes.
        var camForward = new Vector3(x: -sin, y: 0f, z: -cos);
        var camRight = new Vector3(x: cos, y: 0f, z: -sin);

        if (alignment == Quaternion.Identity) {
            // A body standing on world up gets the pair rotation back exactly — no transform, no rounding.
            return ((camForward * forward) + (camRight * strafe));
        }

        return ((Vector3.Transform(
            rotation: alignment,
            value: camForward
        ) * forward) + (Vector3.Transform(
            rotation: alignment,
            value: camRight
        ) * strafe));
    }
    private static PlayerIntent WriteMoveDirection(
        RoleChannelOrdinals roles,
        PlayerIntent intent,
        WorldSeatViewState view,
        Quaternion bodyOrientation,
        float rowForward,
        float rowStrafe,
        float rowYaw,
        float stickForward,
        float stickStrafe,
        float stickYaw
    ) {
        var up = Vector3.Transform(
            rotation: bodyOrientation,
            value: Vector3.UnitY
        );

        if (up.LengthSquared() <= 0f) {
            return intent;
        }

        var alignment = view.CarriedUpAlignment(up: up);

        // The two contributions fold exactly as the planar pair's do: each clamped to the unit disc against its OWN
        // frame yaw, summed, and the sum clamped once.
        var direction = (ComposeMoveDirection(
            alignment: alignment,
            forward: rowForward,
            strafe: rowStrafe,
            yaw: rowYaw
        ) + ComposeMoveDirection(
            alignment: alignment,
            forward: stickForward,
            strafe: stickStrafe,
            yaw: stickYaw
        ));
        var length = direction.Length();

        if (length > 1f) {
            direction /= length;
        }

        intent = roles.Write(
            intent: intent,
            role: ChannelRole.MoveX,
            value: FixedQ4816.FromDouble(value: direction.X)
        );
        intent = roles.Write(
            intent: intent,
            role: ChannelRole.MoveY,
            value: FixedQ4816.FromDouble(value: direction.Y)
        );

        return roles.Write(
            intent: intent,
            role: ChannelRole.MoveZ,
            value: FixedQ4816.FromDouble(value: direction.Z)
        );
    }
    private static PlayerIntent WriteMove(RoleChannelOrdinals roles, PlayerIntent intent, float forward, float strafe) {
        var negativeOne = -FixedQ4816.One;

        intent = roles.Write(
            intent: intent,
            role: ChannelRole.MoveAdvance,
            value: FixedQ4816.Clamp(
                value: FixedQ4816.FromDouble(value: forward),
                minimum: negativeOne,
                maximum: FixedQ4816.One
            )
        );

        return roles.Write(
            intent: intent,
            role: ChannelRole.MoveStrafe,
            value: FixedQ4816.Clamp(
                value: FixedQ4816.FromDouble(value: strafe),
                minimum: negativeOne,
                maximum: FixedQ4816.One
            )
        );
    }
    /// <summary>The steer composition: held pointer <c>player.steer</c> writes the camera's full world facing, while
    /// an Axis2D <c>player.look.steer</c> sample writes planar yaw only so action-game look turns an upright body and
    /// vertical look remains camera pitch. The sim's own facing snap does the turning. Input
    /// composition only, like <see cref="ComposeMoveFrame"/>: no camera pose reaches the sim, only a commanded
    /// direction. Only meaningful under a world yaw reference (the validator refuses steer arming beside a body-relative
    /// one: a camera that follows the body cannot also lead it).</summary>
    private PlayerIntent ComposeSteer(int slot, int bodyIndex, WorldAuthorityEndpoint endpoint, WorldDefinition definition, PlayerIntent intent) {
        if (
            (m_roster.Seat(slot: slot) is not { } seat) ||
            (!seat.PointerSteering && !seat.LookFacesBody) ||
            !endpoint.TryEntityPose(
                index: bodyIndex,
                orientation: out var bodyOrientation,
                position: out _
            )
        ) {
            return intent;
        }

        var channels = (ReferenceEquals(
            objA: definition,
            objB: m_definition
        )
            ? m_channels
            : WorldChannelTable.Compile(channels: definition.Channels)
        );
        var roles = channels.RoleOrdinals;

        if ((roles.FaceX < 0) || (roles.FaceZ < 0) || (seat.PointerSteering && (roles.FaceY < 0))) {
            return intent;
        }

        var yaw = seat.View.LogicalYaw(
            views: definition.Views,
            bodyOrientation: bodyOrientation
        );
        // The orbit's pitch is the eye's elevation above the target, so the camera looks DOWN by that angle.
        var pitch = seat.View.LogicalPitch(views: definition.Views);
        var planar = (seat.PointerSteering ? MathF.Cos(x: pitch) : 1f);

        intent = roles.Write(
            intent: intent,
            role: ChannelRole.FaceX,
            value: FixedQ4816.FromDouble(value: (-MathF.Sin(x: yaw) * planar))
        );
        if (roles.FaceY >= 0) {
            intent = roles.Write(
                intent: intent,
                role: ChannelRole.FaceY,
                value: (seat.PointerSteering
                    ? FixedQ4816.FromDouble(value: -MathF.Sin(x: pitch))
                    : FixedQ4816.Zero
                )
            );
        }

        return roles.Write(
            intent: intent,
            role: ChannelRole.FaceZ,
            value: FixedQ4816.FromDouble(value: (-MathF.Cos(x: yaw) * planar))
        );
    }
    private static WorldKit? ResolveSeatKit(WorldDefinition definition) {
        var kits = definition.Kits;

        for (var index = 0; (index < kits.Count); index++) {
            if (string.Equals(
                a: kits[index].Name,
                b: definition.DefaultSeatKit,
                comparisonType: StringComparison.Ordinal
            )) {
                return kits[index];
            }
        }

        return null;
    }

    /// <summary>Consumes each seat's right-stick and toggled motion-control samples into its seat-owned view state
    /// exactly once this tick, then runs the world's follow camera for a seat with no manual look input. Gyro is
    /// angular velocity in the provider-neutral gamepad frame (+X right, +Y up, +Z back); the seat preference applies
    /// independent per-axis dead zones/inversion and two authored 3D projections to produce semantic look-right/up
    /// rates. Stick and gyro combine before one allocation-free view nudge/lock. An explicit held recenter remains
    /// higher priority and wins that frame.</summary>
    public void AdvanceSeatViews(float deltaSeconds) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is not { } seat) {
                continue;
            }

            var route = m_seatRouter.Route(slot: slot);
            var definition = route.Endpoint.Definition;

            var preference = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
            var look = seat.Look;
            var motionLook = (seat.MotionControlsActive
                ? preference.Gyro.Project(angularVelocity: seat.MotionAngularVelocity)
                : Vector2.Zero
            );
            var lookRate = (new Vector2(
                x: (((float)((double)look.Value.X)) * preference.StickLookRate),
                y: (((float)((double)look.Value.Y)) * preference.StickLookRate)
            ) + motionLook);
            var looking = (
                look.IsActive ||
                seat.MotionControlsActive ||
                seat.FreeLooking ||
                seat.Orbiting ||
                seat.PointerSteering
            );

            seat.View.Nudge(
                input: lookRate,
                yawScale: deltaSeconds,
                pitchScale: deltaSeconds,
                preference: preference,
                views: definition.Views
            );

            // A held recenter drives the camera round behind the body EVERY tick — the body turning under it drags
            // the camera along — and wins over both this tick's look nudge and the follow below.
            if (seat.Recentering) {
                if (route.Endpoint.TryEntityHeading(
                    index: route.EntityIndex,
                    heading: out var recenterHeading
                )) {
                    seat.View.RecenterLook(
                        targetYaw: recenterHeading,
                        views: definition.Views
                    );
                }

                continue;
            }

            if (
                looking ||
                (definition.Views.SeatControl.Follow is not { } follow) ||
                (!follow.WhileIdle && !seat.MovementHeld) ||
                !route.Endpoint.TryEntityHeading(
                    index: route.EntityIndex,
                    heading: out var heading
                )
            ) {
                continue;
            }

            seat.View.Follow(
                targetYaw: heading,
                rate: follow.Rate,
                deltaSeconds: deltaSeconds
            );
        }
    }
    /// <summary>Attaches the accepted-lever applier. Composition-root wiring, done after construction because the
    /// presentation services it writes are themselves built around the client.</summary>
    /// <param name="levers">The applier accepted levers dispatch through.</param>
    public void AttachSessionLevers(WorldSessionLeverSink levers) {
        ArgumentNullException.ThrowIfNull(argument: levers);

        m_levers = levers;
    }
    /// <summary>The entity's render body color: a joined seat composes client-side (profile color with the
    /// pending-gray desaturation folded in); every other entity carries the snapshot's color.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 BodyColor(int index) {
        return (((index < PlayerRoster.MaxSlots) && m_roster.IsJoined(slot: index))
            ? m_roster.BodyColor(slot: index)
            : m_color[index]
        );
    }
    /// <summary>The entity-owned procedural catalog rig from the latest snapshot.</summary>
    public byte CatalogRig(int index) => m_catalogRig[index];
    /// <inheritdoc/>
    public void DeliverAnswer(in QueryAnswer answer) {
        // Queries are synchronous over the loopback (the link returns the answer); nothing arrives on this lane yet.
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
    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // Store the live definition and bump the delivery revision (one component of WriteRevision), so the frame source rebuilds
        // its program and re-reads scene/screens on its next capture. Poses still flow only through snapshots.
        m_definition = definition;
        m_fields = ((definition.Fields is { } fields)
            ? (((m_fields is { } existing) && (existing.Document.Lattice == fields.Lattice) && (existing.FieldCount == fields.Fields.Count))
                ? existing
                : new WorldClientFieldLattice(document: fields))
            : null
        );
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_targets = WorldTargetRegisterTable.Compile(
            registers: definition.TargetRegisters,
            channelCount: m_channels.ChannelCount
        );
        m_definitionRevision++;
    }
    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        // Already past the server's Mutate check on the lever's folded-into section (see WorldServer.ApplySessionLever),
        // so this only dispatches the write onto the presentation service the lever names.
        m_levers?.Apply(lever: lever);
    }
    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot) {
        Array.Clear(array: m_seen);
        m_fields?.Apply(
            deltas: snapshot.FieldCells.Span,
            full: snapshot.FieldsFull
        );

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            var index = entry.Index;

            m_seen[index] = true;
            m_color[index] = entry.BodyColor;
            m_kit[index] = entry.Kit;
            m_look[index] = entry.Look;
            m_catalogRig[index] = entry.CatalogRig;
            m_generation[index] = entry.Generation;
            m_placementId[index] = entry.PlacementId;

            if (!m_active[index]) {
                // Newly active: both interpolation endpoints start at the spawn pose so the first frame never streaks.
                m_previousPosition[index] = entry.Position;
                m_previousOrientation[index] = entry.Orientation;
                m_easers[index].Reset();
                m_poseEpoch[index]++;
            } else {
                switch (entry.Continuity.Kind) {
                    case EntityContinuityKind.Teleport:
                        // Never interpolate across the jump; any in-flight correction offset is dropped with it.
                        m_previousPosition[index] = entry.Position;
                        m_previousOrientation[index] = entry.Orientation;
                        m_easers[index].Reset();
                        m_poseEpoch[index]++;

                        break;
                    case EntityContinuityKind.Correction: {
                            // Authority snapped: ease the render error (last drawn tick pose minus authority) to zero over
                            // the window. Over-threshold corrections snap instead: the easer basis is the previous
                            // snapshot, which may lag same-tick multi-pose batches past the server's own snap-escape check.
                            var positionError = (m_currentPosition[index] - entry.Position);

                            if (positionError.Length() > m_definition.Motion.MaxSmoothError) {
                                m_easers[index].Reset();
                                m_poseEpoch[index]++;
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
            // Keep the resolved pose meaningful in a headless composition, where no presentation frame calls
            // UpdateRenderPoses. A windowed frame replaces these with its interpolated/eased values before drawing.
            m_renderPosition[index] = entry.Position;
            m_renderOrientation[index] = entry.Orientation;
            m_active[index] = true;
        }

        var peers = 0;
        var stepSeconds = ((float)EngineTicks.ToSeconds(ticks: snapshot.StepTicks));

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
        m_authority = snapshot.Authority;
    }
    /// <summary>The complete durable address of the active occupant in a local slot.</summary>
    public WorldEntityAddress EntityAddress(int index) => new(
        Authority: m_authority,
        Index: index,
        Generation: m_generation[index]
    );
    /// <summary>Whether the entity is drawn this frame (present in the latest snapshot).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_active[index];
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

        return ((lookIndex < rows.Count)
            ? rows[lookIndex]
            : WorldLook.Implicit
        );
    }
    /// <summary>The entity's resolved look row index from the latest snapshot — the frame source's appearance selector
    /// (presentation-only).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public byte LookIndex(int index) => m_look[index];
    /// <summary>The entity's per-frame render attitude (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Quaternion Orientation(int index) => m_renderOrientation[index];
    /// <summary>The placement row this entity inhabits, or <see langword="null"/> for a seat/peer — the frame source
    /// renders an inhabitant's creation geometry (a body-rooted stamp) instead of a catalog avatar.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public string? PlacementId(int index) => m_placementId[index];
    /// <summary>The entity's pose-discontinuity counter — bumped on activation, a <see cref="EntityContinuityKind.Teleport"/>,
    /// and an over-threshold <see cref="EntityContinuityKind.Correction"/> snap. A presentation-side second-order
    /// follower reseeds whenever this changes, so it never chases across a jump.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public int PoseEpoch(int index) => m_poseEpoch[index];
    /// <summary>The entity's per-frame render position (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 Position(int index) => m_renderPosition[index];
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
    /// <param name="endpoint">The authority whose clock is about to consume <paramref name="tick"/>.</param>
    /// <param name="tick">The authority-local tick the submissions are for.</param>
    public void SubmitAuthorityIntents(WorldAuthorityEndpoint endpoint, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);
        Span<bool> live = stackalloc bool[PlayerRoster.MaxSlots];
        Span<int> targets = stackalloc int[PlayerRoster.MaxSlots];
        var routes = new WorldAuthorityRoute?[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                m_roster.IsPending(slot: slot) ||
                (m_roster.Seat(slot: slot) is not { } seat) ||
                !seat.Source.IsLive
            ) {
                continue;
            }

            var route = m_seatRouter.Route(slot: slot);

            if (!ReferenceEquals(
                objA: route.Endpoint,
                objB: endpoint
            )) {
                continue;
            }
            if (
                (m_lastSubmissionEpoch[slot] == route.Epoch) &&
                (m_lastSubmissionTick[slot] >= tick)
            ) {
                continue;
            }

            live[slot] = true;
            targets[slot] = route.EntityIndex;
            routes[slot] = route;
        }

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!live[slot]) {
                continue;
            }

            var seat = m_roster.Seat(slot: slot)!;
            var definition = endpoint.Definition;
            var intent = ComposeSteer(
                slot: slot,
                bodyIndex: targets[slot],
                endpoint: endpoint,
                definition: definition,
                intent: ComposeMoveFrame(
                    slot: slot,
                    bodyIndex: targets[slot],
                    endpoint: endpoint,
                    definition: definition,
                    intent: seat.HeldIntent()
                )
            );
            var heldChannels = seat.HeldChannels;

            if (
                !m_roster.IsClaimed(slot: slot) &&
                (intent == default) &&
                (heldChannels == default) &&
                ClaimedElsewhereTargets(
                live: live,
                targets: targets,
                body: targets[slot],
                exceptSlot: slot
            )
            ) {
                continue;
            }

            endpoint.Submissions.SubmitIntent(submission: new IntentSubmission(
                Tick: tick,
                EntityIndex: targets[slot],
                Intent: intent,
                Principal: m_roster.PrincipalOf(slot: slot),
                HeldChannels: heldChannels
            ));
            m_lastSubmissionEpoch[slot] = routes[slot]!.Epoch;
            m_lastSubmissionTick[slot] = tick;
        }
    }
    /// <summary>Resolves the nearest snapshot subject inside a source body's clamped designation cone. This is a
    /// proposal only; the server re-resolves the returned subject and owns the register write.</summary>
    public bool TryFindDesignationSubject(int sourceBody, string registerName, out GrantSubject subject) {
        subject = default;

        if (
            !IsActive(index: sourceBody) ||
            !m_targets.TryGetIndex(
            index: out var registerIndex,
            name: registerName
        )
        ) {
            return false;
        }

        var register = m_definition.TargetRegisters[registerIndex];
        var halfAngle = register.MaximumHalfAngleDegrees;
        var range = FixedQ4816.FromDouble(value: register.MaximumRange);
        var minimumDot = FixedQ4816.FromDouble(value: Math.Cos(d: (halfAngle * (Math.PI / 180.0))));
        var origin = FixedVector3.FromVector3(value: m_currentPosition[sourceBody]);
        var forward = FixedVector3.FromVector3(value: Vector3.Transform(
            value: -Vector3.UnitZ,
            rotation: m_currentOrientation[sourceBody]
        ));
        var nearest = FixedQ4816.MaxValue;
        var found = -1;

        for (var index = 0; (index < EntityCapacity); index++) {
            if (
                (index == sourceBody) ||
                !m_active[index]
            ) {
                continue;
            }

            var candidate = FixedVector3.FromVector3(value: m_currentPosition[index]);

            if (
                BodyTargetConeSense.Contains(
                candidate: in candidate,
                distanceSquared: out var squared,
                forward: in forward,
                minimumDot: minimumDot,
                origin: in origin,
                range: range
            ) &&
                (squared < nearest)
            ) {
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
    /// <summary>Resolves the active entity index a placement's first inhabited body occupies (the audio anchor / stamp
    /// pose lookup), or <see langword="false"/> when no active entity inhabits it.</summary>
    /// <param name="placementId">The placement row id.</param>
    /// <param name="index">The resolved 0-based entity index.</param>
    public bool TryInhabitantBody(string placementId, out int index) {
        for (var candidate = 0; (candidate < EntityCapacity); candidate++) {
            if (
                m_active[candidate] &&
                string.Equals(
                a: m_placementId[candidate],
                b: placementId,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                index = candidate;

                return true;
            }
        }

        index = -1;

        return false;
    }
    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        if (
            (((uint)anchorId) >= EntityCapacity) ||
            !m_active[anchorId]
        ) {
            anchor = default;

            return false;
        }

        anchor = new SdfAnchor(
            Position: m_renderPosition[anchorId],
            Orientation: m_renderOrientation[anchorId]
        );

        return true;
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

            var position = Vector3.Lerp(
                value1: m_previousPosition[index],
                value2: m_currentPosition[index],
                amount: alpha
            );
            // Quaternion.Lerp is the nlerp: shortest-path dot-sign flip and renormalize.
            var orientation = Quaternion.Lerp(
                quaternion1: m_previousOrientation[index],
                quaternion2: m_currentOrientation[index],
                amount: alpha
            );

            m_easers[index].Apply(
                orientation: ref orientation,
                position: ref position
            );
            m_renderPosition[index] = position;
            m_renderOrientation[index] = orientation;
        }
    }
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

        // Fold the current (smoothstep-eased) offset into an interpolated render pose: the position error adds in and the
        // orientation error Slerp(identity, E, weight) is applied outermost (world space) to the attitude, so the
        // on-screen craft glides from where it was to authority. A no-op once the window drains.
        public readonly void Apply(ref Vector3 position, ref Quaternion orientation) {
            if (
                (m_remaining > 0f) &&
                (m_window > 0f)
            ) {
                // Smoothstep ease: weight is 1 at receipt (craft sits at its old pose) and eases to 0 as the window drains
                // (craft arrives at authority), with a soft start and a soft settle. fraction = remaining/window runs 1→0.
                var fraction = (m_remaining / m_window);
                var weight = ((fraction * fraction) * (3f - (2f * fraction)));

                position += (m_position * weight);
                // At weight 1 the render attitude is E · interpolated = the old attitude; at weight 0 it is the
                // interpolated (authoritative) attitude — the angular twin of the position offset's decay.
                orientation = (Quaternion.Slerp(
                    quaternion1: Quaternion.Identity,
                    quaternion2: m_orientation,
                    amount: weight
                ) * orientation);
            }
        }
        // Arm the easer with a fresh correction error over a smoothing window — the Correction continuity path.
        public void Begin(Vector3 positionError, Quaternion orientationError, float seconds) {
            m_position = positionError;
            m_orientation = orientationError;
            m_window = seconds;
            m_remaining = seconds;
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
        // Drop any in-flight offset (a hard teleport) so it never drags the avatar off an authoritative jump.
        public void Reset() {
            m_position = default;
            m_orientation = Quaternion.Identity;
            m_window = 0f;
            m_remaining = 0f;
        }
    }
}

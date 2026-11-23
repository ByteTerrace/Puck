using Puck.Commands;
using System.Numerics;
using Puck.Maths;
using Puck.SdfVm;
using Puck.SignedDistance.Queries;
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
public sealed class WorldClient : IClientSink, ISdfAnchorSource, IWorldStampSource {
    /// <summary>The entity-view capacity — single-sourced from <see cref="WorldBodiesLimits.CapacityCeiling"/>
    /// so the validator's admitted population.capacity and this client's fixed
    /// per-entity arrays can never again drift apart: an over-capacity document refuses at load instead of booting
    /// into a latent out-of-bounds throw here.</summary>
    public const int EntityCapacity = WorldBodiesLimits.CapacityCeiling;
    /// <summary>How many counters <see cref="WriteRevision"/> reports — the component count
    /// <c>WorldSceneEmitter</c> folds into its own when it lays out its revision vector.</summary>
    public const int RevisionComponentCount = 4;

    // The shared live composition-override store — the frame source's composer reads it; DeliverComposition writes it.
    private readonly WorldCompositionState m_composition;
    private readonly PlayerRoster m_roster;
    // The authority claim is the sole source for both the entity an input drives and the endpoint that accepts it.
    // There is no boot/away distinction in this client.
    private readonly WorldSeatAuthorityRouter m_seatRouter;

    private readonly IReadOnlyList<BindingSourceContract>?[] m_inputProfiles = new IReadOnlyList<BindingSourceContract>?[PlayerRoster.MaxSlots];
    private readonly WorldAuthorityRoute?[] m_inputRoutes = new WorldAuthorityRoute?[PlayerRoster.MaxSlots];
    private readonly WorldDefinition?[] m_inputDefinitions = new WorldDefinition?[PlayerRoster.MaxSlots];
    private readonly WorldAuthorityRoute?[] m_submissionRoutes = new WorldAuthorityRoute?[PlayerRoster.MaxSlots];

    private InputRouter? m_inputRouter;

    /// <summary>Attaches the host router once, for authority-published destination latch cancellations.</summary>
    public void UseInputRouter(InputRouter router) => m_inputRouter = router;
    /// <summary>Admits a changed routed binding profile and applies the authority's published source decisions.</summary>
    public void SynchronizeInput(int slot, IReadOnlyList<BindingSourceContract> sources) {
        if (m_roster.IsPending(slot: slot) || (m_roster.Seat(slot: slot) is not { } seat) || (m_seatRouter.TryRoute(slot: slot) is not { } route)) { return; }
        var principal = m_roster.PrincipalOf(slot: slot);

        if ((seat.Input is null) || !ReferenceEquals(objA: m_inputProfiles[slot], objB: sources) || !ReferenceEquals(objA: m_inputRoutes[slot], objB: route) ||
            !ReferenceEquals(objA: m_inputDefinitions[slot], objB: route.Endpoint.Definition)) {
            var freshAttachment = ((seat.Input is null) || !ReferenceEquals(objA: m_inputRoutes[slot], objB: route));
            var sameProfile = ((m_inputProfiles[slot] is { } previous) && previous.SequenceEqual(second: sources));
            var sameContract = ((m_inputDefinitions[slot] is { } definition) && WorldInputCatalog.SameContract(left: definition, right: route.Endpoint.Definition));
            var replace = (freshAttachment || !sameProfile || !sameContract);

            if (replace) { seat.Input = new WorldSeatInput(route.Endpoint.Definition, sources, (freshAttachment ? null : seat.Input)); }
            m_inputProfiles[slot] = sources;
            m_inputRoutes[slot] = route;
            m_inputDefinitions[slot] = route.Endpoint.Definition;
            if (replace) { seat.Input!.Admit(route.Endpoint, principal, route.EntityIndex, freshAttachment); }
        }
        if (TryRouteInput(principal: principal, publication: out var publication, route: route)) { _ = seat.Input!.Reconcile(publication: publication, router: m_inputRouter, seat: seat, slot: slot); } else { seat.Input!.Admit(route.Endpoint, principal, route.EntityIndex, freshAttachment: false); }
        if (m_inputRouter is { } router) { seat.Input!.AcknowledgeReleasedButtons(router: router, slot: slot); }
    }
    // The one door a seat's input publication at its route's authority is read through: under the seat's own principal,
    // else under whichever owner that authority answers the routed body's input as (a forwarded authority stamps a
    // traveler as its peer, a principal this client never names).
    private static bool TryRouteInput(WorldAuthorityRoute route, Principal principal, out WorldInputPublication publication) =>
        (route.Endpoint.Mirror.TryInput(owner: principal, publication: out publication) || route.Endpoint.Mirror.TryInputOf(entity: route.Entity, publication: out publication));

    private readonly SeatGuessView m_seatGuess = new();

    /// <inheritdoc/>
    /// <remarks>This client's own world's scene asks here: <see cref="SeatGuessAt"/> with no endpoint.</remarks>
    public IWorldSeatGuess? SeatGuess => SeatGuessAt(endpoint: null);

    /// <summary>Returns the presenting seat's guess on its own input for the scene of one world: offered only while this
    /// client presents exactly one seat, and only to the scene of the world whose authority the seat's route names, for
    /// the body the route drives there.</summary>
    /// <param name="endpoint">The authority whose world the asking scene draws, or <see langword="null"/> for this
    /// client's own world.</param>
    /// <returns>The guess, or <see langword="null"/> when no guess is offered (<see cref="DescribeGuess"/> names why) or
    /// the seat's route names another world than the asking scene's.</returns>
    /// <remarks>The guess is drawn in that seat's own view alone (<see cref="WorldCreationInstances.GuessOverlay"/>),
    /// whatever else renders the scene, so the audience test counts every joined seat, pending ones included, only
    /// because a scene holds one guess layer.</remarks>
    public IWorldSeatGuess? SeatGuessAt(WorldAuthorityEndpoint? endpoint) {
        if ((BindGuessAudience(slot: out _) is not null) || (m_seatGuess.Route is not { } route)) { return null; }
        // The body index names a body of the route's world only, so no other world's scene is offered it.
        var here = ((endpoint is null)
            ? string.Equals(a: route.Endpoint.Authority, b: m_authority, comparisonType: StringComparison.Ordinal)
            : ReferenceEquals(objA: route.Endpoint, objB: endpoint));

        return (here ? m_seatGuess : null);
    }
    /// <summary>Reads whether this client offers a seat a guess and, when it does, the state of the seat's latest edge
    /// on a led channel and the figures it rests on. It writes nothing.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="channel">The led channel's name.</param>
    /// <returns>A one-line reading: <c>off(reason)</c> when no guess is offered to the seat, else the state, the seat's
    /// outstanding event sequence, the authority's answer (epoch, sequence, tick) and the presented tick and fraction.
    /// How long an outstanding edge shows is the guess layer's (<c>body.rig</c>'s <c>layer:</c> readings).</returns>
    public string DescribeGuess(int slot, string channel) {
        if (BindGuessAudience(slot: out var audience) is { } refusal) { return $"off({refusal})"; }
        if (audience != slot) { return "off(another seat presents)"; }

        return m_seatGuess.Describe(channel: channel);
    }
    // Decides the guess's audience once for a frame and binds the one seat's guess to it: null when exactly one seat is
    // joined on this client and it is admitted and routed to a body, else why not.
    private string? BindGuessAudience(out int slot) {
        var found = -1;

        slot = -1;
        for (var candidate = 0; (candidate < PlayerRoster.MaxSlots); candidate++) {
            if (m_roster.Seat(slot: candidate) is null) { continue; }
            if (found >= 0) { return "two or more seats"; }
            found = candidate;
        }
        if (found < 0) { return "no seat"; }
        if (m_roster.IsPending(slot: found)) { return "seat pending"; }
        if (m_roster.Seat(slot: found)!.Input is not { Admitted: true } input) { return "seat input not admitted"; }
        if ((m_seatRouter.TryRoute(slot: found) is not { } route) || (route.EntityIndex < 0)) { return "seat routes to no body"; }
        m_seatGuess.Bind(body: route.EntityIndex, input: input, presented: StateMirrorFor(endpoint: route.Endpoint), principal: m_roster.PrincipalOf(slot: found), route: route, slot: found);
        slot = found;

        return null;
    }

    // The one seat's guess for a frame: its producer, its route (whose authority answers it) and the mirror its picture
    // presents.
    private sealed class SeatGuessView : IWorldSeatGuess {
        private WorldSeatInput? m_input;
        private WorldStateMirror? m_presented;
        private Principal m_principal;

        public int Body { get; private set; } = -1;
        public int Slot { get; private set; } = -1;
        public WorldAuthorityRoute? Route { get; private set; }

        public void Bind(WorldSeatInput input, WorldAuthorityRoute route, WorldStateMirror presented, Principal principal, int body, int slot) {
            (m_input, Route, m_presented, m_principal, Body, Slot) = (input, route, presented, principal, body, slot);
        }
        public bool TryLead(string channel, out float level, out ulong edge) {
            level = 0f;
            edge = 0UL;
            if ((m_input is not { } input) || (m_presented is not { } presented)) { return false; }

            return input.TryLead(
                answer: Answer(),
                channel: channel,
                edge: out edge,
                level: out level,
                presentedFraction: presented.PresentationFraction,
                presentedTick: presented.Tick
            );
        }
        public string Describe(string channel) {
            if ((m_input is not { } input) || (m_presented is not { } presented)) { return "off(unbound)"; }

            var answer = Answer();
            var reading = input.ReadGuess(answer: answer, channel: channel, presentedFraction: presented.PresentationFraction, presentedTick: presented.Tick);
            var answered = ((answer is { } given) ? $"{given.Epoch}/{given.Sequence}@{given.Tick}" : "none");

            return string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{(reading.State == WorldGuessState.InForce ? "lead" : "cell")}({reading.State}) level={reading.Level:0.##} outstanding={reading.Outstanding} epoch={reading.Epoch} answer={answered} presented={presented.Tick}+{presented.PresentationFraction:0.##}");
        }
        // The answer is read under the owner the route's authority publishes this body's input as.
        private WorldInputAnswer? Answer() => (((Route is { } route) && TryRouteInput(principal: m_principal, publication: out var publication, route: route) &&
            route.Endpoint.Mirror.TryInputAnswer(answer: out var answer, owner: publication.Owner)) ? answer : null);
    }
    /// <summary>Delivers committed text to the admitted input owner at its authority's next input tick.</summary>
    public void ObserveText(CommittedTextInput input) {
        if ((m_roster.PrincipalOf(slot: input.Slot) != input.Principal) || m_roster.IsPending(slot: input.Slot) ||
            (m_roster.Seat(slot: input.Slot)?.Input is not { } producer) || (m_seatRouter.TryRoute(slot: input.Slot) is not { } route)) { return; }
        _ = producer.ObserveText(input: input, tick: route.Endpoint.NextInputTick);
    }

    private int m_activePeerCount;
    private long m_activeRevision;
    private long m_renderPoseRevision;
    private WorldChannelTable m_channels;
    private int m_definitionRevision;
    private int m_responseRevision;
    private int m_holdingRevision;
    // The definition the latest state delivery handed over, before live state values were bound into it.
    private WorldDefinition? m_lastDelivered;
    private int m_solidResponseRevision;
    private WorldDocumentVersion m_documentVersion;

    // The field and document rows a snapshot's cell writes moved, handed to the state mirror.
    private readonly int[] m_movedCells = new int[StateCapacity.MaxRows];
    private readonly int[] m_movedBodyState = new int[WorldDocumentStateView.MaxMovedBodyState];

    // The accepted-lever applier (see WorldSessionLeverSink). Optional so a client composed without the presentation
    // services — a headless or test host — simply drops accepted levers rather than failing to construct.
    private WorldSessionLeverSink? m_levers;
    private int m_serverRevision;
    // The static-scene query field, rebuilt by WorldFramePresenter whenever the composed program changes and parked
    // here so every presentation consumer reads ONE evaluator: the chase camera's clearance sweep and the stamp
    // pool's limb probes ask the same geometry the same way. Unsupported placements are excluded individually;
    // null means no program has been composed yet, or the remaining combined field could not be built.
    private SdfFieldEvaluator? m_staticField;
    private WorldTargetRegisterTable m_targets;
    private ulong m_tick;
    private ulong m_engineTick;

    private readonly WorldStateMirror m_stateMirror;
    private readonly WorldDocumentStateView m_stateView;

    private bool m_pendingStateInstall;

    /// <summary>The authority stamped on the latest delivered entity image.</summary>
    public string Authority => m_authority;
    /// <summary>The client's own world definition: the boot definition, then whatever the authority last delivered after
    /// an applied mutation batch or swap, each adopted through the state view so every state-bound value in it reads the
    /// cells this client was delivered, never the authority's arena. The frame source reads scene/screens from here on
    /// its next rebuild.</summary>
    public WorldDefinition Definition => m_stateView.Definition;
    /// <summary>The monotonic definition-delivery counter — bumped each time the server delivers a new definition. The
    /// frame source watches it to know a scene/screen change landed (distinct from a population/roster change).</summary>
    public int DefinitionRevision => m_definitionRevision;
    /// <summary>Monotonic counter bumped whenever any entity's active status changes across a snapshot delivery.</summary>
    public long ActiveRevision => m_activeRevision;
    /// <summary>Monotonic counter bumped whenever any active entity's render pose or active status changes.</summary>
    public long RenderPoseRevision => m_renderPoseRevision;
    /// <summary>Gets the monotonic solid-response counter: bumped with the response revision only when a solid
    /// placement is among the rows the delivery moved, so a consumer of solid geometry alone (the static query field)
    /// holds across a lamp or a display swapping its creation.</summary>
    public int SolidResponseRevision => m_solidResponseRevision;
    /// <summary>Gets the monotonic holding counter: bumped instead of the response revision (<see cref="WriteRevision"/>)
    /// when a delivery moved only placement holdings, so the scene reconciles its rows and composes again only for a row
    /// whose shown response it does not keep at hand.</summary>
    public int HoldingRevision => m_holdingRevision;
    /// <summary>The version of the latest delivered definition, or <see langword="default"/> before the first delivery.</summary>
    public WorldDocumentVersion DocumentVersion => m_documentVersion;
    /// <summary>The client seat table.</summary>
    public PlayerRoster Roster => m_roster;
    /// <summary>Gets the static-scene query field, or <see langword="null"/> when none is resolved.</summary>
    public SdfFieldEvaluator? StaticField => m_staticField;
    /// <summary>The latest snapshot's tick.</summary>
    public ulong Tick => m_tick;
    /// <summary>The latest snapshot's engine tick — what a <c>StateAdvance</c> row's live value is computed at;
    /// never derived from <see cref="Tick"/> at a simulation rate.</summary>
    public ulong EngineTick => m_engineTick;

    private long m_arrivalTimestamp;

    /// <summary>The last delivered snapshot's step width, in engine ticks.</summary>
    public ulong StepTicks { get; private set; }
    /// <summary>The presentation fraction since the last snapshot arrived, shared with session views.</summary>
    public float InterpolationAlpha => WorldSessionMirror.ResolveInterpolationAlpha(
        stepSeconds: ((float)FixedTickConversion.ToSeconds(ticks: StepTicks)), arrivalTimestamp: m_arrivalTimestamp);
    /// <summary>Gets the state mirror every presentation binding of this client reads through, refreshed at each
    /// state delivery and each tick, and resolved to row ordinals at each definition delivery.</summary>
    public WorldStateMirror StateMirror => m_stateMirror;

    /// <summary>Publishes the client's coherent definition, structural revision and delivered samples after each
    /// mirror delivery. The callback borrows the mirror synchronously on the boot client's presentation thread.</summary>
    public event Action<WorldDefinition, int, WorldStateMirror>? DeliveredState;

    private void PublishDeliveredState() => DeliveredState?.Invoke(Definition, m_definitionRevision, m_stateMirror);

    /// <summary>Returns the state mirror presentation reads an authority's rows through: this client's own
    /// <see cref="StateMirror"/> for the authority this client observes, and the authority's own followed mirror
    /// (<see cref="WorldAuthorityEndpoint.FollowState"/>) for any other, so a seat routed to another authority reads the
    /// rows of the document its rig names, at that authority's delivered tick.</summary>
    /// <param name="endpoint">The authority whose rows are read.</param>
    /// <returns>The mirror over that authority's delivered rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
    public WorldStateMirror StateMirrorFor(WorldAuthorityEndpoint endpoint) {
        ArgumentNullException.ThrowIfNull(argument: endpoint);

        return (string.Equals(
            a: endpoint.Authority,
            b: m_authority,
            comparisonType: StringComparison.Ordinal
        )
            ? m_stateMirror
            : endpoint.FollowState()
        );
    }

    // The shared per-seat live orbit. Camera-relative movement reads only its already-integrated yaw while composing
    // a world-frame intent; the deterministic simulation still receives ordinary fixed-point role channels and has
    // no camera dependency of its own.
    private readonly ulong[] m_lastSubmissionEpoch = new ulong[PlayerRoster.MaxSlots];
    private readonly ulong[] m_lastSubmissionTick = new ulong[PlayerRoster.MaxSlots];
    // The double-buffered per-entity tick poses (the interpolation endpoints), active flags, placements and pose epochs
    // (m_poses), the tick's palette/archetype image, and the per-entity correction easers. Sized to the table ceiling;
    // inactive slots are simply unseen.
    private readonly WorldPoseTable m_poses = new(capacity: EntityCapacity);
    private readonly Vector3[] m_color = new Vector3[EntityCapacity];
    // The kit row index per entity: the chase camera reads the framed body's collider through it.
    private readonly byte[] m_kit = new byte[EntityCapacity];
    // The LOOK row index per entity — the frame source reads it to resolve each body's appearance (catalog rig vs.
    // creation stamp), scale, and gait amplitude. PRESENTATION-ONLY.
    private readonly byte[] m_look = new byte[EntityCapacity];
    private readonly BodyFacts[] m_facts = new BodyFacts[EntityCapacity];
    // The occupant-owned procedural rig. This is deliberately distinct from the authority-local entity index: a
    // seamless transfer may move the occupant to another slot without changing its implicit shape.
    private readonly byte[] m_catalogRig = new byte[EntityCapacity];
    private readonly int[] m_generation = new int[EntityCapacity];
    private readonly RenderErrorEaser[] m_easers = new RenderErrorEaser[EntityCapacity];
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
        m_stateView = new WorldDocumentStateView(clock: () => (m_tick, m_engineTick), delivered: definition);
        m_channels = WorldChannelTable.For(definition: Definition);
        m_targets = WorldTargetRegisterTable.Compile(
            registers: Definition.TargetRegisters
        );
        m_composition = composition;
        m_levers = null;
        m_stateMirror = new WorldStateMirror(view: m_stateView);
        m_stateMirror.Delivered += PublishDeliveredState;

        for (var index = 0; (index < EntityCapacity); index++) {
            m_renderOrientation[index] = Quaternion.Identity;
            m_easers[index].Reset();
        }
    }

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

        if (
            (forward == 0f) &&
            (strafe == 0f)
        ) {
            return Vector3.Zero;
        }

        var sin = MathF.Sin(x: yaw);
        var cos = MathF.Cos(x: yaw);
        // The camera's world heading and the right that goes with it: the pair path's Rotate written as two axes.
        var camForward = new Vector3(
            x: -sin,
            y: 0f,
            z: -cos
        );
        var camRight = new Vector3(
            x: cos,
            y: 0f,
            z: -sin
        );

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
    /// <summary>The move composition — the determinism seam <c>WorldMotion</c>'s <c>MoveFrame</c> remarks
    /// promise: every movement contribution is rotated into WORLD axes here, before it reaches the wire, so the sim
    /// only ever reads a world-frame vector and never a camera pose. Two producers fold: the seat's held channel
    /// rows (already summed by <see cref="SeatController.HeldIntent"/>), rotated by the frame the world declared on
    /// its MoveAdvance/MoveStrafe pair (<see cref="WorldChannelTable.MoveFrame"/> — <see cref="ChannelFrame.Camera"/>
    /// by the camera yaw, <see cref="ChannelFrame.Heading"/> by the body's authoritative HEADING off the wire
    /// (<see cref="WorldAuthorityEndpoint.TryEntityHeading(int, out float)"/> — never the drawn attitude, which the sim's facing snap
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
    /// World-framed) — the stick folds in raw beside the rows. Nothing rotates either for a seat its authority holds
    /// captured on a device (<see cref="WorldAuthorityEndpoint.DrivesDevice"/>): the rows and the stick sum raw, so a
    /// screen's pad and a board's rules read right as right whichever way the camera faces.</summary>
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

        var channels = seat.Channels;
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
        var stickForward = ((float)stick.Y);
        var stickStrafe = ((float)stick.X);
        var rowsMove = ((rowForward != 0f) || (rowStrafe != 0f));
        var stickMoves = ((stickForward != 0f) || (stickStrafe != 0f));
        var facesTravel = (stickMoves && (move.Behavior == SeatMoveBehavior.FaceTravel));

        if (
            !rowsMove &&
            !stickMoves
        ) {
            // Nothing moves: the camera-frame latch releases, so the next movement re-reads the camera.
            seat.CameraFrameYaw = null;

            return intent;
        }
        if (endpoint.DrivesDevice(index: bodyIndex)) {
            // A device's controls are the seat's own: the pair reaches the screen's pad or the board's rules as
            // pressed, never rotated into the world, and no facing is written for the idle avatar.
            seat.CameraFrameYaw = null;

            return (stickMoves
                ? WriteMove(
                    forward: (rowForward + stickForward),
                    intent: intent,
                    roles: roles,
                    strafe: (rowStrafe + stickStrafe)
                )
                : intent);
        }

        if (
            (ResolveSeatKit(definition: definition) is not { } kit) ||
            (kit.Motion.MoveFrame != MotionMoveFrame.World)
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
        ));

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
                : (0f, 0f)
        ));

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
    /// <summary>The steer composition: held pointer <c>player.steer</c> writes the camera's full world facing, while
    /// an Axis2D <c>player.look.steer</c> sample writes planar yaw only so action-game look turns an upright body and
    /// vertical look remains camera pitch. The sim's own facing snap does the turning. Input
    /// composition only, like <see cref="ComposeMoveFrame"/>: no camera pose reaches the sim, only a commanded
    /// direction. Only meaningful under a world yaw reference (the validator refuses steer arming beside a body-relative
    /// one: a camera that follows the body cannot also lead it).</summary>
    private PlayerIntent ComposeSteer(int slot, int bodyIndex, WorldAuthorityEndpoint endpoint, WorldDefinition definition, PlayerIntent intent) {
        if (
            (m_roster.Seat(slot: slot) is not { } seat) ||
            (!seat.PointerSteering && !seat.LookFacesBody && !seat.View.FirstPersonLook) ||
            (definition.Views.SeatControl.YawReference != WorldSeatYawReference.World) ||
            !endpoint.TryEntityPose(
            index: bodyIndex,
            orientation: out var bodyOrientation,
            position: out _
        )
        ) {
            return intent;
        }

        var channels = seat.Channels;
        var roles = channels.RoleOrdinals;

        if (
            (roles.FaceX < 0) ||
            (roles.FaceZ < 0) ||
            (seat.PointerSteering && !seat.View.FirstPersonLook && (roles.FaceY < 0))
        ) {
            return intent;
        }

        var yaw = seat.View.LogicalYaw(
            views: definition.Views,
            bodyOrientation: bodyOrientation
        );
        // The orbit's pitch is the eye's elevation above the target, so the camera looks DOWN by that angle.
        var pitch = seat.View.LogicalPitch(views: definition.Views);
        var planar = ((seat.PointerSteering && !seat.View.FirstPersonLook)
            ? MathF.Cos(x: pitch)
            : 1f
        );

        intent = roles.Write(
            intent: intent,
            role: ChannelRole.FaceX,
            value: FixedQ4816.FromDouble(value: (-MathF.Sin(x: yaw) * planar))
        );
        if (roles.FaceY >= 0) {
            intent = roles.Write(
                intent: intent,
                role: ChannelRole.FaceY,
                value: ((seat.PointerSteering && !seat.View.FirstPersonLook)
                ? FixedQ4816.FromDouble(value: -MathF.Sin(x: pitch))
                : FixedQ4816.Zero)
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

    /// <summary>Reads a seat's live facing, the yaw its stick movement is framed against (the same yaw intent
    /// composition reads), and the turn one tick of full right-stick look deflection gives it under the seat's look
    /// preference.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="yaw">The facing yaw, in radians (0 faces -Z, a quarter turn faces -X).</param>
    /// <param name="yawPerTick">The yaw change, in radians, of one tick at full positive X deflection: negative, since
    /// a look to the right lowers the yaw, unless the preference inverts it.</param>
    /// <returns><see langword="true"/> when the seat is joined and its body's pose is known.</returns>
    public bool TrySeatLook(int slot, out float yaw, out float yawPerTick) {
        yaw = 0f;
        yawPerTick = 0f;
        if ((m_roster.Seat(slot: slot) is not { } seat) || (m_seatRouter.TryRoute(slot: slot) is not { } route)) {
            return false;
        }

        var definition = route.Endpoint.Definition;

        if (!route.Endpoint.TryEntityPose(index: route.EntityIndex, orientation: out var bodyOrientation, position: out _)) {
            return false;
        }

        var preference = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);

        yaw = seat.View.LogicalYaw(bodyOrientation: bodyOrientation, views: definition.Views);
        yawPerTick = ((-preference.StickLookRate / definition.SimulationRateHz) * (preference.InvertYaw ? -1f : 1f));
        return true;
    }
    /// <summary>Consumes each seat's right-stick and toggled motion-control samples into its seat-owned view state
    /// exactly once this tick, then runs the world's follow camera for a seat with no manual look input. Gyro is
    /// angular velocity in the provider-neutral gamepad frame (+X right, +Y up, +Z back); the seat preference applies
    /// independent per-axis dead zones/inversion and two authored 3D projections to produce semantic look-right/up
    /// rates. Stick and gyro combine before one allocation-free view nudge/lock. An explicit held recenter remains
    /// higher priority and wins that frame. Each seat's view records the tick's whole step, which a frame between
    /// ticks interpolates across (<see cref="WorldSeatViewState.PresentedLook"/>).</summary>
    /// <param name="deltaSeconds">The fixed tick's length, in seconds.</param>
    /// <param name="carried">Whether a tick of the authority behind an endpoint is about to execute beside the frame
    /// and carry this step: a seat routed to such an endpoint holds its step until <see cref="PresentSeatLook"/>
    /// names the endpoint (<see cref="WorldSeatViewState.CompleteTick"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="carried"/> is <see langword="null"/>.</exception>
    public void AdvanceSeatViews(float deltaSeconds, Func<WorldAuthorityEndpoint, bool> carried) {
        ArgumentNullException.ThrowIfNull(argument: carried);

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is not { } seat) {
                continue;
            }

            seat.View.BeginTick();
            AdvanceSeatView(
                deltaSeconds: deltaSeconds,
                seat: seat,
                slot: slot
            );
            seat.View.CompleteTick(held: ((m_seatRouter.TryRoute(slot: slot)?.Endpoint is { } endpoint) && carried(arg: endpoint)));
        }
    }
    /// <summary>Presents the look step each seat routed to an endpoint holds, once that endpoint's tick has been
    /// collected (<see cref="WorldSeatViewState.PresentHeldStep"/>).</summary>
    /// <param name="endpoint">The endpoint whose tick was collected, or <see langword="null"/> for every seat.</param>
    public void PresentSeatLook(WorldAuthorityEndpoint? endpoint) {
        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (
                (m_roster.Seat(slot: slot) is { } seat) &&
                ((endpoint is null) || ReferenceEquals(objA: m_seatRouter.TryRoute(slot: slot)?.Endpoint, objB: endpoint))
            ) {
                seat.View.PresentHeldStep();
            }
        }
    }

    private void AdvanceSeatView(SeatController seat, int slot, float deltaSeconds) {
        var route = m_seatRouter.Route(slot: slot);
        var definition = route.Endpoint.Definition;

        var preference = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
        var look = seat.Look;
        var motionLook = (seat.MotionControlsActive
            ? preference.Gyro.Project(angularVelocity: seat.MotionAngularVelocity)
            : Vector2.Zero
        );
        var lookRate = (new Vector2(
            x: (((float)look.Value.X) * preference.StickLookRate),
            y: (((float)look.Value.Y) * preference.StickLookRate)
        ) + motionLook);
        var looking = (
            look.IsActive ||
            seat.MotionControlsActive ||
            seat.FreeLooking ||
            seat.Orbiting ||
            seat.PointerSteering
        );

        // A body posed or teleported to a new heading carries a world-referenced look with it, ahead of this tick's nudge.
        if (route.Endpoint.TryEntityHeading(
            index: route.EntityIndex,
            heading: out var observedHeading,
            poseEpoch: out var observedEpoch
        )) {
            seat.View.ObserveBody(
                heading: observedHeading,
                poseEpoch: observedEpoch,
                routeEpoch: route.Epoch,
                yawReference: definition.Views.SeatControl.YawReference
            );
        }

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

            return;
        }

        if (
            looking || seat.View.FirstPersonLook ||
            (definition.Views.SeatControl.Follow is not { } follow) ||
            (!follow.WhileIdle && !seat.MovementHeld) ||
            !route.Endpoint.TryEntityHeading(
            index: route.EntityIndex,
            heading: out var heading
        )
        ) {
            return;
        }

        seat.View.Follow(
            targetYaw: heading,
            rate: follow.Rate,
            deltaSeconds: deltaSeconds
        );
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
            case WorldComposition.SetOperatorCamera free:
                m_composition.OperatorCamera = free.Pose;

                break;
            case WorldComposition.SelectCamera camera:
                m_composition.SelectedCamera = camera.Name;
                // Naming a camera, or auto, snaps back to the document's views, so the free camera yields.
                m_composition.OperatorCamera = null;

                break;
        }
    }
    /// <inheritdoc/>
    public void DeliverDefinition(WorldDefinition definition, WorldDocumentVersion version, in WorldStreamStamp stream) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // Adopt the definition and bump the delivery revision (one component of WriteRevision), so the frame source rebuilds
        // its program and re-reads scene/screens on its next capture. Poses still flow only through snapshots.
        var adopted = m_stateView.Adopt(delivered: definition);

        m_lastDelivered = definition;

        BeginVersion(version: version);
        m_channels = WorldChannelTable.For(definition: adopted);
        m_targets = WorldTargetRegisterTable.Compile(
            registers: adopted.TargetRegisters
        );
        m_definitionRevision++;
        m_pendingStateInstall = true;
        m_stateMirror.Install(
            engineTick: m_engineTick,
            tick: m_tick
        );
    }
    /// <inheritdoc/>
    public void DeliverPresentation(WorldDefinition definition, WorldPresentationRows rows, WorldDocumentVersion version, in WorldStreamStamp stream) {
        if (!rows.TryApply(Definition, out var changed)) { throw new InvalidOperationException("A presentation delivery must match the client's creation roster."); }
        _ = m_stateView.Adopt(delivered: changed, valuesFollow: true);
        m_lastDelivered = changed;
        BeginVersion(version);
        m_definitionRevision++;
    }
    /// <inheritdoc/>
    /// <remarks>A client is fed its own authority's fan-out (<see cref="WorldStreamStamp.Unspanned"/>), whose one span
    /// never ends, and never a spanned stream, so it receives no cut.</remarks>
    public void DeliverCut(in WorldStreamStamp stream, bool terminal) {
    }
    /// <inheritdoc/>
    /// <remarks>A client holds no span (see <see cref="DeliverCut"/>), so it has none to close.</remarks>
    public void CloseSpan(ulong stream) {
    }
    /// <inheritdoc/>
    public void DeliverSessionLever(WorldSessionLever lever) {
        // Already past the server's Mutate check on the lever's folded-into section (see WorldServer.ApplySessionLever),
        // so this only dispatches the write onto the presentation service the lever names.
        m_levers?.Apply(lever: lever);
    }

    private readonly WorldSolidScaleMirror m_solidScales = new();
    /// <summary>The local rig's delivered one-shot frames.</summary>
    public WorldPresentationCueFeed CueFrames { get; } = new();
    /// <summary>Observes the local seat's disclosed presentation events.</summary>
    public Action<WorldDefinition, WorldPresentationCueTick>? PresentCues { get; set; }
    private float m_solidScaleAlpha = 1f;

    /// <inheritdoc/>
    public float? SolidScale(string placementId) => m_solidScales.Read(alpha: m_solidScaleAlpha, placementId: placementId);
    /// <inheritdoc/>
    public void DeliverSnapshot(in WorldSnapshot snapshot, in WorldStreamStamp stream) {
        m_solidScaleAlpha = 1f;
        m_solidScales.Deliver(sizes: snapshot.SolidScales.Span);
        m_poses.Begin();

        // Field and document row cells are state the snapshot carries beside the document; the mirror re-reads the
        // rows they moved.
        var movedCells = m_stateView.ApplyFieldCells(
            deltas: snapshot.FieldCells.Span,
            moved: m_movedCells
        );

        if ((movedCells > 0) && !m_pendingStateInstall) {
            m_stateMirror.RefreshRows(ordinals: m_movedCells.AsSpan(
                length: movedCells,
                start: 0
            ));
        }

        // Another authority's snapshot moves every entity's address.
        m_bodyChangesWhole |= (m_authority != snapshot.Authority);

        foreach (ref readonly var entry in snapshot.Entries.Span) {
            var wasActive = ((((uint)entry.Index) < EntityCapacity) && m_poses.IsActive(index: entry.Index));

            if (!m_poses.TryAdmit(
                admission: out var admission,
                easeCorrections: true,
                entry: in entry
            )) {
                continue;
            }

            var index = entry.Index;

            // Everything a stamp pool reads of a body besides its look (BodyChanges): a step other than plain
            // double-buffering moved its pose epoch or its carries.
            if (
                (admission.Step != WorldPoseStep.Interpolated) ||
                (wasActive != entry.Active) ||
                (m_facts[index] != entry.Facts) ||
                (m_generation[index] != entry.Generation) ||
                (m_renderPosition[index] != entry.Position) ||
                (m_renderOrientation[index] != entry.Orientation)
            ) {
                _ = m_changedBodies.Add(index: index);
            }

            m_color[index] = entry.BodyColor;
            m_kit[index] = entry.Kit;
            m_look[index] = entry.Look;
            m_facts[index] = entry.Facts;
            m_stateView.ApplyBodyState(
                body: index,
                values: entry.State.Span
            );
            m_catalogRig[index] = entry.CatalogRig;
            m_generation[index] = entry.Generation;

            switch (admission.Step) {
                case WorldPoseStep.Seeded:
                    // Both endpoints now hold the fresh pose; any in-flight correction offset is dropped with the jump.
                    m_easers[index].Reset();

                    break;
                case WorldPoseStep.Corrected: {
                        // Authority snapped: ease the render error (last drawn tick pose minus authority) to zero over
                        // the window. Over-threshold corrections snap instead: the easer basis is the previous
                        // snapshot, which may lag same-tick multi-pose batches past the server's own snap-escape check.
                        var positionError = (admission.PriorPosition - entry.Position);

                        if (positionError.Length() > Definition.Motion.MaxSmoothError) {
                            m_easers[index].Reset();
                            m_poses.Discontinue(index: index);
                        } else {
                            m_easers[index].Begin(
                                positionError: positionError,
                                orientationError: Quaternion.Multiply(
                                    value1: admission.PriorOrientation,
                                    value2: Quaternion.Conjugate(value: entry.Orientation)
                                ),
                                seconds: entry.Continuity.Seconds
                            );
                        }

                        break;
                    }
                case WorldPoseStep.Carried:
                    // Any in-flight correction rides the crossing with the previous pose.
                    m_easers[index].Carry(carry: entry.Continuity.Carried!.Value);

                    break;
            }

            // Keep the resolved pose meaningful in a headless composition, where no presentation frame calls
            // UpdateRenderPoses. A windowed frame replaces these with its interpolated/eased values before drawing.
            m_renderPosition[index] = entry.Position;
            m_renderOrientation[index] = entry.Orientation;
        }

        // A body's action state is likewise carried beside the document, on its entry.
        var movedBodyState = m_stateView.TakeMovedBodyState(moved: m_movedBodyState);

        if ((movedBodyState > 0) && !m_pendingStateInstall) {
            m_stateMirror.RefreshRows(ordinals: m_movedBodyState.AsSpan(
                length: movedBodyState,
                start: 0
            ));
        }

        var peers = 0;
        var stepSeconds = ((float)FixedTickConversion.ToSeconds(ticks: snapshot.StepTicks));

        m_poses.Complete();
        m_posesDelivered = true;
        if (m_poses.ActiveChanged) {
            m_activeRevision++;
            // A body that left is in no snapshot entry, so the set that changed is not named body by body.
            m_bodyChangesWhole = true;
        }
        m_renderPoseRevision++;
        foreach (var index in m_poses.Seen) {
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
        m_engineTick = snapshot.EngineTick;
        StepTicks = snapshot.StepTicks;
        m_arrivalTimestamp = WorldDeliveryClock.Arrival;
        m_authority = snapshot.Authority;
        if (m_pendingStateInstall) {
            m_pendingStateInstall = false;
            m_stateMirror.Install(completingDelivery: true, engineTick: m_engineTick, tick: m_tick);
        } else {
            // A tick no state delivery refreshed still moves every bound cell whose value-over-time trait has not rested.
            m_stateMirror.Advance(engineTick: m_engineTick, tick: m_tick);
        }
        var cues = WorldPresentationCueFeed.Capture(snapshot);
        if (cues.Cues.Length != 0) { CueFrames.Publish(cues); }
        PresentCues?.Invoke(Definition, cues);
    }
    /// <inheritdoc/>
    /// <remarks>A value-only mutation cannot have changed channels, target registers, or scene shape: the fresh
    /// definition is stored for state-value reads, and the state mirror refreshes the slots bound to the rows the stamp
    /// names. A delivery that replaced, added or removed placement rows (a holding swap, a dealt child, a contribution)
    /// bumps the response revision, so the frame source composes again, or, when a moved row shows a creation
    /// that derives a face, a camera or a sound, <see cref="DefinitionRevision"/>, so the presentation reconciles them as
    /// well (<see cref="WorldPlacementResponse.ComparePlacements"/>).</remarks>
    public void DeliverState(WorldDefinition definition, WorldDocumentVersion version, in WorldStateStamp stamp, in WorldStreamStamp stream) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var previous = Definition;
        var previousDelivered = m_lastDelivered;
        var adopted = m_stateView.Adopt(delivered: definition, valuesFollow: (stamp.ArenaValues && (version.Activation == m_documentVersion.Activation)));

        m_lastDelivered = definition;
        switch (WorldPlacementResponse.ComparePlacements(after: adopted, before: previous, holdings: out _, solid: out var solid)) {
            case WorldPlacementChange.Appearance:
                // A sweep that moved only holdings leaves the composition to the scene, which presents a row whose every
                // response it keeps at hand by its copies and composes again only for the rest (WorldSceneEmitter). The
                // delivered rows are judged, since binding live state values rebuilds an adopted row's state references.
                var holdings = ((WorldPlacementResponse.ComparePlacements(after: definition, before: previousDelivered, holdings: out var held, solid: out _) == WorldPlacementChange.Appearance) && held);

                if (holdings) { m_holdingRevision++; } else { m_responseRevision++; }
                if (solid) { m_solidResponseRevision++; }

                break;
            case WorldPlacementChange.Facets:
                m_definitionRevision++;

                break;
        }

        BeginVersion(version: version);
        m_stateMirror.Refresh(notifyDelivery: false, stamp: in stamp);
    }

    // Records the version a document was delivered at. A different activation is a different world: the state mirror
    // begins a new lifetime, so what a presentation kept about the previous world's values does not carry over.
    private void BeginVersion(WorldDocumentVersion version) {
        if (version.Activation != m_documentVersion.Activation) {
            m_stateMirror.BeginLifetime();
            CueFrames.Clear();
        }

        m_documentVersion = version;
    }

    /// <summary>The complete durable address of the active occupant in a local slot.</summary>
    public WorldEntityAddress EntityAddress(int index) => new(
        Authority: m_authority,
        Index: index,
        Generation: m_generation[index]
    );
    /// <summary>The entity's authoritative fact mask from the latest snapshot — the presentation's gate for
    /// fact-keyed animation. An inactive index reports whatever its last snapshot carried.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public BodyFacts Facts(int index) => m_facts[index];
    /// <summary>Whether the entity is drawn this frame (present in the latest snapshot).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public bool IsActive(int index) => m_poses.IsActive(index: index);
    /// <summary>The look row an entity wears: the delivered look table indexed by <see cref="LookIndex"/>, or the
    /// implicit single catalog look when the world authors no <c>looks</c> section, and for an index the delivered
    /// table cannot cover. The scene emitter and the part resolver read appearance through this one resolve, so a
    /// stamped part and the body it hangs off can never disagree about which row they are wearing.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The entity's look row.</returns>
    public WorldLook Look(int index) => WorldDefinitionRows.ResolveLook(
        rows: Definition.Looks,
        index: LookIndex(index: index)
    );
    /// <summary>Gets the entity's kit row index from the latest snapshot, which selects the collider a chase camera keeps
    /// its eye out of.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns>The index into the delivered definition's kit rows.</returns>
    public int Kit(int index) => m_kit[index];
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
    public string? PlacementId(int index) => m_poses.PlacementId(index: index);
    /// <summary>The entity's pose-discontinuity counter — bumped on activation, a <see cref="EntityContinuityKind.Teleport"/>,
    /// and an over-threshold <see cref="EntityContinuityKind.Correction"/> snap. A presentation-side second-order
    /// follower reseeds whenever this changes, so it never chases across a jump.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public int PoseEpoch(int index) => m_poses.PoseEpoch(index: index);

    /// <inheritdoc/>
    public WorldPoseCarries Carries => m_poses.Carries;

    /// <summary>The entity's per-frame render position (interpolated and correction-eased).</summary>
    /// <param name="index">The 0-based entity index.</param>
    public Vector3 Position(int index) => m_renderPosition[index];
    /// <summary>Publishes the static-scene query field every presentation consumer shares.</summary>
    /// <param name="field">The evaluator built over the composed program's static layers, or <see langword="null"/>
    /// when the program admits no fixed-point query.</param>
    public void PublishStaticField(SdfFieldEvaluator? field) => m_staticField = field;
    /// <summary>Submits each joined, active seat's device intent (and live-held lane image) for this tick — the
    /// client's per-tick outbound half, run immediately before the server step. A pending seat submits nothing (its
    /// inputs drive the profile picker, not locomotion), and a seat submits only under
    /// <see cref="IntentSource.Live"/> — off-Live the devices are inert and the server-side source fills the gaps.
    /// The submission's acting identity is <see cref="PlayerRoster.PrincipalOf"/> — the slot's own
    /// <see cref="Principal.Seat"/> ordinarily, or whatever identity a <see cref="PlayerRoster.TryClaimSlot"/> call
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
        var routes = m_submissionRoutes;

        Array.Clear(array: routes);

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
                    intent: seat.ConsumeRelative(intent: seat.HeldIntent())
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

            if (seat.Input is not { Admitted: true } input) { continue; }
            endpoint.Submissions.SubmitIntent(submission: input.Publish(m_roster.PrincipalOf(slot: slot), targets[slot], tick, seat, intent));
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

        var register = Definition.TargetRegisters[registerIndex];
        var halfAngle = register.MaximumHalfAngleDegrees;
        var range = FixedQ4816.FromDouble(value: register.MaximumRange);
        var minimumDot = FixedQ4816.Cos(angle: FixedQ4816.FromDegrees(degrees: halfAngle));
        var origin = FixedVector3.FromVector3(value: m_poses.CurrentPosition(index: sourceBody));
        var forward = FixedVector3.FromVector3(value: Vector3.Transform(
            value: -Vector3.UnitZ,
            rotation: m_poses.CurrentOrientation(index: sourceBody)
        ));
        var nearest = FixedQ4816.MaxValue;
        var found = -1;

        // The live bodies in ascending order, so the nearest of two equally near candidates is the lower index.
        foreach (var index in LiveBodies) {
            if (
                (index == sourceBody) ||
                !m_poses.IsActive(index: index)
            ) {
                continue;
            }

            var candidate = FixedVector3.FromVector3(value: m_poses.CurrentPosition(index: index));

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
    public bool TryInhabitantBody(string placementId, out int index) => m_poses.TryInhabitant(
        index: out index,
        placementId: placementId
    );
    /// <inheritdoc/>
    public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
        if (
            (((uint)anchorId) >= EntityCapacity) ||
            !m_poses.IsActive(index: anchorId)
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
    /// zero sub-steps previous == current, so both hold stably (no snap-back). Presentation only: <c>body.where</c>
    /// reports the server sim pose, never this.</summary>
    /// <param name="alpha">How far this frame sits between the last and next fixed sim step, in <c>[0, 1)</c>.</param>
    /// <remarks>The first frame after a delivery resolves every live body and notes which of them moved between the
    /// delivery's two tick poses. A later frame of the same delivery resolves only those: a body whose two poses are
    /// equal resolves to the same pose at every alpha (<see cref="WorldPoseInterpolation"/>), and a correction eases
    /// once a delivery, never between two, so its pose stands until the next delivery. A frame therefore costs the
    /// bodies that moved, and a delivery the bodies that are live, never <see cref="EntityCapacity"/>.</remarks>
    public void UpdateRenderPoses(float alpha) {
        m_solidScaleAlpha = alpha;
        WorldPoseWork.Process.Count(kind: WorldPoseWork.Frames);

        var changed = false;

        if (!m_posesDelivered) {
            foreach (var index in m_moving.AsSpan(start: 0, length: m_movingCount)) {
                _ = ResolveRenderPose(index: index, alpha: alpha, changed: ref changed);
            }
            if (changed) {
                m_renderPoseRevision++;
            }
            WorldPoseWork.Process.Add(kind: WorldPoseWork.Resolved, amount: m_movingCount);

            return;
        }

        var live = m_poses.Live;

        if (m_moving.Length < live.Length) {
            m_moving = new int[((int)System.Numerics.BitOperations.RoundUpToPowerOf2(value: ((uint)live.Length)))];
        }

        var moving = 0;
        var resolved = 0;

        foreach (var index in live) {
            if (!m_poses.IsActive(index: index)) {
                continue;
            }

            resolved++;
            if (ResolveRenderPose(index: index, alpha: alpha, changed: ref changed)) {
                m_moving[moving++] = index;
            }
        }
        if (changed) {
            m_renderPoseRevision++;
        }

        m_movingCount = moving;
        m_posesDelivered = false;
        WorldPoseWork.Process.Add(kind: WorldPoseWork.Resolved, amount: resolved);
    }

    // The bodies whose presented inputs moved since the journal's last frame (a delivery's entries and the poses a
    // frame resolved), and whether the set cannot be named body by body.
    private readonly Puck.Abstractions.Collections.MarkedIndexSet m_changedBodies = new();
    private bool m_bodyChangesWhole = true;
    private readonly Puck.Abstractions.Gpu.GpuChangedRows m_bodyChanges = new(stableRows: true);

    /// <inheritdoc/>
    /// <remarks>A journal frame names the bodies whose render pose <see cref="UpdateRenderPoses"/> resolved to another
    /// value, and those a delivery moved the pose, pose epoch, generation, carries, facts or active flag of. A frame
    /// that changed which bodies are active, or another authority's first snapshot, owes every body.</remarks>
    public Puck.Abstractions.Gpu.GpuRowChanges BodyChanges => m_bodyChanges.Reader;
    /// <inheritdoc/>
    /// <remarks>Reading it closes a journal frame over whatever moved since the last read, so the first consumer of a
    /// frame publishes the changes and the rest read the same serial, whether or not a presentation frame resolved
    /// poses in between (a composition with no presentation delivers and packs without one).</remarks>
    public long BodyChangesSerial {
        get {
            if (m_bodyChangesWhole || (m_changedBodies.Count > 0)) {
                m_bodyChanges.Begin(everything: m_bodyChangesWhole, rows: EntityCapacity);
                if (!m_bodyChangesWhole) {
                    foreach (var index in m_changedBodies.Members) {
                        m_bodyChanges.Owe(count: 1, start: index);
                    }
                }

                m_changedBodies.Clear();
                m_bodyChangesWhole = false;
            }

            return m_bodyChanges.Serial;
        }
    }
    /// <inheritdoc/>
    /// <remarks>Reads the pose table's live set (<see cref="WorldPoseTable.Live"/>), never the capacity.</remarks>
    public void CopyActiveBodies(List<int> bodies) {
        ArgumentNullException.ThrowIfNull(argument: bodies);

        foreach (var index in LiveBodies) {
            if (m_poses.IsActive(index: index)) {
                bodies.Add(item: index);
            }
        }
    }

    // The pose table's live set in ascending order, sorted again only when a delivery changed which bodies are live.
    private readonly List<int> m_liveBodies = [];
    private long m_liveBodiesRevision = -1L;

    /// <summary>Gets the index of every body that may be active, in ascending order: the pose table's live set
    /// (<see cref="WorldPoseTable.Live"/>), which a body a snapshot carried inactive stays in, so a reader still asks
    /// <see cref="IsActive"/>. What a walk of the active bodies visits in place of every index of
    /// <see cref="EntityCapacity"/>; valid until the next delivery.</summary>
    public ReadOnlySpan<int> LiveBodies {
        get {
            if (m_liveBodiesRevision != m_activeRevision) {
                m_liveBodiesRevision = m_activeRevision;
                m_liveBodies.Clear();
                foreach (var index in m_poses.Live) {
                    m_liveBodies.Add(item: index);
                }

                m_liveBodies.Sort();
            }

            return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_liveBodies);
        }
    }

    // The live bodies whose two tick poses differ in the latest delivery (the only ones a frame between two deliveries
    // interpolates), and whether a delivery has arrived since a frame last resolved every live body.
    private int[] m_moving = [];
    private int m_movingCount;
    private bool m_posesDelivered = true;

    // Resolves one body's render pose at alpha, sets changed when the pose it stores differs from the one held, and
    // returns whether the body's two tick poses differ.
    private bool ResolveRenderPose(int index, float alpha, ref bool changed) {
        var previousPosition = m_poses.PreviousPosition(index: index);
        var currentPosition = m_poses.CurrentPosition(index: index);
        var previousOrientation = m_poses.PreviousOrientation(index: index);
        var currentOrientation = m_poses.CurrentOrientation(index: index);
        var position = WorldPoseInterpolation.Position(
            previous: previousPosition,
            current: currentPosition,
            alpha: alpha
        );
        // Quaternion.Lerp is the nlerp: shortest-path dot-sign flip and renormalize.
        var orientation = WorldPoseInterpolation.Orientation(
            previous: previousOrientation,
            current: currentOrientation,
            alpha: alpha
        );

        m_easers[index].Apply(
            orientation: ref orientation,
            position: ref position
        );
        if ((m_renderPosition[index] != position) || (m_renderOrientation[index] != orientation)) {
            m_renderPosition[index] = position;
            m_renderOrientation[index] = orientation;
            changed = true;
            _ = m_changedBodies.Add(index: index);
        }

        return ((previousPosition != currentPosition) || (previousOrientation != currentOrientation));
    }
    /// <summary>Writes this client's program-rebuild watch counters side by side, never added together: the seat-metadata
    /// revision (colors, pending state), the server's declared-set revision from the latest snapshot, the
    /// definition-delivery revision and the response revision.
    /// <para>
    /// The server term is not monotonic — it is assigned from <c>snapshot.Revision</c>, so it can move down. A sum of
    /// these would therefore stall on a real change whenever the server revision fell by exactly as much as
    /// another term rose, holding a stale program with nothing to report it. Keeping them apart lets the composition
    /// host's componentwise compare see each one move on its own (see <see cref="ISdfSceneEmitter.WriteRevision"/>).
    /// </para></summary>
    /// <param name="destination">The exactly-<see cref="RevisionComponentCount"/>-long span to fill.</param>
    public void WriteRevision(Span<int> destination) {
        destination[0] = m_roster.Revision;
        destination[1] = m_serverRevision;
        destination[2] = m_definitionRevision;
        destination[3] = m_responseRevision;
    }

    // The correction error-smoothing state, one per entity, with a Begin/Decay/Apply/Reset lifecycle. Presentation
    // only — the sim never reads it, and it is never part of the pose flowing out to body.where. A
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
        // orientation error E, its angle scaled by the weight (VectorFunctions.ScaleTurn), is applied outermost (world
        // space) to the attitude, so the on-screen craft glides from where it was to authority. A no-op once the window
        // drains.
        public readonly void Apply(ref Vector3 position, ref Quaternion orientation) {
            if (
                (m_remaining > 0f) &&
                (m_window > 0f)
            ) {
                // Smoothstep ease: weight is 1 at receipt (craft sits at its old pose) and eases to 0 as the window drains
                // (craft arrives at authority), with a soft start and a soft settle. fraction = remaining/window runs 1→0.
                var fraction = (m_remaining / m_window);
                var weight = VectorFunctions.Smoothstep(t: fraction);

                position += (m_position * weight);
                // At weight 1 the render attitude is E · interpolated = the old attitude; at weight 0 it is the
                // interpolated (authoritative) attitude — the angular twin of the position offset's decay.
                orientation = (VectorFunctions.ScaleTurn(
                    fraction: weight,
                    turn: m_orientation
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
        // Turn and scale any in-flight offset with an in-place crossing, so it eases on in the carried frame.
        public void Carry(WorldPoseCarry carry) {
            if (m_remaining <= 0f) {
                return;
            }

            var rotation = carry.Rotation;

            m_position = (carry.Direction(direction: m_position) * carry.Scale);
            m_orientation = Quaternion.Normalize(value: ((rotation * m_orientation) * Quaternion.Conjugate(value: rotation)));
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

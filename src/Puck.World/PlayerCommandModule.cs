using System.Globalization;
using System.Runtime.CompilerServices;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The players' console surface: the verbs a piped script (or the on-screen console) drives the avatars with, the two
/// stick routers, and the channel-generic verbs (<see cref="ChannelVerbs"/>) the keyboard binding table targets —
/// one auto-registered command per fixed channel ordinal, so a channel verb never depends on which destination
/// world's channel names exist at boot. The drive-a-body verbs (<c>fly</c> / <c>pose</c> / <c>where</c> /
/// <c>stop</c>) take an optional trailing body index reaching the whole population (0..4095, default body 0):
/// 0..3 resolve to the local roster seats, 4..4095 to the population's simulated entries (each owning its own
/// <see cref="WorldBody"/> sim) — the SAME 0-based body index <c>world.grant body:&lt;n&gt;</c> already addresses,
/// so a script never converts between two numberings for the same entity. A non-local entity is only ever sent
/// inputs (a fly/pose is a command producing intents or a teleport, never a pose stream). The channel verbs carry no
/// body-index argument at all: a bound control targets whichever local seat's device dispatched it (the recorded
/// logical slot — see the class remarks below), and a typed invocation with no device defaults to body 0. The
/// roster-management verbs (<c>join</c> / <c>leave</c> / <c>profile</c> / <c>assign</c>) and every seat-presentation
/// verb (<c>mode</c> / <c>camera</c> / <c>bind</c> / …) stay seat-scoped and 1-based (<c>seat 1..4</c>) — a human
/// operator's ordinal, distinct from the entity index it happens to share with a local body. Mutations are
/// simulation-routed and applied from the tick snapshot immediately before <see cref="WorldSimulation"/> advances;
/// read-only inspection sees the last completed tick.
/// </summary>
/// <remarks><para>The stick channels (<c>player.move</c> / <c>player.move.strafe</c> / <c>player.look</c> / <c>player.look.steer</c>) are not polled: their bindings fire on the
/// default active phase, and the snapshot router re-dispatches carried analog values each tick. Handlers route each
/// dispatch by its recorded logical slot; the local device id is consulted only when a previously unseen live device
/// first needs seating.</para>
/// <para><c>join</c>/<c>leave</c>/<c>fly</c>/<c>stop</c>/<c>where</c>/<c>pose</c> also accept a trailing
/// <c>instance:&lt;name&gt;</c> token (see <see cref="TryStripInstanceToken"/>), addressing a named running
/// <see cref="WorldInstance"/> (<see cref="WorldInstanceHost"/>) instead of the boot world. In the instance form, a
/// slot is always the 1-based local seat 1..<see cref="WorldBodiesLimits.LocalSeatCount"/> (never a population
/// entry, and never defaulted — a bare <c>instance:&lt;name&gt;</c> with no slot is refused), and <c>join</c>'s
/// "next free slot"/either-order profile-then-slot convenience does not apply there.</para>
/// </remarks>
internal sealed partial class PlayerCommandModule(PlayerRoster roster, WorldPopulation population, WorldScreenBinder screens, WorldDefinition definition, IServerLink link, WorldServer server, WorldPerceptionAnchor anchor, WorldClient client, Func<InputRouter> router, WorldInstanceHost instances, WorldSeatBindings seatBindings, WorldSeatAuthorityRouter seatRouter, WorldReplayTape tape, WorldStampPool stamps) : ICommandModule {
    public const string AssignCommand = Puck.World.Client.PlayerCommandNames.AssignCommand;
    /// <summary>The keyboard-claim command (Keyboard F1..F4, press edge). The target slot rides the binding's Axis1D
    /// value as a 1-based player number, the clean scalar constant a binding carries.</summary>
    public const string ClaimCommand = Puck.World.Client.PlayerCommandNames.ClaimCommand;
    /// <summary>The confirm command (Gamepad South / Keyboard Enter, press edge) — promotes the pending player owning
    /// the pressing device.</summary>
    public const string ConfirmCommand = Puck.World.Client.PlayerCommandNames.ConfirmCommand;
    /// <summary>The device-cycle command (Gamepad Start, press edge) — rotates the pressing device to the next slot.</summary>
    public const string CycleCommand = Puck.World.Client.PlayerCommandNames.CycleCommand;
    /// <summary>The free-orbit Axis2D look command. Standard binds the right stick to <see cref="LookSteerCommand"/>
    /// instead; authors retain this command when camera-only orbit is desired.</summary>
    public const string LookCommand = Puck.World.Client.PlayerCommandNames.LookCommand;
    /// <summary>The held modifier that makes a steering look sample camera-only.</summary>
    public const string FreeLookCommand = Puck.World.Client.PlayerCommandNames.FreeLookCommand;
    /// <summary>The Axis2D look command whose yaw also faces the body along the camera's planar look direction.</summary>
    public const string LookSteerCommand = Puck.World.Client.PlayerCommandNames.LookSteerCommand;
    public const string OrbitCommand = Puck.World.Client.PlayerCommandNames.OrbitCommand;
    public const string SteerCommand = Puck.World.Client.PlayerCommandNames.SteerCommand;
    /// <summary>The look-swap command: turns the seat camera a half-turn about the body.</summary>
    /// <summary>The look-recenter command: turns the seat camera round behind the body.</summary>
    public const string RecenterLookCommand = Puck.World.Client.PlayerCommandNames.RecenterLookCommand;
    /// <summary>The generic sensor-input mode toggle.</summary>
    public const string MotionControlsCommand = Puck.World.Client.PlayerCommandNames.MotionControlsCommand;
    /// <summary>The Axis3D angular-velocity input for motion controls.</summary>
    public const string MotionAngularCommand = Puck.World.Client.PlayerCommandNames.MotionAngularCommand;
    /// <summary>The movement-facing Axis2D command (+Y forward, +X strafe right). The handler routes the dispatch to
    /// the owning device's player; standard binds its left stick to <see cref="MoveStrafeCommand"/> instead.</summary>
    public const string MoveCommand = Puck.World.Client.PlayerCommandNames.MoveCommand;
    /// <summary>The no-token Free Cam toggle: <c>player.camera [seat]</c>.</summary>
    public const string CameraCommand = Puck.World.Client.PlayerCommandNames.CameraCommand;
    /// <summary>The generic per-seat mode-family flip: <c>player.mode &lt;family&gt; &lt;state&gt; [seat]</c>, or
    /// <c>player.mode &lt;family&gt; [seat]</c> to read back the seat's current state.</summary>
    public const string ModeCommand = Puck.World.Client.PlayerCommandNames.ModeCommand;
    /// <summary>The live-camera-framed Axis2D movement command that preserves heading so lateral input strafes and
    /// forward travel turns with look yaw.</summary>
    public const string MoveStrafeCommand = Puck.World.Client.PlayerCommandNames.MoveStrafeCommand;

    private readonly PlayerRoster m_roster = roster;
    private readonly WorldPopulation m_population = population;
    private readonly WorldClient m_client = client;
    private readonly WorldScreenBinder m_screens = screens;
    private readonly WorldDefinition m_definition = definition;
    private readonly IServerLink m_link = link;
    private readonly WorldServer m_server = server;
    private readonly WorldPerceptionAnchor m_anchor = anchor;
    private readonly WorldInstanceHost m_instances = instances;
    private readonly WorldReplayTape m_tape = tape;
    // The creation-stamp pool, read (never stepped) by body.rig: the driver/effector state is presentation-side and
    // exists nowhere else, so this is the one door onto it.
    private readonly WorldStampPool m_stamps = stamps;
    // The BOOT world's compiled channel table — name→ordinal resolution for body.press and PickerDirection's
    // pre-join Turn-role check. Validation has already run by the time a WorldDefinition reaches here, so every
    // declared name resolves. NEVER the source a bound channel verb dispatches against — see m_seatBindings.
    private readonly WorldChannelTable m_channels = WorldChannelTable.Compile(channels: definition.Channels);
    // The per-seat CURRENTLY ROUTED channel vocabulary (WorldSeatBindings.Channels, kept in sync with each seat's
    // WorldSeatAuthorityRouter claim by WorldSimulation's post-step sync). WorldSeatBindings lowers an authored
    // channel NAME through that table to one of the fixed ordinal commands registered below; ChannelVerb checks the
    // same table again when the bound control fires. The command registry and its replay-stable ids therefore never
    // depend on which local, late-mounted, or remote destination documents happened to be readable at boot.
    private readonly WorldSeatBindings m_seatBindings = seatBindings;

    // The authored, argument-bearing verbs (assertable on stdout). The drive-a-body verbs take an optional trailing
    // body index reaching the whole population: 0..3 are the local seats, 4..4095 the simulated entries.
    private IEnumerable<CommandDefinition> AuthoredVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.reconcile",
            description: "Applies a smoothed SERVER CORRECTION to a body: body.reconcile <x> <z> <yawDegrees> [seconds] [body]. The SIM pose snaps to the target INSTANTLY (identical end-state to a body.pose ground-plane + heading update), while the on-screen avatar EASES from where it was to the authoritative pose over [seconds] (default 0.25, clamped 0.05..2) — the AAA error-smoothing shape a real server uses. A correction larger than the snap-error ceiling pops instead of gliding. The optional trailing body index is 0..4095 (default 0) — 0..3 local seats, 4..4095 simulated entries. The eased offset is presentation-only: body.where still reports the snapped SIM pose.",
            handler: ReconcileHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.where",
            description: "Echoes a body's FULL 6DOF pose and its authoritative fact mask — [body.where: body:<n> pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd° facts=grounded|climbing home=(x.xx, y.yy, z.zz) scale=s.ss com=(x.xx, y.yy, z.zz)] — so a piped run can assert it moved or changed regime: facts= is the lower-case, |-joined set of the body facts the simulation's own action gates read (grounded, airborne, rising, falling, inmedium, atmediumband, climbing, flying, resting), or none. scale= is the body's live scale multiplier (1.00 unless bodies.scaleRow names a state row carrying this body's own cell) — the collider, resolved move speed, turn rate, and hold probes all read the same value. com= trails only for a rigid-kit body — its live centre of mass, which orbits away from pos= (always the root) for a rolling or tumbling body. body.where [body] (optional body index 0..4095, default 0 — 0..3 local seats, 4..4095 simulated entries). A grounded entity prints pitch=0 roll=0; its y= reads the local ground height under it, not necessarily 0.00 (the garden's deck top sits at y=-0.50). A LOCAL seat's echo also carries anchor=body:<n> — the 0-based body index that seat's presentation (camera eye, audio listener, seat.<n>.position.* HUD bindings) derives from: the seat's bound body, or the routed body while possessing (a Control route targeting a body with capture on). A trailing instance:<name> token reads OUT OF a NAMED running instance's OWN tick snapshot instead — body.where <slot> instance:<name> (slot REQUIRED, 1..WorldBodiesLimits.LocalSeatCount, the instance form's own 1-based seat convention); no anchor rides that form (a spawned instance's seat has no client perceiving from it).",
            handler: WhereHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.rig",
            description: "Echoes a body's live creation-look rig state — the presentation-side animation decisions nothing else can read: [body.rig: body:<n> creation=<id> speed=s.ss drivers=<count> effectors=<count> driver:<name> phase=p.pp weight=w.ww … effector:<name> weight=w.ww planted=yes|no target=(x.xx, y.yy, z.zz)|none …]. speed= is the eased rendered speed the moving/still gate tokens test; a driver's phase= is radians in [0, 2pi) and weight= its eased gate value; an effector's target= is the WORLD point its tip is being asked for this frame, so a scripted run can fence twice and assert a planted foot's target is unchanged while body.where moved. An effector whose chain resolved to too few bones prints bones=<count> and is inert. body.rig [body] (optional body index 0..4095, default 0 — 0..3 local seats, 4..4095 simulated entries). A body that renders through the procedural catalog rig rather than a creation look says so plainly.",
            handler: RigHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.channels",
            description: "Echoes the channel decision read-back for a body — per DECLARED channel, the fold's value and owning-seat base, the held overlay admitted later and its composed result, every fold contributor tagged by principal (trusted/untrusted), the pool ceiling in force, and whether the pool actually clamped this write: body.channels [body] (optional body index 0..4095, default 0 — 0..3 local seats, 4..4095 simulated entries). The fold only ever exists over a human-occupied local seat; any other target reports that plainly rather than fabricating a base/pool.",
            handler: ChannelsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.state",
            description: "Echoes every named action-state slot, including kind, lifetime, exact stored value, body identity, and writes emitted by the most recently completed tick: body.state [body].",
            handler: StateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.designate",
            description: "Proposes a target for one authored target register: body.designate <register> <body:n|nearest|at:x,y,z> [body]. 'nearest' resolves client-side from the latest snapshot inside the body's clamped forward cone; 'at:x,y,z' proposes a world-space point (the seek target a Designated-source producer steers to). The server re-resolves activity, authority, range, cone, targetability, and line of sight (a point checks the same envelope, minus body activity) before writing. Returns target read-back under the body.designate prefix, including the latest refusal.",
            handler: DesignateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.targets",
            description: "Echoes every authored target register, its current body subject, applied/authored cone, line-of-sight requirement, and the latest designation refusal: body.targets [body].",
            handler: TargetsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "body.state-load",
            description: "Stages one durable action-state input for the next simulation tick: body.state-load <name> <counter-value|timer-seconds> [body]. The command is tick-stamped and recorded on the replay authority tape.",
            handler: StateLoadHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.stop",
            description: "Stops a body's avatar dead: clears its whole tape, releases every held movement key, cancels every in-flight timed press (body.press hold — materialized or still pending), AND clears any BindingEntryMode.Toggle latch the seat carries (a toggled-on channel does not survive a stop) — the panic verb: body.stop [body] (optional body index 0..4095, default 0 — 0..3 local seats, 4..4095 simulated entries; stopping a population entry drops its tape so its wander resumes). Echoes the true released/cleared/toggle counts, or, if the Drive gate refused the command (e.g. CC/death), a refusal naming the denial — never an affirmative quoting a stale count. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — body.stop <slot> instance:<name> (slot REQUIRED, 1..WorldBodiesLimits.LocalSeatCount) — echoing a bare \"tape cleared\", since no client mirrors that seat's held-key/toggle state.",
            handler: StopHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.motion",
            description: "Sets or echoes a body's declared motion program: body.motion [program] [body]. With no program it echoes the current selection. A switch is authoritative and may re-constrain the pose. The optional trailing body index is 0..4095 (default 0).",
            handler: MotionHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.fly",
            description: "Enqueues a six-role timed segment on a body's tape: body.fly <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> [body] — each channel a float clamped to [-1,1], held for <seconds>. The body's authored motion program decides which roles it reads: ApplyHold consumes up through a hold row's own thrust alongside its gravity arc, while IntegrateLocalAttitude + ComputeLocalTargetVelocity provide body-frame 6DOF. This is the ONE scripted-tape verb: a planar segment is this verb with up/pitch/roll zeroed — body.fly <forward> <strafe> 0 <turn> 0 0 <seconds>. The optional trailing body index is 0..4095 (default 0) — 0..3 local seats, 4..4095 simulated entries.",
            handler: FlyHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.pose",
            description: "Teleports a body to a full 6DOF pose: body.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> [body] (yaw about world up, pitch about the body right, roll about the body forward; 0/0/0 = level facing -Z). ANY of the six positional values may be - to HOLD that axis at its current value instead of setting it — body.pose - - - 90 - - 0 turns body:0 to face 90° with its position and pitch/roll untouched; body.pose 3 - 5 - - - 0 moves body:0 on the ground plane only. The held axes are read from the SAME live pose this call resolves and folded into ONE atomic SnapPose submission — never a read-then-write pair, so nothing can move the body between the read and the write. A hard teleport (sim snap + previous-pose reset + render-error clear). A grounded entity re-pins Y and levels on its next step. The optional trailing body index is 0..4095 (default 0) — 0..3 local seats, 4..4095 simulated entries. The SPAWN form — body.pose spawn:<id> [body] — poses the body at a declared spawnPoints row (its position and yawDegrees, pitch and roll zero), the console mirror of a rule's pose effect naming a spawnPoint; an undeclared id refuses by name. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — body.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> <slot> instance:<name> (slot REQUIRED, 1..WorldBodiesLimits.LocalSeatCount) — the SAME hold-current axes apply, read from that instance's own live pose.",
            handler: PoseHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.impulse",
            description: "Applies an instantaneous world-space impulse to a rigid-kit body's linear velocity: body.impulse <x> <y> <z> [body] (Δv = impulse / mass). Refused by name for a body whose kit carries no 'rigid' facet — see world.rigid. The optional trailing body index is 0..4095 (default 0); local population only, no instance:<name> routing.",
            handler: ImpulseHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.carry",
            description: "Begins one body carrying another: body.carry <carrier> <target> (both body indices REQUIRED and explicit — no optional-trailing default). The carrier's kit must author a carry facet and the target a rigid one; refused by name when either body is already a party to another carry relationship, the target sits outside the carrier's own live-scaled reach, or the target's own live-scaled mass exceeds the carrier's live-scaled carry ceiling. On success the target's pose and rigid velocity are derived from the carrier's frame every tick — see body.where's carrying=/carriedBy= read-back — and its own rigid integration is suspended until body.release.",
            handler: CarryHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.release",
            description: "Ends a body's active carry, if any: body.release [body] (optional trailing body index, default 0). The released body re-enters the rigid solver carrying the carrier's own current velocity. A friendly no-op refusal when the body is not carrying anything.",
            handler: ReleaseHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.press",
            description: "Presses ANY declared channel — movement roles included — for a timed auto-release: body.press <channel> [value] [holdSeconds] [body]. <channel> is a name from world.affordances' channels section; [value] defaults to the shape's max (1) and is validated against the channel's shape (binary: 0 or 1; unipolar: [0,1]; bipolar: [-1,1]); [holdSeconds] how long it reads held (default a short host-step-derived tap, bounded authoritatively by the deciding Drive grant's hold:<seconds> policy — WorldGrant.DefaultHoldSeconds=2 absent an explicit row — and the 60-second engine backstop). The echo names the TRUE outcome, never an assumed one: a non-positive [holdSeconds] is ignored outright (echoed plainly, no cap blamed); a request either cap truncates echoes the EFFECTIVE hold and names whichever cap structurally bound it (the grant's hold budget, or the engine's hold ceiling when the grant permits the full backstop and the request still exceeds it); a refused command (e.g. a CC/death Drive gate) echoes the refusal, never an affirmative; under no cap and no refusal the echo is unchanged. A press carrying a DIFFERENT value than an ordinal's in-flight hold (materialized or still pending) replaces it outright (its own duration), so an opposing press is never silently swallowed by a longer hold's remaining ticks; a re-press carrying the SAME value only ever extends. [body] the trailing index 0..4095 (default 0 — 0..3 seats, 4..4095 population). The press is INDEPENDENT of the movement tape, so body.fly … then body.press jump fires a runner mid-segment. On a composition channel, what the press DOES is the target's kit binding (the default world's grounded kits bind the vertical impulse via \"jump\" — a short hold = short hop, a long hold = full arc via variable height; an unbound channel leaves it inert). There is no sugar verb for a bound button — the bound control rides its own channel-generic command (see world.affordances); this is the scripted/wire twin reaching every channel.",
            handler: PressHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.control",
            description: "Sets or echoes a body's INTENT SOURCE — what fills its intent gaps between tape segments: body.control [live|idle|producer:<name>] [body]. 'live' admits the submitted device stream; 'idle' masks it so a tape gap holds still; 'producer:<name>' runs that producer program from the target kit before motion. Tapes and body.press still drive under every source. Any switch releases held keys/lanes so nothing bursts. With no mode it echoes the current source. A pending seat's source cannot be set. The optional body index is 0..4095 (default 0). world.population sweeps all peers' sources.",
            handler: ControlHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.engage",
            description: "ROUTES a body's intent onto a TARGET — a diegetic screen (the classic UX) or another body (possession): body.engage <screen>|body:<n> [capture:on|off] [body] — [capture:on|off] defaults to on (today's behavior: the source avatar idles); capture:off MIRRORS instead — the target still receives the routed channels every tick while the source avatar keeps moving under its own input. [body] is the trailing index 0..4095 (default 0). On a SCREEN target the resolved per-frame intent (tape/press/held keys alike) is translated to joypad buttons and delivered to the screen's booted machine; the screen must be declared engageable, carry a booted machine (screen.insert first), and — when its route sets an engage radius — the body's avatar must be within it (body.pose up first); multiple bodies engaged on one screen OR-merge their buttons (the multiplayer cabinet). On a BODY target the routed channels reach the target through the ordinary co-drive contribution path — the actor must ALSO hold Drive over the target body (world.grant seatN drive body:<n>) for anything to actually move; a route alone confers no Drive authority. Route only — orthogonal to body.control.",
            handler: EngageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "body.disengage",
            description: "DISENGAGES a body from its screen so its intent drives its avatar again: body.disengage [body] (optional index 0..4095, default 0). Drops any live held keys/lanes so nothing leaks across the boundary (the avatar does not burst into motion). A friendly no-op echo when the body was not engaged.",
            handler: DisengageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.join",
            description: "Joins a player: player.join [n] joins a PENDING player (a profile is chosen, then confirm) — with no index the next free slot, n (2..4) that specific slot. player.join <profile> [n] joins directly ACTIVE on a named profile (a token in 2..4 is a slot, otherwise a profile name; either order). No device is attached (the console is a network-shaped source), so a piped script builds a quad session. Echoes the roster. A trailing instance:<name> token addresses a NAMED running instance instead of the boot world — player.join <slot> [identity] instance:<name> (slot is REQUIRED, 1..WorldBodiesLimits.LocalSeatCount, never auto-picked; no either-order profile/slot convenience there).",
            handler: JoinHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.leave",
            description: "Removes a scripted or pad player: player.leave <n> (n in 2..4), unmapping its devices and freeing its profile. Player 1 never leaves. Echoes the resulting roster. A trailing instance:<name> token addresses a NAMED running instance instead — player.leave <slot> instance:<name> (slot 1..WorldBodiesLimits.LocalSeatCount, seat 1 included — the boot form's \"player 1 never leaves\" rule is a roster policy that does not apply to an instance's own seat table), which REAPS the instance on empty.",
            handler: LeaveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.identity",
            description: "Sets a specific owned-world identity on a player and confirms it: player.identity <name> [n].",
            handler: ProfileHandler
        );
        yield return PlayerAssignmentCommand.Create(roster: m_roster);
    }
    // The success-echo tail every side-effecting wire verb shares: echo the formatted line when acks are on, else drop
    // it (CommandResult.None). On a quiet pipe (args.Echo false) the EchoHandler skips every format append and no ack
    // string is built — the zero-alloc flood contract. Formats under the invariant culture, so echoes are locale-stable.
    private static CommandResult Echoed(in WireArgs args, [InterpolatedStringHandlerArgument(nameof(args))] ref EchoHandler handler) {
        return (args.Echo
            ? new CommandResult(Output: handler.ToStringAndClear())
            : CommandResult.None
        );
    }
    // Whether a drive verb's resolved target is a local seat — seats carry client-side device state (held keys/lanes,
    // the possession latch copy) that some commands must also touch.
    private bool IsSeat(int index) => ((uint)index < (uint)m_population.LocalSeatCount);
    // A pending local seat (1..3) is choosing a profile — its inputs drive the picker, not locomotion — so a tape
    // enqueued now would sit dormant and burst the instant the seat confirms. The tape verbs (run/fly) refuse it; the
    // teleport verbs (warp/face/pose/where/stop) stay allowed. Population entries (4..4095) are never pending. Returns
    // the error result, or null when the target may accept a tape.
    // The named refusal for a body verb typed while a replay drive holds the seats: the loopback already drops the
    // command structurally (LoopbackTransport.InputMasked), so this says so instead of echoing a press that went
    // nowhere.
    private CommandResult? ReplayDriveError(string verb) {
        if (m_tape.Mode != WorldReplayMode.Replaying) {
            return null;
        }

        return CommandResult.Error(output: $"[{verb}: refused — replay drive of '{m_tape.DriveProgress?.SourceName}' is in progress and local seat input is masked until it ends (replay.cancel ends it now)]");
    }
    private CommandResult? PendingTapeError(int index, string verb) {
        if (
            IsSeat(index: index) &&
            m_roster.IsPending(slot: index)
        ) {
            return CommandResult.Error(output: $"[{verb}: body {index} is pending — confirm an identity first (South/Enter or player.identity)]");
        }

        return null;
    }
    // Resolves a NAMED instance's own local-seat body at the 1-based slot token sitting at args[slotTokenIndex] —
    // bounded to WorldBodiesLimits.LocalSeatCount, exactly like every retired world.instance.seat.* verb's own slot bound
    // (WorldInstanceCommandModule.TrySlot); a spawned instance's population entries beyond the local-seat range were
    // never addressable through seat.* either, so this preserves that scope rather than widening it.
    private static (WorldBody? Player, int Slot, string? Error) ResolveInstanceSlot(WorldInstance instance, in WireArgs args, int slotTokenIndex, string verb) {
        if (
            !args.TryInt(
            index: slotTokenIndex,
            value: out var slot
        ) ||
            (slot < 1) ||
            (slot > WorldBodiesLimits.LocalSeatCount)
        ) {
            return (Player: null, Slot: 0, Error: $"[{verb}: instance-targeted slot must be an integer 1..{WorldBodiesLimits.LocalSeatCount}]");
        }

        return ((instance.Server.Body(index: WorldPopulation.EntityFromDisplay(number: slot)) is { } body)
            ? (Player: body, Slot: slot, Error: null)
            : (Player: null, Slot: slot, Error: $"[{verb}: '{instance.Name}' seat {slot} is not active — see world.instance.seats]")
        );
    }
    // The shared front matter of the two mode-or-echo verbs (body.motion / body.control): validate the ≤2-token
    // shape, reject a token 0 that is neither the mode nor a bare body index, and resolve the target. The caller has
    // already parsed token 0 and passes hasMode in; on success this returns the resolved body + index (Error
    // null), else a populated IsError result keyed off the verb name and its mode <choices>.
    private (WorldBody? Player, int Index, CommandResult? Error) ResolveModeTarget(in WireArgs args, string verb, string choices, bool hasMode) {
        if (args.Count > 2) {
            return (Player: null, Index: 0, Error: CommandResult.Error(output: $"[{verb}: expected at most 2 tokens — an optional [{choices}] and an optional body index]"));
        }

        if (
            (args.Count >= 1) &&
            !hasMode &&
            !args.TryInt(
            index: 0,
            value: out _
        )
        ) {
            return (Player: null, Index: 0, Error: CommandResult.Error(output: $"[{verb}: expected {choices} (or a body index) — {verb} [{choices}] [body]]"));
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: (hasMode
            ? 1
            : 0),
            verb: verb
        );

        if (player is null) {
            return (Player: null, Index: index, Error: CommandResult.Error(output: error!));
        }

        return (Player: player, Index: index, Error: null);
    }
    // Resolve the target body from an optional trailing index at args[requiredCount] (default body 0): 0..3 are
    // the local roster seats (gated on roster membership), 4..4095 the simulated entries. Returns an error (naming
    // world.players for a seat, world.population for an entry) when the index is malformed or names an inactive
    // one. This is the loopback's fast path with sharper wording; off the loopback the server's own
    // QueryAnswer.Refused verdict carries the same miss, rendered as IsError either way.
    private (WorldBody? Player, int Index, string? Error) ResolveTarget(in WireArgs args, int requiredCount, string verb) {
        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: requiredCount,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var index
        )) {
            return (Player: null, Index: 0, Error: $"[{verb}: body index must be an integer 0..{(m_population.Capacity - 1)}]");
        }

        // Only the world's authored local-seat prefix is reserved. In a zero-seat world even body 0 is a peer;
        // the host's split-screen slot ceiling must not turn low-index peers into unjoined seats.
        if (IsSeat(index: index)) {
            return ((m_roster.IsJoined(slot: index) && (m_server.Body(index: index) is { } seat))
                ? (Player: seat, Index: index, Error: null)
                : (Player: null, Index: index, Error: $"[{verb}: body {index} is not joined — see world.players]")
            );
        }

        return ((m_population.EntryBody(index: index) is { } entry)
            ? (Player: entry, Index: index, Error: null)
            : (Player: null, Index: index, Error: $"[{verb}: body {index} is not an active population entry — see world.population]")
        );
    }
    // Text mutations enter the same tick snapshots as physical input. Read-only inspection stays immediate so an
    // operator can inspect the last completed tick even while no simulation step is currently due.
    private static CommandDefinition Route(CommandDefinition command) =>
        ((command.Name is "body.where" or "body.rig" or "player.sticks" or "body.channels" or "body.state" or "body.tether")
            ? command
            : command with { Routing = CommandRouting.Simulation }
        );
    private bool TryRoutedSeatQuery(in WireArgs args, Func<int, WorldQuery> query, out CommandResult result) {
        result = default;
        if (
            !WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var index
        ) ||
            !IsSeat(index: index)
        ) {
            return false;
        }

        var slot = index;

        // The boot claim's Endpoint.Submissions IS the injected local link, so routing through it would answer
        // identically to the local arm below — this presentation-arm selector exists only so the boot path keeps
        // its untagged output and richer local target grammar, never because routing itself would misbehave.
        if (
            !m_roster.IsJoined(slot: slot) ||
            (seatRouter.TryRoute(slot: slot) is not { } route) ||
            string.Equals(
            a: route.Endpoint.Identity,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return false;
        }

        // The routed factory takes the SAME 0-based entity index the local arm's ResolveTarget produces — undoing
        // the shared helper's 1-based QueryIndex, since this family's WorldQuery kinds are 0-based by convention,
        // unlike Audio/Collision's.
        return seatRouter.TryRouteQuery(
            factory: authorityIndex => query((authorityIndex - 1)),
            result: out result,
            slot: slot,
            tagInstance: true
        );
    }
    // Strips an optional trailing `instance:<name>` token — the addressing token that redirects join/leave/fly/stop/
    // where/pose from the boot world's roster/population onto a named running instance's own local-seat table (see
    // WorldInstanceHost). Unambiguous against the trailing player/slot index: "instance:" is a reserved prefix no
    // integer index can ever parse as, and this token is read only as the line's last token, stripped before every
    // other positional argument is parsed — so its presence or absence never shifts any other token's position.
    // 'boot' is refused by name; the boot world is already the default (see WorldInstanceCommandModule.TryResolveNonBoot).
    private bool TryStripInstanceToken(in WireArgs args, string verb, out InstanceTarget target, out CommandResult? error) {
        var count = args.Count;

        if (
            (count == 0) ||
            !WorldArgs.IsInstanceToken(token: args[(count - 1)])
        ) {
            target = new InstanceTarget(
                EffectiveCount: count,
                Instance: null
            );
            error = null;

            return true;
        }

        if (!WorldArgs.TryResolveInstance(
            token: args[(count - 1)],
            verb: verb,
            instances: m_instances,
            instance: out var instance,
            error: out error
        )) {
            target = default;

            return false;
        }

        target = new InstanceTarget(
            EffectiveCount: (count - 1),
            Instance: instance
        );
        error = null;

        return true;
    }
    // Splices ` instance:<name>` just inside a bracketed echo's closing ']' — the same surgery WithPerceptionAnchor
    // uses for anchor=body:<n>, reused here because an instance-targeted read has no perception anchor to report but
    // still owes the caller which instance answered.
    private static string WithInstanceTag(string text, string instanceName) => CommandEcho.SpliceTag(
        prefix: WorldArgs.InstanceTokenPrefix,
        text: text,
        value: instanceName
    );

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in AuthoredVerbs()) {
            yield return Route(command: command);
        }

        foreach (var command in GestureVerbs()) {
            yield return Route(command: command);
        }

        foreach (var command in StickVerbs()) {
            yield return Route(command: command);
        }

        foreach (var command in ChannelVerbs()) {
            yield return Route(command: command);
        }

        foreach (var command in ModeVerbs()) {
            yield return Route(command: command);
        }

        foreach (var command in TetherVerbs()) {
            yield return Route(command: command);
        }
    }

    // A conditional interpolated-string handler that only formats when a wire verb's acks are on. The `out shouldAppend`
    // ctor param makes it lazy: the compiler emits the Append* calls only when it is true (args.Echo), so a quiet flood
    // never touches the inner builder. Formats under the invariant culture.
    [InterpolatedStringHandler]
    private ref struct EchoHandler {
        private DefaultInterpolatedStringHandler m_inner;

        public EchoHandler(int literalLength, int formattedCount, in WireArgs args, out bool shouldAppend) {
            shouldAppend = args.Echo;
            m_inner = (args.Echo
                ? new DefaultInterpolatedStringHandler(
                    literalLength: literalLength,
                    formattedCount: formattedCount,
                    provider: CultureInfo.InvariantCulture
                )
                : default
            );
        }

        public void AppendLiteral(string value) {
            m_inner.AppendLiteral(value: value);
        }
        public void AppendFormatted<T>(T value) {
            m_inner.AppendFormatted(value: value);
        }
        public void AppendFormatted<T>(T value, string? format) {
            m_inner.AppendFormatted(
                format: format,
                value: value
            );
        }
        public string ToStringAndClear() {
            return m_inner.ToStringAndClear();
        }
    }
    // The addressing decision every instance-aware drive-a-player verb resolves ONCE, up front: which running
    // instance (null = the boot world, the default) and the EFFECTIVE token count once a trailing instance:<name>
    // token — if present — is excluded. Identical to args.Count when no such token rides the line, so a caller that
    // already validated shapes against args.Count keeps working unchanged by switching to EffectiveCount.
    private readonly record struct InstanceTarget(WorldInstance? Instance, int EffectiveCount);
}

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
/// world's channel names exist at boot. The drive-a-player verbs (<c>fly</c> / <c>pose</c> / <c>where</c> /
/// <c>stop</c>) take an optional trailing player index reaching the whole population (1..128, default player 1):
/// 1..4 resolve to the local roster seats, 5..128 to the population's simulated entries (each owning its own
/// <see cref="WorldBody"/> sim). A non-local entity is only ever sent inputs (a fly/pose is a command producing
/// intents or a teleport, never a pose stream). The channel verbs carry no player-index argument at all: a bound
/// control targets whichever local seat's device dispatched it (the recorded logical slot — see the class remarks
/// below), and a typed invocation with no device defaults to player 1. The roster-management verbs (<c>join</c> /
/// <c>leave</c> / <c>profile</c> / <c>assign</c>) stay seat-only (1..4). Mutations are simulation-routed and applied
/// from the tick snapshot immediately before <see cref="WorldSimulation"/> advances; read-only inspection sees the
/// last completed tick.
/// </summary>
/// <remarks><para>The stick channels (<c>player.move</c> / <c>player.move.strafe</c> / <c>player.look</c> / <c>player.look.steer</c>) are not polled: their bindings fire on the
/// default active phase, and the snapshot router re-dispatches carried analog values each tick. Handlers route each
/// dispatch by its recorded logical slot; the local device id is consulted only when a previously unseen live device
/// first needs seating.</para>
/// <para><c>join</c>/<c>leave</c>/<c>fly</c>/<c>stop</c>/<c>where</c>/<c>pose</c> also accept a trailing
/// <c>instance:&lt;name&gt;</c> token (see <see cref="TryStripInstanceToken"/>), addressing a named running
/// <see cref="WorldInstance"/> (<see cref="WorldInstanceHost"/>) instead of the boot world. In the instance form, a
/// slot is always the 1-based local seat 1..<see cref="WorldPopulationLimits.LocalSeatCount"/> (never a population
/// entry, and never defaulted — a bare <c>instance:&lt;name&gt;</c> with no slot is refused), and <c>join</c>'s
/// "next free slot"/either-order profile-then-slot convenience does not apply there.</para>
/// </remarks>
internal sealed partial class PlayerCommandModule(PlayerRoster roster, WorldPopulation population, WorldScreenBinder screens, WorldDefinition definition, IServerLink link, WorldServer server, WorldPerceptionAnchor anchor, WorldClient client, Func<InputRouter> router, WorldInstanceHost instances, WorldSeatBindings seatBindings, WorldSeatAuthorityRouter seatRouter, WorldSeatFlyRig flyRig) : ICommandModule {
    // The reserved trailing token an instance-addressed drive-a-player verb carries — see TryStripInstanceToken.
    private const string InstanceTokenPrefix = "instance:";

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
    public const string SwapLookCommand = Puck.World.Client.PlayerCommandNames.SwapLookCommand;
    /// <summary>The look-recenter command: turns the seat camera round behind the body.</summary>
    public const string RecenterLookCommand = Puck.World.Client.PlayerCommandNames.RecenterLookCommand;
    /// <summary>The generic sensor-input mode toggle.</summary>
    public const string MotionControlsCommand = Puck.World.Client.PlayerCommandNames.MotionControlsCommand;
    /// <summary>The Axis3D angular-velocity input for motion controls.</summary>
    public const string MotionAngularCommand = Puck.World.Client.PlayerCommandNames.MotionAngularCommand;
    /// <summary>The movement-facing Axis2D command (+Y forward, +X strafe right). The handler routes the dispatch to
    /// the owning device's player; standard binds its left stick to <see cref="MoveStrafeCommand"/> instead.</summary>
    public const string MoveCommand = Puck.World.Client.PlayerCommandNames.MoveCommand;
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
    // The BOOT world's compiled channel table — name→ordinal resolution for player.press and PickerDirection's
    // pre-join Turn-role check. Validation has already run by the time a WorldDefinition reaches here, so every
    // declared name resolves. NEVER the source a bound channel verb dispatches against — see m_seatBindings.
    private readonly WorldChannelTable m_channels = WorldChannelTable.Compile(channels: definition.Channels);
    // The per-seat CURRENTLY ROUTED channel vocabulary (WorldSeatBindings.Channels, kept in sync with each seat's
    // WorldSeatAuthorityRouter claim by WorldSimulation's post-step sync). WorldSeatBindings lowers an authored
    // channel NAME through that table to one of the fixed ordinal commands registered below; ChannelVerb checks the
    // same table again when the bound control fires. The command registry and its replay-stable ids therefore never
    // depend on which local, late-mounted, or remote destination documents happened to be readable at boot.
    private readonly WorldSeatBindings m_seatBindings = seatBindings;
    private readonly WorldSeatFlyRig m_flyRig = flyRig;

    // The authored, argument-bearing verbs (assertable on stdout). The drive-a-player verbs take an optional trailing
    // player index reaching the whole population: 1..4 are the local seats, 5..128 the simulated entries.
    private IEnumerable<CommandDefinition> AuthoredVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.reconcile",
            description: "Applies a smoothed SERVER CORRECTION to a player: player.reconcile <x> <z> <yawDegrees> [seconds] [player]. The SIM pose snaps to the target INSTANTLY (identical end-state to a player.pose ground-plane + heading update), while the on-screen avatar EASES from where it was to the authoritative pose over [seconds] (default 0.25, clamped 0.05..2) — the AAA error-smoothing shape a real server uses. A correction larger than the snap-error ceiling pops instead of gliding. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. The eased offset is presentation-only: player.where still reports the snapped SIM pose.",
            handler: ReconcileHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.where",
            description: "Echoes a player's FULL 6DOF pose — [player.where: p<N> pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd°] — so a piped run can assert it moved: player.where [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries). Grounded entities print y=0.00 pitch=0 roll=0. A LOCAL seat's echo also carries anchor=body:<n> — the 0-based body index that seat's presentation (camera eye, audio listener, seat.<n>.position.* HUD bindings) derives from: the seat's bound body, or the routed body while possessing (a Control route targeting a body with capture on). A trailing instance:<name> token reads OUT OF a NAMED running instance's OWN tick snapshot instead — player.where <slot> instance:<name> (slot REQUIRED, 1..WorldPopulationLimits.LocalSeatCount); no anchor rides that form (a spawned instance's seat has no client perceiving from it).",
            handler: WhereHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.channels",
            description: "Echoes the channel decision read-back for a player's body — per DECLARED channel, the fold's value and owning-seat base, the held overlay admitted later and its composed result, every fold contributor tagged by principal (trusted/untrusted), the pool ceiling in force, and whether the pool actually clamped this write: player.channels [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries). The fold only ever exists over a human-occupied local seat; any other target reports that plainly rather than fabricating a base/pool.",
            handler: ChannelsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.state",
            description: "Echoes every named action-state slot, including kind, lifetime, exact stored value, player identity, and writes emitted by the most recently completed tick: player.state [player].",
            handler: StateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.designate",
            description: "Proposes a target for one authored target register: player.designate <register> <body:n|nearest|at:x,y,z> [player]. 'nearest' resolves client-side from the latest snapshot inside the player's clamped forward cone; 'at:x,y,z' proposes a world-space point (the seek target a Designated-source producer steers to). The server re-resolves activity, authority, range, cone, targetability, and line of sight (a point checks the same envelope, minus body activity) before writing. Returns player.targets read-back, including the latest refusal.",
            handler: DesignateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.targets",
            description: "Echoes every authored target register, its current body subject, applied/authored cone, line-of-sight requirement, and the latest designation refusal: player.targets [player].",
            handler: TargetsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "player.state-load",
            description: "Stages one durable action-state input for the next simulation tick: player.state-load <name> <counter-value|timer-seconds> [player]. The command is tick-stamped and recorded on the replay authority tape.",
            handler: StateLoadHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.stop",
            description: "Stops a player's avatar dead: clears its whole tape, releases every held movement key, cancels every in-flight timed press (player.press hold — materialized or still pending), AND clears any BindingEntryMode.Toggle latch the seat carries (a toggled-on channel does not survive a stop) — the panic verb: player.stop [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries; stopping a population entry drops its tape so its wander resumes). Echoes the true released/cleared/toggle counts, or, if the Drive gate refused the command (e.g. CC/death), a refusal naming the denial — never an affirmative quoting a stale count. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — player.stop <slot> instance:<name> (slot REQUIRED, 1..WorldPopulationLimits.LocalSeatCount) — echoing a bare \"tape cleared\", since no client mirrors that seat's held-key/toggle state.",
            handler: StopHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.motion",
            description: "Sets or echoes a player's declared body motion program: player.motion [program] [player]. With no program it echoes the current selection. A switch is authoritative and may re-constrain the pose. The optional trailing player index is 1..128 (default 1).",
            handler: MotionHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.fly",
            description: "Enqueues a six-role timed segment on a player's tape: player.fly <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> [player] — each channel a float clamped to [-1,1], held for <seconds>. The body's authored motion program decides which roles it reads: ApplyVerticalDrive consumes up alongside a gravity/jump arc, while IntegrateLocalAttitude + ComputeLocalTargetVelocity provide body-frame 6DOF. This is the ONE scripted-tape verb: a planar segment is this verb with up/pitch/roll zeroed — player.fly <forward> <strafe> 0 <turn> 0 0 <seconds>. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries.",
            handler: FlyHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.pose",
            description: "Teleports a player to a full 6DOF pose: player.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> [player] (yaw about world up, pitch about the body right, roll about the body forward; 0/0/0 = level facing -Z). ANY of the six positional values may be - to HOLD that axis at its current value instead of setting it — player.pose - - - 90 - - 1 turns p1 to face 90° with its position and pitch/roll untouched; player.pose 3 - 5 - - - 1 moves p1 on the ground plane only. The held axes are read from the SAME live pose this call resolves and folded into ONE atomic SnapPose submission — never a read-then-write pair, so nothing can move the body between the read and the write. A hard teleport (sim snap + previous-pose reset + render-error clear). A grounded entity re-pins Y and levels on its next step. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — player.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> <slot> instance:<name> (slot REQUIRED, 1..WorldPopulationLimits.LocalSeatCount) — the SAME hold-current axes apply, read from that instance's own live pose.",
            handler: PoseHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.press",
            description: "Presses ANY declared channel — movement roles included — for a timed auto-release: player.press <channel> [value] [holdSeconds] [player]. <channel> is a name from world.affordances' channels section; [value] defaults to the shape's max (1) and is validated against the channel's shape (binary: 0 or 1; unipolar: [0,1]; bipolar: [-1,1]); [holdSeconds] how long it reads held (default a short host-step-derived tap, bounded authoritatively by the deciding Drive grant's hold:<seconds> policy — WorldGrant.DefaultHoldSeconds=2 absent an explicit row — and the 60-second engine backstop). The echo names the TRUE outcome, never an assumed one: a non-positive [holdSeconds] is ignored outright (echoed plainly, no cap blamed); a request either cap truncates echoes the EFFECTIVE hold and names whichever cap structurally bound it (the grant's hold budget, or the engine's hold ceiling when the grant permits the full backstop and the request still exceeds it); a refused command (e.g. a CC/death Drive gate) echoes the refusal, never an affirmative; under no cap and no refusal the echo is unchanged. A press carrying a DIFFERENT value than an ordinal's in-flight hold (materialized or still pending) replaces it outright (its own duration), so an opposing press is never silently swallowed by a longer hold's remaining ticks; a re-press carrying the SAME value only ever extends. [player] the trailing index 1..128 (default 1 — 1..4 seats, 5..128 population). The press is INDEPENDENT of the movement tape, so player.fly … then player.press jump fires a runner mid-segment. On a composition channel, what the press DOES is the target's kit binding (the default world's grounded kits bind the vertical impulse via \"jump\" — a short hold = short hop, a long hold = full arc via variable height; an unbound channel leaves it inert). There is no sugar verb for a bound button — the bound control rides its own channel-generic command (see world.affordances); this is the scripted/wire twin reaching every channel.",
            handler: PressHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.control",
            description: "Sets or echoes a player's INTENT SOURCE — what fills its intent gaps between tape segments: player.control [live|idle|producer:<name>] [player]. 'live' admits the submitted device stream; 'idle' masks it so a tape gap holds still; 'producer:<name>' runs that producer program from the target kit before motion. Tapes and player.press still drive under every source. Any switch releases held keys/lanes so nothing bursts. With no mode it echoes the current source. A pending seat's source cannot be set. The optional player index is 1..128 (default 1). world.population sweeps all peers' sources.",
            handler: ControlHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.engage",
            description: "ROUTES a player's intent onto a TARGET — a diegetic screen (the classic UX) or another body (possession): player.engage <screen>|body:<n> [capture:on|off] [player] — [capture:on|off] defaults to on (today's behavior: the source avatar idles); capture:off MIRRORS instead — the target still receives the routed channels every tick while the source avatar keeps moving under its own input. [player] is the trailing index 1..128 (default 1). On a SCREEN target the resolved per-frame intent (tape/press/held keys alike) is translated to joypad buttons and delivered to the screen's booted machine; the screen must be declared engageable, carry a booted machine (screen.insert first), and — when its route sets an engage radius — the player's avatar must be within it (player.pose up first); multiple players engaged on one screen OR-merge their buttons (the multiplayer cabinet). On a BODY target the routed channels reach the target through the ordinary co-drive contribution path — the actor must ALSO hold Drive over the target body (world.grant seatN drive body:<n>) for anything to actually move; a route alone confers no Drive authority. Route only — orthogonal to player.control.",
            handler: EngageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.disengage",
            description: "DISENGAGES a player from its screen so its intent drives its avatar again: player.disengage [player] (optional index 1..128, default 1). Drops any live held keys/lanes so nothing leaks across the boundary (the avatar does not burst into motion). A friendly no-op echo when the player was not engaged.",
            handler: DisengageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.join",
            description: "Joins a player: player.join [n] joins a PENDING player (a profile is chosen, then confirm) — with no index the next free slot, n (2..4) that specific slot. player.join <profile> [n] joins directly ACTIVE on a named profile (a token in 2..4 is a slot, otherwise a profile name; either order). No device is attached (the console is a network-shaped source), so a piped script builds a quad session. Echoes the roster. A trailing instance:<name> token addresses a NAMED running instance instead of the boot world — player.join <slot> [identity] instance:<name> (slot is REQUIRED, 1..WorldPopulationLimits.LocalSeatCount, never auto-picked; no either-order profile/slot convenience there).",
            handler: JoinHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.leave",
            description: "Removes a scripted or pad player: player.leave <n> (n in 2..4), unmapping its devices and freeing its profile. Player 1 never leaves. Echoes the resulting roster. A trailing instance:<name> token addresses a NAMED running instance instead — player.leave <slot> instance:<name> (slot 1..WorldPopulationLimits.LocalSeatCount, seat 1 included — the boot form's \"player 1 never leaves\" rule is a roster policy that does not apply to an instance's own seat table), which REAPS the instance on empty.",
            handler: LeaveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.identity",
            description: "Sets a specific owned-world identity on a player and confirms it: player.identity <name> [n].",
            handler: ProfileHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.assign",
            description: "Moves a device between players: player.assign <kbd|padN> <slot> (slot 1..4). Onto an occupied slot the device joins that team; onto an empty slot it creates a pending player (a profile must be chosen); onto its own slot a no-op. See world.devices for the tokens.",
            handler: AssignHandler
        );
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
    private static bool IsSeat(int index) => (index <= PlayerRoster.MaxSlots);
    // A pending local seat (2..4) is choosing a profile — its inputs drive the picker, not locomotion — so a tape
    // enqueued now would sit dormant and burst the instant the seat confirms. The tape verbs (run/fly) refuse it; the
    // teleport verbs (warp/face/pose/where/stop) stay allowed. Population entries (5..128) are never pending. Returns
    // the error result, or null when the target may accept a tape.
    private CommandResult? PendingTapeError(int index, string verb) {
        if (
            (index <= PlayerRoster.MaxSlots) &&
            m_roster.IsPending(slot: PlayerRoster.SlotFromDisplay(number: index))
        ) {
            return CommandResult.Error(output: $"[{verb}: player {index} is pending — confirm an identity first (South/Enter or player.identity)]");
        }

        return null;
    }
    // Resolves a NAMED instance's own local-seat body at the 1-based slot token sitting at args[slotTokenIndex] —
    // bounded to WorldPopulationLimits.LocalSeatCount, exactly like every retired world.instance.seat.* verb's own slot bound
    // (WorldInstanceCommandModule.TrySlot); a spawned instance's population entries beyond the local-seat range were
    // never addressable through seat.* either, so this preserves that scope rather than widening it.
    private static (WorldBody? Player, int Slot, string? Error) ResolveInstanceSlot(WorldInstance instance, in WireArgs args, int slotTokenIndex, string verb) {
        if (
            !args.TryInt(
            index: slotTokenIndex,
            value: out var slot
        ) ||
            (slot < 1) ||
            (slot > WorldPopulationLimits.LocalSeatCount)
        ) {
            return (Player: null, Slot: 0, Error: $"[{verb}: instance-targeted slot must be an integer 1..{WorldPopulationLimits.LocalSeatCount}]");
        }

        return ((instance.Server.Body(index: (slot - 1)) is { } body)
            ? (Player: body, Slot: slot, Error: null)
            : (Player: null, Slot: slot, Error: $"[{verb}: '{instance.Name}' seat {slot} is not active — see world.instance.seats]")
        );
    }
    // The shared front matter of the two mode-or-echo verbs (player.motion / player.control): validate the ≤2-token
    // shape, reject a token 0 that is neither the mode nor a bare player index, and resolve the target. The caller has
    // already parsed token 0 and passes hasMode in; on success this returns the resolved player + display index (Error
    // null), else a populated IsError result keyed off the verb name and its mode <choices>.
    private (WorldBody? Player, int Index, CommandResult? Error) ResolveModeTarget(in WireArgs args, string verb, string choices, bool hasMode) {
        if (args.Count > 2) {
            return (Player: null, Index: 0, Error: CommandResult.Error(output: $"[{verb}: expected at most 2 tokens — an optional [{choices}] and an optional player index]"));
        }

        if (
            (args.Count >= 1) &&
            !hasMode &&
            !args.TryInt(
            index: 0,
            value: out _
        )
        ) {
            return (Player: null, Index: 0, Error: CommandResult.Error(output: $"[{verb}: expected {choices} (or a player index) — {verb} [{choices}] [player]]"));
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
    // Resolve the target body from an optional trailing index at args[requiredCount] (default player 1): 1..4 are
    // the local roster seats (gated on roster membership), 5..128 the simulated entries. Returns an error (naming
    // world.players for a seat, world.population for an entry) when the index is malformed or names an inactive
    // one. This is the loopback's fast path with sharper wording; off the loopback the server's own
    // QueryAnswer.Refused verdict carries the same miss, rendered as IsError either way.
    private (WorldBody? Player, int Index, string? Error) ResolveTarget(in WireArgs args, int requiredCount, string verb) {
        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: requiredCount,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var index
        )) {
            return (Player: null, Index: 0, Error: $"[{verb}: player index must be an integer 1..{m_population.Capacity}]");
        }

        // 1..4 are the local seats; 5..128 are population entries, addressed by their 0-based entity index (display
        // number − 1). Both resolve to the server's authoritative body.
        if (index <= PlayerRoster.MaxSlots) {
            var slot = PlayerRoster.SlotFromDisplay(number: index);

            return ((m_roster.IsJoined(slot: slot) && (m_server.Body(index: slot) is { } seat))
                ? (Player: seat, Index: index, Error: null)
                : (Player: null, Index: index, Error: $"[{verb}: player {index} is not joined — see world.players]")
            );
        }

        return ((m_population.EntryBody(index: (index - 1)) is { } entry)
            ? (Player: entry, Index: index, Error: null)
            : (Player: null, Index: index, Error: $"[{verb}: player {index} is not an active population entry — see world.population]")
        );
    }
    // Text mutations enter the same tick snapshots as physical input. Read-only inspection stays immediate so an
    // operator can inspect the last completed tick even while no simulation step is currently due.
    private static CommandDefinition Route(CommandDefinition command) =>
        ((command.Name is "player.where" or "player.sticks" or "player.channels" or "player.state")
            ? command
            : command with { Routing = CommandRouting.Simulation }
        );
    private bool TryRoutedSeatQuery(in WireArgs args, Func<int, WorldQuery> query, out CommandResult result) {
        result = default;
        if (
            !WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var index
        ) ||
            (index > PlayerRoster.MaxSlots)
        ) {
            return false;
        }

        var slot = PlayerRoster.SlotFromDisplay(number: index);

        if (!m_roster.IsJoined(slot: slot)) {
            return false;
        }

        var route = seatRouter.Route(slot: slot);

        if (string.Equals(
            a: route.Endpoint.Identity,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            return false;
        }

        var routedResult = default(CommandResult);

        route.Endpoint.Submissions.Query(
            query: query((route.EntityIndex + 1)),
            completion: answer => {
                routedResult = new CommandResult(Output: WithInstanceTag(
                    text: answer.Text,
                    instanceName: route.Endpoint.Identity
                )) { IsError = answer.Refused };
            }
        );
        result = routedResult;
        return true;
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
            !args[(count - 1)].StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: InstanceTokenPrefix
        )
        ) {
            target = new InstanceTarget(
                EffectiveCount: count,
                Instance: null
            );
            error = null;

            return true;
        }

        var name = args[(count - 1)][InstanceTokenPrefix.Length..].ToString();

        if (string.IsNullOrWhiteSpace(value: name)) {
            target = default;
            error = CommandResult.Error(output: $"[{verb}: instance: must name a running instance — see world.instance.status]");

            return false;
        }

        if (string.Equals(
            a: name,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            target = default;
            error = CommandResult.Error(output: $"[{verb}: '{WorldInstanceHost.BootInstanceName}' is the world this process booted with — omit instance: to address it]");

            return false;
        }

        if (
            !m_instances.TryGet(
            instance: out var instance,
            name: name
        ) ||
            (instance is null)
        ) {
            target = default;
            error = CommandResult.Error(output: $"[{verb}: no instance named '{name}' — see world.instance.status]");

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
    private static string WithInstanceTag(string text, string instanceName) =>
        (text.EndsWith(value: ']')
            ? $"{text[..^1]} instance:{instanceName}]"
            : text
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

using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The players' console surface: the verbs a piped script (or the on-screen console) drives the avatars with, the two
/// stick routers, and the six hold/release movement verbs the keyboard binding table targets. The drive-a-player verbs
/// (<c>player.run</c> / <c>warp</c> / <c>face</c> / <c>where</c> / <c>stop</c>) take an optional trailing player index
/// reaching the whole population (1..128, default player 1): 1..4 resolve to the local roster seats, 5..128 to the
/// population's simulated entries (each owning its own <see cref="WorldBody"/> sim). A non-local entity is only ever
/// sent inputs (a warp/run/face is a command producing intents, never a pose stream). The six keyboard movement verbs
/// always target player 1, and the roster-management verbs (<c>join</c> / <c>leave</c> / <c>profile</c> /
/// <c>assign</c>) stay seat-only (1..4). Mutations are simulation-routed and applied from the tick snapshot immediately
/// before <see cref="WorldSimulation"/> advances; read-only inspection sees the last completed tick.
/// </summary>
/// <remarks>The stick channels (<c>player.move</c> / <c>player.look</c>) are not polled: their bindings fire on the
/// default active phase, and the snapshot router re-dispatches carried analog values each tick. Handlers route each
/// dispatch by its recorded logical slot; the local device id is consulted only when a previously unseen live device
/// first needs seating.</remarks>
internal sealed class PlayerCommandModule(PlayerRoster roster, WorldPopulation population, WorldEngagement engagement, WorldScreenBinder screens, WorldDefinition definition, IServerLink link, WorldServer server) : ICommandModule {
    /// <summary>The Axis2D command the gamepad's LEFT stick is bound to (+Y forward, +X strafe right). The handler
    /// ROUTES the dispatch to the owning device's player (joining an unmapped pad per the roster rules).</summary>
    public const string MoveCommand = "player.move";
    /// <summary>The Axis2D command the gamepad's RIGHT stick is bound to (+X turns right). Same routing contract as
    /// <see cref="MoveCommand"/>.</summary>
    public const string LookCommand = "player.look";
    /// <summary>The confirm command (Gamepad South / Keyboard Enter, press edge) — promotes the pending player owning
    /// the pressing device.</summary>
    public const string ConfirmCommand = "player.confirm";
    /// <summary>The device-cycle command (Gamepad Start, press edge) — rotates the pressing device to the next slot.</summary>
    public const string CycleCommand = "player.cycle";
    /// <summary>The keyboard-claim command (Keyboard F1..F4, press edge). The target slot rides the binding's Axis1D
    /// value as a 1-based player number, the clean scalar constant a binding carries.</summary>
    public const string ClaimCommand = "player.claim";
    /// <summary>The primary-channel GESTURE command (Keyboard Space / Gamepad South-when-active, BOTH edges) — the
    /// bound-button path onto the <see cref="ActionLanes.Primary"/> channel: a press edge holds the lane, a release edge
    /// frees it, so a live control gets variable height under a kit that binds the vertical impulse. This is the button
    /// twin of the six movement verbs; the TYPED/scripted path is the argument-bearing <c>player.press</c> instead (a
    /// typed <c>player.primary</c> is refused, pointing at it).</summary>
    public const string PrimaryCommand = "player.primary";
    /// <summary>The secondary-channel GESTURE command (Gamepad East, BOTH edges) — the bound-button path onto the
    /// <see cref="ActionLanes.Secondary"/> channel: a press edge holds the lane, a release edge frees it. Its meaning
    /// is the kit's binding (the default world's grounded kits dash, its free kits surge). The TYPED/scripted path is
    /// <c>player.press secondary</c>.</summary>
    public const string SecondaryCommand = "player.secondary";
    /// <summary>The Gamepad-South gesture command (BOTH edges) — the CONTEXT-routed South button: while the pressing pad's
    /// seat is PENDING (or the pad is unmapped) it is the CONFIRM flow byte-for-byte (join, then confirm, Started edge); once
    /// the seat is ACTIVE it is the <see cref="ActionLanes.Primary"/> channel (both edges for variable height). One
    /// button both seats a player and acts for them, with no restart.</summary>
    public const string SouthCommand = "player.south";

    // The action-lane names player.press parses against, surfaced in the unknown-lane error. A new lane joins here and
    // in TryParseLane together.
    private const string KnownActionLanes = "primary, secondary";

    // The player.reconcile smoothing window: the default when [seconds] is omitted, and the clamp a supplied value is
    // held to.
    private const float DefaultReconcileSeconds = 0.25f;
    private const float MaxReconcileSeconds = 2f;
    private const float MinReconcileSeconds = 0.05f;

    private readonly PlayerRoster m_roster = roster;
    private readonly WorldPopulation m_population = population;
    private readonly WorldEngagement m_engagement = engagement;
    private readonly WorldScreenBinder m_screens = screens;
    private readonly WorldDefinition m_definition = definition;
    private readonly IServerLink m_link = link;
    private readonly WorldServer m_server = server;

    // Whether a drive verb's resolved target is a local seat — seats carry client-side device state (held keys/lanes,
    // the possession latch copy) that some commands must also touch.
    private static bool IsSeat(int index) => (index <= PlayerRoster.MaxSlots);

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

        foreach (var command in MovementVerbs()) {
            yield return Route(command: command);
        }
    }

    // Text mutations enter the same tick snapshots as physical input. Read-only inspection stays immediate so an
    // operator can inspect the last completed tick even while no simulation step is currently due.
    private static CommandDefinition Route(CommandDefinition command) =>
        ((command.Name is "player.where" or "player.sticks")
            ? command
            : command with { Routing = CommandRouting.Simulation });

    // The authored, argument-bearing verbs (assertable on stdout). The drive-a-player verbs take an optional trailing
    // player index reaching the whole population: 1..4 are the local seats, 5..128 the simulated entries.
    private IEnumerable<CommandDefinition> AuthoredVerbs() {
        yield return CommandDefinition.WithWireArgs(
            name: "player.run",
            description: "Enqueues a timed scripted segment on a player's tape: player.run <forward> <strafe> <turn> <seconds> [player] — each axis a float clamped to [-1,1] (forward drives along facing, strafe along the avatar's right, turn spins its heading), held for <seconds> of run time; the optional trailing player index is 1..128 (default 1) — 1..4 are the local seats, 5..128 the simulated population entries. A live segment overrides that player's keyboard/pad (or, on a population entry, its wander) until it expires; this is the doom-replay primitive a piped script drives, and the proof a non-local entity is driven by INPUTS alone.",
            handler: RunHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.warp",
            description: "Teleports a player's avatar to a ground-plane position: player.warp <x> <z> [player]. Leaves the heading unchanged; the optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. A warp is a server-authoritative teleport command, not a pose stream.",
            handler: WarpHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.face",
            description: "Sets a player's heading in degrees: player.face <degrees> [player] (0 = facing -Z); the optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries.",
            handler: FaceHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.reconcile",
            description: "Applies a smoothed SERVER CORRECTION to a player: player.reconcile <x> <z> <yawDegrees> [seconds] [player]. The SIM pose snaps to the target INSTANTLY (identical end-state to a warp+face), while the on-screen avatar EASES from where it was to the authoritative pose over [seconds] (default 0.25, clamped 0.05..2) — the AAA error-smoothing shape a real server uses. A correction larger than the snap-error ceiling pops instead of gliding. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. The eased offset is presentation-only: player.where still reports the snapped SIM pose.",
            handler: ReconcileHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.where",
            description: "Echoes a player's FULL 6DOF pose — [player.where: p<N> pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd°] — so a piped run can assert it moved: player.where [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries). Grounded entities print y=0.00 pitch=0 roll=0.",
            handler: WhereHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.stop",
            description: "Stops a player's avatar dead: clears its whole tape and releases every held movement key: player.stop [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries; stopping a population entry drops its tape so its wander resumes).",
            handler: StopHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.motion",
            description: "Sets or echoes a player's motion model: player.motion [grounded|free] [player]. grounded is the ground avatar (planar, Y pinned, pitch/roll zero — the default); free is the space-sim full-6DOF body-frame flight model. With no mode it echoes the target's current model. A switch is authoritative like a game-mode change (free→grounded snaps to the plane and levels the attitude). The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries.",
            handler: MotionHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.fly",
            description: "Enqueues a 6DOF timed segment on a player's tape: player.fly <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> [player] — each channel a float clamped to [-1,1] (forward/strafe/up drive the body axes, yaw/pitch/roll spin them), held for <seconds>. Works in either model — grounded ignores up/pitch/roll by its constraints; free integrates all six in the body frame. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries.",
            handler: FlyHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.pose",
            description: "Teleports a player to a full 6DOF pose: player.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> [player] (yaw about world up, pitch about the body right, roll about the body forward; 0/0/0 = level facing -Z). A hard teleport (sim snap + previous-pose reset + render-error clear); warp/face stay the planar shorthands. A grounded entity re-pins Y and levels on its next step. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries.",
            handler: PoseHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.press",
            description: "Presses a player's abstract ACTION channel for a timed auto-release: player.press <lane> [holdSeconds] [player] — <lane> is a channel name (today: primary), [holdSeconds] how long it reads held (default a short host-step-derived tap, clamped 0..2), [player] the trailing index 1..128 (default 1 — 1..4 seats, 5..128 population). The channel is INDEPENDENT of the movement tape, so player.run … then player.press primary fires a runner mid-segment. What the press DOES is the target's kit binding: the default world's grounded kits bind the vertical impulse (a short hold = short hop, a long hold = full arc via variable height); an unbound kit leaves it inert. There is no sugar verb — the bound button rides player.primary (gesture); this is its scripted/wire twin.",
            handler: PressHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.control",
            description: "Sets or echoes a player's INTENT SOURCE — what fills its intent gaps between tape segments: player.control [live|idle|wander] [player]. 'live' (the seat default) admits the submitted device stream and the device jump edges. 'idle' masks the submitted stream so a script owns the entity — a tape GAP holds still instead of leaking the human's held keys/analog, and the device jump (Space/South) no-ops; the tape and player.press still drive. 'wander' runs the deterministic index-seeded wander producer in the gaps (submissions still outrank it; device lanes stay masked — wander is not possession by the human), so a seat can join the crowd while unattended. Any switch releases held keys/lanes so nothing bursts. With no mode it echoes the target's current source. A pending seat's source cannot be set (confirm a profile first). The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. NOTE: world.population idle|wander sweeps ALL peers' sources (last-writer-wins).",
            handler: ControlHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.engage",
            description: "ENGAGES a player on a diegetic screen so its intent drives the screen's machine instead of its avatar: player.engage <screen> [player] — <screen> the engine screen index, [player] the trailing index 1..128 (default 1). The player's resolved per-frame intent (tape/press/held keys alike) is translated to joypad buttons and delivered to the screen's booted machine; the avatar stands idle. The screen must be declared engageable, carry a booted machine (screen.insert first), and — when its route sets an engage radius — the player's avatar must be within it (player.warp up first). Multiple players engaged on one screen OR-merge their buttons (the multiplayer cabinet). Route only — orthogonal to player.control.",
            handler: EngageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.disengage",
            description: "DISENGAGES a player from its screen so its intent drives its avatar again: player.disengage [player] (optional index 1..128, default 1). Drops any live held keys/lanes so nothing leaks across the boundary (the avatar does not burst into motion). A friendly no-op echo when the player was not engaged.",
            handler: DisengageHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.join",
            description: "Joins a player: player.join [n] joins a PENDING player (a profile is chosen, then confirm) — with no index the next free slot, n (2..4) that specific slot. player.join <profile> [n] joins directly ACTIVE on a named profile (a token in 2..4 is a slot, otherwise a profile name; either order). No device is attached (the console is a network-shaped source), so a piped script builds a quad session. Echoes the roster.",
            handler: JoinHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.leave",
            description: "Removes a scripted or pad player: player.leave <n> (n in 2..4), unmapping its devices and freeing its profile. Player 1 never leaves. Echoes the resulting roster.",
            handler: LeaveHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.profile",
            description: "Sets a specific profile on a player and confirms it: player.profile <name> [n] (optional player index 1..4, default 1). On a pending player it is the choose-and-confirm; on an active player a live identity switch (persists the boot seat for player 1). Friendly error when the name is unknown or already in use.",
            handler: ProfileHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "player.assign",
            description: "Moves a device between players: player.assign <kbd|padN> <slot> (slot 1..4). Onto an occupied slot the device joins that team; onto an empty slot it creates a pending player (a profile must be chosen); onto its own slot a no-op. See world.devices for the tokens.",
            handler: AssignHandler
        );
    }

    // The device-driven roster gestures — confirm/cycle/claim — routed by the pressing device's id. Confirm (South /
    // Enter) promotes the pending player owning the device; cycle (Start) rotates that device to the next slot;
    // claim (F1..F4) moves the keyboard onto the slot carried as the binding's Axis1D value. Bound in Program; over
    // stdin they act on the keyboard (the default device id).
    private IEnumerable<CommandDefinition> GestureVerbs() {
        yield return CommandDefinition.Verb(
            name: ConfirmCommand,
            description: "Confirms the pending player owning the pressing device, promoting it to active on its candidate profile (South / Enter). A first press from an unmapped device joins it; a second confirms. Over stdin it acts on the keyboard.",
            valueKind: CommandValueKind.Digital,
            handler: ConfirmHandler
        );
        yield return CommandDefinition.Verb(
            name: CycleCommand,
            description: "Rotates the pressing device to the next player slot, wrapping 1→2→3→4→1 (pad Start). Onto an empty slot it creates a pending player; onto an occupied one it joins that team. Over stdin it cycles the keyboard.",
            valueKind: CommandValueKind.Digital,
            handler: CycleHandler
        );
        yield return CommandDefinition.Verb(
            name: ClaimCommand,
            description: "Moves the keyboard onto the player slot carried as the binding's value (F1..F4). Onto an empty slot it creates a pending player; onto an occupied one it joins that team; onto its own slot a no-op.",
            valueKind: CommandValueKind.Axis1D,
            handler: ClaimHandler
        );
        yield return CommandDefinition.Verb(
            name: PrimaryCommand,
            description: "Holds the keyboard player's PRIMARY action channel while its button is down (Space), both edges — what it does is the seat kit's binding: in the default world a press launches the platformer jump, holding longer jumps higher (variable height), releasing early cuts it to a hop. A held button, not a typed verb: type player.press primary [holdSeconds] [player] to script it. Inert while the seat is pending (choosing a profile) and under an unbound kit or the free motion model.",
            valueKind: CommandValueKind.Digital,
            handler: PrimaryHandler
        );
        yield return CommandDefinition.Verb(
            name: SecondaryCommand,
            description: "Holds the keyboard player's SECONDARY action channel while its button is down (Gamepad East), both edges — what it does is the seat kit's binding: in the default world a grounded seat dashes forward (cooldown-gated). A held button, not a typed verb: type player.press secondary [holdSeconds] [player] to script it.",
            valueKind: CommandValueKind.Digital,
            handler: SecondaryHandler
        );
        yield return CommandDefinition.Verb(
            name: SouthCommand,
            description: "The context-routed Gamepad South button (both edges): confirms the pressing pad's pending profile choice (join, then confirm) while its seat is pending, and drives the PRIMARY action channel once the seat is active (both edges for variable height under the default kit's jump binding). Not meant to be typed.",
            valueKind: CommandValueKind.Digital,
            handler: SouthHandler
        );
    }

    // The gamepad's stick channels — routers, not polled — plus the sticks observability verb. The router bindings
    // fire every deflected frame; the handler routes the dispatch (with its device id) into the roster and returns
    // None (no stdout spam per frame).
    private IEnumerable<CommandDefinition> StickVerbs() {
        yield return CommandDefinition.Verb(
            name: MoveCommand,
            description: "The left stick's movement channel (Axis2D, +Y forward / +X strafe right) — routed to the owning device's player each frame; not meant to be typed.",
            valueKind: CommandValueKind.Axis2D,
            handler: MoveRouter
        );
        yield return CommandDefinition.Verb(
            name: LookCommand,
            description: "The right stick's look channel (Axis2D, +X turns right) — routed to the owning device's player each frame; not meant to be typed.",
            valueKind: CommandValueKind.Axis2D,
            handler: LookRouter
        );
        yield return CommandDefinition.Verb(
            name: "player.sticks",
            description: "Echoes every joined player's current analog — p<N> move=(x, y) look=(x, y). Values are cleared per frame, so a non-zero read only appears while a stick is actively deflected during this same command pump (the observability check for controller plumbing).",
            valueKind: CommandValueKind.Digital,
            handler: SticksHandler
        );
    }

    // The six keyboard movement verbs, targeting whichever player owns the keyboard (slot 0 by default, or a slot the
    // keyboard was reassigned onto). Each is bound to a key twice (press + release) and reads the phase to hold-or-free
    // its axis, so ONE verb covers both edges; while the keyboard's player is pending, turn-left/right cycle its
    // candidate profile instead of steering.
    private IEnumerable<CommandDefinition> MovementVerbs() {
        yield return MovementVerb(name: "player.forward", axis: SeatController.AxisForward, description: "Holds the keyboard player's forward motion while its key is down (W / Up).");
        yield return MovementVerb(name: "player.back", axis: SeatController.AxisBack, description: "Holds the keyboard player's backward motion while its key is down (S / Down).");
        yield return MovementVerb(name: "player.strafe-left", axis: SeatController.AxisStrafeLeft, description: "Holds the keyboard player's left strafe while its key is down (Q).");
        yield return MovementVerb(name: "player.strafe-right", axis: SeatController.AxisStrafeRight, description: "Holds the keyboard player's right strafe while its key is down (E).");
        yield return MovementVerb(name: "player.turn-left", axis: SeatController.AxisTurnLeft, description: "Turns the keyboard player left while its key is down (A / Left); cycles the candidate profile while pending.");
        yield return MovementVerb(name: "player.turn-right", axis: SeatController.AxisTurnRight, description: "Turns the keyboard player right while its key is down (D / Right); cycles the candidate profile while pending.");
    }
    private CommandResult MoveRouter(CommandContext context) {
        m_roster.RouteMove(slot: context.Slot, value: context.Value.AsAxis2D);

        return CommandResult.None;
    }
    private CommandResult LookRouter(CommandContext context) {
        m_roster.RouteLook(slot: context.Slot, value: context.Value.AsAxis2D);

        return CommandResult.None;
    }
    private CommandResult SticksHandler(CommandContext context) {
        var segments = new List<string>(capacity: PlayerRoster.MaxSlots);

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is not { } seat) {
                continue;
            }

            var move = seat.AnalogMove;
            var look = seat.AnalogLook;

            segments.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"p{PlayerRoster.DisplayNumber(slot: slot)} move=({move.X:0.00}, {move.Y:0.00}) look=({look.X:0.00}, {look.Y:0.00})"
            ));
        }

        return new CommandResult(Output: $"[player.sticks: {string.Join(separator: " | ", values: segments)}]");
    }

    // The drive-a-player wire verbs. Each takes a zero-copy WireArgs (parsed from the stdin line span), marks every
    // failure IsError so `wire.ack quiet` drops only successes, and gates its success-echo on args.Echo so a quiet flood
    // builds no ack string. The error strings are the wire contract. player.where is a query (not AcknowledgementOnly) — its data
    // always echoes.
    private CommandResult RunHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (4 or 5)) {
            return new CommandResult(Output: "[player.run: expected 4 values — <forward> <strafe> <turn> <seconds> — plus an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 4, verb: "player.run");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (PendingTapeError(index: index, verb: "player.run") is { } pendingError) {
            return pendingError;
        }

        if (!args.TryFloat(index: 0, value: out var forward) ||
            !args.TryFloat(index: 1, value: out var strafe) ||
            !args.TryFloat(index: 2, value: out var turn) ||
            !args.TryFloat(index: 3, value: out var seconds)) {
            return new CommandResult(Output: "[player.run: could not parse the four values as numbers]") {
                IsError = true,
            };
        }

        if (!(seconds > 0f)) {
            return new CommandResult(Output: "[player.run: <seconds> must be greater than 0]") {
                IsError = true,
            };
        }

        forward = Math.Clamp(value: forward, min: -1f, max: 1f);
        strafe = Math.Clamp(value: strafe, min: -1f, max: 1f);
        turn = Math.Clamp(value: turn, min: -1f, max: 1f);

        m_link.SubmitCommand(command: new WorldCommand.EnqueueSegment(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            Intent: new PlayerIntent(MoveForward: forward, MoveStrafe: strafe, Turn: turn),
            Seconds: seconds
        ));

        return Echoed(args: in args, handler: $"[player.run: forward={forward:0.##} strafe={strafe:0.##} turn={turn:0.##} for {seconds:0.##}s]");
    }
    private CommandResult WarpHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return new CommandResult(Output: "[player.warp: expected 2 values — <x> <z> — plus an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 2, verb: "player.warp");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (!args.TryFloat(index: 0, value: out var x) ||
            !args.TryFloat(index: 1, value: out var z)) {
            return new CommandResult(Output: "[player.warp: could not parse <x> <z> as numbers]") {
                IsError = true,
            };
        }

        m_link.SubmitCommand(command: new WorldCommand.Teleport(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            Position: new Vector3(x: x, y: 0f, z: z),
            YawRadians: 0f,
            PitchRadians: 0f,
            RollRadians: 0f,
            Kind: TeleportKind.Warp
        ));

        return Echoed(args: in args, handler: $"[player.warp: ({x:0.00}, {z:0.00})]");
    }
    private CommandResult FaceHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return new CommandResult(Output: "[player.face: expected 1 value — <degrees> — plus an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 1, verb: "player.face");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (!args.TryFloat(index: 0, value: out var degrees)) {
            return new CommandResult(Output: "[player.face: could not parse <degrees> as a number]") {
                IsError = true,
            };
        }

        m_link.SubmitCommand(command: new WorldCommand.Face(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            YawRadians: (degrees * (MathF.PI / 180f))
        ));

        return Echoed(args: in args, handler: $"[player.face: {degrees:0}°]");
    }
    private CommandResult ReconcileHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (3 or 4 or 5)) {
            return new CommandResult(Output: "[player.reconcile: expected 3 values — <x> <z> <yawDegrees> — plus an optional smoothing time and player index]") {
                IsError = true,
            };
        }

        // Layout: <x> <z> <yawDegrees> [seconds] [player]. The trailing player index is the LAST token (as with every
        // drive-a-player verb); the optional [seconds] appears only in the full 5-token form. So the index sits at token 4
        // when seconds is present, token 3 otherwise — and is absent (default player 1) in the bare 3-token form.
        var hasSeconds = (args.Count == 5);

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: (hasSeconds ? 4 : 3), verb: "player.reconcile");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (!args.TryFloat(index: 0, value: out var x) ||
            !args.TryFloat(index: 1, value: out var z) ||
            !args.TryFloat(index: 2, value: out var degrees)) {
            return new CommandResult(Output: "[player.reconcile: could not parse <x> <z> <yawDegrees> as numbers]") {
                IsError = true,
            };
        }

        var seconds = DefaultReconcileSeconds;

        if (hasSeconds && !args.TryFloat(index: 3, value: out seconds)) {
            return new CommandResult(Output: "[player.reconcile: could not parse <seconds> as a number]") {
                IsError = true,
            };
        }

        seconds = Math.Clamp(value: seconds, min: MinReconcileSeconds, max: MaxReconcileSeconds);

        m_link.SubmitCommand(command: new WorldCommand.Reconcile(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            X: x,
            Z: z,
            YawRadians: (degrees * (MathF.PI / 180f)),
            Seconds: seconds
        ));

        return Echoed(args: in args, handler: $"[player.reconcile: p{index} → ({x:0.00}, {z:0.00}) yaw={degrees:0}° over {seconds:0.##}s]");
    }
    private CommandResult WhereHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return new CommandResult(Output: "[player.where: expected at most 1 value — an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 0, verb: "player.where");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        // A query verb (not AcknowledgementOnly): the pose read-back IS the answer, so it always echoes — even under wire.ack quiet.
        // Every pose is the server's to report; the answer prints verbatim, and its verdict rides through as IsError so a
        // miss the client-side guard did not catch still reaches wire.errors.
        var answer = m_link.Query(query: new WorldQuery.PlayerWhere(Index: index));

        return new CommandResult(Output: answer.Text) {
            IsError = answer.Refused,
        };
    }
    private CommandResult StopHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return new CommandResult(Output: "[player.stop: expected at most 1 value — an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 0, verb: "player.stop");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        m_link.SubmitCommand(command: new WorldCommand.Stop(Principal: WorldPrincipal.Console, EntityIndex: (index - 1)));

        // A seat's held device state is client-side: free it here so the stop covers both halves.
        if (IsSeat(index: index)) {
            m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.ReleaseAllHeld();
        }

        return Echoed(args: in args, handler: $"[player.stop: player {index} — tape cleared, keys released]");
    }
    private CommandResult MotionHandler(CommandContext context, WireArgs args) {
        // Token 0 is the MODE only when it names one; otherwise the whole (0- or 1-token) tail is just the player index for
        // a read-back. So the mode is present only when token 0 parses as grounded/free — which also disambiguates a bare
        // `player.motion 7` (echo player 7) from `player.motion free` (set player 1) with no positional guesswork.
        var model = MotionModel.Grounded;
        var hasMode = ((args.Count >= 1) && TryParseModel(token: args[0], model: out model));

        var (player, index, error) = ResolveModeTarget(args: in args, verb: "player.motion", choices: "grounded|free", hasMode: hasMode);

        if (error is { } modeError) {
            return modeError;
        }

        if (hasMode) {
            m_link.SubmitCommand(command: new WorldCommand.SetMotion(Principal: WorldPrincipal.Console, EntityIndex: (index - 1), Model: model));

            return Echoed(args: in args, handler: $"[player.motion: player {index} → {ModelWord(model: model)}]");
        }

        // No mode: a read-back — echo the target's current model. Always surfaced (a query answer), like player.where.
        return new CommandResult(Output: $"[player.motion: player {index} is {ModelWord(model: player!.Model)}]");
    }
    private CommandResult ControlHandler(CommandContext context, WireArgs args) {
        // Token 0 is the MODE only when it names one; otherwise the whole (0- or 1-token) tail is just the player index
        // for a read-back — the same positional shape as player.motion, so a bare `player.control 7` echoes player 7's
        // source while `player.control idle` sets player 1 with no positional guesswork.
        var source = IntentSource.Live;
        var hasMode = ((args.Count >= 1) && TryParseIntentSource(token: args[0], source: out source));

        var (player, index, error) = ResolveModeTarget(args: in args, verb: "player.control", choices: "live|idle|wander", hasMode: hasMode);

        if (error is { } modeError) {
            return modeError;
        }

        // A PENDING seat's source cannot be set — its inputs drive the profile picker, not gameplay, so a source set
        // now would sit dormant and take effect only on confirm. Reuse the tape verbs' pending guard (seats only;
        // population entries 5..128 are never pending). Gates BOTH set and read — a pending seat is always Live anyway.
        if (PendingTapeError(index: index, verb: "player.control") is { } pendingError) {
            return pendingError;
        }

        if (hasMode) {
            m_link.SubmitCommand(command: new WorldCommand.SetControl(Principal: WorldPrincipal.Console, EntityIndex: (index - 1), Source: source));

            // The seat's client-side source copy gates the live device producers; write it in the same command so the
            // mask lands with no tick gap (dropping any held keys/lanes on the transition).
            if (IsSeat(index: index)) {
                m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.SetIntentSource(source: source);
            }

            return Echoed(args: in args, handler: $"[player.control: p{index} {SourceWord(source: source)}]");
        }

        // No mode: a read-back — echo the target's current source. Always surfaced (a query answer), like player.motion.
        return new CommandResult(Output: $"[player.control: p{index} is {SourceWord(source: player!.Source)}]");
    }
    private CommandResult EngageHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return new CommandResult(Output: "[player.engage: expected a screen index — plus an optional player index]") {
                IsError = true,
            };
        }

        if (!args.TryInt(index: 0, value: out var screenIndex)) {
            return new CommandResult(Output: $"[player.engage: screen index '{args[0].ToString()}' must be an integer]") {
                IsError = true,
            };
        }

        // The player index (if any) trails the screen index at token 1.
        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 1, verb: "player.engage");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (FindScreen(screenIndex: screenIndex) is not { } screen) {
            return new CommandResult(Output: $"[player.engage: no screen {screenIndex} — see world.screens]") {
                IsError = true,
            };
        }

        // Route policy is mechanical data-following: the screen must permit engagement, carry a machine to receive the
        // input (an engageable None/test-pattern/unbooted screen has nothing to control — engage errors loudly), and,
        // when its route sets a radius, the avatar must be within it of the screen's origin (a plain planar distance).
        // LOOPBACK-ONLY: the radius check reads the server body's pose in-process; a socket transport checks the
        // radius server-side in the engage command (machine presence stays a client check — the binder is client-owned).
        if (!screen.Route.Engageable) {
            return new CommandResult(Output: $"[player.engage: screen {screenIndex} is not engageable]") {
                IsError = true,
            };
        }

        // route.autoInsert: engaging an empty engageable screen first boots its selected magazine entry (the "walk over,
        // press the button, the screen lights" gesture is one act, not an insert then an engage).
        if (screen.Route.AutoInsert && !m_screens.HasMachine(index: screenIndex) && m_screens.TryMagazine(index: screenIndex, selected: out var selected, magazine: out _)) {
            _ = m_screens.TrySelect(index: screenIndex, entry: selected);
        }

        if (!m_screens.HasMachine(index: screenIndex)) {
            return new CommandResult(Output: $"[player.engage: screen {screenIndex} has no machine to control — screen.insert a cart first]") {
                IsError = true,
            };
        }

        if (screen.Route.EngageRadius > 0f) {
            var deltaX = (player.Position.X - screen.Origin.X);
            var deltaZ = (player.Position.Z - screen.Origin.Z);
            var distance = MathF.Sqrt(x: ((deltaX * deltaX) + (deltaZ * deltaZ)));

            if (distance > screen.Route.EngageRadius) {
                return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[player.engage: p{index} is {distance:0.0}u from screen {screenIndex} — within {screen.Route.EngageRadius:0.0}u to engage (player.warp closer)]")) {
                    IsError = true,
                };
            }
        }

        if (!m_engagement.Engage(index: index, body: player, screenIndex: screenIndex)) {
            return new CommandResult(Output: $"[player.engage: p{index} lacks control over screen {screenIndex} — see world.grants]") {
                IsError = true,
            };
        }

        return Echoed(args: in args, handler: $"[player.engage: p{index} engaged screen {screenIndex}]");
    }
    private CommandResult DisengageHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return new CommandResult(Output: "[player.disengage: expected at most 1 value — an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 0, verb: "player.disengage");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        return (m_engagement.Disengage(index: index, body: player)
            ? Echoed(args: in args, handler: $"[player.disengage: p{index} disengaged]")
            : Echoed(args: in args, handler: $"[player.disengage: p{index} was not engaged]"));
    }

    // The declared screen with the given engine index, or null when no screen declares it.
    private WorldScreen? FindScreen(int screenIndex) {
        foreach (var screen in m_definition.Screens) {
            if (screen.Index == screenIndex) {
                return screen;
            }
        }

        return null;
    }

    // The shared front matter of the two mode-or-echo verbs (player.motion / player.control): validate the ≤2-token
    // shape, reject a token 0 that is neither the mode nor a bare player index, and resolve the target. The caller has
    // already parsed token 0 and passes hasMode in; on success this returns the resolved player + display index (Error
    // null), else a populated IsError result keyed off the verb name and its mode <choices>.
    private (WorldBody? Player, int Index, CommandResult? Error) ResolveModeTarget(in WireArgs args, string verb, string choices, bool hasMode) {
        if (args.Count > 2) {
            return (Player: null, Index: 0, Error: new CommandResult(Output: $"[{verb}: expected at most 2 tokens — an optional [{choices}] and an optional player index]") {
                IsError = true,
            });
        }

        if ((args.Count >= 1) && !hasMode && !args.TryInt(index: 0, value: out _)) {
            return (Player: null, Index: 0, Error: new CommandResult(Output: $"[{verb}: expected {choices} (or a player index) — {verb} [{choices}] [player]]") {
                IsError = true,
            });
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: (hasMode ? 1 : 0), verb: verb);

        if (player is null) {
            return (Player: null, Index: index, Error: new CommandResult(Output: error!) {
                IsError = true,
            });
        }

        return (Player: player, Index: index, Error: null);
    }

    // Parse an intent-source token (case-insensitive). Returns false for anything that is not live/idle/wander.
    private static bool TryParseIntentSource(ReadOnlySpan<char> token, out IntentSource source) {
        if (token.Equals(other: "live", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            source = IntentSource.Live;

            return true;
        }

        if (token.Equals(other: "idle", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            source = IntentSource.Idle;

            return true;
        }

        if (token.Equals(other: "wander", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            source = IntentSource.Wander;

            return true;
        }

        source = IntentSource.Live;

        return false;
    }
    private static string SourceWord(IntentSource source) => (source switch {
        IntentSource.Idle => "idle",
        IntentSource.Wander => "wander",
        _ => "live",
    });

    // The success-echo tail every side-effecting wire verb shares: echo the formatted line when acks are on, else drop
    // it (CommandResult.None). On a quiet pipe (args.Echo false) the EchoHandler skips every format append and no ack
    // string is built — the zero-alloc flood contract. Formats under the invariant culture, so echoes are locale-stable.
    private static CommandResult Echoed(in WireArgs args, [InterpolatedStringHandlerArgument(nameof(args))] ref EchoHandler handler) {
        return (args.Echo ? new CommandResult(Output: handler.ToStringAndClear()) : CommandResult.None);
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
                ? new DefaultInterpolatedStringHandler(literalLength: literalLength, formattedCount: formattedCount, provider: CultureInfo.InvariantCulture)
                : default);
        }

        public void AppendLiteral(string value) {
            m_inner.AppendLiteral(value: value);
        }
        public void AppendFormatted<T>(T value) {
            m_inner.AppendFormatted(value: value);
        }
        public void AppendFormatted<T>(T value, string? format) {
            m_inner.AppendFormatted(value: value, format: format);
        }
        public string ToStringAndClear() {
            return m_inner.ToStringAndClear();
        }
    }

    private CommandResult FlyHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (7 or 8)) {
            return new CommandResult(Output: "[player.fly: expected 7 values — <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> — plus an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 7, verb: "player.fly");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (PendingTapeError(index: index, verb: "player.fly") is { } pendingError) {
            return pendingError;
        }

        if (!args.TryFloat(index: 0, value: out var forward) ||
            !args.TryFloat(index: 1, value: out var strafe) ||
            !args.TryFloat(index: 2, value: out var up) ||
            !args.TryFloat(index: 3, value: out var yaw) ||
            !args.TryFloat(index: 4, value: out var pitch) ||
            !args.TryFloat(index: 5, value: out var roll) ||
            !args.TryFloat(index: 6, value: out var seconds)) {
            return new CommandResult(Output: "[player.fly: could not parse the seven values as numbers]") {
                IsError = true,
            };
        }

        if (!(seconds > 0f)) {
            return new CommandResult(Output: "[player.fly: <seconds> must be greater than 0]") {
                IsError = true,
            };
        }

        forward = Math.Clamp(value: forward, min: -1f, max: 1f);
        strafe = Math.Clamp(value: strafe, min: -1f, max: 1f);
        up = Math.Clamp(value: up, min: -1f, max: 1f);
        yaw = Math.Clamp(value: yaw, min: -1f, max: 1f);
        pitch = Math.Clamp(value: pitch, min: -1f, max: 1f);
        roll = Math.Clamp(value: roll, min: -1f, max: 1f);

        // The fly channel order (forward, strafe, up, yaw, pitch, roll) maps onto PlayerIntent (MoveForward, MoveStrafe,
        // Turn, MoveUp, Pitch, Roll) — the "yaw" channel is the Turn rate.
        m_link.SubmitCommand(command: new WorldCommand.EnqueueSegment(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            Intent: new PlayerIntent(MoveForward: forward, MoveStrafe: strafe, Turn: yaw, MoveUp: up, Pitch: pitch, Roll: roll),
            Seconds: seconds
        ));

        return Echoed(args: in args, handler: $"[player.fly: fwd={forward:0.##} strafe={strafe:0.##} up={up:0.##} yaw={yaw:0.##} pitch={pitch:0.##} roll={roll:0.##} for {seconds:0.##}s]");
    }
    private CommandResult PoseHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (6 or 7)) {
            return new CommandResult(Output: "[player.pose: expected 6 values — <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> — plus an optional player index]") {
                IsError = true,
            };
        }

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: 6, verb: "player.pose");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (!args.TryFloat(index: 0, value: out var x) ||
            !args.TryFloat(index: 1, value: out var y) ||
            !args.TryFloat(index: 2, value: out var z) ||
            !args.TryFloat(index: 3, value: out var yawDegrees) ||
            !args.TryFloat(index: 4, value: out var pitchDegrees) ||
            !args.TryFloat(index: 5, value: out var rollDegrees)) {
            return new CommandResult(Output: "[player.pose: could not parse the six values as numbers]") {
                IsError = true,
            };
        }

        const float toRadians = (MathF.PI / 180f);

        m_link.SubmitCommand(command: new WorldCommand.Teleport(
            Principal: WorldPrincipal.Console,
            EntityIndex: (index - 1),
            Position: new Vector3(x: x, y: y, z: z),
            YawRadians: (yawDegrees * toRadians),
            PitchRadians: (pitchDegrees * toRadians),
            RollRadians: (rollDegrees * toRadians),
            Kind: TeleportKind.Pose
        ));

        return Echoed(args: in args, handler: $"[player.pose: ({x:0.00}, {y:0.00}, {z:0.00}) yaw={yawDegrees:0}° pitch={pitchDegrees:0}° roll={rollDegrees:0}°]");
    }
    private CommandResult PressHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return new CommandResult(Output: "[player.press: expected an action lane name — plus an optional hold time and player index]") {
                IsError = true,
            };
        }

        if (!TryParseLane(token: args[0], lane: out var lane)) {
            return new CommandResult(Output: $"[player.press: unknown action lane '{args[0].ToString()}' — known lanes: {KnownActionLanes}]") {
                IsError = true,
            };
        }

        // Layout: <action> [holdSeconds] [player]. token0 is the lane NAME; a second token is the hold, a third the player
        // index — so the index sits at token 2 when a hold is present, token 1 otherwise (and defaults to player 1 if absent).
        var hasHold = (args.Count >= 2);

        var (player, index, error) = ResolveTarget(args: in args, requiredCount: (hasHold ? 2 : 1), verb: "player.press");

        if (player is null) {
            return new CommandResult(Output: error!) {
                IsError = true,
            };
        }

        if (PendingTapeError(index: index, verb: "player.press") is { } pendingError) {
            return pendingError;
        }

        float? holdSeconds = null;

        if (hasHold) {
            if (!args.TryFloat(index: 1, value: out var authoredHoldSeconds)) {
                return new CommandResult(Output: "[player.press: could not parse <holdSeconds> as a number]") {
                    IsError = true,
                };
            }

            holdSeconds = Math.Clamp(value: authoredHoldSeconds, min: 0f, max: WorldBody.MaxActionHoldSeconds);
        }

        m_link.SubmitCommand(command: new WorldCommand.PressLane(Principal: WorldPrincipal.Console, EntityIndex: (index - 1), Lane: lane, HoldSeconds: holdSeconds));

        if (holdSeconds is { } seconds) {
            return Echoed(args: in args, handler: $"[player.press: {LaneWord(lane: lane)} p{index} for {seconds:0.###}s]");
        }

        return Echoed(args: in args, handler: $"[player.press: {LaneWord(lane: lane)} p{index} for {WorldBody.DefaultActionHoldSteps} host steps]");
    }

    // Parse an action-lane name (case-insensitive) to its ActionLanes bit. Grows in lockstep with KnownActionLanes.
    private static bool TryParseLane(ReadOnlySpan<char> token, out ActionLanes lane) {
        if (token.Equals(other: "primary", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            lane = ActionLanes.Primary;

            return true;
        }

        if (token.Equals(other: "secondary", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            lane = ActionLanes.Secondary;

            return true;
        }

        lane = ActionLanes.None;

        return false;
    }
    private static string LaneWord(ActionLanes lane) => (lane switch {
        ActionLanes.Primary => "primary",
        ActionLanes.Secondary => "secondary",
        _ => lane.ToString(),
    });

    // The keyboard PRIMARY gesture (Space, both edges): a held button onto the owning seat's Primary channel. A typed
    // `player.primary` is refused — the scripted path is the argument-bearing player.press — mirroring the six movement
    // verbs' typed guard.
    private CommandResult PrimaryHandler(CommandContext context) {
        if (context.Parse is not null) {
            return new CommandResult(Output: "[player.primary: a held action button, not a typed verb — use player.press primary [holdSeconds] [player] to script it]") { IsError = true };
        }

        var slot = context.Slot;

        if (m_roster.Seat(slot: slot) is null) {
            return CommandResult.None;
        }

        // Inert while the seat is choosing a profile (its inputs drive the picker, not gameplay) — the action waits for confirm.
        if (m_roster.IsPending(slot: slot)) {
            return CommandResult.None;
        }

        return ApplyLaneEdge(context: context, slot: slot, lane: ActionLanes.Primary);
    }

    // The Gamepad-East SECONDARY gesture (both edges): a held button onto the owning seat's Secondary channel — the
    // same shape as PrimaryHandler over the other lane.
    private CommandResult SecondaryHandler(CommandContext context) {
        if (context.Parse is not null) {
            return new CommandResult(Output: "[player.secondary: a held action button, not a typed verb — use player.press secondary [holdSeconds] [player] to script it]") { IsError = true };
        }

        var slot = context.Slot;

        if ((m_roster.Seat(slot: slot) is null) || m_roster.IsPending(slot: slot)) {
            return CommandResult.None;
        }

        return ApplyLaneEdge(context: context, slot: slot, lane: ActionLanes.Secondary);
    }

    // The context-routed Gamepad South gesture (both edges): confirm while the pressing pad's seat is pending or the pad
    // is unmapped (the confirm flow, Started edge only), the Primary channel once the seat is active (both edges for
    // variable height under the default kit's jump binding).
    private CommandResult SouthHandler(CommandContext context) {
        var slot = context.Slot;

        // The first accepted signal committed this device-to-slot assignment while the snapshot was built. When the
        // seating policy attaches a new pad to already-active player 1, consume this first South edge as the documented
        // seat gesture. AssignedSlot is recorded, so replay makes the same seat-vs-jump decision without a local device.
        if (context.AssignedSlot && m_roster.IsJoined(slot: slot) && !m_roster.IsPending(slot: slot)) {
            return new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} seated]");
        }

        // An empty recorded lane is a first-press join. Do not immediately confirm it: the first South joins pending,
        // and a later press confirms, identically in live input and replay.
        if (!m_roster.IsJoined(slot: slot)) {
            if (context.Phase is not CommandPhase.Started) {
                return CommandResult.None;
            }

            _ = m_roster.JoinPending(slot: slot, origin: ParticipantOrigin.Device);

            return new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {m_roster.Describe()}");
        }

        if (m_roster.IsPending(slot: slot)) {
            return ((context.Phase is CommandPhase.Started)
                ? ConfirmSlot(slot: slot)
                : CommandResult.None);
        }

        // Active seat: South is the Primary channel (both edges).
        return ApplyLaneEdge(context: context, slot: slot, lane: ActionLanes.Primary);
    }

    // Route a bound-button edge onto the slot's seat action channel: a press/continuous edge holds the lane, a release
    // frees it, so a live control gets variable behavior under the kit's binding. The single owner of the
    // phase→lane-edge mapping the keyboard and pads share.
    private CommandResult ApplyLaneEdge(CommandContext context, int slot, ActionLanes lane) {
        if (m_roster.Seat(slot: slot) is { } seat) {
            if (context.Phase is CommandPhase.Started or CommandPhase.Active) {
                seat.PressLaneEdge(lane: lane);
            } else {
                seat.ReleaseLaneEdge(lane: lane);
            }
        }

        return CommandResult.None;
    }

    // A pending local seat (2..4) is choosing a profile — its inputs drive the picker, not locomotion — so a tape
    // enqueued now would sit dormant and burst the instant the seat confirms. The tape verbs (run/fly) refuse it; the
    // teleport verbs (warp/face/pose/where/stop) stay allowed. Population entries (5..128) are never pending. Returns
    // the error result, or null when the target may accept a tape.
    private CommandResult? PendingTapeError(int index, string verb) {
        if ((index <= PlayerRoster.MaxSlots) && m_roster.IsPending(slot: PlayerRoster.SlotFromDisplay(number: index))) {
            return new CommandResult(Output: $"[{verb}: player {index} is pending — confirm a profile first (South/Enter or player.profile)]") {
                IsError = true,
            };
        }

        return null;
    }

    // Parse a motion-model token (case-insensitive). Returns false for anything that is not grounded/free.
    private static bool TryParseModel(ReadOnlySpan<char> token, out MotionModel model) {
        if (token.Equals(other: "grounded", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            model = MotionModel.Grounded;

            return true;
        }

        if (token.Equals(other: "free", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            model = MotionModel.Free;

            return true;
        }

        model = MotionModel.Grounded;

        return false;
    }
    private static string ModelWord(MotionModel model) => ((model == MotionModel.Free) ? "free" : "grounded");
    private CommandResult JoinHandler(CommandContext context, WireArgs args) {
        if (args.Count > 2) {
            return new CommandResult(Output: "[player.join: expected at most 2 tokens — an optional profile name and/or a slot 2..4]") { IsError = true };
        }

        // Split the (up to two) tokens into an optional slot (an int in 2..4) and an optional profile name (either
        // order): a token that parses as a slot is the slot, otherwise it is a profile name.
        var slotIndex = -1;
        string? profileName = null;

        for (var tokenIndex = 0; (tokenIndex < args.Count); tokenIndex++) {
            if (args.TryInt(index: tokenIndex, value: out var n) && (n >= 2) && (n <= PlayerRoster.MaxSlots)) {
                if (slotIndex >= 0) {
                    return new CommandResult(Output: "[player.join: gave two slot numbers — expected <profile> and/or <slot 2..4>]") { IsError = true };
                }

                slotIndex = PlayerRoster.SlotFromDisplay(number: n);
            } else if (profileName is null) {
                profileName = args[tokenIndex].ToString();
            } else {
                return new CommandResult(Output: "[player.join: gave two profile names — expected <profile> and/or <slot 2..4>]") { IsError = true };
            }
        }

        // A named profile joins directly ACTIVE (one-shot); no profile joins PENDING (a candidate is chosen, then
        // confirm). The profile must exist and not already be in use by another active player.
        if (profileName is not null) {
            if (m_roster.FindProfile(name: profileName) is not { } profile) {
                return new CommandResult(Output: $"[player.join: no profile named '{profileName}' — see profile.list]") { IsError = true };
            }

            if (m_roster.ActiveSlotUsing(profile: profile) >= 0) {
                return new CommandResult(Output: $"[player.join: profile '{profile.Name}' is already in use — see world.players]") { IsError = true };
            }

            var joined = ((slotIndex >= 0) ? (m_roster.JoinActive(slot: slotIndex, profile: profile, origin: ParticipantOrigin.Script) ? slotIndex : -1) : m_roster.JoinActiveNextFree(profile: profile, origin: ParticipantOrigin.Script));

            return ReportJoin(slot: joined, requestedSlot: slotIndex, active: true);
        }

        var pending = ((slotIndex >= 0) ? (m_roster.JoinPending(slot: slotIndex, origin: ParticipantOrigin.Script) ? slotIndex : -1) : m_roster.JoinPendingNextFree(origin: ParticipantOrigin.Script));

        return ReportJoin(slot: pending, requestedSlot: slotIndex, active: false);
    }

    // Format a join result: a full roster, an already-joined explicit slot, or a fresh join with the roster echoed.
    private CommandResult ReportJoin(int slot, int requestedSlot, bool active) {
        if (slot < 0) {
            return ((requestedSlot >= 0)
                ? new CommandResult(Output: $"[player.join: player {PlayerRoster.DisplayNumber(slot: requestedSlot)} is already joined]")
                : new CommandResult(Output: $"[player.join: the roster is full ({PlayerRoster.MaxSlots} players)]") { IsError = true });
        }

        var word = (active ? "joined active" : "joined pending");

        return new CommandResult(Output: $"[player.join: player {PlayerRoster.DisplayNumber(slot: slot)} {word}] {m_roster.Describe()}");
    }
    private CommandResult ProfileHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return new CommandResult(Output: "[player.profile: expected a profile name plus an optional player index — player.profile <name> [n]]") { IsError = true };
        }

        if (!WorldArgs.TryParseIndex(args: in args, at: 1, min: 1, max: PlayerRoster.MaxSlots, fallback: 1, value: out var index)) {
            return new CommandResult(Output: $"[player.profile: player index must be an integer 1..{PlayerRoster.MaxSlots}]") { IsError = true };
        }

        var profileName = args[0].ToString();

        if (m_roster.FindProfile(name: profileName) is not { } profile) {
            return new CommandResult(Output: $"[player.profile: no profile named '{profileName}' — see profile.list]") { IsError = true };
        }

        return (m_roster.SetProfile(slot: PlayerRoster.SlotFromDisplay(number: index), profile: profile) switch {
            SetProfileOutcome.NotJoined => new CommandResult(Output: $"[player.profile: player {index} is not joined — see world.players]") { IsError = true },
            SetProfileOutcome.InUse => new CommandResult(Output: $"[player.profile: profile '{profile.Name}' is already in use — see world.players]") { IsError = true },
            _ => new CommandResult(Output: $"[player.profile: player {index} is now {profile.Name}] {m_roster.Describe()}"),
        });
    }
    private CommandResult AssignHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return new CommandResult(Output: "[player.assign: expected a device token and a slot — player.assign <kbd|padN> <slot 1..4>]") { IsError = true };
        }

        var deviceToken = args[0].ToString();

        if (!m_roster.TryResolveDeviceToken(token: deviceToken, device: out var device)) {
            return new CommandResult(Output: $"[player.assign: no device '{deviceToken}' — see world.devices]") { IsError = true };
        }

        if (!WorldArgs.TryParseIndex(args: in args, at: 1, min: 1, max: PlayerRoster.MaxSlots, fallback: null, value: out var slot)) {
            return new CommandResult(Output: $"[player.assign: <slot> must be an integer 1..{PlayerRoster.MaxSlots}]") { IsError = true };
        }

        return DescribeAssign(verb: "player.assign", outcome: m_roster.AssignDevice(device: device, targetSlot: PlayerRoster.SlotFromDisplay(number: slot)), slot: PlayerRoster.SlotFromDisplay(number: slot));
    }
    private CommandResult ConfirmHandler(CommandContext context) {
        // Physical/snapshot input is lane-addressed. A text invocation deliberately retains the documented local
        // keyboard-device behavior (player.assign may have moved it since boot).
        if ((context.Parse is null) && context.AssignedSlot && m_roster.IsJoined(slot: context.Slot) && !m_roster.IsPending(slot: context.Slot)) {
            return DescribeConfirm(outcome: ConfirmOutcome.Seated, slot: context.Slot, device: null);
        }

        var (outcome, slot) = ((context.Parse is null)
            ? ConfirmInputSlot(slot: context.Slot)
            : m_roster.Confirm(device: context.DeviceId));

        return DescribeConfirm(outcome: outcome, slot: slot, device: context.DeviceId);
    }
    private (ConfirmOutcome Outcome, int Slot) ConfirmInputSlot(int slot) {
        if (!m_roster.IsJoined(slot: slot)) {
            return (m_roster.JoinPending(slot: slot, origin: ParticipantOrigin.Device)
                ? (Outcome: ConfirmOutcome.Joined, Slot: slot)
                : (Outcome: ConfirmOutcome.Ignored, Slot: -1));
        }

        return m_roster.Confirm(slot: slot);
    }
    private CommandResult ConfirmSlot(int slot) {
        var (outcome, affectedSlot) = m_roster.Confirm(slot: slot);

        return DescribeConfirm(outcome: outcome, slot: affectedSlot, device: null);
    }
    private CommandResult DescribeConfirm(ConfirmOutcome outcome, int slot, InputDeviceId? device) {

        return (outcome switch {
            ConfirmOutcome.Confirmed => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} confirmed] {m_roster.Describe()}"),
            ConfirmOutcome.Joined => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {m_roster.Describe()}"),
            ConfirmOutcome.Seated when (device is { } source) => new CommandResult(Output: $"[player.confirm: {m_roster.DeviceToken(device: source)} seated with player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            ConfirmOutcome.Seated => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} seated]"),
            ConfirmOutcome.AlreadyActive => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} is already active]"),
            _ => new CommandResult(Output: $"[player.confirm: the roster is full ({PlayerRoster.MaxSlots} players)]") { IsError = true },
        });
    }
    private CommandResult CycleHandler(CommandContext context) {
        var (outcome, slot) = m_roster.CycleDevice(device: context.DeviceId);

        return DescribeAssign(verb: "player.cycle", outcome: outcome, slot: slot);
    }
    private CommandResult ClaimHandler(CommandContext context) {
        // The target slot rides the binding's Axis1D value as a 1-based player number (the clean scalar constant a
        // CommandBinding carries — CommandValue.Axis(float)); a typed invocation with no value is a no-op.
        var player = (int)MathF.Round(x: context.Value.AsAxis1D);

        if ((player < 1) || (player > PlayerRoster.MaxSlots)) {
            return CommandResult.None;
        }

        return DescribeAssign(verb: ClaimCommand, outcome: m_roster.AssignDevice(device: context.DeviceId, targetSlot: PlayerRoster.SlotFromDisplay(number: player)), slot: PlayerRoster.SlotFromDisplay(number: player));
    }

    // Format a device-reassignment outcome, echoing the roster on a change.
    private CommandResult DescribeAssign(string verb, AssignOutcome outcome, int slot) {
        return (outcome switch {
            AssignOutcome.CreatedPending => new CommandResult(Output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {m_roster.Describe()}"),
            AssignOutcome.JoinedTeam => new CommandResult(Output: $"[{verb}: device moved to player {PlayerRoster.DisplayNumber(slot: slot)}] {m_roster.Describe()}"),
            AssignOutcome.NoOp => new CommandResult(Output: $"[{verb}: device already on player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            _ => new CommandResult(Output: $"[{verb}: the roster is full ({PlayerRoster.MaxSlots} players)]") { IsError = true },
        });
    }
    private CommandResult LeaveHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return new CommandResult(Output: "[player.leave: expected a player index — player.leave <n>, n in 2..4]") { IsError = true };
        }

        if (!WorldArgs.TryParseIndex(args: in args, at: 0, min: 2, max: PlayerRoster.MaxSlots, fallback: null, value: out var n)) {
            return new CommandResult(Output: $"[player.leave: <n> must be an integer 2..{PlayerRoster.MaxSlots}]") { IsError = true };
        }

        return (m_roster.Leave(slot: PlayerRoster.SlotFromDisplay(number: n))
            ? new CommandResult(Output: $"[player.leave: player {n} left] {m_roster.Describe()}")
            : new CommandResult(Output: $"[player.leave: player {n} is not joined]") { IsError = true });
    }

    // Resolve the target body from an optional trailing index at args[requiredCount] (default player 1), reaching the
    // whole entity table: 1..4 are the local roster seats (gated on roster membership), 5..128 the simulated entries
    // (each owning its own body). Returns an error (naming world.players for a seat, world.population for an entry)
    // when the index is malformed or names an inactive one. The m_server/m_population liveness reads are in-process, so
    // this is the loopback's fast path with the sharper wording (seat vs population entry); off the loopback the server's
    // own QueryAnswer.Refused verdict carries the same miss, and the handler renders it as IsError either way.
    private (WorldBody? Player, int Index, string? Error) ResolveTarget(in WireArgs args, int requiredCount, string verb) {
        if (!WorldArgs.TryParseIndex(args: in args, at: requiredCount, min: 1, max: WorldPopulation.MaxPopulation, fallback: 1, value: out var index)) {
            return (Player: null, Index: 0, Error: $"[{verb}: player index must be an integer 1..{WorldPopulation.MaxPopulation}]");
        }

        // 1..4 are the local seats; 5..128 are population entries, addressed by their 0-based entity index (display
        // number − 1). Both resolve to the server's authoritative body.
        if (index <= PlayerRoster.MaxSlots) {
            var slot = PlayerRoster.SlotFromDisplay(number: index);

            return ((m_roster.IsJoined(slot: slot) && (m_server.Body(index: slot) is { } seat))
                ? (Player: seat, Index: index, Error: null)
                : (Player: null, Index: index, Error: $"[{verb}: player {index} is not joined — see world.players]"));
        }

        return ((m_population.EntryBody(index: (index - 1)) is { } entry)
            ? (Player: entry, Index: index, Error: null)
            : (Player: null, Index: index, Error: $"[{verb}: player {index} is not an active population entry — see world.population]"));
    }

    // A keyboard movement verb, targeting whichever player owns the keyboard (the default device id, reassignable
    // between slots). Press/continuous edges hold the axis, a release edge frees it (the binding pushes both edges to
    // this verb, so the phase separates them). While the keyboard's player is pending, its movement is repurposed as
    // the profile picker: a turn-left/turn-right press cycles the candidate and the other axes stay inert.
    private CommandDefinition MovementVerb(string name, string axis, string description) {
        return CommandDefinition.Verb(
            name: name,
            description: description,
            valueKind: CommandValueKind.Digital,
            handler: context => {
                // The text path (a typed `player.forward` over stdin) arrives Phase=Completed with a non-null Parse,
                // which would otherwise fall through to the Release branch and no-op. Point the scripter at the tape
                // primitive instead of releasing an unheld axis.
                if (context.Parse is not null) {
                    return new CommandResult(Output: $"[{name}: a held movement key, not a typed verb — use player.run <forward> <strafe> <turn> <seconds> [player] to script motion]") { IsError = true };
                }

                var slot = context.Slot;

                if (m_roster.Seat(slot: slot) is null) {
                    return CommandResult.None;
                }

                // The roster owns the pending-vs-locomotion decision: while the slot is pending it consumes this press
                // as a picker step (turn keys cycle, other axes inert); an active slot lets the held-key locomotion run.
                if (m_roster.TryPickerStep(slot: slot, direction: ((context.Phase is CommandPhase.Started) ? PickerDirection(axis: axis) : 0))) {
                    return CommandResult.None;
                }

                if (m_roster.Seat(slot: slot) is { } seat) {
                    // An off-Live seat masks the human's live movement: the device hold/release no-ops so the held-axis
                    // set stays clean and nothing bursts on the return to live. Roster membership is untouched.
                    if (seat.Source != IntentSource.Live) {
                        return CommandResult.None;
                    }

                    if (context.Phase is CommandPhase.Started or CommandPhase.Active) {
                        seat.Hold(axis: axis);
                    } else {
                        seat.Release(axis: axis);
                    }
                }

                return CommandResult.None;
            }
        );
    }

    // The picker step direction a movement axis maps to while pending: turn-left cycles to the previous candidate,
    // turn-right to the next, every other axis is inert (0).
    private static int PickerDirection(string axis) {
        if (string.Equals(a: axis, b: SeatController.AxisTurnLeft, comparisonType: StringComparison.Ordinal)) {
            return -1;
        }

        if (string.Equals(a: axis, b: SeatController.AxisTurnRight, comparisonType: StringComparison.Ordinal)) {
            return 1;
        }

        return 0;
    }
}

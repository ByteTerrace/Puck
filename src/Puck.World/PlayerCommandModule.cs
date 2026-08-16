using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Commands;
using Puck.Maths;
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
/// <remarks><para>The stick channels (<c>player.move</c> / <c>player.look</c>) are not polled: their bindings fire on the
/// default active phase, and the snapshot router re-dispatches carried analog values each tick. Handlers route each
/// dispatch by its recorded logical slot; the local device id is consulted only when a previously unseen live device
/// first needs seating.</para>
/// <para><c>join</c>/<c>leave</c>/<c>fly</c>/<c>stop</c>/<c>where</c>/<c>pose</c> also accept a trailing
/// <c>instance:&lt;name&gt;</c> token (see <see cref="TryStripInstanceToken"/>), addressing a named running
/// <see cref="WorldInstance"/> (<see cref="WorldInstanceHost"/>) instead of the boot world. In the instance form, a
/// slot is always the 1-based local seat 1..<see cref="Server.WorldPopulation.LocalSeatCount"/> (never a population
/// entry, and never defaulted — a bare <c>instance:&lt;name&gt;</c> with no slot is refused), and <c>join</c>'s
/// "next free slot"/either-order profile-then-slot convenience does not apply there.</para>
/// </remarks>
internal sealed class PlayerCommandModule(PlayerRoster roster, WorldPopulation population, WorldScreenBinder screens, WorldDefinition definition, IServerLink link, WorldServer server, WorldPerceptionAnchor anchor, WorldClient client, Func<InputRouter> router, WorldInstanceHost instances, WorldSeatBindings seatBindings) : ICommandModule {
    // The player.reconcile smoothing window: the default when [seconds] is omitted, and the clamp a supplied value is
    // held to.
    private const float DefaultReconcileSeconds = 0.25f;
    // The reserved trailing token an instance-addressed drive-a-player verb carries — see TryStripInstanceToken.
    private const string InstanceTokenPrefix = "instance:";
    private const float MaxReconcileSeconds = 2f;
    private const float MinReconcileSeconds = 0.05f;

    /// <summary>The keyboard-claim command (Keyboard F1..F4, press edge). The target slot rides the binding's Axis1D
    /// value as a 1-based player number, the clean scalar constant a binding carries.</summary>
    public const string ClaimCommand = "player.claim";
    /// <summary>The confirm command (Gamepad South / Keyboard Enter, press edge) — promotes the pending player owning
    /// the pressing device.</summary>
    public const string ConfirmCommand = "player.confirm";
    /// <summary>The device-cycle command (Gamepad Start, press edge) — rotates the pressing device to the next slot.</summary>
    public const string CycleCommand = "player.cycle";
    /// <summary>The Axis2D command the gamepad's RIGHT stick is bound to (+X turns the body right, +Y pitches the
    /// presentation camera up). Same routing contract as <see cref="MoveCommand"/>.</summary>
    public const string LookCommand = "player.look";
    /// <summary>The Axis2D command the gamepad's LEFT stick is bound to (+Y forward, +X strafe right). The handler
    /// ROUTES the dispatch to the owning device's player (joining an unmapped pad per the roster rules).</summary>
    public const string MoveCommand = "player.move";

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

    /// <summary>The runtime command name a seat binding lowers one validated channel ordinal to. Every possible
    /// ordinal is registered at boot, so destination discovery never mutates the command registry or its replay ids.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    internal static string RoutedChannelCommandName(int ordinal) => $"channel.ordinal.{ordinal}";

    private CommandResult AssignHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[player.assign: expected a device token and a slot — player.assign <kbd|padN> <slot 1..4>]");
        }

        var deviceToken = args[0].ToString();

        if (!m_roster.TryResolveDeviceToken(
            device: out var device,
            token: deviceToken
        )) {
            return CommandResult.Error(output: $"[player.assign: no device '{deviceToken}' — see world.devices]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 1,
            fallback: null,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var slot
        )) {
            return CommandResult.Error(output: $"[player.assign: <slot> must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        // An operator command naming BOTH the device and the destination explicitly — context.ActingPrincipal() (the
        // text door's Console) is the real actor, threaded all the way into AssignDevice's Drive check, never a
        // fabricated target identity.
        return DescribeAssign(
            verb: "player.assign",
            outcome: m_roster.AssignDevice(
                device: device,
                targetSlot: PlayerRoster.SlotFromDisplay(number: slot),
                actingPrincipal: context.ActingPrincipal()
            ),
            slot: PlayerRoster.SlotFromDisplay(number: slot)
        );
    }
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
            description: "Echoes a player's FULL 6DOF pose — [player.where: p<N> pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd°] — so a piped run can assert it moved: player.where [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries). Grounded entities print y=0.00 pitch=0 roll=0. A LOCAL seat's echo also carries anchor=body:<n> — the 0-based body index that seat's presentation (camera eye, audio listener, seat.<n>.position.* HUD bindings) derives from: the seat's bound body, or the routed body while possessing (a Control route targeting a body with capture on). A trailing instance:<name> token reads OUT OF a NAMED running instance's OWN tick snapshot instead — player.where <slot> instance:<name> (slot REQUIRED, 1..WorldPopulation.LocalSeatCount); no anchor rides that form (a spawned instance's seat has no client perceiving from it).",
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
            description: "Proposes a subject for one authored target register: player.designate <register> <body:n|nearest> [player]. 'nearest' resolves client-side from the latest snapshot inside the player's clamped forward cone; either form submits only the resolved body subject. The server re-resolves activity, authority, range, cone, targetability, and line of sight before writing. Returns player.targets read-back, including the latest refusal.",
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
            description: "Stops a player's avatar dead: clears its whole tape, releases every held movement key, cancels every in-flight timed press (player.press hold — materialized or still pending), AND clears any BindingEntryMode.Toggle latch the seat carries (a toggled-on channel does not survive a stop) — the panic verb: player.stop [player] (optional player index 1..128, default 1 — 1..4 local seats, 5..128 simulated entries; stopping a population entry drops its tape so its wander resumes). Echoes the true released/cleared/toggle counts, or, if the Drive gate refused the command (e.g. CC/death), a refusal naming the denial — never an affirmative quoting a stale count. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — player.stop <slot> instance:<name> (slot REQUIRED, 1..WorldPopulation.LocalSeatCount) — echoing a bare \"tape cleared\", since no client mirrors that seat's held-key/toggle state.",
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
            description: "Teleports a player to a full 6DOF pose: player.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> [player] (yaw about world up, pitch about the body right, roll about the body forward; 0/0/0 = level facing -Z). ANY of the six positional values may be - to HOLD that axis at its current value instead of setting it — player.pose - - - 90 - - 1 turns p1 to face 90° with its position and pitch/roll untouched; player.pose 3 - 5 - - - 1 moves p1 on the ground plane only. The held axes are read from the SAME live pose this call resolves and folded into ONE atomic SnapPose submission — never a read-then-write pair, so nothing can move the body between the read and the write. A hard teleport (sim snap + previous-pose reset + render-error clear). A grounded entity re-pins Y and levels on its next step. The optional trailing player index is 1..128 (default 1) — 1..4 local seats, 5..128 simulated entries. A trailing instance:<name> token addresses a NAMED running instance's own seat instead — player.pose <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg> <slot> instance:<name> (slot REQUIRED, 1..WorldPopulation.LocalSeatCount) — the SAME hold-current axes apply, read from that instance's own live pose.",
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
            description: "Joins a player: player.join [n] joins a PENDING player (a profile is chosen, then confirm) — with no index the next free slot, n (2..4) that specific slot. player.join <profile> [n] joins directly ACTIVE on a named profile (a token in 2..4 is a slot, otherwise a profile name; either order). No device is attached (the console is a network-shaped source), so a piped script builds a quad session. Echoes the roster. A trailing instance:<name> token addresses a NAMED running instance instead of the boot world — player.join <slot> [identity] instance:<name> (slot is REQUIRED, 1..WorldPopulation.LocalSeatCount, never auto-picked; no either-order profile/slot convenience there).",
            handler: JoinHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.leave",
            description: "Removes a scripted or pad player: player.leave <n> (n in 2..4), unmapping its devices and freeing its profile. Player 1 never leaves. Echoes the resulting roster. A trailing instance:<name> token addresses a NAMED running instance instead — player.leave <slot> instance:<name> (slot 1..WorldPopulation.LocalSeatCount, seat 1 included — the boot form's \"player 1 never leaves\" rule is a roster policy that does not apply to an instance's own seat table), which REAPS the instance on empty.",
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
    // A channel-generic movement/composition verb targeting whichever player owns the binding's device. Press/
    // continuous edges hold the channel at the binding's scaled value; a release edge frees it. While the keyboard's
    // player is pending, the Turn-role channel becomes the profile picker (positive scale cycles forward, negative
    // back) and every other channel stays inert.
    private CommandDefinition ChannelVerb(int ordinal) {
        var commandName = RoutedChannelCommandName(ordinal: ordinal);

        return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: commandName,
            description: $"Holds the currently routed world's declared channel at ordinal {ordinal} while its source is active — an internal binding target lowered from the authored channel name, not a typed verb.",
            valueKind: CommandValueKind.Axis1D,
            handler: context => {
                if (context.Origin != CommandOrigin.Binding) {
                    return CommandResult.Error(output: $"[{commandName}: an internal bound-channel destination, not a typed verb — use player.press <channel-name> [value] [holdSeconds] [player] to script it]");
                }

                var slot = context.Slot;

                if (m_roster.Seat(slot: slot) is null) {
                    return CommandResult.None;
                }

                // Checked against the DISPATCHING SEAT's own CURRENTLY ROUTED table. A release always runs even if
                // the route changed since the press: it frees whatever this physical control last actually held.
                var seatTable = m_seatBindings.Channels(slot: slot);
                var declared = seatTable.IsDeclared(ordinal: ordinal);
                var scale = FixedQ4816.FromDouble(value: context.Value.AsAxis1D);

                // The roster owns the pending-vs-locomotion decision: while the slot is pending it consumes a Turn-role
                // press as a picker step; an active slot lets the held-channel locomotion run. TryPickerStep's own
                // contract consumes the press unconditionally while pending — every pending seat sits at the boot
                // route (pre-join), so an ordinal the boot table does not declare steers nothing (direction 0) and
                // never breaks the consume.
                var direction = (((context.Phase is CommandPhase.Started) && declared)
                    ? PickerDirection(
                        ordinal: ordinal,
                        scale: scale,
                        table: seatTable
                    )
                    : 0
                );

                if (m_roster.TryPickerStep(
                    direction: direction,
                    slot: slot
                )) {
                    return CommandResult.None;
                }

                if (m_roster.Seat(slot: slot) is { } seat) {
                    // An off-Live seat masks the human's live input: the device hold/release no-ops so the held set
                    // stays clean and nothing bursts on the return to live. Roster membership is untouched.
                    if (seat.Source != IntentSource.Live) {
                        return CommandResult.None;
                    }

                    // Zero is the neutral image of an analog channel even when a backend reports it as Active
                    // rather than Completed. Treat it as the same release edge so a trigger/stick cannot leave a
                    // zero-valued dictionary row behind, and so every backend gets identical held semantics.
                    if (
                        (context.Phase is CommandPhase.Started or CommandPhase.Active) &&
                        (scale != FixedQ4816.Zero)
                    ) {
                        if (declared) {
                            seat.HoldChannel(
                                controlId: context.Source,
                                ordinal: ordinal,
                                scale: scale
                            );
                        }
                    } else {
                        seat.ReleaseChannel(controlId: context.Source);
                    }
                }

                return CommandResult.None;
            }
        );
    }
    // One command per fixed channel ORDINAL, not per authored name. WorldSeatBindings validates a name against the
    // firing seat's currently routed table and lowers it to this stable vocabulary while compiling that seat's
    // profile. This is complete for every legal world because ChannelLimits.MaxChannels is the document ceiling; it
    // does not crawl references, assume a target exists at boot, or change the command-id table when one appears.
    private IEnumerable<CommandDefinition> ChannelVerbs() {
        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            yield return ChannelVerb(ordinal: ordinal);
        }
    }
    private CommandResult ChannelsHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.channels: expected at most 1 value — an optional player index]");
        }

        if (TryRoutedSeatQuery(
            args: in args,
            query: static index => new WorldQuery.PlayerChannels(Index: index),
            result: out var routed
        )) {
            return routed;
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.channels"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        // A query verb, exactly like player.where: the channel read-back IS the answer, so it always echoes — even
        // under wire.ack quiet — and its verdict rides through as IsError so a miss reaches wire.errors. The
        // completion fires INLINE over loopback; the result formats from it, never a post-submit live read.
        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerChannels(Index: index),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) {
                    IsError = answer.Refused,
                };
            }
        );

        return result;
    }
    private CommandResult ClaimHandler(CommandContext context) {
        // The target slot rides the binding's Axis1D value as a 1-based player number (the clean scalar constant a
        // CommandBinding carries — CommandValue.Axis(float)); a typed invocation with no value is a no-op. This
        // decodes an authored integer constant, not a continuous stick axis, so it keeps float->MathF.Round rather
        // than routing through the fixed-point quantization doors: MathF.Round's default mode is ties-to-even
        // (banker's rounding), stated here so the decode's mapping is explicit rather than inherited silently.
        var player = ((int)MathF.Round(x: context.Value.AsAxis1D));

        if (
            (player < 1) ||
            (player > PlayerRoster.MaxSlots)
        ) {
            return CommandResult.None;
        }

        var targetSlot = PlayerRoster.SlotFromDisplay(number: player);

        // context.ActingPrincipal() is the ingress-stamped identity for the PRESSING device's own lane — a handler
        // reads this, it never constructs one (CommandContext.Principal's own rule). AssignDevice decides whether
        // this identity (an already-bound device relocating) or self-provisioning (an unbound device's bootstrap)
        // governs the target — see its own remarks.
        return DescribeAssign(
            verb: ClaimCommand,
            outcome: m_roster.AssignDevice(
                device: context.DeviceId,
                targetSlot: targetSlot,
                actingPrincipal: context.ActingPrincipal()
            ),
            slot: targetSlot
        );
    }
    private CommandResult ConfirmHandler(CommandContext context) {
        // A slot under an exclusive TryClaimSlot hold (the editor, a replay device, a test harness) never seats or
        // confirms through the ordinary human gesture path: the slot-addressed ConfirmInputSlot below carries no
        // device identity at all, so it cannot consult the device-keyed m_programmaticDevices exclusion the way
        // PlayerRoster.Confirm(InputDeviceId) does — making that guard dead on exactly this pushed/lane-addressed
        // path. PlayerRoster.IsClaimed is the slot-scoped equivalent, and it is what must gate here.
        if (
            (context.Origin == CommandOrigin.Binding) &&
            context.AssignedSlot &&
            m_roster.IsClaimed(slot: context.Slot)
        ) {
            return CommandResult.None;
        }

        // Physical/snapshot input is lane-addressed. A text invocation deliberately retains the documented local
        // keyboard-device behavior (player.assign may have moved it since boot).
        if (
            (context.Origin == CommandOrigin.Binding) &&
            context.AssignedSlot &&
            m_roster.IsJoined(slot: context.Slot) &&
            !m_roster.IsPending(slot: context.Slot)
        ) {
            return DescribeConfirm(
                outcome: ConfirmOutcome.Seated,
                slot: context.Slot,
                device: null,
                actingPrincipal: context.ActingPrincipal()
            );
        }

        // context.ActingPrincipal() names the real submitter either way: for the physical/lane-addressed branch it
        // resolves through PrincipalOf(context.Slot) — the pressing lane's own identity, correct self-service; for
        // the text/device-keyed branch it is Console, the operator confirming context.DeviceId on its behalf.
        var actingPrincipal = context.ActingPrincipal();

        var (outcome, slot) = ((context.Origin == CommandOrigin.Binding)
            ? ConfirmInputSlot(
                slot: context.Slot,
                actingPrincipal: actingPrincipal,
                device: context.DeviceId
            )
            : m_roster.Confirm(
                device: context.DeviceId,
                actingPrincipal: actingPrincipal
            )
        );

        return DescribeConfirm(
            outcome: outcome,
            slot: slot,
            device: context.DeviceId,
            actingPrincipal: actingPrincipal
        );
    }
    private (ConfirmOutcome Outcome, int Slot) ConfirmInputSlot(int slot, WorldPrincipal actingPrincipal, InputDeviceId device) {
        if (!m_roster.IsJoined(slot: slot)) {
            return (m_roster.JoinPending(
                actingPrincipal: actingPrincipal,
                origin: ParticipantOrigin.Device,
                slot: slot
            ) switch {
                JoinResult.Ok => (Outcome: ConfirmOutcome.Joined, Slot: slot),
                JoinResult.Denied => (Outcome: ConfirmOutcome.Denied, Slot: slot),
                _ => (Outcome: ConfirmOutcome.Ignored, Slot: -1),
            });
        }

        return m_roster.Confirm(
            actingPrincipal: actingPrincipal,
            device: device,
            slot: slot
        );
    }
    private CommandResult ControlHandler(CommandContext context, WireArgs args) {
        // Token 0 is the MODE only when it names one; otherwise the whole (0- or 1-token) tail is just the player index
        // for a read-back — the same positional shape as player.motion, so a bare `player.control 7` echoes player 7's
        // source while `player.control idle` sets player 1 with no positional guesswork.
        var source = IntentSource.Live;
        var hasMode = ((args.Count >= 1) && TryParseIntentSource(
            token: args[0],
            source: out source
        ));

        var (player, index, error) = ResolveModeTarget(
            args: in args,
            choices: "live|idle|producer:<name>",
            hasMode: hasMode,
            verb: "player.control"
        );

        if (error is { } modeError) {
            return modeError;
        }

        // A PENDING seat's source cannot be set — its inputs drive the profile picker, not gameplay, so a source set
        // now would sit dormant and take effect only on confirm. Reuse the tape verbs' pending guard (seats only;
        // population entries 5..128 are never pending). Gates BOTH set and read — a pending seat is always Live anyway.
        if (PendingTapeError(
            index: index,
            verb: "player.control"
        ) is { } pendingError) {
            return pendingError;
        }

        if (hasMode) {
            if (!m_population.SupportsSource(
                index: (index - 1),
                refusal: out var refusal,
                source: source
            )) {
                return CommandResult.Error(output: $"[player.control: {refusal}]");
            }

            m_link.SubmitCommand(command: new WorldCommand.SetControl(
                Principal: context.ActingPrincipal(),
                EntityIndex: (index - 1),
                Source: source
            ));

            // The seat's client-side source copy gates the live device producers; write it in the same command so the
            // mask lands with no tick gap (dropping any held keys/lanes on the transition).
            if (IsSeat(index: index)) {
                m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.SetIntentSource(source: source);
            }

            return Echoed(
                args: in args,
                handler: $"[player.control: p{index} {SourceWord(source: source)}]"
            );
        }

        // No mode: a read-back — echo the target's current source. Always surfaced (a query answer), like player.motion.
        return new CommandResult(Output: $"[player.control: p{index} is {SourceWord(source: player!.Source)}]");
    }
    // The resolved player's current heading decomposed to degrees — the exact inverse of the Euler construction
    // WorldBody.Pose applies (Ry(yaw)·Rx(pitch)·Rz(roll)), read from the PUBLIC WorldBody.Orientation so player.pose's
    // "-" hold never re-derives a fact WorldBody itself does not already expose; mirrors WorldBody's own private
    // EulerRadians (see its remarks on the codebase-wide yaw-about-+Y / pitch-about-+X / roll-about-+Z convention).
    private static (float YawDegrees, float PitchDegrees, float RollDegrees) CurrentEulerDegrees(WorldBody player) {
        var orientation = player.Orientation;
        var forward = Vector3.Transform(
            value: -Vector3.UnitZ,
            rotation: orientation
        );
        var up = Vector3.Transform(
            value: Vector3.UnitY,
            rotation: orientation
        );
        var right = Vector3.Transform(
            value: Vector3.UnitX,
            rotation: orientation
        );
        var yaw = MathF.Atan2(
            x: -forward.Z,
            y: -forward.X
        );
        var pitch = MathF.Asin(x: Math.Clamp(
            max: 1f,
            min: -1f,
            value: forward.Y
        ));
        var roll = MathF.Atan2(
            x: up.Y,
            y: right.Y
        );

        const float ToDegrees = (180f / MathF.PI);

        return (YawDegrees: (yaw * ToDegrees), PitchDegrees: (pitch * ToDegrees), RollDegrees: (roll * ToDegrees));
    }
    private CommandResult CycleHandler(CommandContext context) {
        // context.ActingPrincipal() is the ingress-stamped identity for THIS lane (the pressing device's own
        // current/source seat, if any) — consumed here, never reconstructed. CycleDevice/AssignDevice decide
        // internally whether it or self-provisioning governs the target, since only they know whether the device
        // was already bound (see AssignDevice's own remarks).
        var (outcome, slot) = m_roster.CycleDevice(
            device: context.DeviceId,
            actingPrincipal: context.ActingPrincipal()
        );

        return DescribeAssign(
            outcome: outcome,
            slot: slot,
            verb: "player.cycle"
        );
    }
    // Format a device-reassignment outcome, echoing the roster on a change. Each Ignored-shaped outcome gets its OWN
    // accurate reason (see AssignOutcome's own remarks) rather than one hardcoded "roster is full" text that used to
    // print even when the real cause was an exclusively-claimed device or target slot.
    private CommandResult DescribeAssign(string verb, AssignOutcome outcome, int slot) {
        return (outcome switch {
            AssignOutcome.CreatedPending => new CommandResult(Output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {m_roster.Describe()}"),
            AssignOutcome.JoinedTeam => new CommandResult(Output: $"[{verb}: device moved to player {PlayerRoster.DisplayNumber(slot: slot)}] {m_roster.Describe()}"),
            AssignOutcome.NoOp => new CommandResult(Output: $"[{verb}: device already on player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            AssignOutcome.DeviceClaimed => CommandResult.Error(output: $"[{verb}: this device is exclusively claimed and cannot be reassigned]"),
            AssignOutcome.TargetClaimed => CommandResult.Error(output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} is exclusively claimed — a device cannot move onto it]"),
            // Denied is distinct from Ignored ("roster is full"/out of range) — the QUIBBLE's own shape, closed here
            // too so a plain authority refusal never misreports as "no room". world.why over drive/body:<slot>
            // explains the refusal with the actor already named in the loud stderr line AssignDevice printed.
            AssignOutcome.Denied => CommandResult.Error(output: $"[{verb}: player {PlayerRoster.DisplayNumber(slot: slot)} — actor denied, see wire.errors/world.why]"),
            _ => CommandResult.Error(output: $"[{verb}: the roster is full ({PlayerRoster.MaxSlots} players)]"),
        });
    }
    private CommandResult DescribeConfirm(ConfirmOutcome outcome, int slot, InputDeviceId? device, WorldPrincipal actingPrincipal) {

        return (outcome switch {
            ConfirmOutcome.Confirmed => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} confirmed] {m_roster.Describe()}"),
            ConfirmOutcome.Joined => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} joined pending] {m_roster.Describe()}"),
            ConfirmOutcome.Seated when (device is { } source) => new CommandResult(Output: $"[player.confirm: {m_roster.DeviceToken(device: source)} seated with player {PlayerRoster.DisplayNumber(slot: slot)}]"),
            ConfirmOutcome.Seated => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} seated]"),
            ConfirmOutcome.AlreadyActive => new CommandResult(Output: $"[player.confirm: player {PlayerRoster.DisplayNumber(slot: slot)} is already active]"),
            ConfirmOutcome.Denied => CommandResult.Error(output: $"[player.confirm: {actingPrincipal.Describe()} cannot confirm player {PlayerRoster.DisplayNumber(slot: slot)} — see world.why]"),
            _ => CommandResult.Error(output: $"[player.confirm: the roster is full ({PlayerRoster.MaxSlots} players)]"),
        });
    }
    private CommandResult DesignateHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (2 or 3)) {
            return CommandResult.Error(output: "[player.designate: expected <register> <body:n|nearest> [player]]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "player.designate"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var register = args[0].ToString();
        GrantSubject subject;

        if (args.Is(
            index: 1,
            value: "nearest"
        )) {
            if (!m_client.TryFindDesignationSubject(
                registerName: register,
                sourceBody: (index - 1),
                subject: out subject
            )) {
                return CommandResult.Error(output: $"[player.designate: no client-snapshot candidate lies inside register '{register}'s clamped cone]");
            }
        } else if (
            !GrantSubject.TryParse(
            token: args[1],
            subject: out subject
        ) ||
            (subject.Kind != GrantSubjectKind.Body)
        ) {
            return CommandResult.Error(output: $"[player.designate: subject '{args[1].ToString()}' must be body:<n> or nearest]");
        }

        m_link.SubmitDesignation(
            designation: new WorldDesignation(
                EntityIndex: (index - 1),
                Register: register,
                Subject: subject
            ),
            principal: context.ActingPrincipal()
        );

        return TargetsResult(index: index);
    }
    private CommandResult DisengageHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.disengage: expected at most 1 value — an optional player index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.disengage"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var actingPrincipal = context.ActingPrincipal();
        var targetPrincipal = TargetPrincipalFor(index: index);
        // A READ-ONLY peek of the decision Server.WorldEngagement.Disengage will make — the console echo's source of
        // truth (see WorldEngagement.PeekDisengage's own remarks for why this is safe over loopback). The command
        // below is submitted UNCONDITIONALLY regardless of the peek, so the SERVER's own check is what actually
        // decides (a denied-disengage attack case must be refused there, never merely by this client choosing not to
        // submit).
        var outcome = m_server.Engagement.PeekDisengage(
            actingPrincipal: actingPrincipal,
            entityIndex: (index - 1),
            targetPrincipal: targetPrincipal
        );

        m_link.SubmitCommand(command: new WorldCommand.Disengage(
            EntityIndex: (index - 1),
            Principal: actingPrincipal,
            TargetPrincipal: targetPrincipal
        ));

        if (outcome == DisengageOutcome.Denied) {
            return CommandResult.Error(output: $"[player.disengage: {actingPrincipal.Describe()} lacks control over p{index}'s screen — see world.why]");
        }

        // Only a true disengage (ordinary, or the stuck-latch repair) drops p{index}'s held device state; the
        // route-without-latch repair means the entity was never actually engaged, so there is nothing to release.
        // This is client-side held state only — a BindingEntryMode.Toggle latch (InputRouter) survives a disengage
        // on purpose, since rerouting input is not a stop.
        if (
            ((outcome == DisengageOutcome.RepairedLatch) || (outcome == DisengageOutcome.Disengaged)) &&
            IsSeat(index: index)
        ) {
            m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.ReleaseAllHeld();
        }

        if (
            (outcome == DisengageOutcome.RepairedLatch) ||
            (outcome == DisengageOutcome.RepairedRoute)
        ) {
            // The latch and the route disagreed (see WorldEngagement.ResolveDisengage's own remarks for the decision) —
            // a consistency repair, not the ordinary success/no-op pair, so it gets its own distinct echo rather than
            // silently reading as either.
            return Echoed(
                args: in args,
                handler: $"[player.disengage: p{index}'s engagement latch/route was inconsistent — repaired]"
            );
        }

        return ((outcome == DisengageOutcome.Disengaged)
            ? Echoed(
                args: in args,
                handler: $"[player.disengage: p{index} disengaged]"
            )
            : Echoed(
                args: in args,
                handler: $"[player.disengage: p{index} was not engaged]"
            )
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
    private CommandResult EngageHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return CommandResult.Error(output: "[player.engage: expected a target (a screen index or body:<n>) — plus an optional capture:on|off and an optional player index]");
        }

        // capture:on|off, when present, is ALWAYS the LAST token — this keeps the target and the (optional) player
        // index at their historical fixed positions (0 and 1) so nothing about the classic screen-engage shape moves.
        var capture = true;
        var tokenCount = args.Count;

        if (
            (tokenCount >= 2) &&
            LooksLikeCaptureToken(token: args[(tokenCount - 1)])
        ) {
            if (!TryParseCapture(
                token: args[(tokenCount - 1)],
                capture: out capture
            )) {
                return CommandResult.Error(output: $"[player.engage: '{args[(tokenCount - 1)].ToString()}' must be capture:on or capture:off]");
            }

            tokenCount--;
        }

        if (!TryParseEngageTarget(
            token: args[0],
            target: out var target
        )) {
            return CommandResult.Error(output: $"[player.engage: target '{args[0].ToString()}' must be a screen index or body:<n>]");
        }

        // The player index (if any) trails the target at token 1 — read directly rather than through
        // WorldArgs.TryParseIndex, which reads the ORIGINAL args by position and would misparse a stripped capture:
        // token sitting at args[1] in the (target, capture) two-token shape (tokenCount == 1, original args.Count == 2)
        // as a malformed player index instead of the absent-token default.
        var index = 1;

        if (tokenCount >= 2) {
            if (
                !args.TryInt(
                index: 1,
                value: out index
            ) ||
                (index < 1) ||
                (index > m_population.Capacity)
            ) {
                return CommandResult.Error(output: $"[player.engage: player index must be an integer 1..{m_population.Capacity}]");
            }
        }

        var player = ((index <= PlayerRoster.MaxSlots)
            ? (m_roster.IsJoined(slot: PlayerRoster.SlotFromDisplay(number: index))
                ? m_server.Body(index: PlayerRoster.SlotFromDisplay(number: index))
                : null)
            : m_population.EntryBody(index: (index - 1))
        );

        if (player is null) {
            var missError = ((index <= PlayerRoster.MaxSlots)
                ? $"[player.engage: player {index} is not joined — see world.players]"
                : $"[player.engage: player {index} is not an active population entry — see world.population]"
            );

            return CommandResult.Error(output: missError);
        }

        // Authority check happens before any mutation, including the auto-insert boot below: it checks the acting
        // principal (the submitter), not the target player's own principal — every seat is pre-seeded Control/all,
        // so checking the target would pass unconditionally. This is a client-side precheck against the server's
        // grant table; the mutation itself re-checks the identical pair atomically in WorldCommand.Engage's apply.
        var actingPrincipal = context.ActingPrincipal();

        if (m_server.Engagement.CheckEngage(
            actingPrincipal: actingPrincipal,
            target: target
        ) is { IsAllowed: false } engageVerdict) {
            return CommandResult.Error(output: $"[player.engage: {actingPrincipal.Describe()} cannot control {target.Describe()} ({engageVerdict.DescribeDenial()}) — see world.why]");
        }

        if (target.Kind == GrantSubjectKind.Screen) {
            var screenIndex = target.Value;

            if (FindScreen(screenIndex: screenIndex) is not { } screen) {
                return CommandResult.Error(output: $"[player.engage: no screen {screenIndex} — see world.screens]");
            }

            // Engaging requires the screen to permit engagement, carry a machine to receive input, and — when the
            // route sets a radius — the avatar be within that planar distance of the screen's origin. The radius
            // check here reads the server body's pose in-process (loopback only); a socket transport checks the
            // radius server-side in the engage command instead.
            if (!screen.Route.Engageable) {
                return CommandResult.Error(output: $"[player.engage: screen {screenIndex} is not engageable]");
            }

            // route.autoInsert: engaging an empty engageable screen first boots its selected magazine entry (the "walk
            // over, press the button, the screen lights" gesture is one act, not an insert then an engage).
            // The boot itself is a WorldScreenOp.Select submission
            // through the ordered domain — Server.WorldMachineHost applies it SYNCHRONOUSLY, so the HasMachine check
            // two lines below observes its effect immediately, exactly like the pre-inversion direct binder call did.
            if (
                screen.Route.AutoInsert &&
                !m_screens.HasMachine(index: screenIndex) &&
                m_screens.TryMagazine(
                index: screenIndex,
                magazine: out _,
                selected: out var selected
            )
            ) {
                m_link.SubmitScreenOp(
                    op: new WorldScreenOp.Select(
                        Entry: selected,
                        Index: screenIndex
                    ),
                    principal: actingPrincipal
                );
            }

            if (!m_screens.HasMachine(index: screenIndex)) {
                return CommandResult.Error(output: $"[player.engage: screen {screenIndex} has no machine to control — screen.insert a cart first]");
            }

            if (screen.Route.EngageRadius > 0f) {
                var position = player.FixedPosition;
                var delta = new FixedVector2(
                    X: (position.X - FixedQ4816.FromDouble(value: screen.Origin.X)),
                    Y: (position.Z - FixedQ4816.FromDouble(value: screen.Origin.Z))
                );
                var radius = FixedQ4816.FromDouble(value: screen.Route.EngageRadius);

                if (delta.LengthSquared > (radius * radius)) {
                    return CommandResult.Error(output: string.Create(
                        provider: CultureInfo.InvariantCulture,
                        handler: $"[player.engage: p{index} is {((double)delta.Length):0.0}u from screen {screenIndex} — within {screen.Route.EngageRadius:0.0}u to engage (player.pose closer)]"
                    ));
                }
            }
        } else if (m_population.EntryBody(index: target.Value) is null) {
            return CommandResult.Error(output: $"[player.engage: no body {target.Value} — see world.population]");
        }

        // The precheck above already confirmed actingPrincipal holds Control over target on this same thread, so
        // this submission is guaranteed to land; the command re-checks the identical pair atomically server-side in
        // Server.WorldEngagement.Engage. p{index}'s device state (held keys/lanes) is dropped client-side in the
        // same breath as the submission.
        var targetPrincipal = TargetPrincipalFor(index: index);

        m_link.SubmitCommand(command: new WorldCommand.Engage(
            Capture: capture,
            EntityIndex: (index - 1),
            Principal: actingPrincipal,
            Target: target,
            TargetPrincipal: targetPrincipal
        ));

        // Only the CLIENT-side held-device image is dropped here — deliberately NOT InputRouter.ClearSlotHeld (the
        // input-layer BindingEntryMode.Toggle latch): engaging reroutes where a seat's held channels are DELIVERED
        // (this body vs. a possessed one), it does not stop the seat, so a toggled-on sprint should still read
        // toggled-on once the route lands. player.stop is the one seam that clears the latch (see its own remarks).
        if (
            capture &&
            IsSeat(index: index)
        ) {
            m_roster.Seat(slot: PlayerRoster.SlotFromDisplay(number: index))?.ReleaseAllHeld();
        }

        return Echoed(
            args: in args,
            handler: $"[player.engage: p{index} routed to {target.Describe()} ({(capture
            ? "capture"
            : "mirror")})]"
        );
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
    private CommandResult FlyHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.fly"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 8) {
                return CommandResult.Error(output: $"[player.fly: instance-targeted form expects 7 values — <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> — plus the REQUIRED instance seat, before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 7,
                verb: "player.fly"
            );

            if (instancePlayer is null) {
                return CommandResult.Error(output: slotError!);
            }

            if (!TryParseFlySegment(
                args: in args,
                forward: out var iForward,
                pitch: out var iPitch,
                roll: out var iRoll,
                seconds: out var iSeconds,
                strafe: out var iStrafe,
                up: out var iUp,
                yaw: out var iYaw
            )) {
                return CommandResult.Error(output: "[player.fly: could not parse the seven values as numbers]");
            }

            if (!(iSeconds > 0f)) {
                return CommandResult.Error(output: "[player.fly: <seconds> must be greater than 0]");
            }

            // The instance's OWN channel table — a spawned instance's document may declare channels differently from
            // the boot world's, so this is compiled from ITS definition, never
            // the boot instance's m_channels.
            var instanceChannels = WorldChannelTable.Compile(channels: instance.Server.Definition.Channels);

            instance.Server.ApplyCommand(command: new WorldCommand.EnqueueSegment(
                Principal: context.ActingPrincipal(),
                EntityIndex: (instanceSlot - 1),
                Intent: instanceChannels.RoleOrdinals.Intent(
                    moveForward: FixedQ4816.FromDouble(value: iForward),
                    moveStrafe: FixedQ4816.FromDouble(value: iStrafe),
                    turn: FixedQ4816.FromDouble(value: iYaw),
                    moveUp: FixedQ4816.FromDouble(value: iUp),
                    pitch: FixedQ4816.FromDouble(value: iPitch),
                    roll: FixedQ4816.FromDouble(value: iRoll)
                ),
                Seconds: iSeconds
            ));

            return new CommandResult(Output: $"[player.fly: '{instance.Name}' seat {instanceSlot} fwd={iForward:0.##} strafe={iStrafe:0.##} up={iUp:0.##} yaw={iYaw:0.##} pitch={iPitch:0.##} roll={iRoll:0.##} for {iSeconds:0.##}s]");
        }

        if (instanceTarget.EffectiveCount is not (7 or 8)) {
            return CommandResult.Error(output: "[player.fly: expected 7 values — <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> — plus an optional player index]");
        }

        if (!TryParseFlySegment(
            args: in args,
            forward: out var forward,
            pitch: out var pitch,
            roll: out var roll,
            seconds: out var seconds,
            strafe: out var strafe,
            up: out var up,
            yaw: out var yaw
        )) {
            return CommandResult.Error(output: "[player.fly: could not parse the seven values as numbers]");
        }

        if (!(seconds > 0f)) {
            return CommandResult.Error(output: "[player.fly: <seconds> must be greater than 0]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 7,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[player.fly: player index must be an integer 1..{m_population.Capacity}]");
        }

        // A local seat keeps its console-facing player number while its authoritative body travels. Follow the
        // identical live route player.where and ordinary device intent already use; resolving through the boot
        // roster here would reject the deliberately departed boot body and make a remotely presented seat
        // inspectable but not script-drivable. The routed link owns both local-instance and federated credential
        // translation, while the destination definition supplies its own channel ordinals.
        if (index <= PlayerRoster.MaxSlots) {
            var rosterSlot = PlayerRoster.SlotFromDisplay(number: index);
            var location = m_instances.SeatRoute(slot: rosterSlot);

            if (
                m_roster.IsJoined(slot: rosterSlot) &&
                !string.Equals(
                a: location.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                var routedChannels = WorldChannelTable.Compile(channels: m_instances.ResolveRoutedDefinition(slot: rosterSlot).Channels);

                location.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.EnqueueSegment(
                    Principal: context.ActingPrincipal(),
                    EntityIndex: location.EntityIndex,
                    Intent: routedChannels.RoleOrdinals.Intent(
                        moveForward: FixedQ4816.FromDouble(value: forward),
                        moveStrafe: FixedQ4816.FromDouble(value: strafe),
                        turn: FixedQ4816.FromDouble(value: yaw),
                        moveUp: FixedQ4816.FromDouble(value: up),
                        pitch: FixedQ4816.FromDouble(value: pitch),
                        roll: FixedQ4816.FromDouble(value: roll)
                    ),
                    Seconds: seconds
                ));

                return Echoed(
                    args: in args,
                    handler: $"[player.fly: p{index} via '{location.Endpoint.Identity}' body={location.EntityIndex} fwd={forward:0.##} strafe={strafe:0.##} up={up:0.##} yaw={yaw:0.##} pitch={pitch:0.##} roll={roll:0.##} for {seconds:0.##}s]"
                );
            }
        }

        var (player, resolvedIndex, error) = ResolveTarget(
            args: in args,
            requiredCount: 7,
            verb: "player.fly"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (PendingTapeError(
            index: resolvedIndex,
            verb: "player.fly"
        ) is { } pendingError) {
            return pendingError;
        }

        // The fly channel order (forward, strafe, up, yaw, pitch, roll) maps onto PlayerIntent (MoveForward, MoveStrafe,
        // Turn, MoveUp, Pitch, Roll) — the "yaw" channel is the Turn rate.
        m_link.SubmitCommand(command: new WorldCommand.EnqueueSegment(
            Principal: context.ActingPrincipal(),
            EntityIndex: (resolvedIndex - 1),
            Intent: m_channels.RoleOrdinals.Intent(
                moveForward: FixedQ4816.FromDouble(value: forward),
                moveStrafe: FixedQ4816.FromDouble(value: strafe),
                turn: FixedQ4816.FromDouble(value: yaw),
                moveUp: FixedQ4816.FromDouble(value: up),
                pitch: FixedQ4816.FromDouble(value: pitch),
                roll: FixedQ4816.FromDouble(value: roll)
            ),
            Seconds: seconds
        ));

        return Echoed(
            args: in args,
            handler: $"[player.fly: fwd={forward:0.##} strafe={strafe:0.##} up={up:0.##} yaw={yaw:0.##} pitch={pitch:0.##} roll={roll:0.##} for {seconds:0.##}s]"
        );
    }
    // The device-driven roster gestures — confirm/cycle/claim — routed by the pressing device's id. Confirm (South /
    // Enter) promotes the pending player owning the device; cycle (Start) rotates that device to the next slot;
    // claim (F1..F4) moves the keyboard onto the slot carried as the binding's Axis1D value. Bound in Program; over
    // stdin they act on the keyboard (the default device id).
    private IEnumerable<CommandDefinition> GestureVerbs() {
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: ConfirmCommand,
            description: "Confirms the pending player owning the pressing device, promoting it to active on its candidate profile (South / Enter). A first press from an unmapped device joins it; a second confirms. Over stdin it acts on the keyboard.",
            valueKind: CommandValueKind.Digital,
            handler: ConfirmHandler
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: CycleCommand,
            description: "Rotates the pressing device to the next player slot, wrapping 1→2→3→4→1 (pad Start). Onto an empty slot it creates a pending player; onto an occupied one it joins that team. Over stdin it cycles the keyboard.",
            valueKind: CommandValueKind.Digital,
            handler: CycleHandler
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: ClaimCommand,
            description: "Moves the keyboard onto the player slot carried as the binding's value (F1..F4). Onto an empty slot it creates a pending player; onto an occupied one it joins that team; onto its own slot a no-op.",
            valueKind: CommandValueKind.Axis1D,
            handler: ClaimHandler
        );
    }
    // Whether a drive verb's resolved target is a local seat — seats carry client-side device state (held keys/lanes,
    // the possession latch copy) that some commands must also touch.
    private static bool IsSeat(int index) => (index <= PlayerRoster.MaxSlots);
    private CommandResult JoinHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.join"
        )) {
            return tokenError!.Value;
        }

        // The instance-targeted form keeps the instance seat table's OWN exact grammar/semantics — a required
        // 1-based local seat (never a population entry, never auto-picked) and an optional trailing identity name —
        // rather than the boot form's either-order profile-then-slot convenience, which seat.enter never had.
        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount is (< 1 or > 2)) {
                return CommandResult.Error(output: $"[player.join: instance-targeted form expects <slot> [identity], before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldPopulation.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[player.join: instance-targeted <slot> must be an integer 1..{WorldPopulation.LocalSeatCount}]");
            }

            var instanceIdentity = ((instanceTarget.EffectiveCount == 2)
                ? args[1].ToString()
                : null
            );
            var joinReply = instance.Server.ApplySession(request: new SessionRequest.Join(
                Principal: context.ActingPrincipal(),
                Slot: (instanceSlot - 1),
                IdentityName: instanceIdentity,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            ));

            return (joinReply.Accepted
                ? new CommandResult(Output: $"[player.join: '{instance.Name}' seat {instanceSlot} entered{((instanceIdentity is null)
                    ? " pending"
                    : $" as {instanceIdentity}")}]")
                : CommandResult.Error(output: $"[player.join: '{instance.Name}' seat {instanceSlot} refused ({joinReply.Reason})]")
            );
        }

        if (instanceTarget.EffectiveCount > 2) {
            return CommandResult.Error(output: "[player.join: expected at most 2 tokens — an optional profile name and/or a slot 2..4]");
        }

        // Split the (up to two) tokens into an optional slot (an int in 2..4) and an optional profile name (either
        // order): a token that parses as a slot is the slot, otherwise it is a profile name.
        var slotIndex = -1;
        string? profileName = null;

        for (var tokenIndex = 0; (tokenIndex < instanceTarget.EffectiveCount); tokenIndex++) {
            if (
                args.TryInt(
                index: tokenIndex,
                value: out var n
            ) &&
                (n >= 2) &&
                (n <= PlayerRoster.MaxSlots)
            ) {
                if (slotIndex >= 0) {
                    return CommandResult.Error(output: "[player.join: gave two slot numbers — expected <profile> and/or <slot 2..4>]");
                }

                slotIndex = PlayerRoster.SlotFromDisplay(number: n);
            } else if (profileName is null) {
                profileName = args[tokenIndex].ToString();
            } else {
                return CommandResult.Error(output: "[player.join: gave two profile names — expected <profile> and/or <slot 2..4>]");
            }
        }

        // A named profile joins directly ACTIVE (one-shot); no profile joins PENDING (a candidate is chosen, then
        // confirm). The profile must exist and not already be in use by another active player.
        var actingPrincipal = context.ActingPrincipal();

        if (profileName is not null) {
            if (m_roster.FindProfile(name: profileName) is not { } profile) {
                return CommandResult.Error(output: $"[player.join: no identity named '{profileName}' — see identity.list]");
            }

            if (m_roster.ActiveSlotUsing(profile: profile) >= 0) {
                return CommandResult.Error(output: $"[player.join: profile '{profile.Name}' is already in use — see world.players]");
            }

            var (result, slot) = ((slotIndex >= 0)
                ? (m_roster.JoinActive(
                    actingPrincipal: actingPrincipal,
                    origin: ParticipantOrigin.Script,
                    profile: profile,
                    slot: slotIndex
                ), slotIndex)
                : m_roster.JoinActiveNextFree(
                    actingPrincipal: _ => actingPrincipal,
                    origin: ParticipantOrigin.Script,
                    profile: profile
                )
            );

            return ReportJoin(
                actingPrincipal: actingPrincipal,
                active: true,
                result: result,
                slot: slot
            );
        }

        var (pendingResult, pendingSlot) = ((slotIndex >= 0)
            ? (m_roster.JoinPending(
                actingPrincipal: actingPrincipal,
                origin: ParticipantOrigin.Script,
                slot: slotIndex
            ), slotIndex)
            : m_roster.JoinPendingNextFree(
                actingPrincipal: _ => actingPrincipal,
                origin: ParticipantOrigin.Script
            )
        );

        return ReportJoin(
            actingPrincipal: actingPrincipal,
            active: false,
            result: pendingResult,
            slot: pendingSlot
        );
    }
    private CommandResult LeaveHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.leave"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 1) {
                return CommandResult.Error(output: $"[player.leave: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldPopulation.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[player.leave: instance-targeted <slot> must be an integer 1..{WorldPopulation.LocalSeatCount}]");
            }

            if (m_instances.TryFindFollowedRosterSlot(
                instanceName: instance.Name,
                instanceSlot: (instanceSlot - 1),
                rosterSlot: out var rosterSlot
            )) {
                if (!m_roster.Leave(
                    slot: rosterSlot,
                    actingPrincipal: context.ActingPrincipal()
                )) {
                    return CommandResult.Error(output: $"[player.leave: '{instance.Name}' seat {instanceSlot} is followed by player {(rosterSlot + 1)}, which cannot leave or the actor was denied]");
                }

                return new CommandResult(Output: $"[player.leave: player {(rosterSlot + 1)} left '{instance.Name}' seat {instanceSlot}] {m_roster.Describe()}");
            }

            var leaveReply = instance.Server.ApplySession(request: new SessionRequest.Leave(
                Principal: context.ActingPrincipal(),
                Slot: (instanceSlot - 1)
            ));

            if (!leaveReply.Accepted) {
                return CommandResult.Error(output: $"[player.leave: '{instance.Name}' seat {instanceSlot} refused ({leaveReply.Reason})]");
            }

            var reaped = m_instances.ReapIfEmpty(name: instance.Name);

            return new CommandResult(Output: $"[player.leave: '{instance.Name}' seat {instanceSlot} left{(reaped
                ? $" — '{instance.Name}' reaped (0 active entries)"
                : string.Empty)}]");
        }

        if (instanceTarget.EffectiveCount != 1) {
            return CommandResult.Error(output: "[player.leave: expected a player index — player.leave <n>, n in 2..4]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            fallback: null,
            max: PlayerRoster.MaxSlots,
            min: 2,
            value: out var n
        )) {
            return CommandResult.Error(output: $"[player.leave: <n> must be an integer 2..{PlayerRoster.MaxSlots}]");
        }

        return (m_roster.Leave(
            slot: PlayerRoster.SlotFromDisplay(number: n),
            actingPrincipal: context.ActingPrincipal()
        )
            ? new CommandResult(Output: $"[player.leave: player {n} left] {m_roster.Describe()}")
            : CommandResult.Error(output: $"[player.leave: player {n} is not joined, or the actor was denied — see wire.errors/world.why]")
        );
    }
    // The other of the two quantization doors — see MoveRouter's remarks.
    private CommandResult LookRouter(CommandContext context, WireArgs args) {
        if (!TryStickValue(
            args: in args,
            context: context,
            error: out var error,
            value: out var value,
            verb: LookCommand
        )) {
            return error;
        }
        m_roster.RouteLook(
            slot: context.Slot,
            value: value,
            actingPrincipal: context.ActingPrincipal()
        );

        return CommandResult.None;
    }
    // Whether a trailing token spells the capture:on|off shape at all — used to decide whether the LAST token is a
    // capture argument (and so must be stripped before the player-index position is read) or genuinely the player
    // index itself.
    private static bool LooksLikeCaptureToken(ReadOnlySpan<char> token) =>
        token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "capture:"
        );
    private CommandResult MotionHandler(CommandContext context, WireArgs args) {
        var program = ((args.Count >= 1)
            ? args[0].ToString()
            : string.Empty
        );
        var hasMode = ((args.Count >= 1) && !args.TryInt(
            index: 0,
            value: out _
        ));

        var (player, index, error) = ResolveModeTarget(
            args: in args,
            choices: "<program>",
            hasMode: hasMode,
            verb: "player.motion"
        );

        if (error is { } modeError) {
            return modeError;
        }

        if (hasMode) {
            m_link.SubmitCommand(command: new WorldCommand.SetBodyMotion(
                Principal: context.ActingPrincipal(),
                EntityIndex: (index - 1),
                BodyMotionProgram: program
            ));

            // The submit drains synchronously (WorldServer.Submit), so the coherence door has already run by the time
            // control returns here — read back its verdict rather than assuming success, the same "deep refusal
            // reported in the read-back, not flagged IsError" shape player.designate's TargetsResult already uses
            // (the request itself was well-formed; the server-side switch was refused). Always echoes, unconditionally
            // (never gated by wire.ack quiet) — a refusal must never go silent.
            if (m_population.MotionRefusal(bodyIndex: (index - 1)) is { Length: > 0 } refusal) {
                return new CommandResult(Output: $"[player.motion: player {index} refused → {refusal}]");
            }

            return new CommandResult(Output: $"[player.motion: player {index} → {program}]");
        }

        // No program: echo the target's current selection.
        return new CommandResult(Output: $"[player.motion: player {index} is {player!.BodyMotionProgram}]");
    }
    // One of exactly two quantization doors (see CommandValueQuantization.QuantizeAxis's own remarks) — the router
    // seam where a physical stick float first becomes command state. Everything below this call is fixed point;
    // nothing downstream re-derives a conversion.
    private CommandResult MoveRouter(CommandContext context, WireArgs args) {
        if (!TryStickValue(
            args: in args,
            context: context,
            error: out var error,
            value: out var value,
            verb: MoveCommand
        )) {
            return error;
        }
        m_roster.RouteMove(
            slot: context.Slot,
            value: value,
            actingPrincipal: context.ActingPrincipal()
        );

        return CommandResult.None;
    }
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
    // The picker step direction while pending: only the Turn-role channel steers the picker (positive scale = next
    // candidate, negative = previous), every other channel is inert — the channel-role generalization of the old
    // fixed AxisTurnLeft/AxisTurnRight check. Reads the Turn role from the SAME table the caller resolved `ordinal`
    // against, never a different (e.g. boot) table — an ordinal is only ever meaningful against the table that
    // produced it.
    private static int PickerDirection(WorldChannelTable table, int ordinal, FixedQ4816 scale) {
        if (ordinal != table.RoleOrdinals.Turn) {
            return 0;
        }

        return Math.Sign(value: ((double)scale));
    }
    private CommandResult PoseHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.pose"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 7) {
                return CommandResult.Error(output: $"[player.pose: instance-targeted form expects 6 values — <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg>, any of which may be - to hold its current value — plus the REQUIRED instance seat, before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 6,
                verb: "player.pose"
            );

            if (instancePlayer is null) {
                return CommandResult.Error(output: slotError!);
            }

            if (!TryResolvePoseSegment(
                args: in args,
                error: out var parseError,
                pitchDegrees: out var ipitch,
                player: instancePlayer,
                rollDegrees: out var iroll,
                verb: "player.pose",
                x: out var ix,
                y: out var iy,
                yawDegrees: out var iyaw,
                z: out var iz
            )) {
                return parseError!.Value;
            }

            instance.Server.ApplyCommand(command: new WorldCommand.SnapPose(
                Principal: context.ActingPrincipal(),
                EntityIndex: (instanceSlot - 1),
                Position: new Vector3(
                    x: ix,
                    y: iy,
                    z: iz
                ),
                YawRadians: (iyaw * (MathF.PI / 180f)),
                PitchRadians: (ipitch * (MathF.PI / 180f)),
                RollRadians: (iroll * (MathF.PI / 180f)),
                Mode: SnapPoseMode.Pose
            ));

            return new CommandResult(Output: $"[player.pose: '{instance.Name}' seat {instanceSlot} ({ix:0.00}, {iy:0.00}, {iz:0.00}) yaw={iyaw:0}° pitch={ipitch:0}° roll={iroll:0}°]");
        }

        if (instanceTarget.EffectiveCount is not (6 or 7)) {
            return CommandResult.Error(output: "[player.pose: expected 6 values — <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg>, any of which may be - to hold its current value — plus an optional player index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 6,
            verb: "player.pose"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (!TryResolvePoseSegment(
            args: in args,
            error: out var bootParseError,
            pitchDegrees: out var pitchDegrees,
            player: player,
            rollDegrees: out var rollDegrees,
            verb: "player.pose",
            x: out var x,
            y: out var y,
            yawDegrees: out var yawDegrees,
            z: out var z
        )) {
            return bootParseError!.Value;
        }

        const float ToRadians = (MathF.PI / 180f);

        m_link.SubmitCommand(command: new WorldCommand.SnapPose(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1),
            Position: new Vector3(
                x: x,
                y: y,
                z: z
            ),
            YawRadians: (yawDegrees * ToRadians),
            PitchRadians: (pitchDegrees * ToRadians),
            RollRadians: (rollDegrees * ToRadians),
            Mode: SnapPoseMode.Pose
        ));

        return Echoed(
            args: in args,
            handler: $"[player.pose: ({x:0.00}, {y:0.00}, {z:0.00}) yaw={yawDegrees:0}° pitch={pitchDegrees:0}° roll={rollDegrees:0}°]"
        );
    }
    private CommandResult PressHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 4)) {
            return CommandResult.Error(output: "[player.press: expected a channel name — plus an optional value, hold time, and player index]");
        }

        // Layout: <channel> [value] [holdSeconds] [player]. Resolve the console-facing seat before the channel:
        // after a transfer the destination document owns both the body's channel vocabulary and the command door.
        // Looking either up in the boot world makes an otherwise fully routed seat lose action buttons precisely at
        // an invisible boundary (movement continued to work because player.fly already followed this route).
        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 3,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[player.press: player index must be an integer 1..{m_population.Capacity}]");
        }

        WorldAuthorityRoute? routedLocation = null;
        var targetChannels = m_channels;

        if (index <= PlayerRoster.MaxSlots) {
            var rosterSlot = PlayerRoster.SlotFromDisplay(number: index);
            var location = m_instances.SeatRoute(slot: rosterSlot);

            if (
                m_roster.IsJoined(slot: rosterSlot) &&
                !string.Equals(
                a: location.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                routedLocation = location;
                targetChannels = WorldChannelTable.Compile(channels: m_instances.ResolveRoutedDefinition(slot: rosterSlot).Channels);
            }
        }

        var channelName = args[0].ToString();

        if (!targetChannels.TryGetOrdinal(
            name: channelName,
            ordinal: out var ordinal
        )) {
            return CommandResult.Error(output: $"[player.press: unknown channel '{channelName}' — see world.affordances]");
        }

        if (routedLocation is null) {
            // Preserve the boot body's joined/active refusal and pending-seat semantics when no handoff occurred.
            var (player, _, error) = ResolveTarget(
                args: in args,
                requiredCount: 3,
                verb: "player.press"
            );

            if (player is null) {
                return CommandResult.Error(output: error!);
            }

            if (PendingTapeError(
                index: index,
                verb: "player.press"
            ) is { } pendingError) {
                return pendingError;
            }
        }

        var shape = targetChannels.Shape(ordinal: ordinal);
        var value = FixedQ4816.One;

        if (args.Count >= 2) {
            if (!args.TryFloat(
                index: 1,
                value: out var authoredValue
            )) {
                return CommandResult.Error(output: "[player.press: could not parse <value> as a number]");
            }

            value = FixedQ4816.FromDouble(value: authoredValue);
        }

        var shapeError = shape switch {
            ChannelShape.Binary when ((value != FixedQ4816.Zero) && (value != FixedQ4816.One)) => $"[player.press: channel \"{channelName}\" is binary — value must be 0 or 1]",
            ChannelShape.Unipolar when ((value < FixedQ4816.Zero) || (value > FixedQ4816.One)) => $"[player.press: channel \"{channelName}\" is unipolar — value must be in [0, 1]]",
            ChannelShape.Bipolar when ((value < -FixedQ4816.One) || (value > FixedQ4816.One)) => $"[player.press: channel \"{channelName}\" is bipolar — value must be in [-1, 1]]",
            _ => null,
        };

        if (shapeError is not null) {
            return CommandResult.Error(output: shapeError);
        }

        float? holdSeconds = null;
        var authoredHoldSeconds = 0f;

        if (args.Count >= 3) {
            if (!args.TryFloat(
                index: 2,
                value: out authoredHoldSeconds
            )) {
                return CommandResult.Error(output: "[player.press: could not parse <holdSeconds> as a number]");
            }

            // Sent raw, unclamped — the server is the sole authority over both caps (the deciding grant's ceiling
            // and the engine backstop) and the one that labels which bound the result. NaN and non-positive values
            // are handled authoritatively server-side (PressHoldCapKind.Ignored).
            holdSeconds = authoredHoldSeconds;
        }

        if (routedLocation is { } route) {
            route.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.PressChannel(
                Principal: context.ActingPrincipal(),
                EntityIndex: route.EntityIndex,
                ChannelOrdinal: ordinal,
                Value: value,
                HoldSeconds: holdSeconds
            ));

            // Federation submission is intentionally transport-shaped: unlike the in-process boot link, its
            // authoritative press outcome arrives through observation rather than as a synchronous population
            // side effect. Echo the request (and let wire.errors expose refusal) without fabricating which cap won.
            var routedDuration = ((holdSeconds is { } routedSeconds)
                ? $" for {routedSeconds:0.###}s"
                : " for one host step"
            );

            return Echoed(
                args: in args,
                handler: $"[player.press: {channelName}={((double)value):0.###} p{index} via '{route.Endpoint.Identity}' body={route.EntityIndex}{routedDuration}]"
            );
        }

        m_link.SubmitCommand(command: new WorldCommand.PressChannel(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1),
            ChannelOrdinal: ordinal,
            Value: value,
            HoldSeconds: holdSeconds
        ));

        // The submit drains synchronously (WorldServer.Submit), so the refusal — or the outcome — is already
        // recorded by the time control returns here. Refusal is checked FIRST and covers BOTH the timed and
        // untimed paths (they share one refusal slot): WorldServer writes it from EVERY early return a
        // PressChannel command can take, so a non-empty refusal means nothing below was ever applied and must not
        // be echoed as an affirmative quoting some earlier, unrelated attempt's numbers.
        var refusal = m_population.PressRefusal(bodyIndex: (index - 1));

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[player.press: {channelName}={((double)value):0.###} p{index} refused → {refusal}]");
        }

        if (holdSeconds is { } seconds) {
            // Read back the TRUE effective hold and which cap (if either) decided it, rather than assuming the
            // request was honored (WorldGrant.DefaultHoldSeconds silently truncates it otherwise) or guessing the
            // binder from the effective value's magnitude — CapKind is computed server-side against the actual
            // clamp inputs, so it names the binder that structurally applied, not whichever one a coincidence of
            // numbers would suggest.
            var outcome = m_population.LastPressOutcome(bodyIndex: (index - 1));

            switch (outcome.CapKind) {
                case PressHoldCapKind.Ignored:
                    return Echoed(
                        args: in args,
                        handler: $"[player.press: {channelName}={((double)value):0.###} p{index} — non-positive hold ignored, in-flight hold (if any) left untouched]"
                    );
                case PressHoldCapKind.GrantBudget:
                    return Echoed(
                        args: in args,
                        handler: $"[player.press: {channelName}={((double)value):0.###} p{index} holding {((double)outcome.EffectiveHoldSeconds):0.###}s — requested {authoredHoldSeconds:0.###}, capped by the grant's hold budget]"
                    );
                case PressHoldCapKind.EngineCeiling:
                    return Echoed(
                        args: in args,
                        handler: $"[player.press: {channelName}={((double)value):0.###} p{index} holding {((double)outcome.EffectiveHoldSeconds):0.###}s — requested {authoredHoldSeconds:0.###}, capped by the engine's {WorldBody.MaxActionHoldSeconds:0.###}s hold ceiling]"
                    );
                default:
                    return Echoed(
                        args: in args,
                        handler: $"[player.press: {channelName}={((double)value):0.###} p{index} for {seconds:0.###}s]"
                    );
            }
        }

        return Echoed(
            args: in args,
            handler: $"[player.press: {channelName}={((double)value):0.###} p{index} for one host step]"
        );
    }
    private CommandResult ProfileHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (1 or 2)) {
            return CommandResult.Error(output: "[player.identity: expected an identity name plus an optional player index — player.identity <name> [n]]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 1,
            fallback: 1,
            max: PlayerRoster.MaxSlots,
            min: 1,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[player.identity: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
        }

        var profileName = args[0].ToString();

        if (m_roster.FindProfile(name: profileName) is not { } profile) {
            return CommandResult.Error(output: $"[player.identity: no identity named '{profileName}' — see identity.list]");
        }

        return (m_roster.SetProfile(
            slot: PlayerRoster.SlotFromDisplay(number: index),
            profile: profile,
            actingPrincipal: context.ActingPrincipal()
        ) switch {
            SetProfileOutcome.NotJoined => CommandResult.Error(output: $"[player.identity: player {index} is not joined — see world.players]"),
            SetProfileOutcome.InUse => CommandResult.Error(output: $"[player.identity: identity '{profile.Name}' is already in use — see world.players]"),
            SetProfileOutcome.Denied => CommandResult.Error(output: $"[player.identity: {context.ActingPrincipal().Describe()} cannot set player {index}'s identity — see world.why]"),
            _ => new CommandResult(Output: $"[player.identity: player {index} is now {profile.Name}] {m_roster.Describe()}"),
        });
    }
    // The drive-a-player wire verbs. Each takes a zero-copy WireArgs (parsed from the stdin line span), marks every
    // failure IsError so `wire.ack quiet` drops only successes, and gates its success-echo on args.Echo so a quiet flood
    // builds no ack string. The error strings are the wire contract. player.where is a query (not AcknowledgementOnly) — its data
    // always echoes.
    private CommandResult ReconcileHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (3 or 4 or 5)) {
            return CommandResult.Error(output: "[player.reconcile: expected 3 values — <x> <z> <yawDegrees> — plus an optional smoothing time and player index]");
        }

        // Layout: <x> <z> <yawDegrees> [seconds] [player]. The trailing player index is the LAST token (as with every
        // drive-a-player verb); the optional [seconds] appears only in the full 5-token form. So the index sits at token 4
        // when seconds is present, token 3 otherwise — and is absent (default player 1) in the bare 3-token form.
        var hasSeconds = (args.Count == 5);

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: (hasSeconds
            ? 4
            : 3),
            verb: "player.reconcile"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (
            !args.TryFloat(
            index: 0,
            value: out var x
        ) ||
            !args.TryFloat(
            index: 1,
            value: out var z
        ) ||
            !args.TryFloat(
            index: 2,
            value: out var degrees
        )
        ) {
            return CommandResult.Error(output: "[player.reconcile: could not parse <x> <z> <yawDegrees> as numbers]");
        }

        var seconds = DefaultReconcileSeconds;

        if (
            hasSeconds &&
            !args.TryFloat(
            index: 3,
            value: out seconds
        )
        ) {
            return CommandResult.Error(output: "[player.reconcile: could not parse <seconds> as a number]");
        }

        seconds = Math.Clamp(
            max: MaxReconcileSeconds,
            min: MinReconcileSeconds,
            value: seconds
        );

        m_link.SubmitCommand(command: new WorldCommand.Reconcile(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1),
            X: x,
            Z: z,
            YawRadians: (degrees * (MathF.PI / 180f)),
            Seconds: seconds
        ));

        return Echoed(
            args: in args,
            handler: $"[player.reconcile: p{index} → ({x:0.00}, {z:0.00}) yaw={degrees:0}° over {seconds:0.##}s]"
        );
    }
    // Format a join result — a STRUCTURED denial/full/occupied/ok outcome (never a bare -1 collapsing "no room" and
    // "the actor was refused" into the same "roster is full" line the QUIBBLE named). slot is the specific slot for
    // an explicit-target request, or the attempted/resolved slot for a next-free one (-1 only for Full, where no
    // slot was ever found to name).
    private CommandResult ReportJoin(JoinResult result, int slot, bool active, WorldPrincipal actingPrincipal) {
        return (result switch {
            JoinResult.Ok => new CommandResult(Output: $"[player.join: player {PlayerRoster.DisplayNumber(slot: slot)} {(active
            ? "joined active"
            : "joined pending")}] {m_roster.Describe()}"),
            JoinResult.Occupied => CommandResult.Error(output: $"[player.join: player {PlayerRoster.DisplayNumber(slot: slot)} is already joined]"),
            JoinResult.Full => CommandResult.Error(output: $"[player.join: the roster is full ({PlayerRoster.MaxSlots} players)]"),
            _ => CommandResult.Error(output: $"[player.join: {actingPrincipal.Describe()} cannot join slot {PlayerRoster.DisplayNumber(slot: slot)} — see world.why]"),
        });
    }
    // Resolves a NAMED instance's own local-seat body at the 1-based slot token sitting at args[slotTokenIndex] —
    // bounded to WorldPopulation.LocalSeatCount, exactly like every retired world.instance.seat.* verb's own slot bound
    // (WorldInstanceCommandModule.TrySlot); a spawned instance's population entries beyond the local-seat range were
    // never addressable through seat.* either, so this preserves that scope rather than widening it.
    private static (WorldBody? Player, int Slot, string? Error) ResolveInstanceSlot(WorldInstance instance, in WireArgs args, int slotTokenIndex, string verb) {
        if (
            !args.TryInt(
            index: slotTokenIndex,
            value: out var slot
        ) ||
            (slot < 1) ||
            (slot > WorldPopulation.LocalSeatCount)
        ) {
            return (Player: null, Slot: 0, Error: $"[{verb}: instance-targeted slot must be an integer 1..{WorldPopulation.LocalSeatCount}]");
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
    private static string SourceWord(IntentSource source) => (source.IsIdle
        ? "idle"
        : ((source.ProducerName is { } producer)
            ? $"producer:{producer}"
            : "live"
    ));
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.state: expected at most 1 value — an optional player index]");
        }

        if (TryRoutedSeatQuery(
            args: in args,
            query: static index => new WorldQuery.PlayerState(Index: index),
            result: out var routed
        )) {
            return routed;
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.state"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerState(Index: index),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
            }
        );
        return result;
    }
    private CommandResult StateLoadHandler(CommandContext context, WireArgs args) {
        if (
            (args.Count < 2) ||
            (args.Count > 3)
        ) {
            return CommandResult.Error(output: "[player.state-load: expected <name> <counter-value|timer-seconds> [player]]");
        }
        if (
            !args.TryFloat(
            index: 1,
            value: out var authored
        ) ||
            !float.IsFinite(f: authored)
        ) {
            return CommandResult.Error(output: "[player.state-load: value must be finite]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 2,
            verb: "player.state-load"
        );
        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        var name = args[0].ToString();

        if (!player.TryDescribeActionState(
            kind: out var kind,
            lifetime: out var lifetime,
            name: name,
            playerWritable: out var playerWritable,
            timerTicks: out _,
            value: out _
        )) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' names no declared slot]");
        }
        if (lifetime != ActionStateLifetime.Durable) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' is ephemeral]");
        }
        if (!playerWritable) {
            return CommandResult.Error(output: $"[player.state-load: state '{name}' is not player-writable]");
        }
        if (
            (kind == ActionStateKind.Timer) &&
            (authored < 0f)
        ) {
            return CommandResult.Error(output: "[player.state-load: timer seconds must be non-negative]");
        }

        var value = ((kind == ActionStateKind.Counter)
            ? new DurableStateValue(
                Name: name,
                Value: FixedQ4816.FromDouble(value: authored),
                TimerTicks: 0UL
            )
            : new DurableStateValue(
                Name: name,
                Value: FixedQ4816.Zero,
                TimerTicks: FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: authored))
            )
        );
        var tick = m_server.NextInputTick;

        m_link.SubmitCommand(command: new WorldCommand.LoadDurableState(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1),
            Tick: tick,
            Values: [value]
        ));

        return Echoed(
            args: in args,
            handler: $"[player.state-load: p{index} {name} staged for tick {tick}]"
        );
    }
    // The gamepad's stick channels — routers, not polled — plus the sticks observability verb. The router bindings
    // fire every deflected frame; the handler routes the dispatch (with its device id) into the roster and returns
    // None (no stdout spam per frame).
    private IEnumerable<CommandDefinition> StickVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MoveCommand,
            description: "The left stick's movement channel (Axis2D, +Y forward / +X strafe right) — routed to the owning device's player each frame. A typed player.move <x> <y> injects one exact tick through this same router for automation and accessibility surfaces.",
            valueKind: CommandValueKind.Axis2D,
            handler: MoveRouter
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: LookCommand,
            description: "The right stick's look channel (Axis2D, +X looks right / +Y looks up) — routed to the owning device's player each frame. A typed player.look <x> <y> injects one exact tick through this same router.",
            valueKind: CommandValueKind.Axis2D,
            handler: LookRouter
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: "player.sticks",
            description: "Echoes every joined player's current analog — p<N> move=(x, y) look=(x, y). Values are cleared per frame, so a non-zero read only appears while a stick is actively deflected during this same command pump (the observability check for controller plumbing).",
            valueKind: CommandValueKind.Digital,
            handler: SticksHandler
        );
    }
    private CommandResult SticksHandler(CommandContext context) {
        var segments = new List<string>(capacity: PlayerRoster.MaxSlots);

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.Seat(slot: slot) is not { } seat) {
                continue;
            }

            // The one site that converts the seat's fixed-point analog state back to float for display — no
            // simulation consumer reads a float form; this is presentation-only.
            var move = seat.AnalogMove;
            var look = seat.AnalogLook;
            var moveX = ((float)((double)move.X));
            var moveY = ((float)((double)move.Y));
            var lookX = ((float)((double)look.X));
            var lookY = ((float)((double)look.Y));

            segments.Add(item: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"p{PlayerRoster.DisplayNumber(slot: slot)} move=({moveX:0.00}, {moveY:0.00}) look=({lookX:0.00}, {lookY:0.00})"
            ));
        }

        return new CommandResult(Output: $"[player.sticks: {string.Join(
            separator: " | ",
            values: segments
        )}]");
    }
    private CommandResult StopHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.stop"
        )) {
            return tokenError!.Value;
        }

        // The instance-targeted form applies via that instance's OWN ApplyCommand, exactly as a console line typed
        // into that instance would —
        // a bare "tape cleared" echo, never the boot form's richer refusal/outcome detail (no client mirrors a
        // spawned instance's seat, so there is no held-key/toggle-latch state to reconcile here either).
        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 1) {
                return CommandResult.Error(output: $"[player.stop: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 0,
                verb: "player.stop"
            );

            if (instancePlayer is null) {
                return CommandResult.Error(output: slotError!);
            }

            instance.Server.ApplyCommand(command: new WorldCommand.Stop(
                Principal: context.ActingPrincipal(),
                EntityIndex: (instanceSlot - 1)
            ));

            return new CommandResult(Output: $"[player.stop: '{instance.Name}' seat {instanceSlot} — tape cleared]");
        }

        if (instanceTarget.EffectiveCount > 1) {
            return CommandResult.Error(output: "[player.stop: expected at most 1 value — an optional player index]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var requestedIndex
        )) {
            return CommandResult.Error(output: $"[player.stop: player index must be an integer 1..{m_population.Capacity}]");
        }

        // A local seat retains its console-facing number after authority handoff. Stop through the same immutable
        // route used by live sticks and player.fly; resolving the departed boot body first would reject precisely
        // the panic command a traveler needs during a remote-control failure.
        if (requestedIndex <= PlayerRoster.MaxSlots) {
            var routedSlot = PlayerRoster.SlotFromDisplay(number: requestedIndex);
            var route = m_instances.SeatRoute(slot: routedSlot);

            if (
                m_roster.IsJoined(slot: routedSlot) &&
                !string.Equals(
                a: route.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                route.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.Stop(
                    Principal: context.ActingPrincipal(),
                    EntityIndex: route.EntityIndex
                ));
                m_roster.Seat(slot: routedSlot)?.ReleaseAllHeld();
                var routedLatches = router().ClearSlotHeld(slot: routedSlot);

                return Echoed(
                    args: in args,
                    handler: $"[player.stop: p{requestedIndex} via '{route.Endpoint.Identity}' body={route.EntityIndex} — tape and held input cleared, {routedLatches} toggle latch{((routedLatches == 1)
                    ? ""
                    : "es")} cleared]"
                );
            }
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.stop"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        m_link.SubmitCommand(command: new WorldCommand.Stop(
            Principal: context.ActingPrincipal(),
            EntityIndex: (index - 1)
        ));

        // The submit drains synchronously (WorldServer.Submit), so the outcome — or the refusal, if the Drive gate
        // denied it — is already recorded by the time control returns here. Refusal is checked FIRST: WorldServer
        // writes it from EVERY early return a Stop command can take, so a non-empty refusal means the counts below
        // were never applied and must not be echoed as if they were (the read-back shape player.motion's
        // MotionRefusal uses, mirrored so a refused stop can never quote another attempt's stale numbers).
        var refusal = m_population.StopRefusal(bodyIndex: (index - 1));

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[player.stop: player {index} refused → {refusal}]");
        }

        var outcome = m_population.LastStopOutcome(bodyIndex: (index - 1));
        var clearedLatches = 0;

        // A seat's held device state is client-side: free it here so the stop covers both halves. Only on an actual
        // stop — a refused command changed nothing server-side, so the seat's own local image should not be
        // silently dropped. This also releases a Toggle-mode channel latched ON (see BindingEntryMode), which a
        // physical release alone never reaches.
        if (IsSeat(index: index)) {
            var slot = PlayerRoster.SlotFromDisplay(number: index);

            m_roster.Seat(slot: slot)?.ReleaseAllHeld();
            clearedLatches = router().ClearSlotHeld(slot: slot);
        }

        return Echoed(
            args: in args,
            handler: $"[player.stop: player {index} — tape cleared, released {outcome.ReleasedHeldChannels} held channels, cleared {outcome.ClearedTimedPresses} timed presses, {clearedLatches} toggle latch{((clearedLatches == 1)
            ? ""
            : "es")} cleared]"
        );
    }
    // The identity an engagement route is recorded under for a 1-based display index — the seat's own claimed
    // identity (PlayerRoster.PrincipalOf, falling back to WorldPrincipal.Seat) for 1..4, or the population's current
    // peer identity for 5..128. Passed explicitly because only the client's roster knows about a claim override;
    // Server.WorldEngagement resolves a body's own principal by index arithmetic alone and has no roster to ask.
    private WorldPrincipal TargetPrincipalFor(int index) {
        return (IsSeat(index: index)
            ? m_roster.PrincipalOf(slot: PlayerRoster.SlotFromDisplay(number: index))
            : m_server.Population.PeerPrincipal(index: (index - 1))
        );
    }
    private CommandResult TargetsHandler(CommandContext context, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[player.targets: expected an optional player index]");
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.targets"
        );
        return ((player is null)
            ? CommandResult.Error(output: error!)
            : TargetsResult(index: index)
        );
    }
    private CommandResult TargetsResult(int index) {
        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerTargets(Index: index),
            completion: answer => {
                result = new CommandResult(Output: answer.Text) { IsError = answer.Refused };
            }
        );
        return result;
    }
    // Parses a player.pose positional axis token: a literal "-" holds the value already read into `current`;
    // anything else must parse as a finite float, exactly like every other drive-a-player float argument.
    private static bool TryFloatOrHold(in WireArgs args, int index, float current, out float value) {
        if (args.Is(
            index: index,
            value: "-"
        )) {
            value = current;

            return true;
        }

        return args.TryFloat(
            index: index,
            value: out value
        );
    }
    // Parses a confirmed capture: token's on|off value. Returns false (capture defaulted true) for anything else,
    // so the caller can report the exact malformed token rather than a generic parse failure.
    private static bool TryParseCapture(ReadOnlySpan<char> token, out bool capture) {
        var value = token[8..];

        if (value.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "on"
        )) {
            capture = true;

            return true;
        }

        if (value.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "off"
        )) {
            capture = false;

            return true;
        }

        capture = true;

        return false;
    }
    // Parses player.engage's target token: a bare non-negative integer names a SCREEN (the historical, unchanged
    // shape); "screen:<n>"/"body:<n>" name either explicitly (the context-routes widening) — the SAME grammar
    // world.grant's subject token already uses, so an operator who knows one already knows the other. Any other
    // GrantSubject shape (all/section/profile/composition) is not a legitimate engage target and is rejected.
    private static bool TryParseEngageTarget(ReadOnlySpan<char> token, out GrantSubject target) {
        if (
            GrantSubject.TryParse(
            subject: out target,
            token: token
        ) &&
            (target.Kind is GrantSubjectKind.Screen or GrantSubjectKind.Body)
        ) {
            return true;
        }

        if (
            int.TryParse(
            s: token,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out var screenIndex
        ) &&
            (screenIndex >= 0)
        ) {
            target = GrantSubject.Screen(index: screenIndex);

            return true;
        }

        target = default;

        return false;
    }
    // Parses and clamps player.fly's seven positional values — shared by the boot and instance-targeted branches so
    // the exact same [-1,1] clamp (every role channel IS bipolar by validator rule — WorldDefinitionValidator
    // .ValidateChannels refuses any other declared shape on a role channel) applies identically to both.
    private static bool TryParseFlySegment(in WireArgs args, out float forward, out float strafe, out float up, out float yaw, out float pitch, out float roll, out float seconds) {
        forward = strafe = up = yaw = pitch = roll = seconds = 0f;

        if (
            !args.TryFloat(
            index: 0,
            value: out forward
        ) ||
            !args.TryFloat(
            index: 1,
            value: out strafe
        ) ||
            !args.TryFloat(
            index: 2,
            value: out up
        ) ||
            !args.TryFloat(
            index: 3,
            value: out yaw
        ) ||
            !args.TryFloat(
            index: 4,
            value: out pitch
        ) ||
            !args.TryFloat(
            index: 5,
            value: out roll
        ) ||
            !args.TryFloat(
            index: 6,
            value: out seconds
        )
        ) {
            return false;
        }

        forward = Math.Clamp(
            max: 1f,
            min: -1f,
            value: forward
        );
        strafe = Math.Clamp(
            max: 1f,
            min: -1f,
            value: strafe
        );
        up = Math.Clamp(
            max: 1f,
            min: -1f,
            value: up
        );
        yaw = Math.Clamp(
            max: 1f,
            min: -1f,
            value: yaw
        );
        pitch = Math.Clamp(
            max: 1f,
            min: -1f,
            value: pitch
        );
        roll = Math.Clamp(
            max: 1f,
            min: -1f,
            value: roll
        );

        return true;
    }
    // Parse an intent-source token.
    private static bool TryParseIntentSource(ReadOnlySpan<char> token, out IntentSource source) {
        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "live"
        )) {
            source = IntentSource.Live;

            return true;
        }

        if (token.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            other: "idle"
        )) {
            source = IntentSource.Idle;

            return true;
        }

        if (
            token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "producer:"
        ) &&
            (token.Length > "producer:".Length)
        ) {
            source = IntentSource.Producer(name: token["producer:".Length..].ToString());

            return true;
        }

        source = IntentSource.Live;

        return false;
    }
    // The hold-current resolution shared by the boot and instance-targeted branches: a synchronous, same-thread read
    // of the same live body the caller just resolved, so nothing can move it before the SnapPose submission a few
    // lines later — one atomic write, never a read-then-write race. Heading/pitch/roll are decomposed from the
    // public Orientation with the exact inverse of WorldBody's Euler construction, so a held axis reproduces the
    // identical triple player.where would report.
    private static bool TryResolvePoseSegment(in WireArgs args, WorldBody player, string verb, out float x, out float y, out float z, out float yawDegrees, out float pitchDegrees, out float rollDegrees, out CommandResult? error) {
        x = y = z = yawDegrees = pitchDegrees = rollDegrees = 0f;

        var currentPosition = player.Position;

        var (currentYawDegrees, currentPitchDegrees, currentRollDegrees) = CurrentEulerDegrees(player: player);

        if (
            !TryFloatOrHold(
            args: in args,
            current: currentPosition.X,
            index: 0,
            value: out x
        ) ||
            !TryFloatOrHold(
            args: in args,
            current: currentPosition.Y,
            index: 1,
            value: out y
        ) ||
            !TryFloatOrHold(
            args: in args,
            current: currentPosition.Z,
            index: 2,
            value: out z
        ) ||
            !TryFloatOrHold(
            args: in args,
            current: currentYawDegrees,
            index: 3,
            value: out yawDegrees
        ) ||
            !TryFloatOrHold(
            args: in args,
            current: currentPitchDegrees,
            index: 4,
            value: out pitchDegrees
        ) ||
            !TryFloatOrHold(
            args: in args,
            current: currentRollDegrees,
            index: 5,
            value: out rollDegrees
        )
        ) {
            error = CommandResult.Error(output: $"[{verb}: could not parse the six values as numbers (each may be - to hold its current value)]");

            return false;
        }

        error = null;

        return true;
    }
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

        var route = m_instances.SeatRoute(slot: slot);

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
    private static bool TryStickValue(CommandContext context, in WireArgs args, string verb, out FixedVector2 value, out CommandResult error) {
        if (args.Count == 0) {
            value = CommandValueQuantization.QuantizeAxis(value: context.Value.AsAxis2D);
            error = default;
            return true;
        }
        if (
            (args.Count != 2) ||
            !args.TryFloat(
            index: 0,
            value: out var x
        ) ||
            !args.TryFloat(
            index: 1,
            value: out var y
        ) ||
            !float.IsFinite(f: x) ||
            !float.IsFinite(f: y)
        ) {
            value = default;
            error = CommandResult.Error(output: $"[{verb}: expected two finite values — <x> <y>]");
            return false;
        }

        value = CommandValueQuantization.QuantizeAxis(value: new Vector2(
            x: Math.Clamp(
                max: 1f,
                min: -1f,
                value: x
            ),
            y: Math.Clamp(
                max: 1f,
                min: -1f,
                value: y
            )
        ));
        error = default;
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
    private CommandResult WhereHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "player.where"
        )) {
            return tokenError!.Value;
        }

        // The instance-targeted form reads straight out of the NAMED instance's OWN tick snapshot via its own
        // WorldServer.Answer — never the boot world's — and carries no perception anchor (that is client presentation
        // state a spawned instance's seat has no client mirroring).
        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 1) {
                return CommandResult.Error(output: $"[player.where: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldPopulation.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldPopulation.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[player.where: instance-targeted <slot> must be an integer 1..{WorldPopulation.LocalSeatCount}]");
            }

            var instanceAnswer = instance.Server.Answer(query: new WorldQuery.PlayerWhere(Index: instanceSlot));

            return new CommandResult(Output: WithInstanceTag(
                text: instanceAnswer.Text,
                instanceName: instance.Name
            )) {
                IsError = instanceAnswer.Refused,
            };
        }

        if (instanceTarget.EffectiveCount > 1) {
            return CommandResult.Error(output: "[player.where: expected at most 1 value — an optional player index]");
        }

        if (
            WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 1,
            max: m_population.Capacity,
            fallback: 1,
            value: out var routedIndex
        ) &&
            (routedIndex <= PlayerRoster.MaxSlots)
        ) {
            var rosterSlot = PlayerRoster.SlotFromDisplay(number: routedIndex);
            var location = m_instances.SeatRoute(slot: rosterSlot);

            if (
                m_roster.IsJoined(slot: rosterSlot) &&
                !string.Equals(
                a: location.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                var routedResult = default(CommandResult);

                location.Endpoint.Submissions.Query(
                    query: new WorldQuery.PlayerWhere(Index: (location.EntityIndex + 1)),
                    completion: answer => {
                        var current = m_instances.SeatRoute(slot: rosterSlot);
                        var tagged = WithInstanceTag(
                            text: answer.Text,
                            instanceName: current.Endpoint.Identity
                        );

                        routedResult = new CommandResult(Output: $"{tagged[..^1]} anchor=body:{current.EntityIndex}]") { IsError = answer.Refused };
                    }
                );
                return routedResult;
            }
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "player.where"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        // A query verb (not AcknowledgementOnly): the pose read-back IS the answer, so it always echoes — even under wire.ack quiet.
        // Every pose is the server's to report; the answer prints verbatim, and its verdict rides through as IsError so a
        // miss the client-side guard did not catch still reaches wire.errors. The completion fires INLINE over loopback,
        // so the result is settled before this call returns — the console result formats from it, never a live read.
        var result = default(CommandResult);

        m_link.Query(
            query: new WorldQuery.PlayerWhere(Index: index),
            completion: answer => {
                result = new CommandResult(Output: WithPerceptionAnchor(
                    text: answer.Text,
                    index: index,
                    refused: answer.Refused
                )) {
                    IsError = answer.Refused,
                };
            }
        );

        return result;
    }
    // Splices ` instance:<name>` just inside a bracketed echo's closing ']' — the same surgery WithPerceptionAnchor
    // uses for anchor=body:<n>, reused here because an instance-targeted read has no perception anchor to report but
    // still owes the caller which instance answered.
    private static string WithInstanceTag(string text, string instanceName) =>
        (text.EndsWith(value: ']')
            ? $"{text[..^1]} instance:{instanceName}]"
            : text
        );
    // The perception-anchor read-back: a LOCAL seat's player.where answer carries anchor=body:<n> — the 0-based body
    // index ALL of that seat's presentation derives from (camera eye, audio listener, seat.<n>.position.* HUD
    // bindings; see Client.WorldPerceptionAnchor) — spliced inside the server's bracketed echo CLIENT-side, because
    // the anchor is client presentation state the server never holds and the wire answer must stay untouched.
    // Refusals and non-seat targets (5..128 own no seat, hence no anchor) pass through verbatim.
    private string WithPerceptionAnchor(string text, int index, bool refused) {
        if (
            refused ||
            (index > PlayerRoster.MaxSlots) ||
            !text.EndsWith(value: ']')
        ) {
            return text;
        }

        return $"{text[..^1]} anchor=body:{m_anchor.PerceivedBody(slot: PlayerRoster.SlotFromDisplay(number: index))}]";
    }

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

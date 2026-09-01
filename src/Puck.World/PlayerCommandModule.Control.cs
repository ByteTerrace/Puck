using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    /// <summary>The runtime command name a seat binding lowers one validated channel ordinal to. Every possible
    /// ordinal is registered at boot, so destination discovery never mutates the command registry or its replay ids.</summary>
    /// <param name="ordinal">The channel ordinal.</param>
    internal static string RoutedChannelCommandName(int ordinal) => Puck.World.Client.PlayerCommandNames.RoutedChannelCommandName(ordinal: ordinal);

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
                    return CommandResult.Error(output: $"[{commandName}: an internal bound-channel destination, not a typed verb — use body.press <channel-name> [value] [holdSeconds] [player] to script it]");
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
            return CommandResult.Error(output: "[body.channels: expected at most 1 value — an optional body index]");
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
            verb: "body.channels"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        // A query verb, exactly like body.where: the channel read-back IS the answer, so it always echoes — even
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
    private CommandResult ControlHandler(CommandContext context, WireArgs args) {
        // Token 0 is the MODE only when it names one; otherwise the whole (0- or 1-token) tail is just the body index
        // for a read-back — the same positional shape as body.motion, so a bare `body.control 7` echoes body 7's
        // source while `body.control idle` sets body 0 with no positional guesswork.
        var source = IntentSource.Live;
        var hasMode = ((args.Count >= 1) && TryParseIntentSource(
            token: args[0],
            source: out source
        ));

        var (player, index, error) = ResolveModeTarget(
            args: in args,
            choices: "live|idle|producer:<name>",
            hasMode: hasMode,
            verb: "body.control"
        );

        if (error is { } modeError) {
            return modeError;
        }

        // A PENDING seat's source cannot be set — its inputs drive the profile picker, not gameplay, so a source set
        // now would sit dormant and take effect only on confirm. Reuse the tape verbs' pending guard (seats only;
        // population entries 4..127 are never pending). Gates BOTH set and read — a pending seat is always Live anyway.
        if (PendingTapeError(
            index: index,
            verb: "body.control"
        ) is { } pendingError) {
            return pendingError;
        }

        if (hasMode) {
            if (!m_population.SupportsSource(
                index: index,
                refusal: out var refusal,
                source: source
            )) {
                return CommandResult.Error(output: $"[body.control: {refusal}]");
            }

            if (ReplayDriveError(verb: "body.control") is { } driveError) {
                return driveError;
            }

            m_link.SubmitCommand(command: new WorldCommand.SetControl(
                Principal: context.ActingPrincipal(),
                EntityIndex: index,
                Source: source
            ));

            // The seat's client-side source copy gates the live device producers; write it in the same command so the
            // mask lands with no tick gap (dropping any held keys/lanes on the transition).
            if (IsSeat(index: index)) {
                m_roster.Seat(slot: index)?.SetIntentSource(source: source);
            }

            return Echoed(
                args: in args,
                handler: $"[body.control: body:{index} {SourceWord(source: source)}]"
            );
        }

        // No mode: a read-back — echo the target's current source. Always surfaced (a query answer), like body.motion.
        return new CommandResult(Output: $"[body.control: body:{index} is {SourceWord(source: player!.Source)}]");
    }
    // The other of the two quantization doors — see MoveRouter's remarks.
    // A held presentation flag on the dispatching seat: press/active edges set it, release/cancel edges clear it.
    private CommandResult HeldFlag(CommandContext context, Action<SeatController, bool> set) {
        if (m_roster.Seat(slot: context.Slot) is { } seat) {
            set(
                arg1: seat,
                arg2: (context.Phase is CommandPhase.Started or CommandPhase.Active)
            );
        }

        return CommandResult.None;
    }
    private CommandResult LookRouter(CommandContext context, WireArgs args, SeatLookBehavior behavior, string verb) {
        if (!TryStickValue(
            args: in args,
            context: context,
            error: out var error,
            value: out var value,
            verb: verb
        )) {
            return error;
        }
        m_roster.RouteLook(
            slot: context.Slot,
            value: value,
            behavior: behavior,
            actingPrincipal: context.ActingPrincipal()
        );

        return CommandResult.None;
    }
    // One of exactly two quantization doors (see CommandValueQuantization.QuantizeAxis's own remarks) — the router
    // seam where a physical stick float first becomes command state. Everything below this call is fixed point;
    // nothing downstream re-derives a conversion.
    private CommandResult MoveRouter(CommandContext context, WireArgs args, SeatMoveBehavior behavior, string verb) {
        if (!TryStickValue(
            args: in args,
            context: context,
            error: out var error,
            value: out var value,
            verb: verb
        )) {
            return error;
        }
        m_roster.RouteMove(
            slot: context.Slot,
            value: value,
            behavior: behavior,
            actingPrincipal: context.ActingPrincipal()
        );

        return CommandResult.None;
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
    private CommandResult PressHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 4)) {
            return CommandResult.Error(output: "[body.press: expected a channel name — plus an optional value, hold time, and body index]");
        }

        // Layout: <channel> [value] [holdSeconds] [body]. Resolve the console-facing seat before the channel:
        // after a transfer the destination document owns both the body's channel vocabulary and the command door.
        // Looking either up in the boot world makes an otherwise fully routed seat lose action buttons precisely at
        // an invisible boundary (movement continued to work because body.fly already followed this route).
        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 3,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[body.press: body index must be an integer 0..{(m_population.Capacity - 1)}]");
        }

        WorldAuthorityRoute? routedLocation = null;
        var targetChannels = m_channels;

        if (IsSeat(index: index)) {
            var rosterSlot = index;
            var location = seatRouter.Route(slot: rosterSlot);

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
            return CommandResult.Error(output: $"[body.press: unknown channel '{channelName}' — see world.affordances]");
        }

        if (routedLocation is null) {
            // Preserve the boot body's joined/active refusal and pending-seat semantics when no handoff occurred.
            var (player, _, error) = ResolveTarget(
                args: in args,
                requiredCount: 3,
                verb: "body.press"
            );

            if (player is null) {
                return CommandResult.Error(output: error!);
            }

            if (PendingTapeError(
                index: index,
                verb: "body.press"
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
                return CommandResult.Error(output: "[body.press: could not parse <value> as a number]");
            }

            value = FixedQ4816.FromDouble(value: authoredValue);
        }

        var shapeError = shape switch {
            ChannelShape.Binary when ((value != FixedQ4816.Zero) && (value != FixedQ4816.One)) => $"[body.press: channel \"{channelName}\" is binary — value must be 0 or 1]",
            ChannelShape.Unipolar when ((value < FixedQ4816.Zero) || (value > FixedQ4816.One)) => $"[body.press: channel \"{channelName}\" is unipolar — value must be in [0, 1]]",
            ChannelShape.Bipolar when ((value < -FixedQ4816.One) || (value > FixedQ4816.One)) => $"[body.press: channel \"{channelName}\" is bipolar — value must be in [-1, 1]]",
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
                return CommandResult.Error(output: "[body.press: could not parse <holdSeconds> as a number]");
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
                handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} via '{route.Endpoint.Identity}' body={route.EntityIndex}{routedDuration}]"
            );
        }

        if (ReplayDriveError(verb: "body.press") is { } driveError) {
            return driveError;
        }

        m_link.SubmitCommand(command: new WorldCommand.PressChannel(
            Principal: context.ActingPrincipal(),
            EntityIndex: index,
            ChannelOrdinal: ordinal,
            Value: value,
            HoldSeconds: holdSeconds
        ));

        // The submit drains synchronously (WorldServer.Submit), so the refusal — or the outcome — is already
        // recorded by the time control returns here. Refusal is checked FIRST and covers BOTH the timed and
        // untimed paths (they share one refusal slot): WorldServer writes it from EVERY early return a
        // PressChannel command can take, so a non-empty refusal means nothing below was ever applied and must not
        // be echoed as an affirmative quoting some earlier, unrelated attempt's numbers.
        var refusal = m_population.PressRefusal(bodyIndex: index);

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[body.press: {channelName}={((double)value):0.###} body:{index} refused → {refusal}]");
        }

        if (holdSeconds is { } seconds) {
            // Read back the TRUE effective hold and which cap (if either) decided it, rather than assuming the
            // request was honored (WorldGrant.DefaultHoldSeconds silently truncates it otherwise) or guessing the
            // binder from the effective value's magnitude — CapKind is computed server-side against the actual
            // clamp inputs, so it names the binder that structurally applied, not whichever one a coincidence of
            // numbers would suggest.
            var outcome = m_population.LastPressOutcome(bodyIndex: index);

            switch (outcome.CapKind) {
                case PressHoldCapKind.Ignored:
                    return Echoed(
                        args: in args,
                        handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} — non-positive hold ignored, in-flight hold (if any) left untouched]"
                    );
                case PressHoldCapKind.GrantBudget:
                    return Echoed(
                        args: in args,
                        handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} holding {((double)outcome.EffectiveHoldSeconds):0.###}s — requested {authoredHoldSeconds:0.###}, capped by the grant's hold budget]"
                    );
                case PressHoldCapKind.EngineCeiling:
                    return Echoed(
                        args: in args,
                        handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} holding {((double)outcome.EffectiveHoldSeconds):0.###}s — requested {authoredHoldSeconds:0.###}, capped by the engine's {WorldBody.MaxActionHoldSeconds:0.###}s hold ceiling]"
                    );
                default:
                    return Echoed(
                        args: in args,
                        handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} for {seconds:0.###}s]"
                    );
            }
        }

        return Echoed(
            args: in args,
            handler: $"[body.press: {channelName}={((double)value):0.###} body:{index} for one host step]"
        );
    }
    private static string SourceWord(IntentSource source) => (source.IsIdle
        ? "idle"
        : ((source.ProducerName is { } producer)
            ? $"producer:{producer}"
            : "live"
    ));
    // The gamepad's stick channels — routers, not polled — plus the sticks observability verb. The router bindings
    // fire every deflected frame; the handler routes the dispatch (with its device id) into the roster and returns
    // None (no stdout spam per frame).
    private IEnumerable<CommandDefinition> StickVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MoveCommand,
            description: "The left stick's movement pair (Axis2D, +Y forward / +X strafe right), CAMERA-framed and facing the way it moves — the seat rotates it by the rendered camera yaw and writes its direction to the FaceX/FaceZ roles before it reaches the wire, whatever frame the world declared on its channel rows (channels[].frame). Routed to the owning device's player each frame. A typed player.move <x> <y> injects one exact tick through this same router for automation and accessibility surfaces.",
            valueKind: CommandValueKind.Axis2D,
            handler: (context, args) => MoveRouter(
                context: context,
                args: args,
                behavior: SeatMoveBehavior.FaceTravel,
                verb: MoveCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: MoveStrafeCommand,
            description: "An action movement pair (Axis2D, +Y forward / +X strafe right) that preserves body heading and follows live camera yaw every tick, so lateral input is a true strafe and holding forward while looking turns the trajectory. Standard binds the left gamepad stick here; player.move remains available for latched movement-facing control.",
            valueKind: CommandValueKind.Axis2D,
            handler: (context, args) => MoveRouter(
                context: context,
                args: args,
                behavior: SeatMoveBehavior.Strafe,
                verb: MoveStrafeCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: LookCommand,
            description: "A free-orbit look channel (Axis2D, +X looks right / +Y looks up) — camera only, routed to the owning device's player each frame. A typed player.look <x> <y> injects one exact tick through this same router.",
            valueKind: CommandValueKind.Axis2D,
            handler: (context, args) => LookRouter(
                context: context,
                args: args,
                behavior: SeatLookBehavior.Orbit,
                verb: LookCommand
            )
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: LookSteerCommand,
            description: "An action look channel (Axis2D, +X turns/looks right / +Y looks up): orbits the camera and, while deflected, writes its planar yaw into FaceX/FaceZ so horizontal look turns the upright body. Standard binds the right gamepad stick here; player.look remains available for authored free orbit.",
            valueKind: CommandValueKind.Axis2D,
            handler: (context, args) => LookRouter(
                context: context,
                args: args,
                behavior: SeatLookBehavior.FaceBody,
                verb: LookSteerCommand
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: FreeLookCommand,
            description: "Held modifier: a player.look.steer right stick continues orbiting the camera but stops writing body heading, while left-stick movement resolves against authoritative character heading instead of camera yaw. Standard authors LT + RB here. Independent from player.orbit, which arms pointer motion.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => HeldFlag(
                context: context,
                set: static (seat, held) => seat.SetFreeLook(held: held)
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: OrbitCommand,
            description: "Held: while down, pointer motion orbits the seat camera (yaw/pitch at playerDefaults.seatLook's pointer sensitivities); the body is untouched. Bind it once — the router delivers the release to a held verb. Presentation-only: nothing reaches the sim.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => HeldFlag(
                context: context,
                set: static (seat, held) => seat.SetOrbit(held: held)
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: RecenterLookCommand,
            description: "Held: turns the seat camera round behind the body and KEEPS it there while down (the body turning under it drags the camera along); the seat rig's own smoothing eases each turn. Bind it once — the router delivers the release to a held verb. Presentation-only: nothing reaches the sim. Look-behind is not a verb: author the seat rig's orbit yaw as a state binding (orbit.yaw: state.look.behind) and flip that cell — see player.state.cell.toggle.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => HeldFlag(
                context: context,
                set: static (seat, held) => seat.SetRecenter(held: held)
            )
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: MotionControlsCommand,
            description: "Toggles the seat's motion-input mode. Standard authors LT + North; each press alternates on/off. While enabled, gamepad angular velocity controls camera look through playerDefaults.seatLook.gyro's full-axis projection and authored follow yields. Presentation-only today; the generic mode also admits a future orientation/tilt movement adapter.",
            valueKind: CommandValueKind.Digital,
            handler: context => {
                _ = m_roster.Seat(slot: context.Slot)?.ToggleMotionControls();

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: MotionAngularCommand,
            description: "The gamepad's provider-neutral angular velocity (Axis3D radians/second, +X right / +Y up / +Z back). An already-seated player consumes the full vector only while player.motion.controls is toggled on; it never joins a seat by sensor noise and never reaches deterministic simulation.",
            valueKind: CommandValueKind.Axis3D,
            handler: context => {
                if (m_roster.Seat(slot: context.Slot) is { } seat) {
                    seat.SetMotionAngularVelocity(angularVelocity: context.Value.AsAxis3D);
                }

                return CommandResult.None;
            }
        );
        yield return CommandDefinition.Verb(
            bindability: CommandBindability.Bindable,
            name: SteerCommand,
            description: "Held: while down, pointer motion orbits the seat camera AND the body faces where the camera looks — the seat composes the camera facing into the world's FaceX/FaceY/FaceZ channels each tick and the sim's facing snap turns the body (needs those channels and views.seatControl.yawReference World; the validator refuses a binding without them). Bind it once, like player.orbit.",
            valueKind: CommandValueKind.Digital,
            held: true,
            handler: context => HeldFlag(
                context: context,
                set: static (seat, held) => seat.SetSteer(held: held)
            )
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
            var move = seat.Move.Value;
            var look = seat.Look.Value;
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
}

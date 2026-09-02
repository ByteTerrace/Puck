using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    // The body.reconcile smoothing window: the default when [seconds] is omitted, and the clamp a supplied value is
    // held to.
    private const float DefaultReconcileSeconds = 0.25f;
    private const float MaxReconcileSeconds = 2f;
    private const float MinReconcileSeconds = 0.05f;

    // A resolved pose's current heading decomposed to degrees — the exact inverse of the Euler construction
    // WorldBody.Pose applies (Ry(yaw)·Rx(pitch)·Rz(roll)), read from an orientation quaternion so body.pose's
    // "-" hold never re-derives a fact its source (a local WorldBody or a routed endpoint's mirrored pose) does
    // not already expose; mirrors WorldBody's own private EulerRadians (see its remarks on the codebase-wide
    // yaw-about-+Y / pitch-about-+X / roll-about-+Z convention).
    private static (float YawDegrees, float PitchDegrees, float RollDegrees) CurrentEulerDegrees(Quaternion orientation) {
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
    private CommandResult FlyHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "body.fly"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 8) {
                return CommandResult.Error(output: $"[body.fly: instance-targeted form expects 7 values — <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> — plus the REQUIRED instance seat, before instance:<name> — slot is 1..{WorldBodiesLimits.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 7,
                verb: "body.fly"
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
                return CommandResult.Error(output: "[body.fly: could not parse the seven values as numbers]");
            }

            if (!(iSeconds > 0f)) {
                return CommandResult.Error(output: "[body.fly: <seconds> must be greater than 0]");
            }

            // The instance's OWN channel table — a spawned instance's document may declare channels differently from
            // the boot world's, so this is compiled from ITS definition, never
            // the boot instance's m_channels.
            var instanceChannels = WorldChannelTable.Compile(channels: instance.Server.Definition.Channels);

            instance.Server.ApplyCommand(command: new WorldCommand.EnqueueSegment(
                Principal: context.ActingPrincipal(),
                EntityIndex: WorldPopulation.EntityFromDisplay(number: instanceSlot),
                Intent: instanceChannels.RoleOrdinals.Intent(
                    moveAdvance: FixedQ4816.FromDouble(value: iForward),
                    moveStrafe: FixedQ4816.FromDouble(value: iStrafe),
                    turn: FixedQ4816.FromDouble(value: iYaw),
                    moveUp: FixedQ4816.FromDouble(value: iUp),
                    pitch: FixedQ4816.FromDouble(value: iPitch),
                    roll: FixedQ4816.FromDouble(value: iRoll)
                ),
                Seconds: iSeconds
            ));

            return new CommandResult(Output: $"[body.fly: '{instance.Name}' seat {instanceSlot} fwd={iForward:0.##} strafe={iStrafe:0.##} up={iUp:0.##} yaw={iYaw:0.##} pitch={iPitch:0.##} roll={iRoll:0.##} for {iSeconds:0.##}s]");
        }

        if (instanceTarget.EffectiveCount is not (7 or 8)) {
            return CommandResult.Error(output: "[body.fly: expected 7 values — <forward> <strafe> <up> <yaw> <pitch> <roll> <seconds> — plus an optional body index]");
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
            return CommandResult.Error(output: "[body.fly: could not parse the seven values as numbers]");
        }

        if (!(seconds > 0f)) {
            return CommandResult.Error(output: "[body.fly: <seconds> must be greater than 0]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 7,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[body.fly: body index must be an integer 0..{(m_population.Capacity - 1)}]");
        }

        // A local seat's 0-based index IS its roster slot — no display-number conversion. Follow the
        // identical live route body.where and ordinary device intent already use; resolving through the boot
        // roster here would reject the deliberately departed boot body and make a remotely presented seat
        // inspectable but not script-drivable. The routed link owns both local-instance and federated credential
        // translation, while the destination definition supplies its own channel ordinals.
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
                var routedChannels = WorldChannelTable.Compile(channels: m_instances.ResolveRoutedDefinition(slot: rosterSlot).Channels);

                location.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.EnqueueSegment(
                    Principal: context.ActingPrincipal(),
                    EntityIndex: location.EntityIndex,
                    Intent: routedChannels.RoleOrdinals.Intent(
                        moveAdvance: FixedQ4816.FromDouble(value: forward),
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
                    handler: $"[body.fly: body:{index} via '{location.Endpoint.Identity}' body={location.EntityIndex} fwd={forward:0.##} strafe={strafe:0.##} up={up:0.##} yaw={yaw:0.##} pitch={pitch:0.##} roll={roll:0.##} for {seconds:0.##}s]"
                );
            }
        }

        var (player, resolvedIndex, error) = ResolveTarget(
            args: in args,
            requiredCount: 7,
            verb: "body.fly"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (PendingTapeError(
            index: resolvedIndex,
            verb: "body.fly"
        ) is { } pendingError) {
            return pendingError;
        }

        if (ReplayDriveError(verb: "body.fly") is { } driveError) {
            return driveError;
        }

        // The fly channel order (forward, strafe, up, yaw, pitch, roll) maps onto PlayerIntent (MoveAdvance, MoveStrafe,
        // Turn, MoveUp, Pitch, Roll) — the "yaw" channel is the Turn rate.
        m_link.SubmitCommand(command: new WorldCommand.EnqueueSegment(
            Principal: context.ActingPrincipal(),
            EntityIndex: resolvedIndex,
            Intent: m_channels.RoleOrdinals.Intent(
                moveAdvance: FixedQ4816.FromDouble(value: forward),
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
            handler: $"[body.fly: fwd={forward:0.##} strafe={strafe:0.##} up={up:0.##} yaw={yaw:0.##} pitch={pitch:0.##} roll={roll:0.##} for {seconds:0.##}s]"
        );
    }
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
            verb: "body.motion"
        );

        if (error is { } modeError) {
            return modeError;
        }

        if (hasMode) {
            if (ReplayDriveError(verb: "body.motion") is { } driveError) {
                return driveError;
            }

            m_link.SubmitCommand(command: new WorldCommand.SetBodyMotion(
                Principal: context.ActingPrincipal(),
                EntityIndex: index,
                BodyMotionProgram: program
            ));

            // The submit drains synchronously (WorldServer.Submit), so the coherence door has already run by the time
            // control returns here — read back its verdict rather than assuming success, the same "deep refusal
            // reported in the read-back, not flagged IsError" shape body.designate's TargetsResult already uses
            // (the request itself was well-formed; the server-side switch was refused). Always echoes, unconditionally
            // (never gated by wire.ack quiet) — a refusal must never go silent.
            if (m_population.MotionRefusal(bodyIndex: index) is { Length: > 0 } refusal) {
                return new CommandResult(Output: $"[body.motion: body:{index} refused → {refusal}]");
            }

            return new CommandResult(Output: $"[body.motion: body:{index} → {program}]");
        }

        // No program: echo the target's current selection.
        return new CommandResult(Output: $"[body.motion: body:{index} is {player!.BodyMotionProgram}]");
    }
    private CommandResult PoseHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "body.pose"
        )) {
            return tokenError!.Value;
        }

        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 7) {
                return CommandResult.Error(output: $"[body.pose: instance-targeted form expects 6 values — <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg>, any of which may be - to hold its current value — plus the REQUIRED instance seat, before instance:<name> — slot is 1..{WorldBodiesLimits.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 6,
                verb: "body.pose"
            );

            if (instancePlayer is null) {
                return CommandResult.Error(output: slotError!);
            }

            if (!TryResolvePoseSegment(
                args: in args,
                currentOrientation: instancePlayer.Orientation,
                currentPosition: instancePlayer.Position,
                error: out var parseError,
                pitchDegrees: out var ipitch,
                rollDegrees: out var iroll,
                verb: "body.pose",
                x: out var ix,
                y: out var iy,
                yawDegrees: out var iyaw,
                z: out var iz
            )) {
                return parseError!.Value;
            }

            instance.Server.ApplyCommand(command: new WorldCommand.SnapPose(
                Principal: context.ActingPrincipal(),
                EntityIndex: WorldPopulation.EntityFromDisplay(number: instanceSlot),
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

            return new CommandResult(Output: $"[body.pose: '{instance.Name}' seat {instanceSlot} ({ix:0.00}, {iy:0.00}, {iz:0.00}) yaw={iyaw:0}° pitch={ipitch:0}° roll={iroll:0}°]");
        }

        if (instanceTarget.EffectiveCount is not (6 or 7)) {
            return CommandResult.Error(output: "[body.pose: expected 6 values — <x> <y> <z> <yawDeg> <pitchDeg> <rollDeg>, any of which may be - to hold its current value — plus an optional body index]");
        }

        // Follow the identical live route body.where/body.fly already use before falling back to the boot
        // roster: resolving through the boot roster here would reject a deliberately departed boot body and make a
        // remotely presented seat inspectable but not teleportable. A held ("-") axis reads the routed endpoint's
        // own mirrored pose, never the local (stale) body.
        if (
            WorldArgs.TryParseIndex(
            args: in args,
            at: 6,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var routedIndex
        ) &&
            IsSeat(index: routedIndex)
        ) {
            var rosterSlot = routedIndex;
            var location = seatRouter.Route(slot: rosterSlot);

            if (
                m_roster.IsJoined(slot: rosterSlot) &&
                !string.Equals(
                a: location.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                if (!location.Endpoint.TryEntityPose(
                    index: location.EntityIndex,
                    orientation: out var routedOrientation,
                    position: out var routedPosition
                )) {
                    return CommandResult.Error(output: $"[body.pose: '{location.Endpoint.Identity}' body {location.EntityIndex} is not active]");
                }

                if (!TryResolvePoseSegment(
                    args: in args,
                    currentOrientation: routedOrientation,
                    currentPosition: routedPosition,
                    error: out var routedParseError,
                    pitchDegrees: out var rpitch,
                    rollDegrees: out var rroll,
                    verb: "body.pose",
                    x: out var rx,
                    y: out var ry,
                    yawDegrees: out var ryaw,
                    z: out var rz
                )) {
                    return routedParseError!.Value;
                }

                const float RoutedToRadians = (MathF.PI / 180f);

                location.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.SnapPose(
                    Principal: context.ActingPrincipal(),
                    EntityIndex: location.EntityIndex,
                    Position: new Vector3(
                        x: rx,
                        y: ry,
                        z: rz
                    ),
                    YawRadians: (ryaw * RoutedToRadians),
                    PitchRadians: (rpitch * RoutedToRadians),
                    RollRadians: (rroll * RoutedToRadians),
                    Mode: SnapPoseMode.Pose
                ));

                return Echoed(
                    args: in args,
                    handler: $"[body.pose: body:{routedIndex} via '{location.Endpoint.Identity}' body={location.EntityIndex} ({rx:0.00}, {ry:0.00}, {rz:0.00}) yaw={ryaw:0}° pitch={rpitch:0}° roll={rroll:0}°]"
                );
            }
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 6,
            verb: "body.pose"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (!TryResolvePoseSegment(
            args: in args,
            currentOrientation: player.Orientation,
            currentPosition: player.Position,
            error: out var bootParseError,
            pitchDegrees: out var pitchDegrees,
            rollDegrees: out var rollDegrees,
            verb: "body.pose",
            x: out var x,
            y: out var y,
            yawDegrees: out var yawDegrees,
            z: out var z
        )) {
            return bootParseError!.Value;
        }

        const float ToRadians = (MathF.PI / 180f);

        if (ReplayDriveError(verb: "body.pose") is { } driveError) {
            return driveError;
        }

        m_link.SubmitCommand(command: new WorldCommand.SnapPose(
            Principal: context.ActingPrincipal(),
            EntityIndex: index,
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
            handler: $"[body.pose: ({x:0.00}, {y:0.00}, {z:0.00}) yaw={yawDegrees:0}° pitch={pitchDegrees:0}° roll={rollDegrees:0}°]"
        );
    }
    // The drive-a-player wire verbs. Each takes a zero-copy WireArgs (parsed from the stdin line span), marks every
    // failure IsError so `wire.ack quiet` drops only successes, and gates its success-echo on args.Echo so a quiet flood
    // builds no ack string. The error strings are the wire contract. body.where is a query (not AcknowledgementOnly) — its data
    // always echoes.
    private CommandResult ReconcileHandler(CommandContext context, WireArgs args) {
        if (args.Count is not (3 or 4 or 5)) {
            return CommandResult.Error(output: "[body.reconcile: expected 3 values — <x> <z> <yawDegrees> — plus an optional smoothing time and body index]");
        }

        // Layout: <x> <z> <yawDegrees> [seconds] [body]. The trailing body index is the LAST token (as with every
        // drive-a-body verb); the optional [seconds] appears only in the full 5-token form. So the index sits at token 4
        // when seconds is present, token 3 otherwise — and is absent (default body 0) in the bare 3-token form.
        var hasSeconds = (args.Count == 5);

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: (hasSeconds
            ? 4
            : 3),
            verb: "body.reconcile"
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
            return CommandResult.Error(output: "[body.reconcile: could not parse <x> <z> <yawDegrees> as numbers]");
        }

        var seconds = DefaultReconcileSeconds;

        if (
            hasSeconds &&
            !args.TryFloat(
            index: 3,
            value: out seconds
        )
        ) {
            return CommandResult.Error(output: "[body.reconcile: could not parse <seconds> as a number]");
        }

        seconds = Math.Clamp(
            max: MaxReconcileSeconds,
            min: MinReconcileSeconds,
            value: seconds
        );

        m_link.SubmitCommand(command: new WorldCommand.Reconcile(
            Principal: context.ActingPrincipal(),
            EntityIndex: index,
            X: x,
            Z: z,
            YawRadians: (degrees * (MathF.PI / 180f)),
            Seconds: seconds
        ));

        return Echoed(
            args: in args,
            handler: $"[body.reconcile: body:{index} → ({x:0.00}, {z:0.00}) yaw={degrees:0}° over {seconds:0.##}s]"
        );
    }
    private CommandResult StopHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "body.stop"
        )) {
            return tokenError!.Value;
        }

        // The instance-targeted form applies via that instance's OWN ApplyCommand, exactly as a console line typed
        // into that instance would —
        // a bare "tape cleared" echo, never the boot form's richer refusal/outcome detail (no client mirrors a
        // spawned instance's seat, so there is no held-key/toggle-latch state to reconcile here either).
        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 1) {
                return CommandResult.Error(output: $"[body.stop: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldBodiesLimits.LocalSeatCount}]");
            }

            var (instancePlayer, instanceSlot, slotError) = ResolveInstanceSlot(
                args: in args,
                instance: instance,
                slotTokenIndex: 0,
                verb: "body.stop"
            );

            if (instancePlayer is null) {
                return CommandResult.Error(output: slotError!);
            }

            instance.Server.ApplyCommand(command: new WorldCommand.Stop(
                Principal: context.ActingPrincipal(),
                EntityIndex: WorldPopulation.EntityFromDisplay(number: instanceSlot)
            ));

            return new CommandResult(Output: $"[body.stop: '{instance.Name}' seat {instanceSlot} — tape cleared]");
        }

        if (instanceTarget.EffectiveCount > 1) {
            return CommandResult.Error(output: "[body.stop: expected at most 1 value — an optional body index]");
        }

        if (!WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var requestedIndex
        )) {
            return CommandResult.Error(output: $"[body.stop: body index must be an integer 0..{(m_population.Capacity - 1)}]");
        }

        // A local seat's 0-based index is its roster slot directly — no display-number conversion. Stop through the
        // same immutable route used by live sticks and body.fly; resolving the departed boot body first would
        // reject precisely the panic command a traveler needs during a remote-control failure.
        if (IsSeat(index: requestedIndex)) {
            var routedSlot = requestedIndex;
            var route = seatRouter.Route(slot: routedSlot);

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
                    handler: $"[body.stop: body:{requestedIndex} via '{route.Endpoint.Identity}' body={route.EntityIndex} — tape and held input cleared, {routedLatches} toggle latch{((routedLatches == 1)
                    ? ""
                    : "es")} cleared]"
                );
            }
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.stop"
        );

        if (player is null) {
            return CommandResult.Error(output: error!);
        }

        if (ReplayDriveError(verb: "body.stop") is { } driveError) {
            return driveError;
        }

        m_link.SubmitCommand(command: new WorldCommand.Stop(
            Principal: context.ActingPrincipal(),
            EntityIndex: index
        ));

        // The submit drains synchronously (WorldServer.Submit), so the outcome — or the refusal, if the Drive gate
        // denied it — is already recorded by the time control returns here. Refusal is checked FIRST: WorldServer
        // writes it from EVERY early return a Stop command can take, so a non-empty refusal means the counts below
        // were never applied and must not be echoed as if they were (the read-back shape body.motion's
        // MotionRefusal uses, mirrored so a refused stop can never quote another attempt's stale numbers).
        var refusal = m_population.StopRefusal(bodyIndex: index);

        if (refusal is { Length: > 0 }) {
            return new CommandResult(Output: $"[body.stop: body:{index} refused → {refusal}]");
        }

        var outcome = m_population.LastStopOutcome(bodyIndex: index);
        var clearedLatches = 0;

        // A seat's held device state is client-side: free it here so the stop covers both halves. Only on an actual
        // stop — a refused command changed nothing server-side, so the seat's own local image should not be
        // silently dropped. This also releases a Toggle-mode channel latched ON (see BindingEntryMode), which a
        // physical release alone never reaches.
        if (IsSeat(index: index)) {
            var slot = index;

            m_roster.Seat(slot: slot)?.ReleaseAllHeld();
            clearedLatches = router().ClearSlotHeld(slot: slot);
        }

        return Echoed(
            args: in args,
            handler: $"[body.stop: body:{index} — tape cleared, released {outcome.ReleasedHeldChannels} held channels, cleared {outcome.ClearedTimedPresses} timed presses, {clearedLatches} toggle latch{((clearedLatches == 1)
            ? ""
            : "es")} cleared]"
        );
    }
    // Parses a body.pose positional axis token: a literal "-" holds the value already read into `current`;
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
    // Parses and clamps body.fly's seven positional values — shared by the boot and instance-targeted branches so
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
    // The hold-current resolution shared by the boot, routed, and instance-targeted branches: a synchronous,
    // same-thread read of the same live pose the caller just resolved (a local body's own state, or a routed
    // endpoint's mirrored pose), so nothing can move it before the SnapPose submission a few lines later — one
    // atomic write, never a read-then-write race. Heading/pitch/roll are decomposed from the orientation with the
    // exact inverse of WorldBody's Euler construction, so a held axis reproduces the identical triple body.where
    // would report.
    private static bool TryResolvePoseSegment(in WireArgs args, Vector3 currentPosition, Quaternion currentOrientation, string verb, out float x, out float y, out float z, out float yawDegrees, out float pitchDegrees, out float rollDegrees, out CommandResult? error) {
        x = y = z = yawDegrees = pitchDegrees = rollDegrees = 0f;

        var (currentYawDegrees, currentPitchDegrees, currentRollDegrees) = CurrentEulerDegrees(orientation: currentOrientation);

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
    private CommandResult WhereHandler(CommandContext context, WireArgs args) {
        if (!TryStripInstanceToken(
            args: in args,
            error: out var tokenError,
            target: out var instanceTarget,
            verb: "body.where"
        )) {
            return tokenError!.Value;
        }

        // The instance-targeted form reads straight out of the NAMED instance's OWN tick snapshot via its own
        // WorldServer.Answer — never the boot world's — and carries no perception anchor (that is client presentation
        // state a spawned instance's seat has no client mirroring).
        if (instanceTarget.Instance is { } instance) {
            if (instanceTarget.EffectiveCount != 1) {
                return CommandResult.Error(output: $"[body.where: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldBodiesLimits.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldBodiesLimits.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[body.where: instance-targeted <slot> must be an integer 1..{WorldBodiesLimits.LocalSeatCount}]");
            }

            var instanceAnswer = instance.Server.Answer(query: new WorldQuery.PlayerWhere(Index: WorldPopulation.EntityFromDisplay(number: instanceSlot)));

            return new CommandResult(Output: WithInstanceTag(
                text: instanceAnswer.Text,
                instanceName: instance.Name
            )) {
                IsError = instanceAnswer.Refused,
            };
        }

        if (instanceTarget.EffectiveCount > 1) {
            return CommandResult.Error(output: "[body.where: expected at most 1 value — an optional body index]");
        }

        if (
            WorldArgs.TryParseIndex(
            args: in args,
            at: 0,
            min: 0,
            max: (m_population.Capacity - 1),
            fallback: 0,
            value: out var routedIndex
        ) &&
            IsSeat(index: routedIndex)
        ) {
            var rosterSlot = routedIndex;

            // The boot claim's Endpoint.Submissions IS the injected local link, so routing through it would answer
            // identically to the local arm below — this selector exists only for the boot path's untagged,
            // anchor-free output, never because routing itself would misbehave.
            if (
                m_roster.IsJoined(slot: rosterSlot) &&
                (seatRouter.TryRoute(slot: rosterSlot) is { } location) &&
                !string.Equals(
                a: location.Endpoint.Identity,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            ) &&
                seatRouter.TryRouteQuery(
                factory: authorityIndex => new WorldQuery.PlayerWhere(Index: (authorityIndex - 1)),
                result: out var routed,
                slot: rosterSlot,
                tagInstance: true
            )
            ) {
                // player.where's own anchor=body:N suffix, off the route as it stands now — it may have moved on
                // since the query was submitted.
                var current = (seatRouter.TryRoute(slot: rosterSlot) ?? location);

                return new CommandResult(Output: $"{routed.Output[..^1]} anchor=body:{current.EntityIndex}]") { IsError = routed.IsError };
            }
        }

        var (player, index, error) = ResolveTarget(
            args: in args,
            requiredCount: 0,
            verb: "body.where"
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
    // The perception-anchor read-back: a LOCAL seat's body.where answer carries anchor=body:<n> — the 0-based body
    // index ALL of that seat's presentation derives from (camera eye, audio listener, seat.<n>.position.* HUD
    // bindings; see Client.WorldPerceptionAnchor) — spliced inside the server's bracketed echo CLIENT-side, because
    // the anchor is client presentation state the server never holds and the wire answer must stay untouched.
    // Refusals and non-seat targets (4..127 own no seat, hence no anchor) pass through verbatim.
    private string WithPerceptionAnchor(string text, int index, bool refused) {
        if (
            refused ||
            !IsSeat(index: index)
        ) {
            return text;
        }

        return CommandEcho.SpliceTag(
            text: text,
            tag: $"anchor=body:{m_anchor.PerceivedBody(slot: index)}"
        );
    }
}

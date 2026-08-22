using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
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
    private CommandResult DescribeAssign(string verb, AssignOutcome outcome, int slot) => PlayerAssignmentCommand.Describe(
        outcome: outcome,
        roster: m_roster,
        slot: slot,
        verb: verb
    );
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
                return CommandResult.Error(output: $"[player.join: instance-targeted form expects <slot> [identity], before instance:<name> — slot is 1..{WorldPopulationLimits.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldPopulationLimits.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[player.join: instance-targeted <slot> must be an integer 1..{WorldPopulationLimits.LocalSeatCount}]");
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
                return CommandResult.Error(output: $"[player.leave: instance-targeted form expects <slot>, before instance:<name> — slot is 1..{WorldPopulationLimits.LocalSeatCount}]");
            }

            if (
                !args.TryInt(
                index: 0,
                value: out var instanceSlot
            ) ||
                (instanceSlot < 1) ||
                (instanceSlot > WorldPopulationLimits.LocalSeatCount)
            ) {
                return CommandResult.Error(output: $"[player.leave: instance-targeted <slot> must be an integer 1..{WorldPopulationLimits.LocalSeatCount}]");
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
}

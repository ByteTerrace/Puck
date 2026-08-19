using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    // The intent source captured at the moment a seat's family state enters a camera-targeting state — restored the
    // moment it leaves one. Per-seat, not per-family: only one control application is active at a time (a document
    // whose seatModes declare two simultaneously-reachable camera-targeting states across different families is an
    // authoring choice the validator does not forbid, but the second activation simply re-captures the seat's
    // CURRENT source, which is already Idle by then, so it restores correctly regardless).
    private readonly IntentSource[] m_modePriorSource = new IntentSource[PlayerRoster.MaxSlots];

    // Diverts the seat's own body intent to Idle (the existing player.control contract, applied on both halves so
    // the mask lands with no tick gap) and activates the fly rig, seeded from the current chase framing.
    private void ActivateCameraApplication(CommandContext context, int slot) {
        var controller = m_roster.Seat(slot: slot);

        m_modePriorSource[slot] = (controller?.Source ?? IntentSource.Live);

        m_link.SubmitCommand(command: new WorldCommand.SetControl(
            Principal: m_roster.PrincipalOf(slot: slot),
            EntityIndex: slot,
            Source: IntentSource.Idle
        ));
        controller?.SetIntentSource(source: IntentSource.Idle);
        m_flyRig.Activate(slot: slot);
    }
    // Restores the seat's captured intent source and deactivates the fly rig — the chase rig re-anchors to the
    // avatar deterministically, so there is no pose to restore.
    private void DeactivateCameraApplication(int slot) {
        m_flyRig.Deactivate(slot: slot);

        m_link.SubmitCommand(command: new WorldCommand.SetControl(
            Principal: m_roster.PrincipalOf(slot: slot),
            EntityIndex: slot,
            Source: m_modePriorSource[slot]
        ));
        m_roster.Seat(slot: slot)?.SetIntentSource(source: m_modePriorSource[slot]);
    }
    private static bool IsCameraTarget(WorldSeatModeState? state) => string.Equals(
        a: state?.Target,
        b: "camera",
        comparisonType: StringComparison.Ordinal
    );
    private CommandResult ModeHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 3)) {
            return CommandResult.Error(output: $"[{ModeCommand}: expected <family> <state> [seat], or <family> [seat] to read back]");
        }

        var family = args[0].ToString();

        if (args.Count == 1) {
            var (readSlot, readError) = SeatCommandArgs.ResolveSlot(
                args: in args,
                at: 1,
                context: context,
                verb: ModeCommand
            );

            if (readError is { } resolveReadError) {
                return resolveReadError;
            }

            return ReadMode(
                family: family,
                slot: readSlot
            );
        }

        var state = args[1].ToString();
        var (slot, error) = SeatCommandArgs.ResolveSlot(
            args: in args,
            at: 2,
            context: context,
            verb: ModeCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (m_seatBindings.TryResolveMode(
            family: family,
            slot: slot
        ) is not { } modeFamily) {
            return CommandResult.Error(output: $"[{ModeCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document declares no family '{family}']");
        }

        WorldSeatModeState? targetState = null;

        foreach (var candidate in modeFamily.States) {
            if (string.Equals(
                a: candidate.Name,
                b: state,
                comparisonType: StringComparison.Ordinal
            )) {
                targetState = candidate;

                break;
            }
        }

        if (targetState is null) {
            var admissible = string.Join(
                separator: ", ",
                values: modeFamily.States.Select(selector: static candidate => candidate.Name)
            );

            return CommandResult.Error(output: $"[{ModeCommand}: family '{family}' has no state '{state}' — {admissible}]");
        }

        var previousStateName = m_seatBindings.ModeState(
            family: family,
            slot: slot
        );
        WorldSeatModeState? previousState = null;

        if (previousStateName is not null) {
            foreach (var candidate in modeFamily.States) {
                if (string.Equals(
                    a: candidate.Name,
                    b: previousStateName,
                    comparisonType: StringComparison.Ordinal
                )) {
                    previousState = candidate;

                    break;
                }
            }
        }

        var wasCamera = IsCameraTarget(state: previousState);
        var isCamera = IsCameraTarget(state: targetState);

        if (
            wasCamera &&
            !isCamera
        ) {
            DeactivateCameraApplication(slot: slot);
        } else if (
            !wasCamera &&
            isCamera
        ) {
            ActivateCameraApplication(
                context: context,
                slot: slot
            );
        }

        m_seatBindings.SetContextState(
            family: family,
            slot: slot,
            state: state
        );

        return SeatCommandArgs.Echo(
            detail: $"'{family}' = '{state}'",
            slot: slot,
            verb: ModeCommand
        );
    }
    private IEnumerable<CommandDefinition> ModeVerbs() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: ModeCommand,
            description: "Flips a seat's published state within an AUTHORED per-seat mode family (see the document's seatModes section): player.mode <family> <state> [seat] (seat 1..4, default 1). player.mode <family> [seat] reads back the seat's current state. A state whose family declares target: \"camera\" composes the fly control application: the seat's own body intent diverts to Idle (the same player.control idle contract — a live tape or player.press still drives it) and the world-authored views.flyRig frames the seat instead, seeded from the current chase framing (no pose pop); leaving such a state restores the seat's prior intent source. An unknown family or state is refused by name, naming every admissible sibling.",
            handler: ModeHandler,
            routing: CommandRouting.Simulation
        );
    }
    private CommandResult ReadMode(int slot, string family) {
        if (m_seatBindings.TryResolveMode(
            family: family,
            slot: slot
        ) is null) {
            return CommandResult.Error(output: $"[{ModeCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)}'s document declares no family '{family}']");
        }

        var state = (m_seatBindings.ModeState(
            family: family,
            slot: slot
        ) ?? "(unpublished)");

        return SeatCommandArgs.Echo(
            detail: $"'{family}' = '{state}'",
            slot: slot,
            verb: ModeCommand
        );
    }
}

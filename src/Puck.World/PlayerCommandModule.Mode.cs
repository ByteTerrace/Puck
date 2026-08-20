using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    // Diverts the seat's own body intent to Idle (the existing player.control contract, applied on both halves so
    // the mask lands with no tick gap) and activates the fly rig, seeded from the current chase framing. The SetControl
    // is stamped with the ACTING principal (never the target seat's own), so the server's Drive gate
    // (WorldServer.ApplyCommand) checks the ACTOR's authority over the target body exactly as player.control does; the
    // ModeHandler caller has already refused the whole command when that authority is absent, so this only ever runs
    // for a command the server will accept — nothing here mutates local state ahead of a doomed submission.
    private void ActivateCameraApplication(WorldPrincipal actingPrincipal, int slot) {
        var controller = m_roster.Seat(slot: slot);
        // The source to restore on exit. A seat that is ALREADY diverted (Idle) when this runs — a prior application
        // torn down without a clean deactivate, e.g. a world reseed dropping the mode state — must never record the
        // diversion itself as the restore target, or exit would leave the seat permanently dead-stick; fall back to
        // Live so exit re-admits the live device. The fly rig holds this alongside its own active flag, so a departed
        // seat's prune clears it too.
        var priorSource = (controller?.Source ?? IntentSource.Live);

        if (priorSource.IsIdle) {
            priorSource = IntentSource.Live;
        }

        m_link.SubmitCommand(command: new WorldCommand.SetControl(
            Principal: actingPrincipal,
            EntityIndex: slot,
            Source: IntentSource.Idle
        ));
        controller?.SetIntentSource(source: IntentSource.Idle);
        m_flyRig.Activate(
            priorSource: priorSource,
            slot: slot
        );
    }
    // Restores the seat's captured intent source (held by the fly rig) and deactivates the fly rig — the chase rig
    // re-anchors to the avatar deterministically, so there is no pose to restore. The restoring SetControl carries the
    // acting principal for the same Drive-gate reason ActivateCameraApplication does.
    private void DeactivateCameraApplication(WorldPrincipal actingPrincipal, int slot) {
        WorldCameraApplication.Deactivate(
            actingPrincipal: actingPrincipal,
            flyRig: m_flyRig,
            link: m_link,
            roster: m_roster,
            slot: slot
        );
    }
    private static bool IsCameraTarget(WorldSeatModeState? state) => string.Equals(
        a: state?.Target,
        b: WorldSeatModeState.CameraTarget,
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

        // The two-token fold: an integer 1..MaxSlots second token reads that seat back; anything else is a state
        // for the resolved seat. A state named like a seat index stays reachable through the three-token form.
        if (
            (args.Count == 2) &&
            WorldArgs.TryParseIndex(
                args: args,
                at: 1,
                fallback: null,
                max: PlayerRoster.MaxSlots,
                min: 1,
                value: out var readSeat
            )
        ) {
            return ReadMode(
                family: family,
                slot: PlayerRoster.SlotFromDisplay(number: readSeat)
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

        // Composing or dissolving the fly control application idles/restores the TARGET body and swaps its rig and
        // binding group — a Drive-authority act. Gate it on the ACTOR's Drive over the target body (the same grant the
        // server's SetControl door checks), so a session holding Drive over only its own body cannot idle another
        // seat's body or hijack its fly rig by naming a trailing [seat]. A pure non-camera flip is presentation only
        // and needs no such authority.
        if (wasCamera != isCamera) {
            var actingPrincipal = context.ActingPrincipal();

            if (!m_server.Grants.Allows(
                principal: actingPrincipal,
                capability: WorldCapability.Drive,
                subject: GrantSubject.Body(index: slot)
            ).IsAllowed) {
                return CommandResult.Error(output: $"[{ModeCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} — {actingPrincipal.Describe()} cannot drive body:{slot} to compose the fly control application]");
            }

            if (wasCamera) {
                DeactivateCameraApplication(
                    actingPrincipal: actingPrincipal,
                    slot: slot
                );
            } else {
                ActivateCameraApplication(
                    actingPrincipal: actingPrincipal,
                    slot: slot
                );
            }
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
            description: "Flips a seat's published state within an AUTHORED per-seat mode family (see the document's seatModes section): player.mode <family> <state> [seat] (seat 1..4, default 1). player.mode <family> [seat] reads back the seat's current state (a two-token line whose second token is an integer 1..4 reads that SEAT; a state named like a seat index needs the explicit three-token form). A state whose family declares target: \"camera\" composes the fly control application: the seat's own body intent diverts to Idle (the same player.control idle contract — a live tape or player.press still drives it) and the world-authored views.flyRig frames the seat instead, seeded from the current chase framing (no pose pop); leaving such a state restores the seat's prior intent source. An unknown family or state is refused by name, naming every admissible sibling.",
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

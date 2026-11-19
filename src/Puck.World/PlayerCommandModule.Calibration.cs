using System.Globalization;

using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class PlayerCommandModule {
    private static readonly CommandForms PlayerCalibrateForms = CommandForms.Of(verb: CalibrateCommand, "<seconds> [seat]");

    // player.calibrate <seconds> [seat]: submits the seat's calibration as an ordered authority command, so the offset
    // is hashed, replayed and applied at an epoch boundary. The authority bounds it by inputHistory.calibrationLimitSeconds
    // and narrates a refusal on input.refused.
    private CommandResult CalibrateHandler(CommandContext context, WireArgs args) {
        if (args.Count is (< 1 or > 2)) {
            return PlayerCalibrateForms.Refuse(args: in args);
        }

        if (!FixedQ4816.TryParse(
            provider: CultureInfo.InvariantCulture,
            result: out var offset,
            s: args[0].ToString()
        )) {
            return CommandResult.Error(output: $"[{CalibrateCommand}: could not parse <seconds> as a number]");
        }

        var (slot, error) = SeatCommandArgs.ResolveJoinedSeat(
            args: in args,
            at: 1,
            context: context,
            roster: m_roster,
            verb: CalibrateCommand
        );

        if (error is { } resolveError) {
            return resolveError;
        }

        if (seatRouter.TryRoute(slot: slot) is not { } route) {
            return CommandResult.Error(output: $"[{CalibrateCommand}: seat {PlayerRoster.DisplayNumber(slot: slot)} has no authority route]");
        }

        route.Endpoint.Submissions.SubmitCommand(command: new WorldCommand.CalibrateInput(m_roster.PrincipalOf(slot: slot), route.EntityIndex, offset));

        return SeatCommandArgs.Echo(
            detail: $"calibration {offset.ToString(format: null, formatProvider: CultureInfo.InvariantCulture)}s submitted",
            slot: slot,
            verb: CalibrateCommand
        );
    }
    private IEnumerable<CommandDefinition> CalibrationVerbs() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: CalibrateCommand,
            forms: PlayerCalibrateForms,
            description: $"Sets a seat's input latency calibration: {PlayerCalibrateForms.Spelling} (seat 1..{PlayerRoster.MaxSlots}, default the acting seat). <seconds> is signed and added to the captured time of every press of this seat that the authority judges, so a player who presses late against the beat they hear, because of display or audio delay, calibrates with a negative value. The authority applies it at an epoch boundary, keeps it in its hashed state and replay, and bounds it by the document's inputHistory.calibrationLimitSeconds (zero when unauthored, which refuses every nonzero value on input.refused). Bind it with a literal argument for a calibration exercise's choices.",
            handler: CalibrateHandler,
            routing: CommandRouting.Simulation
        );
    }
}

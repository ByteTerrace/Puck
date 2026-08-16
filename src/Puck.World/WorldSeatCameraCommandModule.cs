using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The seat-owned camera read-back. Camera yaw and pitch participate in camera-relative simulation input,
/// so their observability belongs to the authoritative client/seat core and is available in every executable shape,
/// including a headless federation canary.</summary>
internal sealed class WorldSeatCameraCommandModule(WorldInstanceHost instances, PlayerRoster roster, WorldContinuum continuum) : ICommandModule {
    private string Describe(int slot) {
        const float RadiansToDegrees = (180f / MathF.PI);
        var definition = instances.ResolveRoutedDefinition(slot: slot);
        var seatLook = (roster.Seat(slot: slot)?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
        var control = definition.Views.SeatControl;
        var state = roster.Seat(slot: slot)?.View;
        var route = instances.SeatRoute(slot: slot);
        var builder = new StringBuilder(value: "[world.view.camera: ");
        var resolved = continuum.TryResolveSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var anchor,
            slot: slot
        );

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"player={PlayerRoster.DisplayNumber(slot: slot)} authority={route.Endpoint.Identity} entity={route.Entity.Authority}/{route.Entity.Index}#{route.Entity.Generation} epoch={route.Epoch} resolved={resolved.ToString().ToLowerInvariant()}"
        );
        if (resolved) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" anchor=({anchor.X:0.###},{anchor.Y:0.###},{anchor.Z:0.###})"
            );
        }
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" arming={seatLook.Arming.ToString().ToLowerInvariant()} yawReference={control.YawReference.ToString().ToLowerInvariant()} yawSensitivity={seatLook.YawSensitivity:0.#####} pitchSensitivity={seatLook.PitchSensitivity:0.#####} stickLookRate={seatLook.StickLookRate:0.#####} invertYaw={seatLook.InvertYaw.ToString().ToLowerInvariant()} invertPitch={seatLook.InvertPitch.ToString().ToLowerInvariant()} minPitch={(control.MinPitch * RadiansToDegrees):0.##} maxPitch={(control.MaxPitch * RadiansToDegrees):0.##}"
        );
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" yaw={((state?.Yaw ?? 0f) * RadiansToDegrees):0.##} pitch={((state?.Pitch ?? 0f) * RadiansToDegrees):0.##}"
        );

        return builder.Append(value: ']').ToString();
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.camera",
            description: $"Echoes a seat's live control policy and camera orbit: world.view.camera [player], player 1..{PlayerRoster.MaxSlots}. The definition follows the seat's current authority route; profile look preferences remain seat-owned across handoff.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: $"[world.view.camera: too many arguments — expected [<player>], player an integer 1..{PlayerRoster.MaxSlots}]");
                }
                if (!WorldArgs.TryParseIndex(
                    args: args,
                    at: 0,
                    fallback: 1,
                    max: PlayerRoster.MaxSlots,
                    min: 1,
                    value: out var player
                )) {
                    return CommandResult.Error(output: $"[world.view.camera: player index must be an integer 1..{PlayerRoster.MaxSlots}]");
                }

                return new CommandResult(Output: Describe(slot: PlayerRoster.SlotFromDisplay(number: player)));
            },
            routing: CommandRouting.Immediate
        );
    }
}

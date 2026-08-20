using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The seat-owned camera read-back. Camera yaw and pitch participate in camera-relative simulation input,
/// so their observability belongs to the authoritative client/seat core and is available in every executable shape,
/// including a headless federation canary.</summary>
internal sealed class WorldSeatCameraCommandModule(WorldInstanceHost instances, PlayerRoster roster, WorldContinuum continuum, WorldSeatAuthorityRouter seatRouter, WorldSeatBindings seatBindings) : ICommandModule {
    private string Describe(int slot) {
        const float RadiansToDegrees = (180f / MathF.PI);
        var definition = instances.ResolveRoutedDefinition(slot: slot);
        var seat = roster.Seat(slot: slot);
        var seatLook = (seat?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
        var gyro = seatLook.Gyro;
        var control = definition.Views.SeatControl;
        var state = seat?.View;
        var angularVelocity = (seat?.MotionAngularVelocity ?? Vector3.Zero);
        var route = seatRouter.Route(slot: slot);
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
            handler: $" yawReference={control.YawReference.ToString().ToLowerInvariant()} yawSensitivity={seatLook.YawSensitivity:0.#####} pitchSensitivity={seatLook.PitchSensitivity:0.#####} stickLookRate={seatLook.StickLookRate:0.#####} gyroScale={gyro.Scale:0.#####} gyroDeadZone=({gyro.DeadZone.X:0.#####},{gyro.DeadZone.Y:0.#####},{gyro.DeadZone.Z:0.#####}) gyroInvert=({gyro.InvertX.ToString().ToLowerInvariant()},{gyro.InvertY.ToString().ToLowerInvariant()},{gyro.InvertZ.ToString().ToLowerInvariant()}) gyroYaw=({gyro.Yaw.X:0.#####},{gyro.Yaw.Y:0.#####},{gyro.Yaw.Z:0.#####}) gyroPitch=({gyro.Pitch.X:0.#####},{gyro.Pitch.Y:0.#####},{gyro.Pitch.Z:0.#####}) invertYaw={seatLook.InvertYaw.ToString().ToLowerInvariant()} invertPitch={seatLook.InvertPitch.ToString().ToLowerInvariant()} minPitch={(control.MinPitch * RadiansToDegrees):0.##} maxPitch={(control.MaxPitch * RadiansToDegrees):0.##}"
        );
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" yaw={((state?.Yaw ?? 0f) * RadiansToDegrees):0.##} pitch={((state?.Pitch ?? 0f) * RadiansToDegrees):0.##} freeLook={(seat?.FreeLooking ?? false).ToString().ToLowerInvariant()} motionControls={(seat?.MotionControlsActive ?? false).ToString().ToLowerInvariant()} angularVelocity=({angularVelocity.X:0.####},{angularVelocity.Y:0.####},{angularVelocity.Z:0.####})"
        );

        if (seatBindings.IsCameraModeActive(slot: slot)) {
            _ = builder.Append(value: " flying=true");
        }

        return builder.Append(value: ']').ToString();
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.camera",
            description: $"Echoes a seat's live control policy, held free-look state, motion-input state, and camera orbit: world.view.camera [player], player 1..{PlayerRoster.MaxSlots}. The definition follows the seat's current authority route; profile look preferences remain seat-owned across handoff.",
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

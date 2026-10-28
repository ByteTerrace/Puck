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
    // The keys the default reading keeps: where the camera is and looks, how, and what the clearance did to the view.
    private static readonly string[] CompactKeys = ["tick", "view", "eye", "target", "yaw", "pitch", "fov", "mode", "fade", "clearance"];

    // The reading `world.view.camera [player] [full|compact|<key>,<key>...]` asks for: the compact set by default,
    // every field for `full`, or the named keys in the order given. A key the seat does not report is named in the
    // refusal rather than dropped.
    private CommandResult Read(CommandContext context, WireArgs args) {
        if (args.Count > 2) {
            return CommandResult.Error(output: $"[world.view.camera: too many arguments — expected [<player>] [full|compact|<key>,<key>...], player an integer 1..{PlayerRoster.MaxSlots}]");
        }

        // A lone token that is not a seat number is the shape, read for the acting seat.
        var shapeAt = ((args.Count == 2) ? 1 : (((args.Count == 1) && !char.IsAsciiDigit(c: args[0][0])) ? 0 : -1));
        var shape = ((shapeAt >= 0) ? args[shapeAt].ToString() : "compact");
        int slot;
        CommandResult? error;

        if (shapeAt == 0) {
            (slot, error) = (context.Slot, null);
        } else {
            (slot, error) = SeatCommandArgs.ResolveSlot(
                args: in args,
                at: 0,
                context: context,
                verb: "world.view.camera"
            );
        }

        if (error is { } refusal) {
            return refusal;
        }

        var fields = Fields(slot: slot);

        if (shape == "full") {
            return new CommandResult(Output: Frame(fields: fields));
        }

        var keys = ((shape == "compact") ? CompactKeys : shape.Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var chosen = new List<KeyValuePair<string, string>>(capacity: keys.Length);

        foreach (var key in keys) {
            var found = fields.FindIndex(match: field => (field.Key == key));

            if (found >= 0) {
                chosen.Add(item: fields[found]);
            } else if (shape != "compact") {
                return CommandResult.Error(output: $"[world.view.camera: no field '{key}' — `full` lists every field]");
            }
        }

        return new CommandResult(Output: Frame(fields: chosen));
    }
    private static string Frame(List<KeyValuePair<string, string>> fields) {
        var builder = new StringBuilder(value: "[world.view.camera:");

        foreach (var (key, value) in fields) {
            _ = builder.Append(value: ' ').Append(value: key).Append(value: '=').Append(value: value);
        }

        return builder.Append(value: ']').ToString();
    }
    private string Describe(int slot) => Frame(fields: Compact(fields: Fields(slot: slot)));
    private static List<KeyValuePair<string, string>> Compact(List<KeyValuePair<string, string>> fields) => [.. CompactKeys.SelectMany(selector: key => fields.Where(predicate: field => (field.Key == key)))];
    // Every field the seat reports, in reading order, as the key=value tokens its description is made of.
    private List<KeyValuePair<string, string>> Fields(int slot) {
        var fields = new List<KeyValuePair<string, string>>();

        foreach (var token in Text(slot: slot).Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries)) {
            var split = token.IndexOf(value: '=');

            fields.Add(item: new(key: token[..split], value: token[(split + 1)..]));
        }

        return fields;
    }
    private string Text(int slot) {
        var definition = instances.ResolveRoutedDefinition(slot: slot);
        var seat = roster.Seat(slot: slot);
        var seatLook = (seat?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
        var gyro = seatLook.Gyro;
        var control = definition.Views.SeatControl;
        var state = seat?.View;
        var choice = (state?.ClearanceChoice(control: control, feel: seatLook) ?? ((seatLook.ClearanceMode == WorldCameraClearanceChoice.Auto) ? control.ClearanceMode : seatLook.ClearanceMode));
        var angularVelocity = (seat?.MotionAngularVelocity ?? Vector3.Zero);
        var route = seatRouter.Route(slot: slot);
        var builder = new StringBuilder();
        var resolved = continuum.TryResolvePresentedSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var anchor,
            slot: slot
        );

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"tick={continuum.Tick} fov={float.RadiansToDegrees(radians: (state?.FieldOfView ?? 0f)):0.##} player={PlayerRoster.DisplayNumber(slot: slot)} authority={route.Endpoint.Identity} entity={route.Entity.Authority}/{route.Entity.Index}#{route.Entity.Generation} epoch={route.Epoch} resolved={resolved.ToString().ToLowerInvariant()}"
        );
        if (resolved) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" anchor=({anchor.X:0.###},{anchor.Y:0.###},{anchor.Z:0.###})"
            );
        }
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" yawReference={control.YawReference.ToString().ToLowerInvariant()} yawSensitivity={seatLook.YawSensitivity:0.#####} pitchSensitivity={seatLook.PitchSensitivity:0.#####} stickLookRate={seatLook.StickLookRate:0.#####} gyroScale={gyro.Scale:0.#####} gyroDeadZone=({gyro.DeadZone.X:0.#####},{gyro.DeadZone.Y:0.#####},{gyro.DeadZone.Z:0.#####}) gyroInvert=({gyro.InvertX.ToString().ToLowerInvariant()},{gyro.InvertY.ToString().ToLowerInvariant()},{gyro.InvertZ.ToString().ToLowerInvariant()}) gyroYaw=({gyro.Yaw.X:0.#####},{gyro.Yaw.Y:0.#####},{gyro.Yaw.Z:0.#####}) gyroPitch=({gyro.Pitch.X:0.#####},{gyro.Pitch.Y:0.#####},{gyro.Pitch.Z:0.#####}) invertYaw={seatLook.InvertYaw.ToString().ToLowerInvariant()} invertPitch={seatLook.InvertPitch.ToString().ToLowerInvariant()} minPitch={float.RadiansToDegrees(radians: control.MinPitch):0.##} maxPitch={float.RadiansToDegrees(radians: control.MaxPitch):0.##}"
        );
        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $" yaw={float.RadiansToDegrees(radians: (state?.Yaw ?? 0f)):0.##} pitch={float.RadiansToDegrees(radians: (state?.Pitch ?? 0f)):0.##} freeLook={(seat?.FreeLooking ?? false).ToString().ToLowerInvariant()} orbit={(seat?.Orbiting ?? false).ToString().ToLowerInvariant()} steer={(seat?.PointerSteering ?? false).ToString().ToLowerInvariant()} motionControls={(seat?.MotionControlsActive ?? false).ToString().ToLowerInvariant()} angularVelocity=({angularVelocity.X:0.####},{angularVelocity.Y:0.####},{angularVelocity.Z:0.####})"
        );

        if (state is not null) {
            _ = builder.Append(value: $" mode={choice.ToString().ToLowerInvariant()}");
            _ = builder.Append(value: $" autoFirstPerson={state.AutomaticFirstPerson(control, seatLook).ToString().ToLowerInvariant()}");
            _ = builder.Append(value: ' ').Append(value: state.DescribeClearance());
        }

        if (seatBindings.IsCameraModeActive(slot: slot)) {
            _ = builder.Append(value: " cameraApplication=true");
        }

        return builder.ToString();
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.camera.view",
            description: "Eases this seat into its authored eye or back out: player.camera.view [first|third|toggle|auto]. No argument toggles. Auto restores the camera program's state-driven view and optional automatic pull-in.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[player.camera.view: expected first|third|toggle|auto]");
                }
                var seat = roster.Seat(slot: context.Slot);
                if (seat is null) {
                    return CommandResult.Error(output: "[player.camera.view: no occupied seat]");
                }
                var token = ((args.Count == 0) ? "toggle" : args[0].ToString().ToLowerInvariant());
                switch (token) {
                    case "first": seat.View.SetView(true); break;
                    case "third": seat.View.SetView(false); break;
                    case "toggle": seat.View.SetView(!seat.View.FirstPerson); break;
                    case "auto": seat.View.SetView(null); break;
                    default: return CommandResult.Error(output: "[player.camera.view: expected first|third|toggle|auto]");
                }
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.camera.automatic",
            description: "Chooses automatic first-person pull-in: player.camera.automatic [on|off|auto|toggle]. Auto restores the portable preference or author's default. Explicit first/third view choices take priority; player.camera.view auto restores state-driven behavior.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[player.camera.automatic: expected on|off|auto|toggle]");
                }
                var seat = roster.Seat(slot: context.Slot);
                if (seat is null) {
                    return CommandResult.Error(output: "[player.camera.automatic: no occupied seat]");
                }
                var definition = instances.ResolveRoutedDefinition(slot: context.Slot);
                var feel = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
                var token = ((args.Count == 0) ? "toggle" : args[0].ToString().ToLowerInvariant());
                switch (token) {
                    case "on": seat.View.SetAutomaticFirstPerson(true); break;
                    case "off": seat.View.SetAutomaticFirstPerson(false); break;
                    case "auto": seat.View.SetAutomaticFirstPerson(null); break;
                    case "toggle": seat.View.SetAutomaticFirstPerson(!seat.View.AutomaticFirstPerson(definition.Views.SeatControl, feel)); break;
                    default: return CommandResult.Error(output: "[player.camera.automatic: expected on|off|auto|toggle]");
                }
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "player.camera.clearance",
            description: "Chooses this seat's tight-space camera: player.camera.clearance [fade|cutaway|auto|toggle]. No argument toggles fade/cutaway. Auto restores the portable preference or world's default. Heading is preserved; world.view.camera echoes mode and fade.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[player.camera.clearance: expected fade|cutaway|auto|toggle]");
                }
                var token = ((args.Count == 0) ? "toggle" : args[0].ToString());
                var seat = roster.Seat(slot: context.Slot);

                if (seat is null) {
                    return CommandResult.Error(output: "[player.camera.clearance: no occupied seat]");
                }
                var definition = instances.ResolveRoutedDefinition(slot: context.Slot);
                var feel = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
                WorldCameraClearanceChoice choice;

                if (string.Equals(a: token, b: "toggle", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    choice = ((seat.View.ClearanceChoice(control: definition.Views.SeatControl, feel: feel) == WorldCameraClearanceChoice.Fade)
                        ? WorldCameraClearanceChoice.Cutaway : WorldCameraClearanceChoice.Fade);
                } else if (!Enum.TryParse(token, ignoreCase: true, out choice) || !Enum.IsDefined(value: choice)) {
                    return CommandResult.Error(output: "[player.camera.clearance: expected fade|cutaway|auto|toggle]");
                }
                seat.View.SetClearanceChoice(choice: choice);
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.view.camera",
            description: $"Echoes a seat's camera: world.view.camera [player] [full|<key>,<key>...], player 1..{PlayerRoster.MaxSlots}; the default reading is tick, view, eye, target, yaw, pitch, fov, mode, fade and clearance. `full` reads every field (control policy, held free-look state, motion-input state, camera orbit and the last presented eye), and a comma list reads just those keys in the order given. tick= is the latest simulation tick the client received, view= first or third, eye= and target= are world metres, yaw= and pitch= degrees, fov= the vertical field of view in degrees of the last framed seat camera (0 before any frame, as in a headless World). full also reads viewSource= player, program or clearance, transition= zero (third) to one (first), autoFirstPerson= the effective preference, room= independent head clearance in full-size metres, dwell= consecutive entry/exit seconds, boom= the placed boom over the authored one, rise= the degrees the boom rose over the body, near= the metres of geometry the near plane cuts away between the eye and the body, mode= fade or cutaway, fade= zero (solid) to one (hidden), clearance= none, open, shortened, risen or crowded. The definition follows the seat's current authority route; profile look preferences remain seat-owned across handoff.",
            handler: Read,
            routing: CommandRouting.Immediate
        );
    }
}

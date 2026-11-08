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
    private static readonly CommandForms WorldViewCameraForms = CommandForms.Of(verb: "world.view.camera", "[<player>] [full|compact|<key>,<key>...]");
    private static readonly CommandForms ViewRecenterForms = CommandForms.Of(verb: "view.recenter", "[player]");
    private static readonly CommandForms PlayerCameraViewForms = CommandForms.Of(verb: "player.camera.view", "[first|third|toggle|auto]");
    private static readonly CommandForms PlayerCameraAutomaticForms = CommandForms.Of(verb: "player.camera.automatic", "[on|off|auto|toggle]");
    // The keys the default reading keeps: where the camera is and looks, how, and what the clearance did to the view.
    private static readonly string[] CompactKeys = ["tick", "view", "eye", "target", "yaw", "pitch", "fov", "fade", "clearance", "cutaway"];

    // The reading `world.view.camera [player] [full|compact|<key>,<key>...]` asks for: the compact set by default,
    // every field for `full`, or the named keys in the order given. A key the seat does not report is named in the
    // refusal rather than dropped.
    private CommandResult Read(CommandContext context, WireArgs args) {
        if (args.Count > 2) {
            return WorldViewCameraForms.Refuse(got: args[2].ToString());
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

        var keys = ((shape == "compact") ? CompactKeys : shape.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: ','));
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
            // A value holding a space or a quote is quoted, so the line reads back as the pairs it was made from.
            var quoted = ((value.Length == 0) || (value.AsSpan().IndexOfAny(value0: ' ', value1: '"') >= 0));

            _ = builder.Append(value: ' ').Append(value: key).Append(value: '=').Append(value: (quoted ? $"\"{value.Replace(newValue: "\\\"", oldValue: "\"")}\"" : value));
        }

        return builder.Append(value: ']').ToString();
    }
    // Resets the seat's live orbit to behind the body it is framed on, whose heading the routed authority holds.
    private void Recenter(SeatController seat, int slot) {
        var route = seatRouter.Route(slot: slot);

        _ = route.Endpoint.TryEntityHeading(heading: out var heading, index: route.EntityIndex);
        seat.View.Recenter(
            heading: heading,
            views: instances.ResolveRoutedDefinition(slot: slot).Views
        );
    }
    private string Describe(int slot) => Frame(fields: Compact(fields: Fields(slot: slot)));
    private static List<KeyValuePair<string, string>> Compact(List<KeyValuePair<string, string>> fields) => [.. CompactKeys.SelectMany(selector: key => fields.Where(predicate: field => (field.Key == key)))];
    // Every field the seat reports, in reading order, as structured pairs: a value that holds a space (an authority
    // named `side room`) stays one value.
    private List<KeyValuePair<string, string>> Fields(int slot) {
        var definition = instances.ResolveRoutedDefinition(slot: slot);
        var seat = roster.Seat(slot: slot);
        var seatLook = (seat?.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
        var gyro = seatLook.Gyro;
        var control = definition.Views.SeatControl;
        var state = seat?.View;
        var angularVelocity = (seat?.MotionAngularVelocity ?? Vector3.Zero);
        var route = seatRouter.Route(slot: slot);
        var fields = new List<KeyValuePair<string, string>>();
        var resolved = continuum.TryResolvePresentedSeatPose(
            interpolationAlpha: 1f,
            orientation: out _,
            position: out var anchor,
            slot: slot
        );
        var invariant = CultureInfo.InvariantCulture;

        void Add(string key, FormattableString value) => fields.Add(item: new(key: key, value: value.ToString(formatProvider: invariant)));
        static string Flag(bool value) => (value ? "true" : "false");

        Add(key: "tick", value: $"{continuum.Tick}");
        Add(key: "fov", value: $"{float.RadiansToDegrees(radians: (state?.FieldOfView ?? 0f)):0.##}");
        Add(key: "player", value: $"{PlayerRoster.DisplayNumber(slot: slot)}");
        Add(key: "authority", value: $"{route.Endpoint.Identity}");
        Add(key: "entity", value: $"{route.Entity.Authority}/{route.Entity.Index}#{route.Entity.Generation}");
        Add(key: "epoch", value: $"{route.Epoch}");
        Add(key: "resolved", value: $"{Flag(value: resolved)}");
        if (resolved) {
            Add(key: "anchor", value: $"({anchor.X:0.###},{anchor.Y:0.###},{anchor.Z:0.###})");
        }
        Add(key: "yawReference", value: $"{control.YawReference.ToString().ToLowerInvariant()}");
        Add(key: "yawSensitivity", value: $"{seatLook.YawSensitivity:0.#####}");
        Add(key: "pitchSensitivity", value: $"{seatLook.PitchSensitivity:0.#####}");
        Add(key: "stickLookRate", value: $"{seatLook.StickLookRate:0.#####}");
        Add(key: "gyroScale", value: $"{gyro.Scale:0.#####}");
        Add(key: "gyroDeadZone", value: $"({gyro.DeadZone.X:0.#####},{gyro.DeadZone.Y:0.#####},{gyro.DeadZone.Z:0.#####})");
        Add(key: "gyroInvert", value: $"({Flag(value: gyro.InvertX)},{Flag(value: gyro.InvertY)},{Flag(value: gyro.InvertZ)})");
        Add(key: "gyroYaw", value: $"({gyro.Yaw.X:0.#####},{gyro.Yaw.Y:0.#####},{gyro.Yaw.Z:0.#####})");
        Add(key: "gyroPitch", value: $"({gyro.Pitch.X:0.#####},{gyro.Pitch.Y:0.#####},{gyro.Pitch.Z:0.#####})");
        Add(key: "invertYaw", value: $"{Flag(value: seatLook.InvertYaw)}");
        Add(key: "invertPitch", value: $"{Flag(value: seatLook.InvertPitch)}");
        Add(key: "minPitch", value: $"{float.RadiansToDegrees(radians: control.MinPitch):0.##}");
        Add(key: "maxPitch", value: $"{float.RadiansToDegrees(radians: control.MaxPitch):0.##}");
        Add(key: "yaw", value: $"{float.RadiansToDegrees(radians: (state?.Yaw ?? 0f)):0.##}");
        Add(key: "pitch", value: $"{float.RadiansToDegrees(radians: (state?.Pitch ?? 0f)):0.##}");
        Add(key: "boomDistance", value: $"{(state?.BoomDistance(views: definition.Views) ?? 0f):0.###}");
        if (control.Boom is { } boom) {
            Add(key: "boomMinimum", value: $"{boom.Minimum:0.###}");
            Add(key: "boomMaximum", value: $"{boom.Maximum:0.###}");
            Add(key: "boomStep", value: $"{boom.Step:0.###}");
        }
        Add(key: "freeLook", value: $"{Flag(value: (seat?.FreeLooking ?? false))}");
        Add(key: "orbit", value: $"{Flag(value: (seat?.Orbiting ?? false))}");
        Add(key: "steer", value: $"{Flag(value: (seat?.PointerSteering ?? false))}");
        Add(key: "motionControls", value: $"{Flag(value: (seat?.MotionControlsActive ?? false))}");
        Add(key: "angularVelocity", value: $"({angularVelocity.X:0.####},{angularVelocity.Y:0.####},{angularVelocity.Z:0.####})");

        if (state is not null) {
            Add(key: "autoFirstPerson", value: $"{Flag(value: state.AutomaticFirstPerson(control: control, feel: seatLook))}");

            // The client's clearance reading is a fixed run of key=value tokens that hold no free text.
            foreach (var token in state.DescribeClearance().Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ' ')) {
                var split = token.IndexOf(value: '=');

                fields.Add(item: new(key: token[..split], value: token[(split + 1)..]));
            }
        }

        if (seatBindings.IsCameraModeActive(slot: slot)) {
            Add(key: "cameraApplication", value: $"true");
        }

        return fields;
    }

    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "player.camera.boom",
            description: "Moves the seat's orbit distance from an Axis2D source's vertical input, within views.seatControl.boom. Which pages bind it, and which bind their own scroll act over it, is authored. The existing boom dynamics ease the eye.",
            valueKind: CommandValueKind.Axis2D,
            handler: (context, args) => {
                if (context.Origin != CommandOrigin.Binding) {
                    return CommandResult.Error(output: "[player.camera.boom: requires an Axis2D binding]");
                }
                var seat = roster.Seat(slot: context.Slot);

                if (seat is null) { return CommandResult.Error(output: "[player.camera.boom: no occupied seat]"); }
                seat.View.MoveBoom(units: context.Value.AsAxis2D.Y, views: instances.ResolveRoutedDefinition(slot: context.Slot).Views);
                return CommandResult.None;
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "player.camera.reset",
            description: "Resets this seat's live orbit angles and distance to its authored rig defaults, the camera behind the body it frames.",
            handler: (context, args) => {
                if (context.Phase is CommandPhase.Completed or CommandPhase.Canceled) { return CommandResult.None; }
                var seat = roster.Seat(slot: context.Slot);

                if (seat is null) { return CommandResult.Error(output: "[player.camera.reset: no occupied seat]"); }
                Recenter(seat: seat, slot: context.Slot);
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "view.recenter",
            description: $"Resets a seat's live camera orbit and boom to its authored frame, the camera behind the body it frames: {ViewRecenterForms.Spelling}, player 1..4 (default the acting seat). Answers with the camera state, including without physical devices. Session presentation only.",
            forms: ViewRecenterForms,
            handler: (context, args) => {
                if (args.Count > 1) {
                    return ViewRecenterForms.Refuse();
                }
                var (slot, error) = SeatCommandArgs.ResolveSlot(args: in args, at: 0, context: context, verb: "view.recenter");
                if (error is { } refusal) { return refusal; }
                if (roster.Seat(slot: slot) is not { } seat) {
                    return CommandResult.Error(output: "[view.recenter: no occupied seat]");
                }
                Recenter(seat: seat, slot: slot);
                return new CommandResult(Output: Describe(slot: slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "player.camera.view",
            description: $"Eases this seat into its authored eye or back out: {PlayerCameraViewForms.Spelling}. No argument toggles. Auto restores the camera program's state-driven view and optional automatic pull-in.",
            forms: PlayerCameraViewForms,
            handler: (context, args) => {
                if (args.Count > 1) {
                    return PlayerCameraViewForms.Refuse();
                }
                var seat = roster.Seat(slot: context.Slot);

                if (seat is null) {
                    return CommandResult.Error(output: "[player.camera.view: no occupied seat]");
                }
                var token = ((args.Count == 0) ? "toggle" : args[0].ToString().ToLowerInvariant());

                switch (token) {
                    case "first": seat.View.SetView(first: true); break;
                    case "third": seat.View.SetView(first: false); break;
                    case "toggle": seat.View.SetView(first: !seat.View.FirstPerson); break;
                    case "auto": seat.View.SetView(first: null); break;
                    default: return PlayerCameraViewForms.Refuse();
                }
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Bindable,
            name: "player.camera.automatic",
            description: $"Chooses automatic first-person pull-in: {PlayerCameraAutomaticForms.Spelling}. Auto restores the portable preference or author's default. Explicit first/third view choices take priority; player.camera.view auto restores state-driven behavior.",
            forms: PlayerCameraAutomaticForms,
            handler: (context, args) => {
                if (args.Count > 1) {
                    return PlayerCameraAutomaticForms.Refuse();
                }
                var seat = roster.Seat(slot: context.Slot);

                if (seat is null) {
                    return CommandResult.Error(output: "[player.camera.automatic: no occupied seat]");
                }
                var definition = instances.ResolveRoutedDefinition(slot: context.Slot);
                var feel = (seat.Profile?.SeatLook ?? definition.PlayerDefaults.SeatLook);
                var token = ((args.Count == 0) ? "toggle" : args[0].ToString().ToLowerInvariant());

                switch (token) {
                    case "on": seat.View.SetAutomaticFirstPerson(enabled: true); break;
                    case "off": seat.View.SetAutomaticFirstPerson(enabled: false); break;
                    case "auto": seat.View.SetAutomaticFirstPerson(enabled: null); break;
                    case "toggle": seat.View.SetAutomaticFirstPerson(enabled: !seat.View.AutomaticFirstPerson(control: definition.Views.SeatControl, feel: feel)); break;
                    default: return PlayerCameraAutomaticForms.Refuse();
                }
                return new CommandResult(Output: Describe(slot: context.Slot));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.view.camera",
            description: $"Echoes a seat's camera: {WorldViewCameraForms.Spelling}, player 1..{PlayerRoster.MaxSlots}; the default reading is tick, view, eye, target, yaw, pitch, fov, fade, clearance and cutaway. `full` reads every field (control policy, held free-look state, motion-input state, camera orbit and the last presented eye), and a comma list reads just those keys in the order given; a value holding a space is quoted. tick= is the latest simulation tick the client received, view= first or third, eye= and target= are world metres, yaw= and pitch= degrees, fov= the vertical field of view in degrees of the last framed seat camera (0 before any frame, as in a headless World). full also reads viewSource= player, program or clearance, transition= zero (third) to one (first), autoFirstPerson= the effective preference, room= independent head clearance in full-size metres, dwell= consecutive entry/exit seconds, boom= the placed boom over the authored one, rise= the degrees the boom rose over the body, near= the metres of geometry the near plane cuts away between the eye and the body, fade= zero (solid) to one (hidden), clearance= none (the camera program authors no clearance op), open, shortened, risen or crowded, then the clearance op's parameters as the frame resolved them, full-size: cutaway= true or false, eyeRadius=, surfaceSkin=, maxRise= degrees, minimumBoom=, fadeStart=, fadeEnd=, closeTarget=, closeTargetStart=, closeTargetFull=, nearMargin=, retractionSkin= and obstructedTargetSkin=. The definition follows the seat's current authority route; profile look preferences remain seat-owned across handoff.",
            forms: WorldViewCameraForms,
            handler: Read,
            routing: CommandRouting.Immediate
        );
    }
}

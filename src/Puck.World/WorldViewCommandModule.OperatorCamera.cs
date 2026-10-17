using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World;

internal sealed partial class WorldViewCommandModule {
    private const string OperatorCameraForms = "view.override camera pose <x> <y> <z> <yaw> <pitch> [<fov>] | look <x> <y> <z> <targetX> <targetY> <targetZ> [<fov>] | orbit <targetX> <targetY> <targetZ> <distance> <yaw> <pitch> [<fov>] | turn <dYaw> <dPitch> | swing <dYaw> <dPitch> [<dDistance>] | move <dRight> <dUp> <dForward> | fov <degrees> — angles in degrees, distances in metres";

    // Reads `count` finite numbers from `args` starting at `first`; false names nothing, the caller refuses with the form.
    private static bool TryNumbers(in WireArgs args, int first, int count, out float[] numbers) {
        numbers = new float[count];

        for (var index = 0; (index < count); index++) {
            if (
                !float.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    result: out numbers[index],
                    s: args[(first + index)].ToString(),
                    style: NumberStyles.Float
                ) ||
                !float.IsFinite(f: numbers[index])
            ) {
                return false;
            }
        }

        return true;
    }
    private static CommandResult OperatorCameraRefusal(string reason) => CommandResult.Error(output: $"[view.override: camera {reason}; forms: {OperatorCameraForms}]");
    private static bool ValidFieldOfView(float degrees) => ((degrees >= 1f) && (degrees <= 170f));
    // A form's trailing optional field of view, in degrees: the default when absent, a refusal's reason when out of range.
    private static float FieldOfViewOr(float[] numbers, int index, out string? refusal) {
        refusal = null;

        if (index >= numbers.Length) {
            return WorldOperatorCamera.DefaultFieldOfViewRadians;
        }
        if (!ValidFieldOfView(degrees: numbers[index])) {
            refusal = $"field of view {numbers[index].ToString(provider: CultureInfo.InvariantCulture)} is outside 1..170 degrees";
        }

        return (numbers[index] * (MathF.PI / 180f));
    }
    // The operator camera forms of `view.override camera`: an absolute pose, a look-at, an orbit about a point, and deltas
    // against the pose already set. Every one submits the resulting ABSOLUTE pose and answers with it.
    private CommandResult OperatorCamera(CommandContext context, in WireArgs args) {
        var form = args[1].ToString().ToLowerInvariant();
        var extra = (args.Count - 2);
        (int Min, int Max)? arity = form switch {
            "pose" => (5, 6),
            "look" => (6, 7),
            "orbit" => (6, 7),
            "turn" => (2, 2),
            "swing" => (2, 3),
            "move" => (3, 3),
            "fov" => (1, 1),
            _ => null,
        };

        if (arity is not { } range) {
            return OperatorCameraRefusal(reason: $"has no form '{args[1]}'");
        }
        if ((extra < range.Min) || (extra > range.Max)) {
            return OperatorCameraRefusal(reason: $"{form} takes {((range.Min == range.Max) ? range.Min.ToString(provider: CultureInfo.InvariantCulture) : $"{range.Min} or {range.Max}")} numbers, got {extra}");
        }
        if (!TryNumbers(
            args: args,
            count: extra,
            first: 2,
            numbers: out var n
        )) {
            return OperatorCameraRefusal(reason: $"{form} takes finite numbers");
        }

        WorldOperatorCamera? pose;
        string? fovRefusal = null;

        switch (form) {
            case "pose":
                var poseFov = FieldOfViewOr(index: 5, numbers: n, refusal: out fovRefusal);

                pose = WorldOperatorCamera.FromPose(
                    eye: new Vector3(x: n[0], y: n[1], z: n[2]),
                    fieldOfViewRadians: poseFov,
                    pitchDegrees: n[4],
                    yawDegrees: n[3]
                );

                break;
            case "look":
                var lookFov = FieldOfViewOr(index: 6, numbers: n, refusal: out fovRefusal);

                pose = WorldOperatorCamera.FromLookAt(
                    eye: new Vector3(x: n[0], y: n[1], z: n[2]),
                    fieldOfViewRadians: lookFov,
                    target: new Vector3(x: n[3], y: n[4], z: n[5])
                );
                if (pose is null) {
                    return OperatorCameraRefusal(reason: $"look needs an eye at least {WorldOperatorCamera.MinDistance.ToString(provider: CultureInfo.InvariantCulture)} m from its target");
                }

                break;
            case "orbit":
                var orbitFov = FieldOfViewOr(index: 6, numbers: n, refusal: out fovRefusal);

                if (n[3] < WorldOperatorCamera.MinDistance) {
                    return OperatorCameraRefusal(reason: $"orbit needs a distance of at least {WorldOperatorCamera.MinDistance.ToString(provider: CultureInfo.InvariantCulture)} m");
                }

                pose = WorldOperatorCamera.FromOrbit(
                    distance: n[3],
                    fieldOfViewRadians: orbitFov,
                    pitchDegrees: n[5],
                    target: new Vector3(x: n[0], y: n[1], z: n[2]),
                    yawDegrees: n[4]
                );

                break;
            default:
                if (composition.OperatorCamera is not { } current) {
                    return OperatorCameraRefusal(reason: $"{form} moves the operator camera set by pose, look or orbit, and none is set");
                }
                if ((form == "fov") && !ValidFieldOfView(degrees: n[0])) {
                    fovRefusal = $"field of view {n[0].ToString(provider: CultureInfo.InvariantCulture)} is outside 1..170 degrees";
                }

                pose = form switch {
                    "turn" => current.Turn(pitchDegrees: n[1], yawDegrees: n[0]),
                    "swing" => current.Swing(distance: ((extra == 3) ? n[2] : 0f), pitchDegrees: n[1], yawDegrees: n[0]),
                    "move" => current.Move(forward: n[2], right: n[0], up: n[1]),
                    _ => current.WithFieldOfView(fieldOfViewRadians: (n[0] * (MathF.PI / 180f))),
                };

                break;
        }

        return ((fovRefusal is not null)
            ? OperatorCameraRefusal(reason: fovRefusal)
            : Submit(
                composition: new WorldComposition.SetOperatorCamera(Pose: pose),
                context: context
            ));
    }
}

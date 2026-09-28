namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The editor section: every pitch, width, rate and radius positive, the angle step a real turn, the pitch clamp
    // inside the poles and ordered, and the distance range positive and ordered. An absent member takes its defaults,
    // which hold every rule.
    private static void ValidateEditor(WorldEditorDefaults editor, List<string> errors) {
        if (editor.Grid is { } grid) {
            if (grid.Pitch is { } pitch) {
                RequirePositive(errors: errors, name: "editor.grid.pitch[0]", value: pitch.X);
                RequirePositive(errors: errors, name: "editor.grid.pitch[1]", value: pitch.Y);
                RequirePositive(errors: errors, name: "editor.grid.pitch[2]", value: pitch.Z);
            }

            RequireFinite(errors: errors, name: "editor.grid.planeY", value: grid.PlaneY);
            RequirePositive(errors: errors, name: "editor.grid.lineWidth", value: grid.LineWidth);
        }

        if (editor.Snap is { } snap) {
            RequireRange(errors: errors, max: 180f, min: 0f, minExclusive: true, name: "editor.snap.angleStepDegrees", value: snap.AngleStepDegrees);
            RequirePositive(errors: errors, name: "editor.snap.objectPatchRadius", value: snap.ObjectPatchRadius);
        }

        if (editor.Camera is { } camera) {
            const float Pole = (MathF.PI / 2f);

            RequirePositive(errors: errors, name: "editor.camera.orbitRate", value: camera.OrbitRate);
            RequirePositive(errors: errors, name: "editor.camera.panRate", value: camera.PanRate);
            RequirePositive(errors: errors, name: "editor.camera.zoomRate", value: camera.ZoomRate);
            RequirePositive(errors: errors, name: "editor.camera.flyRate", value: camera.FlyRate);
            RequireRange(errors: errors, max: Pole, maxExclusive: true, min: -Pole, minExclusive: true, name: "editor.camera.minPitch", value: camera.MinPitch);
            RequireRange(errors: errors, max: Pole, maxExclusive: true, min: -Pole, minExclusive: true, name: "editor.camera.maxPitch", value: camera.MaxPitch);
            RequirePositive(errors: errors, name: "editor.camera.minDistance", value: camera.MinDistance);
            RequirePositive(errors: errors, name: "editor.camera.maxDistance", value: camera.MaxDistance);

            if (camera.MinPitch >= camera.MaxPitch) {
                errors.Add(item: $"editor.camera.minPitch {camera.MinPitch} must be below editor.camera.maxPitch {camera.MaxPitch}.");
            }

            if (camera.MinDistance >= camera.MaxDistance) {
                errors.Add(item: $"editor.camera.minDistance {camera.MinDistance} must be below editor.camera.maxDistance {camera.MaxDistance}.");
            }
        }
    }
}

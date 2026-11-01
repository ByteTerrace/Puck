namespace Puck.World;

public static partial class WorldDefinitionValidator {
    // The editor section: every pitch, width, rate and radius positive, the angle step a real turn, the pitch clamp
    // inside the poles and ordered, the distance range positive and ordered, and the toggle bound to a source or an
    // activator. A member present as JSON null refuses by name: the serializer admits an explicit null for a required
    // member. The toggle's binding and the bar are admitted with the binding overlays they compose with
    // (ValidateBindingOverlays).
    private static void ValidateEditor(WorldEditorDefaults? editor, List<string> errors) {
        if (editor is null) {
            return;
        }

        if (editor.Grid is not { } grid) {
            errors.Add(item: "editor.grid is required.");
        } else if (grid.Pitch is not { } gridPitch) {
            errors.Add(item: "editor.grid.pitch is required.");
        } else {
            var pitch = gridPitch.Value;

            RequirePositive(errors: errors, name: "editor.grid.pitch[0]", value: pitch.X);
            RequirePositive(errors: errors, name: "editor.grid.pitch[1]", value: pitch.Y);
            RequirePositive(errors: errors, name: "editor.grid.pitch[2]", value: pitch.Z);
            RequireFinite(errors: errors, name: "editor.grid.planeY", value: grid.PlaneY);
            RequirePositive(errors: errors, name: "editor.grid.lineWidth", value: grid.LineWidth);
        }

        if (editor.Snap is not { } snap) {
            errors.Add(item: "editor.snap is required.");
        } else {
            RequireRange(errors: errors, max: 180f, min: 0f, minExclusive: true, name: "editor.snap.angleStepDegrees", value: snap.AngleStepDegrees);
            RequirePositive(errors: errors, name: "editor.snap.objectPatchRadius", value: snap.ObjectPatchRadius);
        }

        if (editor.Bar is null) {
            errors.Add(item: "editor.bar is required.");
        }

        if (editor.ReadOnlyLabel is null) {
            errors.Add(item: "editor.readOnlyLabel is required.");
        }

        if (editor.Toggle is not { } toggle) {
            errors.Add(item: "editor.toggle is required.");
        } else {
            if (toggle.Sources is not { Count: > 0 } && (toggle.Activator is null)) {
                errors.Add(item: "editor.toggle must bind at least one source or an activator.");
            }

            // A page that rebinds the toggle names it by this id, so the entry needs one.
            if (string.IsNullOrEmpty(value: toggle.Id)) {
                errors.Add(item: "editor.toggle must carry an id.");
            }
        }

        if (editor.Camera is not { } camera) {
            errors.Add(item: "editor.camera is required.");

            return;
        }

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

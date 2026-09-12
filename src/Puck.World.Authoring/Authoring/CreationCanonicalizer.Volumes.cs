using System.Numerics;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

public static partial class CreationCanonicalizer {
    // A creation's bounded volumes (VolumeDocument): a known kind, a parent that names a declared shape, a positive
    // finite box, in-range integration steps, hex colours, and non-negative finite scalars — refused here by name so
    // a shipped creation never reaches SdfWorldEngine.PackVolumes with a value the shader would clamp silently.
    private static void ValidateVolumes(CreationDocument document, ShapeLookup lookup, List<DocumentValidationError> errors) {
        if (document.Volumes is not { Count: > 0 } volumes) {
            return;
        }

        if (volumes.Count > SdfProgramBuilder.MaxVolumes) {
            errors.Add(item: new(
                Message: $"volumes declares {volumes.Count} entries; at most {SdfProgramBuilder.MaxVolumes} are admitted.",
                Path: "volumes"
            ));
        }

        for (var index = 0; (index < volumes.Count); index++) {
            var volume = volumes[index];
            var path = $"volumes[{index}]";

            if (volume is null) {
                errors.Add(item: new(
                    Message: "volume entry is null.",
                    Path: path
                ));

                continue;
            }

            if (!volume.IsKnownKind) {
                errors.Add(item: new(
                    Message: $"kind '{volume.Kind}' is not a volume kind; expected '{VolumeDocument.FlowKind}' or '{VolumeDocument.CloudKind}'.",
                    Path: $"{path}.kind"
                ));
            }

            if (
                (volume.Parent is { } parent) &&
                (lookup.Find(name: parent) < 0)
            ) {
                errors.Add(item: new(
                    Message: $"parent '{parent}' names no shape in this creation.",
                    Path: $"{path}.parent"
                ));
            }

            if (!IsFinite(value: volume.Position.Value)) {
                errors.Add(item: new(
                    Message: "position must contain finite coordinates.",
                    Path: $"{path}.position"
                ));
            }

            var rotation = volume.Rotation.Value;

            if (
                !float.IsFinite(f: rotation.X) ||
                !float.IsFinite(f: rotation.Y) ||
                !float.IsFinite(f: rotation.Z) ||
                !float.IsFinite(f: rotation.W) || !float.IsFinite(rotation.LengthSquared()) || rotation.LengthSquared() < 1e-12f
            ) {
                errors.Add(item: new(
                    Message: "rotation must contain finite components.",
                    Path: $"{path}.rotation"
                ));
            }

            var halfExtent = volume.HalfExtent.Value;

            if (
                !IsFinite(value: halfExtent) ||
                (halfExtent.X <= 0f) ||
                (halfExtent.Y <= 0f) ||
                (halfExtent.Z <= 0f)
            ) {
                errors.Add(item: new(
                    Message: "halfExtent must contain finite positive lengths on every axis.",
                    Path: $"{path}.halfExtent"
                ));
            }

            ValidatePositive(value: volume.Axis, name: "axis", errors: errors, path: path);
            ValidatePositive(value: volume.Width, name: "width", errors: errors, path: path);
            ValidateUnitRange(volume.Coverage, "coverage", errors, path + ".coverage");
            ValidateUnitRange(volume.Softness, "softness", errors, path + ".softness");
            if (volume.Softness is 0f) {
                errors.Add(new(Path: path + ".softness", Message: "softness must be positive."));
            }
            if (volume.Kind != VolumeDocument.CloudKind && (volume.Coverage is not null || volume.Softness is not null)) {
                errors.Add(new(Path: path + (volume.Coverage is not null ? ".coverage" : ".softness"), Message: "coverage and softness are cloud-only controls."));
            }
            if (volume.Kind == VolumeDocument.CloudKind && volume.Axis is not null) {
                errors.Add(new(Path: path + ".axis", Message: "axis is a flow-only control; a cloud uses halfExtent."));
            }
            if (volume.Speed is { } speed && !float.IsFinite(speed)) {
                errors.Add(new(Path: path + ".speed", Message: "speed must be finite."));
            }
            ValidateNonNegative(value: volume.Intensity, name: "intensity", errors: errors, path: path);
            ValidateNonNegative(value: volume.Extinction, name: "extinction", errors: errors, path: path);

            if (
                (volume.Steps is { } steps) &&
                ((steps < SdfVolume.MinSteps) || (steps > SdfVolume.MaxSteps))
            ) {
                errors.Add(item: new(
                    Message: $"steps must be within {SdfVolume.MinSteps}..{SdfVolume.MaxSteps}.",
                    Path: $"{path}.steps"
                ));
            }

            if (volume.Ramp is not { Count: >= 1 and <= 4 }) {
                errors.Add(new(Path: path + ".ramp", Message: "A volume ramp requires one to four density stops."));
            } else {
                var previous = -1f;
                foreach (var stop in volume.Ramp) {
                    if (stop is null || !float.IsFinite(stop.Density) || stop.Density <= previous || stop.Density > 1f || stop.Density < 0f) {
                        errors.Add(new(Path: path + ".ramp", Message: "Density stops must increase in [0, 1]."));
                        continue;
                    }
                    ValidateHexColor(stop.Color, "ramp.color", errors, path);
                    previous = stop.Density;
                }
            }
            ValidateUnitRange(volume.PulseAmplitude, "pulseAmplitude", errors, path + ".pulseAmplitude");
            ValidateNonNegative(volume.PulseFrequency, "pulseFrequency", errors, path);
            if (volume.IntensityLane is { } lane && (uint)lane > 3u) {
                errors.Add(new(Path: path + ".intensityLane", Message: "Intensity lane must be in [0, 3]."));
            }
        }

        static bool IsFinite(Vector3 value) => (float.IsFinite(f: value.X) && float.IsFinite(f: value.Y) && float.IsFinite(f: value.Z));

        static void ValidateHexColor(string? value, string name, List<DocumentValidationError> errors, string path) {
            if (
                (value is not null) &&
                !HexColor.TryParse(rgb: out _, value: value)
            ) {
                errors.Add(item: new(
                    Message: $"{name} must be #RRGGBB.",
                    Path: $"{path}.{name}"
                ));
            }
        }

        static void ValidateNonNegative(float? value, string name, List<DocumentValidationError> errors, string path) {
            if (
                (value is { } scalar) &&
                (!float.IsFinite(f: scalar) || (scalar < 0f))
            ) {
                errors.Add(item: new(
                    Message: $"{name} must be finite and non-negative.",
                    Path: $"{path}.{name}"
                ));
            }
        }

        static void ValidatePositive(float? value, string name, List<DocumentValidationError> errors, string path) {
            if (
                (value is { } scalar) &&
                (!float.IsFinite(f: scalar) || (scalar <= 0f))
            ) {
                errors.Add(item: new(
                    Message: $"{name} must be finite and positive.",
                    Path: $"{path}.{name}"
                ));
            }
        }
    }
}

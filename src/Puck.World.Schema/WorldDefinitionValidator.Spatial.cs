using System.Numerics;

namespace Puck.World;

public static partial class WorldDefinitionValidator {
    /// <summary>Validates the named spatial volumes authored on one placement.</summary>
    /// <param name="placement">The placement whose volume collection is checked.</param>
    /// <param name="path">The document path used to qualify diagnostics.</param>
    /// <param name="errors">The diagnostic sink.</param>
    public static void ValidatePlacementSpatial(WorldPlacement placement, string path, List<string> errors) {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(errors);

        if (placement.Spatial is not { Count: > 0 } spatial) {
            return;
        }

        // A spatial row's local scale participates in every compiled radius and half-extent.  Reject a
        // positive value that would disappear at the fixed-point boundary before it can become a silent point.
        RequireNavigationFixedPositive(placement.Scale, $"{path}.scale", errors);

        var spatialNames = new HashSet<string>(StringComparer.Ordinal);
        for (var spatialIndex = 0; spatialIndex < spatial.Count; spatialIndex++) {
            var volume = spatial[spatialIndex];
            var spatialPath = $"{path}.spatial[{spatialIndex}]";
            if (volume is null) {
                errors.Add($"{spatialPath} is required.");
                continue;
            }
            if (!Enum.IsDefined(volume.Role)) { errors.Add($"{spatialPath}.role is unsupported."); }
            if (string.IsNullOrWhiteSpace(volume.Name)) {
                errors.Add($"{spatialPath}.name is required.");
            }
            if (!spatialNames.Add(volume.Name)) {
                errors.Add($"{spatialPath}.name '{volume.Name}' is duplicated on placement '{placement.Id}'.");
            }
            if (volume.Shape is null) {
                errors.Add($"{spatialPath}.shape is required.");
                continue;
            }
            if (volume.Shape.Center is null || !IsFinite(volume.Shape.Center)) {
                errors.Add($"{spatialPath}.shape.center must contain finite coordinates.");
            }
            if (!float.IsFinite(volume.Shape.YawDegrees)) {
                errors.Add($"{spatialPath}.shape.yawDegrees must be finite.");
            }
            switch (volume.Shape.Kind) {
                case WorldSpatialShapeKind.Box:
                    if (volume.Shape.HalfExtents is null || !IsFinite(volume.Shape.HalfExtents)) {
                        errors.Add($"{spatialPath}.shape.halfExtents must contain finite coordinates.");
                    } else {
                        RequirePositive(volume.Shape.HalfExtents.X, $"{spatialPath}.shape.halfExtents.x", errors);
                        RequirePositive(volume.Shape.HalfExtents.Y, $"{spatialPath}.shape.halfExtents.y", errors);
                        RequirePositive(volume.Shape.HalfExtents.Z, $"{spatialPath}.shape.halfExtents.z", errors);
                        RequireNavigationFixedPositive(volume.Shape.HalfExtents.X, $"{spatialPath}.shape.halfExtents.x", errors);
                        RequireNavigationFixedPositive(volume.Shape.HalfExtents.Y, $"{spatialPath}.shape.halfExtents.y", errors);
                        RequireNavigationFixedPositive(volume.Shape.HalfExtents.Z, $"{spatialPath}.shape.halfExtents.z", errors);
                    }
                    if (volume.Shape.Radius != 0f) {
                        errors.Add($"{spatialPath}.shape.radius must be zero for a box.");
                    }
                    break;
                case WorldSpatialShapeKind.Sphere:
                    RequirePositive(volume.Shape.Radius, $"{spatialPath}.shape.radius", errors);
                    RequireNavigationFixedPositive(volume.Shape.Radius, $"{spatialPath}.shape.radius", errors);
                    if (volume.Shape.HalfExtents is null || volume.Shape.HalfExtents.Value != Vector3.Zero) {
                        errors.Add($"{spatialPath}.shape.halfExtents must be [0, 0, 0] for a sphere.");
                    }
                    break;
                default:
                    errors.Add($"{spatialPath}.shape.kind '{volume.Shape.Kind}' is unsupported.");
                    break;
            }
            if (volume.Role == WorldPlacementSpatialRole.Influence) {
                if (!CellName.TryParse(volume.Channel, out _, out _)) {
                    errors.Add($"{spatialPath}.channel must be a valid cell name for an influence volume.");
                }
            } else if (volume.Channel is not null) {
                errors.Add($"{spatialPath}.channel is only valid for influence volumes.");
            }
        }
    }

    /// <summary>Validates the transform and spatial volume geometry needed before compiling a placement candidate.</summary>
    /// <param name="placement">The candidate placement.</param>
    /// <param name="reason">The first refusal, or an empty string when valid.</param>
    /// <returns><see langword="true"/> when the candidate has finite geometry accepted by the spatial validator.</returns>
    public static bool TryValidatePlacementGeometry(WorldPlacement placement, out string reason) {
        ArgumentNullException.ThrowIfNull(placement);

        var errors = new List<string>();
        if (!IsFinite(placement.Position)) {
            errors.Add("placement.position must contain finite coordinates.");
        }
        RequireFinite(placement.YawDegrees, "placement.yawDegrees", errors);
        RequirePositive(placement.Scale, "placement.scale", errors);
        ValidatePlacementSpatial(placement, "placement", errors);
        reason = errors.Count == 0 ? string.Empty : errors[0];
        return errors.Count == 0;
    }
}

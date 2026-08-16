using System.Numerics;

namespace Puck.Commands;

/// <summary>The result of resolving one spatial or axis vector against a radial.</summary>
public enum BindingWheelSelectionOutcome {
    /// <summary>The input's authored selection policy is disabled.</summary>
    Disabled,

    /// <summary>The input remains inside the authored non-selecting hub.</summary>
    DeadZone,

    /// <summary>The input lies beyond a bounded selector's outer target.</summary>
    Outside,

    /// <summary><see cref="BindingWheelSelection.Sector"/> names the selected sector.</summary>
    Sector,
}
/// <summary>A device-neutral radial-selection result.</summary>
/// <param name="Sector">The zero-based selected sector, or <c>-1</c> when none is selected.</param>
/// <param name="Outcome">Why a sector was or was not selected.</param>
public readonly record struct BindingWheelSelection(int Sector, BindingWheelSelectionOutcome Outcome) {
    /// <summary>The stable read-back token used by radial presenters.</summary>
    public string Reason => Outcome switch {
        BindingWheelSelectionOutcome.Disabled => "disabled",
        BindingWheelSelectionOutcome.DeadZone => "dead-center",
        BindingWheelSelectionOutcome.Outside => "outside",
        BindingWheelSelectionOutcome.Sector => "sector",
        _ => "invalid",
    };
}
/// <summary>Pure radial geometry shared by pointer presentation today and future spatial inputs such as touch.</summary>
public static class BindingWheelGeometry {
    private static BindingWheelSelection SelectAngle(Vector2 vector, int sectorCount, BindingWheelStyleDefinition style) {
        var clockwiseAngle = MathF.Atan2(
            x: -vector.Y,
            y: vector.X
        );

        if (clockwiseAngle < 0f) {
            clockwiseAngle += MathF.Tau;
        }

        var rotation = (style.RotationDegrees * (MathF.PI / 180f));
        var relative = (style.Clockwise
            ? (clockwiseAngle - rotation)
            : (rotation - clockwiseAngle)
        );

        relative = (((relative % MathF.Tau) + MathF.Tau) % MathF.Tau);
        var span = (MathF.Tau / sectorCount);
        var sector = (((int)((relative + (span * 0.5f)) / span)) % sectorCount);

        return new BindingWheelSelection(
            Outcome: BindingWheelSelectionOutcome.Sector,
            Sector: sector
        );
    }

    /// <summary>Converts pointer/touch displacement into the same neutral-relative magnitude space an Axis2D
    /// selector already occupies.</summary>
    public static Vector2 NormalizeSpatialExcursion(Vector2 vector, float viewportUnit, BindingWheelExcursionView excursion) {
        ArgumentNullException.ThrowIfNull(argument: excursion);

        return (vector / (viewportUnit * excursion.SpatialTravelFraction));
    }
    /// <summary>Resolves a normalized neutral-relative vector into an authored ring. The previous selected ring
    /// supplies hysteresis; -1 resolves directly against the ordinary authored boundaries. The final ring is
    /// intentionally unbounded.</summary>
    public static int ResolveExcursionRing(Vector2 vector, BindingWheelExcursionView excursion, int previousRing) {
        ArgumentNullException.ThrowIfNull(argument: excursion);

        var magnitudeSquared = vector.LengthSquared();

        if (magnitudeSquared <= excursion.DeadZoneSquared) {
            return -1;
        }

        var thresholds = excursion.ThresholdsSquared;
        var ringCount = (thresholds.Count + 1);

        if (
            (previousRing < 0) ||
            (previousRing >= ringCount)
        ) {
            var resolved = 0;

            while (
                (resolved < thresholds.Count) &&
                (magnitudeSquared > thresholds[resolved])
            ) {
                resolved++;
            }

            return resolved;
        }

        var ring = previousRing;

        while (
            (ring < (ringCount - 1)) &&
            (magnitudeSquared > excursion.OutwardThresholdsSquared[ring])
        ) {
            ring++;
        }

        while (
            (ring > 0) &&
            (magnitudeSquared < excursion.InwardThresholdsSquared[(ring - 1)])
        ) {
            ring--;
        }

        return ring;
    }
    /// <summary>Resolves the fixed hub used for one open gesture. A pointer-relative radial falls back to viewport
    /// center when that seat has no pointer location.</summary>
    public static Vector2 ResolveOpeningCenter(BindingWheelPlacement placement, bool pointerAvailable, Vector2 pointer, Vector2 viewportCenter) =>
        (((placement == BindingWheelPlacement.Pointer) && pointerAvailable)
            ? pointer
            : viewportCenter
        );
    /// <summary>Chooses the vector used to select and qualify a spatial input's sector independently from
    /// presentation placement and excursion-based ring choice. Angle gestures measure from the input device's
    /// captured neutral; direct targeting measures from the displayed hub.</summary>
    public static Vector2 ResolveSpatialTargetVector(BindingWheelSpatialSelectionMode mode, Vector2 position, Vector2 neutral, Vector2 hub) =>
        (mode switch {
            BindingWheelSpatialSelectionMode.Angle => (position - neutral),
            BindingWheelSpatialSelectionMode.HitTarget => (position - hub),
            _ => Vector2.Zero,
        });
    /// <summary>Resolves a normalized Axis2D selector. Axis selection is always directional and retains the authored
    /// dead zone plus the conventional normalized outer guard.</summary>
    public static BindingWheelSelection SelectAxis(Vector2 vector, int sectorCount, BindingWheelStyleDefinition style) {
        ArgumentNullException.ThrowIfNull(argument: style);

        var distanceSquared = vector.LengthSquared();

        if (distanceSquared <= (style.DeadZoneFraction * style.DeadZoneFraction)) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.DeadZone,
                Sector: -1
            );
        }

        var outer = (1f + style.OuterGraceRingFraction);

        if (distanceSquared > (outer * outer)) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.Outside,
                Sector: -1
            );
        }

        return SelectAngle(
            sectorCount: sectorCount,
            style: style,
            vector: vector
        );
    }
    /// <summary>Resolves only the angular component after another policy has accepted the vector and chosen a ring.</summary>
    public static BindingWheelSelection SelectDirection(Vector2 vector, int sectorCount, BindingWheelStyleDefinition style) {
        ArgumentNullException.ThrowIfNull(argument: style);

        return SelectAngle(
            sectorCount: sectorCount,
            style: style,
            vector: vector
        );
    }
    /// <summary>Selects a sector from a vector already resolved against the policy-defined origin. Angle
    /// deliberately has no outer limit, so a fast pointer throw remains selected; HitTarget retains the visible
    /// annulus.</summary>
    public static BindingWheelSelection SelectSpatial(
        Vector2 vector,
        int sectorCount,
        int ringCount,
        BindingWheelStyleDefinition style,
        BindingWheelSpatialSelectionMode mode,
        float unit
    ) {
        ArgumentNullException.ThrowIfNull(argument: style);

        if (mode == BindingWheelSpatialSelectionMode.Disabled) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.Disabled,
                Sector: -1
            );
        }

        var distanceSquared = vector.LengthSquared();
        var inner = (unit * style.DeadZoneFraction);

        if (distanceSquared <= (inner * inner)) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.DeadZone,
                Sector: -1
            );
        }

        if (mode == BindingWheelSpatialSelectionMode.HitTarget) {
            var outer = (inner + (((ringCount + style.OuterGraceRingFraction) * unit) * style.RingWidthFraction));

            if (distanceSquared > (outer * outer)) {
                return new BindingWheelSelection(
                    Outcome: BindingWheelSelectionOutcome.Outside,
                    Sector: -1
                );
            }
        }

        return SelectAngle(
            sectorCount: sectorCount,
            style: style,
            vector: vector
        );
    }
}

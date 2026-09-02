using System.Numerics;

using Puck.Maths;

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
/// <summary>Pure radial geometry and selection policy shared by pointer presentation today and future spatial
/// inputs such as touch.</summary>
/// <remarks>
/// The sector a gesture picks is dispatched into the seat's deterministic command lane, so the CHOICE has to be
/// reproducible even though the vector reaching it is presentation float. Every step here is therefore either an
/// exactly-rounded IEEE operation (add, subtract, multiply, divide, remainder, comparison, truncation — all
/// bit-identical on every machine .NET runs on) or <see cref="FixedQ4816.Atan2"/>, whose pure-integer
/// implementation is documented bit-identical across machines. No <c>MathF</c>/<c>Math</c> transcendental is
/// reachable from a selection: a libm <c>atan2</c> is free to differ in its last place between runtimes, and one
/// differing ULP at a sector boundary is a different command.
/// <para>The sector rule is half-open and the same in every quadrant: sector <c>k</c> is centred
/// <c>(k + SectorOffset)</c> sectors clockwise of twelve o'clock and sweeps from half a sector before that centre,
/// so a direction sitting exactly on a seam selects the sector clockwise of the seam. Because the reading is
/// quantised, that promise is kept to within one and a half steps of the Q16 angle grid (2.3e-5 rad); a direction
/// inside that band of a seam selects the clockwise sector too.</para>
/// </remarks>
public static class BindingWheelGeometry {
    // One step of the Q16 angle grid FixedQ4816.Atan2 reports on, in radians. Its documented worst case is 0.51 of
    // this, so a whole step is strictly more than the reading can be wrong by — see SelectAngle.
    private const double AngleStep = (1d / 65_536d);
    // The exponent the prescale below lands the larger component's magnitude on: [2^15, 2^16). See SelectAngle.
    private const int PrescaleExponent = 15;

    // The angle decision, made reproducible. The vector is first scaled by a POWER OF TWO so its larger component
    // lands in [2^15, 2^16): that is exact in binary floating point (it only shifts exponents), so it moves no
    // angle, while spending the Q48.16 conversion's 16 fractional bits on a value 15 bits above the units place —
    // the component rounding then perturbs the angle by under 2^-14 of one Q16 angle step, whether the caller
    // passed a normalized stick deflection or a pointer displacement in pixels. (Landing in [1, 2) instead left
    // that perturbation at about half a step, comparable with Atan2's own error and enough to unseat a seam.)
    private static BindingWheelSelection SelectAngle(Vector2 vector, int sectorCount, BindingWheelStyleDefinition style) {
        var magnitude = MathF.Max(
            x: MathF.Abs(x: vector.X),
            y: MathF.Abs(x: vector.Y)
        );

        // A zero or non-finite vector names no direction. SelectAxis and SelectSpatial already refuse the first via
        // their dead zones, but SelectDirection is entered on a ring decision alone and every one of the three is
        // public, so the refusal lives here — the one place all of them pass through.
        if (
            !float.IsFinite(f: magnitude) ||
            (magnitude == 0f)
        ) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.DeadZone,
                Sector: -1
            );
        }

        var exponent = (PrescaleExponent - MathF.ILogB(x: magnitude));
        // Screen space is +Y down and sector zero sits at twelve o'clock, so the clockwise angle from that mark is
        // atan2(x, -y).
        var clockwiseAngle = ((double)FixedQ4816.Atan2(
            x: FixedQ4816.FromDouble(value: -MathF.ScaleB(
                n: exponent,
                x: vector.Y
            )),
            y: FixedQ4816.FromDouble(value: MathF.ScaleB(
                n: exponent,
                x: vector.X
            ))
        ));

        if (clockwiseAngle < 0d) {
            clockwiseAngle += Math.Tau;
        }

        // The half-open rule, made SYMMETRIC across the branch cut. Atan2 rounds a MAGNITUDE to the nearest Q16
        // step, so the reading straddles the true angle; wrapping a negative reading by a whole turn flips which
        // way that straddle points, and a direction sitting exactly on a seam fell forward on the right half of the
        // radial and backward on the left — (-1, -1) picked the sector before 315 degrees while (1, -1) picked the
        // sector after 45. Reading the quantised value as the CLOCKWISE END of the step it names removes the
        // asymmetry: one whole step is strictly more than the documented 0.51-step error, so the angle the sector
        // is taken from is never behind the true angle, in any quadrant. The cost is that the decision moves at
        // most 1.51 steps (2.3e-5 rad) early — four orders of magnitude finer than any authored sector.
        clockwiseAngle += AngleStep;

        // Sector k is CENTERED at (k + SectorOffset) sectors clockwise from north, so its sweep starts half a
        // sector before that. The writer draws with the identical rule, so the piece under the selector is the
        // piece that lights.
        var span = (Math.Tau / sectorCount);
        var relative = ((clockwiseAngle - (style.SectorOffset * span)) + (span * 0.5d));

        relative = (((relative % Math.Tau) + Math.Tau) % Math.Tau);
        var sector = (((int)(relative / span)) % sectorCount);

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

        if (distanceSquared <= (style.AxisDeadZone * style.AxisDeadZone)) {
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

        // An Axis2D selector reports +Y up (the stick convention); the angle math is screen space (+Y down).
        return SelectAngle(
            sectorCount: sectorCount,
            style: style,
            vector: new Vector2(
                x: vector.X,
                y: -vector.Y
            )
        );
    }
    /// <summary>Resolves only the angular component after another policy has accepted the vector and chosen a ring.
    /// A zero or non-finite vector names no direction and resolves to
    /// <see cref="BindingWheelSelectionOutcome.DeadZone"/> — it is NOT sector zero, which is what an unguarded
    /// <c>atan2(0, 0)</c> would silently report.</summary>
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
    /// <summary>Converts an authored
    /// <see cref="BindingWheelStyleDefinition.SelectionGraceSeconds"/> window into whole engine ticks — the unit a
    /// presenter must count the window in, so the sector that survives a dead-centre reading (and therefore the
    /// command a commit dispatches) is decided against the engine's one monotonic tick base rather than a private
    /// wall clock the host cannot substitute.</summary>
    /// <param name="seconds">The authored window; zero, negative, and non-finite all disable it.</param>
    /// <param name="ticksPerSecond">The engine's tick rate.</param>
    /// <returns>The window's whole tick count, truncated toward zero; <c>0</c> when the window is disabled.</returns>
    public static ulong SelectionGraceTicks(float seconds, ulong ticksPerSecond) {
        if (
            !float.IsFinite(f: seconds) ||
            (seconds <= 0f)
        ) {
            return 0UL;
        }

        var ticks = (((double)seconds) * ticksPerSecond);

        // An absurd authored window is clamped rather than wrapped: an unchecked conversion of an out-of-range
        // double to ulong is undefined, and "never expires" is the honest reading of a window that long.
        return ((ticks >= 18_446_744_073_709_551_615d)
            ? ulong.MaxValue
            : ((ulong)ticks)
        );
    }
}

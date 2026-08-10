using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The trigger policy a portal face applies over its <see cref="WorldFaceFrame"/> — the one place a door's enterable
/// volume is decided.
/// </summary>
/// <remarks>
/// <para>The band is one-sided along <see cref="WorldFaceFrame.Normal"/>: a door fires from the side it faces, and
/// walking into its back is not a crossing. Width and height are the frame's own, so a bigger door has a bigger
/// threshold — the volume moves and resizes with the drawn face.</para>
/// <para>Depth is <c>max(frame half-depth, crossing floor)</c>. The floor exists because a frame can be authored
/// thinner than one step of travel, which would leave a walk-through detectable only by the swept clip; it is a
/// sampling term, not a soundness one. Soundness is the swept clip's
/// (<see cref="WorldFaceRegion.Sweep"/>): it tests the whole previous-to-current segment and cannot be tunnelled at
/// any speed, rate, or motion program. That matters because <see cref="SpeedCeiling"/> reads only what the document
/// declares, and a seated player's live profile speed can exceed an unenveloped kit's authored speed — an
/// underestimate here costs nothing but a thinner band.</para>
/// </remarks>
public static class WorldFacePortalPolicy {
    /// <summary>The fastest travel a document declares, in world units per second — the maximum over the world's
    /// profileless motion default and every kit's own arm ceiling (an authored envelope's upper bound where one is
    /// declared, the arm's own base speed otherwise, scaled by its held sprint/boost multiplier) together with the
    /// arm's terminal vertical speeds.</summary>
    /// <param name="definition">The document to read.</param>
    /// <returns>The declared speed ceiling.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedQ4816 SpeedCeiling(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var ceiling = FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: definition.Motion.MoveSpeed));

        // A sibling of WorldBody's own motion-arm switch (the sim) and WorldDefinitionValidator's (authoring
        // checks) — each dispatches on the SAME closed WorldMotionModel hierarchy for a DIFFERENT question (drive
        // the body vs. validate the document vs., here, bound how fast a body can travel), so collapsing them into
        // one predicate would blur three distinct policies rather than deduplicate one. A new WorldMotionModel arm
        // owes all three switches an entry; the compiler will not name the other two for you.
        foreach (var kit in definition.Kits) {
            if (kit is null) {
                continue;
            }

            switch (kit.Motion) {
                case WorldMotionModel.Grounded grounded:
                    ceiling = FixedQ4816.Max(x: ceiling, y: Scaled(baseSpeed: (grounded.MoveSpeedEnvelope?.Max ?? grounded.MoveSpeed), multiplier: grounded.SprintMultiplier));
                    ceiling = FixedQ4816.Max(x: ceiling, y: Magnitude(value: grounded.MaxFallSpeed));

                    break;
                case WorldMotionModel.Vehicle vehicle:
                    ceiling = FixedQ4816.Max(x: ceiling, y: Scaled(baseSpeed: (vehicle.TopSpeedEnvelope?.Max ?? vehicle.TopSpeed), multiplier: vehicle.BoostMultiplier));
                    ceiling = FixedQ4816.Max(x: ceiling, y: Magnitude(value: vehicle.ReverseTopSpeed));
                    ceiling = FixedQ4816.Max(x: ceiling, y: Magnitude(value: vehicle.MaxFallSpeed));

                    break;
                case WorldMotionModel.Swim swim:
                    ceiling = FixedQ4816.Max(x: ceiling, y: Scaled(baseSpeed: (swim.ThrustSpeedEnvelope?.Max ?? swim.ThrustSpeed), multiplier: swim.SprintMultiplier));
                    ceiling = FixedQ4816.Max(x: ceiling, y: Magnitude(value: swim.MaxRiseSpeed));
                    ceiling = FixedQ4816.Max(x: ceiling, y: Magnitude(value: swim.MaxSinkSpeed));

                    break;
                default:
                    break;
            }
        }

        return ceiling;
    }

    /// <summary>The least band depth a portal face admits: the declared <see cref="SpeedCeiling"/> travelled over one
    /// simulation step, plus the world's contact skin. A resident, non-stepping world (rate zero) advances nobody, so
    /// its floor is zero.</summary>
    /// <param name="definition">The document to read.</param>
    /// <returns>The crossing floor in world units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedQ4816 CrossingFloor(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var rate = definition.SimulationRateHz;

        if (rate <= 0) {
            return FixedQ4816.Zero;
        }

        var step = (FixedQ4816.One / FixedQ4816.FromInteger(value: rate));

        return ((SpeedCeiling(definition: definition) * step) + FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: definition.Collision.ContactSkin)));
    }

    /// <summary>Builds the enterable region a portal face opens.</summary>
    /// <param name="row">The derived face row.</param>
    /// <param name="crossingFloor">The document's <see cref="CrossingFloor"/>.</param>
    /// <param name="aperture">The region on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the face's shape kind maps onto a region arm.</returns>
    public static bool TryAperture(in WorldFaceRow row, FixedQ4816 crossingFloor, out WorldFaceAperture? aperture) {
        switch (row.Aperture) {
            case WorldFaceApertureKind.Box:
                aperture = new WorldFaceAperture.Box(Frame: row.Frame, Depth: FixedQ4816.Max(x: row.Frame.HalfDepth, y: crossingFloor));

                return true;
            default:
                aperture = null;

                return false;
        }
    }

    private static FixedQ4816 Magnitude(float value) => FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value));

    // A held sprint/boost multiplies the resolved base rate, never divides it: a multiplier below one is a slower
    // held state, so it can only lower a ceiling the unheld rate already covers.
    private static FixedQ4816 Scaled(float baseSpeed, float multiplier) {
        var resolved = Magnitude(value: baseSpeed);

        return FixedQ4816.Max(x: resolved, y: (resolved * Magnitude(value: multiplier)));
    }
}

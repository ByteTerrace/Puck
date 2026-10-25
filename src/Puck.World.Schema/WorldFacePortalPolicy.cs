using Puck.Maths;

namespace Puck.World;

/// <summary>
/// The trigger policy a portal face applies over its <see cref="WorldFaceFrame"/> — the one place a door's enterable
/// volume is decided.
/// </summary>
/// <remarks>
/// <para>The slab lies behind the glass, against <see cref="WorldFaceFrame.Normal"/>, and is entered only from the
/// front: a door fires when a body's origin passes through its glass from the side it faces, and walking into its
/// back is not a crossing. Width and height are the frame's own, so a bigger door has a bigger opening — the volume
/// moves and resizes with the drawn face. While a body stands in the opening, contact with geometry behind the glass
/// is ignored (<c>Puck.World.Server.WorldPortalHoleContactField</c>), so a door laid on solid ground lets a body drop
/// through it.</para>
/// <para>Depth is <c>max(frame half-depth, crossing floor)</c>. The floor exists because a frame can be authored
/// thinner than one step of travel, which would leave a walk-through detectable only by the swept clip; it is a
/// sampling term, not a soundness one. Soundness is the swept clip's
/// (<see cref="WorldFaceRegion.Sweep"/>): it tests the whole previous-to-current segment and cannot be tunnelled at
/// any speed, rate, or motion program. That matters because <see cref="SpeedCeiling"/> reads only what the document
/// declares, and a seated player's live profile speed can exceed an unenveloped kit's authored speed — an
/// underestimate here costs nothing but a thinner band.</para>
/// </remarks>
public static class WorldFacePortalPolicy {
    private static FixedQ4816 Magnitude(float value) => FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: value));
    // A held sprint/boost multiplies the resolved base rate, never divides it: a multiplier below one is a slower
    // held state, so it can only lower a ceiling the unheld rate already covers.
    private static FixedQ4816 Scaled(float baseSpeed, float multiplier) {
        var resolved = Magnitude(value: baseSpeed);

        return FixedQ4816.Max(
            x: resolved,
            y: (resolved * Magnitude(value: multiplier))
        );
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
    /// <summary>The fastest travel a document declares, in world units per second — the maximum over the world's
    /// profileless motion default and, per kit, its speed row's ceiling (the authored <c>speed.envelope</c> upper
    /// bound where one is declared, its own <c>speed.value</c> otherwise, scaled by its held multiplier), its
    /// holds' fastest authored vertical speed (a terminal fall speed or a medium's rise/sink terminal — zero for a
    /// kit whose holds are all Pull or None, which folds into this maximum as a no-op rather than lowering it), and
    /// a drive-decomposition shaping row's <c>along.backwardSpeed</c> where one is authored.</summary>
    /// <param name="definition">The document to read.</param>
    /// <returns>The declared speed ceiling.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedQ4816 SpeedCeiling(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var ceiling = FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: definition.Motion.MoveSpeed));

        var kits = definition.Kits;

        for (var index = 0; (index < kits.Count); index++) {
            var kit = kits[index];

            if (kit is null) {
                continue;
            }

            var motion = kit.Motion;

            ceiling = FixedQ4816.Max(
                x: ceiling,
                y: Scaled(
                    baseSpeed: (motion.Speed.Envelope?.Max ?? motion.Speed.Value),
                    multiplier: (motion.Speed.Held?.Multiplier ?? 1f)
                )
            );
            ceiling = FixedQ4816.Max(
                x: ceiling,
                y: Magnitude(value: WorldHoldFactory.MaxEnvelopeSpeed(holds: motion.Holds))
            );

            // An anisotropic shaping row's along facet travels backwards at its own rate, which no forward bound covers.
            var shaping = motion.Shaping;

            for (var rowIndex = 0; (rowIndex < (shaping?.Count ?? 0)); rowIndex++) {
                var row = shaping![rowIndex];

                if (
                    (row?.Across is not null) &&
                    (row.Along?.BackwardSpeed is { } reverse)
                ) {
                    ceiling = FixedQ4816.Max(
                        x: ceiling,
                        y: Magnitude(value: reverse)
                    );
                }
            }
        }

        return ceiling;
    }
    /// <summary>Returns the opening of every portal face a document declares, in document order.</summary>
    /// <param name="definition">The document to read.</param>
    /// <returns>Each portal face's box aperture.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldFaceAperture.Box[] PortalApertures(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var catalog = WorldFaceCatalog.For(definition: definition);
        var openings = new List<WorldFaceAperture.Box>();

        foreach (var placement in definition.Placements) {
            foreach (var face in (placement?.FaceSources ?? [])) {
                if (
                    (face?.Portal is not null) &&
                    catalog.TryFindAperture(
                    aperture: out var aperture,
                    definition: definition,
                    faceName: face.Face,
                    placementId: placement!.Id
                ) &&
                    (aperture is WorldFaceAperture.Box box)
                ) {
                    openings.Add(item: box);
                }
            }
        }

        return [.. openings];
    }
    /// <summary>Builds the enterable region a portal face opens.</summary>
    /// <param name="row">The derived face row.</param>
    /// <param name="crossingFloor">The document's <see cref="CrossingFloor"/>.</param>
    /// <param name="aperture">The region on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the face's shape kind opens a region.</returns>
    public static bool TryAperture(in WorldFaceRow row, FixedQ4816 crossingFloor, out WorldFaceAperture? aperture) {
        if (row.Aperture is not { } recipe) {
            aperture = null;

            return false;
        }

        aperture = recipe.Open(
            arg1: row.Frame,
            arg2: crossingFloor
        );

        return true;
    }
}

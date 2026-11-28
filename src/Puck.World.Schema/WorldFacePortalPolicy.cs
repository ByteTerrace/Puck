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
/// <para>The slab's depth is the face's own, <c>max(frame half-depth, slab floor)</c>, the floor being the world's
/// contact skin. Each reader widens it by the body it asks about, never by the fastest travel the world declares:
/// <see cref="WorldFaceRegion.Sweep"/> reaches past the slab by the segment's own travel along the normal, so a body
/// that passed through the glass this step is found in it, and the opening's band
/// (<c>Puck.World.Server.WorldPortalHoleContactField</c>) reaches as far as the asking body's collider and its travel
/// this step. A body standing clear of a door is pushed as it is anywhere else, however fast another kit can go. The
/// depth decides no crossing: whether a door fires is the glass test, which tests the whole previous-to-current segment
/// and cannot be tunnelled at any speed, rate, or motion program.</para>
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

    /// <summary>The least depth a portal face's slab has whatever the face's own thickness: the world's contact skin, so
    /// a face drawn thinner than the skin still holds an origin the contact solve leaves at its glass.</summary>
    /// <param name="definition">The document to read.</param>
    /// <returns>The slab floor in world units.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static FixedQ4816 SlabFloor(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        return FixedQ4816.Abs(value: FixedQ4816.FromDouble(value: definition.Collision.ContactSkin));
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
    /// <summary>Builds the enterable region a portal face opens.</summary>
    /// <param name="row">The derived face row.</param>
    /// <param name="slabFloor">The document's <see cref="SlabFloor"/>.</param>
    /// <param name="aperture">The region on success; otherwise <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the face's shape kind opens a region.</returns>
    public static bool TryAperture(in WorldFaceRow row, FixedQ4816 slabFloor, out WorldFaceAperture? aperture) {
        if (row.Aperture is not { } recipe) {
            aperture = null;

            return false;
        }

        aperture = recipe.Open(
            arg1: row.Frame,
            arg2: slabFloor
        );

        return true;
    }
}

using Puck.Maths;
using Puck.Physics;

namespace Puck.World;

/// <summary>
/// One placement face's derived geometry, in the deterministic fixed-point domain: an orthonormal frame plus the
/// half-extents of the surface it names. Trigger, arrival, and render all read this one derivation
/// (<see cref="WorldFaceCatalog"/>); nothing re-derives a face's position from the placement root.
/// </summary>
/// <remarks>
/// <para>Geometry only. Trigger band depth, arrival clearance, and render proud-epsilon/interior fraction are
/// per-consumer policy over this one frame and may differ from each other.</para>
/// <para><see cref="Normal"/> is the outward direction (<c>Right x Up</c> convention, local <c>+Z</c> forward). The
/// triad is orthonormal from the placement yaw and the shape's own rotation composed together
/// (<c>WorldFaceCatalog.DeriveFrame</c>) — it can carry pitch or roll; <see cref="IsYawOnly"/> is the exact test for
/// whether <see cref="Up"/> came out as world <c>+Y</c>. A portal face may lie at any orientation whose pairing with
/// its counterpart maps world up onto itself (<see cref="WorldFrameIsometry.PreservesWorldUp"/>), such as a floor
/// face paired with a ceiling face.</para>
/// </remarks>
/// <param name="Origin">The face surface's center in world space.</param>
/// <param name="Right">The unit in-plane axis along the face's width.</param>
/// <param name="Up">The unit in-plane axis along the face's height.</param>
/// <param name="Normal">The unit outward axis.</param>
/// <param name="HalfWidth">The half-extent along <paramref name="Right"/>.</param>
/// <param name="HalfHeight">The half-extent along <paramref name="Up"/>.</param>
/// <param name="HalfDepth">The half-extent along <paramref name="Normal"/>.</param>
public readonly record struct WorldFaceFrame(
    FixedVector3 Origin,
    FixedVector3 Right,
    FixedVector3 Up,
    FixedVector3 Normal,
    FixedQ4816 HalfWidth,
    FixedQ4816 HalfHeight,
    FixedQ4816 HalfDepth
) {
    /// <summary>Gets a value indicating whether this frame is a pure rotation about world up — <see cref="Up"/> is
    /// exactly world <c>+Y</c>, so the whole frame is described by <see cref="PlanarYawRadians"/> alone.</summary>
    /// <remarks>The test is exact, and can be: a rotation about <c>+Y</c> leaves <c>(0,1,0)</c> bit-unchanged under
    /// <see cref="FixedQuaternion.Rotate"/> (both cross products vanish), and an authored quaternion's off-axis
    /// components below one Q48.16 unit quantize to zero at the conversion door. A frame that fails this carries
    /// pitch or roll.</remarks>
    public bool IsYawOnly =>
        ((Up.X == FixedQ4816.Zero) && (Up.Y == FixedQ4816.One) && (Up.Z == FixedQ4816.Zero));
    /// <summary>Gets the frame's heading about world up, in radians — the yaw a rotation of world <c>+Z</c> onto
    /// <see cref="Normal"/> would use. Meaningful only for a yaw-only frame; see <see cref="IsYawOnly"/>.</summary>
    public FixedQ4816 PlanarYawRadians => FixedQ4816.Atan2(
        y: Normal.X,
        x: Normal.Z
    );

    /// <summary>Gets the world-space point at in-plane coordinates <paramref name="u"/> along <see cref="Right"/> and
    /// <paramref name="v"/> along <see cref="Up"/>, measured from <see cref="Origin"/> — the same coordinate system
    /// <see cref="WorldFaceCrossing.SeamU"/> and <see cref="WorldFaceCrossing.SeamV"/> are expressed in, so a swept
    /// crossing's seam converts back to a world position with no separate derivation.</summary>
    /// <param name="u">The offset along <see cref="Right"/>.</param>
    /// <param name="v">The offset along <see cref="Up"/>.</param>
    /// <returns>The world-space point <c>Origin + (u * Right) + (v * Up)</c>.</returns>
    public FixedVector3 PointAt(FixedQ4816 u, FixedQ4816 v) => ((Origin + (Right * u)) + (Up * v));
}
/// <summary>
/// The region a face opens — the shape a swept body is tested against. Each arm carries whatever its own test needs,
/// so an arm whose frame varies along the surface is expressible without widening the ones that do not.
/// </summary>
public abstract record WorldFaceAperture {
    private WorldFaceAperture() {
    }

    /// <summary>A planar rectangular aperture: the face's glass, which a body's origin crosses from the side it faces,
    /// and the face's own frame extruded one-sidedly behind it, against <see cref="WorldFaceFrame.Normal"/>, the
    /// slab a body occupies once it has passed through.</summary>
    /// <param name="Frame">The face frame the slab is built on.</param>
    /// <param name="Depth">How far the slab extends behind the glass, along <see cref="WorldFaceFrame.Normal"/>: the
    /// slab is <c>[-Depth, 0]</c>. It bounds occupancy only; whether a body crossed is the glass test alone
    /// (<see cref="WorldFaceRegion.Sweep"/>).</param>
    public sealed record Box(WorldFaceFrame Frame, FixedQ4816 Depth) : WorldFaceAperture {
        /// <summary>Gets a world-axis box that holds the whole slab, grown past the rounding of the region test by a
        /// sixty-fourth of the slab's extents and a sixty-fourth of a metre. A segment whose bounds miss it neither
        /// ends in the slab nor crosses it, so a scan skips the region test for it
        /// (<see cref="WorldFaceRegion.Sweep"/>).</summary>
        public FixedAabb Bounds {
            get {
                var halfDepth = (Depth / FixedQ4816.FromInteger(value: 2));
                var extent = FixedAabb.OrientedHalfExtent(
                    axisX: Frame.Right,
                    axisY: Frame.Up,
                    axisZ: Frame.Normal,
                    halfExtent: new FixedVector3(X: Frame.HalfWidth, Y: Frame.HalfHeight, Z: halfDepth)
                );
                var margin = (((Frame.HalfWidth + Frame.HalfHeight + Depth) / FixedQ4816.FromInteger(value: 64)) + FixedQ4816.FromRawBits(value: 1024L));

                return FixedAabb.FromCenter(center: (Frame.Origin - (Frame.Normal * halfDepth)), extent: extent).Grow(margin: margin);
            }
        }
    }
}
/// <summary>One swept region test's answer.</summary>
/// <param name="Inside">Whether the segment's end point lies in the region. This is the occupancy latch value: it is
/// what a face records about a body, independent of whether the segment crossed.</param>
/// <param name="Crossed">Whether the segment passes through the glass from the side it faces: it starts in front of
/// the face's plane, ends on or behind it, and meets the plane within the face's rectangle.</param>
/// <param name="Parameter">The parameter in <c>[0, 1]</c> along the segment at which it meets the glass; zero when
/// nothing crossed.</param>
/// <param name="SeamU">The in-plane coordinate along <see cref="WorldFaceFrame.Right"/> at
/// <paramref name="Parameter"/>.</param>
/// <param name="SeamV">The in-plane coordinate along <see cref="WorldFaceFrame.Up"/> at
/// <paramref name="Parameter"/>.</param>
/// <param name="Frame">The frame at <paramref name="Parameter"/>.</param>
public readonly record struct WorldFaceCrossing(
    bool Inside,
    bool Crossed,
    FixedQ4816 Parameter,
    FixedQ4816 SeamU,
    FixedQ4816 SeamV,
    WorldFaceFrame Frame
);
/// <summary>
/// The swept region test behind every face-crossing decision, dispatched on the aperture arm.
/// </summary>
public static class WorldFaceRegion {
    // Inside is the end point's slab test, the occupancy fact a latch records. Crossed is the swept test of the glass
    // itself: the segment's normal component goes from in front of the plane to on or behind it, and the in-plane point
    // where it meets the plane (the shared clipper's boundary parameter, using only Dot and division) lies within the
    // face's rectangle. It reads no depth, so no speed, rate or motion program can tunnel through it, and it fires
    // only for travel through the glass from the side it faces: a segment that starts on or behind the plane (a
    // body posed into the slab, a step away from the glass on its far side, a walk out through the front) never fires.
    private static WorldFaceCrossing SweepBox(WorldFaceAperture.Box box, FixedVector3 from, FixedVector3 to) {
        var frame = box.Frame;
        var delta = (to - frame.Origin);
        var alongNormal = FixedVector3.Dot(
            left: delta,
            right: frame.Normal
        );
        var alongRight = FixedVector3.Dot(
            left: delta,
            right: frame.Right
        );
        var alongUp = FixedVector3.Dot(
            left: delta,
            right: frame.Up
        );
        var inside = ((alongNormal <= FixedQ4816.Zero) && (alongNormal >= -box.Depth) &&
            (FixedQ4816.Abs(value: alongRight) <= frame.HalfWidth) &&
            (FixedQ4816.Abs(value: alongUp) <= frame.HalfHeight));

        var previousDelta = (from - frame.Origin);
        var previousAlongNormal = FixedVector3.Dot(
            left: previousDelta,
            right: frame.Normal
        );
        var previousAlongRight = FixedVector3.Dot(
            left: previousDelta,
            right: frame.Right
        );
        var previousAlongUp = FixedVector3.Dot(
            left: previousDelta,
            right: frame.Up
        );

        // The glass is passed from the front only by a segment that starts in front of its plane and ends on or behind
        // it; the parameter is where it meets the plane, and the door fires when that point lies on the glass. A
        // segment that starts on or behind the plane never fires, however it moves: a pose that sets a body down
        // behind a door (a zero-length segment) or a step away from the glass on its far side did not pass through it.
        var parameter = FixedQ4816.Zero;
        var crossesPlane = (
            (previousAlongNormal > FixedQ4816.Zero) &&
            (alongNormal <= FixedQ4816.Zero)
        );

        if (crossesPlane) {
            var exit = FixedQ4816.One;

            _ = FixedSegmentClip.Axis(
                start: previousAlongNormal,
                end: alongNormal,
                lower: alongNormal,
                upper: FixedQ4816.Zero,
                enter: ref parameter,
                exit: ref exit
            );
        }

        var seamU = (previousAlongRight + ((alongRight - previousAlongRight) * parameter));
        var seamV = (previousAlongUp + ((alongUp - previousAlongUp) * parameter));
        var swept = (
            crossesPlane &&
            (FixedQ4816.Abs(value: seamU) <= frame.HalfWidth) &&
            (FixedQ4816.Abs(value: seamV) <= frame.HalfHeight)
        );

        if (!swept) {
            parameter = FixedQ4816.Zero;
            seamU = previousAlongRight;
            seamV = previousAlongUp;
        }

        return new WorldFaceCrossing(
            Crossed: swept,
            Frame: frame,
            Inside: inside,
            Parameter: parameter,
            SeamU: seamU,
            SeamV: seamV
        );
    }

    /// <summary>Tests the segment <paramref name="from"/> to <paramref name="to"/> against an aperture.</summary>
    /// <param name="aperture">The region to test.</param>
    /// <param name="from">The segment start — a body's previous scan origin.</param>
    /// <param name="to">The segment end — a body's current origin.</param>
    /// <returns>The swept answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="aperture"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="aperture"/> is an arm this method does not
    /// implement.</exception>
    public static WorldFaceCrossing Sweep(WorldFaceAperture aperture, FixedVector3 from, FixedVector3 to) {
        ArgumentNullException.ThrowIfNull(aperture);

        return aperture switch {
            WorldFaceAperture.Box box => SweepBox(
            box: box,
            from: from,
            to: to
        ),
            _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(aperture),
            actualValue: aperture,
            message: "The face aperture arm has no swept region test."
        ),
        };
    }
}
/// <summary>
/// One face's claim on a body's segment, ordered so that a body crossing several faces in one step resolves to
/// exactly one winner: earliest parameter first, then the face's own stable document identity.
/// </summary>
/// <remarks>The identity tie-break exists so the winner never depends on hash or dictionary iteration order. Two
/// faces are only ever compared for the same body and the same step, so <see cref="Parameter"/> is measured along
/// one shared segment.</remarks>
/// <param name="PlacementId">The face's owning placement id.</param>
/// <param name="FaceName">The declared face name.</param>
/// <param name="Parameter">The crossing parameter along the body's segment.</param>
public readonly record struct WorldFaceCrossingClaim(string PlacementId, string FaceName, FixedQ4816 Parameter)
    : IComparable<WorldFaceCrossingClaim> {
    /// <inheritdoc/>
    public int CompareTo(WorldFaceCrossingClaim other) {
        var byParameter = Parameter.CompareTo(other: other.Parameter);

        if (byParameter != 0) {
            return byParameter;
        }

        var byPlacement = string.CompareOrdinal(
            strA: PlacementId,
            strB: other.PlacementId
        );

        return ((byPlacement != 0)
            ? byPlacement
            : string.CompareOrdinal(
                strA: FaceName,
                strB: other.FaceName
            )
        );
    }
    /// <summary>Determines whether this claim outranks <paramref name="other"/>.</summary>
    /// <param name="other">The claim to compare against.</param>
    /// <returns><see langword="true"/> when this claim wins.</returns>
    public bool Outranks(WorldFaceCrossingClaim other) => (CompareTo(other: other) < 0);
}

using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance.Queries;

namespace Puck.World.Client;

/// <summary>
/// The presentation query field: the static scene a camera's clearance sweep, a limb probe, a pick or an editor
/// pointer asks, as a union of a fixed part and an optional responding part. The fixed part holds what changes only with a
/// definition delivery (the solid placements that do not respond, the screens, the neighbours' strips); the responding
/// part holds the solid placements whose shown creation follows a response sweep, so a response rebuilds that small part
/// alone (<c>WorldFramePresenter.RebuildStaticField</c>). Each query asks every part with that part's own march and
/// answers the union: the nearer hit, any overlap, and the normal of the part nearer the point.
/// </summary>
public sealed class WorldQueryField {
    /// <summary>Initializes a new instance of the <see cref="WorldQueryField"/> class.</summary>
    /// <param name="fixedPart">The part that changes only with a definition delivery.</param>
    /// <param name="responding">The part a response sweep moves, or <see langword="null"/> when no responding solid row
    /// stands in the field.</param>
    /// <exception cref="ArgumentNullException"><paramref name="fixedPart"/> is <see langword="null"/>.</exception>
    public WorldQueryField(SdfFieldEvaluator fixedPart, SdfFieldEvaluator? responding = null) {
        ArgumentNullException.ThrowIfNull(argument: fixedPart);
        Fixed = fixedPart;
        Responding = responding;
    }

    /// <summary>Gets the part that changes only with a definition delivery.</summary>
    public SdfFieldEvaluator Fixed { get; }
    /// <summary>Gets the part a response sweep moves, or <see langword="null"/>.</summary>
    public SdfFieldEvaluator? Responding { get; }

    /// <summary>Casts a ray against the union (<see cref="IWorldQuery.Raycast"/>): the nearer of the parts' hits.</summary>
    /// <param name="origin">The ray's origin.</param>
    /// <param name="dir">The ray's direction.</param>
    /// <param name="maxDist">The farthest distance tested.</param>
    /// <param name="hit">The nearer hit, or default.</param>
    /// <returns>Whether any part was hit.</returns>
    public bool Raycast(FixedPosition origin, FixedVector3 dir, FixedQ4816 maxDist, out RayHit hit) {
        var found = Fixed.Raycast(dir: dir, hit: out hit, maxDist: maxDist, origin: origin);

        if ((Responding is { } responding) && responding.Raycast(dir: dir, hit: out var other, maxDist: (found ? hit.Distance : maxDist), origin: origin) && (!found || (other.Distance < hit.Distance))) {
            hit = other;
            found = true;
        }
        return found;
    }
    /// <summary>Sweeps a sphere against the union (<see cref="IWorldQuery.SphereCast"/>): the nearer of the parts' hits.</summary>
    /// <param name="origin">The sweep's origin.</param>
    /// <param name="dir">The sweep's direction.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <param name="maxDist">The farthest distance swept.</param>
    /// <param name="hit">The nearer hit, or default.</param>
    /// <returns>Whether any part was hit.</returns>
    public bool SphereCast(FixedPosition origin, FixedVector3 dir, FixedQ4816 radius, FixedQ4816 maxDist, out RayHit hit) {
        var found = Fixed.SphereCast(dir: dir, hit: out hit, maxDist: maxDist, origin: origin, radius: radius);

        if ((Responding is { } responding) && responding.SphereCast(dir: dir, hit: out var other, maxDist: (found ? hit.Distance : maxDist), origin: origin, radius: radius) && (!found || (other.Distance < hit.Distance))) {
            hit = other;
            found = true;
        }
        return found;
    }
    /// <summary>Tests whether a sphere overlaps any part (<see cref="IWorldQuery.Overlap"/>).</summary>
    /// <param name="center">The sphere's center.</param>
    /// <param name="radius">The sphere's radius.</param>
    /// <returns>Whether any part reports the sphere occupied.</returns>
    public bool Overlap(FixedPosition center, FixedQ4816 radius) =>
        (Fixed.Overlap(center: center, radius: radius) || ((Responding is { } responding) && responding.Overlap(center: center, radius: radius)));
    /// <summary>Reads the surface normal at a point from the part whose surface is nearer it.</summary>
    /// <param name="position">The point.</param>
    /// <param name="normal">The unit normal, or default.</param>
    /// <returns>Whether the nearer part answered a normal.</returns>
    public bool TrySurfaceNormal(FixedPosition position, out Vector3 normal) {
        if ((Responding is { } responding) && responding.TryDistance(distance: out var near, material: out _, position: position) &&
            (!Fixed.TryDistance(distance: out var far, material: out _, position: position) || (FixedQ4816.Abs(value: near) < FixedQ4816.Abs(value: far)))) {
            return responding.TrySurfaceNormal(normal: out normal, position: position);
        }
        return Fixed.TrySurfaceNormal(normal: out normal, position: position);
    }
}

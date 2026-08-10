using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// The one contact-resolution seam a grounded <see cref="WorldBody"/> solves its swept position against. The analytic
/// provider derives convex colliders; the field provider evaluates the compiled SDF with no integrator change.
/// </summary>
/// <remarks><see cref="TryUp"/> is the load-bearing member: a body needs an up axis while falling, not only while in
/// contact, so a planetoid walker reads its up here. The analytic provider returns constant <c>+Y</c>, which for
/// a flat ground plane <em>is</em> the up axis, so one grounded integrator covers both worlds with no branch.</remarks>
public interface IContactField {
    /// <summary>Gets the document-derived analytic collider census, including the placement contribution.</summary>
    WorldContactCensus Census { get; }

    /// <summary>Resolves a body's swept position and velocity to a legal, depenetrated state and reports whether the body
    /// is now standing (a contact normal whose up-alignment clears the compiled walkable-slope threshold) and the last
    /// resolved non-walkable contact's surface normal — a vertical wall pushes and reports its normal without ever
    /// setting <see cref="ContactResolution.Grounded"/>. Walkable pushes (the ground, a ramp) never write the
    /// obstruction normal: a standing body re-resolves its ground contact on every solver iteration, so tracking
    /// the unconditional last push would let a later ground re-resolve silently erase an earlier wall push from the
    /// same call.</summary>
    /// <param name="position">The body's foot point (in/out): the swept position on entry, the depenetrated position on
    /// return.</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into any resolved surface is removed.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <returns>The grounded verdict and the last resolved non-walkable contact normal (zero when nothing obstructed
    /// the body).</returns>
    ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes);

    /// <summary>Returns the world up axis at a position — the direction a grounded body's gravity opposes and a standing test
    /// aligns against. The analytic provider always answers constant <c>+Y</c>.</summary>
    /// <param name="position">The body's foot point.</param>
    /// <param name="up">The unit up axis on return.</param>
    /// <returns><see langword="true"/> when an up axis is available.</returns>
    bool TryUp(in FixedVector3 position, out FixedVector3 up);
}

/// <summary>The outcome of one <see cref="IContactField.Resolve"/> call — the grounded verdict every integrator
/// already consults, plus the last resolved non-walkable contact's surface normal. Read-back only:
/// <see cref="ObstructionNormal"/> feeds no integration and changes no simulation behavior — it is
/// <c>world.contacts</c>' obstruction witness, surfacing the fact that a body pushed against a wall (whose normal
/// fails the grounded alignment test).</summary>
/// <param name="Grounded"><see langword="true"/> when the body is standing on a walkable surface after resolution.</param>
/// <param name="ObstructionNormal">The last resolved non-walkable contact's unit surface normal this call, or
/// <see cref="FixedVector3.Zero"/> when nothing obstructed the body. A walkable push (the ground, a ramp) never
/// writes this — only a contact whose alignment fails the grounded test does.</param>
public readonly record struct ContactResolution(bool Grounded, FixedVector3 ObstructionNormal);

/// <summary>The analytic collider vocabulary's live document census.</summary>
/// <param name="SphereCount">All sphere colliders.</param>
/// <param name="BoxCount">All axis-aligned box colliders.</param>
/// <param name="PlaneCount">All half-space colliders.</param>
/// <param name="PlacementSphereCount">Sphere colliders derived from creation placements.</param>
/// <param name="PlacementBoxCount">Box colliders derived from creation placements.</param>
/// <param name="PlacementPlaneCount">Half-space colliders derived from creation placements.</param>
/// <param name="UnsupportedPlacementCount">Placement primitive copies with no analytic representation.</param>
public readonly record struct WorldContactCensus(
    long SphereCount,
    long BoxCount,
    long PlaneCount,
    long PlacementSphereCount,
    long PlacementBoxCount,
    long PlacementPlaneCount,
    long UnsupportedPlacementCount
) {
    /// <summary>Gets all analytic colliders.</summary>
    public long SolidCount => (SphereCount + BoxCount + PlaneCount);

    /// <summary>Gets all analytic colliders derived from placements.</summary>
    public long PlacementColliderCount => (PlacementSphereCount + PlacementBoxCount + PlacementPlaneCount);
}

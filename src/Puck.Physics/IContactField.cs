using Puck.Maths;

namespace Puck.Physics;

/// <summary>
/// The seam a grounded body solves its swept position against. An analytic provider derives convex colliders; a field
/// provider evaluates a scalar field. Neither changes the integrator.
/// </summary>
/// <remarks><see cref="TryUp"/> is the load-bearing member: a body needs an up axis while falling, not only while in
/// contact, so a walker on curved ground reads its up here. A provider over flat ground returns constant <c>+Y</c>,
/// which for that ground <em>is</em> the up axis, so one grounded integrator covers both with no branch.</remarks>
public interface IContactField {
    /// <summary>Resolves a body's swept position and velocity to a legal, depenetrated state and reports whether the
    /// body is now standing (a contact normal whose up-alignment clears the compiled walkable-slope threshold) and the
    /// last resolved non-walkable contact's surface normal — a vertical wall pushes and reports its normal without
    /// ever setting <see cref="ContactResolution.Grounded"/>. Walkable pushes (the ground, a ramp) never write the
    /// obstruction normal: a standing body re-resolves its ground contact on every solver iteration, so tracking the
    /// unconditional last push would let a later ground re-resolve silently erase an earlier wall push from the same
    /// call.</summary>
    /// <param name="position">The body's foot point (in/out): the swept position on entry, the depenetrated position
    /// on return.</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into any resolved surface is
    /// removed.</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis — the direction a contact normal's alignment is tested against to decide
    /// whether the body is standing. A world with a constant field passes <c>+Y</c>; one with a solved field passes the
    /// direction its gravity opposes.</param>
    /// <returns>The grounded verdict, the surface normal it grounded on, and the last resolved non-walkable contact
    /// normal (zero when nothing obstructed the body).</returns>
    ContactResolution Resolve(ref FixedVector3 position, ref FixedVector3 velocity, in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up);
    /// <summary>Resolves one integrated step from <paramref name="previousPosition"/> to
    /// <paramref name="position"/>. Providers whose endpoint solve is already sufficient inherit that behavior;
    /// fields may use the start point to recover the approached surface when the endpoint lies inside geometry.</summary>
    /// <param name="previousPosition">The body's foot point before the step.</param>
    /// <param name="position">The body's foot point (in/out).</param>
    /// <param name="velocity">The body's velocity (in/out).</param>
    /// <param name="orientation">The body's local-to-world orientation.</param>
    /// <param name="volumes">The body's compiled convex volumes.</param>
    /// <param name="up">The body's up axis.</param>
    /// <returns>The grounded verdict, the surface normal it grounded on, and the last resolved non-walkable contact normal.</returns>
    ContactResolution ResolveSweep(in FixedVector3 previousPosition, ref FixedVector3 position, ref FixedVector3 velocity,
        in FixedQuaternion orientation, ReadOnlySpan<FixedBodyColliderVolume> volumes, in FixedVector3 up) =>
        Resolve(
            orientation: in orientation,
            position: ref position,
            up: in up,
            velocity: ref velocity,
            volumes: volumes
        );
    /// <summary>Returns the world up axis at a position — the direction a grounded body's gravity opposes and a
    /// standing test aligns against.</summary>
    /// <param name="position">The body's foot point.</param>
    /// <param name="up">The unit up axis on return.</param>
    /// <returns><see langword="true"/> when an up axis is available.</returns>
    bool TryUp(in FixedVector3 position, out FixedVector3 up);
}
/// <summary>The outcome of one <see cref="IContactField.Resolve"/> call — the grounded verdict every integrator
/// consults, plus the last resolved non-walkable contact's surface normal. The normal is read-back only: it feeds no
/// integration and changes no simulation behavior.</summary>
/// <param name="Grounded"><see langword="true"/> when the body is standing on a walkable surface after resolution.</param>
/// <param name="ObstructionNormal">The last resolved non-walkable contact's unit surface normal this call, or
/// <see cref="FixedVector3.Zero"/> when nothing obstructed the body. A walkable push (the ground, a ramp) never
/// writes this — only a contact whose alignment fails the grounded test does.</param>
/// <param name="GroundNormal">The unit surface normal of the last WALKABLE push, or <see cref="FixedVector3.Zero"/>
/// when the body did not ground. A standing body's up is the surface it stands on, not the direction its gravity
/// pulls: the two differ wherever a floor is not perpendicular to the field, and walking the field's tangent instead
/// of the floor's would carry the body off a flat floor.</param>
public readonly record struct ContactResolution(bool Grounded, FixedVector3 ObstructionNormal, FixedVector3 GroundNormal = default);

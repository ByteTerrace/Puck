using Puck.Maths;

namespace Puck.World.Server;

/// <summary>
/// The one contact-resolution seam a grounded <see cref="WorldBody"/> solves its swept position against — R2's
/// synthesis of the two collision designs into a single provider interface. Arc 1 ships the analytic provider
/// (<see cref="WorldColliderSet"/>, document-derived convex colliders); Arc 2 adds an SDF provider behind the same seam
/// with no integrator change.
/// </summary>
/// <remarks><see cref="TryUp"/> is the load-bearing member: a body needs an up axis while FALLING, not only while in
/// contact, so a planetoid walker (Arc 2) reads its up here. The analytic provider returns constant <c>+Y</c>, which for
/// a flat ground plane <em>is</em> the up axis, so one grounded integrator covers both worlds with no branch.</remarks>
internal interface IContactField {
    /// <summary>Resolves a body's swept position and velocity to a legal, depenetrated state and reports whether the body
    /// is now STANDING (a contact normal whose up-alignment clears the compiled walkable-slope threshold).</summary>
    /// <param name="position">The body's foot point (in/out): the swept position on entry, the depenetrated position on
    /// return.</param>
    /// <param name="velocity">The body's velocity (in/out): the component driving into any resolved surface is removed.</param>
    /// <param name="radius">The body capsule radius.</param>
    /// <param name="height">The body capsule total height from the foot point.</param>
    /// <returns><see langword="true"/> when the body is standing on a walkable surface after resolution.</returns>
    bool Resolve(ref FixedVector3 position, ref FixedVector3 velocity, FixedQ4816 radius, FixedQ4816 height);

    /// <summary>The world up axis at a position — the direction a grounded body's gravity opposes and a standing test
    /// aligns against. The analytic provider always answers constant <c>+Y</c>.</summary>
    /// <param name="position">The body's foot point.</param>
    /// <param name="up">The unit up axis on return.</param>
    /// <returns><see langword="true"/> when an up axis is available.</returns>
    bool TryUp(in FixedVector3 position, out FixedVector3 up);
}

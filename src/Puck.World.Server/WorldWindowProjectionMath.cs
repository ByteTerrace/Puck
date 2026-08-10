using System.Numerics;

namespace Puck.World.Server;

/// <summary>
/// A face's derived aperture, converted to presentation float — the single-precision mirror of one
/// <see cref="WorldFaceFrame"/>, taken once at the fixed-point-to-float boundary <see cref="WorldFaceCatalog"/>'s own
/// remarks describe ("rendering converts a finished frame to single precision at its own boundary"). Carries only
/// what the window projection needs: the plane basis and the rectangle half-extents, never <c>HalfDepth</c> (a
/// window looks through the aperture, not at its thickness).
/// </summary>
/// <param name="Origin">The aperture rectangle's world-space center.</param>
/// <param name="Right">The unit Right axis.</param>
/// <param name="Up">The unit Up axis.</param>
/// <param name="Normal">The unit outward Normal (Right x Up convention).</param>
/// <param name="HalfWidth">The half-extent along <paramref name="Right"/>.</param>
/// <param name="HalfHeight">The half-extent along <paramref name="Up"/>.</param>
public readonly record struct WorldFaceGeometry(Vector3 Origin, Vector3 Right, Vector3 Up, Vector3 Normal, float HalfWidth, float HalfHeight) {
    /// <summary>Converts a derived <see cref="WorldFaceFrame"/> to its presentation-float mirror.</summary>
    public static WorldFaceGeometry FromFrame(WorldFaceFrame frame) => new(
        Origin: frame.Origin.ToVector3(),
        Right: frame.Right.ToVector3(),
        Up: frame.Up.ToVector3(),
        Normal: frame.Normal.ToVector3(),
        HalfWidth: (float)(double)frame.HalfWidth,
        HalfHeight: (float)(double)frame.HalfHeight
    );
}

/// <summary>
/// Presentation-float border-window math: maps a viewer's eye through a mapped border pair's isometry into the
/// destination side — the position half of the same isometry <see cref="WorldPortalArrivalMath"/> applies
/// fixed-point for a traveler's simulated arrival pose (a body that actually crosses the threshold). Deliberately a
/// separate, independently-derived copy: nothing here ever reaches simulation state, this type never reads or calls
/// <see cref="WorldPortalArrivalMath"/>, and the border-windows lane that authored it does not edit that file (see
/// its own task brief: "add your own projection math type rather than editing theirs"). Public — not internal to
/// this assembly — because <c>Puck.World.Client.WorldWindowFrustumFit</c> (the composition root, which also needs
/// <c>Puck.SdfVm.Views.SdfAsymmetricFrustum</c> and so cannot itself live in this project's narrower dependency
/// profile — see docs/project-map.md) is this type's one other caller, and <c>tests/Puck.World.Tests</c> — which
/// deliberately does not reference <c>Puck.World</c> at all — is where the correctness proof below lives
/// (<c>WorldWindowProjectionMathLawTests</c>). Widening the member across both callers rather than granting either
/// one <c>InternalsVisibleTo</c> follows CLAUDE.md's ruling directly.
/// </summary>
/// <remarks>
/// <para><b>It IS the arrival isometry — the full 180° door-to-door flip.</b> A window shows "what an arrived
/// viewer would see," rendered live without moving anyone: the same <c>(u,v,n) → (-u,v,-n)</c> flip
/// <see cref="WorldPortalArrivalMath.ComputeArrival"/> documents (walking out of the source face's outward normal
/// arrives walking in through the destination face), independently re-derived here over the full
/// <see cref="WorldFaceFrame"/> basis (Right/Up/Normal) rather than a bare placement yaw, because the face-frame
/// landing's whole point was that face geometry has exactly one derivation (<see cref="WorldFaceCatalog"/>).</para>
/// <para><b>Why the Right component must flip too — this is forced, not a choice.</b> A mapped border pair's two
/// apertures have opposite outward Normals by construction (each one's Normal points toward its own room's approach
/// side, and the two rooms sit on opposite sides of the seam). Under this type's Right×Up=Normal convention with a
/// shared Up (=world +Y, the yaw-only contract every mapped facet is validated against), an opposite Normal forces
/// an opposite (anti-parallel) world Right — there is no authored yaw pair for which the two apertures' Rights end
/// up parallel. A non-degenerate proof: source yaw -90° gives (Right=+Z, Normal=-X); a deltaYaw≡0 counterpart at yaw
/// +90° gives (Right=-Z, Normal=+X) — both flipped. Mapping eye (u,v,n)=(5,·,3) through the identity-border case
/// (destYaw-srcYaw+180°≡0°, so the true mapped eye is the same world point the traveler stood at) only reproduces
/// that point under the full <c>(-u,v,-n)</c> flip; a Normal-only flip <c>(u,v,-n)</c> lands at a mirrored world
/// point — this was a real reflection bug an earlier version of this file shipped with (every window rendered the
/// destination laterally mirrored, parallax moving the wrong way as the eye moved), caught by
/// <c>WorldWindowProjectionMathLawTests</c> cross-checking a hand-picked pair against
/// <see cref="WorldPortalArrivalMath.ComputeArrival"/>'s own answer for the same pair.</para>
/// <para><b>The algebra.</b> Decompose a world point's offset from the source aperture's origin along the source's
/// own orthonormal (Right, Up, Normal) basis: <c>(u, v, n) = (offset·Right, offset·Up, offset·Normal)</c>. The
/// mapped point is <c>destination.Origin - u·destination.Right + v·destination.Up - n·destination.Normal</c> — Up is
/// unaffected (it is the shared axis both apertures rotate about); Right and Normal both flip. The mapped eye lands
/// outside the destination room (on the side its own Normal faces away from), looking in — exactly where a window's
/// virtual eye belongs, and exactly why <c>WorldWindowFrustumFit.TryFitWindow</c> fits its off-axis frustum against
/// <c>-destination.Normal</c> rather than <c>+destination.Normal</c>.</para>
/// </remarks>
public static class WorldWindowProjectionMath {
    /// <summary>Maps a world-space point through the source aperture's frame into the destination aperture's frame
    /// under the full 180° door-to-door flip — see this type's own remarks for why both Right and Normal flip.</summary>
    /// <param name="point">The point to map, in source-world space.</param>
    /// <param name="source">The source face's own aperture geometry.</param>
    /// <param name="destination">The destination counterpart face's own aperture geometry.</param>
    /// <returns>The mapped point, in destination-world space.</returns>
    public static Vector3 MapPoint(Vector3 point, WorldFaceGeometry source, WorldFaceGeometry destination) {
        var offset = (point - source.Origin);
        var u = Vector3.Dot(vector1: offset, vector2: source.Right);
        var v = Vector3.Dot(vector1: offset, vector2: source.Up);
        var n = Vector3.Dot(vector1: offset, vector2: source.Normal);

        return (destination.Origin - (u * destination.Right) + (v * destination.Up) - (n * destination.Normal));
    }
}

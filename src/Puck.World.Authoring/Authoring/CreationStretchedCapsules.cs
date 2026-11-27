using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>
/// The capsules of a creation whose X and Z scales differ (<see cref="SdfSolidGeometry.IsStretchedCapsule"/>): the
/// shapes a creation author meant as an elliptic section with round ends, which the engine draws as the unit capsule
/// under a generic Scale instead.
/// </summary>
public static class CreationStretchedCapsules {
    /// <summary>Returns the shapes of a creation that are stretched capsules, in shape order.</summary>
    /// <param name="document">The creation.</param>
    /// <returns>The shapes' indices.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<int> Of(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var shapes = (document.Shapes ?? []);
        var stretched = new List<int>();

        for (var index = 0; (index < shapes.Count); index++) {
            // A scale bound to state has no value to compare until a host resolves it.
            if (
                (shapes[index] is { Type: SdfSolidPrimitive.Capsule, Scale: { Reference: null } scale }) &&
                SdfSolidGeometry.IsStretchedCapsule(scale: scale, type: SdfSolidPrimitive.Capsule)
            ) {
                stretched.Add(item: index);
            }
        }

        return stretched;
    }
    /// <summary>Describes a stretched capsule (<see cref="Of"/>) for a diagnostic: the shape by name, why its ends are not
    /// round, and the form that keeps them.</summary>
    /// <param name="document">The creation.</param>
    /// <param name="shape">The shape's index, one <see cref="Of"/> returned.</param>
    /// <returns>One sentence, starting lowercase so a caller can lead it with the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static string Describe(CreationDocument document, int shape) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var scale = ((System.Numerics.Vector3)document.Shapes![shape].Scale);
        var half = SdfSolidGeometry.CapsuleHalfSegment(scaleY: scale.Y);

        return $"scales Capsule '{CreationMotion.NameOf(document: document, index: shape)}' {scale.X:G6} by {scale.Z:G6} in x and z: only equal x and z keep a capsule's ends round, so the unit capsule rides a generic Scale that stretches its ends along every axis, y included, and reads a bound rather than the distance; for an elliptic section with round ends write an Ellipsoid at the capsule's segment midpoint ({half:G6} above its position along its own y) with elongate [0, {half:G6}, 0] and the radii you want, or make x and z equal.";
    }
}

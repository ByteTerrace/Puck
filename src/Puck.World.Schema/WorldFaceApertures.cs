using System.Collections.Frozen;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>How one solid primitive's face opens into the region a swept body is tested against.</summary>
/// <param name="Primitive">The solid primitive this recipe is keyed by.</param>
/// <param name="Open">Builds the region from the face's derived frame and the document's crossing floor.</param>
public sealed record WorldFaceApertureRecipe(SdfSolidPrimitive Primitive, Func<WorldFaceFrame, FixedQ4816, WorldFaceAperture> Open);
/// <summary>
/// The one declaration of which solid primitives open a walkable aperture and how — keyed by the face's named shape
/// kind. A primitive absent from the table draws its face and opens nothing, so a portal facet on it is refused by
/// name at validation rather than silently walked through a region nobody authored.
/// </summary>
/// <remarks>
/// <para>Both consumers read the recipe the derivation already resolved onto <see cref="WorldFaceRow.Aperture"/> —
/// <c>WorldDefinitionValidator</c> asks whether one exists, <c>WorldFacePortalPolicy.TryAperture</c> calls it — so a
/// new primitive is one entry here and nothing else.</para>
/// <para>Aperture-ness is a fact about the shape, not an authored field: the face names a shape, and whether that
/// shape's surface bounds a region is decided by its primitive kind. There is no document member to author it with,
/// and adding one would let a world declare a walkable aperture on a surface the region test cannot express.</para>
/// </remarks>
public static class WorldFaceApertures {
    private static readonly FrozenDictionary<SdfSolidPrimitive, WorldFaceApertureRecipe> ByPrimitive =
        new Dictionary<SdfSolidPrimitive, WorldFaceApertureRecipe> {
            // The one-sided slab: the drawn face's own frame extruded along its normal, never thinner than one step
            // of the fastest declared travel (the crossing floor).
            [SdfSolidPrimitive.Box] = new WorldFaceApertureRecipe(
                Open: static (frame, crossingFloor) => new WorldFaceAperture.Box(
                    Depth: FixedQ4816.Max(
                        x: frame.HalfDepth,
                        y: crossingFloor
                    ),
                    Frame: frame
                ),
                Primitive: SdfSolidPrimitive.Box
            ),
        }.ToFrozenDictionary();

    /// <summary>Gets the aperture recipe a face's named shape kind opens, or <see langword="null"/> when it opens
    /// none — including a face that names no shape at all.</summary>
    /// <param name="primitive">The named shape's primitive kind, or <see langword="null"/> when the face names no
    /// shape.</param>
    /// <returns>The recipe, or <see langword="null"/>.</returns>
    public static WorldFaceApertureRecipe? For(SdfSolidPrimitive? primitive) => ((primitive is { } kind)
        ? (ByPrimitive.TryGetValue(
            key: kind,
            value: out var recipe
        )
            ? recipe
            : null
        )
        : null
    );
}

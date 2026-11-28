using System.Text.Json.Serialization;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>
/// How a creation darkens what stands about it (<see cref="WorldPrototype.Occlusion"/>): an analytic occluder
/// (<see cref="SdfOccluder"/>), which a body takes from its kit's collider where that is one sphere, capsule or box and
/// a placement fits to the box the creation's shapes fill at rest, which takes its share of the ambient and
/// indirect light of the surfaces about it and casts a soft shadow toward every ranged light, screen and emitter face,
/// never toward a directional light. Every placement showing the creation and every body wearing it as a look carries
/// it, at its scale, and it moves as they move. The creation's own surfaces on a moving body are neither darkened nor
/// shadowed by it. An absent member takes its engine default; the standard package chooses the values its creations
/// cast with.
/// </summary>
/// <param name="Strength">The share, in [0, 1], of the ambient and indirect light the occluder takes away where it
/// covers a surface's whole hemisphere; one by default.</param>
/// <param name="Shadow">The share, in [0, 1], of a ranged light, screen or emitter face its shadow takes away where it
/// covers the light whole; <paramref name="Strength"/> by default.</param>
/// <param name="Reach">How far past the occluder's bounding sphere, in the creation's units, it still darkens the ambient
/// and indirect light, eased to nothing there; the bounding sphere's own radius by default. It bounds only that
/// darkening: the shadow toward a light reaches as far as the light does.</param>
/// <param name="Softness">The penumbra's half slope toward a light of no size (a point or spot light): its shadow's edge
/// widens by this much for each unit from the occluder; zero by default, a point light's hard edge.</param>
/// <param name="Shape">Which solid is fitted to the rest box: a sphere, a capsule along the long axis, the box itself, or
/// by default the one the box's proportions suggest. A body whose kit's collider stands for it casts that collider's
/// solid whatever this says.</param>
public sealed record WorldPrototypeOcclusion(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Strength = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Shadow = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Reach = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Softness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfOccluderFit? Shape = null
) {
    /// <summary>Returns a fitted solid (<see cref="SdfOccluder.Fit"/>) with this occlusion's strengths, reach and softness,
    /// in the creation's frame and units.</summary>
    /// <param name="fitted">The solid fitted to the creation's rest box.</param>
    /// <returns>The occluder.</returns>
    public SdfOccluder Apply(SdfOccluder fitted) {
        var bound = fitted.BoundingRadius;
        var strength = (Strength ?? 1f);

        return (fitted with {
            Reach = (bound + MathF.Max(x: (Reach ?? bound), y: 0f)),
            Shadow = (Shadow ?? strength),
            Softness = MathF.Max(x: (Softness ?? 0f), y: 0f),
            Strength = strength,
        });
    }
}

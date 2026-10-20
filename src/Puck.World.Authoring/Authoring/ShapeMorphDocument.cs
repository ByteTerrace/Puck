using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of <see cref="SdfProgramBuilder.PushFieldMorph"/>'s parameters, carried by a shape whose
/// <see cref="ShapeDocument.Blend"/> is <see cref="SdfBlendOp.Morph"/>: the shape's own scope composes into the field
/// before it by linear interpolation, the riding dynamic slot's <see cref="Lane"/> running the blend from the field
/// (lane at <see cref="From"/>) to the shape (lane at <see cref="To"/>). A shape under no dynamic slot (a static
/// placement) reads lane 0.</summary>
/// <param name="Lane">Which dynamic-slot lane drives the morph, in [0, 3].</param>
/// <param name="From">The lane value where the field before the shape shows alone (t = 0).</param>
/// <param name="To">The lane value where the shape shows alone (t = 1); must differ from <see cref="From"/>.</param>
public sealed record ShapeMorphDocument(int Lane, float From, float To);
/// <summary>Opens a shape's own field scope with the compose its blend names: the pop-only blends
/// (<see cref="SdfBlendOp.Morph"/>, <see cref="SdfBlendOp.StairsUnion"/>, <see cref="SdfBlendOp.StairsSubtraction"/>)
/// take their dedicated openers, and every other blend the plain <see cref="SdfProgramBuilder.PushField"/>. The one
/// place both emission paths read a shape's blend into a scope.</summary>
public static class ShapeScope {
    /// <summary>The step count a stairs blend takes when the shape authors none.</summary>
    public const int DefaultSteps = 4;
    /// <summary>The most steps a stairs blend authors.</summary>
    public const int MaxSteps = 64;

    /// <summary>Returns whether <paramref name="blend"/> composes only at a scope's pop, so a shape carrying it always
    /// opens its own scope.</summary>
    /// <param name="blend">The blend.</param>
    /// <returns><see langword="true"/> for the morph and stairs blends.</returns>
    public static bool IsPopOnly(SdfBlendOp blend) => (blend is SdfBlendOp.Morph or SdfBlendOp.StairsUnion or SdfBlendOp.StairsSubtraction);
    /// <summary>Opens the scope a shape's blend composes through.</summary>
    /// <param name="chain">The builder chain.</param>
    /// <param name="blend">The shape's blend.</param>
    /// <param name="smooth">The blend radius, in the units the accumulator reads (world units); a stairs blend's
    /// radius.</param>
    /// <param name="steps">The stairs blend's step count (null = <see cref="DefaultSteps"/>).</param>
    /// <param name="morph">The morph blend's parameters; a morph blend without them reads lane 0 from -1 to 1.</param>
    /// <returns><paramref name="chain"/>, for further chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="chain"/> is <see langword="null"/>.</exception>
    public static SdfProgramBuilder Open(SdfProgramBuilder chain, SdfBlendOp blend, float smooth, int? steps = null, ShapeMorphDocument? morph = null) {
        ArgumentNullException.ThrowIfNull(chain);

        return blend switch {
            SdfBlendOp.Morph => chain.PushFieldMorph(from: (morph?.From ?? -1f), laneIndex: (morph?.Lane ?? 0), to: (morph?.To ?? 1f)),
            SdfBlendOp.StairsUnion => chain.PushFieldStairs(radius: smooth, steps: Math.Clamp(value: (steps ?? DefaultSteps), min: 1, max: MaxSteps)),
            SdfBlendOp.StairsSubtraction => chain.PushFieldStairs(radius: smooth, steps: Math.Clamp(value: (steps ?? DefaultSteps), min: 1, max: MaxSteps), subtraction: true),
            _ => chain.PushField(compose: blend, smooth: smooth),
        };
    }
}

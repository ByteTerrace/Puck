using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>
/// The glyphs a static instance holds (<see cref="SdfProgramBuilder.Text"/>): the world-axis box their cells span, each
/// cell's rectangle extruded by its slab, and the spacing their strokes ask of the mesh bake (<see cref="Detail"/>). A
/// glyph's field is its atlas distance only within half the atlas's distance range of an edge and a lower bound past it,
/// so the bake refines every cell of the box the surface can enter, at that spacing
/// (<see cref="Baking.SdfLatticeRegion.Bound"/>).
/// </summary>
/// <param name="Low">The box's lowest corner, in world units.</param>
/// <param name="High">The box's highest corner, in world units.</param>
/// <param name="Detail">The finest spacing any of the glyphs asks, in world units (<see cref="SdfProgramBuilder.GlyphDetail"/>).</param>
public readonly record struct SdfGlyphSpan(Vector3 Low, Vector3 High, float Detail) {
    /// <summary>Returns the span holding both spans' boxes, at the finer of their details.</summary>
    /// <param name="other">The other span.</param>
    /// <returns>The joined span.</returns>
    public SdfGlyphSpan Join(SdfGlyphSpan other) => new(Detail: MathF.Min(x: Detail, y: other.Detail), High: Vector3.Max(value1: High, value2: other.High), Low: Vector3.Min(value1: Low, value2: other.Low));
}

using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.Maths;

namespace Puck.World;

/// <summary>
/// A pointer surface: a rectangle in a placement's frame that a seat's pointer ray maps onto, the way a
/// <c>Simulation</c> screen's face maps one, so a creation's face (a console's glass, a panel of buttons) answers a
/// pointer in fixed point from document data alone. The rectangle rides the row's resolved frame: its
/// <see cref="Origin"/>, <see cref="Right"/> and <see cref="Up"/> are authored in the placement's own frame, turned by
/// its yaw, and its lengths scale with its scale, so a surface authored in a creation's units lies on that creation's
/// face wherever the row stands. A hit is the source-normalized point the ray meets, <c>x</c> right and <c>y</c> down,
/// each in <c>[0, 1)</c> on the surface, and it is hit-tested against the surface's named <see cref="Regions"/>.
/// <para>A rule reads the pointer through <c>pointer(&lt;seat&gt;|any, &lt;placement&gt;, &lt;facet&gt;)</c>
/// (<see cref="WorldRuleFacts.PointerPrefix"/>): <c>x</c>, <c>y</c>, <c>on</c>, <c>press</c> and a channel,
/// <c>region</c> and a region's id, or <c>row</c> and a region's id. A participant points at a surface while it holds
/// <c>Control</c> over the placement. Presentation draws nothing for a surface; the creation the row shows is what a
/// player sees there.</para>
/// </summary>
/// <param name="Origin">The rectangle's centre in the placement's frame, in the frame's units.</param>
/// <param name="Right">The unit axis, in the placement's frame, that <c>x</c> increases along.</param>
/// <param name="Up">The unit axis, in the placement's frame, that <c>y</c> decreases along; orthogonal to
/// <paramref name="Right"/>. The surface faces along their cross product.</param>
/// <param name="HalfWidth">The rectangle's half-width along <paramref name="Right"/>, in the frame's units; positive.</param>
/// <param name="HalfHeight">The rectangle's half-height along <paramref name="Up"/>, in the frame's units; positive.</param>
/// <param name="Regions">The named rectangles of the surface a rule hit-tests, or <see langword="null"/> for none.
/// Omitted from the wire when null.</param>
public sealed record WorldPointerSurface(
    DocumentVector3 Origin,
    DocumentVector3 Right,
    DocumentVector3 Up,
    float HalfWidth,
    float HalfHeight,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldPointerRegion>? Regions = null
) {
    /// <summary>Returns the mapping a pointer ray takes onto the surface where a placement's resolved frame puts it:
    /// a one-by-one source, so a hit's coordinate is the source-normalized fraction of the surface.</summary>
    /// <param name="frame">The placement's resolved frame.</param>
    /// <param name="placementId">The placement's id, which names the mapping's source.</param>
    /// <returns>The mapping, whose destination is <see cref="SourceDestination.Simulation"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="placementId"/> is <see langword="null"/>.</exception>
    public SourceMapping MappingIn(CompiledPlacementFrame frame, string placementId) {
        ArgumentNullException.ThrowIfNull(argument: placementId);

        var rotation = frame.FixedRotation;
        var scale = frame.FixedScale;

        return new SourceMapping(
            Crop: SourcePixelRect.Whole(height: 1, width: 1),
            Destination: SourceDestination.Simulation,
            Filter: Puck.Abstractions.Gpu.GpuSamplerFilter.Nearest,
            Opener: SourceOpener.Document,
            Placement: new SourcePlacement.Surface(
                HalfHeight: (float)((double)(FixedQ4816.FromDouble(value: HalfHeight) * scale)),
                HalfWidth: (float)((double)(FixedQ4816.FromDouble(value: HalfWidth) * scale)),
                Origin: (frame.FixedPosition + rotation.Rotate(vector: (FixedVector3.FromVector3(value: Origin) * scale))).ToVector3(),
                Right: rotation.Rotate(vector: FixedVector3.FromVector3(value: Right)).ToVector3(),
                Up: rotation.Rotate(vector: FixedVector3.FromVector3(value: Up)).ToVector3()
            ),
            Source: SourceHandle.Producer(name: $"placements[{placementId}].pointer"),
            SourceHeight: 1,
            SourceWidth: 1
        );
    }
}
/// <summary>
/// One named rectangle of a pointer surface (<see cref="WorldPointerSurface.Regions"/>) or of a <c>Simulation</c>
/// screen's face (<see cref="WorldScreenRoute.Regions"/>), in the source-normalized coordinates a pointer hit reads:
/// <c>x</c> right and <c>y</c> down, each in <c>[0, 1]</c> across the face. A region with <see cref="Rows"/> is a list:
/// a row read answers which of its equal rows, counted from the top, the hit lies in.
/// </summary>
/// <param name="Id">The region's id, unique on its surface; a rule names the region by it.</param>
/// <param name="Left">The rectangle's left edge, a fraction of the face's width.</param>
/// <param name="Top">The rectangle's top edge, a fraction of the face's height.</param>
/// <param name="Right">The rectangle's right edge; greater than <paramref name="Left"/>.</param>
/// <param name="Bottom">The rectangle's bottom edge; greater than <paramref name="Top"/>.</param>
/// <param name="Rows">The number of equal rows the region divides into from top to bottom, or <see langword="null"/>
/// for one. Omitted from the wire when null.</param>
public sealed record WorldPointerRegion(
    [property: DocumentText("a pointer region name")] string Id,
    float Left,
    float Top,
    float Right,
    float Bottom,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Rows = null
) {
    /// <summary>Returns which row of the region a source-normalized point lies in, counted from the top.</summary>
    /// <param name="point">The point, <c>x</c> right and <c>y</c> down.</param>
    /// <returns>The row, <c>0</c> through <see cref="Rows"/> minus one; <c>-1</c> when the point lies outside the
    /// region. A region with no rows answers <c>0</c> inside it.</returns>
    public long RowOf(FixedVector2 point) {
        var left = FixedQ4816.FromDouble(value: Left);
        var top = FixedQ4816.FromDouble(value: Top);
        var right = FixedQ4816.FromDouble(value: Right);
        var bottom = FixedQ4816.FromDouble(value: Bottom);

        if ((point.X < left) || (point.X >= right) || (point.Y < top) || (point.Y >= bottom)) {
            return -1L;
        }

        var rows = Math.Max(val1: (Rows ?? 1), val2: 1);
        // The point lies at or below the top, so the whole part is the floor.
        var row = ((((point.Y - top) * FixedQ4816.FromInteger(value: rows)) / (bottom - top)).Value >> FixedQ4816.FractionBitCount);

        return Math.Clamp(value: row, min: 0L, max: (rows - 1L));
    }
    /// <summary>Checks a surface's regions, adding one line to <paramref name="errors"/> for each refusal: a missing or
    /// repeated id, an edge outside <c>[0, 1]</c> or not finite, an empty rectangle, or fewer than one row.</summary>
    /// <param name="regions">The regions, or <see langword="null"/> for none.</param>
    /// <param name="path">The regions' document path, which every refusal starts with.</param>
    /// <param name="errors">The refusals.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    public static void Validate(IReadOnlyList<WorldPointerRegion>? regions, string path, List<string> errors) {
        ArgumentNullException.ThrowIfNull(argument: errors);

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < (regions?.Count ?? 0)); index++) {
            var region = regions![index];
            var regionPath = $"{path}[{index}]";

            if (region is null) {
                errors.Add(item: $"{regionPath} is required.");

                continue;
            }
            if (string.IsNullOrEmpty(value: region.Id) || !ids.Add(item: region.Id)) {
                errors.Add(item: $"{regionPath}.id must name the region once on its surface.");
            }
            if (
                !float.IsFinite(f: region.Left) || !float.IsFinite(f: region.Top) || !float.IsFinite(f: region.Right) || !float.IsFinite(f: region.Bottom) ||
                (region.Left < 0f) || (region.Top < 0f) || (region.Right > 1f) || (region.Bottom > 1f) ||
                (region.Right <= region.Left) || (region.Bottom <= region.Top)
            ) {
                errors.Add(item: $"{regionPath} must lie within [0, 1] across the face, its right past its left and its bottom past its top.");
            }
            if (region.Rows is < 1) {
                errors.Add(item: $"{regionPath}.rows must be at least 1.");
            }
        }
    }
}

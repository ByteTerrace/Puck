using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The authored form of <see cref="SdfProgramBuilder.LaneErode"/> — per-shape lane-driven erosion, the
/// diegetic "the body IS the health bar" carrier: reads the riding dynamic slot's <see cref="Lane"/> component and,
/// as it rises from <see cref="From"/> to <see cref="To"/>, dilates the shape inward — ragged-fronted by noise —
/// until it is skipped entirely. Admitted on every primitive (<see cref="ShapeDocument.Erode"/>); applies to the
/// shape's own local point, immediately before its own primitive emission (whatever <see cref="ShapeDocument.Twist"/>/
/// <see cref="ShapeDocument.Bend"/>/<see cref="ShapeDocument.Flare"/>/<see cref="ShapeDocument.Shear"/>/
/// <see cref="ShapeDocument.Bumps"/> the shape's own chain still applies land on the point as usual). A shape under
/// no dynamic slot (a static placement, or a dynamic one with no bound <c>lanes</c> expression) reads lane 0.
/// Render-only: <see cref="SdfOp.LaneErode"/>'s remarks list the field-contact exclusion.</summary>
/// <param name="Lane">Which dynamic-slot lane drives the erosion.</param>
/// <param name="From">The lane value where erosion begins (t = 0).</param>
/// <param name="To">The lane value where the shape is fully eroded (t = 1); may be less than <see cref="From"/> to
/// run the fold in reverse (the shape grows IN as the lane rises — the torn-fabric use). Must differ from
/// <see cref="From"/>.</param>
/// <param name="Noise">The erosion front's noise lattice frequency, cells per world unit (null = 1).</param>
public sealed record ShapeErodeDocument(int Lane, float From, float To, float? Noise = null);

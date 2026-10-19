using System.Numerics;

namespace Puck.SignedDistance;

public sealed partial class SdfProgramBuilder {
    private readonly List<SdfCompiledPath> m_paths = [];

    private int m_reservedPathWords;

    /// <summary>Reserves auxiliary table capacity for a construction probe without manufacturing thousands of
    /// artificial contours. Each possible path spends at most 128 edges times two uint4 words. This affects
    /// SdfProgram.PartCompilationWordCapacity only, never the executable instruction or table stream.</summary>
    public void ReservePathTables(int shapeCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(shapeCount);
        m_reservedPathWords = checked((m_reservedPathWords + ((shapeCount * SdfPathProfile.MaxEdges) * 8)));
    }
    /// <summary>Emits one bounded, render-only path extrusion. XY scale places the finished profile's edges, so a fill
    /// keeps its exact distance at any scale; a stroke stays a run of round disks, its radii scaled by the geometric mean
    /// of the two factors (the factor itself when they agree). Depth is independent. Flattening happens once, never
    /// during a ray query. Invalid geometry or an exceeded subdivision budget throws ArgumentException. No field
    /// inflation hides approximation error.</summary>
    public SdfProgramBuilder Path(SdfPathProfile profile, Vector2 scale, float halfDepth, int material,
        SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false) {
        ArgumentNullException.ThrowIfNull(profile);
        RequirePositive(scale.X, nameof(scale), "Path X scale");
        RequirePositive(scale.Y, nameof(scale), "Path Y scale");
        RequirePositive(halfDepth, nameof(halfDepth), "Path half-depth");

        var radiusScale = MathF.Sqrt(x: (scale.X * scale.Y));
        var edges = profile.Compile();
        var reach = 0f;

        for (var index = 0; (index < edges.Length); index++) {
            var edge = edges[index];

            edges[index] = new SdfPathEdge(A: (edge.A * scale), B: (edge.B * scale), RadiusA: (edge.RadiusA * radiusScale), RadiusB: (edge.RadiusB * radiusScale));
            reach = MathF.Max(x: reach, y: MathF.Max(x: (edges[index].A.Length() + edges[index].RadiusA), y: (edges[index].B.Length() + edges[index].RadiusB)));
        }
        m_paths.Add(item: new(m_instructions.Count, edges));
        return Shape(dimensions: new Vector4(0f, edges.Length, reach, halfDepth), material: material,
            shape: SdfShapeType.Path, blend: blend, smooth: smooth, detail: detail,
            derived1: ((profile.Stroke is null) ? 0f : 1f));
    }
}

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
    /// <summary>Emits one bounded, render-only path extrusion. XY scale stretches the finished profile, including
    /// its stroke; depth is independent. Flattening happens once, never during a ray query. Invalid geometry or an
    /// exceeded subdivision budget throws ArgumentException. No field inflation hides approximation error.</summary>
    public SdfProgramBuilder Path(SdfPathProfile profile, Vector2 scale, float halfDepth, int material,
        SdfBlendOp blend = SdfBlendOp.Union, float smooth = 0f, bool detail = false) {
        ArgumentNullException.ThrowIfNull(profile);
        RequirePositive(scale.X, nameof(scale), "Path X scale");
        RequirePositive(scale.Y, nameof(scale), "Path Y scale");
        RequirePositive(halfDepth, nameof(halfDepth), "Path half-depth");
        // Keep circular stroke cross-sections in profile space. The shared scale op supplies the conservative
        // min-axis distance correction for anisotropic XY, without changing the authored zero set.
        var edges = profile.Compile();
        var reach = 0f;

        foreach (var edge in edges) {
            reach = MathF.Max(x: reach, y: MathF.Max(x: (edge.A.Length() + edge.RadiusA), y: (edge.B.Length() + edge.RadiusB)));
        }
        // Scale Z by the smaller XY factor too, and compensate the authored depth. A uniformly resized
        // outline then keeps an exact distance in Z instead of needlessly shortening every frontal ray step.
        var depthScale = MathF.Min(x: scale.X, y: scale.Y);
        var localDepth = (halfDepth / depthScale);

        RequirePositive(localDepth, nameof(halfDepth), "Scaled path half-depth");
        Scale(scale: new Vector3(value: scale, z: depthScale));
        m_paths.Add(item: new(m_instructions.Count, edges));
        return Shape(dimensions: new Vector4(0f, edges.Length, reach, localDepth), material: material,
            shape: SdfShapeType.Path, blend: blend, smooth: smooth, detail: detail,
            derived1: ((profile.Stroke is null) ? 0f : 1f));
    }
}

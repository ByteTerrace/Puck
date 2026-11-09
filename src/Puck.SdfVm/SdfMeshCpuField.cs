using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;
using Puck.SignedDistance.Queries;
namespace Puck.SdfVm;
/// <summary>The shared CPU render-field sampler for offline contours and rigid seam fallback.</summary>
public sealed class SdfMeshCpuField : ISdfMeshLevelField, IDisposable {
    /// <summary>The minimum gradient spacing in metres, outside fixed-point roundoff.</summary>
    public const float MinimumSpacing = 0.0005f;

    /// <summary>Reads one value and material, with a positive far value outside the evaluation frame.</summary>
    public static float Sample(SdfFieldEvaluator field, Vector3 point, out int material) => (field.TryDistance(FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point)), out var distance, out material) ? (float)((double)distance) : 1000f);
    /// <summary>Pairs an evaluator with its distance reader once per worker.</summary>
    public static (SdfFieldEvaluator Field, Func<Vector3, float> Read) Tapped(SdfFieldEvaluator field) => (field, point => Sample(field: field, material: out _, point: point));

    private const int Serial = 256;

    private readonly ThreadLocal<(SdfFieldEvaluator Field, Func<Vector3, float> Read)> m_evaluators;

    public SdfMeshCpuField(SdfProgram program) {

        m_evaluators = new(valueFactory: () => SdfMeshCpuField.Tapped(field: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render)));
        StepScale = ((float)((double)m_evaluators.Value.Field.StepScale));
    }

    public float StepScale { get; }

    public void Dispose() => m_evaluators.Dispose();
    /// <inheritdoc/>
    public void Values(ReadOnlySpan<Vector3> points, Span<float> values, Span<int> materials, SdfMeshLevelWork work) => Read(gradients: default, materials: materials, points: points, spacing: 0f, values: values);
    /// <inheritdoc/>
    public void Gradients(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<Vector3> gradients, SdfMeshLevelWork work) => Read(points, MathF.Max(x: MinimumSpacing, y: spacing), values, default, gradients);

    private void Read(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<int> materials, Span<Vector3> gradients) {
        var at = points.ToArray();
        var answers = new float[at.Length];
        var found = (materials.IsEmpty ? [] : new int[at.Length]);
        var slopes = (gradients.IsEmpty ? [] : new Vector3[at.Length]);

        void One(int index) {
            var (field, read) = m_evaluators.Value;
            answers[index] = Sample(field, at[index], out var material);
            if (found.Length > 0) { found[index] = material; }
            if (slopes.Length > 0) { slopes[index] = SdfMeshBakeTaps.Gradient(field: read, point: at[index], spacing: spacing); }
        }
        if (at.Length < Serial) {
            for (var index = 0; (index < at.Length); index++) { One(index: index); }
        } else {
            _ = Parallel.For(0, at.Length, One);
        }
        answers.CopyTo(destination: values);
        found.CopyTo(destination: materials);
        slopes.CopyTo(destination: gradients);
    }
}

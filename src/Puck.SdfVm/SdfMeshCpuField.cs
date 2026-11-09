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
        public static float Sample(SdfFieldEvaluator field, Vector3 point, out int material) => field.TryDistance(FixedPosition.FromLocal(FixedVector3.FromVector3(point)), out var distance, out material) ? (float)(double)distance : 1000f;
        /// <summary>Pairs an evaluator with its distance reader once per worker.</summary>
        public static (SdfFieldEvaluator Field, Func<Vector3, float> Read) Tapped(SdfFieldEvaluator field) => (field, point => Sample(field, point, out _));
        private const int Serial = 256;

        private readonly ThreadLocal<(SdfFieldEvaluator Field, Func<Vector3, float> Read)> m_evaluators;


        public SdfMeshCpuField(SdfProgram program) {

            m_evaluators = new(valueFactory: () => SdfMeshCpuField.Tapped(field: new SdfFieldEvaluator(program: program, reading: SdfFieldReading.Render)));
            StepScale = (float)(double)m_evaluators.Value.Field.StepScale;
        }

        public float StepScale { get; }

        public void Dispose() => m_evaluators.Dispose();

        public void Values(ReadOnlySpan<Vector3> points, Span<float> values, Span<int> materials, SdfMeshLevelWork work) {

            var at = points.ToArray();
            var answers = new float[at.Length];
            var found = new int[at.Length];

            void One(int index) {
                answers[index] = SdfMeshCpuField.Sample(field: m_evaluators.Value.Field, material: out found[index], point: at[index]);
            }

            if (at.Length < Serial) {
                for (var index = 0; (index < at.Length); index++) { One(index: index); }
            } else {
                _ = Parallel.For(fromInclusive: 0, toExclusive: at.Length, body: One);
            }
            answers.CopyTo(destination: values);
            found.CopyTo(destination: materials);

        }

        public void Gradients(ReadOnlySpan<Vector3> points, float spacing, Span<float> values, Span<Vector3> gradients, SdfMeshLevelWork work) {

            var at = points.ToArray();
            var answers = new float[at.Length];
            var slopes = new Vector3[at.Length];
            var step = MathF.Max(x: SdfMeshCpuField.MinimumSpacing, y: spacing);

            void One(int index) {
                var (field, read) = m_evaluators.Value;

                answers[index] = SdfMeshCpuField.Sample(field: field, material: out _, point: at[index]);
                slopes[index] = SdfMeshBakeTaps.Gradient(field: read, point: at[index], spacing: step);
            }

            if (at.Length < Serial) {
                for (var index = 0; (index < at.Length); index++) { One(index: index); }
            } else {
                _ = Parallel.For(fromInclusive: 0, toExclusive: at.Length, body: One);
            }
            answers.CopyTo(destination: values);
            slopes.CopyTo(destination: gradients);
            // A gradient sample is its value and four taps, as the bake counts a gradient lattice's taps.

        }
    }

using Puck.Abstractions.Counting;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>Reuses exact box answers from one immutable compiled field. A contact provider owns one cache for its
/// field revision; mutable fields, including lattice columns, remain outside it.</summary>
/// <param name="field">The immutable program whose bounds are reused.</param>
/// <remarks>Only identical boxes share an answer. Sweep proposals, query budgets, refusals and reached fractions
/// therefore stay unchanged. Storage is bounded and allocated once; replacement follows query order, never time.
/// Concurrent callers are serialized, while deterministic work counts describe an ordered query stream.</remarks>
public sealed class SdfBoundsCache(SdfFieldEvaluator field) : IFieldBounds {
    /// <summary>Gets the kind counting boxes requested from immutable fields.</summary>
    public static WorkKind Queries { get; } = new(name: "sdf.bounds.queries", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting cache misses that evaluate the field.</summary>
    public static WorkKind Evaluations { get; } = new(name: "sdf.bounds.evaluations", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting instructions visited after instance culling.</summary>
    public static WorkKind Instructions { get; } = new(name: "sdf.bounds.instructions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting full interval rotation expansions on a cache miss.</summary>
    public static WorkKind Rotations { get; } = new(name: "sdf.bounds.rotation-expansions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the process-wide immutable bounds work, including every authority's field.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.bounds", kinds: [Queries, Evaluations, Instructions, Rotations]);

    private const int Sets = 64;
    private const int Ways = 4;

    private readonly SdfFieldEvaluator m_field = (field ?? throw new ArgumentNullException(paramName: nameof(field)));
    private readonly Entry[] m_entries = new Entry[(Sets * Ways)];
    private readonly byte[] m_next = new byte[Sets];
    private readonly Lock m_gate = new();

    /// <inheritdoc/>
    public FixedQ4816 StepScale => m_field.StepScale;
    /// <summary>Gets the number of requested boxes, including reused answers.</summary>
    public long BoundsQueries { get; private set; }
    /// <summary>Gets the number of requests sent to the field instead of answered from the cache.</summary>
    public long FieldEvaluations { get; private set; }
    /// <summary>Gets the instructions actually visited by those field evaluations, after instance culling.</summary>
    public long InstructionsWalked { get; private set; }
    /// <summary>Gets the full interval rotation expansions performed by field evaluations.</summary>
    public long RotationsExpanded { get; private set; }

    /// <inheritdoc/>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
        lock (m_gate) {
            BoundsQueries++;
            ProcessWork.Count(kind: Queries);

            if (!lower.TryDelta(delta: out var low, origin: FixedPosition.Zero) ||
                !upper.TryDelta(delta: out var high, origin: FixedPosition.Zero)) {
                FieldEvaluations++;
                ProcessWork.Count(kind: Evaluations);
                return m_field.TryDistanceBounds(distance: out distance, lower: lower, upper: upper);
            }

            var hash = Fnv1aHash.Create();

            hash.Add(value: low.X.Value);
            hash.Add(value: low.Y.Value);
            hash.Add(value: low.Z.Value);
            hash.Add(value: high.X.Value);
            hash.Add(value: high.Y.Value);
            hash.Add(value: high.Z.Value);
            var set = ((int)(hash.Value & (Sets - 1)));
            var first = (set * Ways);

            for (var way = 0; (way < Ways); way++) {
                ref readonly var entry = ref m_entries[(first + way)];

                if (entry.Valid && (entry.Lower == low) && (entry.Upper == high)) {
                    distance = entry.Distance;
                    return true;
                }
            }

            FieldEvaluations++;
            ProcessWork.Count(kind: Evaluations);
            var answered = m_field.TryDistanceBounds(distance: out distance, instructionsWalked: out var walked, lower: lower, rotationsExpanded: out var rotations, upper: upper);

            InstructionsWalked += walked;
            ProcessWork.Add(amount: walked, kind: Instructions);
            RotationsExpanded += rotations;
            ProcessWork.Add(amount: rotations, kind: Rotations);

            if (answered) {
                m_entries[(first + m_next[set])] = new Entry(Distance: distance, Lower: low, Upper: high, Valid: true);
                m_next[set] = ((byte)((m_next[set] + 1) % Ways));
            }

            return answered;
        }
    }

    private readonly record struct Entry(FixedVector3 Lower, FixedVector3 Upper, FixedInterval Distance, bool Valid);
}

using Puck.Maths;

namespace Puck.SignedDistance.Queries;

/// <summary>Reuses exact box answers from one immutable compiled field. A contact provider owns one cache for its
/// field revision; mutable fields, including lattice columns, remain outside it.</summary>
/// <param name="field">The immutable program whose bounds are reused.</param>
/// <remarks>Only identical boxes share an answer. Sweep proposals, query budgets, refusals and reached fractions
/// therefore stay unchanged. Storage is bounded and allocated once; replacement follows query order, never time.
/// Concurrent callers are serialized, while deterministic work counts describe an ordered query stream.</remarks>
public sealed class SdfBoundsCache(SdfFieldEvaluator field) : IFieldBounds {
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

    /// <inheritdoc/>
    public bool TryDistanceBounds(FixedPosition lower, FixedPosition upper, out FixedInterval distance) {
        lock (m_gate) {
            BoundsQueries++;

            if (!lower.TryDelta(delta: out var low, origin: FixedPosition.Zero) ||
                !upper.TryDelta(delta: out var high, origin: FixedPosition.Zero)) {
                FieldEvaluations++;
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
            var answered = m_field.TryDistanceBounds(distance: out distance, instructionsWalked: out var walked, lower: lower, upper: upper);

            InstructionsWalked += walked;

            if (answered) {
                m_entries[(first + m_next[set])] = new Entry(Distance: distance, Lower: low, Upper: high, Valid: true);
                m_next[set] = ((byte)((m_next[set] + 1) % Ways));
            }

            return answered;
        }
    }

    private readonly record struct Entry(FixedVector3 Lower, FixedVector3 Upper, FixedInterval Distance, bool Valid);
}

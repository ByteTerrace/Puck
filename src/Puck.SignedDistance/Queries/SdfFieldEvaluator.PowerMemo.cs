using Puck.Abstractions;
using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    private const int PowerMemoCapacity = (1 << 16);
    private const long PowerMemoPayloadBytes = (PowerMemoCapacity * sizeof(ulong));
    private static readonly object PowerMemoKey = new();
    // All evaluators share one bounded payload. Racing cold callers may derive multiple small Lazy wrappers, but
    // only the wrapper held by the table allocates the payload. No program or placement holds a table reference.
    private static readonly CodeDerivedTable<object, Lazy<ExactPowerMemo>> PowerMemos = new();

    /// <summary>Gets the current code generation's process-shared power-memo payload bytes. Detached generations
    /// may still be in use by calls begun before a code update; this is not a managed-heap or allocator reading.</summary>
    public static long PowerMemoRetainedBytes =>
        (PowerMemos.TryGetValue(key: PowerMemoKey, value: out var memo) && memo.IsValueCreated)
            ? PowerMemoPayloadBytes
            : 0L;

    private static ExactPowerMemo CurrentPowerMemo() => PowerMemos.GetValue(
        key: PowerMemoKey,
        derive: static _ => new(valueFactory: static () => new ExactPowerMemo(), mode: LazyThreadSafetyMode.ExecutionAndPublication)
    ).Value;

    // One cell publishes its complete key and result atomically. Matching the complete key, rather than only the
    // index, makes collisions and competing writers affect work alone. Every miss delegates to the one numeric truth.
    private sealed class ExactPowerMemo {
        private const int BaseBits = 18;
        private const int ExponentBits = 21;
        private const int KeyBits = (BaseBits + ExponentBits);
        private const ulong KeyMask = ((1UL << KeyBits) - 1UL);
        private const ulong ResultMask = ((1UL << BaseBits) - 1UL);
        private const ulong Valid = (1UL << 63);
        private const long OneRaw = (1L << FixedQ4816.FractionBitCount);
        private readonly ulong[] m_slots = new ulong[PowerMemoCapacity];

        public ExactPowerMemo() {
            PointWork.Count(kind: PowerMemoAllocations);
            PointWork.Add(kind: PowerMemoAllocatedBytes, amount: PowerMemoPayloadBytes);
        }

        public FixedQ4816 Pow(FixedQ4816 x, FixedQ4816 y, ref PowerWork work) {
            // These branches stay in FixedQ4816.Pow: the memo neither reimplements their arithmetic nor spends a
            // cache cell on them. In particular the maximal superellipsoid ratio is always exactly One.
            if ((x == FixedQ4816.Zero) || (x == FixedQ4816.One)) {
                work.Bypasses++;
                return FixedQ4816.Pow(x: x, y: y);
            }

            var raw = x.Value;
            var exponent = y.Value;
            // The factored superellipsoid uses unit powers and roots of [1,3]. Guard this door as well: future
            // callers and any value outside the packed representation retain the direct numeric path unchanged.
            if (!(((raw > 0L) && (raw < OneRaw) && (exponent >= OneRaw) && (exponent <= (16L * OneRaw))) ||
                  ((raw > OneRaw) && (raw <= (3L * OneRaw)) && (exponent >= (OneRaw / 16L)) && (exponent <= OneRaw)))) {
                work.Fallbacks++;
                return FixedQ4816.Pow(x: x, y: y);
            }

            var key = (((ulong)raw) | (((ulong)exponent) << BaseBits));
            var index = ((int)(InvertibleBitMix.Mix64(value: key) & (PowerMemoCapacity - 1UL)));
            ref var slot = ref m_slots[index];
            var held = Volatile.Read(location: ref slot);

            if (((held & Valid) != 0UL) && ((held & KeyMask) == key)) {
                work.Hits++;
                return FixedQ4816.FromRawBits(value: ((long)((held >> KeyBits) & ResultMask)));
            }

            work.Misses++;
            var result = FixedQ4816.Pow(x: x, y: y);
            // This bound follows from the two admitted domains. Keep the numeric answer if the implementation ever
            // changes beyond it: truncating a result into the packed word is never an allowed cache operation.
            if (((ulong)result.Value) > ResultMask) {
                work.Fallbacks++;
                return result;
            }

            Volatile.Write(location: ref slot, value: (Valid | key | (((ulong)result.Value) << KeyBits)));
            return result;
        }
    }

    // The point walk owns these counts, including its primer recursion. Four shared additions per point replace
    // four interlocked additions per shape, so counting does not consume the arithmetic saving.
    private struct PowerWork {
        public long Hits;
        public long Misses;
        public long Fallbacks;
        public long Bypasses;

        public readonly void Publish() {
            if (Hits != 0L) { PointWork.Add(kind: PowerMemoHits, amount: Hits); }
            if (Misses != 0L) { PointWork.Add(kind: PowerMemoMisses, amount: Misses); }
            if (Fallbacks != 0L) { PointWork.Add(kind: PowerMemoFallbacks, amount: Fallbacks); }
            if (Bypasses != 0L) { PointWork.Add(kind: PowerMemoBypasses, amount: Bypasses); }
        }
    }
}

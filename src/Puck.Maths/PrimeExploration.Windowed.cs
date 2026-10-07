using System.Buffers;

namespace Puck.Maths;

public static partial class PrimeExploration {
    // Completes one segment with every upper base prime through the square root of the segment's high endpoint.
    // The streamed generator supplies the primes in ascending batches; the stateless marker divides once for
    // each prime's first multiple in the segment. No per-prime state outlives the segment, so the workspace is the
    // segment bitmap, the generator's fixed buffers and one batch of decoded primes, independent of pi(sqrt(high)).
    private sealed class WindowedUpperMarker(CancellationToken cancellationToken) : IDisposable {
        private readonly uint[] m_primes = ArrayPool<uint>.Shared.Rent(minimumLength: UpperPrimeStream.BatchCapacity);

        internal void Mark(Span<byte> segment, ulong blockLow, ulong low, ulong high) {
            using var stream = new UpperPrimeStream(cancellationToken: cancellationToken, limit: ((uint)high.SquareRoot()));
            int count;

            while ((count = stream.Fill()) != 0) {
                var coordinates = stream.Coordinates.AsSpan(length: count, start: 0);
                var primes = m_primes.AsSpan(length: count, start: 0);

                for (var index = 0; (index < primes.Length); ++index) {
                    var coordinate = coordinates[index];

                    primes[index] = (((coordinate >> 3) * PrimeWheel30.Modulus) + PrimeWheel30.NumericResidues[((int)(coordinate & (PrimeWheel30.ChannelCount - 1)))]);
                }
                MarkBases(blockLow: blockLow, high: high, low: low, primes: primes, segment: segment);
            }
        }

        public void Dispose() => ArrayPool<uint>.Shared.Return(array: m_primes);
    }
}

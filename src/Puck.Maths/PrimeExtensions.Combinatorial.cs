using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Shared compact factor and partial-sieve tables for the Gourdon decomposition.
    // The compressed pi lookup follows primecount's src/PiTable.hpp.
    // Copyright (C) 2022-2026 Kim Walisch. Distributed under the BSD 2-Clause license;
    // see THIRD-PARTY-NOTICES.md. The wheel, storage and unsigned arithmetic below are local.
    private const int CombinatorialTinyPrimeCount = 8;
    private const uint CombinatorialTinyPeriod = 9_699_690;
    private const uint CombinatorialTinyTotient = 1_658_880;
    private const uint CombinatorialTableCapacityStep = (1U << 16);
    private const ushort CombinatorialFactorMask = 0x7fff;
    private const ushort CombinatorialNegativeMoebius = 0x8000;

    private static ulong CountCombinatorial64(ulong value, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) {
        var workspace = new CombinatorialCountingWorkspace();

        return workspace.Count(cancellationToken: cancellationToken, profile: profile, value: value);
    }

    // The request retains compact factor/pi tables and leaf buffers between counts.
    // Capacity is bounded by 2^27: sqrt(capacity)<32767 keeps the ushort least-factor
    // sentinel strictly above every composite least factor. Managed arrays need no disposal.
    private sealed partial class CombinatorialCountingWorkspace {
        private CombinatorialFactorTables? m_tables;
        private CombinatorialLeafSieve? m_sieve;
        private ulong[] m_phi = [];
        private ulong[] m_quotients = [];

        internal ulong Count(ulong value, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) {
            cancellationToken.ThrowIfCancellationRequested();
            if (value <= uint.MaxValue) { return ((uint)value).PrimeCountingFunction(); }

            var cutoff = ResolveGourdonCutoff(value: value);
            var xStar = ResolveGourdonXStar(cutoff: cutoff, value: value);
            var lookupLimit = Math.Max(val1: cutoff, val2: Math.Max(val1: ((uint)(value / xStar).SquareRoot()),
                val2: ((uint)((value / xStar) / cutoff))));

            if ((m_tables is null) || (m_tables.Capacity < lookupLimit)) {
                cancellationToken.ThrowIfCancellationRequested();

                var capacity = ((((lookupLimit + CombinatorialTableCapacityStep) - 1U) / CombinatorialTableCapacityStep) * CombinatorialTableCapacityStep);
                var tables = new CombinatorialFactorTables(cancellationToken: cancellationToken, capacity: capacity);

                m_tables = tables;
                if (profile is not null) { profile.TableBuilt = true; }
            }
            if (profile is not null) {
                profile.Cutoff = cutoff;
                profile.TableCapacity = m_tables.Capacity;
            }

            var ordinary = CountOrdinaryLeaves64(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, tables: m_tables, value: value);
            var sigma = CountGourdonSigma(cancellationToken: cancellationToken, cutoff: cutoff, tables: m_tables, value: value, xStar: xStar);
            var easy = CountGourdonEasyLeaves(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, tables: m_tables, value: value, xStar: xStar);
            var special = CountGourdonHardLeaves(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, tables: m_tables, value: value, workspace: this, xStar: xStar);
            var semiprimes = PrimeExploration.CountSemiprimes(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, value: value);

            // pi(x)=A-B+C+D+Phi0+Sigma. CountGourdonSigma cancels the common
            // triangular part of B and Sigma0, permitting the existing P2 stream.
            // With z=y, Phi0 is precisely the ordinary mu(m)*phi(x/m,k) sum.
            var result = ((((ordinary + easy) + special) + sigma) - semiprimes);

            cancellationToken.ThrowIfCancellationRequested();
            return checked((ulong)result);
        }
        internal (CombinatorialLeafSieve Sieve, ulong[] Phi, ulong[] Quotients) PrepareLeaves(ulong value, uint width, uint[] primes, int primeCount, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_phi.Length <= primeCount) {
                m_phi = new ulong[(primeCount + 1)];
                m_quotients = new ulong[(primeCount + 1)];
            }
            Array.Clear(array: m_phi, index: 0, length: (primeCount + 1));

            for (var index = (CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
                if ((index & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                m_quotients[index] = (value / primes[index]);
            }
            m_sieve ??= new();
            m_sieve.Reset(primeCount: primeCount, primes: primes, width: width);
            return (m_sieve, m_phi, m_quotients);
        }

    }

    private static uint CombinatorialCubeRoot(ulong value) {
        var lower = 0U;
        var upper = (1U << 22);

        while ((upper - lower) > 1) {
            var middle = (lower + ((upper - lower) >> 1));
            var cube = ((((UInt128)middle) * middle) * middle);

            if (cube <= value) { lower = middle; } else { upper = middle; }
        }
        return lower;
    }
    private static Int128 CountOrdinaryLeaves64(ulong value, uint cutoff, CombinatorialFactorTables tables, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
        var sum = Int128.Zero;
        var factors = tables.Factors;
        var last = CombinatorialCoordinate(value: cutoff);

        if (profile is not null) { profile.OrdinaryFactorCoordinates += ((uint)(last + 1)); }

        // Phi0 = sum(mu(m) * phi(x/m,k)), m<=y and least-prime-factor(m)>p_k.
        // The m=1 entry has mu=1 and a sentinel least factor larger than every prime.
        for (var coordinate = 0; (coordinate <= last); ++coordinate) {
            if ((coordinate & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

            var factor = factors[coordinate];

            if ((factor & CombinatorialFactorMask) <= 19) { continue; }

            var number = CombinatorialNumber(coordinate: coordinate);
            var count = CountTinyPhi(value: (value / number));

            sum += (((factor & CombinatorialNegativeMoebius) == 0) ? ((Int128)count) : -((Int128)count));
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CountTinyPhi(ulong value) {
        var remainder = ((uint)(value % CombinatorialTinyPeriod));
        var word = (remainder / 240U);
        var mask = CombinatorialTables.PrefixMasks[(remainder % 240U)];

        return ((((value / CombinatorialTinyPeriod) * CombinatorialTinyTotient)
            + CombinatorialTables.TinyPrefix[word])
            + ((uint)BitOperations.PopCount(value: CombinatorialTables.TinyWords[word] & mask)));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CombinatorialCoordinate(uint value) {
        return (((((int)(value / 30U)) * 8) + BitOperations.PopCount(value: CombinatorialTables.PrefixMasks[(value % 30U)])) - 1);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CombinatorialNumber(int coordinate) {
        return (((((uint)coordinate) >> 3) * 30U) + PrimeWheel30.NumericResidues[coordinate & 7]);
    }

    private sealed class CombinatorialFactorTables {
        private readonly ulong[] m_primeBits;
        private readonly uint[] m_primePrefix;

        internal uint Capacity { get; }
        internal ushort[] Factors { get; }
        internal uint[] Primes { get; }

        internal CombinatorialFactorTables(uint capacity, CancellationToken cancellationToken) {
            Capacity = capacity;
            Factors = new ushort[(((capacity / 30U) + 1U) * 8U)];
            m_primeBits = new ulong[((capacity / 240U) + 1U)];
            m_primePrefix = new uint[m_primeBits.Length];
            Array.Fill(array: Factors, value: CombinatorialFactorMask);

            var squareRoot = capacity.SquareRoot();
            var last = CombinatorialCoordinate(value: capacity);

            // Only U(30) numbers can contribute beyond p_c=19. Zero means squareful;
            // bit15 is the sign of mu, and bits0..14 hold the least prime factor.
            // An untouched entry is prime. Factors above sqrt(capacity) need only the
            // 32767 sentinel: every least-factor comparison uses a prime below
            // sqrt(active y)<=sqrt(capacity)<32767.
            for (var coordinate = 1; (coordinate <= last); ++coordinate) {
                if ((coordinate & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                if (Factors[coordinate] != CombinatorialFactorMask) { continue; }

                var prime = CombinatorialNumber(coordinate: coordinate);

                m_primeBits[(coordinate >> 6)] |= (1UL << (coordinate & 63));
                SieveFactor(cancellationToken: cancellationToken, prime: prime, squareRoot: squareRoot);
            }

            var count = 3U;

            for (var index = 0; (index < m_primeBits.Length); ++index) {
                if ((index & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                m_primePrefix[index] = count;
                count += ((uint)BitOperations.PopCount(value: m_primeBits[index]));
            }
            Primes = new uint[(count + 1U)];
            Primes[1] = 2;
            Primes[2] = 3;
            Primes[3] = 5;

            var next = 4;

            for (var index = 0; (index < m_primeBits.Length); ++index) {
                if ((index & 4095) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                var bits = m_primeBits[index];

                while (bits != 0) {
                    var bit = BitOperations.TrailingZeroCount(value: bits);

                    Primes[next++] = CombinatorialNumber(coordinate: ((index * 64) + bit));
                    bits &= (bits - 1);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint CountPrimes(uint value) {
            if (value < 5) { return ((value < 2) ? 0U : ((value < 3) ? 1U : 2U)); }

            var word = (value / 240U);

            return (m_primePrefix[word] + ((uint)BitOperations.PopCount(value: m_primeBits[word]
                & CombinatorialTables.PrefixMasks[(value % 240U)])));
        }

        private void SieveFactor(uint prime, uint squareRoot, CancellationToken cancellationToken) {
            _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(prime % 30U)));

            var steps = CombinatorialTables.Steps.AsSpan(length: 8, start: (channel * 8));
            var quotient = (prime / 30U);
            var cursor = quotient;
            var phase = 0;
            var blockCount = ((uint)(Factors.Length / 8));
            var visited = 0U;

            while (cursor < blockCount) {
                if ((visited++ & 65535U) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var step = steps[phase];
                var coordinate = ((cursor * 8U) + step.Bit);
                var factor = Factors[coordinate];

                if (factor != 0) {
                    var least = factor & CombinatorialFactorMask;

                    if ((least == CombinatorialFactorMask) && (prime <= squareRoot)) { least = ((int)prime); }
                    Factors[coordinate] = ((ushort)(least | ((factor ^ CombinatorialNegativeMoebius) & CombinatorialNegativeMoebius)));
                }
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & 7;
            }
            if (prime > squareRoot) { return; }

            var square = (prime * prime);

            _ = PrimeWheel30.TryChannel(channel: out channel, residue: ((byte)(square % 30U)));
            steps = CombinatorialTables.Steps.AsSpan(length: 8, start: (channel * 8));
            quotient = (square / 30U);
            cursor = quotient;
            phase = 0;
            while (cursor < blockCount) {
                var step = steps[phase];

                Factors[((cursor * 8U) + step.Bit)] = 0;
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & 7;
            }
        }
    }
    private readonly struct CombinatorialWheelStep {
        internal readonly byte Bit;
        internal readonly byte Gap;
        internal readonly byte Carry;

        internal CombinatorialWheelStep(byte bit, byte gap, byte carry) {
            Bit = bit;
            Gap = gap;
            Carry = carry;
        }
    }
    private static class CombinatorialTables {
        internal static readonly ulong[] PrefixMasks = CreatePrefixMasks();
        internal static readonly CombinatorialWheelStep[] Steps = CreateSteps();
        internal static readonly ulong[] TinyWords = CreateTinyWords();
        internal static readonly uint[] TinyPrefix = CreateTinyPrefix();

        private static ulong[] CreatePrefixMasks() {
            var masks = new ulong[240];
            var mask = 0UL;
            var residues = PrimeWheel30.NumericResidues;

            for (var offset = 0; (offset < masks.Length); ++offset) {
                for (var bit = 0; (bit < residues.Length); ++bit) {
                    if ((offset % 30) == residues[bit]) { mask |= (1UL << (((offset / 30) * 8) + bit)); }
                }
                masks[offset] = mask;
            }
            return masks;
        }
        private static CombinatorialWheelStep[] CreateSteps() {
            var steps = new CombinatorialWheelStep[64];
            var residues = PrimeWheel30.NumericResidues;

            for (var channel = 0; (channel < 8); ++channel) {
                var prime = PrimeWheel30.Residues[channel];

                for (var phase = 0; (phase < 8); ++phase) {
                    var multiplier = residues[phase];
                    var next = ((phase == 7) ? 31 : residues[(phase + 1)]);
                    var target = PrimeWheel30.Multiply(left: ((byte)channel), right: PrimeWheel30.NumericChannels[phase]);
                    var carry = (((prime * next) / 30) - ((prime * multiplier) / 30));

                    steps[((channel * 8) + phase)] = new(bit: PrimeWheel30.NumericIndex(channel: target),
                        gap: ((byte)(next - multiplier)), carry: ((byte)carry));
                }
            }
            return steps;
        }
        private static ulong[] CreateTinyWords() {
            var words = new ulong[((CombinatorialTinyPeriod + 239U) / 240U)];

            Array.Fill(array: words, value: ulong.MaxValue);
            words[^1] &= PrefixMasks[((CombinatorialTinyPeriod - 1U) % 240U)];

            ReadOnlySpan<uint> primes = [7, 11, 13, 17, 19];

            foreach (var prime in primes) {
                _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)prime));

                var quotient = (prime / 30U);
                var cursor = quotient;
                var phase = 0;
                var steps = Steps.AsSpan(length: 8, start: (channel * 8));

                while (cursor < (CombinatorialTinyPeriod / 30U)) {
                    var step = steps[phase];
                    var bit = ((((int)(cursor & 7U)) * 8) + step.Bit);

                    words[(cursor >> 3)] &= ~(1UL << bit);
                    cursor += ((quotient * step.Gap) + step.Carry);
                    phase = (phase + 1) & 7;
                }
            }
            return words;
        }
        private static uint[] CreateTinyPrefix() {
            var prefix = new uint[TinyWords.Length];
            var count = 0U;

            for (var index = 0; (index < prefix.Length); ++index) {
                prefix[index] = count;
                count += ((uint)BitOperations.PopCount(value: TinyWords[index]));
            }
            return prefix;
        }
    }
    // The sieve retains one bit for each U(30) candidate. Unlike a prime enumeration
    // sieve, crossing off p starts at p, not p*p: phi excludes the sieving primes too.
    // Block counters are decremented only when a bit was set. Within each prime, leaf
    // quotients are increasing, so prefix queries reuse both block and word prefixes.
    private sealed class CombinatorialLeafSieve {
        private ulong[] m_words = [];
        private uint[] m_counters = [];
        private uint[] m_next = [];
        private byte[] m_phase = [];
        private int m_wordLength;
        private int m_counterShift;
        private int m_counterIndex;
        private int m_wordIndex;
        private uint m_counterSum;
        private uint m_wordSum;
        private uint m_total;

        internal uint TotalCount => m_total;

        internal void Reset(uint width, uint[] primes, int primeCount) {
            var words = ((int)(width / 240U));

            if (m_words.Length < words) {
                m_words = new ulong[words];
                m_counters = new uint[((words + 15) >> 4)];
            }
            if (m_next.Length <= primeCount) {
                m_next = new uint[(primeCount + 1)];
                m_phase = new byte[(primeCount + 1)];
            }
            // A request can correct its estimate in either direction. Reset active states
            // to the first multiple p, even when retained arrays extend past this count.
            for (var index = (CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
                m_next[index] = (primes[index] / 30U);
                _ = PrimeWheel30.TryChannel(residue: ((byte)(primes[index] % 30U)), channel: out var channel);
                m_phase[index] = ((byte)(channel * 8));
            }
        }
        internal void Initialize(ulong low, uint width) {
            m_wordLength = ((int)((width + 239U) / 240U));

            var bytes = MemoryMarshal.AsBytes(span: m_words.AsSpan(length: m_wordLength, start: 0));
            var pattern = MemoryMarshal.AsBytes(span: CombinatorialTables.TinyWords.AsSpan())[..((int)(CombinatorialTinyPeriod / 30U))];
            var source = ((int)((low / 30UL) % ((uint)pattern.Length)));
            var copied = 0;

            while (copied < bytes.Length) {
                var length = Math.Min(val1: (pattern.Length - source), val2: (bytes.Length - copied));

                pattern.Slice(length: length, start: source).CopyTo(destination: bytes[copied..]);
                copied += length;
                source = 0;
            }
            m_words[(m_wordLength - 1)] &= CombinatorialTables.PrefixMasks[((width - 1U) % 240U)];

            // Balance coarse counter reads against POPCNT tails. The integer fourth
            // root changes only block size; the minimum block contains sixteen words.
            var blockWords = Math.Max(val1: 16U, val2: ((uint)(low.SquareRoot().SquareRoot() / 16UL)));

            m_counterShift = BitOperations.Log2(value: BitOperations.RoundUpToPowerOf2(value: blockWords));
            Array.Clear(array: m_counters);
            m_total = 0;

            for (var index = 0; (index < m_wordLength); ++index) {
                var count = ((uint)BitOperations.PopCount(value: m_words[index]));

                m_counters[(index >> m_counterShift)] += count;
                m_total += count;
            }
            ResetPrefix();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint CountPrefix(uint offset) {
            var targetWord = ((int)(offset / 240U));
            var targetCounter = (targetWord >> m_counterShift);

            if (m_counterIndex < targetCounter) {
                do { m_counterSum += m_counters[m_counterIndex++]; } while (m_counterIndex < targetCounter);
                m_wordIndex = (targetCounter << m_counterShift);
                m_wordSum = 0;
            }
            while (m_wordIndex < targetWord) { m_wordSum += ((uint)BitOperations.PopCount(value: m_words[m_wordIndex++])); }

            return ((m_counterSum + m_wordSum)
                + ((uint)BitOperations.PopCount(value: m_words[targetWord] & CombinatorialTables.PrefixMasks[(offset % 240U)])));
        }
        internal void CrossOff(uint prime, int index) {
            var cursor = m_next[index];
            var phase = ((int)m_phase[index]);
            var channel = phase & ~7;
            var quotient = (prime / 30U);
            var byteLength = ((uint)(m_wordLength * 8));
            var steps = CombinatorialTables.Steps;

            while (cursor < byteLength) {
                var step = steps[phase];
                var wordIndex = ((int)(cursor >> 3));
                var bit = ((((int)(cursor & 7U)) * 8) + step.Bit);
                var word = m_words[wordIndex];
                var removed = ((uint)((word >> bit) & 1UL));

                m_words[wordIndex] = word & ~(1UL << bit);
                m_counters[(wordIndex >> m_counterShift)] -= removed;
                m_total -= removed;
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = channel | ((phase + 1) & 7);
            }
            m_next[index] = (cursor - byteLength);
            m_phase[index] = ((byte)phase);
            ResetPrefix();
        }

        private void ResetPrefix() {
            m_counterIndex = 0;
            m_wordIndex = 0;
            m_counterSum = 0;
            m_wordSum = 0;
        }
    }
}

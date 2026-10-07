using System.Diagnostics;
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
            // Bounds below the quotient-counting root cap never reach this counter.
            Debug.Assert((value > CountingValueLimit));

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
            // triangular part of B and Sigma0, permitting the streamed P2 count.
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
        var word = (remainder / PrimeWheel30.WordIntegers);
        var mask = CombinatorialTables.PrefixMasks[(remainder % PrimeWheel30.WordIntegers)];

        return ((((value / CombinatorialTinyPeriod) * CombinatorialTinyTotient)
            + CombinatorialTables.TinyPrefix[word])
            + ((uint)BitOperations.PopCount(value: CombinatorialTables.TinyWords[word] & mask)));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CombinatorialCoordinate(uint value) {
        return (((((int)(value / PrimeWheel30.Modulus)) * PrimeWheel30.ChannelCount)
            + BitOperations.PopCount(value: CombinatorialTables.PrefixMasks[(value % PrimeWheel30.Modulus)])) - 1);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CombinatorialNumber(int coordinate) {
        return (((((uint)coordinate) >> 3) * PrimeWheel30.Modulus) + PrimeWheel30.NumericResidues[coordinate & (PrimeWheel30.ChannelCount - 1)]);
    }

    private sealed class CombinatorialFactorTables {
        private readonly ulong[] m_primeBits;
        private readonly uint[] m_primePrefix;

        internal uint Capacity { get; }
        internal ushort[] Factors { get; }
        internal uint[] Primes { get; }

        internal CombinatorialFactorTables(uint capacity, CancellationToken cancellationToken) {
            Capacity = capacity;
            Factors = new ushort[(((capacity / PrimeWheel30.Modulus) + 1U) * PrimeWheel30.ChannelCount)];
            m_primeBits = new ulong[((capacity / PrimeWheel30.WordIntegers) + 1U)];
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

            var word = (value / PrimeWheel30.WordIntegers);

            return (m_primePrefix[word] + ((uint)BitOperations.PopCount(value: m_primeBits[word]
                & CombinatorialTables.PrefixMasks[(value % PrimeWheel30.WordIntegers)])));
        }

        private void SieveFactor(uint prime, uint squareRoot, CancellationToken cancellationToken) {
            _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(prime % PrimeWheel30.Modulus)));

            var steps = CombinatorialTables.Steps.AsSpan(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));
            var quotient = (prime / PrimeWheel30.Modulus);
            var cursor = quotient;
            var phase = 0;
            var blockCount = ((uint)(Factors.Length / PrimeWheel30.ChannelCount));
            var visited = 0U;

            while (cursor < blockCount) {
                if ((visited++ & 65535U) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var step = steps[phase];
                var coordinate = ((cursor * PrimeWheel30.ChannelCount) + step.Bit);
                var factor = Factors[coordinate];

                if (factor != 0) {
                    var least = factor & CombinatorialFactorMask;

                    if ((least == CombinatorialFactorMask) && (prime <= squareRoot)) { least = ((int)prime); }
                    Factors[coordinate] = ((ushort)(least | ((factor ^ CombinatorialNegativeMoebius) & CombinatorialNegativeMoebius)));
                }
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
            }
            if (prime > squareRoot) { return; }

            var square = (prime * prime);

            _ = PrimeWheel30.TryChannel(channel: out channel, residue: ((byte)(square % PrimeWheel30.Modulus)));
            steps = CombinatorialTables.Steps.AsSpan(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));
            quotient = (square / PrimeWheel30.Modulus);
            cursor = quotient;
            phase = 0;
            while (cursor < blockCount) {
                var step = steps[phase];

                Factors[((cursor * PrimeWheel30.ChannelCount) + step.Bit)] = 0;
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
            }
        }
    }

    internal readonly struct CombinatorialWheelStep {
        internal readonly byte Bit;
        internal readonly byte Gap;
        internal readonly byte Carry;

        internal CombinatorialWheelStep(byte bit, byte gap, byte carry) {
            Bit = bit;
            Gap = gap;
            Carry = carry;
        }
    }
    internal static class CombinatorialTables {
        internal static readonly ulong[] PrefixMasks = CreatePrefixMasks();
        internal static readonly CombinatorialWheelStep[] Steps = CreateSteps();
        internal static readonly ulong[] TinyWords = CreateTinyWords();
        internal static readonly uint[] TinyPrefix = CreateTinyPrefix();

        private static ulong[] CreatePrefixMasks() {
            var masks = new ulong[PrimeWheel30.WordIntegers];

            for (var offset = 0; (offset < masks.Length); ++offset) { masks[offset] = PrimeWheel30.WordPrefixMask(offset: offset); }
            return masks;
        }
        private static CombinatorialWheelStep[] CreateSteps() {
            var steps = new CombinatorialWheelStep[(PrimeWheel30.ChannelCount * PrimeWheel30.ChannelCount)];

            for (var channel = 0; (channel < PrimeWheel30.ChannelCount); ++channel) {
                var residue = PrimeWheel30.Residues[channel];

                for (var phase = 0; (phase < PrimeWheel30.ChannelCount); ++phase) {
                    steps[((channel * PrimeWheel30.ChannelCount) + phase)] = new(bit: ((byte)PrimeWheel30.TargetBit(phase: phase, residue: residue)),
                        carry: ((byte)PrimeWheel30.Carry(phase: phase, residue: residue)), gap: ((byte)PrimeWheel30.Gap(phase: phase)));
                }
            }
            return steps;
        }
        private static ulong[] CreateTinyWords() {
            var words = new ulong[(((CombinatorialTinyPeriod + PrimeWheel30.WordIntegers) - 1U) / PrimeWheel30.WordIntegers)];

            Array.Fill(array: words, value: ulong.MaxValue);
            words[^1] &= PrefixMasks[((CombinatorialTinyPeriod - 1U) % PrimeWheel30.WordIntegers)];

            ReadOnlySpan<uint> primes = [7, 11, 13, 17, 19];

            foreach (var prime in primes) {
                _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)prime));

                var quotient = (prime / PrimeWheel30.Modulus);
                var cursor = quotient;
                var phase = 0;
                var steps = Steps.AsSpan(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));

                while (cursor < (CombinatorialTinyPeriod / PrimeWheel30.Modulus)) {
                    var step = steps[phase];
                    var bit = ((((int)(cursor % PrimeWheel30.ChannelCount)) * PrimeWheel30.ChannelCount) + step.Bit);

                    words[(cursor / PrimeWheel30.ChannelCount)] &= ~(1UL << bit);
                    cursor += ((quotient * step.Gap) + step.Carry);
                    phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
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
    // quotients are increasing, so a prefix cursor reuses both block and word prefixes.
    internal sealed class CombinatorialLeafSieve {
        private ulong[] m_words = [];
        private uint[] m_counters = [];
        private uint[] m_next = [];
        private byte[] m_phase = [];
        private int m_wordLength;
        private int m_counterShift;
        private uint m_total;

        internal uint TotalCount => m_total;

        internal void Reset(uint width, uint[] primes, int primeCount) {
            var words = ((int)(width / PrimeWheel30.WordIntegers));

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
                m_next[index] = (primes[index] / PrimeWheel30.Modulus);
                _ = PrimeWheel30.TryChannel(residue: ((byte)(primes[index] % PrimeWheel30.Modulus)), channel: out var channel);
                m_phase[index] = ((byte)(channel * PrimeWheel30.ChannelCount));
            }
        }
        internal void Initialize(ulong low, uint width) {
            m_wordLength = ((int)(((width + PrimeWheel30.WordIntegers) - 1U) / PrimeWheel30.WordIntegers));

            var bytes = MemoryMarshal.AsBytes(span: m_words.AsSpan(length: m_wordLength, start: 0));
            var pattern = MemoryMarshal.AsBytes(span: CombinatorialTables.TinyWords.AsSpan())[..((int)(CombinatorialTinyPeriod / PrimeWheel30.Modulus))];
            var source = ((int)((low / PrimeWheel30.Modulus) % ((uint)pattern.Length)));
            var copied = 0;

            while (copied < bytes.Length) {
                var length = Math.Min(val1: (pattern.Length - source), val2: (bytes.Length - copied));

                pattern.Slice(length: length, start: source).CopyTo(destination: bytes[copied..]);
                copied += length;
                source = 0;
            }
            m_words[(m_wordLength - 1)] &= CombinatorialTables.PrefixMasks[((width - 1U) % PrimeWheel30.WordIntegers)];

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
        }
        // Starts nondecreasing prefix queries over the current bitmap; the cursor is stale after the next crossing.
        internal CombinatorialPrefixCursor StartPrefix() =>
            new(counters: m_counters, shift: m_counterShift, wordLength: m_wordLength, words: m_words);
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void CrossOff(uint prime, int index) {
            var state = m_phase[index];
            var cursor = m_next[index];
            var phase = state & (PrimeWheel30.ChannelCount - 1);
            var byteLength = ((uint)(m_wordLength * sizeof(ulong)));
            // Little-endian words: wheel byte b holds bits 8*(b%8)..8*(b%8)+7 of word b/8.
            ref var bytes = ref Unsafe.As<ulong, byte>(source: ref MemoryMarshal.GetArrayDataReference(array: m_words));
            ref var counters = ref MemoryMarshal.GetArrayDataReference(array: m_counters);
            var shift = (m_counterShift + 3);
            var quotient = (prime / PrimeWheel30.Modulus);
            var removed = (state >> 3) switch {
                0 => CrossOff<CombinatorialChannel0>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                1 => CrossOff<CombinatorialChannel1>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                2 => CrossOff<CombinatorialChannel2>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                3 => CrossOff<CombinatorialChannel3>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                4 => CrossOff<CombinatorialChannel4>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                5 => CrossOff<CombinatorialChannel5>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                6 => CrossOff<CombinatorialChannel6>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
                _ => CrossOff<CombinatorialChannel7>(byteLength: byteLength, bytes: ref bytes, counters: ref counters, cursor: ref cursor, phase: ref phase, quotient: quotient, shift: shift),
            };

            m_total -= removed;
            m_next[index] = (cursor - byteLength);
            m_phase[index] = ((byte)((state & ~(PrimeWheel30.ChannelCount - 1)) | phase));
        }

        // Crosses off one prime of a fixed channel from wheel byte cursor at the given phase, through the end of the
        // bitmap, and returns the number of bits it cleared. A prime whose eight-step cycle fits in two counter blocks
        // keeps its block's decrements in a register and settles them when a crossing enters a later block; a larger
        // prime decrements each crossing's counter directly. Full cycles use folded per-channel bits and carries.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static uint CrossOff<TChannel>(ref byte bytes, ref uint counters, uint byteLength, int shift, uint quotient,
            ref uint cursor, ref int phase)
            where TChannel : struct, ICombinatorialChannel {
            var residue = PrimeWheel30.Residues[TChannel.Channel];
            var span = ((quotient * PrimeWheel30.Modulus) + residue);
            ref readonly var steps = ref CombinatorialTables.Steps[(TChannel.Channel * PrimeWheel30.ChannelCount)];
            var at = cursor;
            var current = phase;
            var removed = 0U;
            // A full cycle from at, starting at phase zero, stays inside the bitmap exactly when at<=limit.
            var limit = (((long)byteLength) - span);

            if (at >= byteLength) { return 0; }
            // A cycle of p bytes clears eight bits, so a block of B bytes sees 8B/p crossings. Dense mode pays one
            // hard-to-predict branch per block entered, p/(8B) per crossing; sparse mode chains a counter
            // read-modify-write through memory for the other 1-p/(8B). With a mispredict costing two to three
            // store-forwarding latencies, the two break even near p=2B to 2.6B, so dense mode takes p<=2B.
            if (span <= (2U << shift)) {
                var threshold = (((at >> shift) + 1U) << shift);
                var pending = 0U;

                for (; ((current != 0) && (at < byteLength)); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                    var step = Unsafe.Add(source: ref Unsafe.AsRef(source: in steps), elementOffset: current);

                    CrossDense(at: at, bit: step.Bit, bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * step.Gap) + step.Carry);
                }
                while (at <= limit) {
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 0, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 0))) + ((uint)PrimeWheel30.Carry(phase: 0, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 1, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 1))) + ((uint)PrimeWheel30.Carry(phase: 1, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 2, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 2))) + ((uint)PrimeWheel30.Carry(phase: 2, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 3, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 3))) + ((uint)PrimeWheel30.Carry(phase: 3, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 4, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 4))) + ((uint)PrimeWheel30.Carry(phase: 4, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 5, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 5))) + ((uint)PrimeWheel30.Carry(phase: 5, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 6, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 6))) + ((uint)PrimeWheel30.Carry(phase: 6, residue: residue)));
                    CrossDense(at: at, bit: PrimeWheel30.TargetBit(phase: 7, residue: residue), bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 7))) + ((uint)PrimeWheel30.Carry(phase: 7, residue: residue)));
                }
                for (; (at < byteLength); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                    var step = Unsafe.Add(source: ref Unsafe.AsRef(source: in steps), elementOffset: current);

                    CrossDense(at: at, bit: step.Bit, bytes: ref bytes, counters: ref counters, pending: ref pending, removed: ref removed, shift: shift, threshold: ref threshold);
                    at += ((quotient * step.Gap) + step.Carry);
                }
                Unsafe.Add(elementOffset: ((nint)((threshold >> shift) - 1U)), source: ref counters) -= pending;
                removed += pending;
            } else {
                for (; ((current != 0) && (at < byteLength)); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                    var step = Unsafe.Add(source: ref Unsafe.AsRef(source: in steps), elementOffset: current);

                    removed += CrossSparse(at: at, bit: step.Bit, bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * step.Gap) + step.Carry);
                }
                while (at <= limit) {
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 0, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 0))) + ((uint)PrimeWheel30.Carry(phase: 0, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 1, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 1))) + ((uint)PrimeWheel30.Carry(phase: 1, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 2, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 2))) + ((uint)PrimeWheel30.Carry(phase: 2, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 3, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 3))) + ((uint)PrimeWheel30.Carry(phase: 3, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 4, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 4))) + ((uint)PrimeWheel30.Carry(phase: 4, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 5, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 5))) + ((uint)PrimeWheel30.Carry(phase: 5, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 6, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 6))) + ((uint)PrimeWheel30.Carry(phase: 6, residue: residue)));
                    removed += CrossSparse(at: at, bit: PrimeWheel30.TargetBit(phase: 7, residue: residue), bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * ((uint)PrimeWheel30.Gap(phase: 7))) + ((uint)PrimeWheel30.Carry(phase: 7, residue: residue)));
                }
                for (; (at < byteLength); current = (current + 1) & (PrimeWheel30.ChannelCount - 1)) {
                    var step = Unsafe.Add(source: ref Unsafe.AsRef(source: in steps), elementOffset: current);

                    removed += CrossSparse(at: at, bit: step.Bit, bytes: ref bytes, counters: ref counters, shift: shift);
                    at += ((quotient * step.Gap) + step.Carry);
                }
            }
            cursor = at;
            phase = current;
            return removed;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CrossDense(ref byte bytes, ref uint counters, int shift, uint at, int bit, ref uint threshold,
            ref uint pending, ref uint removed) {
            if (at >= threshold) {
                Unsafe.Add(elementOffset: ((nint)((threshold >> shift) - 1U)), source: ref counters) -= pending;
                removed += pending;
                pending = 0;
                threshold = (((at >> shift) + 1U) << shift);
            }
            ref var target = ref Unsafe.Add(elementOffset: ((nint)at), source: ref bytes);
            uint value = target;

            pending += (value >> bit) & 1U;
            target = ((byte)(value & ~(1U << bit)));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint CrossSparse(ref byte bytes, ref uint counters, int shift, uint at, int bit) {
            ref var target = ref Unsafe.Add(elementOffset: ((nint)at), source: ref bytes);
            uint value = target;
            var hit = (value >> bit) & 1U;

            target = ((byte)(value & ~(1U << bit)));
            Unsafe.Add(elementOffset: ((nint)(at >> shift)), source: ref counters) -= hit;
            return hit;
        }
    }
    // Reads prefix counts of one leaf-sieve bitmap for nondecreasing offsets below its width. The hard-leaf loop keeps
    // one cursor per prime as a local; a batch copies it into registers and writes it back.
    internal ref struct CombinatorialPrefixCursor {
        private readonly ref ulong m_words;
        private readonly ref uint m_counters;
        private readonly ref ulong m_masks;
        private readonly int m_shift;
        private readonly int m_wordLength;

        private int m_counterIndex;
        private int m_wordIndex;
        private uint m_counterSum;
        private uint m_wordSum;

        internal CombinatorialPrefixCursor(ulong[] words, uint[] counters, int shift, int wordLength) {
            m_words = ref MemoryMarshal.GetArrayDataReference(array: words);
            m_counters = ref MemoryMarshal.GetArrayDataReference(array: counters);
            m_masks = ref MemoryMarshal.GetArrayDataReference(array: CombinatorialTables.PrefixMasks);
            m_shift = shift;
            m_wordLength = wordLength;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint Count(uint offset) {
            var targetWord = ((int)(offset / PrimeWheel30.WordIntegers));
            var targetCounter = (targetWord >> m_shift);

            Debug.Assert((targetWord < m_wordLength));
            if (m_counterIndex < targetCounter) {
                var counterSum = m_counterSum;
                var counterIndex = m_counterIndex;

                do { counterSum += Unsafe.Add(source: ref m_counters, elementOffset: counterIndex++); } while (counterIndex < targetCounter);
                m_counterSum = counterSum;
                m_counterIndex = counterIndex;
                m_wordIndex = (targetCounter << m_shift);
                m_wordSum = 0;
            }
            if (m_wordIndex < targetWord) {
                var wordSum = m_wordSum;
                var wordIndex = m_wordIndex;

                do { wordSum += ((uint)BitOperations.PopCount(value: Unsafe.Add(source: ref m_words, elementOffset: wordIndex++))); } while (wordIndex < targetWord);
                m_wordSum = wordSum;
                m_wordIndex = wordIndex;
            }
            var residue = (offset - (((uint)targetWord) * PrimeWheel30.WordIntegers));

            return ((m_counterSum + m_wordSum) + ((uint)BitOperations.PopCount(value: Unsafe.Add(elementOffset: targetWord, source: ref m_words)
                & Unsafe.Add(elementOffset: ((nint)residue), source: ref m_masks))));
        }
    }

    // One wheel channel per type, so each crossing loop folds its channel's bits and carries.
    private interface ICombinatorialChannel {
        static abstract int Channel { get; }
    }
    private readonly struct CombinatorialChannel0 : ICombinatorialChannel { public static int Channel => 0; }
    private readonly struct CombinatorialChannel1 : ICombinatorialChannel { public static int Channel => 1; }
    private readonly struct CombinatorialChannel2 : ICombinatorialChannel { public static int Channel => 2; }
    private readonly struct CombinatorialChannel3 : ICombinatorialChannel { public static int Channel => 3; }
    private readonly struct CombinatorialChannel4 : ICombinatorialChannel { public static int Channel => 4; }
    private readonly struct CombinatorialChannel5 : ICombinatorialChannel { public static int Channel => 5; }
    private readonly struct CombinatorialChannel6 : ICombinatorialChannel { public static int Channel => 6; }
    private readonly struct CombinatorialChannel7 : ICombinatorialChannel { public static int Channel => 7; }
}

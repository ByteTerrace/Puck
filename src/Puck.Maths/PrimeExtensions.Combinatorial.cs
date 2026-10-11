using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    // Shared compact factor and partial-sieve tables for the Gourdon decomposition.
    // The compressed pi lookup follows primecount's src/PiTable.hpp.
    // Copyright (C) 2022-2026 Kim Walisch. Distributed under the BSD 2-Clause license;
    // see THIRD-PARTY-NOTICES.md. The wheel, storage and unsigned arithmetic below are local.
    private const uint CombinatorialTableCapacityStep = (1U << 16);

    private static ulong CountCombinatorial64(ulong value, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) =>
        CountCombinatorial64Core(cancellationToken: cancellationToken, cutoff: ResolveGourdonCutoff(value: value), profile: profile, value: value);

    /// <summary>Counts the primes at or below <paramref name="value"/> by Gourdon's method with an explicit cutoff
    /// <paramref name="cutoff"/>, which moves work between the phases without changing the result.</summary>
    /// <param name="value">The inclusive bound, in the Gourdon regime: above the quotient-counting root cap.</param>
    /// <param name="cutoff">The admissible cutoff y: strictly between the integer cube and square roots of
    /// <paramref name="value"/> and at most 2^27, the bounds <see cref="ResolveGourdonCutoff(ulong)"/> keeps.</param>
    /// <param name="cancellationToken">Observed between batches.</param>
    /// <param name="profile">Receives the counted work, or <see langword="null"/>.</param>
    /// <returns>The number of primes at or below <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not above the quotient-counting root
    /// cap, or <paramref name="cutoff"/> is not strictly between the integer cube and square roots of
    /// <paramref name="value"/>, or exceeds 2^27.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <remarks>The law <c>prime-selection.gourdon-cutoff-moves-work-only</c> counts one bound at cutoffs across the
    /// admissible range.</remarks>
    public static ulong CountCombinatorial64(ulong value, uint cutoff, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: value, other: CountingValueLimit, paramName: nameof(value));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: cutoff, other: CombinatorialCubeRoot(value: value), paramName: nameof(cutoff));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: ((ulong)cutoff), other: value.SquareRoot(), paramName: nameof(cutoff));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: cutoff, other: GourdonTableCapacityLimit, paramName: nameof(cutoff));
        return CountCombinatorial64Core(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, value: value);
    }

    private static ulong CountCombinatorial64Core(ulong value, uint cutoff, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile) {
        var workspace = new CombinatorialCountingWorkspace();

        return workspace.Count(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, value: value);
    }

    // The request retains compact factor/pi tables and leaf buffers between counts.
    // Capacity is bounded by 2^27: sqrt(capacity)<32767 keeps the ushort least-factor
    // sentinel strictly above every composite least factor. Managed arrays need no disposal.
    private sealed partial class CombinatorialCountingWorkspace {
        private CombinatorialFactorTables? m_tables;
        private PrimeLeafKernels.CombinatorialLeafSieve? m_sieve;
        private ulong[] m_phi = [];
        private ulong[] m_quotients = [];

        internal ulong Count(ulong value, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) =>
            Count(cancellationToken: cancellationToken, cutoff: ResolveGourdonCutoff(value: value), profile: profile, value: value);
        internal ulong Count(ulong value, uint cutoff, CancellationToken cancellationToken, PrimeCountWorkBuilder? profile = null) {
            cancellationToken.ThrowIfCancellationRequested();
            // Bounds below the quotient-counting root cap never reach this counter.
            Debug.Assert((value > CountingValueLimit));
            Debug.Assert(((cutoff > CombinatorialCubeRoot(value: value)) && (cutoff < value.SquareRoot()) && (cutoff <= GourdonTableCapacityLimit)));

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
            var semiprimes = PrimeExploration.CountSemiprimesCore(cancellationToken: cancellationToken, cutoff: cutoff, profile: profile, value: value);

            // pi(x)=A-B+C+D+Phi0+Sigma. CountGourdonSigma cancels the common
            // triangular part of B and Sigma0, permitting the streamed P2 count.
            // With z=y, Phi0 is precisely the ordinary mu(m)*phi(x/m,k) sum.
            var result = ((((ordinary + easy) + special) + sigma) - semiprimes);

            cancellationToken.ThrowIfCancellationRequested();
            return checked((ulong)result);
        }
        internal (PrimeLeafKernels.CombinatorialLeafSieve Sieve, ulong[] Phi, ulong[] Quotients) PrepareLeaves(ulong value, uint width, uint[] primes, int primeCount, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            if (m_phi.Length <= primeCount) {
                m_phi = new ulong[(primeCount + 1)];
                m_quotients = new ulong[(primeCount + 1)];
            }
            Array.Clear(array: m_phi, index: 0, length: (primeCount + 1));

            for (var index = (PrimeLeafKernels.CombinatorialTinyPrimeCount + 1); (index <= primeCount); ++index) {
                if ((index & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                m_quotients[index] = (value / primes[index]);
            }
            m_sieve ??= new();
            m_sieve.ResetCore(primeCount: primeCount, primes: primes, width: width);
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
        var last = PrimeLeafKernels.CombinatorialCoordinate(value: cutoff);

        if (profile is not null) { profile.OrdinaryFactorCoordinates += ((uint)(last + 1)); }

        // Phi0 = sum(mu(m) * phi(x/m,k)), m<=y and least-prime-factor(m)>p_k.
        // The m=1 entry has mu=1 and a sentinel least factor larger than every prime.
        for (var coordinate = 0; (coordinate <= last); ++coordinate) {
            if ((coordinate & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

            var factor = factors[coordinate];

            if ((factor & PrimeLeafKernels.CombinatorialFactorMask) <= 19) { continue; }

            var number = PrimeLeafKernels.CombinatorialNumber(coordinate: coordinate);
            // phi(x/m,8) < x/5 < 2^62 fits a long, and the Moebius sign bit selects its negation without a branch.
            var count = ((long)CountTinyPhi(value: (value / number)));
            var negative = ((long)(factor >> 15));

            sum += ((count ^ -negative) + negative);
        }
        return sum;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CountTinyPhi(ulong value) {
        // Each quotient and remainder pair shares one division; the JIT does not combine a separate / and %.
        var (periods, offset) = Math.DivRem(left: value, right: PrimeLeafKernels.CombinatorialTinyPeriod);
        var (word, residue) = Math.DivRem(left: ((uint)offset), right: ((uint)PrimeWheel30.WordIntegers));
        var mask = PrimeLeafKernels.CombinatorialTables.PrefixMasks[((int)residue)];

        return (((periods * PrimeLeafKernels.CombinatorialTinyTotient)
            + PrimeLeafKernels.CombinatorialTables.TinyPrefix[word])
            + ((uint)BitOperations.PopCount(value: PrimeLeafKernels.CombinatorialTables.TinyWords[word] & mask)));
    }

    private sealed class CombinatorialFactorTables {
        private readonly ulong[] m_primeBits;
        private readonly uint[] m_primePrefix;

        internal uint Capacity { get; }
        internal ushort[] Factors { get; }
        internal uint[] Primes { get; }
        // PrimeDivisionKernels.GourdonReciprocal(Primes[i]) for every i>0, so the easy leaves divide by a multiply; entry 0 is unused.
        internal double[] Reciprocals { get; }

        internal CombinatorialFactorTables(uint capacity, CancellationToken cancellationToken) {
            Capacity = capacity;
            Factors = new ushort[(((capacity / PrimeWheel30.Modulus) + 1U) * PrimeWheel30.ChannelCount)];
            m_primeBits = new ulong[((capacity / PrimeWheel30.WordIntegers) + 1U)];
            m_primePrefix = new uint[m_primeBits.Length];
            Array.Fill(array: Factors, value: PrimeLeafKernels.CombinatorialFactorMask);

            var squareRoot = capacity.SquareRoot();
            var last = PrimeLeafKernels.CombinatorialCoordinate(value: capacity);

            // Only U(30) numbers can contribute beyond p_c=19. Zero means squareful;
            // bit15 is the sign of mu, and bits0..14 hold the least prime factor.
            // An untouched entry is prime. Factors above sqrt(capacity) need only the
            // 32767 sentinel: every least-factor comparison uses a prime below
            // sqrt(active y)<=sqrt(capacity)<32767.
            for (var coordinate = 1; (coordinate <= last); ++coordinate) {
                if ((coordinate & 16383) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                if (Factors[coordinate] != PrimeLeafKernels.CombinatorialFactorMask) { continue; }

                var prime = PrimeLeafKernels.CombinatorialNumber(coordinate: coordinate);

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

                    Primes[next++] = PrimeLeafKernels.CombinatorialNumber(coordinate: ((index * 64) + bit));
                    bits &= (bits - 1);
                }
            }
            Reciprocals = new double[Primes.Length];
            for (var index = 1; (index < Primes.Length); ++index) {
                if ((index & 65535) == 0) { cancellationToken.ThrowIfCancellationRequested(); }
                Reciprocals[index] = PrimeDivisionKernels.GourdonReciprocal(divisor: Primes[index]);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal uint CountPrimes(uint value) {
            if (value < 5) { return ((value < 2) ? 0U : ((value < 3) ? 1U : 2U)); }

            var word = (value / PrimeWheel30.WordIntegers);

            return (m_primePrefix[word] + ((uint)BitOperations.PopCount(value: m_primeBits[word]
                & PrimeLeafKernels.CombinatorialTables.PrefixMasks[((int)(value % PrimeWheel30.WordIntegers))])));
        }

        // Most calls run under a thousand iterations and would finish in tier-0 code without this.
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private void SieveFactor(uint prime, uint squareRoot, CancellationToken cancellationToken) {
            _ = PrimeWheel30.TryChannel(channel: out var channel, residue: ((byte)(prime % PrimeWheel30.Modulus)));

            var steps = PrimeLeafKernels.CombinatorialTables.Steps.Slice(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));
            var quotient = (prime / PrimeWheel30.Modulus);
            var cursor = quotient;
            var phase = 0;
            var factors = Factors;
            var blockCount = ((uint)(factors.Length / PrimeWheel30.ChannelCount));
            var visited = 0U;

            while (cursor < blockCount) {
                if ((visited++ & 65535U) == 0) { cancellationToken.ThrowIfCancellationRequested(); }

                var step = steps[phase];
                var coordinate = ((cursor * PrimeWheel30.ChannelCount) + step.Bit);
                var factor = factors[coordinate];

                if (factor != 0) {
                    var least = factor & PrimeLeafKernels.CombinatorialFactorMask;

                    if ((least == PrimeLeafKernels.CombinatorialFactorMask) && (prime <= squareRoot)) { least = ((int)prime); }
                    factors[coordinate] = ((ushort)(least | ((factor ^ PrimeLeafKernels.CombinatorialNegativeMoebius) & PrimeLeafKernels.CombinatorialNegativeMoebius)));
                }
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
            }
            if (prime > squareRoot) { return; }

            var square = (prime * prime);

            _ = PrimeWheel30.TryChannel(channel: out channel, residue: ((byte)(square % PrimeWheel30.Modulus)));
            steps = PrimeLeafKernels.CombinatorialTables.Steps.Slice(length: PrimeWheel30.ChannelCount, start: (channel * PrimeWheel30.ChannelCount));
            quotient = (square / PrimeWheel30.Modulus);
            cursor = quotient;
            phase = 0;
            while (cursor < blockCount) {
                var step = steps[phase];

                factors[((cursor * PrimeWheel30.ChannelCount) + step.Bit)] = 0;
                cursor += ((quotient * step.Gap) + step.Carry);
                phase = (phase + 1) & (PrimeWheel30.ChannelCount - 1);
            }
        }
    }
}

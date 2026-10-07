using System.Numerics;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.Cli.Bench;

// The historical seven-witness control stays in the benchmark assembly after production switches to
// Baillie–PSW. Its minimal Montgomery substrate transcribes the old production arithmetic deliberately:
// this is a performance control, not an independent correctness oracle. Setup uses the UInt128 reference.
internal static class PrimeMillerRabinBaseline {
    private static ReadOnlySpan<ulong> Witnesses => [2, 325, 9375, 28178, 450775, 9780504, 1795265022];

    internal static bool IsPrime(ulong value) {
        if (value <= uint.MaxValue) { return ((uint)value).IsPrime(); }
        if (((value & 1UL) == 0UL) || ((value % 3UL) == 0UL) || ((value % 5UL) == 0UL)) { return false; }
        return IsPrimeCandidate(value: value);
    }
    internal static bool IsPrimeCandidate(ulong value) {
        if (value < 2UL) { return false; }
        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>= twoExponent;
        var ring = new MontgomeryRing(modulus: value);

        foreach (var witness in Witnesses) {
            if (!PassesWitness(oddPart: oddPart, ring: in ring, twoExponent: twoExponent, witness: witness)) { return false; }
        }
        return true;
    }

    private static bool PassesWitness(in MontgomeryRing ring, ulong oddPart, int twoExponent, ulong witness) {
        var residue = (witness % ring.Modulus);

        if (residue == 0UL) { return true; }
        var power = ring.Power(value: ring.Encode(value: residue), exponent: oddPart);
        var negativeOne = ring.NegativeOne;

        if ((power == ring.One) || (power == negativeOne)) { return true; }
        for (var round = 1; (round < twoExponent); ++round) {
            power = ring.Multiply(left: power, right: power);
            if (power == negativeOne) { return true; }
        }
        return false;
    }

    private readonly struct MontgomeryRing {
        internal MontgomeryRing(ulong modulus) {
            var one = ((ulong.MaxValue % modulus) + 1UL);

            Modulus = modulus;
            ModulusInverse = unchecked((0UL - modulus.ModularInverse()));
            One = one;
            RadixSquared = ((ulong)((((UInt128)one) * one) % modulus));
        }

        internal ulong Modulus { get; }

        private ulong ModulusInverse { get; }

        internal ulong One { get; }
        internal ulong NegativeOne => (Modulus - One);

        private ulong RadixSquared { get; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Encode(ulong value) => Multiply(left: value, right: RadixSquared);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Multiply(ulong left, ulong right) {
            var product = (((UInt128)left) * right);
            var low = ((ulong)product);
            var factor = unchecked((low * ModulusInverse));
            var addend = (((ulong)((((UInt128)factor) * Modulus) >>> 64)) + ((low != 0) ? 1UL : 0UL));
            var sum = (((ulong)(product >>> 64)) + addend);
            var inRange = unchecked((0UL - ((sum >= addend) & (sum < Modulus) ? 1UL : 0UL)));

            return ((sum - Modulus) + (Modulus & inRange));
        }
        internal ulong Power(ulong value, ulong exponent) {
            var power = value;
            var result = One;

            while (0UL != exponent) {
                if (0UL != (exponent & 1UL)) { result = Multiply(left: result, right: power); }
                exponent >>>= 1;
                if (0UL != exponent) { power = Multiply(left: power, right: power); }
            }
            return result;
        }
    }
}

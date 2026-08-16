using System.Numerics;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Deterministic operand generation, byte-for-byte the same seeds and regimes as the standalone
// quadratic-algebra bench (which no longer builds), so numbers taken before it left remain comparable with these.
// No wall-clock and no unseeded randomness anywhere in setup.
internal static class Operands {
    // That bench's master seed.
    public const int Seed = 0x5140A;
    public const ulong Modulus = 1_000_000_007UL;
    public const int WidePairCount = 1024;

    // Static-readonly algebra descriptors (the "static" variant): initonly fields the JIT may fold P/Q to constants
    // after tier-up. Named exactly like that bench's Alg.* helpers.
    public static readonly QuadraticAlgebra<FixedQ4816> ComplexFused = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.NegativeOne);
    public static readonly QuadraticAlgebra<FixedQ4816> SplitFused = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.One);
    public static readonly QuadraticAlgebra<FixedQ4816> DualFix = QuadraticAlgebra<FixedQ4816>.Create(p: FixedQ4816.Zero, q: FixedQ4816.Zero);
    public static readonly QuadraticAlgebra<FixedQuaternion> DualQuat = QuadraticAlgebra<FixedQuaternion>.Create(p: FixedQuaternion.AdditiveIdentity, q: FixedQuaternion.AdditiveIdentity);
    public static readonly ulong NonSquare = QuadraticExtensionField64.SmallestNonSquare(baseField: PrimeField64.Create(modulus: Modulus));
    public static readonly QuadraticAlgebra<ModP> ModAlg = QuadraticAlgebra<ModP>.Create(p: new ModP(Modulus: Modulus, Value: 0UL), q: new ModP(Modulus: Modulus, Value: NonSquare));

    // Raw with |value| in [2^31, 2^40): forces the Int128 wide path in both the fused kernel and the hand-written type.
    public static long WideRaw(Random rng) {
        var magnitude = rng.NextInt64(maxValue: (1L << 40), minValue: (1L << 31));

        return ((0 == rng.Next(maxValue: 2)) ? magnitude : -magnitude);
    }
    // Raw with |value| < 2^31: narrow operands — the window in which every kernel measured here, multiply and norm,
    // hand-written and generic, takes its long fast tier instead of the Int128 fallback.
    public static long NarrowRaw(Random rng) =>
        rng.NextInt64(maxValue: ((1L << 31) - 1L), minValue: -((1L << 31) - 1L));
    public static ulong RandomResidue(Random rng) =>
        ((ulong)rng.NextInt64(maxValue: ((long)Modulus), minValue: 0L));
}
// A residue in F_p carried as a value plus its modulus (copied verbatim from that bench and its
// verifier), so the generic-math operators reduce without a static modulus. Identity elements carry modulus zero; each
// binary operation adopts the operative (non-zero) modulus of its operands.
internal readonly record struct ModP(ulong Value, ulong Modulus)
    : IAdditionOperators<ModP, ModP, ModP>,
      ISubtractionOperators<ModP, ModP, ModP>,
      IMultiplyOperators<ModP, ModP, ModP>,
      IUnaryNegationOperators<ModP, ModP>,
      IAdditiveIdentity<ModP, ModP>,
      IMultiplicativeIdentity<ModP, ModP> {
    static ModP IAdditiveIdentity<ModP, ModP>.AdditiveIdentity => new(Modulus: 0UL, Value: 0UL);
    static ModP IMultiplicativeIdentity<ModP, ModP>.MultiplicativeIdentity => new(Modulus: 0UL, Value: 1UL);

    public static ModP operator +(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked((left.Value + right.Value)), Modulus: 0UL); }

        var sum = (left.Value + right.Value);

        return new(Modulus: modulus, Value: ((sum >= modulus) ? (sum - modulus) : sum));
    }
    public static ModP operator -(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked((left.Value - right.Value)), Modulus: 0UL); }

        return new(Value: ((left.Value >= right.Value) ? (left.Value - right.Value) : ((left.Value + modulus) - right.Value)), Modulus: modulus);
    }
    public static ModP operator *(ModP left, ModP right) {
        var modulus = Operative(left: left, right: right);

        if (0UL == modulus) { return new(Value: unchecked((left.Value * right.Value)), Modulus: 0UL); }

        return new(Value: ((ulong)((((UInt128)left.Value) * right.Value) % modulus)), Modulus: modulus);
    }
    public static ModP operator -(ModP value) {
        if ((0UL == value.Modulus) || (0UL == value.Value)) { return new(Value: unchecked((0UL - value.Value)), Modulus: value.Modulus); }

        return new(Value: (value.Modulus - value.Value), Modulus: value.Modulus);
    }

    private static ulong Operative(ModP left, ModP right) =>
        Math.Max(val1: left.Modulus, val2: right.Modulus);
}

using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for <see cref="NumberTheoreticTransform"/> and <see cref="NumberTheoreticTransformPlan"/>. Every statement is either an EXACT
/// field-arithmetic identity or agreement with <see cref="Oracles"/>' shared-nothing <see cref="BigInteger"/>
/// arithmetic — nothing here is a bound, because nothing in the subject rounds. <see cref="LawRegistry"/> invokes
/// each claim below as a Default-tier law.
/// </summary>
internal static class NttClaims {
    // The lengths every sweep runs at: the two degenerate transforms, a handful of small powers of two, and one past
    // any single machine word's worth of butterflies.
    private static readonly int[] Lengths = [1, 2, 4, 8, 16, 64, 256, 1024];

    /// <summary>Fills a length-<paramref name="length"/> sequence with reduced field elements from a seeded
    /// <see cref="Pcg32XshRr"/> stream, so a sweep is deterministic without repeating one operand pattern.</summary>
    /// <param name="length">The sequence length.</param>
    /// <param name="stream">The generator stream id, so two sequences drawn for one case do not share content.</param>
    /// <returns>The generated sequence.</returns>
    private static ulong[] Sequence(int length, ulong stream) {
        var rng = Pcg32XshRr.Create(state: 0x4E5454_2D4657544DUL, stream: stream);
        var values = new ulong[length];

        for (var i = 0; (i < length); ++i) {
            var raw = (((ulong)rng.NextUInt32()) << 32) | rng.NextUInt32();

            values[i] = NumberTheoreticTransform.Field.Reduce(value: raw);
        }

        return values;
    }
    /// <summary>Runs an action that must throw, and reports what it did instead.</summary>
    private static string? Refuses(Action action, Type type, string? parameterName, string what) {
        try {
            action();
        } catch (Exception thrown) when (type.IsInstanceOfType(o: thrown)) {
            if (parameterName is null) { return null; }

            return (((thrown is ArgumentException argument) && (argument.ParamName == parameterName))
                ? null
                : $"{what} threw {thrown.GetType().Name} naming '{(thrown as ArgumentException)?.ParamName}' rather than '{parameterName}'");
        } catch (Exception thrown) {
            return $"{what} threw {thrown.GetType().Name} rather than {type.Name}";
        }

        return $"{what} did not throw at all";
    }

    /// <summary>Proves <see cref="NumberTheoreticTransform.Modulus"/> is prime, that
    /// <c>Modulus - 1</c> factors as <c>PrimeFactor * 2^MaximumLog2Length</c> with <c>PrimeFactor</c> itself prime,
    /// and that <see cref="NumberTheoreticTransform.PrimitiveRoot"/> generates the whole multiplicative group — the
    /// Pocklington-style certificate for a two-prime-factor group order: a generator candidate is primitive exactly
    /// when it is not one at the order divided by EITHER prime factor.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? PrimeAndPrimitiveRoot() {
        const ulong PrimeFactor = 262111UL;
        var modulus = NumberTheoreticTransform.Modulus;
        var wideModulus = new BigInteger(value: modulus);

        if (!PrimeField64.IsPrime(value: modulus)) {
            return $"NumberTheoreticTransform.Modulus ({modulus}) is not prime by PrimeField64.IsPrime";
        }

        if (!Oracles.ExactPrimality(value: modulus)) {
            return $"NumberTheoreticTransform.Modulus ({modulus}) is not prime by the independent trial-division/strong-round sieve";
        }

        if (!PrimeField64.IsPrime(value: PrimeFactor)) {
            return $"the declared odd factor {PrimeFactor} of Modulus - 1 is not prime by PrimeField64.IsPrime";
        }

        if (!Oracles.ExactPrimality(value: PrimeFactor)) {
            return $"the declared odd factor {PrimeFactor} of Modulus - 1 is not prime by the independent sieve";
        }

        var reconstructed = ((PrimeFactor << NumberTheoreticTransform.MaximumLog2Length) + 1UL);

        if (reconstructed != modulus) {
            return $"PrimeFactor * 2^MaximumLog2Length + 1 = {reconstructed}, not Modulus ({modulus})";
        }

        // Independent order check: BigInteger.ModPow, sharing no code with PrimeField64.Pow's Montgomery-ring chain.
        var order = (wideModulus - BigInteger.One);
        var root = new BigInteger(value: NumberTheoreticTransform.PrimitiveRoot);
        var full = BigInteger.ModPow(exponent: order, modulus: wideModulus, value: root);

        if (!full.IsOne) {
            return $"PrimitiveRoot^(Modulus - 1) = {full}, not one, so it is not even a group element of the right order";
        }

        var atHalfOrder = BigInteger.ModPow(exponent: (order / 2), modulus: wideModulus, value: root);

        if (atHalfOrder.IsOne) {
            return "PrimitiveRoot^((Modulus - 1) / 2) = 1, so PrimitiveRoot's order divides (Modulus - 1) / 2 and it is not primitive";
        }

        var atFactorOrder = BigInteger.ModPow(exponent: (order / PrimeFactor), modulus: wideModulus, value: root);

        if (atFactorOrder.IsOne) {
            return "PrimitiveRoot^((Modulus - 1) / PrimeFactor) = 1, so PrimitiveRoot's order divides (Modulus - 1) / PrimeFactor and it is not primitive";
        }

        return null;
    }
    /// <summary>Proves <see cref="NumberTheoreticTransform.Inverse"/> undoes <see cref="NumberTheoreticTransform.Forward"/>
    /// EXACTLY — bit-for-bit, not within a bound, because every step is exact field arithmetic — at every swept
    /// length.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripExact() {
        foreach (var length in Lengths) {
            var plan = NumberTheoreticTransformPlan.Create(length: length);
            var original = Sequence(length: length, stream: ((ulong)length));
            var working = ((ulong[])original.Clone());

            NumberTheoreticTransform.Forward(plan: plan, values: working);
            NumberTheoreticTransform.Inverse(plan: plan, values: working);

            for (var i = 0; (i < length); ++i) {
                if (working[i] != original[i]) {
                    return $"length {length}, index {i}: round trip gave {working[i]}, expected {original[i]}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="NumberTheoreticTransform.Forward"/> is EXACTLY linear —
    /// <c>Forward(a) + Forward(b) == Forward(a + b)</c> pointwise, in the field — at every swept length.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityExact() {
        var field = NumberTheoreticTransform.Field;

        foreach (var length in Lengths) {
            var plan = NumberTheoreticTransformPlan.Create(length: length);
            var a = Sequence(length: length, stream: (100UL + ((ulong)length)));
            var b = Sequence(length: length, stream: (200UL + ((ulong)length)));
            var sum = new ulong[length];

            for (var i = 0; (i < length); ++i) { sum[i] = field.Add(left: a[i], right: b[i]); }

            var forwardA = ((ulong[])a.Clone());
            var forwardB = ((ulong[])b.Clone());
            var forwardSum = sum;

            NumberTheoreticTransform.Forward(plan: plan, values: forwardA);
            NumberTheoreticTransform.Forward(plan: plan, values: forwardB);
            NumberTheoreticTransform.Forward(plan: plan, values: forwardSum);

            for (var i = 0; (i < length); ++i) {
                var expected = field.Add(left: forwardA[i], right: forwardB[i]);

                if (forwardSum[i] != expected) {
                    return $"length {length}, bin {i}: Forward(a+b) = {forwardSum[i]}, Forward(a)+Forward(b) = {expected}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="NumberTheoreticTransform.Convolve"/> matches <see cref="Oracles.CyclicConvolutionModulus"/>'s
    /// O(N^2) definition-form sum EXACTLY, at every swept length, over both freshly drawn content and the modulus'
    /// own boundary values (zero and one below the modulus).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ConvolutionVsOracle() {
        foreach (var length in Lengths) {
            var plan = NumberTheoreticTransformPlan.Create(length: length);
            var a = Sequence(length: length, stream: (300UL + ((ulong)length)));
            var b = Sequence(length: length, stream: (400UL + ((ulong)length)));

            // Boundary values on the first couple of lanes, wherever the length has them: zero and the modulus' own
            // top representative, which the O(N^2) reference's reduction must handle the same way the field does.
            a[0] = 0UL;
            b[0] = (NumberTheoreticTransform.Modulus - 1UL);

            if (length > 1) {
                a[1] = (NumberTheoreticTransform.Modulus - 1UL);
                b[1] = 0UL;
            }

            var expected = Oracles.CyclicConvolutionModulus(left: a, modulus: NumberTheoreticTransform.Modulus, right: b);
            var left = ((ulong[])a.Clone());
            var right = ((ulong[])b.Clone());
            var actual = new ulong[length];

            NumberTheoreticTransform.Convolve(destination: actual, left: left, plan: plan, right: right);

            for (var i = 0; (i < length); ++i) {
                if (actual[i] != expected[i]) {
                    return $"length {length}, index {i}: Convolve gave {actual[i]}, O(N^2) oracle gives {expected[i]}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves every documented refusal: a non-power-of-two, zero or negative
    /// <see cref="NumberTheoreticTransformPlan.Create"/> length; and a <see cref="NumberTheoreticTransform.Forward"/>,
    /// <see cref="NumberTheoreticTransform.Inverse"/>, <see cref="NumberTheoreticTransform.Convolve"/> or
    /// <see cref="NumberTheoreticTransform.PointwiseMultiply"/> span whose length does not match the plan (or, for
    /// <c>PointwiseMultiply</c>, does not match its sibling spans).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LengthRefusals() {
        return (Refuses(action: () => NumberTheoreticTransformPlan.Create(length: 0), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "NumberTheoreticTransformPlan.Create(0)") ??
               (Refuses(action: () => NumberTheoreticTransformPlan.Create(length: -4), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "NumberTheoreticTransformPlan.Create(-4)") ??
               (Refuses(action: () => NumberTheoreticTransformPlan.Create(length: 3), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "NumberTheoreticTransformPlan.Create(3) (not a power of two)") ??
               (Refuses(action: () => NumberTheoreticTransformPlan.Create(length: 6), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "NumberTheoreticTransformPlan.Create(6) (not a power of two)") ??
               (Refuses(action: () => NumberTheoreticTransform.Forward(plan: NumberTheoreticTransformPlan.Create(length: 8), values: new ulong[4]), type: typeof(ArgumentException), parameterName: "values", what: "Forward with a mis-sized span") ??
               (Refuses(action: () => NumberTheoreticTransform.Inverse(plan: NumberTheoreticTransformPlan.Create(length: 8), values: new ulong[16]), type: typeof(ArgumentException), parameterName: "values", what: "Inverse with a mis-sized span") ??
               (Refuses(action: () => NumberTheoreticTransform.Convolve(plan: NumberTheoreticTransformPlan.Create(length: 8), left: new ulong[8], right: new ulong[4], destination: new ulong[8]), type: typeof(ArgumentException), parameterName: "right", what: "Convolve with a mis-sized right span") ??
               (Refuses(action: () => NumberTheoreticTransform.Convolve(plan: NumberTheoreticTransformPlan.Create(length: 8), left: new ulong[8], right: new ulong[8], destination: new ulong[4]), type: typeof(ArgumentException), parameterName: "destination", what: "Convolve with a mis-sized destination span") ??
               Refuses(action: () => NumberTheoreticTransform.PointwiseMultiply(destination: new ulong[8], left: new ulong[8], right: new ulong[4]), type: typeof(ArgumentException), parameterName: "right", what: "PointwiseMultiply with mismatched spans")))))))));
    }
}

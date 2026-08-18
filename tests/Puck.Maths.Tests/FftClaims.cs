using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>
/// Claims for <see cref="FixedFourierTransform"/> and <see cref="FixedFourierPlan"/>. Every statement is either an
/// EXACT identity (impulse/DC/Nyquist bins, where the twiddle involved is exactly <c>±1</c> or <c>±i</c> so the
/// one-rounding multiply never rounds) or a MEASURED bound: the twiddle multiplies round, so round trip, linearity
/// and Parseval hold only within a ceiling this file measures and freezes rather than asserts on faith.
/// <see cref="LawRegistry"/> invokes each claim below as a Default-tier law; the <c>*Deep</c> siblings mirror the
/// same statements at strictly stronger operands (longer lengths) as Deep-tier laws.
/// </summary>
internal static class FftClaims {
    // Operand magnitude every sweep draws within: raw in [-2^20, 2^20], about +/-16.0. Round-trip, linearity and
    // Parseval error scale with operand amplitude (the twiddles' own ~0.5 ULP quantization error multiplies through
    // the signal), so the bounds below are measured and frozen AT this envelope; a caller using a larger amplitude
    // should expect proportionally larger absolute error.
    private const long AmplitudeRaw = (1L << 20);

    private static readonly int[] DefaultLengths = [1, 2, 4, 8, 16, 32, 64, 128, 256];
    private static readonly int[] DeepLengths = [512, 1024, 2048, 4096];

    // Measured maxima (see NTT-FFT lane report) at the envelope above, with margin.
    private const long RoundTripBoundDefault = 64L;
    private const long LinearityBoundDeep = 96L;
    private const long LinearityBoundDefault = 24L;
    private const long RoundTripBoundDeep = 96L;

    private static readonly BigInteger ParsevalBoundDefault = 400_000_000_000L;
    private static readonly BigInteger ParsevalBoundDeep = 60_000_000_000_000L;
    // Small lengths only: DirectDft is O(N^2) SinCos calls and stays inside the Default-tier suite's budget only at
    // these sizes; larger lengths are covered by the round-trip/linearity/Parseval bounds instead.
    private static readonly int[] DirectSumLengths = [1, 2, 4, 8, 16, 32];

    private static FixedComplex[] Sequence(int length, ulong stream) {
        var rng = Pcg32XshRr.Create(state: 0x4658_5254_2D46_4654UL, stream: stream);
        var values = new FixedComplex[length];

        for (var i = 0; (i < length); ++i) {
            var realRaw = (((long)rng.NextUInt32(maximum: ((uint)((2 * AmplitudeRaw) + 1)), minimum: 0U)) - AmplitudeRaw);
            var imaginaryRaw = (((long)rng.NextUInt32(maximum: ((uint)((2 * AmplitudeRaw) + 1)), minimum: 0U)) - AmplitudeRaw);

            values[i] = new(Real: FixedQ4816.FromRawBits(value: realRaw), Imaginary: FixedQ4816.FromRawBits(value: imaginaryRaw));
        }

        return values;
    }
    // The direct O(N^2) DFT sum, built from the SAME FixedComplex.FromAngle/operator* kernel FixedFourierPlan uses,
    // but with no bit-reversal and no butterfly decomposition: bin k accumulates one running FixedComplex sum of
    // x[n] * FromAngle(-2*pi*k*n/N), each term rounding once and the running sum adding exactly. A different
    // summation SCHEDULE over the identical kernel, so agreement pins the radix-2 indexing rather than the kernel.
    private static FixedComplex[] DirectDft(ReadOnlySpan<FixedComplex> values) {
        var n = values.Length;
        var result = new FixedComplex[n];
        var turn = ((-2.0 * Math.PI) / n);

        for (var k = 0; (k < n); ++k) {
            var sum = FixedComplex.AdditiveIdentity;

            for (var index = 0; (index < n); ++index) {
                var angle = FixedQ4816.FromDouble(value: ((turn * k) * index));
                var twiddle = FixedComplex.FromAngle(angle: angle);

                sum += (values[index] * twiddle);
            }

            result[k] = sum;
        }

        return result;
    }
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

    /// <summary>Proves an impulse's spectrum is EXACTLY flat at <see cref="FixedQ4816.One"/>, a DC input's spectrum
    /// is EXACTLY <c>N * value</c> at bin zero and EXACTLY zero elsewhere, and an alternating <c>±1</c> (Nyquist)
    /// input's spectrum is EXACTLY <c>N * value</c> at the top bin and EXACTLY zero elsewhere — every twiddle these
    /// three inputs touch is exactly <c>±1</c> or <c>±i</c>, so <see cref="FixedComplex.operator *(FixedComplex, FixedComplex)"/>
    /// never rounds and the statement is exact rather than bounded.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ImpulseDcNyquistExact() {
        foreach (var length in new[] { 2, 4, 8, 16, 32, 64, 128, 256 }) {
            var plan = FixedFourierPlan.Create(length: length);

            var impulse = new FixedComplex[length];

            impulse[0] = new(Real: FixedQ4816.One, Imaginary: FixedQ4816.Zero);
            FixedFourierTransform.Forward(plan: plan, values: impulse);

            foreach (var bin in impulse) {
                if (bin != FixedComplex.MultiplicativeIdentity) {
                    return $"length {length}: impulse spectrum bin is {bin}, expected exactly one";
                }
            }

            var dc = new FixedComplex[length];
            var dcValue = FixedQ4816.FromInteger(value: 3);

            for (var i = 0; (i < length); ++i) { dc[i] = new(Real: dcValue, Imaginary: FixedQ4816.Zero); }

            FixedFourierTransform.Forward(plan: plan, values: dc);

            var expectedDc = new FixedComplex(Real: (dcValue * FixedQ4816.FromInteger(value: length)), Imaginary: FixedQ4816.Zero);

            if (dc[0] != expectedDc) {
                return $"length {length}: DC bin 0 is {dc[0]}, expected exactly {expectedDc}";
            }

            for (var k = 1; (k < length); ++k) {
                if (dc[k] != FixedComplex.AdditiveIdentity) {
                    return $"length {length}: DC bin {k} is {dc[k]}, expected exactly zero";
                }
            }

            var nyquist = new FixedComplex[length];
            var nyquistValue = FixedQ4816.FromInteger(value: 5);

            for (var i = 0; (i < length); ++i) {
                nyquist[i] = new(Real: ((0 == (i & 1)) ? nyquistValue : -nyquistValue), Imaginary: FixedQ4816.Zero);
            }

            FixedFourierTransform.Forward(plan: plan, values: nyquist);

            var topBin = (length / 2);
            var expectedNyquist = new FixedComplex(Real: (nyquistValue * FixedQ4816.FromInteger(value: length)), Imaginary: FixedQ4816.Zero);

            if (nyquist[topBin] != expectedNyquist) {
                return $"length {length}: Nyquist bin {topBin} is {nyquist[topBin]}, expected exactly {expectedNyquist}";
            }

            for (var k = 0; (k < length); ++k) {
                if (k == topBin) { continue; }

                if (nyquist[k] != FixedComplex.AdditiveIdentity) {
                    return $"length {length}: Nyquist bin {k} is {nyquist[k]}, expected exactly zero";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedFourierTransform.Inverse"/> recovers <see cref="FixedFourierTransform.Forward"/>'s
    /// input within a measured raw-Q16 ULP bound, over <see cref="DefaultLengths"/> at the module's amplitude
    /// envelope.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripBound() =>
        RoundTripBoundCore(bound: RoundTripBoundDefault, lengths: DefaultLengths, saltBase: 1_000UL);
    /// <summary>MIRROR of <see cref="RoundTripBound"/> at <see cref="DeepLengths"/> — strictly longer transforms,
    /// where the per-stage accumulation runs deeper.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RoundTripBoundDeepMirror() =>
        RoundTripBoundCore(bound: RoundTripBoundDeep, lengths: DeepLengths, saltBase: 2_000UL);

    private static string? RoundTripBoundCore(int[] lengths, long bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var original = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var working = ((FixedComplex[])original.Clone());

            FixedFourierTransform.Forward(plan: plan, values: working);
            FixedFourierTransform.Inverse(plan: plan, values: working);

            for (var i = 0; (i < length); ++i) {
                var dr = Math.Abs(value: (working[i].Real.Value - original[i].Real.Value));
                var di = Math.Abs(value: (working[i].Imaginary.Value - original[i].Imaginary.Value));

                if ((dr > bound) || (di > bound)) {
                    return $"length {length}, index {i}: round trip error ({dr},{di}) raw ULPs exceeds the bound {bound}";
                }
            }
        }

        return null;
    }

    /// <summary>Proves <see cref="FixedFourierTransform.Forward"/> is linear within a measured raw-Q16 ULP bound —
    /// <c>Forward(a) + Forward(b)</c> against <c>Forward(a + b)</c> — over <see cref="DefaultLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityBound() =>
        LinearityBoundCore(bound: LinearityBoundDefault, lengths: DefaultLengths, saltBase: 3_000UL);
    /// <summary>MIRROR of <see cref="LinearityBound"/> at <see cref="DeepLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LinearityBoundDeepMirror() =>
        LinearityBoundCore(bound: LinearityBoundDeep, lengths: DeepLengths, saltBase: 4_000UL);

    private static string? LinearityBoundCore(int[] lengths, long bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var a = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var b = Sequence(length: length, stream: ((saltBase + 500_000UL) + ((ulong)length)));
            var sum = new FixedComplex[length];

            for (var i = 0; (i < length); ++i) { sum[i] = (a[i] + b[i]); }

            FixedFourierTransform.Forward(plan: plan, values: a);
            FixedFourierTransform.Forward(plan: plan, values: b);
            FixedFourierTransform.Forward(plan: plan, values: sum);

            for (var k = 0; (k < length); ++k) {
                var expected = (a[k] + b[k]);
                var dr = Math.Abs(value: (sum[k].Real.Value - expected.Real.Value));
                var di = Math.Abs(value: (sum[k].Imaginary.Value - expected.Imaginary.Value));

                if ((dr > bound) || (di > bound)) {
                    return $"length {length}, bin {k}: linearity error ({dr},{di}) raw ULPs exceeds the bound {bound}";
                }
            }
        }

        return null;
    }

    /// <summary>Proves Parseval's identity — <c>sum |x_n|^2 == (1/N) sum |X_k|^2</c> — holds within a measured raw
    /// Q32-unit bound, computed EXACTLY in <see cref="BigInteger"/> (raw components are integers, so their squares
    /// and sums are exact; no floating point enters the comparison) over <see cref="DefaultLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParsevalBound() =>
        ParsevalBoundCore(bound: ParsevalBoundDefault, lengths: DefaultLengths, saltBase: 5_000UL);
    /// <summary>MIRROR of <see cref="ParsevalBound"/> at <see cref="DeepLengths"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ParsevalBoundDeepMirror() =>
        ParsevalBoundCore(bound: ParsevalBoundDeep, lengths: DeepLengths, saltBase: 6_000UL);

    private static string? ParsevalBoundCore(int[] lengths, BigInteger bound, ulong saltBase) {
        foreach (var length in lengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var original = Sequence(length: length, stream: (saltBase + ((ulong)length)));
            var timeEnergy = BigInteger.Zero;

            foreach (var v in original) {
                timeEnergy += ((((BigInteger)v.Real.Value) * v.Real.Value) + (((BigInteger)v.Imaginary.Value) * v.Imaginary.Value));
            }

            var spectrum = ((FixedComplex[])original.Clone());

            FixedFourierTransform.Forward(plan: plan, values: spectrum);

            var freqEnergy = BigInteger.Zero;

            foreach (var v in spectrum) {
                freqEnergy += ((((BigInteger)v.Real.Value) * v.Real.Value) + (((BigInteger)v.Imaginary.Value) * v.Imaginary.Value));
            }

            var error = BigInteger.Abs(value: (freqEnergy - (((BigInteger)length) * timeEnergy)));

            if (error > bound) {
                return $"length {length}: Parseval error {error} raw Q32 units exceeds the bound {bound}";
            }
        }

        return null;
    }

    /// <summary>Proves two runs of <see cref="FixedFourierTransform.Forward"/> and
    /// <see cref="FixedFourierTransform.Inverse"/> on identical input, in this process, return bit-identical
    /// results — same-process purity, never a pinned historical value.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SelfReferentialBitIdentity() {
        foreach (var length in DefaultLengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var seed = Sequence(length: length, stream: (7_000UL + ((ulong)length)));

            var firstForward = ((FixedComplex[])seed.Clone());
            var secondForward = ((FixedComplex[])seed.Clone());

            FixedFourierTransform.Forward(plan: plan, values: firstForward);
            FixedFourierTransform.Forward(plan: plan, values: secondForward);

            if (!firstForward.AsSpan().SequenceEqual(other: secondForward)) {
                return $"length {length}: two Forward runs on identical input disagreed";
            }

            var firstInverse = ((FixedComplex[])firstForward.Clone());
            var secondInverse = ((FixedComplex[])firstForward.Clone());

            FixedFourierTransform.Inverse(plan: plan, values: firstInverse);
            FixedFourierTransform.Inverse(plan: plan, values: secondInverse);

            if (!firstInverse.AsSpan().SequenceEqual(other: secondInverse)) {
                return $"length {length}: two Inverse runs on identical input disagreed";
            }
        }

        return null;
    }
    /// <summary>Proves the radix-2 butterfly network agrees with the direct O(N^2) DFT sum built from the SAME
    /// <see cref="FixedComplex"/> kernel but a different summation schedule (no bit-reversal, no stage
    /// decomposition), within a measured bound — this pins the butterfly indexing and twiddle assignment rather
    /// than the kernel, which the round-trip and impulse/DC/Nyquist statements already pin.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Radix2VsDirectSum() {
        // Measured: a calibration run at this envelope observed a worst case of 107 raw ULPs at length 32 — the
        // direct sum accumulates one independent ~0.5 ULP rounding per term (up to N per bin) against the radix-2
        // network's one per stage (log2(N) per bin), so the two schedules' errors do not cancel. 200 is that
        // measurement with margin, still small next to the size a genuine indexing defect would show.
        const long Bound = 200L;

        foreach (var length in DirectSumLengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var input = Sequence(length: length, stream: (8_000UL + ((ulong)length)));
            var radix2 = ((FixedComplex[])input.Clone());

            FixedFourierTransform.Forward(plan: plan, values: radix2);

            var direct = DirectDft(values: input);

            for (var k = 0; (k < length); ++k) {
                var dr = Math.Abs(value: (radix2[k].Real.Value - direct[k].Real.Value));
                var di = Math.Abs(value: (radix2[k].Imaginary.Value - direct[k].Imaginary.Value));

                if ((dr > Bound) || (di > Bound)) {
                    return $"length {length}, bin {k}: radix-2 gave {radix2[k]}, direct sum gives {direct[k]}, error ({dr},{di}) exceeds the bound {Bound}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves <see cref="FixedFourierTransform.ForwardReal"/> is EXACTLY a zero-imaginary embed followed by
    /// <see cref="FixedFourierTransform.Forward"/>, and <see cref="FixedFourierTransform.InverseReal"/> is EXACTLY
    /// <see cref="FixedFourierTransform.Inverse"/> followed by discarding the imaginary part — a wiring statement,
    /// not an accuracy one, so it is exact rather than bounded.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RealWrappersAreFaithfulEmbeddings() {
        foreach (var length in DefaultLengths) {
            var plan = FixedFourierPlan.Create(length: length);
            var complexInput = Sequence(length: length, stream: (9_000UL + ((ulong)length)));
            var real = new FixedQ4816[length];

            for (var i = 0; (i < length); ++i) { real[i] = complexInput[i].Real; }

            var viaReal = new FixedComplex[length];

            FixedFourierTransform.ForwardReal(destination: viaReal, plan: plan, real: real);

            var embedded = new FixedComplex[length];

            for (var i = 0; (i < length); ++i) { embedded[i] = new(Real: real[i], Imaginary: FixedQ4816.Zero); }

            FixedFourierTransform.Forward(plan: plan, values: embedded);

            if (!viaReal.AsSpan().SequenceEqual(other: embedded)) {
                return $"length {length}: ForwardReal disagrees with Forward on a zero-imaginary embed";
            }

            var spectrumForReal = ((FixedComplex[])viaReal.Clone());
            var spectrumForComplex = ((FixedComplex[])viaReal.Clone());
            var extractedReal = new FixedQ4816[length];

            FixedFourierTransform.InverseReal(destination: extractedReal, plan: plan, spectrum: spectrumForReal);
            FixedFourierTransform.Inverse(plan: plan, values: spectrumForComplex);

            for (var i = 0; (i < length); ++i) {
                if (extractedReal[i] != spectrumForComplex[i].Real) {
                    return $"length {length}, index {i}: InverseReal gave {extractedReal[i]}, Inverse's real part gives {spectrumForComplex[i].Real}";
                }
            }
        }

        return null;
    }
    /// <summary>Proves every documented refusal: a non-power-of-two or non-positive
    /// <see cref="FixedFourierPlan.Create"/> length, and a mis-sized span to
    /// <see cref="FixedFourierTransform.Forward"/>, <see cref="FixedFourierTransform.Inverse"/>,
    /// <see cref="FixedFourierTransform.ForwardReal"/> or <see cref="FixedFourierTransform.InverseReal"/>.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LengthRefusals() {
        var plan = FixedFourierPlan.Create(length: 8);

        return (Refuses(action: () => FixedFourierPlan.Create(length: 0), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedFourierPlan.Create(0)") ??
               (Refuses(action: () => FixedFourierPlan.Create(length: -8), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedFourierPlan.Create(-8)") ??
               (Refuses(action: () => FixedFourierPlan.Create(length: 5), type: typeof(ArgumentOutOfRangeException), parameterName: "length", what: "FixedFourierPlan.Create(5) (not a power of two)") ??
               (Refuses(action: () => FixedFourierTransform.Forward(plan: plan, values: new FixedComplex[4]), type: typeof(ArgumentException), parameterName: "values", what: "Forward with a mis-sized span") ??
               (Refuses(action: () => FixedFourierTransform.Inverse(plan: plan, values: new FixedComplex[16]), type: typeof(ArgumentException), parameterName: "values", what: "Inverse with a mis-sized span") ??
               (Refuses(action: () => FixedFourierTransform.ForwardReal(destination: new FixedComplex[8], plan: plan, real: new FixedQ4816[4]), type: typeof(ArgumentException), parameterName: "real", what: "ForwardReal with a mis-sized real span") ??
               (Refuses(action: () => FixedFourierTransform.ForwardReal(destination: new FixedComplex[4], plan: plan, real: new FixedQ4816[8]), type: typeof(ArgumentException), parameterName: "destination", what: "ForwardReal with a mis-sized destination span") ??
               (Refuses(action: () => FixedFourierTransform.InverseReal(destination: new FixedQ4816[8], plan: plan, spectrum: new FixedComplex[4]), type: typeof(ArgumentException), parameterName: "spectrum", what: "InverseReal with a mis-sized spectrum span") ??
               Refuses(action: () => FixedFourierTransform.InverseReal(destination: new FixedQ4816[4], plan: plan, spectrum: new FixedComplex[8]), type: typeof(ArgumentException), parameterName: "destination", what: "InverseReal with a mis-sized destination span")))))))));
    }
}

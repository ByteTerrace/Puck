using System.Diagnostics;
using System.Numerics;

namespace Puck.Maths.Tests;

/// <summary>Claim bodies for the <c>symmetric-solve</c> family — <see cref="FixedSymmetricSolve"/>'s scale-free 2×2
/// and 3×3 symmetric solve and invert kernels. The VsOracle bodies compare against <see cref="Oracles"/>'s
/// independent <see cref="BigInteger"/> Cramer's-rule reference, which shares no preconditioning, no shift, and no
/// rounding structure with the subject — but DOES share the same expanded determinant and adjugate cofactor
/// formulas, so a sign error transcribed identically into both is invisible to that comparison and to the residual
/// laws below it (a necessary bound only — see <see cref="Solve2ResidualWithinEnvelope"/>'s remarks). The VsBareiss
/// bodies (<see cref="Solve2VsBareiss"/> and its three siblings) compare against
/// <see cref="Oracles.TryBareissSolveSymmetric2"/>'s fraction-free elimination family instead, which shares no
/// cofactor or determinant formula with either the subject or the adjugate oracle, and
/// <see cref="Solve3AllCofactorsExactValue"/> pins a hand-derived constant where all six cofactors are individually
/// load-bearing — together the actual defence against a shared cofactor sign transcription (third symmetric-solve
/// validation, Finding 2). Every agreement claim folds its operands onto a moderate band
/// (<see cref="FoldModerate"/>): the subject's shared preconditioning shift is EXACT (a lossless left shift, or no
/// shift at all) precisely when every group operand's own magnitude is already STRICTLY BELOW <c>2^(target+1)</c> —
/// AT that power of two the leading bit already sits one past the target, so the shift is -1, not zero — and the
/// fold keeps every draw inside that envelope, which is the realistic regime an effective-mass matrix at any
/// Q-anything scale lives in. Outside that envelope — an entry whose OWN raw magnitude already reaches the target,
/// forcing a lossy rounding right-shift before any real work starts — the ratio is only approximately preserved
/// (the same bounded-precision contract <see cref="FixedVectorMath.Normalize(long, long, long)"/> already carries at extreme scales),
/// so exact agreement is not claimed there; <see cref="Solve3ExtremeMagnitudeAgrees"/> and
/// <see cref="InvertLargeMagnitudeEnvelopeRefuses"/> exercise that corner directly instead of leaking into these
/// swept claims.</summary>
internal static class SymmetricSolveClaims {
    /// <summary>Folds a raw onto roughly a 31-bit-magnitude band by reinterpreting its low 32 bits as a signed
    /// integer. Applied to every operand of every oracle-agreement claim in this file: it keeps the preconditioning
    /// shift <see cref="FixedSymmetricSolve"/> derives comfortably non-negative for every draw (31 bits is far below
    /// both families' target — 40 for the 3×3 family, 61 for the 2×2 — so the shift is always a lossless LEFT shift),
    /// which is both the realistic regime (effective-mass entries at a Q48.16-like scale) and the one regime where
    /// exact bit-for-bit agreement with the unpreconditioned oracle is provable. The large-magnitude corner is
    /// covered by its own dedicated claims instead of leaking into these.</summary>
    private static long FoldModerate(long raw) => unchecked((long)(int)raw);

    private static int OutputShift(long raw) => (int)((ulong)raw % 49UL);

    /// <summary>Solve2 against the independent oracle, at a caller-chosen output scale swept from the domain's own
    /// second lane vector, over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lane 0 = a, 1 = b, 2 = d, 3 = rhsX, 4 = rhsY, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve2VsOracle(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var rhsX = FoldModerate(raw: left[3]);
        var rhsY = FoldModerate(raw: left[4]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: rhsX, rhsY: rhsY, outputFractionShift: shift, x: out var sx, y: out var sy);
        var oracleOk = Oracles.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: rhsX, rhsY: rhsY, outputFractionShift: shift, x: out var ox, y: out var oy);

        if (subjectOk != oracleOk) {
            return $"solve2 outcome mismatch at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}): subject={subjectOk} oracle={oracleOk}";
        }

        // The refusal contract is "false AND every output zero" — checked directly against the subject here
        // (never merely against the oracle, which could share the same defect) even when both refuse.
        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L))
                ? null
                : $"solve2 refused at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}) but left a non-zero output: ({sx},{sy})");
        }

        return (((sx == ox) && (sy == oy))
            ? null
            : $"solve2 mismatch at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}): subject=({sx},{sy}) oracle=({ox},{oy})");
    }

    /// <summary>Solve2 against <see cref="Oracles.TryBareissSolveSymmetric2"/> — the SECOND, algorithmically
    /// independent reference this family carries, added because <see cref="Solve2VsOracle"/>'s adjugate oracle
    /// transcribes the SAME determinant formula as the subject and so cannot discriminate a shared cofactor sign
    /// error, and because the residual laws (<see cref="Solve2ResidualWithinEnvelope"/>) are a necessary bound
    /// only, not a correctness oracle. The concrete <c>C13</c> witness is 3×3 evidence and belongs to
    /// <see cref="Solve3VsBareiss"/>; this 2×2 law covers the corresponding shared-transcription risk in its own
    /// determinant and two numerator formulas.
    /// Fraction-free Bareiss elimination never expands a determinant or names a cofactor, so a sign error
    /// transcribed identically into the subject and the adjugate oracle cannot also survive here. Over
    /// <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lane 0 = a, 1 = b, 2 = d, 3 = rhsX, 4 = rhsY, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve2VsBareiss(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var rhsX = FoldModerate(raw: left[3]);
        var rhsY = FoldModerate(raw: left[4]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: rhsX, rhsY: rhsY, outputFractionShift: shift, x: out var sx, y: out var sy);
        var bareissOk = Oracles.TryBareissSolveSymmetric2(a: a, b: b, d: d, rhsX: rhsX, rhsY: rhsY, outputFractionShift: shift, x: out var bx, y: out var by);

        if (subjectOk != bareissOk) {
            return $"solve2 vs Bareiss outcome mismatch at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}): subject={subjectOk} bareiss={bareissOk}";
        }

        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L))
                ? null
                : $"solve2 vs Bareiss: refused at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}) but left a non-zero output: ({sx},{sy})");
        }

        return (((sx == bx) && (sy == by))
            ? null
            : $"solve2 vs Bareiss mismatch at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}): subject=({sx},{sy}) bareiss=({bx},{by})");
    }

    /// <summary>Solve3 against the independent oracle — the primary demonstration of the six-term triple-product
    /// determinant's bit budget, over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f; lanes 6..8 = rhsX, rhsY, rhsZ; each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3VsOracle(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var rhsX = FoldModerate(raw: left[6]);
        var rhsY = FoldModerate(raw: left[7]);
        var rhsZ = FoldModerate(raw: left[8]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: shift, x: out var sx, y: out var sy, z: out var sz);
        var oracleOk = Oracles.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: shift, x: out var ox, y: out var oy, z: out var oz);

        if (subjectOk != oracleOk) {
            return $"solve3 outcome mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})): subject={subjectOk} oracle={oracleOk}";
        }

        // See Solve2VsOracle's own note: checked directly against the subject even when both refuse.
        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L) && (sz == 0L))
                ? null
                : $"solve3 refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})) but left a non-zero output: ({sx},{sy},{sz})");
        }

        return (((sx == ox) && (sy == oy) && (sz == oz))
            ? null
            : $"solve3 mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})): subject=({sx},{sy},{sz}) oracle=({ox},{oy},{oz})");
    }

    /// <summary>Solve3 against <see cref="Oracles.TryBareissSolveSymmetric3"/> — the 3×3 sibling of
    /// <see cref="Solve2VsBareiss"/>: the primary defence this family now has against a shared sign error in any
    /// of the six adjugate cofactors (<c>C11..C33</c>), which <see cref="Solve3VsOracle"/>'s adjugate oracle and
    /// <see cref="Solve3ResidualWithinEnvelope"/>'s forward-only residual bound cannot reliably catch — see both
    /// for the concrete <c>C13</c> witness. Over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f; lanes 6..8 = rhsX, rhsY, rhsZ; each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3VsBareiss(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var rhsX = FoldModerate(raw: left[6]);
        var rhsY = FoldModerate(raw: left[7]);
        var rhsZ = FoldModerate(raw: left[8]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: shift, x: out var sx, y: out var sy, z: out var sz);
        var bareissOk = Oracles.TryBareissSolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: shift, x: out var bx, y: out var by, z: out var bz);

        if (subjectOk != bareissOk) {
            return $"solve3 vs Bareiss outcome mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})): subject={subjectOk} bareiss={bareissOk}";
        }

        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L) && (sz == 0L))
                ? null
                : $"solve3 vs Bareiss: refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})) but left a non-zero output: ({sx},{sy},{sz})");
        }

        return (((sx == bx) && (sy == by) && (sz == bz))
            ? null
            : $"solve3 vs Bareiss mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})): subject=({sx},{sy},{sz}) bareiss=({bx},{by},{bz})");
    }

    /// <summary>Invert2 against the independent oracle, over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lanes 0..2 = a, b, d, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert2VsOracle(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: shift, invA: out var sa, invB: out var sb, invD: out var sd);
        var oracleOk = Oracles.TryInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: shift, invA: out var oa, invB: out var ob, invD: out var od);

        if (subjectOk != oracleOk) {
            return $"invert2 outcome mismatch at shift {shift} for (a={a}, b={b}, d={d}): subject={subjectOk} oracle={oracleOk}";
        }

        // See Solve2VsOracle's own note: checked directly against the subject even when both refuse.
        if (!subjectOk) {
            return (((sa == 0L) && (sb == 0L) && (sd == 0L))
                ? null
                : $"invert2 refused at shift {shift} for (a={a}, b={b}, d={d}) but left a non-zero output: ({sa},{sb},{sd})");
        }

        return (((sa == oa) && (sb == ob) && (sd == od))
            ? null
            : $"invert2 mismatch at shift {shift} for (a={a}, b={b}, d={d}): subject=({sa},{sb},{sd}) oracle=({oa},{ob},{od})");
    }

    /// <summary>Invert2 against <see cref="Oracles.TryBareissInvertSymmetric2"/> — the Invert sibling of
    /// <see cref="Solve2VsBareiss"/>, added for the same reason: <see cref="Invert2VsOracle"/>'s adjugate oracle
    /// shares the subject's own determinant formula. Over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lanes 0..2 = a, b, d, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert2VsBareiss(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: shift, invA: out var sa, invB: out var sb, invD: out var sd);
        var bareissOk = Oracles.TryBareissInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: shift, invA: out var ba, invB: out var bb, invD: out var bd);

        if (subjectOk != bareissOk) {
            return $"invert2 vs Bareiss outcome mismatch at shift {shift} for (a={a}, b={b}, d={d}): subject={subjectOk} bareiss={bareissOk}";
        }

        if (!subjectOk) {
            return (((sa == 0L) && (sb == 0L) && (sd == 0L))
                ? null
                : $"invert2 vs Bareiss: refused at shift {shift} for (a={a}, b={b}, d={d}) but left a non-zero output: ({sa},{sb},{sd})");
        }

        return (((sa == ba) && (sb == bb) && (sd == bd))
            ? null
            : $"invert2 vs Bareiss mismatch at shift {shift} for (a={a}, b={b}, d={d}): subject=({sa},{sb},{sd}) bareiss=({ba},{bb},{bd})");
    }

    /// <summary>Invert3 against the independent oracle, over <see cref="FoldModerate"/>'s moderate band.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert3VsOracle(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, outputFractionShift: shift, invA: out var sa, invB: out var sb, invC: out var sc, invD: out var sd, invE: out var se, invF: out var sf);
        var oracleOk = Oracles.TryInvertSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, outputFractionShift: shift, invA: out var oa, invB: out var ob, invC: out var oc, invD: out var od, invE: out var oe, invF: out var of);

        if (subjectOk != oracleOk) {
            return $"invert3 outcome mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}): subject={subjectOk} oracle={oracleOk}";
        }

        // See Solve2VsOracle's own note: checked directly against the subject even when both refuse.
        if (!subjectOk) {
            return (((sa == 0L) && (sb == 0L) && (sc == 0L) && (sd == 0L) && (se == 0L) && (sf == 0L))
                ? null
                : $"invert3 refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}) but left a non-zero output: ({sa},{sb},{sc},{sd},{se},{sf})");
        }

        return (((sa == oa) && (sb == ob) && (sc == oc) && (sd == od) && (se == oe) && (sf == of))
            ? null
            : $"invert3 mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}): subject=({sa},{sb},{sc},{sd},{se},{sf}) oracle=({oa},{ob},{oc},{od},{oe},{of})");
    }

    /// <summary>Invert3 against <see cref="Oracles.TryBareissInvertSymmetric3"/> — the 3×3 Invert sibling of
    /// <see cref="Solve3VsBareiss"/>: the primary defence Invert now has against a shared sign error in any of
    /// the six adjugate cofactors, which <see cref="Invert3VsOracle"/>'s adjugate oracle cannot catch (Invert
    /// returns the cofactors themselves, over <c>det</c>, so a shared cofactor sign error there is even more
    /// directly invisible to that oracle than Solve's numerators are). Over <see cref="FoldModerate"/>'s moderate
    /// band.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert3VsBareiss(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, outputFractionShift: shift, invA: out var sa, invB: out var sb, invC: out var sc, invD: out var sd, invE: out var se, invF: out var sf);
        var bareissOk = Oracles.TryBareissInvertSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, outputFractionShift: shift, invA: out var ba, invB: out var bb, invC: out var bc, invD: out var bd, invE: out var be, invF: out var bf);

        if (subjectOk != bareissOk) {
            return $"invert3 vs Bareiss outcome mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}): subject={subjectOk} bareiss={bareissOk}";
        }

        if (!subjectOk) {
            return (((sa == 0L) && (sb == 0L) && (sc == 0L) && (sd == 0L) && (se == 0L) && (sf == 0L))
                ? null
                : $"invert3 vs Bareiss: refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}) but left a non-zero output: ({sa},{sb},{sc},{sd},{se},{sf})");
        }

        return (((sa == ba) && (sb == bb) && (sc == bc) && (sd == bd) && (se == be) && (sf == bf))
            ? null
            : $"invert3 vs Bareiss mismatch at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}): subject=({sa},{sb},{sc},{sd},{se},{sf}) bareiss=({ba},{bb},{bc},{bd},{be},{bf})");
    }

    /// <summary>Proves the preconditioning earns its place: at these entries, an UNPRECONDITIONED triple product
    /// (three raw longs near <see cref="long.MaxValue"/>, multiplied directly at whatever width they landed in)
    /// would overflow — three factors near 2⁶³ multiply to roughly 2¹⁸⁹, sixty-two bits past even
    /// <see cref="Int128"/>'s 127-bit magnitude, let alone a 64-bit accumulator. <see cref="FixedSymmetricSolve"/>
    /// preconditions every entry down to at most bit 41 before forming any triple product, so it answers exactly —
    /// checked here against the same independent oracle used above, at these specific extreme, hand-picked
    /// operands.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3ExtremeMagnitudeAgrees() {
        const long a = long.MaxValue;
        const long d = long.MaxValue;
        const long f = long.MaxValue;
        const long b = (long.MinValue + 1L);
        const long c = (long.MinValue + 1L);
        const long e = (long.MinValue + 1L);
        const long rhsX = long.MinValue;
        const long rhsY = long.MaxValue;
        const long rhsZ = (long.MinValue + 1L);
        const int outputFractionShift = 16;

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: outputFractionShift, x: out var sx, y: out var sy, z: out var sz);
        var oracleOk = Oracles.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: outputFractionShift, x: out var ox, y: out var oy, z: out var oz);

        if (subjectOk != oracleOk) {
            return $"extreme-magnitude solve3 outcome mismatch: subject={subjectOk} oracle={oracleOk}";
        }

        if (!subjectOk) {
            return "extreme-magnitude solve3 refused, but the independent oracle expected a representable answer — the preconditioning is not doing its job";
        }

        return (((sx == ox) && (sy == oy) && (sz == oz))
            ? null
            : $"extreme-magnitude solve3 mismatch: subject=({sx},{sy},{sz}) oracle=({ox},{oy},{oz})");
    }

    /// <summary>Exactly singular 2×2 and 3×3 matrices, for both Solve and Invert, must refuse with every
    /// <see langword="out"/> parameter at zero — the one singularity a pure-integer kernel can observe.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? SingularMatricesRefuse() {
        // All zero: singular by construction at both sizes.
        if (FixedSymmetricSolve.TrySolveSymmetric2(a: 0L, b: 0L, d: 0L, rhsX: 5L, rhsY: 7L, outputFractionShift: 16, x: out var zx, y: out var zy) || (zx != 0L) || (zy != 0L)) {
            return "Solve2 failed to refuse (or left a non-zero out) on the all-zero matrix";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric2(a: 0L, b: 0L, d: 0L, outputFractionShift: 16, invA: out var za, invB: out var zb, invD: out var zd) || (za != 0L) || (zb != 0L) || (zd != 0L)) {
            return "Invert2 failed to refuse (or left a non-zero out) on the all-zero matrix";
        }

        // Rank-1: K = [[4,6],[6,9]], det = 36 - 36 = 0 exactly.
        if (FixedSymmetricSolve.TrySolveSymmetric2(a: 4L, b: 6L, d: 9L, rhsX: 1L, rhsY: 1L, outputFractionShift: 16, x: out var rx, y: out var ry) || (rx != 0L) || (ry != 0L)) {
            return "Solve2 failed to refuse (or left a non-zero out) on a rank-1 singular matrix";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric2(a: 4L, b: 6L, d: 9L, outputFractionShift: 16, invA: out var ra, invB: out var rb, invD: out var rd) || (ra != 0L) || (rb != 0L) || (rd != 0L)) {
            return "Invert2 failed to refuse (or left a non-zero out) on a rank-1 singular matrix";
        }

        // 3x3 all zero.
        if (FixedSymmetricSolve.TrySolveSymmetric3(a: 0L, b: 0L, c: 0L, d: 0L, e: 0L, f: 0L, rhsX: 1L, rhsY: 2L, rhsZ: 3L, outputFractionShift: 16, x: out var s3x, y: out var s3y, z: out var s3z) ||
            (s3x != 0L) || (s3y != 0L) || (s3z != 0L)) {
            return "Solve3 failed to refuse (or left a non-zero out) on the all-zero matrix";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric3(a: 0L, b: 0L, c: 0L, d: 0L, e: 0L, f: 0L, outputFractionShift: 16, invA: out var i3a, invB: out var i3b, invC: out var i3c, invD: out var i3d, invE: out var i3e, invF: out var i3f) ||
            (i3a != 0L) || (i3b != 0L) || (i3c != 0L) || (i3d != 0L) || (i3e != 0L) || (i3f != 0L)) {
            return "Invert3 failed to refuse (or left a non-zero out) on the all-zero matrix";
        }

        // 3x3 rank-2: row three is the sum of rows one and two, so det is exactly zero by construction
        // (K = [[2,1,3],[1,2,3],[3,3,6]] — the third row/column is row one plus row two).
        if (FixedSymmetricSolve.TrySolveSymmetric3(a: 2L, b: 1L, c: 3L, d: 2L, e: 3L, f: 6L, rhsX: 1L, rhsY: 1L, rhsZ: 1L, outputFractionShift: 16, x: out var rk3x, y: out var rk3y, z: out var rk3z) ||
            (rk3x != 0L) || (rk3y != 0L) || (rk3z != 0L)) {
            return "Solve3 failed to refuse (or left a non-zero out) on a rank-deficient singular matrix";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric3(a: 2L, b: 1L, c: 3L, d: 2L, e: 3L, f: 6L, outputFractionShift: 16, invA: out var rk3ia, invB: out var rk3ib, invC: out var rk3ic, invD: out var rk3id, invE: out var rk3ie, invF: out var rk3if) ||
            (rk3ia != 0L) || (rk3ib != 0L) || (rk3ic != 0L) || (rk3id != 0L) || (rk3ie != 0L) || (rk3if != 0L)) {
            return "Invert3 failed to refuse (or left a non-zero out) on a rank-deficient singular matrix";
        }

        return null;
    }

    /// <summary>Pins Invert's conservative large-magnitude refusal envelope (documented in
    /// <see cref="FixedSymmetricSolve"/>'s type remarks): a DIAGONAL matrix with every entry at
    /// <see cref="long.MinValue"/> — magnitude exactly <c>2⁶³</c>, the largest a raw <see cref="long"/> can carry —
    /// is comfortably non-singular (a diagonal determinant is the product of the diagonal, never zero here), yet at
    /// an output fraction shift of zero the internal preconditioning shift (<c>−23</c> for the 3×3 family, whose
    /// target bit is 40; <c>−2</c> for the 2×2 family, whose target bit is 61 — both derived from <c>2⁶³</c>'s own
    /// bit position, 63) leaves a negative combined fraction-bit count, so both refuse rather than manufacture a
    /// value from bits that were never there. The analogous Solve call, given a right-hand side to supply the
    /// missing homogeneity degree, is NOT bound by this envelope and must still answer — checked here at the SAME
    /// entries against the independent oracle, where the diagonal's symmetry makes the true ratio exactly one in
    /// every component regardless of how the shared shift rounds.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? InvertLargeMagnitudeEnvelopeRefuses() {
        const long huge = long.MinValue;
        const int outputFractionShift = 0;

        if (FixedSymmetricSolve.TryInvertSymmetric3(a: huge, b: 0L, c: 0L, d: huge, e: 0L, f: huge, outputFractionShift: outputFractionShift, invA: out var ia, invB: out var ib, invC: out var ic, invD: out var id, invE: out var ie, invF: out var iff) ||
            (ia != 0L) || (ib != 0L) || (ic != 0L) || (id != 0L) || (ie != 0L) || (iff != 0L)) {
            return "TryInvertSymmetric3 answered (rather than refusing, or left a non-zero out) at the documented large-magnitude envelope";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric2(a: huge, b: 0L, d: huge, outputFractionShift: outputFractionShift, invA: out var ia2, invB: out var ib2, invD: out var id2) ||
            (ia2 != 0L) || (ib2 != 0L) || (id2 != 0L)) {
            return "TryInvertSymmetric2 answered (rather than refusing, or left a non-zero out) at the documented large-magnitude envelope";
        }

        // Solve at the SAME diagonal entries must still answer: the right-hand side supplies the missing
        // homogeneity degree that makes Solve's ratio scale-invariant, so it is not bound by Invert's envelope.
        // Both sides must answer EXACTLY (1,1,1) — not merely agree with each other, and not merely both refuse —
        // because that is the declared claim (K·(1,1,1) = rhs by construction, an exact integer ratio no rounding
        // can move). Checking only outcome-and-equality would let both refuse, or let both agree on any equal
        // non-unit triple, pass silently; this pins the value the declaration promises.
        var solveOk = FixedSymmetricSolve.TrySolveSymmetric3(a: huge, b: 0L, c: 0L, d: huge, e: 0L, f: huge, rhsX: huge, rhsY: huge, rhsZ: huge, outputFractionShift: outputFractionShift, x: out var sx, y: out var sy, z: out var sz);
        var oracleOk = Oracles.TrySolveSymmetric3(a: huge, b: 0L, c: 0L, d: huge, e: 0L, f: huge, rhsX: huge, rhsY: huge, rhsZ: huge, outputFractionShift: outputFractionShift, x: out var ox, y: out var oy, z: out var oz);

        if (!solveOk) {
            return "TrySolveSymmetric3 at the same diagonal entries refused, but the right-hand side supplies the missing homogeneity degree Invert lacks — Solve is not bound by Invert's envelope and must answer exactly (1,1,1) here";
        }

        if (!oracleOk || (ox != 1L) || (oy != 1L) || (oz != 1L)) {
            return $"the independent oracle did not answer exactly (1,1,1) at the same diagonal entries (oracleOk={oracleOk}, oracle=({ox},{oy},{oz})) — the claim's own exact-value premise is wrong";
        }

        return (((sx == 1L) && (sy == 1L) && (sz == 1L))
            ? null
            : $"Solve3 at the same diagonal entries answered ({sx},{sy},{sz}), expected exactly (1,1,1)");
    }

    /// <summary>Pins the rank-one lossy-preconditioning corner directly: a matrix whose RAW determinant is EXACTLY
    /// zero (<c>u = 3000000001</c>, <c>v = 3000000000</c>; <c>K = [[u², uv], [uv, v²]]</c>, hand-verified rank one),
    /// but whose top bit sits at 62 — one past <see cref="FixedSymmetricSolve.Symmetric2TargetLeadingBit"/> — so the
    /// shared preconditioning shift is -1, a lossy right-shift that ties-to-even rounds the matrix into an apparently
    /// NONsingular preconditioned one. <see cref="FixedSymmetricSolve.TrySolveSymmetric2"/> and
    /// <see cref="FixedSymmetricSolve.TryInvertSymmetric2"/> must both refuse, checked directly against the "false
    /// AND every output zero" contract rather than against any oracle, which would only prove the two implementations
    /// agree — not that either is right.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LossyRankOneSingularRefuses() {
        const long a = 9000000006000000001L;
        const long b = 9000000003000000000L;
        const long d = 9000000000000000000L;

        if (FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: a, rhsY: d, outputFractionShift: 0, x: out var sx, y: out var sy) || (sx != 0L) || (sy != 0L)) {
            return $"TrySolveSymmetric2 fabricated a solution ({sx},{sy}) for the exactly-singular rank-one lossy-preconditioning witness instead of refusing";
        }

        if (FixedSymmetricSolve.TryInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: 1, invA: out var ia, invB: out var ib, invD: out var id) || (ia != 0L) || (ib != 0L) || (id != 0L)) {
            return $"TryInvertSymmetric2 fabricated an inverse ({ia},{ib},{id}) for the exactly-singular rank-one lossy-preconditioning witness instead of refusing";
        }

        return null;
    }

    /// <summary>Pins the EXACT lossless-preconditioning boundary the type's own remarks describe for the 2×2 family:
    /// magnitude STRICTLY BELOW <c>2^62</c> preconditions losslessly (shift <c>&gt;= 0</c>); AT <c>2^62</c> the
    /// leading bit already sits one past the target, so the shift is -1 and every entry at or above it rounds. One
    /// raw below the boundary must still answer EXACTLY; at the boundary the well-posed identity system's entries
    /// round to zero and the preconditioned determinant becomes zero, so the kernel honestly declines rather than
    /// answer something else — declining is never a fabrication (the RAW determinant here is 1, not 0), unlike
    /// <see cref="LossyRankOneSingularRefuses"/>'s corner where an exactly singular raw matrix would otherwise be
    /// answered.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? LosslessBoundaryIsExact() {
        const long a = 1L;
        const long b = 0L;
        const long d = 1L;
        const int outputFractionShift = 0;
        const long belowRhsY = ((1L << 62) - 1L);
        const long atRhsY = (1L << 62);

        if (!FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: 1L, rhsY: belowRhsY, outputFractionShift: outputFractionShift, x: out var belowX, y: out var belowY) ||
            (belowX != 1L) || (belowY != belowRhsY)) {
            return $"TrySolveSymmetric2 at the identity matrix with rhsY = 2^62 - 1 (inside the documented lossless band) did not answer exactly: ({belowX},{belowY})";
        }

        if (FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: 1L, rhsY: atRhsY, outputFractionShift: outputFractionShift, x: out var atX, y: out var atY) || (atX != 0L) || (atY != 0L)) {
            return $"TrySolveSymmetric2 at the identity matrix with rhsY = 2^62 answered ({atX},{atY}) instead of declining (or left a non-zero output) at the documented lossy boundary";
        }

        return null;
    }

    /// <summary>Pins <see cref="FusedArithmetic.TryDivideMagnitudeRounded"/>'s full-width contract directly,
    /// independent of any symmetric-solve kernel's own operand bounds (the four kernels' proven determinant budgets
    /// keep every caller comfortably inside the safe region, so this exercises the helper's OWN documented edges):
    /// negative counts (<c>-1</c> and <see cref="int.MinValue"/>) must refuse promptly with a cleared output before
    /// either the masked starting shift or the restoring loop can observe them; a requested fraction bit count that
    /// needs a bit the starting integer quotient's own zero-ness must not hide (129 fraction bits over a numerator
    /// smaller than its denominator); the SAME operands one bit inside the legal margin (127) must still answer
    /// exactly; and a numerator/denominator pair whose restoring-division remainder would double past
    /// <see cref="UInt128"/>'s own ceiling under an unconditional-double loop shape.
    /// Checked against <see cref="Oracles.RoundRationalTiesToEven"/>, which compares <c>2r</c> against <c>d</c> — a
    /// DIFFERENT tie formulation than the subject's own <c>d-r</c> versus <c>r</c> — so a shared rounding-compare
    /// defect cannot pass both.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? DivideMagnitudeRoundedFullWidthAgrees() {
        var refusalStart = Stopwatch.GetTimestamp();
        var minimumOk = FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: UInt128.One, denominatorMagnitude: UInt128.One, fractionBitCount: int.MinValue, quotient: out var minimumQuotient);
        var negativeOneOk = FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: UInt128.One, denominatorMagnitude: UInt128.One, fractionBitCount: -1, quotient: out var negativeOneQuotient);
        var refusalElapsed = Stopwatch.GetElapsedTime(startingTimestamp: refusalStart);

        if (minimumOk || (minimumQuotient != UInt128.Zero)) {
            return $"TryDivideMagnitudeRounded accepted int.MinValue or left a stale output instead of refusing: ok={minimumOk}, quotient={minimumQuotient}";
        }

        if (negativeOneOk || (negativeOneQuotient != UInt128.Zero)) {
            return $"TryDivideMagnitudeRounded accepted -1 or left a stale output instead of refusing: ok={negativeOneOk}, quotient={negativeOneQuotient}";
        }

        if (refusalElapsed >= TimeSpan.FromSeconds(value: 1.0)) {
            return $"TryDivideMagnitudeRounded took {refusalElapsed.TotalMilliseconds:F3} ms to refuse negative fraction counts; the guard must run before the restoring loop is initialized";
        }

        var smallNumerator = (UInt128.One << 121);
        var smallDenominator = (UInt128.One << 122);

        if (FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: smallNumerator, denominatorMagnitude: smallDenominator, fractionBitCount: 129, quotient: out var wrapped)) {
            return $"TryDivideMagnitudeRounded answered {wrapped} instead of refusing at fractionBitCount 129 with a zero starting integer quotient";
        }

        if (!FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: smallNumerator, denominatorMagnitude: smallDenominator, fractionBitCount: 127, quotient: out var margin)) {
            return "TryDivideMagnitudeRounded refused at fractionBitCount 127, one bit inside its own documented margin";
        }

        var expectedMargin = Oracles.RoundRationalTiesToEven(numerator: (((BigInteger)smallNumerator) << 127), denominator: (BigInteger)smallDenominator);

        if (margin != (UInt128)expectedMargin) {
            return $"TryDivideMagnitudeRounded at fractionBitCount 127 answered {margin}, the independent oracle expected {expectedMargin}";
        }

        var overflowNumerator = (UInt128.One << 127);
        var overflowDenominator = UInt128.MaxValue;

        if (!FusedArithmetic.TryDivideMagnitudeRounded(numeratorMagnitude: overflowNumerator, denominatorMagnitude: overflowDenominator, fractionBitCount: 1, quotient: out var overflowQuotient)) {
            return "TryDivideMagnitudeRounded refused at the full-width restoring-division witness, but the true quotient is representable";
        }

        var expectedOverflow = Oracles.RoundRationalTiesToEven(numerator: (((BigInteger)overflowNumerator) << 1), denominator: (BigInteger)overflowDenominator);

        return ((overflowQuotient == (UInt128)expectedOverflow)
            ? null
            : $"TryDivideMagnitudeRounded at the full-width restoring-division witness answered {overflowQuotient}, the independent oracle expected {expectedOverflow}");
    }

    /// <summary>Pins the exact witness for the "refusal leaves a stale non-zero output" defect: at
    /// <c>(a=1, b=0, d=1, rhsX=1, rhsY=1048576, outputFractionShift=48)</c> the first solved component
    /// (<c>x = 2^48</c>) rounds and fits the signed 64-bit carrier while the second (<c>y = 2^68</c>) does not, so
    /// <see cref="FixedSymmetricSolve.TrySolveSymmetric2"/> must refuse with BOTH outputs at zero — never leave the
    /// already-computed first component behind just because the second one overflows later.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? RefusalLeavesNoStaleOutput() {
        if (FixedSymmetricSolve.TrySolveSymmetric2(a: 1L, b: 0L, d: 1L, rhsX: 1L, rhsY: 1048576L, outputFractionShift: 48, x: out var x, y: out var y) || (x != 0L) || (y != 0L)) {
            return $"TrySolveSymmetric2 answered ({x},{y}) instead of refusing (or left a non-zero output) at the stale-output witness";
        }

        return null;
    }

    /// <summary>Proves Solve's answer actually reconstructs the right-hand side — <c>K·x</c> against
    /// <c>rhs·2^shift</c> — an invariant computed without consulting any cofactor formula, so it shares no
    /// TRANSCRIPTION with either the subject or <see cref="Oracles.TrySolveSymmetric2"/>.
    /// <para><b>This is a NECESSARY bound, not a correctness oracle — it does NOT discriminate a shared cofactor
    /// sign transcription, and must not be read as though it did.</b> The forward direction is exact and tight:
    /// each component's rounding error at most one half implies row <c>i</c>'s residual is at most
    /// <c>0.5·Σⱼ|K[i][j]|</c>. The CONVERSE does not hold — <c>error = K⁻¹·residual</c>, so an ill-conditioned
    /// <c>K</c> lets large, opposite-signed component errors cancel into a small residual. Concrete witness: on
    /// <c>K = [[-3,-3,-3],[-3,-3,-2],[-3,-2,-3]]</c>, <c>rhs = (-1,-2,1)</c>, flipping the 3×3 adjugate's
    /// <c>C13</c> sign identically in the subject and <see cref="Oracles.TrySolveSymmetric3"/> returns the WRONG
    /// vector <c>(1,2,-3)</c> (component errors of <c>5/3</c> and <c>-2</c> against the true <c>(-1,2,-1)</c>) while
    /// this residual bound still accepts it comfortably. The class of defect this law CANNOT catch is caught
    /// instead by <see cref="Oracles.TryBareissSolveSymmetric2"/>'s dedicated laws, which never expand a
    /// determinant or name a cofactor, and by <see cref="Solve3AllCofactorsExactValue"/>'s pinned constant.</para>
    /// Computed exactly in <see cref="BigInteger"/>: writing the true rational solution as <c>x_true</c>, the
    /// returned <c>x</c> is <c>round(x_true·2^shift)</c> ties-to-even with error at most one half
    /// (<see cref="FixedSymmetricSolve"/>'s private <c>TryFinishRatio</c>'s one rounding — exact because
    /// <see cref="FoldModerate"/> keeps the shared preconditioning shift non-negative, hence lossless, so the
    /// subject's computed ratio equals the true rational ratio exactly before that one rounding). Since
    /// <c>K·x_true = rhs</c> exactly, <c>K·x - rhs·2^shift = K·ε</c> where <c>ε</c> is the per-component
    /// rounding-error vector, each entry bounded by one half — so row <c>i</c>'s residual is bounded by
    /// <c>0.5·Σⱼ|K[i][j]|</c>, checked as <c>|2·residual| ≤ Σⱼ|K[i][j]|</c> to stay exact in integers. This bound is
    /// tight going FORWARD (achieved when every component's rounding error has the same sign as its coefficient),
    /// which is exactly why it can never be read backward as a proof of small component error.</summary>
    /// <param name="left">Lane 0 = a, 1 = b, 2 = d, 3 = rhsX, 4 = rhsY, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve2ResidualWithinEnvelope(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var rhsX = FoldModerate(raw: left[3]);
        var rhsY = FoldModerate(raw: left[4]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric2(a: a, b: b, d: d, rhsX: rhsX, rhsY: rhsY, outputFractionShift: shift, x: out var x, y: out var y);

        if (!subjectOk) {
            return (((x == 0L) && (y == 0L))
                ? null
                : $"solve2 residual: refused at shift {shift} for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}) but left a non-zero output: ({x},{y})");
        }

        BigInteger ba = a, bb = b, bd = d;
        var scaledRhsX = (((BigInteger)rhsX) << shift);
        var scaledRhsY = (((BigInteger)rhsY) << shift);

        var row0 = (((ba * x) + (bb * y)) - scaledRhsX);
        var row1 = (((bb * x) + (bd * y)) - scaledRhsY);
        var envelope0 = (BigInteger.Abs(value: ba) + BigInteger.Abs(value: bb));
        var envelope1 = (BigInteger.Abs(value: bb) + BigInteger.Abs(value: bd));

        if (BigInteger.Abs(value: (row0 * 2)) > envelope0) {
            return $"solve2 residual: row 0 residual {row0} exceeds the rounding envelope ±{envelope0}/2 for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}, shift={shift}, x=({x},{y}))";
        }

        if (BigInteger.Abs(value: (row1 * 2)) > envelope1) {
            return $"solve2 residual: row 1 residual {row1} exceeds the rounding envelope ±{envelope1}/2 for (a={a}, b={b}, d={d}, rhsX={rhsX}, rhsY={rhsY}, shift={shift}, x=({x},{y}))";
        }

        return null;
    }

    /// <summary>The 3×3 sibling of <see cref="Solve2ResidualWithinEnvelope"/> — see it for the envelope derivation
    /// AND the caveat that this bound is necessary, not sufficient: it cannot discriminate a shared cofactor sign
    /// transcription (a small residual does not imply small component error under cancellation).
    /// <see cref="Oracles.TryBareissSolveSymmetric3"/>'s dedicated laws and
    /// <see cref="Solve3AllCofactorsExactValue"/>'s pinned constant are what actually catch that class of
    /// defect.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f; lanes 6..8 = rhsX, rhsY, rhsZ; each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3ResidualWithinEnvelope(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var rhsX = FoldModerate(raw: left[6]);
        var rhsY = FoldModerate(raw: left[7]);
        var rhsZ = FoldModerate(raw: left[8]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: shift, x: out var x, y: out var y, z: out var z);

        if (!subjectOk) {
            return (((x == 0L) && (y == 0L) && (z == 0L))
                ? null
                : $"solve3 residual: refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ})) but left a non-zero output: ({x},{y},{z})");
        }

        BigInteger ba = a, bb = b, bc = c, bd = d, be = e, bf = f;
        var scaledRhsX = (((BigInteger)rhsX) << shift);
        var scaledRhsY = (((BigInteger)rhsY) << shift);
        var scaledRhsZ = (((BigInteger)rhsZ) << shift);

        var row0 = (((ba * x) + (bb * y) + (bc * z)) - scaledRhsX);
        var row1 = (((bb * x) + (bd * y) + (be * z)) - scaledRhsY);
        var row2 = (((bc * x) + (be * y) + (bf * z)) - scaledRhsZ);

        var envelope0 = (BigInteger.Abs(value: ba) + BigInteger.Abs(value: bb) + BigInteger.Abs(value: bc));
        var envelope1 = (BigInteger.Abs(value: bb) + BigInteger.Abs(value: bd) + BigInteger.Abs(value: be));
        var envelope2 = (BigInteger.Abs(value: bc) + BigInteger.Abs(value: be) + BigInteger.Abs(value: bf));

        if (BigInteger.Abs(value: (row0 * 2)) > envelope0) {
            return $"solve3 residual: row 0 residual {row0} exceeds the rounding envelope ±{envelope0}/2 for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ}), shift={shift}, x=({x},{y},{z}))";
        }

        if (BigInteger.Abs(value: (row1 * 2)) > envelope1) {
            return $"solve3 residual: row 1 residual {row1} exceeds the rounding envelope ±{envelope1}/2 for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ}), shift={shift}, x=({x},{y},{z}))";
        }

        if (BigInteger.Abs(value: (row2 * 2)) > envelope2) {
            return $"solve3 residual: row 2 residual {row2} exceeds the rounding envelope ±{envelope2}/2 for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, rhs=({rhsX},{rhsY},{rhsZ}), shift={shift}, x=({x},{y},{z}))";
        }

        return null;
    }

    /// <summary>Proves Invert's answer actually inverts <c>K</c> — <c>K·K⁻¹</c> against the identity, scaled by
    /// <c>2^outputFractionShift</c> (the scale Invert's own returned entries carry regardless of the internal
    /// preconditioning shift — see the type's remarks: the leftover <c>2^(−S)</c> factor is exactly what folding
    /// <c>S</c> into the division's own requested fraction-bit count cancels). See
    /// <see cref="Solve2ResidualWithinEnvelope"/> for the envelope argument; here each returned inverse entry
    /// carries its own independent rounding of at most one half, so entry <c>(i,j)</c> of <c>K·K⁻¹ − I·2^shift</c>
    /// is bounded by <c>0.5·Σₖ|K[i][k]|</c> — row <c>i</c>'s own envelope, shared by every column in that row.</summary>
    /// <param name="left">Lanes 0..2 = a, b, d, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert2ResidualWithinEnvelope(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var d = FoldModerate(raw: left[2]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric2(a: a, b: b, d: d, outputFractionShift: shift, invA: out var invA, invB: out var invB, invD: out var invD);

        if (!subjectOk) {
            return (((invA == 0L) && (invB == 0L) && (invD == 0L))
                ? null
                : $"invert2 residual: refused at shift {shift} for (a={a}, b={b}, d={d}) but left a non-zero output: ({invA},{invB},{invD})");
        }

        BigInteger ba = a, bb = b, bd = d;
        var identityScale = (BigInteger.One << shift);

        var r00 = (((ba * invA) + (bb * invB)) - identityScale);
        var r01 = ((ba * invB) + (bb * invD));
        var r10 = ((bb * invA) + (bd * invB));
        var r11 = (((bb * invB) + (bd * invD)) - identityScale);

        var envelopeRow0 = (BigInteger.Abs(value: ba) + BigInteger.Abs(value: bb));
        var envelopeRow1 = (BigInteger.Abs(value: bb) + BigInteger.Abs(value: bd));

        if (BigInteger.Abs(value: (r00 * 2)) > envelopeRow0) {
            return $"invert2 residual: (K·inv)[0,0] residual {r00} exceeds the rounding envelope ±{envelopeRow0}/2 for (a={a}, b={b}, d={d}, shift={shift}, inv=({invA},{invB},{invD}))";
        }

        if (BigInteger.Abs(value: (r01 * 2)) > envelopeRow0) {
            return $"invert2 residual: (K·inv)[0,1] residual {r01} exceeds the rounding envelope ±{envelopeRow0}/2 for (a={a}, b={b}, d={d}, shift={shift}, inv=({invA},{invB},{invD}))";
        }

        if (BigInteger.Abs(value: (r10 * 2)) > envelopeRow1) {
            return $"invert2 residual: (K·inv)[1,0] residual {r10} exceeds the rounding envelope ±{envelopeRow1}/2 for (a={a}, b={b}, d={d}, shift={shift}, inv=({invA},{invB},{invD}))";
        }

        if (BigInteger.Abs(value: (r11 * 2)) > envelopeRow1) {
            return $"invert2 residual: (K·inv)[1,1] residual {r11} exceeds the rounding envelope ±{envelopeRow1}/2 for (a={a}, b={b}, d={d}, shift={shift}, inv=({invA},{invB},{invD}))";
        }

        return null;
    }

    /// <summary>The 3×3 sibling of <see cref="Invert2ResidualWithinEnvelope"/> — see it for the envelope
    /// derivation.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f, each folded.</param>
    /// <param name="right">Lane 0 drives the requested output fraction shift.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Invert3ResidualWithinEnvelope(long[] left, long[] right) {
        var a = FoldModerate(raw: left[0]);
        var b = FoldModerate(raw: left[1]);
        var c = FoldModerate(raw: left[2]);
        var d = FoldModerate(raw: left[3]);
        var e = FoldModerate(raw: left[4]);
        var f = FoldModerate(raw: left[5]);
        var shift = OutputShift(raw: right[0]);

        var subjectOk = FixedSymmetricSolve.TryInvertSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, outputFractionShift: shift, invA: out var invA, invB: out var invB, invC: out var invC, invD: out var invD, invE: out var invE, invF: out var invF);

        if (!subjectOk) {
            return (((invA == 0L) && (invB == 0L) && (invC == 0L) && (invD == 0L) && (invE == 0L) && (invF == 0L))
                ? null
                : $"invert3 residual: refused at shift {shift} for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}) but left a non-zero output: ({invA},{invB},{invC},{invD},{invE},{invF})");
        }

        BigInteger ba = a, bb = b, bc = c, bd = d, be = e, bf = f;
        var identityScale = (BigInteger.One << shift);

        // K's rows are (a,b,c), (b,d,e), (c,e,f); the returned inverse's columns are the same triples over
        // (invA,invB,invC), (invB,invD,invE), (invC,invE,invF) — K·inv, entry by entry.
        var r00 = (((ba * invA) + (bb * invB) + (bc * invC)) - identityScale);
        var r01 = ((ba * invB) + (bb * invD) + (bc * invE));
        var r02 = ((ba * invC) + (bb * invE) + (bc * invF));
        var r10 = ((bb * invA) + (bd * invB) + (be * invC));
        var r11 = (((bb * invB) + (bd * invD) + (be * invE)) - identityScale);
        var r12 = ((bb * invC) + (bd * invE) + (be * invF));
        var r20 = ((bc * invA) + (be * invB) + (bf * invC));
        var r21 = ((bc * invB) + (be * invD) + (bf * invE));
        var r22 = (((bc * invC) + (be * invE) + (bf * invF)) - identityScale);

        var envelope0 = (BigInteger.Abs(value: ba) + BigInteger.Abs(value: bb) + BigInteger.Abs(value: bc));
        var envelope1 = (BigInteger.Abs(value: bb) + BigInteger.Abs(value: bd) + BigInteger.Abs(value: be));
        var envelope2 = (BigInteger.Abs(value: bc) + BigInteger.Abs(value: be) + BigInteger.Abs(value: bf));

        var rows = new[] {
            (Row: 0, Values: new[] { r00, r01, r02 }, Envelope: envelope0),
            (Row: 1, Values: new[] { r10, r11, r12 }, Envelope: envelope1),
            (Row: 2, Values: new[] { r20, r21, r22 }, Envelope: envelope2),
        };

        foreach (var group in rows) {
            for (var column = 0; (column < group.Values.Length); ++column) {
                if (BigInteger.Abs(value: (group.Values[column] * 2)) > group.Envelope) {
                    return $"invert3 residual: (K·inv)[{group.Row},{column}] residual {group.Values[column]} exceeds the rounding envelope ±{group.Envelope}/2 for (a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, shift={shift}, inv=({invA},{invB},{invC},{invD},{invE},{invF}))";
                }
            }
        }

        return null;
    }

    /// <summary>Pins an EXACT Solve3 value on a matrix with <c>C12 != 0</c> — <see cref="FixedSymmetricSolve"/>'s
    /// private adjugate's off-diagonal cofactor <c>c·e - b·f</c>, which vanishes on every diagonal matrix (the only shape
    /// <see cref="InvertLargeMagnitudeEnvelopeRefuses"/>'s exact-value leg exercises). The oracle-agreement laws in
    /// this file cannot expose a shared cofactor sign transcription: this hand-derived constant can, because it is
    /// stated independent of any oracle. <c>K = [[2,1,0],[1,3,0],[0,0,1]]</c> (det = 5, C12 = 0·0 - 1·1 = -1),
    /// <c>rhs = (0,1,0)</c>: the third equation forces <c>z = 0</c>; the remaining 2×2 system
    /// <c>2x+y=0, x+3y=1</c> gives the exact rational solution <c>x = -1/5, y = 2/5, z = 0</c>, which at
    /// <c>outputFractionShift = 16</c> rounds ties-to-even to <c>(-13107, 26214, 0)</c>
    /// (<c>-65536/5 = -13107.2</c> rounds down; <c>131072/5 = 26214.4</c> rounds down).</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3NonDiagonalExactValue() {
        const long a = 2L;
        const long b = 1L;
        const long c = 0L;
        const long d = 3L;
        const long e = 0L;
        const long f = 1L;
        const long rhsX = 0L;
        const long rhsY = 1L;
        const long rhsZ = 0L;
        const int outputFractionShift = 16;
        const long expectedX = -13107L;
        const long expectedY = 26214L;
        const long expectedZ = 0L;

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: outputFractionShift, x: out var x, y: out var y, z: out var z);

        if (!subjectOk) {
            return "non-diagonal exact-value solve3 refused, but K = [[2,1,0],[1,3,0],[0,0,1]] is non-singular (det = 5) and the true solution is representable at this scale";
        }

        return (((x == expectedX) && (y == expectedY) && (z == expectedZ))
            ? null
            : $"non-diagonal exact-value solve3 answered ({x},{y},{z}), expected exactly ({expectedX},{expectedY},{expectedZ}) at a matrix whose C12 cofactor is -1, not 0 — a cofactor sign transcription shared with an oracle would be invisible to the VsOracle laws but not to this pinned constant");
    }

    /// <summary>Pins an EXACT Solve3 value on a matrix where EVERY ONE of the six adjugate cofactors
    /// (<c>C11..C33</c>) is individually nonzero AND load-bearing — <see cref="Solve3NonDiagonalExactValue"/>
    /// exercises <c>C12</c> and <c>C22</c>: its matrix has <c>C11 = 3</c>, <c>C22 = 2</c>, and <c>C33 = 5</c>, but
    /// <c>rhs = (0,1,0)</c> makes only <c>C12</c> and <c>C22</c> load-bearing, while <c>C13 = C23 = 0</c>. That single
    /// witness therefore cannot stand in for all six; in particular it is blind to a <c>C13</c> sign flip.
    /// <c>K = [[2,1,1],[1,3,1],[1,1,2]]</c>
    /// (det = 7; <c>C11 = 5</c>, <c>C12 = -1</c>, <c>C13 = -2</c>, <c>C22 = 3</c>, <c>C23 = -1</c>, <c>C33 = 5</c> —
    /// every one nonzero), <c>rhs = (1,2,3)</c> (every component nonzero, so every cofactor appears with a
    /// nonzero coefficient in at least one output component's numerator: <c>C11</c> only in <c>x</c>'s numerator,
    /// <c>C33</c> only in <c>z</c>'s, and every other cofactor in two). The exact rational solution is
    /// <c>x = -3/7, y = 2/7, z = 11/7</c>; ties-to-even at <c>outputFractionShift = 16</c> gives EXACTLY
    /// <c>(-28087, 18725, 102985)</c>. Stated as a hand-derived constant, independent of
    /// <see cref="Oracles.TrySolveSymmetric3"/> and of <see cref="Oracles.TryBareissSolveSymmetric3"/> alike — this
    /// is a THIRD, deterministic layer that guarantees catching any single cofactor sign flip on every run, rather
    /// than relying on a swept domain's random draws to happen to land on a case where the flip is
    /// observable.</summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Solve3AllCofactorsExactValue() {
        const long a = 2L;
        const long b = 1L;
        const long c = 1L;
        const long d = 3L;
        const long e = 1L;
        const long f = 2L;
        const long rhsX = 1L;
        const long rhsY = 2L;
        const long rhsZ = 3L;
        const int outputFractionShift = 16;
        const long expectedX = -28087L;
        const long expectedY = 18725L;
        const long expectedZ = 102985L;

        var subjectOk = FixedSymmetricSolve.TrySolveSymmetric3(a: a, b: b, c: c, d: d, e: e, f: f, rhsX: rhsX, rhsY: rhsY, rhsZ: rhsZ, outputFractionShift: outputFractionShift, x: out var x, y: out var y, z: out var z);

        if (!subjectOk) {
            return "all-cofactors exact-value solve3 refused, but K = [[2,1,1],[1,3,1],[1,1,2]] is non-singular (det = 7) and the true solution is representable at this scale";
        }

        return (((x == expectedX) && (y == expectedY) && (z == expectedZ))
            ? null
            : $"all-cofactors exact-value solve3 answered ({x},{y},{z}), expected exactly ({expectedX},{expectedY},{expectedZ}) at a matrix where every one of C11..C33 is nonzero and load-bearing — no single cofactor sign flip should be able to leave this constant unchanged");
    }

    // The apply family's operand scales. Unlike Solve and Invert, Apply carries THREE independent fraction bit counts
    // (matrix, vector, result) because its motivating caller's operands genuinely sit at different scales — an inverse
    // inertia at a resolution-leaning one, an angular impulse at a range-leaning one.
    private static int ApplyScale(long raw) => ((int)(((ulong)raw) % 65UL));

    /// <summary>The symmetric 2×2 apply against the independent oracle, at swept operand scales. No fold is applied to
    /// the entries or the vector: unlike Solve, Apply has no preconditioning envelope to stay inside — its per-component
    /// sum of two raw products is bounded by <c>2^127</c> and is exact at the full signed range.</summary>
    /// <param name="left">Lanes 0..2 = a, b, d; lanes 3..4 = the vector.</param>
    /// <param name="right">Lanes 0..2 drive the matrix, vector and result fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Apply2VsOracle(long[] left, long[] right) {
        var a = left[0];
        var b = left[1];
        var d = left[2];
        var vX = left[3];
        var vY = left[4];
        var fractionBitsMatrix = ApplyScale(raw: right[0]);
        var fractionBitsVector = ApplyScale(raw: right[1]);
        var fractionBitsOut = ApplyScale(raw: right[2]);

        var subjectOk = FixedSymmetricSolve.TryApplySymmetric2(
            a: a,
            b: b,
            d: d,
            vX: vX,
            vY: vY,
            fractionBitsMatrix: fractionBitsMatrix,
            fractionBitsVector: fractionBitsVector,
            fractionBitsOut: fractionBitsOut,
            x: out var sx,
            y: out var sy
        );
        var oracleOk = Oracles.TryApplySymmetric2(
            a: a,
            b: b,
            d: d,
            vX: vX,
            vY: vY,
            fractionBitsMatrix: fractionBitsMatrix,
            fractionBitsVector: fractionBitsVector,
            fractionBitsOut: fractionBitsOut,
            x: out var ox,
            y: out var oy
        );
        var operands = $"(a={a}, b={b}, d={d}, v=({vX},{vY}) @ {fractionBitsMatrix}/{fractionBitsVector} -> {fractionBitsOut})";

        if (subjectOk != oracleOk) {
            return $"apply2 outcome mismatch at {operands}: subject={subjectOk} oracle={oracleOk}";
        }

        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L))
                ? null
                : $"apply2 refused at {operands} but left a non-zero output: ({sx},{sy})");
        }

        return (((sx == ox) && (sy == oy))
            ? null
            : $"apply2 mismatch at {operands}: subject=({sx},{sy}) oracle=({ox},{oy})");
    }

    /// <summary>The symmetric 3×3 apply against the independent oracle — the inverse-inertia-times-angular-impulse
    /// kernel itself. See <see cref="Apply2VsOracle"/> for the shared contract.</summary>
    /// <param name="left">Lanes 0..5 = a, b, c, d, e, f; lanes 6..8 = the vector.</param>
    /// <param name="right">Lanes 0..2 drive the matrix, vector and result fraction bit counts.</param>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? Apply3VsOracle(long[] left, long[] right) {
        var a = left[0];
        var b = left[1];
        var c = left[2];
        var d = left[3];
        var e = left[4];
        var f = left[5];
        var vX = left[6];
        var vY = left[7];
        var vZ = left[8];
        var fractionBitsMatrix = ApplyScale(raw: right[0]);
        var fractionBitsVector = ApplyScale(raw: right[1]);
        var fractionBitsOut = ApplyScale(raw: right[2]);

        var subjectOk = FixedSymmetricSolve.TryApplySymmetric3(
            a: a,
            b: b,
            c: c,
            d: d,
            e: e,
            f: f,
            vX: vX,
            vY: vY,
            vZ: vZ,
            fractionBitsMatrix: fractionBitsMatrix,
            fractionBitsVector: fractionBitsVector,
            fractionBitsOut: fractionBitsOut,
            x: out var sx,
            y: out var sy,
            z: out var sz
        );
        var oracleOk = Oracles.TryApplySymmetric3(
            a: a,
            b: b,
            c: c,
            d: d,
            e: e,
            f: f,
            vX: vX,
            vY: vY,
            vZ: vZ,
            fractionBitsMatrix: fractionBitsMatrix,
            fractionBitsVector: fractionBitsVector,
            fractionBitsOut: fractionBitsOut,
            x: out var ox,
            y: out var oy,
            z: out var oz
        );
        var operands = $"(a={a}, b={b}, c={c}, d={d}, e={e}, f={f}, v=({vX},{vY},{vZ}) @ {fractionBitsMatrix}/{fractionBitsVector} -> {fractionBitsOut})";

        if (subjectOk != oracleOk) {
            return $"apply3 outcome mismatch at {operands}: subject={subjectOk} oracle={oracleOk}";
        }

        if (!subjectOk) {
            return (((sx == 0L) && (sy == 0L) && (sz == 0L))
                ? null
                : $"apply3 refused at {operands} but left a non-zero output: ({sx},{sy},{sz})");
        }

        return (((sx == ox) && (sy == oy) && (sz == oz))
            ? null
            : $"apply3 mismatch at {operands}: subject=({sx},{sy},{sz}) oracle=({ox},{oy},{oz})");
    }

    /// <summary>Pins the apply family's all-or-nothing refusal and its symmetry, at hand-derived witnesses.
    /// <para>The identity matrix at Q16 applied to a Q16 vector returns that vector exactly. A matrix whose FIRST
    /// component overflows the raw while the second does not must refuse with BOTH outputs at zero, never leave the
    /// representable component behind — the same defect <see cref="RefusalLeavesNoStaleOutput"/> pins for Solve.
    /// The symmetric off-diagonal must actually be read as symmetric: <c>[[0,1],[1,0]]</c> applied to <c>(1,2)</c> is
    /// <c>(2,1)</c>, which a kernel reading the wrong entry into either row would get wrong.</para></summary>
    /// <returns>The counterexample text, or <see langword="null"/> when the claim holds.</returns>
    public static string? ApplyRefusalAndSymmetry() {
        const long one = 65536L;

        if (!FixedSymmetricSolve.TryApplySymmetric3(
                a: one,
                b: 0L,
                c: 0L,
                d: one,
                e: 0L,
                f: one,
                vX: 3L,
                vY: -5L,
                vZ: 7L,
                fractionBitsMatrix: 16,
                fractionBitsVector: 0,
                fractionBitsOut: 0,
                x: out var ix,
                y: out var iy,
                z: out var iz
            ) || (ix != 3L) || (iy != -5L) || (iz != 7L)) {
            return $"apply3 answered ({ix},{iy},{iz}) for the Q16 identity matrix against (3,-5,7), expected the vector back unchanged";
        }

        // [[0,1],[1,0]] swaps the components; reading the off-diagonal into only one row would answer (2,0) or (0,1).
        if (!FixedSymmetricSolve.TryApplySymmetric2(a: 0L, b: 1L, d: 0L, vX: 1L, vY: 2L, fractionBitsMatrix: 0, fractionBitsVector: 0, fractionBitsOut: 0, x: out var sx, y: out var sy) ||
            (sx != 2L) || (sy != 1L)) {
            return $"apply2 answered ({sx},{sy}) for the exchange matrix against (1,2), expected the swapped (2,1)";
        }

        // The first component is 2^63 (one past long.MaxValue) while the second is exactly representable; the contract
        // is to refuse with both outputs at zero.
        if (FixedSymmetricSolve.TryApplySymmetric2(
                a: (1L << 62),
                b: 0L,
                d: 1L,
                vX: 2L,
                vY: 1L,
                fractionBitsMatrix: 0,
                fractionBitsVector: 0,
                fractionBitsOut: 0,
                x: out var overflowX,
                y: out var overflowY
            ) || (overflowX != 0L) || (overflowY != 0L)) {
            return $"apply2 answered ({overflowX},{overflowY}) where its first component is 2^63, one past the raw carrier — it must refuse with both outputs at zero";
        }

        return null;
    }
}

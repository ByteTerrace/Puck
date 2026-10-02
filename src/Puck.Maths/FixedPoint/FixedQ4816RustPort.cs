using System.Globalization;
using System.Text;

namespace Puck.Maths;

/// <summary>
/// Emits the Rust port of <see cref="FixedQ4816"/>'s six algorithm-pinned transcendentals — <c>atan2</c>,
/// <c>sin</c>/<c>cos</c>, <c>exp2</c>, <c>log2</c>, and <c>pow</c> — for
/// <c>wasm/puck-stdlib/src</c>: <see cref="EmitGenerated"/> produces the ported functions plus
/// their tables and polynomial coefficients (<c>fixed_generated.rs</c>), and <see cref="EmitVectors"/>
/// produces known-answer vectors computed by calling the real <see cref="FixedQ4816"/> at generation time
/// (<c>fixed_vectors.rs</c>). Every numeric table/coefficient is read from the live
/// <see cref="FixedQ4816"/> type by name (<c>FixedQ4816.AtanTableQ61</c>, <c>FixedQ4816.SinCosTableQ60</c>,
/// ...) rather than transcribed as a literal, so this cannot silently drift from the host even if this
/// file is never touched again.
/// </summary>
/// <remarks>
/// This lives in <c>Puck.Maths</c> because it is the assembly that owns the tables and methods it reads —
/// an exporter belongs with the declarations it exports. It is one contributor to
/// <c>Puck.Scripting.WasmStdlibSources.All</c> — the ordered registry
/// the thin <c>puck wasm-stdlib</c> verb (<c>src/Puck.Cli</c>) iterates to write every registered artifact
/// to disk. The registry itself lives in <c>Puck.Scripting</c>, not here — it also aggregates
/// <c>Puck.Scripting.AddonAbiRustPort</c>, which reads types <c>Puck.Maths</c> cannot depend on without
/// inverting the repository's layering — so the emitter is a public surface consumed by that registry.
/// Deterministic and reproducible: no
/// <see cref="Random"/>, no wall clock, no environment-dependent input — an unchanged host produces
/// byte-identical output on every run. Nothing gates that today: the stage that compared the emitted text
/// against what is committed left the build, so drift is caught only by regenerating and reading the diff.
/// </remarks>
public static class FixedQ4816RustPort {
    private const ulong LcgIncrement = 1442695040888963407UL;
    // A fixed-constant LCG (PCG's own multiplier/increment), seeded by a literal — never re-seeded from
    // Random or the wall clock, so the sweep below is exactly reproducible.
    private const ulong LcgMultiplier = 6364136223846793005UL;
    private const ulong LcgSeed = 0x9E3779B97F4A7C15UL;
    private const int VectorTargetCount = 2000;

    private static ulong AdvanceState(ulong state) => unchecked(((state * LcgMultiplier) + LcgIncrement));
    // Every named branch in FixedQ4816.Pow/PowMagnitude, explicitly: zero base at every exponent sign, the
    // exact identity exponents (0, 1, -1) at both signs of base, the exact whole-exponent range boundary
    // (+-32/+-33), MinValue's own carve-out, and a fractional exponent on a negative base (must be
    // Zero — the real power is not a real number there). Split out of EmitPowVectors to keep that method's
    // size under the house metric ceiling.
    private static void AppendPowNamedBranchVectors(List<(long X, long Y, long Expected)> rows) {
        var one = FixedQ4816.One.Value;
        var pairs = new List<(long X, long Y)>
        {
            (0L, 0L), (0L, one), (0L, -one), (0L, (2L * one)), (0L, (-2L * one)),
            (one, 0L), (one, one), (one, -one), ((2L * one), one), ((2L * one), -one),
            (-one, 0L), (-one, one), (-one, -one), (-one, (2L * one)), (-one, (3L * one)),
            (-one, (32L * one)), (-one, (33L * one)), (-one, (-32L * one)), (-one, (-33L * one)),
            (-(2L * one), (32L * one)), (-(2L * one), (33L * one)),
            (long.MinValue, 0L), (long.MinValue, one), (long.MinValue, (2L * one)), (long.MinValue, -one),
            (long.MinValue, (3L * one)), (long.MinValue, (-2L * one)),
            (-one, (one / 2L)), // fractional exponent on a negative base -> Zero
            ((2L * one), (one / 2L)), // fractional exponent on a positive base -> ordinary Exp2/Log2 path
            (long.MaxValue, one), (long.MaxValue, -one), (long.MaxValue, (2L * one)),
        };

        foreach (var (x, y) in pairs) {
            rows.Add(item: (x, y, FixedQ4816.Pow(
                x: new FixedQ4816(Value: x),
                y: new FixedQ4816(Value: y)
            ).Value));
        }
    }
    // Every whole exponent the exact-power path takes (2 <= |n| <= 32) at bases that round — one and a half, three
    // quarters, a raw above one, pi, the smallest raw, a large raw — at both signs, so the port's limb power, its
    // closing shift, its division and both range verdicts each meet a vector that a wrong one would fail.
    private static void AppendPowWholeExponentVectors(List<(long X, long Y, long Expected)> rows) {
        var one = FixedQ4816.One.Value;
        // 98304, 32768 and 163840 are 2^15·o: their 17th powers are true ties; 131072 is the tie 2^-17 at n = -17.
        long[] bases = [98304L, 49152L, 65543L, 205887L, 1L, 12345678901L, 32768L, 163840L, 131072L, 65535L];

        foreach (var magnitude in bases) {
            foreach (var x in (long[])[magnitude, -magnitude]) {
                for (var n = -32L; (n <= 32L); ++n) {
                    if ((n >= -1L) && (n <= 1L)) {
                        continue;
                    }

                    rows.Add(item: (x, (n * one), FixedQ4816.Pow(
                        x: new FixedQ4816(Value: x),
                        y: new FixedQ4816(Value: (n * one))
                    ).Value));
                }
            }
        }
    }
    private static string EmitAtan2Vectors(ref ulong state) {
        var entries = new List<(long Y, long X, long Expected)>();
        var notable = NotableRawValues();

        foreach (var y in notable) {
            foreach (var x in notable) {
                entries.Add(item: (y, x, FixedQ4816.Atan2(
                    x: new FixedQ4816(Value: x),
                    y: new FixedQ4816(Value: y)
                ).Value));
            }
        }

        // Exact table-boundary hits: yMagnitude/xMagnitude = j/(128*scale) lands the internal ratio z
        // exactly on interval boundary j (z = j << 55) for every j, including j = 128 (clamped into the
        // shared top interval, index 127) — see fixed_generated.rs's atan2 for the mapping this mirrors.
        foreach (var scale in new long[] { 1L, 1_000L, 1_000_000L }) {
            foreach (var j in new long[] { 0L, 1L, 2L, 63L, 64L, 65L, 126L, 127L, 128L }) {
                var numerator = (j * scale);
                var denominator = (128L * scale);

                foreach (var ySign in new long[] { 1L, -1L }) {
                    foreach (var xSign in new long[] { 1L, -1L }) {
                        var y = (ySign * numerator);
                        var x = (xSign * denominator);

                        entries.Add(item: (y, x, FixedQ4816.Atan2(
                            x: new FixedQ4816(Value: x),
                            y: new FixedQ4816(Value: y)
                        ).Value));
                    }
                }
            }
        }

        var bound = (64L * FixedQ4816.One.Value);

        while (entries.Count < VectorTargetCount) {
            var y = (((entries.Count % 3) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )
            );
            var x = (((entries.Count % 5) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )
            );

            entries.Add(item: (y, x, FixedQ4816.Atan2(
                x: new FixedQ4816(Value: x),
                y: new FixedQ4816(Value: y)
            ).Value));
        }

        return FormatBinaryVectors(
            arrayName: "ATAN2_VECTORS",
            functionName: "atan2",
            paramA: "y",
            paramB: "x",
            rows: entries,
            testName: "atan2_vectors"
        );
    }
    private static string EmitExp2Vectors(ref ulong state) {
        var rows = new List<(long Value, long Expected)>();
        var notable = NotableRawValues();

        foreach (var value in notable) {
            rows.Add(item: (value, FixedQ4816.Exp2(value: new FixedQ4816(Value: value)).Value));
        }

        // Exact table-boundary hits: the low 16 bits directly select the interval, so f = 0 lands on
        // index 0 with zero residual, f = 127*512 lands on index 127 with zero residual, and the
        // neighbors either side exercise the adjacent interval.
        foreach (var k in new long[] { -20L, -18L, -17L, -16L, -1L, 0L, 1L, 2L, 10L, 20L, 30L, 40L, 45L, 46L }) {
            var baseValue = (k * FixedQ4816.One.Value);

            foreach (var f in new long[] { 0L, 1L, 511L, 512L, 513L, 65023L, 65024L, 65025L, 65534L, 65535L }) {
                var value = (baseValue + f);

                rows.Add(item: (value, FixedQ4816.Exp2(value: new FixedQ4816(Value: value)).Value));
            }
        }

        var bound = (60L * FixedQ4816.One.Value);

        while (rows.Count < VectorTargetCount) {
            var value = (((rows.Count % 3) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )
            );

            rows.Add(item: (value, FixedQ4816.Exp2(value: new FixedQ4816(Value: value)).Value));
        }

        return FormatUnaryVectors(
            arrayName: "EXP2_VECTORS",
            functionName: "exp2",
            rows: rows,
            testName: "exp2_vectors"
        );
    }
    private static string EmitLog2Vectors(ref ulong state) {
        var rows = new List<(long Value, long Expected)>();
        var notable = NotableRawValues();

        foreach (var value in notable) {
            rows.Add(item: (value, FixedQ4816.Log2(value: new FixedQ4816(Value: value)).Value));
        }

        rows.Add(item: (FixedQ4816.Epsilon.Value, FixedQ4816.Log2(value: FixedQ4816.Epsilon).Value));
        rows.Add(item: (FixedQ4816.MaxValue.Value, FixedQ4816.Log2(value: FixedQ4816.MaxValue).Value));

        // Exact table-boundary hits: for integerPart >= 7, raw = 2^integerPart + (index << (integerPart
        // - 7)) lands the mantissa's table index exactly on `index` with zero residual — see
        // fixed_generated.rs's log2_fraction_q61 for the mapping this mirrors.
        foreach (var integerPart in new[] { 7, 10, 16, 20, 24, 30, 32, 40, 47, 48, 55, 60, 61 }) {
            foreach (var index in new long[] { 0L, 1L, 2L, 63L, 64L, 125L, 126L, 127L }) {
                var value = ((1L << integerPart) + (index << (integerPart - 7)));

                rows.Add(item: (value, FixedQ4816.Log2(value: new FixedQ4816(Value: value)).Value));
            }
        }

        while (rows.Count < VectorTargetCount) {
            long value;

            if ((rows.Count % 7) == 0) {
                value = NextFullRangeRaw(state: ref state); // includes negatives/zero — keeps exercising the early return
            } else {
                value = (1L + Math.Abs(value: NextBoundedRaw(
                    bound: (1L << 50),
                    state: ref state
                )));
            }

            rows.Add(item: (value, FixedQ4816.Log2(value: new FixedQ4816(Value: value)).Value));
        }

        return FormatUnaryVectors(
            arrayName: "LOG2_VECTORS",
            functionName: "log2",
            rows: rows,
            testName: "log2_vectors"
        );
    }
    private static string EmitPowVectors(ref ulong state) {
        var rows = new List<(long X, long Y, long Expected)>();
        var notable = NotableRawValues();

        foreach (var x in notable) {
            foreach (var y in notable) {
                rows.Add(item: (x, y, FixedQ4816.Pow(
                    x: new FixedQ4816(Value: x),
                    y: new FixedQ4816(Value: y)
                ).Value));
            }
        }

        AppendPowNamedBranchVectors(rows: rows);
        AppendPowWholeExponentVectors(rows: rows);

        var bound = (64L * FixedQ4816.One.Value);

        while (rows.Count < VectorTargetCount) {
            var x = (((rows.Count % 3) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )
            );
            var y = (((rows.Count % 5) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )
            );

            if (x == 0L) {
                x = 1L; // 0^y is already covered exhaustively above; keep the sweep on the general path
            }

            rows.Add(item: (x, y, FixedQ4816.Pow(
                x: new FixedQ4816(Value: x),
                y: new FixedQ4816(Value: y)
            ).Value));
        }

        return FormatBinaryVectors(
            arrayName: "POW_VECTORS",
            functionName: "pow",
            paramA: "x",
            paramB: "y",
            rows: rows,
            testName: "pow_vectors"
        );
    }
    private static string EmitSinCosVectors(ref ulong state) {
        var angles = new List<long>(collection: NotableRawValues());
        var quarterTurnRaw = FixedQ4816.FromDouble(value: (Math.PI / 2.0)).Value;

        // Turn-domain landmarks: exact multiples of a quarter turn are hard to hit from the angle
        // domain (the reduction lives in raw*InvTwoPi space, not radians), so these are ordinary radian
        // landmarks instead — some large enough to fold through many whole turns before landing.
        foreach (var k in new long[] { -1_000_000L, -100L, -12L, -4L, -3L, -2L, -1L, 0L, 1L, 2L, 3L, 4L, 12L, 100L, 1_000_000L }) {
            var landmark = (k * quarterTurnRaw);

            angles.Add(item: landmark);
            angles.Add(item: (landmark + 1L));
            angles.Add(item: (landmark - 1L));
        }

        var bound = (200L * FixedQ4816.One.Value);

        while (angles.Count < VectorTargetCount) {
            angles.Add(item: (((angles.Count % 3) == 0)
                ? NextFullRangeRaw(state: ref state)
                : NextBoundedRaw(
                    bound: bound,
                    state: ref state
                )));
        }

        // Append regressions after the generated stream so unrelated functions keep their existing vector inputs.
        angles.AddRange(collection: [-94521L, 94521L, -9223372036854774708L, -9074293991763690791L, -5576408924856405436L]);
        var sinRows = angles.Select(selector: angle => (angle, FixedQ4816.Sin(angle: new FixedQ4816(Value: angle)).Value)).ToList();
        var cosRows = angles.Select(selector: angle => (angle, FixedQ4816.Cos(angle: new FixedQ4816(Value: angle)).Value)).ToList();

        var sb = new StringBuilder();

        sb.Append(value: FormatUnaryVectors(
            arrayName: "SIN_VECTORS",
            functionName: "sin",
            rows: sinRows,
            testName: "sin_vectors"
        ));
        sb.Append(value: FormatUnaryVectors(
            arrayName: "COS_VECTORS",
            functionName: "cos",
            rows: cosRows,
            testName: "cos_vectors"
        ));
        return sb.ToString();
    }
    private static string FormatBinaryVectors(
        string functionName,
        string arrayName,
        string testName,
        IReadOnlyList<(long A, long B, long Expected)> rows,
        string paramA,
        string paramB
    ) {
        var sb = new StringBuilder();

        sb.Append(value: "#[test]\n");
        sb.Append(value: "fn ").Append(value: testName).Append(value: "() {\n");
        sb.Append(value: "    for &(").Append(value: paramA).Append(value: ", ").Append(value: paramB).Append(value: ", expected) in ")
            .Append(value: arrayName).Append(value: ".iter() {\n");
        sb.Append(value: "        assert_eq!(").Append(value: functionName).Append(value: '(').Append(value: paramA).Append(value: ", ").Append(value: paramB)
            .Append(value: "), expected, \"").Append(value: functionName).Append(value: '(').Append(value: '{').Append(value: paramA).Append(value: "}, {")
            .Append(value: paramB).Append(value: "}) => {expected}\");\n");
        sb.Append(value: "    }\n");
        sb.Append(value: "}\n\n");
        sb.Append(value: "const ").Append(value: arrayName).Append(value: ": &[(i64, i64, i64)] = &[\n");

        foreach (var (a, b, expected) in rows) {
            sb.Append(value: "    (").Append(value: a.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ", ")
                .Append(value: b.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ", ")
                .Append(value: expected.ToString(provider: CultureInfo.InvariantCulture)).Append(value: "),\n");
        }

        sb.Append(value: "];\n\n");
        return sb.ToString();
    }
    private static string FormatNumericArray<T>(string typeName, string name, IReadOnlyList<T> values, int perLine, string comment) where T : IFormattable {
        var sb = new StringBuilder();

        sb.Append(value: "// ").Append(value: comment).Append(value: '\n');
        sb.Append(value: "const ").Append(value: name).Append(value: ": [").Append(value: typeName).Append(value: "; ").Append(value: values.Count).Append(value: "] = [\n");

        for (var index = 0; (index < values.Count); index += perLine) {
            var line = values.Skip(count: index).Take(count: perLine).Select(selector: static value => value.ToString(format: null, formatProvider: CultureInfo.InvariantCulture));

            sb.Append(value: "    ").Append(value: string.Join(
                separator: ", ",
                values: line
            )).Append(value: ",\n");
        }

        sb.Append(value: "];\n\n");
        return sb.ToString();
    }
    private static string FormatI64Array(string name, IReadOnlyList<long> values, int perLine, string comment) =>
        FormatNumericArray(
            comment: comment,
            name: name,
            perLine: perLine,
            typeName: "i64",
            values: values
        );
    private static string FormatU64Array(string name, IReadOnlyList<ulong> values, int perLine, string comment) =>
        FormatNumericArray(
            comment: comment,
            name: name,
            perLine: perLine,
            typeName: "u64",
            values: values
        );
    private static string FormatUnaryVectors(string functionName, string arrayName, string testName, IReadOnlyList<(long Input, long Expected)> rows) {
        var sb = new StringBuilder();

        sb.Append(value: "#[test]\n");
        sb.Append(value: "fn ").Append(value: testName).Append(value: "() {\n");
        sb.Append(value: "    for &(input, expected) in ").Append(value: arrayName).Append(value: ".iter() {\n");
        sb.Append(value: "        assert_eq!(").Append(value: functionName).Append(value: "(input), expected, \"")
            .Append(value: functionName).Append(value: "({input}) => {expected}\");\n");
        sb.Append(value: "    }\n");
        sb.Append(value: "}\n\n");
        sb.Append(value: "const ").Append(value: arrayName).Append(value: ": &[(i64, i64)] = &[\n");

        foreach (var (input, expected) in rows) {
            sb.Append(value: "    (").Append(value: input.ToString(provider: CultureInfo.InvariantCulture)).Append(value: ", ")
                .Append(value: expected.ToString(provider: CultureInfo.InvariantCulture)).Append(value: "),\n");
        }

        sb.Append(value: "];\n\n");
        return sb.ToString();
    }
    private static long NextBoundedRaw(ref ulong state, long bound) {
        state = AdvanceState(state: state);

        var range = unchecked((ulong)((2L * bound) + 1L));
        var offset = (state % range);

        return unchecked((((long)offset) - bound));
    }
    private static long NextFullRangeRaw(ref ulong state) {
        state = AdvanceState(state: state);
        return unchecked((long)state);
    }
    // Raw values every function's structured coverage draws from: zero, the unit values, both carrier
    // extremes (and their neighbors), and assorted powers of two / near-powers-of-two.
    private static long[] NotableRawValues() {
        var one = FixedQ4816.One.Value;

        return [
            0L, 1L, -1L, 2L, -2L,
            one, -one,
            long.MinValue, long.MaxValue, (long.MinValue + 1L), (long.MaxValue - 1L),
            (32L * one), (-32L * one), (33L * one), (-33L * one),
            (47L * one), (48L * one), (46L * one),
            (-16L * one), (-17L * one), (-18L * one), (-19L * one),
            (1L << 32), -(1L << 32),
            (1L << 47), -(1L << 47),
            (1L << 20), -(1L << 20),
            ((1L << 62) - 1L), (-(1L << 62) + 1L),
        ];
    }

    /// <summary>Emits the complete text of <c>fixed_generated.rs</c>: the ported functions, tables, and
    /// polynomial coefficients, read from the live <see cref="FixedQ4816"/> type.</summary>
    public static string EmitGenerated() {
        var sb = new StringBuilder();

        sb.Append(value: """
//! GENERATED — do not hand-edit. Regenerate with:
//!
//! ```text
//! dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib
//! ```
//!
//! A bit-exact Rust port of `FixedQ4816`'s six algorithm-pinned transcendentals
//! (`src/Puck.Maths/FixedPoint/FixedQ4816.cs`'s `Atan2`, `Sin`/`Cos` (via `SinCos`), `Exp2`, `Log2`, and
//! `Pow`) — the table-plus-polynomial recipe `fixed.rs`'s module doc calls "specified only by a
//! particular algorithm", not a closed-form spec. The 128-entry interval tables and the polynomial
//! coefficients below are read from the live `FixedQ4816` type by the tools verb named above, never
//! transcribed by hand — see `fixed_vectors.rs` for the known-answer proof that this port still agrees
//! with the host bit-for-bit. Running the verb twice against an unchanged host produces byte-identical
//! output; if the host's algorithm ever changes, regenerating both files is how the port catches up.
//!
//! Every function here is guest code now: there is no host round-trip and no WASM import. `fixed.rs`
//! re-exports these six under its own public names, so an addon author's call sites never change.

use crate::fixed::{FRACTION_BITS, ONE, ZERO};

// Mirrors FixedQ4816's private FractionBitMask — derived directly from the public FRACTION_BITS constant
// (16), so there is nothing here that could drift independently of it.
const FRACTION_MASK: u64 = (1u64 << FRACTION_BITS) - 1;


""");

        sb.Append(value: "// Atan2 constants (FixedQ4816.Atan2HalfPiQ61 / PiQ61).\n");
        sb.Append(value: "const ATAN2_HALF_PI_Q61: i64 = ").Append(value: FixedQ4816.Atan2HalfPiQ61).Append(value: ";\n");
        sb.Append(value: "const ATAN2_PI_Q61: i64 = ").Append(value: FixedQ4816.PiQ61).Append(value: ";\n\n");

        sb.Append(value: "// SinCos constants (FixedQ4816.SinCos*).\n");
        sb.Append(value: "const SIN_COS_INV_TWO_PI_Q96: u128 = ").Append(value: FixedQ4816.SinCosInvTwoPiQ96).Append(value: ";\n");
        sb.Append(value: "const SIN_COS_QUARTER_TURN_Q64: i64 = ").Append(value: FixedQ4816.SinCosQuarterTurnQ64).Append(value: ";\n");
        sb.Append(value: "const SIN_COS_TWO_PI_Q60: i64 = ").Append(value: FixedQ4816.SinCosTwoPiQ60).Append(value: ";\n");
        sb.Append(value: "// FixedQ4816.SinCosFractionBitCount (").Append(value: FixedQ4816.SinCosFractionBitCount)
            .Append(value: ") minus FRACTION_BITS (16) — the Q60-to-Q16 narrowing shift.\n");
        sb.Append(value: "const SIN_COS_NARROWING_SHIFT: i64 = ").Append(value: (FixedQ4816.SinCosFractionBitCount - 16)).Append(value: ";\n\n");

        sb.Append(value: FormatI64Array(
            comment: "Quarter-wave sine at i/256 turns, Q60; cosine reverses the index (FixedQ4816.SinCosTableQ60).",
            name: "SIN_COS_TABLE_Q60",
            perLine: 4,
            values: FixedQ4816.SinCosTableQ60.ToArray()
        ));
        sb.Append(value: FormatI64Array(
            comment: "Small-residual sine/cosine Taylor coefficients, Q60 (degrees five/four).",
            name: "SIN_COS_RESIDUAL_Q60",
            perLine: 4,
            values: [FixedQ4816.SinPolyC1Q60, FixedQ4816.SinPolyC2Q60, FixedQ4816.CosPolyC1Q60, FixedQ4816.CosPolyC2Q60]
        ));
        sb.Append(value: FormatI64Array(
            comment: "log2 residual coefficients C1..C7, Q61 (FixedQ4816.Log2PolyC*Q61); log2 reads C1..C4, pow's wide logarithm all seven.",
            name: "LOG2_POLY_Q61",
            perLine: 4,
            values: [FixedQ4816.Log2PolyC1Q61, FixedQ4816.Log2PolyC2Q61, FixedQ4816.Log2PolyC3Q61, FixedQ4816.Log2PolyC4Q61, FixedQ4816.Log2PolyC5Q61, FixedQ4816.Log2PolyC6Q61, FixedQ4816.Log2PolyC7Q61]
        ));
        sb.Append(value: FormatI64Array(
            comment: "exp2 residual coefficients C1..C4, Q62 (FixedQ4816.Exp2PolyC*Q62).",
            name: "EXP2_POLY_Q62",
            perLine: 4,
            values: [FixedQ4816.Exp2PolyC1Q62, FixedQ4816.Exp2PolyC2Q62, FixedQ4816.Exp2PolyC3Q62, FixedQ4816.Exp2PolyC4Q62]
        ));

        sb.Append(value: FormatU64Array(
            comment: "Log2 interval inverse table, Q62 (FixedQ4816.Log2InverseTableQ62).",
            name: "LOG2_INVERSE_TABLE_Q62",
            perLine: 4,
            values: FixedQ4816.Log2InverseTableQ62.ToArray()
        ));
        sb.Append(value: FormatU64Array(
            comment: "Log2 interval table, Q61 (FixedQ4816.Log2TableQ61).",
            name: "LOG2_TABLE_Q61",
            perLine: 4,
            values: FixedQ4816.Log2TableQ61.ToArray()
        ));
        sb.Append(value: FormatU64Array(
            comment: "Exp2 interval table, Q62 (FixedQ4816.Exp2TableQ62).",
            name: "EXP2_TABLE_Q62",
            perLine: 4,
            values: FixedQ4816.Exp2TableQ62.ToArray()
        ));
        sb.Append(value: FormatI64Array(
            comment: "Atan2 interval table, Q61 (FixedQ4816.AtanTableQ61).",
            name: "ATAN_TABLE_Q61",
            perLine: 4,
            values: FixedQ4816.AtanTableQ61.ToArray()
        ));
        sb.Append(value: FormatI64Array(
            comment: "Atan2 first-derivative interval table, Q61 (FixedQ4816.AtanDerivative1TableQ61).",
            name: "ATAN_DERIVATIVE1_TABLE_Q61",
            perLine: 4,
            values: FixedQ4816.AtanDerivative1TableQ61.ToArray()
        ));
        sb.Append(value: FormatI64Array(
            comment: "Atan2 second-derivative interval table, Q61 (FixedQ4816.AtanDerivative2TableQ61).",
            name: "ATAN_DERIVATIVE2_TABLE_Q61",
            perLine: 4,
            values: FixedQ4816.AtanDerivative2TableQ61.ToArray()
        ));
        sb.Append(value: FormatI64Array(
            comment: "Atan2 third-derivative interval table, Q61 (FixedQ4816.AtanDerivative3TableQ61).",
            name: "ATAN_DERIVATIVE3_TABLE_Q61",
            perLine: 4,
            values: FixedQ4816.AtanDerivative3TableQ61.ToArray()
        ));

        sb.Append(value: """
// Signed (x*y) >> 62 via one 128-bit widened multiply and an arithmetic shift — equivalent to the host's
// Math.BigMul-based reconstruction (BigMulShift62) because the high:low halves of a signed 64x64->128
// multiply are exactly the two's-complement bit pattern of `(x as i128) * (y as i128)`, and arithmetic-
// shifting that by 62 then truncating to the low 64 bits reassembles exactly what the host computes from
// high/low. `|x*y|` must stay below 2^125 for the truncation to be lossless (the host carries the same
// precondition).
#[inline]
fn big_mul_shift62(x: i64, y: i64) -> i64 {
    (((x as i128) * (y as i128)) >> 62) as i64
}

// Signed (x*y) >> 60 — see big_mul_shift62; `|x*y|` must stay below 2^123.
#[inline]
fn big_mul_shift60(x: i64, y: i64) -> i64 {
    (((x as i128) * (y as i128)) >> 60) as i64
}

/// Angle from the positive X axis to `(x, y)`, in fixed-point radians in `(-pi, pi]` — ported from
/// `FixedQ4816.Atan2`. Mirrors the host's portable `UInt128` division path (the x86 `DivRem` intrinsic
/// fast path and the portable fallback always agree, per the host's own doc comment).
///
/// **Argument order matches the host method (and C's `atan2`): `(y, x)`, not `(x, y)`.**
#[must_use]
pub fn atan2(y: i64, x: i64) -> i64 {
    if (x == 0) && (y == 0) {
        return ZERO;
    }

    let sign_y = y >> 63;
    let sign_x = x >> 63;
    let y_magnitude = ((y ^ sign_y).wrapping_sub(sign_y)) as u64;
    let x_magnitude = ((x ^ sign_x).wrapping_sub(sign_x)) as u64;
    let swapped = y_magnitude > x_magnitude;
    let numerator = if swapped { x_magnitude } else { y_magnitude };
    let denominator = if swapped { y_magnitude } else { x_magnitude };

    // (numerator << 62) / denominator, widened — the host's portable UInt128 fallback (the quotient always
    // fits u64: the dividend's high word is numerator >> 2, below denominator).
    let z = (((numerator as u128) << 62) / (denominator as u128)) as u64;

    let mut index = (z >> 55) as i64;

    if index > 127 {
        index = 127;
    }

    let index = index as usize;
    let h = z.wrapping_sub((index as u64) << 55) as i64;
    let mut acc = ATAN_DERIVATIVE3_TABLE_Q61[index];

    acc = ATAN_DERIVATIVE2_TABLE_Q61[index].wrapping_add(big_mul_shift62(h, acc));
    acc = ATAN_DERIVATIVE1_TABLE_Q61[index].wrapping_add(big_mul_shift62(h, acc));

    let mut angle = ATAN_TABLE_Q61[index].wrapping_add(big_mul_shift62(h, acc));

    if swapped {
        angle = ATAN2_HALF_PI_Q61.wrapping_sub(angle);
    }

    if sign_x != 0 {
        angle = ATAN2_PI_Q61.wrapping_sub(angle);
    }

    let raw = angle.wrapping_add(1i64 << 44) >> 45;

    if sign_y != 0 {
        raw.wrapping_neg()
    } else {
        raw
    }
}

// Q96 reciprocal reduction. Wrapping the product loses no bit used by the Q16 input's [48,111] phase slice.
// The product wrap is exact; the reciprocal approximates the irrational constant. Reduce magnitudes first.
fn sin_cos_turns(angle: i64) -> i64 {
    let product = (angle.unsigned_abs() as u128).wrapping_mul(SIN_COS_INV_TWO_PI_Q96);
    let turns = (product >> 48) as u64 as i64;
    if angle < 0 { turns.wrapping_neg() } else { turns }
}

fn narrow_sin_cos_q60(value: i64) -> i64 {
    let magnitude = value.unsigned_abs();
    let bias = (1u64 << (SIN_COS_NARROWING_SHIFT - 1)) - 1;
    let rounded = ((magnitude + bias + ((magnitude >> SIN_COS_NARROWING_SHIFT) & 1)) >> SIN_COS_NARROWING_SHIFT) as i64;
    if value < 0 { -rounded } else { rounded }
}

// First-quadrant nearest-node residual, |r| <= pi/256. Returns (quadrant, index, sin(r), cos(r)).
fn sin_cos_residual(fractional_turns: i64) -> (u64, usize, i64, i64) {
    let phase = fractional_turns as u64;
    let quadrant = phase >> 62;
    let quarter = SIN_COS_QUARTER_TURN_Q64 as u64;
    let offset = phase & (quarter - 1);
    let position = if quadrant & 1 != 0 { quarter - offset } else { offset };
    let index = ((position + (1u64 << 55)) >> 56) as usize;
    let residual = position as i64 - ((index as i64) << 56);
    let x = (((residual as i128) * (SIN_COS_TWO_PI_Q60 as i128)) >> 64) as i64;
    let u = big_mul_shift60(x, x);
    let sin = x + big_mul_shift60(big_mul_shift60(x, u), SIN_COS_RESIDUAL_Q60[0] + big_mul_shift60(u, SIN_COS_RESIDUAL_Q60[1]));
    let cos = (1i64 << 60) + big_mul_shift60(u, SIN_COS_RESIDUAL_Q60[2] + big_mul_shift60(u, SIN_COS_RESIDUAL_Q60[3]));
    (quadrant, index, sin, cos)
}

fn sin_cos_component(fractional_turns: i64, cosine: bool) -> i64 {
    let (quadrant, index, sin, cos) = sin_cos_residual(fractional_turns);
    let value = if cosine {
        big_mul_shift60(SIN_COS_TABLE_Q60[64 - index], cos) - big_mul_shift60(SIN_COS_TABLE_Q60[index], sin)
    } else {
        big_mul_shift60(SIN_COS_TABLE_Q60[index], cos) + big_mul_shift60(SIN_COS_TABLE_Q60[64 - index], sin)
    };
    let raw = narrow_sin_cos_q60(value);
    if (quadrant + if cosine { 1 } else { 0 }) & 2 != 0 { -raw } else { raw }
}

/// Sine of `angle` (fixed-point radians), reconstructing only the requested component.
#[must_use]
pub fn sin(angle: i64) -> i64 {
    sin_cos_component(sin_cos_turns(angle), false)
}

/// Cosine of `angle` (fixed-point radians), reconstructing only the requested component.
#[must_use]
pub fn cos(angle: i64) -> i64 {
    sin_cos_component(sin_cos_turns(angle), true)
}

// Fractional base-2 log of a Q62 mantissa in [1, 2), at Q61 — ported from FixedQ4816.Log2FractionQ61.
fn log2_fraction_q61(mantissa_q62: u64) -> i64 {
    let index = ((mantissa_q62 >> 55) & 0x7F) as usize;
    let product = (mantissa_q62 as u128) * (LOG2_INVERSE_TABLE_Q62[index] as u128);
    let combined = (product >> 62) as u64;
    let r = combined.wrapping_sub(1u64 << 62) as i64;
    let mut acc = LOG2_POLY_Q61[3];

    for i in (0..3).rev() {
        acc = LOG2_POLY_Q61[i].wrapping_add(big_mul_shift62(r, acc));
    }

    (LOG2_TABLE_Q61[index] as i64).wrapping_add(big_mul_shift62(r, acc))
}

/// `log2(value)` in fixed point — ported from `FixedQ4816.Log2`. Non-positive inputs return `i64::MIN`
/// (mirroring `FixedQ4816.MinValue`).
#[must_use]
pub fn log2(value: i64) -> i64 {
    if value <= 0 {
        return i64::MIN;
    }

    let raw = value as u64;
    let integer_part = (63u32 - raw.leading_zeros()) as i64; // BitOperations.Log2 for a nonzero u64
    let mantissa_q62 = raw << (62 - integer_part);
    let fraction = log2_fraction_q61(mantissa_q62);

    ((integer_part - (FRACTION_BITS as i64)) << 16)
        .wrapping_add(fraction.wrapping_add(1i64 << 44) >> 45)
}

/// `2^value` in fixed point — ported from `FixedQ4816.Exp2`.
#[must_use]
pub fn exp2(value: i64) -> i64 {
    if value >= (47i64 << FRACTION_BITS) {
        return i64::MAX;
    }

    let k = value >> FRACTION_BITS;
    let shift = 46i64.wrapping_sub(k);

    if shift >= 64 {
        return ZERO;
    }

    let f = value & (FRACTION_MASK as i64);

    if f == 0 {
        return exp2_whole(k);
    }

    exp2_narrow(exp2_mantissa_q62((f >> 9) as usize, (f & 0x1FF) << 46), shift)
}

// `2^exponent` for an exponent carried at Q56 — ported from FixedQ4816.Exp2Q56: the same table and
// polynomial as `exp2`, fed forty-nine residual bits instead of nine.
fn exp2_q56(exponent_q56: i64) -> i64 {
    if exponent_q56 >= (47i64 << 56) {
        return i64::MAX;
    }

    let k = exponent_q56 >> 56;
    let shift = 46i64.wrapping_sub(k);

    if shift >= 64 {
        return ZERO;
    }

    let f = exponent_q56 & ((1i64 << 56) - 1);

    if f == 0 {
        return exp2_whole(k);
    }

    exp2_narrow(exp2_mantissa_q62((f >> 49) as usize, (f & ((1i64 << 49) - 1)) << 6), shift)
}

// 2^k for a whole k in [-17, 46], exactly — ported from FixedQ4816.Exp2Whole: the true tie 2^-17 goes to the even
// zero.
fn exp2_whole(k: i64) -> i64 {
    if k >= -(FRACTION_BITS as i64) { 1i64 << (k + FRACTION_BITS as i64) } else { ZERO }
}

// Narrows a Q62 mantissa by shift (46 - k), below 64, half up — ported from FixedQ4816.Exp2Narrow; a non-whole
// exponent's 2^x is irrational, so a tie there is an artifact of the approximation.
fn exp2_narrow(mantissa: i64, shift: i64) -> i64 {
    if shift <= 0 { mantissa } else { (((mantissa as u64) + (1u64 << (shift - 1))) >> shift) as i64 }
}

// The shared core of exp2, exp2_q56 and pow's whole-exponent estimate — ported from FixedQ4816.Exp2MantissaQ62:
// 2^(i/128 + r) at Q62, unrounded.
fn exp2_mantissa_q62(index: usize, residual_q62: i64) -> i64 {
    let r = residual_q62;
    let mut acc = EXP2_POLY_Q62[3];

    for i in (0..3).rev() {
        acc = EXP2_POLY_Q62[i].wrapping_add(big_mul_shift62(r, acc));
    }

    big_mul_shift62(
        EXP2_TABLE_Q62[index] as i64,
        (1i64 << 62).wrapping_add(big_mul_shift62(r, acc)),
    )
}

// The base-2 logarithm of a positive raw for pow's exponential path — ported from FixedQ4816.Log2Wide: an
// integer part and a Q123 fraction with relative accuracy, the mantissa balanced about one, the last interval read
// directly as log2(1 + r) with r = m/2 - 1, and the Taylor residual through degree six kept as a full i128 product.
fn log2_wide(raw: u64) -> (i64, i128) {
    let bit_index = 63 - raw.leading_zeros() as i64;
    let mantissa_q62 = raw << (62 - bit_index);
    let mut integer_part = bit_index - (FRACTION_BITS as i64);
    let index = ((mantissa_q62 >> 55) & 0x7F) as usize;
    let r: i64;
    let mut table_q123: i128;

    if index == 127 {
        r = (mantissa_q62.wrapping_sub(1u64 << 63) as i64) >> 1;
        table_q123 = 0;
        integer_part += 1;
    } else {
        let product = (mantissa_q62 as u128) * (LOG2_INVERSE_TABLE_Q62[index] as u128);

        r = ((product >> 62) as u64).wrapping_sub(1u64 << 62) as i64;
        table_q123 = ((LOG2_TABLE_Q61[index] as i64) as i128) << 62;

        if index >= 64 {
            table_q123 -= 1i128 << 123;
            integer_part += 1;
        }
    }

    let mut acc = LOG2_POLY_Q61[6];

    for i in (0..6).rev() {
        acc = LOG2_POLY_Q61[i].wrapping_add(big_mul_shift62(r, acc));
    }

    (integer_part, table_q123 + (r as i128) * (acc as i128))
}

// The exact-power width — ported from FixedQ4816.PowWhole's ten-limb stack buffer, which holds every power the
// whole-exponent path forms before its verdict is decided.
const POW_LIMBS: usize = 10;

// The bit length of a little-endian limb magnitude — ported from FixedQ4816.PowBitLength.
fn limb_bit_length(m: &[u64; POW_LIMBS]) -> u32 {
    for i in (0..POW_LIMBS).rev() {
        if m[i] != 0 {
            return (i as u32) * 64 + 64 - m[i].leading_zeros();
        }
    }

    0
}

// m *= factor, in place — LimbBig.MultiplyByInt64 on a non-negative magnitude. The width bound makes the carry out
// of the top limb zero.
fn limb_multiply(m: &mut [u64; POW_LIMBS], factor: u64) {
    let mut carry: u128 = 0;

    for limb in m.iter_mut() {
        let product = (*limb as u128) * (factor as u128) + carry;

        *limb = product as u64;
        carry = product >> 64;
    }
}

fn limb_test_bit(m: &[u64; POW_LIMBS], position: u32) -> bool {
    let limb = (position >> 6) as usize;

    limb < POW_LIMBS && ((m[limb] >> (position & 63)) & 1) != 0
}

fn limb_any_bit_below(m: &[u64; POW_LIMBS], position: u32) -> bool {
    let limb = (position >> 6) as usize;
    let bit = position & 63;

    m[..limb.min(POW_LIMBS)].iter().any(|&l| l != 0) || (bit != 0 && limb < POW_LIMBS && (m[limb] & ((1u64 << bit) - 1)) != 0)
}

// m / 2^shift rounded to nearest, ties to even, low 64 bits — LimbBig.RoundAtShift for a positive magnitude and a
// shift of at least one.
fn limb_round_at_shift(m: &[u64; POW_LIMBS], shift: u32) -> u64 {
    let limb = (shift >> 6) as usize;
    let bit = shift & 63;
    let low = if limb < POW_LIMBS { m[limb] } else { 0 };
    let high = if limb + 1 < POW_LIMBS { m[limb + 1] } else { 0 };
    let truncated = if bit == 0 { low } else { (low >> bit) | (high << (64 - bit)) };
    let round_up = limb_test_bit(m, shift - 1) && (limb_any_bit_below(m, shift - 1) || (truncated & 1) != 0);

    truncated.wrapping_add(round_up as u64)
}

fn limb_compare(a: &[u64; POW_LIMBS], b: &[u64; POW_LIMBS]) -> core::cmp::Ordering {
    for i in (0..POW_LIMBS).rev() {
        if a[i] != b[i] {
            return a[i].cmp(&b[i]);
        }
    }

    core::cmp::Ordering::Equal
}

fn limb_subtract(a: &mut [u64; POW_LIMBS], b: &[u64; POW_LIMBS]) {
    let mut borrow = false;

    for i in 0..POW_LIMBS {
        let (difference, first) = a[i].overflowing_sub(b[i]);
        let (difference, second) = difference.overflowing_sub(borrow as u64);

        a[i] = difference;
        borrow = first || second;
    }
}

fn limb_shift_left_one(m: &mut [u64; POW_LIMBS]) {
    for i in (0..POW_LIMBS).rev() {
        m[i] = (m[i] << 1) | if i > 0 { m[i - 1] >> 63 } else { 0 };
    }
}

// A u128 below 2^127 divided by 2^shift, rounded to nearest with ties to even — ported from
// FixedQ4816.TryRoundShift. `None` when that reaches 2^63; a shift of 128 or more rounds to zero.
fn round_shift_u128(value: u128, shift: u32) -> Option<u64> {
    if shift >= 128 {
        return Some(0);
    }

    let mut quotient = value >> shift;
    let remainder = value & ((1u128 << shift) - 1);
    let half = 1u128 << (shift - 1);

    if remainder > half || (remainder == half && (quotient & 1) != 0) {
        quotient += 1;
    }

    if quotient > (i64::MAX as u128) { None } else { Some(quotient as u64) }
}

// 2^exponent / divisor rounded to nearest, ties to even — ported from
// FixedQ4816.TryRoundPowerOfTwoQuotient: one 128-by-64 estimate from the divisor's top 64 bits (never below the
// quotient, at most two above it), corrected and rounded on the exact remainder. `None` when the rounded quotient
// reaches 2^63.
fn limb_round_power_of_two_quotient(exponent: u32, divisor: &[u64; POW_LIMBS]) -> Option<u64> {
    let bit_length = limb_bit_length(divisor) as i64;

    if (exponent as i64) < bit_length - 1 {
        return Some(0);
    }

    if (exponent as i64) - (bit_length - 1) >= 64 {
        return None;
    }

    let cut = (bit_length - 64).max(0) as u32;
    let limb = (cut >> 6) as usize;
    let bit = cut & 63;
    let top = if bit == 0 { divisor[limb] } else { (divisor[limb] >> bit) | (divisor[limb + 1] << (64 - bit)) };
    let numerator = 1u128 << (exponent - cut);
    let mut estimate = (numerator / (top as u128)) as u64;
    let estimate_remainder = (numerator % (top as u128)) as u64;

    if cut == 0 {
        if estimate > (i64::MAX as u64) {
            return None;
        }

        let twice = (estimate_remainder as u128) << 1;

        if twice > (top as u128) || (twice == (top as u128) && (estimate & 1) != 0) {
            estimate += 1;
        }

        return if estimate > (i64::MAX as u64) { None } else { Some(estimate) };
    }

    if estimate > (i64::MAX as u64) + 2 {
        return None;
    }

    let clamped = estimate > (i64::MAX as u64);
    let mut candidate = if clamped { i64::MAX as u64 } else { estimate };
    let mut product = *divisor;
    let mut remainder = [0u64; POW_LIMBS];
    let mut negative;

    limb_multiply(&mut product, candidate);
    remainder[(exponent >> 6) as usize] = 1u64 << (exponent & 63);

    // remainder = |2^exponent - candidate * divisor|, with its sign in `negative`.
    if limb_compare(&remainder, &product) != core::cmp::Ordering::Less {
        limb_subtract(&mut remainder, &product);
        negative = false;
    } else {
        limb_subtract(&mut product, &remainder);
        remainder = product;
        negative = true;
    }

    while negative {
        if limb_compare(&remainder, divisor) == core::cmp::Ordering::Greater {
            limb_subtract(&mut remainder, divisor);
        } else {
            let mut lifted = *divisor;

            limb_subtract(&mut lifted, &remainder);
            remainder = lifted;
            negative = false;
        }

        candidate -= 1;
    }

    if clamped && limb_compare(&remainder, divisor) != core::cmp::Ordering::Less {
        return None;
    }

    limb_shift_left_one(&mut remainder);

    let comparison = limb_compare(&remainder, divisor);

    if comparison == core::cmp::Ordering::Greater || (comparison == core::cmp::Ordering::Equal && (candidate & 1) != 0) {
        candidate += 1;
    }

    if candidate > (i64::MAX as u64) { None } else { Some(candidate) }
}

// x^n for a whole exponent 2 <= |n| <= 32 as the ONE correct rounding of the exact power — ported from
// FixedQ4816.PowWhole: the power is built in one u128 while it stays below 2^127 and in limbs past that, then
// shifted by 16(n-1) or divided into 2^(16(m+1)) once; a power that reaches the decided bit length is already
// past the carrier (positive) or below half a raw (negative).
// y*log2(x) at Q56 — ported from FixedQ4816.PowExponentQ56: the integer part exactly, the wide logarithm's
// fraction through its top 63 significant bits and one floor to Q56.
fn pow_exponent_q56(raw: u64, y: i64) -> i128 {
    let (integer_part, fraction) = log2_wide(raw);
    let fraction_magnitude = fraction.unsigned_abs();
    let cut = ((128 - fraction_magnitude.leading_zeros()) as i32 - 63).max(0) as u32;
    let fraction_top = (fraction_magnitude >> cut) as i64;
    let signed_top = if fraction < 0 { -fraction_top } else { fraction_top };
    let fraction_product = (y as i128) * (signed_top as i128);

    (((y as i128) * (integer_part as i128)) << 40) + (fraction_product >> (83 - cut))
}

// The correctly-rounded magnitude of x^n from the exponential path when its error bound proves the rounding —
// ported from FixedQ4816.PowWholeEstimate: trusted only more than a relative 2^-40 from a rounding midpoint.
fn pow_whole_estimate(magnitude: u64, exponent: i64) -> Option<u64> {
    let exponent_q56 = pow_exponent_q56(magnitude, exponent << FRACTION_BITS);

    if exponent_q56 >= (23i128 << 56) {
        return None;
    }

    if exponent_q56 < -(17i128 << 56) - (1i128 << 16) {
        return Some(0);
    }

    let e = exponent_q56 as i64;
    let k = e >> 56;

    if k < -17 {
        return None;
    }

    let f = e & ((1i64 << 56) - 1);
    let mantissa = exp2_mantissa_q62((f >> 49) as usize, (f & ((1i64 << 49) - 1)) << 6) as u64;
    let shift = (46 - k) as u32;
    let discarded = mantissa & ((1u64 << shift) - 1);
    let half = 1u64 << (shift - 1);
    let error = (mantissa >> 40) + 64;
    let above = discarded > half;
    let distance = if above { discarded - half } else { half - discarded };

    if distance <= error {
        return None;
    }

    Some((mantissa >> shift) + u64::from(above))
}

fn pow_whole(magnitude: u64, exponent: i64, negative_result: bool) -> i64 {
    let power = exponent.unsigned_abs() as u32;
    let shift = if exponent > 0 { FRACTION_BITS * (power - 1) } else { FRACTION_BITS * (power + 1) };
    let saturated = if negative_result { i64::MIN } else { i64::MAX };
    let base_bit_length = 64 - magnitude.leading_zeros();
    let mut wide = magnitude as u128;
    let mut built = 1u32;

    // X^n has at least n*(bits(X) - 1) + 1 bits; past the one-word lane the estimate answers first.
    let lane_can_hold = power * (base_bit_length - 1) + 1 <= 127;

    while lane_can_hold && built < power && (128 - wide.leading_zeros()) + base_bit_length <= 127 {
        wide *= magnitude as u128;
        built += 1;
    }

    if built < power || (exponent < 0 && shift > 126) {
        if let Some(estimate) = pow_whole_estimate(magnitude, exponent) {
            let estimate = estimate as i64;

            return if negative_result { estimate.wrapping_neg() } else { estimate };
        }
    }

    let rounded = if built == power {
        if exponent > 0 {
            match round_shift_u128(wide, shift) {
                Some(rounded) => rounded,
                None => return saturated,
            }
        } else if shift <= 126 {
            let numerator = 1u128 << shift;
            let mut quotient = numerator / wide;
            let excess = (numerator - quotient * wide) << 1;

            if excess > wide || (excess == wide && (quotient & 1) != 0) {
                quotient += 1;
            }

            if quotient > (i64::MAX as u128) {
                return saturated;
            }

            quotient as u64
        } else {
            let mut divisor = [0u64; POW_LIMBS];

            divisor[0] = wide as u64;
            divisor[1] = (wide >> 64) as u64;

            match limb_round_power_of_two_quotient(shift, &divisor) {
                Some(rounded) => rounded,
                None => return saturated,
            }
        }
    } else {
        let decided_bit_length = if exponent > 0 { shift + 64 } else { shift + 2 };
        let mut current = [0u64; POW_LIMBS];
        let mut decided = false;

        current[0] = wide as u64;
        current[1] = (wide >> 64) as u64;

        while built < power {
            limb_multiply(&mut current, magnitude);
            built += 1;

            if limb_bit_length(&current) >= decided_bit_length {
                decided = true;
                break;
            }
        }

        if exponent > 0 {
            if decided {
                return saturated;
            }

            let rounded = limb_round_at_shift(&current, shift);

            if rounded > (i64::MAX as u64) {
                return saturated;
            }

            rounded
        } else {
            if decided {
                return ZERO;
            }

            match limb_round_power_of_two_quotient(shift, &current) {
                Some(rounded) => rounded,
                None => return saturated,
            }
        }
    };

    if negative_result { (rounded as i64).wrapping_neg() } else { rounded as i64 }
}

// The magnitude kernel — ported from FixedQ4816.PowMagnitude. `x` is strictly positive; `negative_result`
// carries the sign the caller's base/exponent parity already decided.
fn pow_magnitude(x: i64, y: i64, whole: bool, negative_result: bool) -> i64 {
    let exponent = y >> FRACTION_BITS;

    if whole {
        if exponent == 0 {
            return ONE;
        }

        if exponent == 1 {
            return if negative_result { x.wrapping_neg() } else { x };
        }

        if exponent == -1 {
            let inverse = crate::fixed::div(ONE, x);

            return if negative_result { inverse.wrapping_neg() } else { inverse };
        }
    }

    if whole && (-32..=32).contains(&exponent) {
        return pow_whole(x as u64, exponent, negative_result);
    }

    // y*log2(x) at Q56: the integer part exactly, the wide logarithm's fraction through its top 63 significant
    // bits and one floor to Q56; the saturation gates apply to that i128 exponent before it narrows — deliberately
    // NOT `fixed::mul`, which wraps to i64 before the gates ever see the result.
    let exponent_q56 = pow_exponent_q56(x as u64, y);

    if exponent_q56 >= (47i128 << 56) {
        return if negative_result { i64::MIN } else { i64::MAX };
    }

    if exponent_q56 <= -(18i128 << 56) {
        return ZERO;
    }

    let scaled = exp2_q56(exponent_q56 as i64);

    if negative_result { scaled.wrapping_neg() } else { scaled }
}

/// `x` raised to the power `y`, in fixed point — ported from `FixedQ4816.Pow`
/// (`IPowerFunctions<FixedQ4816>`).
#[must_use]
pub fn pow(x: i64, y: i64) -> i64 {
    if x == 0 {
        return if y == 0 {
            ONE
        } else if y > 0 {
            ZERO
        } else {
            i64::MAX
        };
    }

    let whole = (y & (FRACTION_MASK as i64)) == 0;

    if x > 0 {
        return pow_magnitude(x, y, whole, false);
    }

    if !whole {
        return ZERO;
    }

    let exponent = y >> FRACTION_BITS;
    let negative_result = (exponent & 1) != 0;

    if x != i64::MIN {
        return pow_magnitude(x.wrapping_neg(), y, true, negative_result);
    }

    if exponent == 0 {
        ONE
    } else if exponent < 0 {
        ZERO
    } else if exponent == 1 {
        i64::MIN
    } else if negative_result {
        i64::MIN
    } else {
        i64::MAX
    }
}
""");

        return sb.ToString();
    }
    /// <summary>Emits the complete text of <c>fixed_vectors.rs</c>: known-answer vectors for the six ported
    /// functions, computed by calling the real <see cref="FixedQ4816"/> at generation time.</summary>
    public static string EmitVectors() {
        var state = LcgSeed;
        var sb = new StringBuilder();

        sb.Append(value: """
//! GENERATED — known-answer vectors for `fixed_generated.rs`, computed by calling the REAL host
//! `FixedQ4816` at generation time. Do not hand-edit; regenerate with:
//!
//! ```text
//! dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib
//! ```
//!
//! If a change to `fixed_generated.rs` fails one of the tests below, the PORT is wrong — fix the port,
//! never a vector. If a change to the HOST algorithm changes what these vectors expect, regenerating this
//! file is how the port's contract catches up — see the crate README's "golden rule" for why an
//! algorithm-pinned function's exact bits, not a mathematically "equally correct" answer, are what is
//! being pinned.
//!
//! Structured inputs (zero, +-1 raw, +-One, MIN/MAX, powers of two, and constructions that land exactly on
//! a 128-entry table's index 0/127 boundaries and just either side of them) are followed by a deterministic
//! sweep from a fixed-constant LCG seeded by a literal — never `Random`, never the wall clock, so an
//! unchanged host produces byte-identical vectors on every run.

use crate::fixed_generated::{atan2, cos, exp2, log2, pow, sin};

""");

        sb.Append(value: EmitAtan2Vectors(state: ref state));
        sb.Append(value: EmitSinCosVectors(state: ref state));
        sb.Append(value: EmitExp2Vectors(state: ref state));
        sb.Append(value: EmitLog2Vectors(state: ref state));
        sb.Append(value: EmitPowVectors(state: ref state));

        return sb.ToString();
    }
}

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
/// <see cref="FixedQ4816"/> type by name (<c>FixedQ4816.AtanTableQ61</c>, <c>FixedQ4816.SinPolyC0Q60</c>,
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

    // --- Structured inputs + LCG sweep -----------------------------------------------------

    private static ulong AdvanceState(ulong state) => unchecked(((state * LcgMultiplier) + LcgIncrement));
    // Every named branch in FixedQ4816.Pow/PowMagnitude, explicitly: zero base at every exponent sign, the
    // exact identity exponents (0, 1, -1) at both signs of base, the squaring ladder's whole-exponent range
    // boundary (+-32/+-33), MinValue's own carve-out, and a fractional exponent on a negative base (must be
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
    // --- Rust source formatting -------------------------------------------------------------

    private static string FormatI64Array(string name, IReadOnlyList<long> values, int perLine, string comment) {
        var sb = new StringBuilder();

        sb.Append(value: "// ").Append(value: comment).Append(value: '\n');
        sb.Append(value: "const ").Append(value: name).Append(value: ": [i64; ").Append(value: values.Count).Append(value: "] = [\n");

        for (var index = 0; (index < values.Count); index += perLine) {
            var line = values.Skip(count: index).Take(count: perLine).Select(selector: static value => value.ToString(provider: CultureInfo.InvariantCulture));

            sb.Append(value: "    ").Append(value: string.Join(
                separator: ", ",
                values: line
            )).Append(value: ",\n");
        }

        sb.Append(value: "];\n\n");
        return sb.ToString();
    }
    private static string FormatU64Array(string name, IReadOnlyList<ulong> values, int perLine, string comment) {
        var sb = new StringBuilder();

        sb.Append(value: "// ").Append(value: comment).Append(value: '\n');
        sb.Append(value: "const ").Append(value: name).Append(value: ": [u64; ").Append(value: values.Count).Append(value: "] = [\n");

        for (var index = 0; (index < values.Count); index += perLine) {
            var line = values.Skip(count: index).Take(count: perLine).Select(selector: static value => value.ToString(provider: CultureInfo.InvariantCulture));

            sb.Append(value: "    ").Append(value: string.Join(
                separator: ", ",
                values: line
            )).Append(value: ",\n");
        }

        sb.Append(value: "];\n\n");
        return sb.ToString();
    }
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

// Mirrors FixedQ4816's private FractionBitMask/RawHalf — both derived directly from the public
// FRACTION_BITS constant (16), so there is nothing here that could drift independently of it.
const FRACTION_MASK: u64 = (1u64 << FRACTION_BITS) - 1;
const HALF_ULP: u64 = 1u64 << (FRACTION_BITS - 1);


""");

        sb.Append(value: "// Atan2 constants (FixedQ4816.Atan2HalfPiQ61 / PiQ61).\n");
        sb.Append(value: "const ATAN2_HALF_PI_Q61: i64 = ").Append(value: FixedQ4816.Atan2HalfPiQ61).Append(value: ";\n");
        sb.Append(value: "const ATAN2_PI_Q61: i64 = ").Append(value: FixedQ4816.PiQ61).Append(value: ";\n\n");

        sb.Append(value: "// SinCos constants (FixedQ4816.SinCos*).\n");
        sb.Append(value: "const SIN_COS_INV_TWO_PI_Q64: i64 = ").Append(value: FixedQ4816.SinCosInvTwoPiQ64).Append(value: ";\n");
        sb.Append(value: "const SIN_COS_QUARTER_TURN_Q64: i64 = ").Append(value: FixedQ4816.SinCosQuarterTurnQ64).Append(value: ";\n");
        sb.Append(value: "const SIN_COS_TWO_PI_Q60: i64 = ").Append(value: FixedQ4816.SinCosTwoPiQ60).Append(value: ";\n");
        sb.Append(value: "// FixedQ4816.SinCosFractionBitCount (").Append(value: FixedQ4816.SinCosFractionBitCount)
            .Append(value: ") minus FRACTION_BITS (16) — the Q60-to-Q16 narrowing shift.\n");
        sb.Append(value: "const SIN_COS_NARROWING_SHIFT: i64 = ").Append(value: (FixedQ4816.SinCosFractionBitCount - 16)).Append(value: ";\n\n");

        sb.Append(value: FormatI64Array(
            comment: "sin Taylor coefficients C0..C6, Q60 (FixedQ4816.SinPolyC*Q60).",
            name: "SIN_POLY_Q60",
            perLine: 4,
            values: [
                FixedQ4816.SinPolyC0Q60, FixedQ4816.SinPolyC1Q60, FixedQ4816.SinPolyC2Q60, FixedQ4816.SinPolyC3Q60,
                FixedQ4816.SinPolyC4Q60, FixedQ4816.SinPolyC5Q60, FixedQ4816.SinPolyC6Q60,
            ]
        ));
        sb.Append(value: FormatI64Array(
            comment: "cos Taylor coefficients C0..C7, Q60 (FixedQ4816.CosPolyC*Q60).",
            name: "COS_POLY_Q60",
            perLine: 4,
            values: [
                FixedQ4816.CosPolyC0Q60, FixedQ4816.CosPolyC1Q60, FixedQ4816.CosPolyC2Q60, FixedQ4816.CosPolyC3Q60,
                FixedQ4816.CosPolyC4Q60, FixedQ4816.CosPolyC5Q60, FixedQ4816.CosPolyC6Q60, FixedQ4816.CosPolyC7Q60,
            ]
        ));
        sb.Append(value: FormatI64Array(
            comment: "log2 residual coefficients C1..C4, Q61 (FixedQ4816.Log2PolyC*Q61).",
            name: "LOG2_POLY_Q61",
            perLine: 4,
            values: [FixedQ4816.Log2PolyC1Q61, FixedQ4816.Log2PolyC2Q61, FixedQ4816.Log2PolyC3Q61, FixedQ4816.Log2PolyC4Q61]
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

    let raw = (angle.wrapping_add(1i64 << 44)) >> 45;

    if sign_y != 0 {
        raw.wrapping_neg()
    } else {
        raw
    }
}

// Turn-domain reduction shared by sin/cos: `angle * round(2^64 / 2*pi)` as an exact 128-bit signed
// product, arithmetic-shifted right by FRACTION_BITS and truncated to the low 64 bits — the two's-
// complement WRAP that is the exact mod-one-turn reduction (FixedQ4816.SinCos's own doc comment).
fn sin_cos_turns(angle: i64) -> i64 {
    let product = (angle as i128) * (SIN_COS_INV_TWO_PI_Q64 as i128);

    (product >> FRACTION_BITS) as i64
}

// Polynomial core on fractional turns (2^64 raw = one turn) — ported from FixedQ4816.SinCosCore. Returns
// the un-narrowed Q60 (cos, sin) of the folded residual, plus the fold flag (the true cosine is negated
// when folded).
fn sin_cos_core(fractional_turns: i64) -> (i64, i64, bool) {
    let folded = (fractional_turns > SIN_COS_QUARTER_TURN_Q64) || (fractional_turns < -SIN_COS_QUARTER_TURN_Q64);
    let fractional_turns = if folded {
        // sin(pi - theta) = sin theta, cos(pi - theta) = -cos theta: half a turn minus the fraction wraps
        // into [-1/4, 1/4].
        0x8000000000000000u64.wrapping_sub(fractional_turns as u64) as i64
    } else {
        fractional_turns
    };

    // Radians at Q60 (the fold bounds |theta| <= pi/2). The host keeps only the HIGH 64 bits of the signed
    // product here (`Math.BigMul(..., out _)`), which is exactly `(product >> 64) as i64`.
    let x = (((fractional_turns as i128) * (SIN_COS_TWO_PI_Q60 as i128)) >> 64) as i64;
    let u = big_mul_shift60(x, x);

    let mut sin_acc = SIN_POLY_Q60[6];

    for i in (0..6).rev() {
        sin_acc = SIN_POLY_Q60[i].wrapping_add(big_mul_shift60(u, sin_acc));
    }

    let mut cos_acc = COS_POLY_Q60[7];

    for i in (0..7).rev() {
        cos_acc = COS_POLY_Q60[i].wrapping_add(big_mul_shift60(u, cos_acc));
    }

    (cos_acc, big_mul_shift60(x, sin_acc), folded)
}

fn sin_cos(angle: i64) -> (i64, i64) {
    let (cos_q60, sin_q60, folded) = sin_cos_core(sin_cos_turns(angle));
    let sin_raw = ((sin_q60.wrapping_add(1i64 << (SIN_COS_NARROWING_SHIFT - 1))) >> SIN_COS_NARROWING_SHIFT)
        .clamp(-ONE, ONE);
    let cos_raw = ((cos_q60.wrapping_add(1i64 << (SIN_COS_NARROWING_SHIFT - 1))) >> SIN_COS_NARROWING_SHIFT)
        .clamp(-ONE, ONE);

    (sin_raw, if folded { cos_raw.wrapping_neg() } else { cos_raw })
}

/// Sine of `angle` (fixed-point radians) — ported from `FixedQ4816.Sin`/`SinCos`.
#[must_use]
pub fn sin(angle: i64) -> i64 {
    sin_cos(angle).0
}

/// Cosine of `angle` (fixed-point radians) — ported from `FixedQ4816.Cos`/`SinCos`.
#[must_use]
pub fn cos(angle: i64) -> i64 {
    sin_cos(angle).1
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

    ((integer_part - (FRACTION_BITS as i64)) << 16).wrapping_add((fraction.wrapping_add(1i64 << 44)) >> 45)
}

/// `2^value` in fixed point — ported from `FixedQ4816.Exp2`.
#[must_use]
pub fn exp2(value: i64) -> i64 {
    if value >= (47i64 << FRACTION_BITS) {
        return i64::MAX;
    }

    let k = value >> FRACTION_BITS;
    let f = value & (FRACTION_MASK as i64);
    let index = (f >> 9) as usize;
    let r = (f & 0x1FF) << 46;
    let mut acc = EXP2_POLY_Q62[3];

    for i in (0..3).rev() {
        acc = EXP2_POLY_Q62[i].wrapping_add(big_mul_shift62(r, acc));
    }

    let mantissa = big_mul_shift62(
        EXP2_TABLE_Q62[index] as i64,
        (1i64 << 62).wrapping_add(big_mul_shift62(r, acc)),
    );
    let shift = 46i64.wrapping_sub(k);

    if shift >= 64 {
        return ZERO;
    }

    if shift <= 0 {
        mantissa
    } else {
        ((mantissa as u64).wrapping_add(1u64 << (shift - 1)) >> shift) as i64
    }
}

// One squaring-ladder step — ported from FixedQ4816.TryMultiplyMagnitude. `None` when the rounded
// magnitude leaves the i64 carrier (the caller saturates).
fn try_multiply_magnitude(x: u64, y: u64) -> Option<u64> {
    let magnitude = (x as u128) * (y as u128);
    let mut truncated = magnitude >> FRACTION_BITS;
    let remainder = (magnitude as u64) & FRACTION_MASK;

    if (remainder > HALF_ULP) || ((remainder == HALF_ULP) && ((truncated & 1) != 0)) {
        truncated += 1;
    }

    if truncated > (i64::MAX as u128) {
        None
    } else {
        Some(truncated as u64)
    }
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

    let log = log2(x);

    if whole && (-32..=32).contains(&exponent) {
        // The log-derived magnitude decides only the underflow shortcut; overflow is decided exactly by the
        // ladder's own rounded magnitude leaving the carrier, below.
        if log.wrapping_mul(exponent) < (-18i64 << FRACTION_BITS) {
            return ZERO;
        }

        let mut result: u64 = ONE as u64;
        let mut base_magnitude: u64 = if exponent < 0 {
            crate::fixed::div(ONE, x) as u64
        } else {
            x as u64
        };
        let mut remaining = if exponent < 0 { -exponent } else { exponent };

        while remaining > 0 {
            if (remaining & 1) != 0 {
                match try_multiply_magnitude(result, base_magnitude) {
                    Some(next) => result = next,
                    None => return if negative_result { i64::MIN } else { i64::MAX },
                }
            }

            remaining >>= 1;

            if remaining > 0 {
                match try_multiply_magnitude(base_magnitude, base_magnitude) {
                    Some(next) => base_magnitude = next,
                    None => return if negative_result { i64::MIN } else { i64::MAX },
                }
            }
        }

        return if negative_result { (result as i64).wrapping_neg() } else { result as i64 };
    }

    // Full-width y*log2(x), rounded to nearest with ties to even — deliberately NOT `fixed::mul`: `mul`
    // wraps to i64 before the saturation gates below ever see the result, so an exponent outside the
    // Q48.16 range could turn into an arbitrary wrapped one instead of triggering saturation.
    let exponent_product = (y as i128) * (log as i128);
    let exponent_negative = exponent_product < 0;
    let exponent_magnitude = (if exponent_negative { -exponent_product } else { exponent_product }) as u128;
    let mut rounded_exponent_magnitude = exponent_magnitude >> FRACTION_BITS;
    let exponent_remainder = (exponent_magnitude as u64) & FRACTION_MASK;

    if (exponent_remainder > HALF_ULP)
        || ((exponent_remainder == HALF_ULP) && ((rounded_exponent_magnitude & 1) != 0))
    {
        rounded_exponent_magnitude += 1;
    }

    let exponent_raw: i128 = if exponent_negative {
        -(rounded_exponent_magnitude as i128)
    } else {
        rounded_exponent_magnitude as i128
    };

    if exponent_raw >= (47i128 << FRACTION_BITS) {
        return if negative_result { i64::MIN } else { i64::MAX };
    }

    if exponent_raw <= (-18i128 << FRACTION_BITS) {
        return ZERO;
    }

    let scaled = exp2(exponent_raw as i64);

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

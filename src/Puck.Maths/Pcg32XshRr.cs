using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A PCG32 XSH-RR pseudo-random generator: 64-bit linear-congruential state advanced with a per-stream odd
/// increment, permuted to 32 output bits by an xorshift and a data-driven rotation. Pure integer arithmetic;
/// identical seeds produce identical draws on every machine. Generator state is simulation state: it is fully
/// readable (<see cref="State"/>, <see cref="Increment"/>, <see cref="Multiplier"/>) and exactly restorable via
/// <see cref="FromRawBits"/>, so it rides snapshots and replays. For non-reproducible cryptographic draws use
/// <see cref="SecureRandom"/>.
/// </summary>
/// <remarks>
/// The output permutes the pre-advance state and <see cref="Create(ulong, ulong)"/> uses the reference seeding
/// recipe and stream mapping, so draws match the published PCG32 reference implementation bit for bit. Each stream
/// id selects a distinct odd increment, not a statistically independent sequence: ids exactly <c>2^62</c> apart
/// share half their draws (the LCG collapses increments <c>2^63</c> apart, which this mapping reaches at half that
/// id distance). Derive one stream per system from a master seed, using small consecutive stream ids; sharing one
/// generator across systems couples them through draw order. A default-constructed instance is degenerate; create
/// instances with <see cref="Create(ulong, ulong)"/> or <see cref="FromRawBits"/>.
/// </remarks>
public struct Pcg32XshRr {
    /// <summary>The default state multiplier — the PCG reference implementation's 64-bit LCG constant.</summary>
    public const ulong DefaultMultiplier = 6364136223846793005UL;
    /// <summary>The largest valid stream id (<c>2⁶³ − 1</c>); each id selects a distinct odd increment (see remarks for the correlation caveat).</summary>
    public const ulong MaxStream = ((1UL << 63) - 1UL);

    private const string MultiplierError = "multiplier must be congruent to 1 (mod 4) for the state to have full period";
    private const string StreamError = "stream id must not exceed 2^63 - 1";
    private const ulong TwoLn2Q30 = 1488522236UL; // round(2·ln 2 · 2^30)

    private readonly ulong m_increment;
    private readonly ulong m_multiplier;
    private ulong m_state;

    private Pcg32XshRr(ulong increment, ulong multiplier, ulong state) {
        m_increment = increment;
        m_multiplier = multiplier;
        m_state = state;
    }

    /// <summary>Creates a generator from a seed and a stream id using the default multiplier.</summary>
    /// <param name="state">The seed; every 64-bit value is valid (there are no weak seeds).</param>
    /// <param name="stream">The stream id in <c>[0, <see cref="MaxStream"/>]</c>; distinct ids select distinct odd increments — prefer small consecutive ids (see remarks for the <c>2^62</c> correlation caveat).</param>
    /// <returns>A ready-to-draw generator.</returns>
    public static Pcg32XshRr Create(ulong state, ulong stream) =>
        Create(
        multiplier: DefaultMultiplier,
        state: state,
        stream: stream
    );
    /// <summary>Creates a generator from a seed, a stream id, and an explicit state multiplier.</summary>
    /// <param name="multiplier">The LCG state multiplier; must be congruent to 1 (mod 4) so the state has full period. Prefer <see cref="DefaultMultiplier"/>.</param>
    /// <param name="state">The seed; every 64-bit value is valid (there are no weak seeds).</param>
    /// <param name="stream">The stream id in <c>[0, <see cref="MaxStream"/>]</c>; distinct ids select distinct odd increments — prefer small consecutive ids (see remarks for the <c>2^62</c> correlation caveat).</param>
    /// <returns>A ready-to-draw generator.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stream"/> exceeds <see cref="MaxStream"/>, or <paramref name="multiplier"/> is not congruent to 1 (mod 4).</exception>
    /// <remarks>Seeding follows the reference recipe (advance, add the seed, advance again) so generators created
    /// with the same seed on different streams start from different states rather than sharing one draw order; see
    /// the type remarks for the <c>2^62</c> stream-correlation caveat.</remarks>
    public static Pcg32XshRr Create(ulong multiplier, ulong state, ulong stream) {
        if (stream > MaxStream) {
            throw new ArgumentOutOfRangeException(
                actualValue: stream,
                message: StreamError,
                paramName: nameof(stream)
            );
        }

        // Reference mapping: ids shift onto the odd increments. Increments 2^63 apart collapse under the LCG
        // (2^63 · (multiplier + 1) ≡ 0 mod 2^64) and such stream pairs agree on half their draws; under this
        // mapping that requires ids 2^62 apart. Keep derived stream ids small and consecutive.
        var increment = (stream << 1) | 1UL;
        var generator = FromRawBits(
            increment: increment,
            multiplier: multiplier,
            state: 0UL
        );

        _ = generator.NextUInt32();
        generator.m_state += state;
        _ = generator.NextUInt32();

        return generator;
    }
    /// <summary>Restores a generator from its exact raw state, as captured from <see cref="State"/>, <see cref="Increment"/>, and <see cref="Multiplier"/>.</summary>
    /// <param name="increment">The raw odd increment, as read from <see cref="Increment"/>.</param>
    /// <param name="multiplier">The raw multiplier, as read from <see cref="Multiplier"/>.</param>
    /// <param name="state">The raw state, as read from <see cref="State"/>.</param>
    /// <returns>A generator that continues the captured sequence bit for bit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="increment"/> is even, or <paramref name="multiplier"/> is not congruent to 1 (mod 4).</exception>
    public static Pcg32XshRr FromRawBits(ulong increment, ulong multiplier, ulong state) {
        if ((increment & 1UL) == 0UL) {
            throw new ArgumentOutOfRangeException(
                actualValue: increment,
                message: "increment must be odd",
                paramName: nameof(increment)
            );
        }

        if ((multiplier & 3UL) != 1UL) {
            throw new ArgumentOutOfRangeException(
                actualValue: multiplier,
                message: MultiplierError,
                paramName: nameof(multiplier)
            );
        }

        return new(
            increment: increment,
            multiplier: multiplier,
            state: state
        );
    }

    /// <summary>Gets the raw stream increment — persist alongside <see cref="State"/> and <see cref="Multiplier"/> to snapshot the generator.</summary>
    public readonly ulong Increment => m_increment;
    /// <summary>Gets the raw state multiplier — persist alongside <see cref="State"/> and <see cref="Increment"/> to snapshot the generator.</summary>
    public readonly ulong Multiplier => m_multiplier;
    /// <summary>Gets the raw state — persist alongside <see cref="Increment"/> and <see cref="Multiplier"/> to snapshot the generator.</summary>
    public readonly ulong State => m_state;

    /// <summary>Skips the generator forward by <paramref name="count"/> draws in logarithmic time.</summary>
    /// <param name="count">The number of single-draw advances to apply; <c>2⁶⁴ − n</c> steps backward by <c>n</c>.</param>
    /// <remarks>Only whole-state advances are counted: a bounded draw that internally rejected samples consumed more
    /// than one advance, so seek arithmetic must count advances, not calls.</remarks>
    public void Advance(ulong count) {
        // The affine skip: compose (state -> state·m + c) with itself by binary exponentiation.
        var accumulatedMultiplier = 1UL;
        var accumulatedIncrement = 0UL;
        var currentMultiplier = m_multiplier;
        var currentIncrement = m_increment;

        while (count > 0UL) {
            if ((count & 1UL) != 0UL) {
                accumulatedMultiplier = unchecked((accumulatedMultiplier * currentMultiplier));
                accumulatedIncrement = unchecked(((accumulatedIncrement * currentMultiplier) + currentIncrement));
            }

            currentIncrement = unchecked(((currentMultiplier + 1UL) * currentIncrement));
            currentMultiplier = unchecked((currentMultiplier * currentMultiplier));
            count >>= 1;
        }

        m_state = unchecked(((accumulatedMultiplier * m_state) + accumulatedIncrement));
    }
    /// <summary>Draws the next 32 uniformly random bits.</summary>
    /// <returns>A uniformly distributed 32-bit value.</returns>
    public uint NextUInt32() {
        // Advance the state; permute the pre-advance state (reference convention): xorshift-high, then rotate by
        // the top five bits.
        var oldState = m_state;

        m_state = unchecked(((oldState * m_multiplier) + m_increment));

        return uint.RotateRight(
            rotateAmount: ((int)(oldState >> 59)),
            value: unchecked((uint)(((oldState >> 18) ^ oldState) >> 27))
        );
    }
    /// <summary>Draws a uniformly random value from an inclusive range.</summary>
    /// <param name="minimum">One end of the inclusive range.</param>
    /// <param name="maximum">The other end of the inclusive range; the bounds may be given in either order.</param>
    /// <returns>A uniformly distributed value in <c>[min(minimum, maximum), max(minimum, maximum)]</c>, unbiased.</returns>
    /// <remarks>Rejection sampling may consume more than one state advance per call (deterministically — the same
    /// state always draws the same value).</remarks>
    public uint NextUInt32(uint minimum, uint maximum) {
        if (maximum < minimum) {
            (minimum, maximum) = (maximum, minimum);
        }

        var range = (maximum - minimum);

        return ((range != uint.MaxValue)
            ? unchecked((Sample(exclusiveHigh: (range + 1U)) + minimum))
            : NextUInt32());
    }
    /// <summary>Draws one standard-normal value (mean zero, unit deviation).</summary>
    /// <returns>A normally distributed <see cref="FixedQ4816"/>; magnitudes cap at ≈6.66σ (probability ≈ 10⁻¹¹).</returns>
    /// <remarks>Consumes exactly two advances and discards the underlying pair's second value; use <see cref="NextGaussianPair"/> when both are wanted.</remarks>
    public FixedQ4816 NextGaussian() =>
        NextGaussianPair().First;
    /// <summary>Draws two independent standard-normal values (mean zero, unit deviation).</summary>
    /// <returns>The pair <c>(First, Second)</c> of normally distributed <see cref="FixedQ4816"/> values; magnitudes cap at ≈6.66σ (probability ≈ 10⁻¹¹).</returns>
    /// <remarks>Box–Muller over the fixed-point primitives (table-driven log2, integer square root, polynomial
    /// sine/cosine); results are bit-identical across machines. Consumes exactly two advances per pair, so
    /// <see cref="Advance"/>-based seek arithmetic stays exact.</remarks>
    public (FixedQ4816 First, FixedQ4816 Second) NextGaussianPair() {
        var radiusDraw = NextUInt32();
        var angleDraw = NextUInt32();

        // u1 = (draw + 1)/2^32 ∈ (0, 1]; −log2(u1) = 32 − ilog2(draw + 1) − mantissa fraction, at Q34.
        var u1 = (((ulong)radiusDraw) + 1UL);
        var integerPart = BitOperations.Log2(value: u1);
        var fractionQ34 = ((ulong)((FixedQ4816.Log2FractionQ61(mantissaQ62: (u1 << (62 - integerPart))) + (1L << 26)) >> 27));
        var negLog2Q34 = ((((ulong)(32 - integerPart)) << 34) - fractionQ34);

        // s = −2·ln(u1) = (2·ln 2)·(−log2 u1) at Q34 (s ≤ 44.4); r = √s at Q27 (√(s·2^54) = √s·2^27).
        var high = Math.BigMul(
            a: negLog2Q34,
            b: TwoLn2Q30,
            low: out var low
        );
        var sQ34 = (high << 34) | (low >> 30);
        var rQ27 = ((long)((sQ34 << 20).SquareRoot()));

        // θ = angleDraw in turns at full 2^-32 resolution; the CORDIC core returns Q60 sine/cosine.
        var fractionalTurns = unchecked((long)(((ulong)angleDraw) << 32));

        var (cosQ60, sinQ60, folded) = FixedQ4816.SinCosCore(fractionalTurns: fractionalTurns);

        if (folded) {
            cosQ60 = -cosQ60;
        }

        // z = r·(cos, sin): Q27 · Q60 = Q87, rounded to Q16.
        var first = ((long)(((((Int128)rQ27) * cosQ60) + (Int128.One << 70)) >> 71));
        var second = ((long)(((((Int128)rQ27) * sinQ60) + (Int128.One << 70)) >> 71));

        return (new FixedQ4816(Value: first), new FixedQ4816(Value: second));
    }
    /// <summary>Draws a uniformly random fraction in <c>[0, 1)</c> at UQ0.16 resolution.</summary>
    /// <returns>A uniformly distributed <see cref="UFixedQ0016"/> (the draw's top sixteen bits).</returns>
    public UFixedQ0016 NextUFixedQ0016() =>
        new(Value: ((ushort)(NextUInt32() >> 16)));
    /// <summary>Draws a uniformly random fraction in <c>[0, 1)</c> at UQ0.32 resolution.</summary>
    /// <returns>A uniformly distributed <see cref="UFixedQ0032"/>.</returns>
    public UFixedQ0032 NextUFixedQ0032() =>
        new(Value: NextUInt32());
    /// <summary>Shuffles <paramref name="values"/> in place into a uniformly random permutation.</summary>
    /// <typeparam name="TElement">The element type.</typeparam>
    /// <param name="values">The span to permute in place; each of its orderings becomes equally likely.</param>
    /// <remarks>The Fisher–Yates (Durstenfeld) shuffle: one inclusive bounded draw per element from the high end
    /// down, so a span of length <c>n</c> consumes <c>n − 1</c> calls to <see cref="NextUInt32(uint, uint)"/> (each
    /// may reject additional advances, deterministically). Identical state and span produce the identical
    /// permutation on every machine.</remarks>
    public void Shuffle<TElement>(Span<TElement> values) {
        for (var i = (values.Length - 1); (i > 0); --i) {
            var j = ((int)NextUInt32(
                minimum: 0U,
                maximum: ((uint)i)
            ));

            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    // A nearly-divisionless bounded draw: take the high 32 bits of draw·bound, rejecting the small biased
    // window (threshold = 2^32 mod bound) so every value in [0, bound) is exactly equally likely.
    private uint Sample(uint exclusiveHigh) {
        var product = unchecked((((ulong)NextUInt32()) * exclusiveHigh));
        var lowBits = unchecked((uint)product);

        if (lowBits < exclusiveHigh) {
            var threshold = unchecked((((uint)(-((int)exclusiveHigh))) % exclusiveHigh));

            while (lowBits < threshold) {
                product = unchecked((((ulong)NextUInt32()) * exclusiveHigh));
                lowBits = unchecked((uint)product);
            }
        }

        return ((uint)(product >> 32));
    }
}

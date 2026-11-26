using System.Numerics;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Maths;

namespace Puck.State;

/// <summary>
/// A <see cref="StateRow"/>'s continuous accumulation trait: the row's stored cell is a base value, and the
/// read value advances with elapsed engine ticks at an exact per-second rational rate from the engine tick it was
/// last explicitly set (<see cref="StateCellClock.EpochEngineTick"/>). Used for regen, fractional accumulation, a
/// day/night clock — anything that should move on its own between observations.
/// </summary>
/// <remarks>
/// <para>Nothing per-tick materializes or journals: the computed value (<see cref="ComputeCurrentValue"/>) is a pure
/// function of the base, the carrying cell's own <see cref="StateCellClock.EpochEngineTick"/>, the rate, and the
/// engine tick asked about. An explicit write — an <c>UpsertStateCell</c>, or an <c>UpsertStateRow</c> carrying a
/// stored value that differs from the one the cell already held — rebases: the written value becomes the new base and
/// the cell's clock epoch becomes the engine tick the write applied at. An <c>UpsertStateRow</c> restating the stored
/// base is not a write of the value: it moves the epoch, and the accumulation the cell had reached becomes the new
/// base, so re-declaring a row never restarts what a reader was watching.</para>
/// <para><see cref="ComputeCurrentValue"/> is applied only by <see cref="StateReader"/>'s central known-cell
/// computation, so both its name and compiled-handle entrances, every aggregate, read-back, rule gate, HUD binding,
/// and arithmetic write resolve an advancing row through the same code. An
/// <c>add</c> against an advancing row adds to what a reader sees, never to the stored base.</para>
/// <para>A rule's own <c>compareState</c> reads an advancing row's live computed value like any other row. A rule's
/// <c>setState</c>/<c>addState</c> effect against an advancing row's slot cell is an explicit write, so it rebases —
/// a rule that writes the same row every tick overrides this trait's accumulation with its own.</para>
/// <para><see cref="PerSecondNumerator"/>/<see cref="PerSecondDenominator"/> is an exact fraction of the row's own
/// displayed unit per second — the unit its <c>value</c>, <see cref="StateRowShape.Min"/>, and <see cref="StateRowShape.Max"/>
/// are authored in, not raw storage — evaluated against the engine's fixed 50,400-tick-per-second clock
/// (<see cref="FixedTickConversion.TicksPerSecond"/>, the same base <c>valueSeconds</c> duration authoring already
/// uses), never against the world's own <c>simulation.rateHz</c>. At a constant simulation rate this reproduces the
/// same value at every simulation-tick boundary a per-tick rational rate would have, but unlike a per-tick rate it
/// means the same thing regardless of the world's simulation rate, and a live rate change moves no epoch and skews
/// no accumulation, because engine ticks accrue at a fixed real-time pace no simulation rate can move. A world
/// authored at <c>simulation.rateHz: 0</c> never steps, so its engine tick never accrues either — this trait is
/// legal there and simply reads whatever base the last explicit write left, unmoving. For
/// <see cref="CellKind.Fixed"/>, the numerator is additionally scaled by <c>2^FixedQ4816.FractionBitCount</c> before
/// allocating via <see cref="Puck.Maths.DiscreteMeasure"/>'s exact rational allocation, so a rate accumulates without
/// rounding drift.</para>
/// <para>The rate may be negative (decay/drain); a negative rate is the exact mirror of its positive twin, not a
/// floor of the signed affine function — <see cref="Puck.Maths.DiscreteMeasure"/> accepts only a non-negative rate,
/// so this type floors the magnitude and negates it. Decay and regen at equal magnitude stay symmetric.</para>
/// <para>A declared <see cref="StateRowShape.Min"/>/<see cref="StateRowShape.Max"/> clamps the computed value on every read;
/// it never rewrites the stored base or epoch. A value that must wrap is a <see cref="StateCycle"/>, not an
/// advance.</para>
/// <para>A nested advance (<see cref="By"/>) runs on another clock instead of the engine's seconds: its variable is the
/// exact value of the named row (its parent, itself an advancing row), and its rate is
/// <see cref="PerSecondNumerator"/>/<see cref="PerSecondDenominator"/> per unit of the parent, shaped by
/// <see cref="Rate"/>, a piecewise polynomial in the parent's phase (the fractional part of its value, one unit a turn).
/// Between the parent values <c>a</c> and <c>b</c> the row accumulates the rate times the shape's exact integral from
/// <c>a</c> to <c>b</c> (<see cref="Accumulation"/>), so the value is read in closed form at any engine tick, and a
/// parent that is itself nested composes: a polynomial of a polynomial is a polynomial. The affine form is the case with
/// no parent and no shape, whose variable is the engine's seconds and whose integral is the identity; both forms
/// truncate the same exact accumulation toward zero into the carrier, so the two agree wherever both apply.</para>
/// <para>A nested value reads its parent's line from its own epoch, so its epoch never stands before its parent's: a
/// write that moves a parent's line first settles every row nested on it at the write's tick
/// (<see cref="StateArena.TryWriteLive(int, CellKey, long, StateWriteKind, in ArenaTime, out string)"/> and
/// <see cref="StateArena.TrySettleRedeclared"/>), so the accumulation is continuous across the write and follows the
/// parent's motion, never its jumps.</para>
/// </remarks>
/// <param name="PerSecondNumerator">The rate's signed numerator, in the row's own displayed unit per second, or per unit
/// of the parent for a nested advance (see this type's remarks). Negative accumulates downward (decay); zero is declared
/// but inert, which holds a clock still.</param>
/// <param name="PerSecondDenominator">The rate's denominator. Refused at zero or below.</param>
/// <param name="By">The parent row a nested advance runs on: a document row whose slot advances. Absent, the advance
/// runs on the engine's seconds.</param>
/// <param name="Rate">The shape of a nested advance's rate over its parent's phase: pieces of a polynomial, each
/// starting at a share of the parent's turn. Absent, the shape is one everywhere. Refused without
/// <paramref name="By"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateAdvance(
    long PerSecondNumerator,
    long PerSecondDenominator,
    [property: DocumentName(WorldNameKind.State, WorldNameRole.Names)]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? By = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StateRatePiece>? Rate = null
) {
    /// <summary>The deepest chain of nested advances a read follows: a parent past it reads as unavailable, so its
    /// child holds its base.</summary>
    public const int MaxNestingDepth = 16;
    /// <summary>The most pieces one rate shape holds.</summary>
    public const int MaxRatePieces = 64;
    /// <summary>The highest power one rate piece's polynomial carries.</summary>
    public const int MaxRateDegree = 8;

    /// <summary>Gets whether the advance runs on a parent row rather than the engine's seconds.</summary>
    [JsonIgnore]
    public bool IsNested => (By is not null);
    /// <summary>Returns the compiled shape of a nested advance's rate, or <see langword="null"/> for none: an advance with
    /// no <see cref="Rate"/>, or one whose pieces the validator would refuse, accumulates at its rate alone.</summary>
    /// <returns>The compiled shape, cached on first use.</returns>
    public PeriodicPiecewisePolynomial? RateShape() => CompiledShape().Shape;

    /// <summary>Compiles a rate shape's pieces into the periodic polynomial a nested advance integrates.</summary>
    /// <param name="pieces">The pieces, in order, or <see langword="null"/> for none.</param>
    /// <param name="shape">The compiled shape, or <see langword="null"/> when there are no pieces or they are
    /// refused.</param>
    /// <param name="reason">Why the pieces are refused, or empty.</param>
    /// <returns><see langword="true"/> when the pieces are absent or compile.</returns>
    public static bool TryCompileShape(IReadOnlyList<StateRatePiece>? pieces, out PeriodicPiecewisePolynomial? shape, out string reason) {
        shape = null;
        reason = string.Empty;

        if (pieces is null) { return true; }
        if ((pieces.Count == 0) || (pieces.Count > MaxRatePieces)) {
            reason = $"rate holds {pieces.Count} pieces; a shape holds from one to {MaxRatePieces}";
            return false;
        }

        var starts = new Rational[pieces.Count];
        var polynomials = new RationalPolynomial[pieces.Count];

        for (var index = 0; (index < pieces.Count); index++) {
            var piece = pieces[index];

            if (piece is null) {
                reason = $"rate[{index}] is null";
                return false;
            }
            if (!Rational.TryParse(text: piece.From, value: out starts[index])) {
                reason = $"rate[{index}].from '{piece.From}' is not an exact rational (digits with an optional decimal fraction, or a quotient such as 5/8)";
                return false;
            }
            if ((piece.Coefficients is not { Count: > 0 } coefficients) || (coefficients.Count > (MaxRateDegree + 1))) {
                reason = $"rate[{index}].coefficients holds {piece.Coefficients?.Count ?? 0} coefficients; a piece holds from one to {MaxRateDegree + 1}, constant term first";
                return false;
            }

            var exact = new Rational[coefficients.Count];

            for (var power = 0; (power < coefficients.Count); power++) {
                if (!Rational.TryParse(text: coefficients[power], value: out exact[power])) {
                    reason = $"rate[{index}].coefficients[{power}] '{coefficients[power]}' is not an exact rational";
                    return false;
                }
            }

            polynomials[index] = new RationalPolynomial(coefficients: exact);
        }

        try {
            shape = new PeriodicPiecewisePolynomial(
                pieces: polynomials,
                starts: starts
            );
        } catch (ArgumentException error) {
            reason = $"rate pieces do not tile one turn: {error.Message}";
            return false;
        }

        return true;
    }

    /// <summary>Returns what the advance accumulates while its variable moves from one value to another: the rate times
    /// the shape's exact integral between them (the variable's own difference when there is no shape). The variable is
    /// the engine's seconds for an affine advance and the parent's exact value for a nested one; the result is in the
    /// row's displayed unit, before any rounding into the carrier.</summary>
    /// <param name="from">The variable's value at the epoch.</param>
    /// <param name="to">The variable's value at the read.</param>
    /// <returns>The exact accumulation.</returns>
    public Rational Accumulation(Rational from, Rational to) {
        if ((PerSecondNumerator == 0L) || (PerSecondDenominator <= 0L)) { return Rational.Zero; }

        var integral = ((IsNested && (RateShape() is { } shape))
            ? (shape.Integral(x: to) - shape.Integral(x: from))
            : (to - from)
        );

        return (new Rational(Numerator: PerSecondNumerator, Denominator: PerSecondDenominator) * integral);
    }
    /// <summary>Returns the exact value an advancing cell reads, in the row's displayed unit: the stored base plus the
    /// exact accumulation from its epoch (<see cref="Accumulation"/>), clamped into the row's envelope. A nested
    /// advance reads its parent through <paramref name="cells"/>; a parent it cannot read accumulates nothing.</summary>
    /// <param name="row">The carrying row.</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentEngineTick">The engine tick to read the value as of.</param>
    /// <param name="epochEngineTick">The carrying cell's own <see cref="StateCellClock.EpochEngineTick"/>.</param>
    /// <param name="cells">The cells a nested advance reads its parent from, or <see langword="null"/> for none.</param>
    /// <param name="depth">The nesting depth this read stands at; zero for a read that is not a parent's.</param>
    /// <returns>The exact, envelope-clamped value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public Rational ExactValue(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick, IStateClockCells? cells, int depth = 0) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var scale = CarrierScale(kind: row.Kind);
        var value = (new Rational(Numerator: baseValue, Denominator: scale) + ExactAccumulation(
            cells: cells,
            currentEngineTick: currentEngineTick,
            depth: depth,
            epochEngineTick: epochEngineTick
        ));

        if ((row.Min is { } lo) && (value < new Rational(Numerator: lo, Denominator: scale))) {
            return new Rational(Numerator: lo, Denominator: scale);
        }
        if ((row.Max is { } hi) && (value > new Rational(Numerator: hi, Denominator: scale))) {
            return new Rational(Numerator: hi, Denominator: scale);
        }

        return value;
    }
    /// <summary>Returns the polynomial an advancing cell's exact value follows from an engine tick on, in the engine ticks
    /// elapsed since it: the base plus the accumulation, composed through every parent's own polynomial, valid until the
    /// cell or any parent crosses a piece of its shape, a whole turn the shape's piece is taken on, or its envelope. A
    /// recipient that holds it predicts the value exactly at every tick of that span.</summary>
    /// <param name="row">The carrying row.</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="engineTick">The engine tick the polynomial's variable counts from; at or after the epoch.</param>
    /// <param name="epochEngineTick">The carrying cell's own <see cref="StateCellClock.EpochEngineTick"/>.</param>
    /// <param name="cells">The cells a nested advance reads its parent from, or <see langword="null"/> for none.</param>
    /// <param name="curve">The value, in the row's displayed unit, as a polynomial in the elapsed engine ticks; the zero
    /// polynomial when none is returned.</param>
    /// <param name="depth">The nesting depth this read stands at; zero for a read that is not a parent's.</param>
    /// <returns><see langword="true"/> when the value follows a polynomial from <paramref name="engineTick"/>: the
    /// engine tick stands at or after the epoch, every parent reads, and the value stands inside the envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public bool TryValueCurve(StateRow row, long baseValue, ulong engineTick, long epochEngineTick, IStateClockCells? cells, out RationalPolynomial curve, int depth = 0) {
        ArgumentNullException.ThrowIfNull(argument: row);

        curve = RationalPolynomial.Zero;

        var epoch = ((ulong)Math.Max(val1: epochEngineTick, val2: 0L));
        var scale = CarrierScale(kind: row.Kind);
        var stored = new Rational(Numerator: baseValue, Denominator: scale);

        if ((engineTick < epoch) || (PerSecondDenominator <= 0L)) { return false; }

        var rate = new Rational(Numerator: PerSecondNumerator, Denominator: PerSecondDenominator);

        if (!IsNested) {
            // The engine's seconds since the epoch, (engineTick - epoch + x) / TicksPerSecond, under the identity integral.
            var seconds = new RationalPolynomial(coefficients: [
                new Rational(Numerator: (engineTick - epoch), Denominator: FixedTickConversion.TicksPerSecond),
                new Rational(Numerator: BigInteger.One, Denominator: FixedTickConversion.TicksPerSecond),
            ]);

            curve = (RationalPolynomial.Constant(value: stored) + (rate * seconds));
        } else {
            if (
                (cells is null) || (depth >= MaxNestingDepth) ||
                !TryReadClockCurve(cells: cells, curve: out var parent, depth: (depth + 1), engineTick: engineTick, row: By!) ||
                !TryReadExactClock(cells: cells, depth: (depth + 1), engineTick: epoch, row: By!, value: out var atEpoch)
            ) {
                return false;
            }

            var integral = RationalPolynomial.Identity;
            var integralAtEpoch = atEpoch;

            if (RateShape() is { } shape) {
                var start = parent.Evaluate(x: Rational.Zero);

                integral = shape.IntegralOn(piece: shape.PieceAt(fraction: start.Fraction()), turn: start.Floor());
                integralAtEpoch = shape.Integral(x: atEpoch);
            }

            curve = (RationalPolynomial.Constant(value: (stored - (rate * integralAtEpoch))) + (rate * integral.Compose(inner: parent)));
        }

        var now = curve.Evaluate(x: Rational.Zero);

        return !(((row.Min is { } lo) && (now < new Rational(Numerator: lo, Denominator: scale))) ||
            ((row.Max is { } hi) && (now > new Rational(Numerator: hi, Denominator: scale))));
    }
    /// <summary>Reads a document row's exact clock value through a cell source: an advancing slot's
    /// <see cref="ExactValue"/>, or any other numeric slot's stored value.</summary>
    /// <param name="cells">The cell source.</param>
    /// <param name="row">The row's name.</param>
    /// <param name="engineTick">The engine tick to read the value as of.</param>
    /// <param name="depth">The nesting depth this read stands at.</param>
    /// <param name="value">The exact value, in the row's displayed unit; zero when none reads.</param>
    /// <returns><see langword="true"/> when the source holds the row's slot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    public static bool TryReadExactClock(IStateClockCells cells, string row, ulong engineTick, int depth, out Rational value) {
        ArgumentNullException.ThrowIfNull(argument: cells);

        value = Rational.Zero;

        if ((depth > MaxNestingDepth) || !cells.TryClockCell(row: row, cell: out var cell)) { return false; }

        value = ((cell.Advance is { } advance)
            ? advance.ExactValue(baseValue: cell.Base, cells: cells, currentEngineTick: engineTick, depth: depth, epochEngineTick: cell.EpochEngineTick, row: cell.Row)
            : new Rational(Numerator: cell.Base, Denominator: CarrierScale(kind: cell.Row.Kind)));

        return true;
    }
    /// <summary>Reads the polynomial a document row's exact clock value follows from an engine tick on, through a cell
    /// source: an advancing slot's <see cref="TryValueCurve"/>, or any other numeric slot's stored value, which holds
    /// still.</summary>
    /// <param name="cells">The cell source.</param>
    /// <param name="row">The row's name.</param>
    /// <param name="engineTick">The engine tick the polynomial's variable counts from.</param>
    /// <param name="depth">The nesting depth this read stands at.</param>
    /// <param name="curve">The value as a polynomial in the engine ticks elapsed since <paramref name="engineTick"/>; the
    /// zero polynomial when none is returned.</param>
    /// <returns><see langword="true"/> when the row's value follows a polynomial from there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    public static bool TryReadClockCurve(IStateClockCells cells, string row, ulong engineTick, int depth, out RationalPolynomial curve) {
        ArgumentNullException.ThrowIfNull(argument: cells);

        curve = RationalPolynomial.Zero;

        if ((depth > MaxNestingDepth) || !cells.TryClockCell(row: row, cell: out var cell)) { return false; }
        if (cell.Advance is { } advance) {
            return advance.TryValueCurve(baseValue: cell.Base, cells: cells, curve: out curve, depth: depth, engineTick: engineTick, epochEngineTick: cell.EpochEngineTick, row: cell.Row);
        }

        curve = RationalPolynomial.Constant(value: new Rational(Numerator: cell.Base, Denominator: CarrierScale(kind: cell.Row.Kind)));

        return true;
    }
    /// <summary>Returns the raw units one displayed unit of a kind spans: <c>2^16</c> for <see cref="CellKind.Fixed"/>
    /// and one for every other kind.</summary>
    /// <param name="kind">The row's kind.</param>
    /// <returns>The scale.</returns>
    public static long CarrierScale(CellKind kind) => ((kind == CellKind.Fixed)
        ? (1L << FixedQ4816.FractionBitCount)
        : 1L
    );

    // The exact accumulation from the epoch to the read: the engine's seconds for an affine advance, the parent's exact
    // value at both ends for a nested one, which reads nothing when the parent does not read.
    private Rational ExactAccumulation(ulong currentEngineTick, long epochEngineTick, IStateClockCells? cells, int depth) {
        var epoch = ((ulong)Math.Max(val1: epochEngineTick, val2: 0L));

        if ((PerSecondNumerator == 0L) || (currentEngineTick <= epoch)) { return Rational.Zero; }
        if (!IsNested) {
            return Accumulation(
                from: Rational.Zero,
                to: new Rational(Numerator: (currentEngineTick - epoch), Denominator: FixedTickConversion.TicksPerSecond)
            );
        }
        if (
            (cells is null) || (depth >= MaxNestingDepth) ||
            !TryReadExactClock(cells: cells, depth: (depth + 1), engineTick: epoch, row: By!, value: out var from) ||
            !TryReadExactClock(cells: cells, depth: (depth + 1), engineTick: currentEngineTick, row: By!, value: out var to)
        ) {
            return Rational.Zero;
        }

        return Accumulation(from: from, to: to);
    }
    // A nested advance's read: the base plus the exact accumulation truncated toward zero into the carrier, the sum
    // saturated into long as a whole and clamped into the envelope.
    private long ComputeNestedValue(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick, IStateClockCells? cells) {
        var delta = (ExactAccumulation(cells: cells, currentEngineTick: currentEngineTick, depth: 0, epochEngineTick: epochEngineTick) *
            ((BigInteger)CarrierScale(kind: row.Kind))).Truncate();
        var exact = (baseValue + delta);

        return row.ClampToEnvelope(value: ((exact > long.MaxValue)
            ? long.MaxValue
            : ((exact < long.MinValue)
                ? long.MinValue
                : ((long)exact))));
    }
    // A nested Fixed cell's turn: the exact value's fraction scaled by 2^64 and floored, or the read value's turn when
    // the envelope clamps it.
    private ulong NestedTurn(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick, IStateClockCells? cells, ulong readTurn) {
        var exact = (new Rational(Numerator: baseValue, Denominator: CarrierScale(kind: row.Kind)) +
            ExactAccumulation(cells: cells, currentEngineTick: currentEngineTick, depth: 0, epochEngineTick: epochEngineTick));
        var scale = CarrierScale(kind: row.Kind);

        if (((row.Min is { } lo) && (exact < new Rational(Numerator: lo, Denominator: scale))) ||
            ((row.Max is { } hi) && (exact > new Rational(Numerator: hi, Denominator: scale)))) {
            return readTurn;
        }

        return unchecked((ulong)(exact.Fraction() * (BigInteger.One << 64)).Floor());
    }
    private ShapeCache CompiledShape() {
        var cache = m_shape;

        if ((cache is null) || !ReferenceEquals(objA: cache.Pieces, objB: Rate)) {
            _ = TryCompileShape(pieces: Rate, reason: out _, shape: out var shape);
            cache = new ShapeCache(Pieces: Rate, Shape: shape);
            m_shape = cache;
        }

        return cache;
    }

    /// <summary>Computes <paramref name="row"/>'s current value: <paramref name="baseValue"/> plus the exact
    /// accumulation between <paramref name="epochEngineTick"/> and <paramref name="currentEngineTick"/>, clamped
    /// into the row's declared envelope. A <paramref name="currentEngineTick"/> preceding
    /// <paramref name="epochEngineTick"/> reads as zero elapsed rather than a negative accumulation. A nested advance
    /// reads its parent through <paramref name="cells"/>.</summary>
    /// <param name="row">The carrying row (for its <see cref="CellKind"/> and envelope).</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentEngineTick">The engine tick (<see cref="FixedTickConversion.TicksPerSecond"/> per second)
    /// to compute the value as of — never a simulation tick, and never converted at the world's current simulation
    /// rate.</param>
    /// <param name="epochEngineTick">The carrying cell's own <see cref="StateCellClock.EpochEngineTick"/> — the
    /// engine tick the base value was last explicitly set, or its behavior last settled.</param>
    /// <param name="cells">The cells a nested advance reads its parent from, or <see langword="null"/> for none, which
    /// holds a nested value at its base.</param>
    /// <returns>The computed, envelope-clamped raw value.</returns>
    public long ComputeCurrentValue(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick = 0L, IStateClockCells? cells = null) {
        ArgumentNullException.ThrowIfNull(argument: row);

        if (IsNested) {
            return ComputeNestedValue(baseValue: baseValue, cells: cells, currentEngineTick: currentEngineTick, epochEngineTick: epochEngineTick, row: row);
        }

        var delta = 0L;

        if (
            (PerSecondNumerator != 0) &&
            (currentEngineTick > ((ulong)Math.Max(
            val1: epochEngineTick,
            val2: 0L
        )))
        ) {
            var scale = ((row.Kind == CellKind.Fixed)
                ? (1L << FixedQ4816.FractionBitCount)
                : 1L
            );
            var elapsed = (currentEngineTick - ((ulong)Math.Max(
                val1: epochEngineTick,
                val2: 0L
            )));

            if (TryAccumulate(
                elapsed: elapsed,
                magnitude: out var magnitude,
                scale: scale
            )) {
                delta = ((PerSecondNumerator < 0)
                    ? -magnitude
                    : magnitude
                );
            } else {
                // A magnitude past long.MaxValue can still land inside long once the base is added (a drain from a
                // positive base), so the sum is formed exactly and saturated as a whole rather than the magnitude alone.
                var wide = AccumulatedMagnitude(
                    elapsed: elapsed,
                    scale: scale
                );
                var exact = (baseValue + ((PerSecondNumerator < 0)
                    ? -wide
                    : wide));

                return row.ClampToEnvelope(value: ((exact > long.MaxValue)
                    ? long.MaxValue
                    : ((exact < long.MinValue)
                        ? long.MinValue
                        : ((long)exact))));
            }
        }

        // A saturating add into long and a clamp after is the same answer as clamping the exact sum: every envelope
        // bound is itself a long, so saturation can only move a value that is already outside every bound, and it
        // moves it to the same side. Stating the envelope once (StateRow.ClampToEnvelope) is what keeps this read
        // clamp and the rule-effect write's "could this move the cell" test from drifting apart.
        return row.ClampToEnvelope(value: baseValue.AddSaturating(right: delta));
    }

    /// <summary>Returns the turn a <see cref="CellKind.Fixed"/> row's current value stands at, where one whole unit is one
    /// turn: the fractional part of the exact accumulation, as a fraction of a turn scaled by 2^64. The read value
    /// (<see cref="ComputeCurrentValue"/>) keeps only the carrier's sixteen fractional bits, so a clock read through it
    /// steps by a 65,536th of a turn; this keeps the base's bits and the rate's exact rational phase
    /// (<see cref="FixedConicOrbit.Phase(ulong, ulong, ulong)"/>), so a body a turn places moves smoothly between ticks.
    /// A value the row's envelope clamps, or a rate past what the exact form holds, answers the read value's turn. A
    /// nested advance's turn is its exact value's fraction (<see cref="ExactValue"/>), read through its parent.</summary>
    /// <param name="row">The carrying row.</param>
    /// <param name="baseValue">The row's stored raw cell value.</param>
    /// <param name="currentEngineTick">The engine tick to compute the turn as of.</param>
    /// <param name="epochEngineTick">The carrying cell's own <see cref="StateCellClock.EpochEngineTick"/>.</param>
    /// <param name="cells">The cells a nested advance reads its parent from, or <see langword="null"/> for none.</param>
    /// <returns>The turn, scaled by 2^64.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public ulong TurnAt(StateRow row, long baseValue, ulong currentEngineTick, long epochEngineTick = 0L, IStateClockCells? cells = null) {
        ArgumentNullException.ThrowIfNull(argument: row);

        var read = ComputeCurrentValue(baseValue: baseValue, cells: cells, currentEngineTick: currentEngineTick, epochEngineTick: epochEngineTick, row: row);
        var readTurn = (unchecked((ulong)read) << (64 - FixedQ4816.FractionBitCount));
        var epoch = ((ulong)Math.Max(val1: epochEngineTick, val2: 0L));

        if (IsNested) {
            return ((row.Kind == CellKind.Fixed)
                ? NestedTurn(baseValue: baseValue, cells: cells, currentEngineTick: currentEngineTick, epochEngineTick: epochEngineTick, readTurn: readTurn, row: row)
                : readTurn);
        }

        if ((row.Kind != CellKind.Fixed) || (PerSecondNumerator == 0) || (currentEngineTick <= epoch) || (PerSecondNumerator == long.MinValue) ||
            (PerSecondDenominator > (long)(ulong.MaxValue / FixedTickConversion.TicksPerSecond))) {
            return readTurn;
        }

        var elapsed = (currentEngineTick - epoch);

        // A clamped read is the envelope's value, which the exact turn would pass.
        if (TryAccumulate(elapsed: elapsed, magnitude: out var magnitude, scale: (1L << FixedQ4816.FractionBitCount)) &&
            (read != baseValue.AddSaturating(right: ((PerSecondNumerator < 0) ? -magnitude : magnitude)))) {
            return readTurn;
        }

        var phase = FixedConicOrbit.Phase(elapsedTicks: elapsed, periodTicks: (((ulong)PerSecondDenominator) * FixedTickConversion.TicksPerSecond), turns: ((ulong)Math.Abs(value: PerSecondNumerator)));
        var baseTurn = (unchecked((ulong)baseValue) << (64 - FixedQ4816.FractionBitCount));

        return unchecked((PerSecondNumerator < 0) ? (baseTurn - phase) : (baseTurn + phase));
    }

    // |rate| · scale allocated over the elapsed engine ticks, as ⌊elapsed · |rate| · scale / (denominator ·
    // TicksPerSecond)⌋ — the exact rational allocation of DiscreteMeasure, with the per-second rate's own denominator
    // folded together with the engine's fixed ticks-per-second so the allocation is exact over engine ticks directly.
    // The compiled signed-64-bit form answers every read the tick can produce in long arithmetic; the exact form
    // remains behind it for a rate or an elapsed span the bounded representation cannot hold, so the two never
    // disagree on a value, only on cost.
    private bool TryAccumulate(ulong elapsed, long scale, out long magnitude) {
        var compiled = CompiledFor(scale: scale);

        if (
            compiled.IsValid &&
            (elapsed <= long.MaxValue) &&
            compiled.TryAmountBetween(
            amount: out magnitude,
            end: ((long)elapsed),
            start: 0L
        )
        ) {
            return true;
        }

        magnitude = 0L;

        return false;
    }
    private BigInteger AccumulatedMagnitude(ulong elapsed, long scale) =>
        (TryAccumulate(
            elapsed: elapsed,
            magnitude: out var magnitude,
            scale: scale
        )
            ? magnitude
            : ExactMeasure(scale: scale).AmountBetween(
                end: elapsed,
                start: BigInteger.Zero
            )
        );
    private CompiledDiscreteMeasure64 CompiledFor(long scale) {
        var cache = m_compiled;

        // The rate fields are the cache key, so a `with` copy that changes the rate recompiles on its first read
        // rather than answering from the copied cache; the holder is one immutable reference, so a concurrent reader
        // sees either the old cache or the new one. Each scale compiles on its first read only.
        if (
            (cache is null) ||
            (cache.PerSecondNumerator != PerSecondNumerator) ||
            (cache.PerSecondDenominator != PerSecondDenominator)
        ) {
            cache = new CompiledMeasureCache(
                PerSecondNumerator: PerSecondNumerator,
                PerSecondDenominator: PerSecondDenominator
            );
            m_compiled = cache;
        }

        return cache.For(
            advance: this,
            scale: scale
        );
    }
    // The per-second rate's denominator, folded together with the engine's own fixed ticks-per-second, so the
    // resulting measure allocates directly over engine ticks: rate/second == (rate · scale)/(denominator ·
    // TicksPerSecond) per engine tick, exactly (never rounded), regardless of the world's own simulation rate.
    private DiscreteMeasure ExactMeasure(long scale) =>
        DiscreteMeasure.Rational(
            denominator: (((BigInteger)PerSecondDenominator) * FixedTickConversion.TicksPerSecond),
            numerator: (BigInteger.Abs(value: ((BigInteger)PerSecondNumerator)) * scale)
        );

    /// <summary>Tests equality over the authored members alone, the rate's pieces compared in order by value; the
    /// compiled caches are runtime acceleration and never part of the record's identity.</summary>
    public bool Equals(StateAdvance? other) =>
        ((other is not null) && (PerSecondNumerator == other.PerSecondNumerator) && (PerSecondDenominator == other.PerSecondDenominator) &&
            string.Equals(a: By, b: other.By, comparisonType: StringComparison.Ordinal) && StateRatePiece.SequenceEquals(left: Rate, right: other.Rate));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        value1: PerSecondNumerator,
        value2: PerSecondDenominator,
        value3: ((By is null) ? 0 : StringComparer.Ordinal.GetHashCode(obj: By)),
        value4: (Rate?.Count ?? -1)
    );

    // Runtime acceleration beside the immutable record, excluded from its equality above. Invalid compiled values are
    // cached too, so an exact-only rate does not retry compilation on every read.
    private CompiledMeasureCache? m_compiled;
    // The compiled shape, keyed by the pieces it was compiled from, so a `with` copy that replaces them recompiles.
    private ShapeCache? m_shape;

    private sealed record ShapeCache(IReadOnlyList<StateRatePiece>? Pieces, PeriodicPiecewisePolynomial? Shape);

    private sealed class CompiledMeasureCache(long PerSecondNumerator, long PerSecondDenominator) {
        private CompiledDiscreteMeasure64? m_fixed;
        private CompiledDiscreteMeasure64? m_integer;

        public long PerSecondNumerator { get; } = PerSecondNumerator;
        public long PerSecondDenominator { get; } = PerSecondDenominator;

        private static CompiledDiscreteMeasure64 Compile(StateAdvance advance, long scale) {
            _ = advance.ExactMeasure(scale: scale).TryCompileInt64(compiled: out var compiled);

            return compiled;
        }

        public CompiledDiscreteMeasure64 For(StateAdvance advance, long scale) {
            if (scale == FixedQ4816.One.Value) {
                return (m_fixed ??= Compile(
                    advance: advance,
                    scale: scale
                ));
            }

            return (m_integer ??= Compile(
                advance: advance,
                scale: scale
            ));
        }
    }
}
/// <summary>One piece of a nested advance's rate shape (<see cref="StateAdvance.Rate"/>): a polynomial in the parent's
/// phase that holds from <paramref name="From"/> to the next piece's start, or to the end of the turn.</summary>
/// <param name="From">Where the piece starts, as an exact share of the parent's turn: digits with an optional decimal
/// fraction, or a quotient such as <c>5/8</c>. The first piece starts at zero, and each later one after the one before
/// and below one.</param>
/// <param name="Coefficients">The polynomial's exact coefficients in the parent's phase, constant term first, each
/// spelled as <paramref name="From"/> is.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateRatePiece(
    [property: DocumentText("an exact rational share of a turn")] string From,
    [property: DocumentText("exact rational coefficients")] IReadOnlyList<string> Coefficients
) {
    /// <summary>Tests equality by value: the start and every coefficient, in order.</summary>
    /// <param name="other">The other piece.</param>
    /// <returns><see langword="true"/> when both spell the same piece.</returns>
    public bool Equals(StateRatePiece? other) =>
        ((other is not null) && string.Equals(a: From, b: other.From, comparisonType: StringComparison.Ordinal) &&
            (Coefficients ?? []).SequenceEqual(second: (other.Coefficients ?? []), comparer: StringComparer.Ordinal));
    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(
        value1: StringComparer.Ordinal.GetHashCode(obj: (From ?? string.Empty)),
        value2: (Coefficients?.Count ?? 0)
    );

    /// <summary>Tests two shapes for equality piece by piece, either absent equal only to the other absent.</summary>
    /// <param name="left">The first shape, or <see langword="null"/>.</param>
    /// <param name="right">The second shape, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when both are absent or hold equal pieces in order.</returns>
    public static bool SequenceEquals(IReadOnlyList<StateRatePiece>? left, IReadOnlyList<StateRatePiece>? right) =>
        (ReferenceEquals(objA: left, objB: right) || ((left is not null) && (right is not null) && left.SequenceEqual(second: right)));
}
/// <summary>What a nested advance reads of a document row's slot: the row, the advance governing the slot (or none),
/// its stored raw base and the engine tick its clock was settled at.</summary>
/// <param name="Row">The row.</param>
/// <param name="Advance">The advance governing the slot, or <see langword="null"/> for a slot that holds still.</param>
/// <param name="Base">The slot's stored raw value.</param>
/// <param name="EpochEngineTick">The slot's <see cref="StateCellClock.EpochEngineTick"/>.</param>
public readonly record struct StateClockCell(StateRow Row, StateAdvance? Advance, long Base, long EpochEngineTick) {
    /// <summary>Reads a declared row's slot as a nested advance reads it: its stored value, the advance its effective
    /// behavior names and its clock's engine epoch.</summary>
    /// <param name="row">The row.</param>
    /// <param name="cell">The slot, or <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when the row is an Int or Fixed row holding a slot cell.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="row"/> is <see langword="null"/>.</exception>
    public static bool TryFromRow(StateRow row, out StateClockCell cell) {
        ArgumentNullException.ThrowIfNull(argument: row);

        cell = default;

        if ((row.Kind is not (CellKind.Fixed or CellKind.Int)) || (StateRows.FindCell(cells: row.Cells, key: StateRow.SlotKey) is not { } slot)) {
            return false;
        }

        cell = new StateClockCell(
            Advance: EffectiveBehavior.Resolve(cell: slot, row: row).Advance,
            Base: slot.Value.Raw,
            EpochEngineTick: (slot.Clock?.EpochEngineTick ?? 0L),
            Row: row
        );

        return true;
    }
}
/// <summary>The cells of a list of declared rows, as a nested advance reads its parent from a document rather than an
/// arena.</summary>
/// <param name="rows">The rows, each named once.</param>
public sealed class StateRowClockCells(IReadOnlyList<StateRow> rows) : IStateClockCells {
    /// <inheritdoc />
    public bool TryClockCell(string row, out StateClockCell cell) {
        foreach (var candidate in rows) {
            if (string.Equals(a: candidate.Name.Value, b: row, comparisonType: StringComparison.Ordinal)) {
                return StateClockCell.TryFromRow(cell: out cell, row: candidate);
            }
        }

        cell = default;

        return false;
    }
}
/// <summary>The cells a nested advance reads its parent from: a live arena, a document's rows, or an arena seen with one
/// row as it stood before a redeclaration. Every source answers through the one evaluator on
/// <see cref="StateAdvance"/>, so a nested value reads the same from each.</summary>
public interface IStateClockCells {
    /// <summary>Returns a document row's slot as a nested advance reads it.</summary>
    /// <param name="row">The row's name.</param>
    /// <param name="cell">The slot, or <see langword="default"/>.</param>
    /// <returns><see langword="true"/> when the source holds a numeric slot of that row.</returns>
    bool TryClockCell(string row, out StateClockCell cell);
}

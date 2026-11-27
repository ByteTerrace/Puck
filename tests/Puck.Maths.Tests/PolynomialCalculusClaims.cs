using System.Numerics;

namespace Puck.Maths.Tests;

internal static class PolynomialCalculusClaims {
    private const int Seed = 20261125;

    public static string? RationalText() {
        // Spellings that must parse, with the value they denote, and the round trip of what ToString writes.
        (string Text, long Numerator, long Denominator)[] accepted = [
            ("0", 0, 1), ("-0", 0, 1), ("3", 3, 1), ("+7", 7, 1), ("-12.5", -25, 2), ("0.125", 1, 8), ("+0.5", 1, 2),
            ("7/3", 7, 3), ("-7/3", -7, 3), ("7/-3", -7, 3), ("-6/-4", 3, 2), ("10.000", 10, 1), ("0.0001", 1, 10_000),
        ];

        foreach (var (text, numerator, denominator) in accepted) {
            if (!Rational.TryParse(text: text, value: out var parsed) || (parsed.Numerator != numerator) || (parsed.Denominator != denominator)) {
                return $"'{text}' should parse to {numerator}/{denominator}";
            }
            if (Rational.Parse(text: parsed.ToString()) != parsed) {
                return $"{parsed} does not parse back from its own text";
            }
        }

        foreach (var text in new[] { "", "1/0", "1.", ".5", "1e3", " 1", "1 ", "1/2/3", "--1", "1.-5", "1.+5", "0x10", "1,5", "/2", "2/" }) {
            if (Rational.TryParse(text: text, value: out _)) {
                return $"'{text}' should be refused";
            }
        }
        try {
            _ = Rational.Parse(text: "one");
            return "Parse should refuse 'one'";
        } catch (FormatException) {
        }

        // Floor, fraction and truncation against the oracle's own floor, at both signs.
        var random = new Random(Seed: Seed);

        for (var index = 0; (index < 400); index++) {
            var numerator = random.NextInt64(minValue: -1_000_000, maxValue: 1_000_000);
            var denominator = random.NextInt64(minValue: 1, maxValue: 5_000);
            var value = new Rational(Numerator: numerator, Denominator: denominator);
            var floor = new Oracles.ExactFraction(Numerator: numerator, Denominator: denominator).Floor();
            var truncated = ((numerator < 0) ? -new Oracles.ExactFraction(Numerator: -numerator, Denominator: denominator).Floor() : floor);
            var fraction = value.Fraction();

            if (value.Truncate() != truncated) { return $"{value} truncates to {value.Truncate()}, not {truncated}"; }
            if ((fraction.Sign < 0) || (fraction >= Rational.One) || !new Oracles.ExactFraction(Numerator: (numerator - (floor * denominator)), Denominator: denominator).Equals(numerator: fraction.Numerator, denominator: fraction.Denominator)) {
                return $"{value} has fraction {fraction}, not {numerator - (floor * denominator)}/{denominator}";
            }
        }

        return null;
    }
    public static string? ExactCalculus() {
        var random = new Random(Seed: Seed);

        for (var round = 0; (round < 60); round++) {
            var (p, pOracle) = Polynomial(random: random, degree: random.Next(minValue: 0, maxValue: 5));
            var (q, qOracle) = Polynomial(random: random, degree: random.Next(minValue: 0, maxValue: 4));
            var scale = Small(random: random);
            var offset = Small(random: random);

            if (p.Antiderivative().Derivative() != p) { return $"the derivative of {p}'s antiderivative is {p.Antiderivative().Derivative()}"; }
            if (!p.Antiderivative().Evaluate(x: Rational.Zero).IsZero) { return $"{p}'s antiderivative is not zero at zero"; }
            if (p.Degree != (pOracle.Count - 1 - TrailingZeros(pOracle))) { return $"{p} reports degree {p.Degree}"; }

            for (var sample = 0; (sample < 6); sample++) {
                var x = Small(random: random);
                var xOracle = new Oracles.ExactFraction(Numerator: x.Numerator, Denominator: x.Denominator);
                var px = Oracles.PolynomialAt(coefficients: pOracle, x: xOracle);
                var qx = Oracles.PolynomialAt(coefficients: qOracle, x: xOracle);

                if (!Same(value: p.Evaluate(x: x), oracle: px)) { return $"{p} at {x} is {p.Evaluate(x: x)}"; }
                if (!Same(value: RationalPolynomial.Evaluate(coefficients: p.Coefficients, x: x), oracle: px)) { return $"the span kernel disagrees with {p} at {x}"; }
                if (!Same(value: p.Compose(inner: q).Evaluate(x: x), oracle: Oracles.PolynomialAt(coefficients: pOracle, x: qx))) { return $"{p} of {q} at {x} is not {p} at {q}({x})"; }
                if (!Same(value: p.Shift(offset: offset).Evaluate(x: x), oracle: Oracles.PolynomialAt(coefficients: pOracle, x: (xOracle + new Oracles.ExactFraction(Numerator: offset.Numerator, Denominator: offset.Denominator))))) { return $"{p} shifted by {offset} at {x}"; }
                if (!Same(value: (p + q).Evaluate(x: x), oracle: (px + qx))) { return $"({p} + {q}) at {x}"; }
                if (!Same(value: (p - q).Evaluate(x: x), oracle: (px - qx))) { return $"({p} - {q}) at {x}"; }
                if (!Same(value: (-p).Evaluate(x: x), oracle: (Oracles.ExactFraction.Of(value: BigInteger.Zero) - px))) { return $"-{p} at {x}"; }
                if (!Same(value: (p * q).Evaluate(x: x), oracle: (px * qx))) { return $"({p} * {q}) at {x}"; }
                if (!Same(value: (scale * p).Evaluate(x: x), oracle: (new Oracles.ExactFraction(Numerator: scale.Numerator, Denominator: scale.Denominator) * px))) { return $"{scale} * {p} at {x}"; }
                if (!Same(value: (p.Antiderivative().Evaluate(x: x) - p.Antiderivative().Evaluate(x: offset)), oracle: Oracles.PolynomialIntegral(coefficients: pOracle, from: new Oracles.ExactFraction(Numerator: offset.Numerator, Denominator: offset.Denominator), to: xOracle))) {
                    return $"{p}'s antiderivative does not integrate it from {offset} to {x}";
                }
            }

            // A remainder on division by (x - a)(x - b) has degree below two and agrees with the dividend at a and b.
            var a = Small(random: random);
            var b = (a + Rational.One);
            var divisor = (new RationalPolynomial(coefficients: [-a, Rational.One]) * new RationalPolynomial(coefficients: [-b, Rational.One]));
            var remainder = new RationalPolynomial(coefficients: RationalPolynomial.RemainderOf(dividend: p.Coefficients, divisor: divisor.Coefficients));

            if ((remainder.Degree >= 2) || (remainder.Evaluate(x: a) != p.Evaluate(x: a)) || (remainder.Evaluate(x: b) != p.Evaluate(x: b))) {
                return $"{p} mod {divisor} gives {remainder}";
            }
            if (new RationalPolynomial(coefficients: RationalPolynomial.DerivativeOf(coefficients: p.Coefficients)) != p.Derivative()) {
                return $"the span derivative of {p} differs from its derivative";
            }
            if (RationalPolynomial.DegreeOf(coefficients: p.Coefficients) != p.Degree) { return $"the span degree of {p}"; }
        }

        if ((RationalPolynomial.Zero.Degree != -1) || !RationalPolynomial.Zero.IsZero || !RationalPolynomial.Constant(value: Rational.Zero).IsZero) { return "the zero polynomial has degree -1"; }
        if ((RationalPolynomial.One.Evaluate(x: Rational.Two) != Rational.One) || (RationalPolynomial.Identity.Evaluate(x: Rational.Two) != Rational.Two)) { return "one and the identity"; }
        if ((RationalPolynomial.Identity[1] != Rational.One) || !RationalPolynomial.Identity[7].IsZero) { return "the indexer reads a coefficient, zero past the degree"; }
        try {
            _ = RationalPolynomial.Identity[-1];
            return "a negative power should be refused";
        } catch (ArgumentOutOfRangeException) {
        }
        try {
            _ = RationalPolynomial.RemainderOf(dividend: RationalPolynomial.Identity.Coefficients, divisor: []);
            return "division by the zero polynomial should be refused";
        } catch (DivideByZeroException) {
        }

        return null;
    }
    public static string? PeriodicIntegral() {
        var random = new Random(Seed: Seed);

        for (var round = 0; (round < 40); round++) {
            var pieceCount = random.Next(minValue: 1, maxValue: 5);
            var starts = new List<Rational> { Rational.Zero };

            while (starts.Count < pieceCount) {
                var candidate = new Rational(Numerator: random.Next(minValue: 1, maxValue: 24), Denominator: 24);

                if (!starts.Contains(item: candidate)) { starts.Add(item: candidate); }
            }

            starts.Sort();

            var pieces = new RationalPolynomial[pieceCount];
            var oraclePieces = new IReadOnlyList<Oracles.ExactFraction>[pieceCount];

            for (var index = 0; (index < pieceCount); index++) {
                (pieces[index], oraclePieces[index]) = Polynomial(random: random, degree: random.Next(minValue: 0, maxValue: 4));
            }

            var shape = new PeriodicPiecewisePolynomial(pieces: pieces, starts: starts.ToArray());
            var oracleStarts = starts.Select(selector: static start => new Oracles.ExactFraction(Numerator: start.Numerator, Denominator: start.Denominator)).ToArray();

            if (!Same(value: shape.TurnIntegral, oracle: Oracles.PeriodicIntegral(pieces: oraclePieces, starts: oracleStarts, x: Oracles.ExactFraction.Of(value: BigInteger.One)))) {
                return $"the turn integral {shape.TurnIntegral} is not the sum of its pieces";
            }

            for (var sample = 0; (sample < 8); sample++) {
                var x = new Rational(Numerator: random.Next(minValue: -150, maxValue: 150), Denominator: random.Next(minValue: 1, maxValue: 30));
                var xOracle = new Oracles.ExactFraction(Numerator: x.Numerator, Denominator: x.Denominator);
                var piece = shape.PieceAt(fraction: x.Fraction());

                if (!Same(value: shape.Integral(x: x), oracle: Oracles.PeriodicIntegral(pieces: oraclePieces, starts: oracleStarts, x: xOracle))) {
                    return $"the integral to {x} is {shape.Integral(x: x)}, not the walk over every turn";
                }
                if ((shape.Integral(x: (x + Rational.One)) - shape.Integral(x: x)) != shape.TurnIntegral) {
                    return $"a turn from {x} does not add the turn integral";
                }
                if (shape.Evaluate(x: x) != shape.Piece(piece: piece).Evaluate(x: x.Fraction())) {
                    return $"the value at {x} is not its piece's";
                }
                if ((x.Fraction() < shape.PieceStart(piece: piece)) || (x.Fraction() >= shape.PieceEnd(piece: piece))) {
                    return $"{x}'s piece {piece} does not hold its fraction";
                }
                if (shape.IntegralOn(piece: piece, turn: x.Floor()).Evaluate(x: x) != shape.Integral(x: x)) {
                    return $"the piece polynomial at {x} differs from the integral";
                }
            }

            // At a piece boundary the integral is continuous: the polynomial of the piece before reaches it there.
            for (var index = 1; (index < pieceCount); index++) {
                var boundary = (starts[index] + new Rational(Numerator: 3, Denominator: 1));

                if (shape.IntegralOn(piece: (index - 1), turn: 3).Evaluate(x: boundary) != shape.Integral(x: boundary)) {
                    return $"the integral jumps at piece {index}";
                }
            }
        }

        if (!PeriodicPiecewisePolynomial.Constant(value: Rational.Two).IsConstant || (PeriodicPiecewisePolynomial.Constant(value: Rational.Two).Integral(x: new Rational(Numerator: 7, Denominator: 2)) != new Rational(Numerator: 7, Denominator: 1))) {
            return "a constant integrates to itself times its span";
        }
        if (PeriodicPiecewisePolynomial.Constant(value: Rational.One).PieceCount != 1) { return "a constant holds one piece"; }

        foreach (var (starts, count) in new (Rational[] Starts, int Count)[] {
            ([], 0), ([Rational.One], 1), ([Rational.Zero, Rational.Zero], 2), ([Rational.Zero, Rational.One], 2), ([Rational.Zero], 2),
        }) {
            try {
                _ = new PeriodicPiecewisePolynomial(pieces: Enumerable.Repeat(element: RationalPolynomial.One, count: count).ToArray(), starts: starts);
                return $"starts [{string.Join(separator: ", ", values: starts)}] with {count} pieces should be refused";
            } catch (ArgumentException) {
            }
        }
        try {
            _ = PeriodicPiecewisePolynomial.Constant(value: Rational.One).PieceAt(fraction: Rational.One);
            return "a fraction of one turn should be refused";
        } catch (ArgumentOutOfRangeException) {
        }

        return null;
    }

    private static (RationalPolynomial Polynomial, IReadOnlyList<Oracles.ExactFraction> Oracle) Polynomial(Random random, int degree) {
        var coefficients = new Rational[(degree + 1)];
        var oracle = new Oracles.ExactFraction[(degree + 1)];

        for (var power = 0; (power <= degree); power++) {
            coefficients[power] = Small(random: random);
            oracle[power] = new Oracles.ExactFraction(Numerator: coefficients[power].Numerator, Denominator: coefficients[power].Denominator);
        }

        return (new RationalPolynomial(coefficients: coefficients), oracle);
    }
    private static Rational Small(Random random) => new(
        Numerator: random.Next(minValue: -40, maxValue: 41),
        Denominator: random.Next(minValue: 1, maxValue: 13)
    );
    private static bool Same(Rational value, Oracles.ExactFraction oracle) => oracle.Equals(numerator: value.Numerator, denominator: value.Denominator);
    private static int TrailingZeros(IReadOnlyList<Oracles.ExactFraction> coefficients) {
        var count = 0;

        for (var index = (coefficients.Count - 1); ((index >= 0) && coefficients[index].Numerator.IsZero); index--) {
            count++;
        }

        return count;
    }
}

namespace Puck.Maths;

/// <summary>
/// The operations of <see cref="FixedAffineForm"/>, <see cref="FixedAffineForm2"/> and <see cref="FixedAffineForm3"/>,
/// named as <see cref="FixedGradients"/> names the dual reader's: each returns a form holding the round-to-nearest
/// answer of the <see cref="FixedQ4816"/> carrier operation of its name at every point its operands hold (see
/// <see cref="FixedAffineForm"/>'s remarks). They are the carriers of the field formulas' affine lowering, the reading
/// that bounds a field over a box with its correlations kept.
/// </summary>
/// <remarks>
/// <para>A linear operation (a sum, a constant multiple, a dot with a constant, a rotation by a constant) carries the
/// shared symbols exactly, its rounded coefficients' half raws and the carrier's rounding added to the error. A
/// product adds its bilinear remainder, the square terms centred. A convex or concave function (an absolute value, a
/// root, a length) takes a slope near its secant's and bounds what the slope leaves by the function's own curvature over
/// the operand's range. A rounding, a power, a smoothstep or a quotient by a form encloses its interval rule's answer as
/// fresh noise. A minimum or maximum of forms whose ranges are apart is the nearer form itself, and otherwise
/// <c>(a + b ∓ |a − b|) / 2</c>, which keeps what the two share.</para>
/// <para>A comparison answers a <see cref="FixedAffineTruth"/>; a selection under an unknown truth is the
/// <see cref="Join(FixedAffineForm, FixedAffineForm)"/> of both arms.</para>
/// </remarks>
public static class FixedAffineForms {
    private const int FractionBitCount = FixedQ4816.FractionBitCount;
    private const long One = (1L << FractionBitCount);
    // The most bits an exact product of a form's magnitude and a weight may take before every sum of a handful of them
    // and its doubling for nearest rounding still fits Int128. A form that wide holds values far past the carrier, so
    // past it the answer is Entire.
    private const int WeightedBits = 124;
    private const int ProductBits = 120;
    // A component within 2^61 raws squares and sums inside Int128 for a three-vector's length.
    private const int NormBits = 61;
    // A rotation's linear image of a point within 2^58 raws keeps every fused stage of the point rotation inside the
    // carrier, which its four-raw error budget assumes.
    private const int RotationBits = 58;
    // The raws by which FixedQuaternion.RotateInverse can miss the exact linear image of its point: each of its two
    // fused stages rounds to the nearest raw, three raws at most for a unit quaternion, widened to four.
    private const long RotationSlackRaws = 4L;

    // === Lifts and components ===========================================================================================

    /// <summary>Lifts a constant scalar.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The form holding it alone.</returns>
    public static FixedAffineForm Lift(FixedQ4816 value) => value;
    /// <summary>Returns a form unchanged.</summary>
    /// <param name="value">The form.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedAffineForm Lift(FixedAffineForm value) => value;
    /// <summary>Lifts a constant two-vector.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The forms holding it alone.</returns>
    public static FixedAffineForm2 Lift(FixedVector2 value) => value;
    /// <summary>Returns a two-vector of forms unchanged.</summary>
    /// <param name="value">The vector.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedAffineForm2 Lift(FixedAffineForm2 value) => value;
    /// <summary>Lifts a constant three-vector.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The forms holding it alone.</returns>
    public static FixedAffineForm3 Lift(FixedVector3 value) => value;
    /// <summary>Returns a three-vector of forms unchanged.</summary>
    /// <param name="value">The vector.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedAffineForm3 Lift(FixedAffineForm3 value) => value;

    /// <summary>Returns the first component.</summary><param name="a">The vector.</param><returns>The component.</returns>
    public static FixedAffineForm X(FixedAffineForm2 a) => a.X;
    /// <summary>Returns the second component.</summary><param name="a">The vector.</param><returns>The component.</returns>
    public static FixedAffineForm Y(FixedAffineForm2 a) => a.Y;
    /// <summary>Returns the first component.</summary><param name="a">The vector.</param><returns>The component.</returns>
    public static FixedAffineForm X(FixedAffineForm3 a) => a.X;
    /// <summary>Returns the second component.</summary><param name="a">The vector.</param><returns>The component.</returns>
    public static FixedAffineForm Y(FixedAffineForm3 a) => a.Y;
    /// <summary>Returns the third component.</summary><param name="a">The vector.</param><returns>The component.</returns>
    public static FixedAffineForm Z(FixedAffineForm3 a) => a.Z;
    /// <summary>Builds a two-vector.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <returns>The vector.</returns>
    public static FixedAffineForm2 Vec2(FixedAffineForm x, FixedAffineForm y) => new(X: x, Y: y);
    /// <summary>Builds a three-vector.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    /// <returns>The vector.</returns>
    public static FixedAffineForm3 Vec3(FixedAffineForm x, FixedAffineForm y, FixedAffineForm z) => new(X: x, Y: y, Z: z);

    // === Sums ===========================================================================================================

    /// <summary>Adds, exactly.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm Add(FixedAffineForm a, FixedAffineForm b) => ((a.IsUnbounded || b.IsUnbounded)
        ? FixedAffineForm.Entire
        : FixedAffineForm.Make(
            center: (((Int128)a.RawCenter) + b.RawCenter),
            symbol0: (((Int128)a.RawSymbol0) + b.RawSymbol0),
            symbol1: (((Int128)a.RawSymbol1) + b.RawSymbol1),
            symbol2: (((Int128)a.RawSymbol2) + b.RawSymbol2),
            error: (((Int128)a.RawError) + b.RawError)
        ));
    /// <summary>Adds a constant.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm Add(FixedAffineForm a, FixedQ4816 b) => Add(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Adds to a constant.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm Add(FixedQ4816 a, FixedAffineForm b) => Add(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Subtracts, exactly.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm Subtract(FixedAffineForm a, FixedAffineForm b) => Add(a: a, b: Negate(a: b));
    /// <summary>Subtracts a constant.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm Subtract(FixedAffineForm a, FixedQ4816 b) => Subtract(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Subtracts from a constant.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm Subtract(FixedQ4816 a, FixedAffineForm b) => Subtract(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Negates, exactly.</summary><param name="a">The form.</param><returns>The negation.</returns>
    public static FixedAffineForm Negate(FixedAffineForm a) => (a.IsUnbounded
        ? FixedAffineForm.Entire
        : FixedAffineForm.Make(center: -((Int128)a.RawCenter), symbol0: -((Int128)a.RawSymbol0), symbol1: -((Int128)a.RawSymbol1), symbol2: -((Int128)a.RawSymbol2), error: a.RawError));

    /// <summary>Adds component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm2 Add(FixedAffineForm2 a, FixedAffineForm2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Adds a constant component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm2 Add(FixedAffineForm2 a, FixedVector2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Adds to a constant component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm2 Add(FixedVector2 a, FixedAffineForm2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Subtracts component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm2 Subtract(FixedAffineForm2 a, FixedAffineForm2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Subtracts a constant component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm2 Subtract(FixedAffineForm2 a, FixedVector2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Subtracts from a constant component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm2 Subtract(FixedVector2 a, FixedAffineForm2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Negates component by component.</summary><param name="a">The vector.</param><returns>The negation.</returns>
    public static FixedAffineForm2 Negate(FixedAffineForm2 a) => new(X: Negate(a: a.X), Y: Negate(a: a.Y));
    /// <summary>Adds component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm3 Add(FixedAffineForm3 a, FixedAffineForm3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Adds a constant component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm3 Add(FixedAffineForm3 a, FixedVector3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Adds to a constant component by component.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedAffineForm3 Add(FixedVector3 a, FixedAffineForm3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Subtracts component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm3 Subtract(FixedAffineForm3 a, FixedAffineForm3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Subtracts a constant component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm3 Subtract(FixedAffineForm3 a, FixedVector3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Subtracts from a constant component by component.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedAffineForm3 Subtract(FixedVector3 a, FixedAffineForm3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Negates component by component.</summary><param name="a">The vector.</param><returns>The negation.</returns>
    public static FixedAffineForm3 Negate(FixedAffineForm3 a) => new(X: Negate(a: a.X), Y: Negate(a: a.Y), Z: Negate(a: a.Z));

    // === Products and quotients =========================================================================================

    /// <summary>Multiplies two forms as the carrier's rounded product: the bilinear part exact, the square terms
    /// centred, the remainder and the rounding in the error.</summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <returns>The product.</returns>
    public static FixedAffineForm Multiply(FixedAffineForm a, FixedAffineForm b) => Product(a: a, b: b, pad: 1L);
    /// <summary>Multiplies by a constant as the carrier's rounded product; an integer factor multiplies exactly.</summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <returns>The product.</returns>
    public static FixedAffineForm Multiply(FixedAffineForm a, FixedQ4816 b) => (((b.Value & (One - 1L)) == 0L)
        ? Scale(a: a, denominator: 1, numerator: (b.Value >> FractionBitCount), pad: 0L)
        : Scale(a: a, denominator: One, numerator: b.Value, pad: 1L));
    /// <summary>Multiplies a constant by a form as the carrier's rounded product.</summary>
    /// <param name="a">The multiplicand.</param>
    /// <param name="b">The multiplier.</param>
    /// <returns>The product.</returns>
    public static FixedAffineForm Multiply(FixedQ4816 a, FixedAffineForm b) => Multiply(a: b, b: a);
    /// <summary>Divides two forms as the carrier's rounded quotient, through the interval quotient of their ranges:
    /// fresh noise, correlated with nothing; <see cref="FixedAffineForm.Entire"/> when the divisor's range holds
    /// zero.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static FixedAffineForm Divide(FixedAffineForm a, FixedAffineForm b) => FixedAffineForm.FromInterval(interval: (a.Range / b.Range));
    /// <summary>Divides by a constant as the carrier's rounded quotient; <see cref="FixedAffineForm.Entire"/> for a zero
    /// divisor.</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static FixedAffineForm Divide(FixedAffineForm a, FixedQ4816 b) => MultiplyDivide(value: a, divisor: b, multiplier: FixedQ4816.One);
    /// <summary>Divides a constant by a form, through the interval quotient (see
    /// <see cref="Divide(FixedAffineForm, FixedAffineForm)"/>).</summary>
    /// <param name="a">The dividend.</param>
    /// <param name="b">The divisor.</param>
    /// <returns>The quotient.</returns>
    public static FixedAffineForm Divide(FixedQ4816 a, FixedAffineForm b) => Divide(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Returns <c>value · multiplier / divisor</c> rounded once, as
    /// <see cref="FusedArithmetic.MultiplyDivide(FixedQ4816, FixedQ4816, FixedQ4816)"/> rounds it: a constant rational
    /// multiple, the symbols carried exactly; <see cref="FixedAffineForm.Entire"/> for a zero divisor.</summary>
    /// <param name="value">The form.</param>
    /// <param name="multiplier">The constant factor.</param>
    /// <param name="divisor">The constant divisor.</param>
    /// <returns>The rounded quotient of the product.</returns>
    public static FixedAffineForm MultiplyDivide(FixedAffineForm value, FixedQ4816 multiplier, FixedQ4816 divisor) => ((divisor.Value == 0L)
        ? FixedAffineForm.Entire
        : ((multiplier == divisor)
            ? value
            : Scale(a: value, denominator: divisor.Value, numerator: multiplier.Value, pad: 1L)));

    /// <summary>Scales a vector by a form, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm2 Multiply(FixedAffineForm2 a, FixedAffineForm b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b));
    /// <summary>Scales a vector by a constant, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm2 Multiply(FixedAffineForm2 a, FixedQ4816 b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b));
    /// <summary>Scales a constant vector by a form, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm2 Multiply(FixedVector2 a, FixedAffineForm b) => new(X: Multiply(a: b, b: a.X), Y: Multiply(a: b, b: a.Y));
    /// <summary>Scales a vector by a form, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 Multiply(FixedAffineForm3 a, FixedAffineForm b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b), Z: Multiply(a: a.Z, b: b));
    /// <summary>Scales a vector by a constant, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 Multiply(FixedAffineForm3 a, FixedQ4816 b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b), Z: Multiply(a: a.Z, b: b));
    /// <summary>Scales a constant vector by a form, component by component.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 Multiply(FixedVector3 a, FixedAffineForm b) => new(X: Multiply(a: b, b: a.X), Y: Multiply(a: b, b: a.Y), Z: Multiply(a: b, b: a.Z));
    /// <summary>Multiplies component by component.</summary><param name="a">The first factor.</param><param name="b">The second factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 MultiplyComponents(FixedAffineForm3 a, FixedAffineForm3 b) => new(X: Multiply(a: a.X, b: b.X), Y: Multiply(a: a.Y, b: b.Y), Z: Multiply(a: a.Z, b: b.Z));
    /// <summary>Multiplies by a constant component by component.</summary><param name="a">The first factor.</param><param name="b">The second factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 MultiplyComponents(FixedAffineForm3 a, FixedVector3 b) => new(X: Multiply(a: a.X, b: b.X), Y: Multiply(a: a.Y, b: b.Y), Z: Multiply(a: a.Z, b: b.Z));
    /// <summary>Multiplies a constant by a vector component by component.</summary><param name="a">The first factor.</param><param name="b">The second factor.</param><returns>The product.</returns>
    public static FixedAffineForm3 MultiplyComponents(FixedVector3 a, FixedAffineForm3 b) => MultiplyComponents(a: b, b: a);
    /// <summary>Divides component by component.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedAffineForm3 DivideComponents(FixedAffineForm3 a, FixedAffineForm3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));
    /// <summary>Divides by a constant component by component.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedAffineForm3 DivideComponents(FixedAffineForm3 a, FixedVector3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));
    /// <summary>Divides a constant by a vector component by component.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedAffineForm3 DivideComponents(FixedVector3 a, FixedAffineForm3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));

    // === Piecewise functions ============================================================================================

    /// <summary>Returns the absolute value, exactly as the carrier takes it: a form of one sign itself or its negation,
    /// and one that straddles zero the slope near its secant's plus the gap the kink leaves.</summary>
    /// <param name="a">The form.</param>
    /// <returns><c>|a|</c>.</returns>
    public static FixedAffineForm Abs(FixedAffineForm a) {
        if (a.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        var low = a.RawLow;
        var high = a.RawHigh;

        if (low >= 0) {
            return a;
        }

        if (high <= 0) {
            return Negate(a: a);
        }

        // Any slope α in [−1, 1] leaves g(v) = |v| − α·v convex, zero at zero and largest at an end of [low, high]; the
        // secant's (high + low) / (high − low) only keeps that largest value small, so its rounding costs nothing.
        var alpha = Int128.Clamp(value: FixedDirectedRounding.FloorQuotient(denominator: (high - low), numerator: ((high + low) << FractionBitCount)), min: -One, max: One);
        var top = FixedDirectedRounding.CeilingShift(shift: FractionBitCount, value: Int128.Max(x: (-low * (One + alpha)), y: (high * (One - alpha))));

        return Offset(form: Scale(a: a, denominator: One, numerator: alpha, pad: 0L), high: top, low: 0);
    }
    /// <summary>Returns the absolute value component by component.</summary><param name="a">The vector.</param><returns>The absolute values.</returns>
    public static FixedAffineForm2 Abs(FixedAffineForm2 a) => new(X: Abs(a: a.X), Y: Abs(a: a.Y));
    /// <summary>Returns the absolute value component by component.</summary><param name="a">The vector.</param><returns>The absolute values.</returns>
    public static FixedAffineForm3 Abs(FixedAffineForm3 a) => new(X: Abs(a: a.X), Y: Abs(a: a.Y), Z: Abs(a: a.Z));
    /// <summary>Returns the lesser of two forms as the carrier picks it: the nearer form itself where their ranges are
    /// apart, else <c>(a + b − |a − b|) / 2</c>.</summary>
    /// <param name="a">The first form.</param>
    /// <param name="b">The second form.</param>
    /// <returns>The minimum.</returns>
    public static FixedAffineForm Min(FixedAffineForm a, FixedAffineForm b) {
        if (a.IsUnbounded || b.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        if (a.RawHigh <= b.RawLow) {
            return a;
        }

        if (b.RawHigh <= a.RawLow) {
            return b;
        }

        return Scale(a: Subtract(a: Add(a: a, b: b), b: Abs(a: Subtract(a: a, b: b))), denominator: 2, numerator: 1, pad: 0L);
    }
    /// <summary>Returns the lesser of a form and a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The minimum.</returns>
    public static FixedAffineForm Min(FixedAffineForm a, FixedQ4816 b) => Min(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Returns the lesser of a constant and a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The minimum.</returns>
    public static FixedAffineForm Min(FixedQ4816 a, FixedAffineForm b) => Min(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Returns the greater of two forms as the carrier picks it: the farther form itself where their ranges
    /// are apart, else <c>(a + b + |a − b|) / 2</c>.</summary>
    /// <param name="a">The first form.</param>
    /// <param name="b">The second form.</param>
    /// <returns>The maximum.</returns>
    public static FixedAffineForm Max(FixedAffineForm a, FixedAffineForm b) {
        if (a.IsUnbounded || b.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        if (a.RawLow >= b.RawHigh) {
            return a;
        }

        if (b.RawLow >= a.RawHigh) {
            return b;
        }

        return Scale(a: Add(a: Add(a: a, b: b), b: Abs(a: Subtract(a: a, b: b))), denominator: 2, numerator: 1, pad: 0L);
    }
    /// <summary>Returns the greater of a form and a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The maximum.</returns>
    public static FixedAffineForm Max(FixedAffineForm a, FixedQ4816 b) => Max(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Returns the greater of a constant and a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The maximum.</returns>
    public static FixedAffineForm Max(FixedQ4816 a, FixedAffineForm b) => Max(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Clamps a form between two constants, as <see cref="FixedQ4816.Clamp"/> does for bounds in order.</summary>
    /// <param name="a">The form.</param>
    /// <param name="low">The lower bound.</param>
    /// <param name="high">The upper bound, at or above <paramref name="low"/>.</param>
    /// <returns>The clamped form.</returns>
    public static FixedAffineForm Clamp(FixedAffineForm a, FixedQ4816 low, FixedQ4816 high) => Min(a: Max(a: a, b: low), b: high);
    /// <summary>Clamps a form between two forms.</summary>
    /// <param name="a">The form.</param>
    /// <param name="low">The lower bound.</param>
    /// <param name="high">The upper bound.</param>
    /// <returns>The clamped form.</returns>
    public static FixedAffineForm Clamp(FixedAffineForm a, FixedAffineForm low, FixedAffineForm high) => Min(a: Max(a: a, b: low), b: high);
    /// <summary>Clamps component by component between two constant vectors.</summary>
    /// <param name="a">The vector.</param>
    /// <param name="low">The lower bounds.</param>
    /// <param name="high">The upper bounds.</param>
    /// <returns>The clamped vector.</returns>
    public static FixedAffineForm3 Clamp(FixedAffineForm3 a, FixedVector3 low, FixedVector3 high) => new(
        X: Clamp(a: a.X, high: high.X, low: low.X),
        Y: Clamp(a: a.Y, high: high.Y, low: low.Y),
        Z: Clamp(a: a.Z, high: high.Z, low: low.Z)
    );
    /// <summary>Clamps component by component between two vectors of forms.</summary>
    /// <param name="a">The vector.</param>
    /// <param name="low">The lower bounds.</param>
    /// <param name="high">The upper bounds.</param>
    /// <returns>The clamped vector.</returns>
    public static FixedAffineForm3 Clamp(FixedAffineForm3 a, FixedAffineForm3 low, FixedAffineForm3 high) => new(
        X: Clamp(a: a.X, high: high.X, low: low.X),
        Y: Clamp(a: a.Y, high: high.Y, low: low.Y),
        Z: Clamp(a: a.Z, high: high.Z, low: low.Z)
    );
    /// <summary>Returns the first form: the carrier saturates where the kernels' floats would overflow, so no fallback
    /// is taken.</summary>
    /// <param name="a">The form.</param>
    /// <param name="b">The fallback, unused.</param>
    /// <returns><paramref name="a"/>.</returns>
    public static FixedAffineForm FiniteOr(FixedAffineForm a, FixedAffineForm b) => a;
    /// <summary>Returns <c>a + (b − a)·t</c> rounded once, as <see cref="FixedQ4816.Lerp"/> rounds it.</summary>
    /// <param name="a">The value at zero.</param>
    /// <param name="b">The value at one.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The interpolation.</returns>
    public static FixedAffineForm Lerp(FixedAffineForm a, FixedAffineForm b, FixedAffineForm t) =>
        AddError(form: Add(a: a, b: Product(a: Subtract(a: b, b: a), b: t, pad: 0L)), raws: 1);
    /// <summary>Returns the component-wise <c>a + (b − a)·t</c> of two vector forms at one fraction, each component
    /// rounded once as the scalar <see cref="Lerp(FixedAffineForm, FixedAffineForm, FixedAffineForm)"/> rounds it.</summary>
    /// <param name="a">The vector at zero.</param>
    /// <param name="b">The vector at one.</param>
    /// <param name="t">The fraction.</param>
    /// <returns>The interpolation.</returns>
    public static FixedAffineForm3 Lerp(FixedAffineForm3 a, FixedAffineForm3 b, FixedAffineForm t) =>
        new(X: Lerp(a: a.X, b: b.X, t: t), Y: Lerp(a: a.Y, b: b.Y, t: t), Z: Lerp(a: a.Z, b: b.Z, t: t));
    /// <summary>Returns the smoothstep of a form between two constant edges, through the hull of the carrier's
    /// monotone <see cref="FixedQ4816.Smoothstep"/> at the ends of the form's range: fresh noise.</summary>
    /// <param name="edge0">The edge where the step is zero.</param>
    /// <param name="edge1">The edge where the step is one.</param>
    /// <param name="x">The form.</param>
    /// <returns>The step.</returns>
    public static FixedAffineForm Smoothstep(FixedQ4816 edge0, FixedQ4816 edge1, FixedAffineForm x) {
        if (x.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        var range = x.Range;

        return FixedAffineForm.FromInterval(interval: FixedInterval.Hull(
            first: FixedQ4816.Smoothstep(edge0: edge0, edge1: edge1, value: range.Lower),
            second: FixedQ4816.Smoothstep(edge0: edge0, edge1: edge1, value: range.Upper)
        ));
    }
    /// <summary>Returns the nearest integer of a form's every value as <see cref="FixedQ4816.Round"/> takes it,
    /// through the interval rule: fresh noise, or a point where the range rounds to one integer.</summary>
    /// <param name="a">The form.</param>
    /// <returns>The rounded form.</returns>
    public static FixedAffineForm Round(FixedAffineForm a) => FixedAffineForm.FromInterval(interval: FixedInterval.Round(value: a.Range));
    /// <summary>Rounds component by component (see <see cref="Round(FixedAffineForm)"/>).</summary><param name="a">The vector.</param><returns>The rounded vector.</returns>
    public static FixedAffineForm3 Round(FixedAffineForm3 a) => new(X: Round(a: a.X), Y: Round(a: a.Y), Z: Round(a: a.Z));
    /// <summary>Returns the floor of a form's every value, through the interval rule.</summary>
    /// <param name="a">The form.</param>
    /// <returns>The floored form.</returns>
    public static FixedAffineForm Floor(FixedAffineForm a) => FixedAffineForm.FromInterval(interval: FixedInterval.Floor(value: a.Range));
    /// <summary>Returns <c>a^exponent</c> for a constant exponent, through <see cref="FixedInterval.Pow"/> over the
    /// form's range: fresh noise, <see cref="FixedAffineForm.Entire"/> where that rule refuses.</summary>
    /// <param name="a">The base, every value at least zero.</param>
    /// <param name="exponent">The exponent, above zero.</param>
    /// <returns>The power.</returns>
    public static FixedAffineForm Power(FixedAffineForm a, FixedQ4816 exponent) =>
        FixedAffineForm.FromInterval(interval: FixedInterval.Pow(exponent: FixedInterval.FromPoint(value: exponent), value: a.Range));
    /// <summary>Returns the square root as <see cref="FixedQ4816.Sqrt"/> takes it (zero for a value not above zero): a
    /// slope no steeper than the root's at the range's top, which leaves the rest rising, and the rest's two ends.</summary>
    /// <param name="a">The form.</param>
    /// <returns>The root.</returns>
    public static FixedAffineForm Sqrt(FixedAffineForm a) {
        if (a.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        var low = a.RawLow;
        var high = a.RawHigh;

        if (high <= 0) {
            return FixedAffineForm.FromPoint(value: FixedQ4816.Zero);
        }

        if ((low < 0) || (low == high)) {
            return FixedAffineForm.FromInterval(interval: FixedInterval.Sqrt(value: a.Range));
        }

        // The root of raw x is r(x) = √(x·2¹⁶), whose slope 2¹⁵/r(x) falls as x rises; α ≤ 2¹⁶·r′(high) in Q16.
        var highRoot = FusedArithmetic.CeilingSquareRoot(radicand: (((UInt128)high) << FractionBitCount));
        var alpha = (((Int128)1) << ((2 * FractionBitCount) - 1)) / ((Int128)highRoot);
        var lowRoot = ((Int128)(((UInt128)low) << FractionBitCount).SquareRoot());
        var bottom = (lowRoot - FixedDirectedRounding.CeilingShift(shift: FractionBitCount, value: (alpha * low)));
        var top = (((Int128)highRoot) - (alpha * high >> FractionBitCount));

        return AddError(form: Offset(form: Scale(a: a, denominator: One, numerator: alpha, pad: 0L), high: top, low: bottom), raws: 1);
    }

    // === Lengths and projections ========================================================================================

    /// <summary>Returns the Euclidean length as <see cref="FixedVector2.Length"/> takes it (the exact root rounded
    /// once): the length's tangent plane at the vector's centre, which carries the symbols, plus what the curvature
    /// over the vector's deviation and the direction's rounding leave; a vector whose deviation reaches near its centre's
    /// length takes the interval norm of its ranges instead.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length.</returns>
    public static FixedAffineForm Length(FixedAffineForm2 a) => Norm(components: [a.X, a.Y]);
    /// <summary>Returns the Euclidean length as <see cref="FixedVector3.Length"/> takes it (see
    /// <see cref="Length(FixedAffineForm2)"/>).</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length.</returns>
    public static FixedAffineForm Length(FixedAffineForm3 a) => Norm(components: [a.X, a.Y, a.Z]);
    /// <summary>Returns the lesser of two lengths.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The lesser length.</returns>
    public static FixedAffineForm MinLength(FixedAffineForm2 a, FixedAffineForm2 b) => Min(a: Length(a: a), b: Length(a: b));
    /// <summary>Returns the dot product as <see cref="FixedVector2.Dot"/> takes it (exact products, one rounding).</summary>
    /// <param name="a">The first vector.</param><param name="b">The second vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedAffineForm2 a, FixedAffineForm2 b) =>
        AddError(form: Add(a: Product(a: a.X, b: b.X, pad: 0L), b: Product(a: a.Y, b: b.Y, pad: 0L)), raws: 1);
    /// <summary>Returns the dot product with a constant vector (exact weighted sum, one rounding).</summary>
    /// <param name="a">The vector.</param><param name="b">The constant vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedAffineForm2 a, FixedVector2 b) => Linear(denominator: One, forms: [a.X, a.Y], pad: 1L, weights: [b.X.Value, b.Y.Value]);
    /// <summary>Returns the dot product of a constant vector with a vector of forms.</summary>
    /// <param name="a">The constant vector.</param><param name="b">The vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedVector2 a, FixedAffineForm2 b) => Dot(a: b, b: a);
    /// <summary>Returns the dot product as <see cref="FixedVector3.Dot"/> takes it (exact products, one rounding).</summary>
    /// <param name="a">The first vector.</param><param name="b">The second vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedAffineForm3 a, FixedAffineForm3 b) =>
        AddError(form: Add(a: Add(a: Product(a: a.X, b: b.X, pad: 0L), b: Product(a: a.Y, b: b.Y, pad: 0L)), b: Product(a: a.Z, b: b.Z, pad: 0L)), raws: 1);
    /// <summary>Returns the dot product with a constant vector (exact weighted sum, one rounding).</summary>
    /// <param name="a">The vector.</param><param name="b">The constant vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedAffineForm3 a, FixedVector3 b) => Linear(denominator: One, forms: [a.X, a.Y, a.Z], pad: 1L, weights: [b.X.Value, b.Y.Value, b.Z.Value]);
    /// <summary>Returns the dot product of a constant vector with a vector of forms.</summary>
    /// <param name="a">The constant vector.</param><param name="b">The vector.</param><returns>The dot product.</returns>
    public static FixedAffineForm Dot(FixedVector3 a, FixedAffineForm3 b) => Dot(a: b, b: a);
    /// <summary>Returns the parameter of a point's projection onto a constant segment from the origin,
    /// <c>clamp(dot(p, s) / dot(s, s), 0, 1)</c> rounded once, as <see cref="FixedVector2.SegmentParameter"/> takes it:
    /// the quotient is linear in the point, so its symbols carry exactly; zero for a zero segment.</summary>
    /// <param name="p">The point.</param>
    /// <param name="segment">The segment's end.</param>
    /// <returns>The clamped parameter.</returns>
    public static FixedAffineForm SegmentParameter(FixedAffineForm2 p, FixedVector2 segment) => (Squares(segment: [segment.X.Value, segment.Y.Value]) is { } squares
        ? SegmentParameter(components: [p.X, p.Y], segment: [segment.X.Value, segment.Y.Value], squares: squares)
        : FixedAffineForm.FromInterval(interval: FixedInterval.SegmentParameter(segment: segment, x: p.X.Range, y: p.Y.Range)));
    /// <summary>Returns the parameter of a point's projection onto a constant segment from the origin, as
    /// <see cref="FixedVector3.SegmentParameter"/> takes it (see
    /// <see cref="SegmentParameter(FixedAffineForm2, FixedVector2)"/>).</summary>
    /// <param name="p">The point.</param>
    /// <param name="segment">The segment's end.</param>
    /// <returns>The clamped parameter.</returns>
    public static FixedAffineForm SegmentParameter(FixedAffineForm3 p, FixedVector3 segment) => (Squares(segment: [segment.X.Value, segment.Y.Value, segment.Z.Value]) is { } squares
        ? SegmentParameter(components: [p.X, p.Y, p.Z], segment: [segment.X.Value, segment.Y.Value, segment.Z.Value], squares: squares)
        : FixedAffineForm.FromInterval(interval: FixedInterval.SegmentParameter(segment: segment, x: p.X.Range, y: p.Y.Range, z: p.Z.Range)));
    /// <summary>Turns a point back through a constant rotation as <see cref="FixedQuaternion.RotateInverse"/> does: the
    /// exact linear map of the conjugate applied to the centre and every symbol's coefficients, so a rotated box stays a
    /// rotated box, and the point rotation's own few raws in the error.</summary>
    /// <param name="p">The point.</param>
    /// <param name="rotation">The unit rotation.</param>
    /// <returns>The point in the rotation's frame.</returns>
    public static FixedAffineForm3 RotateInverse(FixedAffineForm3 p, FixedQuaternion rotation) {
        if (p.IsUnbounded || (BitLength(value: Int128.Max(x: p.X.RawMagnitude, y: Int128.Max(x: p.Y.RawMagnitude, y: p.Z.RawMagnitude))) > RotationBits)) {
            return new FixedAffineForm3(X: FixedAffineForm.Entire, Y: FixedAffineForm.Entire, Z: FixedAffineForm.Entire);
        }

        // M = (1 − 2|u|²)·I + 2·u·uᵀ + 2w·[u]× for the conjugate u = −(x, y, z), at raw² scale: every entry is an exact
        // integer over 2³².
        var ux = -((Int128)rotation.X.Value);
        var uy = -((Int128)rotation.Y.Value);
        var uz = -((Int128)rotation.Z.Value);
        var w = ((Int128)rotation.W.Value);
        var diagonal = ((((Int128)1) << (2 * FractionBitCount)) - (2 * (((ux * ux) + (uy * uy)) + (uz * uz))));
        var denominator = (((Int128)1) << (2 * FractionBitCount));
        ReadOnlySpan<FixedAffineForm> point = [p.X, p.Y, p.Z];

        return new FixedAffineForm3(
            X: Linear(denominator: denominator, forms: point, pad: RotationSlackRaws, weights: [(diagonal + (2 * ux * ux)), (2 * ((ux * uy) - (w * uz))), (2 * ((ux * uz) + (w * uy)))]),
            Y: Linear(denominator: denominator, forms: point, pad: RotationSlackRaws, weights: [(2 * ((uy * ux) + (w * uz))), (diagonal + (2 * uy * uy)), (2 * ((uy * uz) - (w * ux)))]),
            Z: Linear(denominator: denominator, forms: point, pad: RotationSlackRaws, weights: [(2 * ((uz * ux) - (w * uy))), (2 * ((uz * uy) + (w * ux))), (diagonal + (2 * uz * uz))])
        );
    }

    // === Comparisons and selection ======================================================================================

    /// <summary>Lifts a decided condition.</summary>
    /// <param name="value">The condition.</param>
    /// <returns><see cref="FixedAffineTruth.True"/> or <see cref="FixedAffineTruth.False"/>.</returns>
    public static FixedAffineTruth Truth(bool value) => (value ? FixedAffineTruth.True : FixedAffineTruth.False);
    /// <summary>Returns Kleene's conjunction: true where both hold everywhere, false where either fails
    /// everywhere.</summary>
    /// <param name="a">The first truth.</param>
    /// <param name="b">The second truth.</param>
    /// <returns>The conjunction.</returns>
    public static FixedAffineTruth And(FixedAffineTruth a, FixedAffineTruth b) =>
        ((FixedAffineTruth)((((byte)a) & ((byte)b) & 2) | ((((byte)a) | ((byte)b)) & 1)));
    /// <summary>Returns whether <c>a &lt; b</c> at every point, at none, or at some.</summary><param name="a">The first form.</param><param name="b">The second form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Less(FixedAffineForm a, FixedAffineForm b) => Sign(difference: Subtract(a: a, b: b), strict: true);
    /// <summary>Returns whether a form is below a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The truth.</returns>
    public static FixedAffineTruth Less(FixedAffineForm a, FixedQ4816 b) => Less(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Returns whether a constant is below a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Less(FixedQ4816 a, FixedAffineForm b) => Less(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Returns whether <c>a ≤ b</c> at every point, at none, or at some.</summary><param name="a">The first form.</param><param name="b">The second form.</param><returns>The truth.</returns>
    public static FixedAffineTruth LessEqual(FixedAffineForm a, FixedAffineForm b) => Sign(difference: Subtract(a: a, b: b), strict: false);
    /// <summary>Returns whether a form is at or below a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The truth.</returns>
    public static FixedAffineTruth LessEqual(FixedAffineForm a, FixedQ4816 b) => LessEqual(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Returns whether a constant is at or below a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The truth.</returns>
    public static FixedAffineTruth LessEqual(FixedQ4816 a, FixedAffineForm b) => LessEqual(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Returns whether <c>a &gt; b</c> at every point, at none, or at some.</summary><param name="a">The first form.</param><param name="b">The second form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Greater(FixedAffineForm a, FixedAffineForm b) => Less(a: b, b: a);
    /// <summary>Returns whether a form is above a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The truth.</returns>
    public static FixedAffineTruth Greater(FixedAffineForm a, FixedQ4816 b) => Less(a: b, b: a);
    /// <summary>Returns whether a constant is above a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Greater(FixedQ4816 a, FixedAffineForm b) => Less(a: b, b: a);
    /// <summary>Returns whether <c>a ≥ b</c> at every point, at none, or at some.</summary><param name="a">The first form.</param><param name="b">The second form.</param><returns>The truth.</returns>
    public static FixedAffineTruth GreaterEqual(FixedAffineForm a, FixedAffineForm b) => LessEqual(a: b, b: a);
    /// <summary>Returns whether a form is at or above a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The truth.</returns>
    public static FixedAffineTruth GreaterEqual(FixedAffineForm a, FixedQ4816 b) => LessEqual(a: b, b: a);
    /// <summary>Returns whether a constant is at or above a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The truth.</returns>
    public static FixedAffineTruth GreaterEqual(FixedQ4816 a, FixedAffineForm b) => LessEqual(a: b, b: a);
    /// <summary>Returns whether <c>a = b</c> at every point, at none, or at some.</summary><param name="a">The first form.</param><param name="b">The second form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Equal(FixedAffineForm a, FixedAffineForm b) {
        var difference = Subtract(a: a, b: b);

        if (difference.IsUnbounded) {
            return FixedAffineTruth.Unknown;
        }

        var (low, high) = (difference.RawLow, difference.RawHigh);

        return (((low == 0) && (high == 0)) ? FixedAffineTruth.True : (((low > 0) || (high < 0)) ? FixedAffineTruth.False : FixedAffineTruth.Unknown));
    }
    /// <summary>Returns whether a form equals a constant.</summary><param name="a">The form.</param><param name="b">The constant.</param><returns>The truth.</returns>
    public static FixedAffineTruth Equal(FixedAffineForm a, FixedQ4816 b) => Equal(a: a, b: FixedAffineForm.FromPoint(value: b));
    /// <summary>Returns whether a constant equals a form.</summary><param name="a">The constant.</param><param name="b">The form.</param><returns>The truth.</returns>
    public static FixedAffineTruth Equal(FixedQ4816 a, FixedAffineForm b) => Equal(a: FixedAffineForm.FromPoint(value: a), b: b);
    /// <summary>Returns the form a condition selects: the first where it holds everywhere, the second where it fails
    /// everywhere, and both joined where it may go either way.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="a">The form where it holds.</param>
    /// <param name="b">The form where it fails.</param>
    /// <returns>The selection.</returns>
    public static FixedAffineForm Select(FixedAffineTruth condition, FixedAffineForm a, FixedAffineForm b) => condition switch {
        FixedAffineTruth.True => a,
        FixedAffineTruth.False => b,
        _ => Join(a: a, b: b),
    };
    /// <summary>Selects component by component (see <see cref="Select(FixedAffineTruth, FixedAffineForm, FixedAffineForm)"/>).</summary>
    /// <param name="condition">The condition.</param><param name="a">The vector where it holds.</param><param name="b">The vector where it fails.</param><returns>The selection.</returns>
    public static FixedAffineForm2 Select(FixedAffineTruth condition, FixedAffineForm2 a, FixedAffineForm2 b) =>
        new(X: Select(a: a.X, b: b.X, condition: condition), Y: Select(a: a.Y, b: b.Y, condition: condition));
    /// <summary>Selects component by component (see <see cref="Select(FixedAffineTruth, FixedAffineForm, FixedAffineForm)"/>).</summary>
    /// <param name="condition">The condition.</param><param name="a">The vector where it holds.</param><param name="b">The vector where it fails.</param><returns>The selection.</returns>
    public static FixedAffineForm3 Select(FixedAffineTruth condition, FixedAffineForm3 a, FixedAffineForm3 b) =>
        new(X: Select(a: a.X, b: b.X, condition: condition), Y: Select(a: a.Y, b: b.Y, condition: condition), Z: Select(a: a.Z, b: b.Z, condition: condition));
    /// <summary>Returns a form holding every value either form holds, at each point: the mean of their coefficients,
    /// with the error reaching the farther of the two.</summary>
    /// <param name="a">The first form.</param>
    /// <param name="b">The second form.</param>
    /// <returns>The join.</returns>
    public static FixedAffineForm Join(FixedAffineForm a, FixedAffineForm b) {
        if (a.IsUnbounded || b.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        var center = ((((Int128)a.RawCenter) + b.RawCenter) >> 1);
        var symbol0 = ((((Int128)a.RawSymbol0) + b.RawSymbol0) >> 1);
        var symbol1 = ((((Int128)a.RawSymbol1) + b.RawSymbol1) >> 1);
        var symbol2 = ((((Int128)a.RawSymbol2) + b.RawSymbol2) >> 1);

        Int128 Deviation(FixedAffineForm form) =>
            ((((Int128.Abs(value: (form.RawCenter - center)) + Int128.Abs(value: (form.RawSymbol0 - symbol0))) + Int128.Abs(value: (form.RawSymbol1 - symbol1))) + Int128.Abs(value: (form.RawSymbol2 - symbol2))) + form.RawError);

        return FixedAffineForm.Make(center: center, error: Int128.Max(x: Deviation(form: a), y: Deviation(form: b)), symbol0: symbol0, symbol1: symbol1, symbol2: symbol2);
    }
    /// <summary>Joins component by component.</summary><param name="a">The first vector.</param><param name="b">The second vector.</param><returns>The join.</returns>
    public static FixedAffineForm2 Join(FixedAffineForm2 a, FixedAffineForm2 b) => new(X: Join(a: a.X, b: b.X), Y: Join(a: a.Y, b: b.Y));
    /// <summary>Joins component by component.</summary><param name="a">The first vector.</param><param name="b">The second vector.</param><returns>The join.</returns>
    public static FixedAffineForm3 Join(FixedAffineForm3 a, FixedAffineForm3 b) => new(X: Join(a: a.X, b: b.X), Y: Join(a: a.Y, b: b.Y), Z: Join(a: a.Z, b: b.Z));

    // === The arithmetic every operation rests on ========================================================================

    private static FixedAffineTruth Sign(FixedAffineForm difference, bool strict) {
        if (difference.IsUnbounded) {
            return FixedAffineTruth.Unknown;
        }

        var (low, high) = (difference.RawLow, difference.RawHigh);

        if (strict ? (high < 0) : (high <= 0)) {
            return FixedAffineTruth.True;
        }

        return ((strict ? (low >= 0) : (low > 0)) ? FixedAffineTruth.False : FixedAffineTruth.Unknown);
    }
    // a · numerator / denominator: each coefficient rounded to the nearest raw (half a raw each, counted), the error
    // scaled up, and `pad` raws for the carrier operation's own rounding.
    private static FixedAffineForm Scale(FixedAffineForm a, Int128 numerator, Int128 denominator, long pad) {
        if (a.IsUnbounded || ((BitLength(value: a.RawMagnitude) + BitLength(value: Int128.Abs(value: numerator))) > WeightedBits)) {
            return FixedAffineForm.Entire;
        }

        if (denominator < 0) {
            numerator = -numerator;
            denominator = -denominator;
        }

        var inexact = 0;
        var center = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: (a.RawCenter * numerator));
        var symbol0 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: (a.RawSymbol0 * numerator));
        var symbol1 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: (a.RawSymbol1 * numerator));
        var symbol2 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: (a.RawSymbol2 * numerator));
        var error = ((CeilDivide(denominator: denominator, numerator: (a.RawError * Int128.Abs(value: numerator))) + ((inexact + 1) >> 1)) + pad);

        return FixedAffineForm.Make(center: center, error: error, symbol0: symbol0, symbol1: symbol1, symbol2: symbol2);
    }
    // Σ weights[i] · forms[i] / denominator, the sums exact and each coefficient rounded once.
    private static FixedAffineForm Linear(ReadOnlySpan<FixedAffineForm> forms, ReadOnlySpan<Int128> weights, Int128 denominator, long pad) {
        Int128 widest = 0;
        Int128 heaviest = 0;

        for (var index = 0; (index < forms.Length); index++) {
            if (forms[index].IsUnbounded) {
                return FixedAffineForm.Entire;
            }

            widest = Int128.Max(x: widest, y: forms[index].RawMagnitude);
            heaviest = Int128.Max(x: heaviest, y: Int128.Abs(value: weights[index]));
        }

        if ((BitLength(value: widest) + BitLength(value: heaviest) + 2) > WeightedBits) {
            return FixedAffineForm.Entire;
        }

        Int128 center = 0;
        Int128 symbol0 = 0;
        Int128 symbol1 = 0;
        Int128 symbol2 = 0;
        Int128 error = 0;

        for (var index = 0; (index < forms.Length); index++) {
            var form = forms[index];
            var weight = weights[index];

            center += (form.RawCenter * weight);
            symbol0 += (form.RawSymbol0 * weight);
            symbol1 += (form.RawSymbol1 * weight);
            symbol2 += (form.RawSymbol2 * weight);
            error += (form.RawError * Int128.Abs(value: weight));
        }

        var inexact = 0;

        center = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: center);
        symbol0 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: symbol0);
        symbol1 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: symbol1);
        symbol2 = RoundDivide(denominator: denominator, inexact: ref inexact, numerator: symbol2);

        return FixedAffineForm.Make(center: center, error: ((CeilDivide(denominator: denominator, numerator: error) + ((inexact + 1) >> 1)) + pad), symbol0: symbol0, symbol1: symbol1, symbol2: symbol2);
    }
    // The product of two forms, at raw² scale over 2¹⁶. With a = ca + La + ea and b = cb + Lb + eb, the linear part is
    // ca·cb + ca·Lb + cb·La; La·Lb puts Σ aᵢbᵢ/2 in the centre (εᵢ² lies in [0, 1]) and the rest of it in the error, with
    // every term an independent noise reaches.
    private static FixedAffineForm Product(FixedAffineForm a, FixedAffineForm b, long pad) {
        if (a.IsUnbounded || b.IsUnbounded || ((BitLength(value: a.RawMagnitude) + BitLength(value: b.RawMagnitude)) > ProductBits)) {
            return FixedAffineForm.Entire;
        }

        Int128 ca = a.RawCenter;
        Int128 cb = b.RawCenter;
        Int128 a0 = a.RawSymbol0;
        Int128 a1 = a.RawSymbol1;
        Int128 a2 = a.RawSymbol2;
        Int128 b0 = b.RawSymbol0;
        Int128 b1 = b.RawSymbol1;
        Int128 b2 = b.RawSymbol2;
        Int128 ea = a.RawError;
        Int128 eb = b.RawError;
        var inexact = 0;
        var center = RoundShift(inexact: ref inexact, shift: (FractionBitCount + 1), value: ((((2 * ca * cb) + (a0 * b0)) + (a1 * b1)) + (a2 * b2)));
        var symbol0 = RoundShift(inexact: ref inexact, shift: FractionBitCount, value: ((ca * b0) + (cb * a0)));
        var symbol1 = RoundShift(inexact: ref inexact, shift: FractionBitCount, value: ((ca * b1) + (cb * a1)));
        var symbol2 = RoundShift(inexact: ref inexact, shift: FractionBitCount, value: ((ca * b2) + (cb * a2)));
        var absoluteA = ((Int128.Abs(value: a0) + Int128.Abs(value: a1)) + Int128.Abs(value: a2));
        var absoluteB = ((Int128.Abs(value: b0) + Int128.Abs(value: b1)) + Int128.Abs(value: b2));
        var diagonal = ((Int128.Abs(value: (a0 * b0)) + Int128.Abs(value: (a1 * b1))) + Int128.Abs(value: (a2 * b2)));
        // Σ over i ≠ j of |aᵢbⱼ| is the whole product of the absolute sums less its diagonal.
        var cross = ((absoluteA * absoluteB) - diagonal);
        var twice = ((2 * ((((((Int128.Abs(value: ca) * eb) + (Int128.Abs(value: cb) * ea)) + cross) + (absoluteA * eb)) + (ea * absoluteB)) + (ea * eb))) + diagonal);
        var error = ((FixedDirectedRounding.CeilingShift(shift: (FractionBitCount + 1), value: twice) + ((inexact + 1) >> 1)) + pad);

        return FixedAffineForm.Make(center: center, error: error, symbol0: symbol0, symbol1: symbol1, symbol2: symbol2);
    }
    // The length of a vector of forms. With centre c (length s) and deviation w = v − c (|w| ≤ ρ), and a unit direction n
    // rounded toward zero so |n| ≤ 1: |v| = n·v + g with g ≥ 0 (Cauchy–Schwarz), and g at most ρ²/(2(s − ρ)) for the exact
    // direction n* = c/s, plus |n* − n|·|v| for the rounded one.
    private static FixedAffineForm Norm(ReadOnlySpan<FixedAffineForm> components) {
        Int128 widest = 0;

        foreach (var component in components) {
            if (component.IsUnbounded) {
                return FixedAffineForm.Entire;
            }

            widest = Int128.Max(x: widest, y: component.RawMagnitude);
        }

        if (BitLength(value: widest) > NormBits) {
            return FixedAffineForm.FromInterval(interval: RangeNorm(components: components));
        }

        Int128 centerSquares = 0;
        Int128 radiusSquares = 0;

        foreach (var component in components) {
            centerSquares += (((Int128)component.RawCenter) * component.RawCenter);
            radiusSquares += (component.RawRadius * component.RawRadius);
        }

        var rho = ((Int128)FusedArithmetic.CeilingSquareRoot(radicand: ((UInt128)radiusSquares)));
        var lowLength = ((Int128)((UInt128)centerSquares).SquareRoot());

        // Where the deviation reaches near the centre's length the tangent plane is no tighter than the ranges' norm.
        if (lowLength <= (2 * rho)) {
            return FixedAffineForm.FromInterval(interval: RangeNorm(components: components));
        }

        var highLength = ((Int128)FusedArithmetic.CeilingSquareRoot(radicand: ((UInt128)centerSquares)));
        Span<Int128> direction = stackalloc Int128[components.Length];

        for (var index = 0; (index < components.Length); index++) {
            direction[index] = ((((Int128)components[index].RawCenter) << FractionBitCount) / highLength);
        }

        var curvature = Int128.Min(x: (2 * rho), y: CeilDivide(denominator: (2 * (lowLength - rho)), numerator: (rho * rho)));
        // Each direction component misses c/s by under 2¹⁶/s + 1 in Q16, so the direction by under twice that.
        var turn = CeilDivide(denominator: One, numerator: ((2 * (CeilDivide(denominator: lowLength, numerator: One) + 1)) * (highLength + rho)));
        var linear = Linear(denominator: One, forms: components, pad: 0L, weights: direction);

        return AddError(form: Offset(form: linear, high: (curvature + turn), low: 0), raws: 1);
    }
    private static FixedInterval RangeNorm(ReadOnlySpan<FixedAffineForm> components) => ((components.Length == 2)
        ? FixedInterval.Magnitude(x: components[0].Range, y: components[1].Range)
        : FixedInterval.Magnitude(x: components[0].Range, y: components[1].Range, z: components[2].Range));
    // The exact squared length of a segment, or null past 2⁶¹ raws a component, where it squares past Int128 and the
    // interval rule takes the parameter whole.
    private static Int128? Squares(ReadOnlySpan<long> segment) {
        Int128 squares = 0;

        foreach (var component in segment) {
            if (BitLength(value: Int128.Abs(value: component)) > NormBits) {
                return null;
            }

            squares += (((Int128)component) * component);
        }

        return squares;
    }
    private static FixedAffineForm SegmentParameter(ReadOnlySpan<FixedAffineForm> components, ReadOnlySpan<long> segment, Int128 squares) {
        Span<Int128> weights = stackalloc Int128[segment.Length];

        for (var index = 0; (index < segment.Length); index++) {
            if (components[index].IsUnbounded) {
                return FixedAffineForm.Entire;
            }

            weights[index] = (((Int128)segment[index]) << FractionBitCount);
        }

        if (squares == 0) {
            return FixedAffineForm.FromPoint(value: FixedQ4816.Zero);
        }

        // The point side clamps after its one rounding, and both bounds are on the grid, so clamping the rounded
        // quotient's form clamps every value it holds.
        return Clamp(a: Linear(denominator: squares, forms: components, pad: 1L, weights: weights), high: FixedQ4816.One, low: FixedQ4816.Zero);
    }
    // The form plus every value of [low, high], as its own noise.
    private static FixedAffineForm Offset(FixedAffineForm form, Int128 low, Int128 high) {
        if (form.IsUnbounded) {
            return FixedAffineForm.Entire;
        }

        var middle = ((low + high) >> 1);

        return FixedAffineForm.Make(
            center: (form.RawCenter + middle),
            error: (form.RawError + Int128.Max(x: (high - middle), y: (middle - low))),
            symbol0: form.RawSymbol0,
            symbol1: form.RawSymbol1,
            symbol2: form.RawSymbol2
        );
    }
    private static FixedAffineForm AddError(FixedAffineForm form, long raws) => (form.IsUnbounded
        ? FixedAffineForm.Entire
        : FixedAffineForm.Make(center: form.RawCenter, error: (((Int128)form.RawError) + raws), symbol0: form.RawSymbol0, symbol1: form.RawSymbol1, symbol2: form.RawSymbol2));
    // The nearest integer to value / 2^shift, counting it when it is not exact.
    private static Int128 RoundShift(Int128 value, int shift, ref int inexact) {
        var rounded = ((value + (((Int128)1) << (shift - 1))) >> shift);

        if ((rounded << shift) != value) {
            inexact++;
        }

        return rounded;
    }
    // The nearest integer to numerator / denominator (denominator positive), counting it when it is not exact.
    private static Int128 RoundDivide(Int128 numerator, Int128 denominator, ref int inexact) {
        var rounded = FixedDirectedRounding.FloorQuotient(denominator: (denominator << 1), numerator: ((numerator << 1) + denominator));

        if ((rounded * denominator) != numerator) {
            inexact++;
        }

        return rounded;
    }
    private static Int128 CeilDivide(Int128 numerator, Int128 denominator) => FixedDirectedRounding.CeilingQuotient(denominator: denominator, numerator: numerator);
    private static int BitLength(Int128 value) => ((int)value.BitLength());
}

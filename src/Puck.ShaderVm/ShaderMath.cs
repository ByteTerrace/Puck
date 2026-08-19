namespace Puck.ShaderVm;

/// <summary>The value-graph spelling of every Shader VM operation that is not an operator.</summary>
public static class ShaderMath {
    /// <summary>Takes the component-wise absolute value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Abs(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Absolute, value: value);
    /// <summary>Takes the component-wise floor.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Floor(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Floor, value: value);
    /// <summary>Takes the component-wise fractional part.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Frac(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Fraction, value: value);
    /// <summary>Clamps a value to zero through one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Saturate(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Saturate, value: value);
    /// <summary>Takes the component-wise sign as minus one, zero, or one.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Sign(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Sign, value: value);
    /// <summary>Takes the component-wise square root.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Sqrt(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.SquareRoot, value: value);
    /// <summary>Raises e to a value, component-wise.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Exp(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Exponential, value: value);
    /// <summary>Takes the component-wise sine.</summary>
    /// <param name="value">The value, in radians.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Sin(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Sine, value: value);
    /// <summary>Takes the component-wise cosine.</summary>
    /// <param name="value">The value, in radians.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Cos(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Cosine, value: value);
    /// <summary>Hashes three lanes read as unsigned bit patterns, returning the raw result bits.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Hash3(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.Hash3, value: value);
    /// <summary>Scales each lane's unsigned bit pattern into the unit interval.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Unit(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.BitsToUnitFloat, value: value);
    /// <summary>Returns each lane's truncated integer value as a bit pattern.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression IntegerBits(ShaderExpression value) => ShaderExpression.Unary(op: ShaderOp.IntegerBits, value: value);

    /// <summary>Takes the component-wise minimum.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Min(ShaderExpression left, ShaderExpression right) => ShaderExpression.Binary(left: left, op: ShaderOp.Minimum, right: right);
    /// <summary>Takes the component-wise maximum.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Max(ShaderExpression left, ShaderExpression right) => ShaderExpression.Binary(left: left, op: ShaderOp.Maximum, right: right);
    /// <summary>Raises a value to a component-wise power.</summary>
    /// <param name="value">The base.</param>
    /// <param name="exponent">The exponent.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Pow(ShaderExpression value, ShaderExpression exponent) => ShaderExpression.Binary(left: value, op: ShaderOp.Power, right: exponent);
    /// <summary>Produces one where a value reaches an edge, and zero below it.</summary>
    /// <param name="edge">The edge.</param>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Step(ShaderExpression edge, ShaderExpression value) => ShaderExpression.Binary(left: edge, op: ShaderOp.Step, right: value);
    /// <summary>Takes the two-lane dot product, replicated to every lane.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Dot2(ShaderExpression left, ShaderExpression right) => ShaderExpression.Binary(left: left, op: ShaderOp.Dot2, right: right);
    /// <summary>Takes the angle to a point, measured from the positive abscissa.</summary>
    /// <param name="ordinate">The point's ordinate.</param>
    /// <param name="abscissa">The point's abscissa.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Atan2(ShaderExpression ordinate, ShaderExpression abscissa) => ShaderExpression.Binary(left: ordinate, op: ShaderOp.ArcTangent2, right: abscissa);
    /// <summary>Takes the three-lane dot product, replicated to every lane.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Dot3(ShaderExpression left, ShaderExpression right) => ShaderExpression.Binary(left: left, op: ShaderOp.Dot3, right: right);

    /// <summary>Linearly interpolates between two values.</summary>
    /// <param name="from">The value at zero.</param>
    /// <param name="to">The value at one.</param>
    /// <param name="amount">The interpolant.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Lerp(ShaderExpression from, ShaderExpression to, ShaderExpression amount) => ShaderExpression.Ternary(a: from, b: to, c: amount, op: ShaderOp.Lerp);
    /// <summary>Interpolates smoothly between two edges.</summary>
    /// <param name="edge0">The edge mapping to zero.</param>
    /// <param name="edge1">The edge mapping to one.</param>
    /// <param name="value">The value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression SmoothStep(ShaderExpression edge0, ShaderExpression edge1, ShaderExpression value) => ShaderExpression.Ternary(a: edge0, b: edge1, c: value, op: ShaderOp.SmoothStep);
    /// <summary>Clamps a value between two bounds.</summary>
    /// <param name="value">The value.</param>
    /// <param name="min">The lower bound.</param>
    /// <param name="max">The upper bound.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Clamp(ShaderExpression value, ShaderExpression min, ShaderExpression max) => ShaderExpression.Ternary(a: value, b: min, c: max, op: ShaderOp.Clamp);
    /// <summary>Selects one of two values per lane.</summary>
    /// <param name="condition">The lanes selecting the second value where they are non-zero.</param>
    /// <param name="whenTrue">The value taken where the condition holds.</param>
    /// <param name="whenFalse">The value taken elsewhere.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Select(ShaderExpression condition, ShaderExpression whenTrue, ShaderExpression whenFalse) => ShaderExpression.Ternary(a: whenFalse, b: whenTrue, c: condition, op: ShaderOp.Select);

    /// <summary>Samples the lattice noise at a value's first two lanes.</summary>
    /// <param name="position">The value whose first two lanes locate the sample and whose fourth carries the seed bits.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression ValueNoise2(ShaderExpression position) => ShaderExpression.Unary(op: ShaderOp.ValueNoise2, value: position);
    /// <summary>Sums lattice-noise octaves at a value's first two lanes.</summary>
    /// <param name="position">The value whose first two lanes locate the sample and whose fourth carries the seed bits.</param>
    /// <param name="octaves">The number of octaves to sum.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Fbm2(ShaderExpression position, int octaves) => ShaderExpression.Unary(op: ShaderOp.Fbm2, operand: checked((uint)octaves), value: position);
    /// <summary>Assembles the seeded sample position the lattice fields read.</summary>
    /// <param name="position">The value whose first two lanes locate the sample.</param>
    /// <param name="seed">The seed, truncated to an integer and carried as bits in the fourth lane.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Seeded(ShaderExpression position, ShaderExpression seed) => ShaderExpression.Combine(
        x: position.X,
        y: position.Y,
        z: ShaderExpression.Constant(value: 0f),
        w: IntegerBits(value: seed)
    );
    /// <summary>Assembles the seeded sample position the lattice fields read, for a seed fixed at compile time.</summary>
    /// <param name="position">The value whose first two lanes locate the sample.</param>
    /// <param name="seed">The seed.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Seeded(ShaderExpression position, uint seed) => ((position * ShaderExpression.Constant(x: 1f, y: 1f)) + ShaderExpression.Constant(
        x: 0f,
        y: 0f,
        z: 0f,
        w: BitConverter.UInt32BitsToSingle(seed)
    ));
}

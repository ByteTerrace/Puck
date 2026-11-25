namespace Puck.Maths;

/// <summary>A fixed-point scalar carried with its three partial derivatives along the world axes: the dual number
/// <c>v + g·ε</c> whose infinitesimal part is a vector (<c>εᵢεⱼ = 0</c>), so one forward-mode evaluation answers a value
/// and its whole gradient. <see cref="FixedDual{TValue}"/> carries one direction; this carries the three a field's
/// gradient needs in one walk. Every operation is integer arithmetic on the raws, so the result is bit-identical on every
/// machine.</summary>
/// <param name="Value">The value.</param>
/// <param name="Gradient">The value's partial derivative along each world axis.</param>
public readonly record struct FixedGradient(FixedQ4816 Value, FixedVector3 Gradient) {
    /// <summary>Lifts a constant, whose gradient is zero.</summary>
    /// <param name="value">The constant.</param>
    public static implicit operator FixedGradient(FixedQ4816 value) => new(Value: value, Gradient: FixedVector3.Zero);
}

/// <summary>A fixed-point two-vector whose components carry their gradients (<see cref="FixedGradient"/>).</summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
public readonly record struct FixedGradient2(FixedGradient X, FixedGradient Y) {
    /// <summary>Gets the components' values.</summary>
    public FixedVector2 Value => new(X: X.Value, Y: Y.Value);
}

/// <summary>A fixed-point three-vector whose components carry their gradients (<see cref="FixedGradient"/>): a point with
/// its Jacobian along the world axes, row by row.</summary>
/// <param name="X">The first component.</param>
/// <param name="Y">The second component.</param>
/// <param name="Z">The third component.</param>
public readonly record struct FixedGradient3(FixedGradient X, FixedGradient Y, FixedGradient Z) {
    /// <summary>Gets the components' values.</summary>
    public FixedVector3 Value => new(X: X.Value, Y: Y.Value, Z: Z.Value);

    /// <summary>Seeds a world point: its Jacobian is the identity.</summary>
    /// <param name="point">The point.</param>
    /// <returns>The point with each component's gradient its own axis.</returns>
    public static FixedGradient3 Seed(FixedVector3 point) => new(
        X: new FixedGradient(Value: point.X, Gradient: new FixedVector3(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero)),
        Y: new FixedGradient(Value: point.Y, Gradient: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero)),
        Z: new FixedGradient(Value: point.Z, Gradient: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One))
    );
}

/// <summary>The operations of <see cref="FixedGradient"/>, <see cref="FixedGradient2"/> and <see cref="FixedGradient3"/>:
/// each a fixed-point carrier operation on the values and its chain rule on the gradients, over two gradient-carrying
/// operands or one and a constant. A piecewise operation (an absolute value, a minimum, a clamp) takes the active branch's
/// derivative, deciding the branch by the values as the plain carrier does. These are the C# dual reader's carriers (the
/// field formulas' dual lowering), named as the kernels' dual library names its functions.</summary>
public static class FixedGradients {
    private static FixedVector3 Scaled(FixedVector3 gradient, FixedQ4816 factor) => (gradient * factor);

    /// <summary>Lifts a constant scalar.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The constant with a zero gradient.</returns>
    public static FixedGradient Lift(FixedQ4816 value) => value;
    /// <summary>Returns a gradient-carrying scalar unchanged.</summary>
    /// <param name="value">The scalar.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedGradient Lift(FixedGradient value) => value;
    /// <summary>Lifts a constant two-vector.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The constant with zero gradients.</returns>
    public static FixedGradient2 Lift(FixedVector2 value) => new(X: value.X, Y: value.Y);
    /// <summary>Returns a gradient-carrying two-vector unchanged.</summary>
    /// <param name="value">The vector.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedGradient2 Lift(FixedGradient2 value) => value;
    /// <summary>Lifts a constant three-vector.</summary>
    /// <param name="value">The constant.</param>
    /// <returns>The constant with zero gradients.</returns>
    public static FixedGradient3 Lift(FixedVector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
    /// <summary>Returns a gradient-carrying three-vector unchanged.</summary>
    /// <param name="value">The vector.</param>
    /// <returns><paramref name="value"/>.</returns>
    public static FixedGradient3 Lift(FixedGradient3 value) => value;

    /// <summary>Returns the first component.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The component.</returns>
    public static FixedGradient X(FixedGradient2 a) => a.X;
    /// <summary>Returns the second component.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The component.</returns>
    public static FixedGradient Y(FixedGradient2 a) => a.Y;
    /// <summary>Returns the first component.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The component.</returns>
    public static FixedGradient X(FixedGradient3 a) => a.X;
    /// <summary>Returns the second component.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The component.</returns>
    public static FixedGradient Y(FixedGradient3 a) => a.Y;
    /// <summary>Returns the third component.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The component.</returns>
    public static FixedGradient Z(FixedGradient3 a) => a.Z;
    /// <summary>Builds a two-vector.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <returns>The vector.</returns>
    public static FixedGradient2 Vec2(FixedGradient x, FixedGradient y) => new(X: x, Y: y);
    /// <summary>Builds a three-vector.</summary>
    /// <param name="x">The first component.</param>
    /// <param name="y">The second component.</param>
    /// <param name="z">The third component.</param>
    /// <returns>The vector.</returns>
    public static FixedGradient3 Vec3(FixedGradient x, FixedGradient y, FixedGradient z) => new(X: x, Y: y, Z: z);

    /// <summary>Adds.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient Add(FixedGradient a, FixedGradient b) => new(Value: (a.Value + b.Value), Gradient: (a.Gradient + b.Gradient));
    /// <summary>Adds a constant.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient Add(FixedGradient a, FixedQ4816 b) => new(Value: (a.Value + b), Gradient: a.Gradient);
    /// <summary>Adds to a constant.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient Add(FixedQ4816 a, FixedGradient b) => new(Value: (a + b.Value), Gradient: b.Gradient);
    /// <summary>Subtracts.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient Subtract(FixedGradient a, FixedGradient b) => new(Value: (a.Value - b.Value), Gradient: (a.Gradient - b.Gradient));
    /// <summary>Subtracts a constant.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient Subtract(FixedGradient a, FixedQ4816 b) => new(Value: (a.Value - b), Gradient: a.Gradient);
    /// <summary>Subtracts from a constant.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient Subtract(FixedQ4816 a, FixedGradient b) => new(Value: (a - b.Value), Gradient: -b.Gradient);
    /// <summary>Negates.</summary><param name="a">The operand.</param><returns>The negation.</returns>
    public static FixedGradient Negate(FixedGradient a) => new(Value: -a.Value, Gradient: -a.Gradient);

    /// <summary>Adds componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient2 Add(FixedGradient2 a, FixedGradient2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Adds a constant componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient2 Add(FixedGradient2 a, FixedVector2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Adds to a constant componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient2 Add(FixedVector2 a, FixedGradient2 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y));
    /// <summary>Subtracts componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient2 Subtract(FixedGradient2 a, FixedGradient2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Subtracts a constant componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient2 Subtract(FixedGradient2 a, FixedVector2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Subtracts from a constant componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient2 Subtract(FixedVector2 a, FixedGradient2 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y));
    /// <summary>Negates componentwise.</summary><param name="a">The operand.</param><returns>The negation.</returns>
    public static FixedGradient2 Negate(FixedGradient2 a) => new(X: Negate(a: a.X), Y: Negate(a: a.Y));

    /// <summary>Adds componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient3 Add(FixedGradient3 a, FixedGradient3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Adds a constant componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient3 Add(FixedGradient3 a, FixedVector3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Adds to a constant componentwise.</summary><param name="a">The augend.</param><param name="b">The addend.</param><returns>The sum.</returns>
    public static FixedGradient3 Add(FixedVector3 a, FixedGradient3 b) => new(X: Add(a: a.X, b: b.X), Y: Add(a: a.Y, b: b.Y), Z: Add(a: a.Z, b: b.Z));
    /// <summary>Subtracts componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient3 Subtract(FixedGradient3 a, FixedGradient3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Subtracts a constant componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient3 Subtract(FixedGradient3 a, FixedVector3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Subtracts from a constant componentwise.</summary><param name="a">The minuend.</param><param name="b">The subtrahend.</param><returns>The difference.</returns>
    public static FixedGradient3 Subtract(FixedVector3 a, FixedGradient3 b) => new(X: Subtract(a: a.X, b: b.X), Y: Subtract(a: a.Y, b: b.Y), Z: Subtract(a: a.Z, b: b.Z));
    /// <summary>Negates componentwise.</summary><param name="a">The operand.</param><returns>The negation.</returns>
    public static FixedGradient3 Negate(FixedGradient3 a) => new(X: Negate(a: a.X), Y: Negate(a: a.Y), Z: Negate(a: a.Z));

    /// <summary>Multiplies: the product rule.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient Multiply(FixedGradient a, FixedGradient b) =>
        new(Value: (a.Value * b.Value), Gradient: (Scaled(gradient: a.Gradient, factor: b.Value) + Scaled(gradient: b.Gradient, factor: a.Value)));
    /// <summary>Multiplies by a constant.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient Multiply(FixedGradient a, FixedQ4816 b) => new(Value: (a.Value * b), Gradient: Scaled(gradient: a.Gradient, factor: b));
    /// <summary>Multiplies a constant.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient Multiply(FixedQ4816 a, FixedGradient b) => new(Value: (a * b.Value), Gradient: Scaled(gradient: b.Gradient, factor: a));
    /// <summary>Divides: the quotient rule, <c>(a′ − q·b′) / b</c> with <c>q = a / b</c>.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient Divide(FixedGradient a, FixedGradient b) {
        var quotient = (a.Value / b.Value);

        return new(Value: quotient, Gradient: ((a.Gradient - Scaled(gradient: b.Gradient, factor: quotient)) / b.Value));
    }
    /// <summary>Divides by a constant.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient Divide(FixedGradient a, FixedQ4816 b) => new(Value: (a.Value / b), Gradient: (a.Gradient / b));
    /// <summary>Divides a constant.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient Divide(FixedQ4816 a, FixedGradient b) => Divide(a: Lift(value: a), b: b);
    /// <summary>Scales a vector.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient3 Multiply(FixedGradient3 a, FixedGradient b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b), Z: Multiply(a: a.Z, b: b));
    /// <summary>Scales a vector by a constant.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient3 Multiply(FixedGradient3 a, FixedQ4816 b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b), Z: Multiply(a: a.Z, b: b));
    /// <summary>Scales a constant vector.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient3 Multiply(FixedVector3 a, FixedGradient b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b), Z: Multiply(a: a.Z, b: b));
    /// <summary>Scales a vector.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient2 Multiply(FixedGradient2 a, FixedGradient b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b));
    /// <summary>Scales a vector by a constant.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient2 Multiply(FixedGradient2 a, FixedQ4816 b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b));
    /// <summary>Scales a constant vector.</summary><param name="a">The vector.</param><param name="b">The factor.</param><returns>The product.</returns>
    public static FixedGradient2 Multiply(FixedVector2 a, FixedGradient b) => new(X: Multiply(a: a.X, b: b), Y: Multiply(a: a.Y, b: b));
    /// <summary>Divides a vector.</summary><param name="a">The vector.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient3 Divide(FixedGradient3 a, FixedGradient b) => new(X: Divide(a: a.X, b: b), Y: Divide(a: a.Y, b: b), Z: Divide(a: a.Z, b: b));
    /// <summary>Divides a vector by a constant.</summary><param name="a">The vector.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient3 Divide(FixedGradient3 a, FixedQ4816 b) => new(X: Divide(a: a.X, b: b), Y: Divide(a: a.Y, b: b), Z: Divide(a: a.Z, b: b));
    /// <summary>Divides a vector.</summary><param name="a">The vector.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient2 Divide(FixedGradient2 a, FixedGradient b) => new(X: Divide(a: a.X, b: b), Y: Divide(a: a.Y, b: b));
    /// <summary>Divides a vector by a constant.</summary><param name="a">The vector.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient2 Divide(FixedGradient2 a, FixedQ4816 b) => new(X: Divide(a: a.X, b: b), Y: Divide(a: a.Y, b: b));
    /// <summary>Multiplies componentwise.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient3 MultiplyComponents(FixedGradient3 a, FixedGradient3 b) => new(X: Multiply(a: a.X, b: b.X), Y: Multiply(a: a.Y, b: b.Y), Z: Multiply(a: a.Z, b: b.Z));
    /// <summary>Multiplies by a constant componentwise.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient3 MultiplyComponents(FixedGradient3 a, FixedVector3 b) => new(X: Multiply(a: a.X, b: b.X), Y: Multiply(a: a.Y, b: b.Y), Z: Multiply(a: a.Z, b: b.Z));
    /// <summary>Multiplies a constant componentwise.</summary><param name="a">The multiplicand.</param><param name="b">The multiplier.</param><returns>The product.</returns>
    public static FixedGradient3 MultiplyComponents(FixedVector3 a, FixedGradient3 b) => new(X: Multiply(a: a.X, b: b.X), Y: Multiply(a: a.Y, b: b.Y), Z: Multiply(a: a.Z, b: b.Z));
    /// <summary>Divides componentwise.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient3 DivideComponents(FixedGradient3 a, FixedGradient3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));
    /// <summary>Divides by a constant componentwise.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient3 DivideComponents(FixedGradient3 a, FixedVector3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));
    /// <summary>Divides a constant componentwise.</summary><param name="a">The dividend.</param><param name="b">The divisor.</param><returns>The quotient.</returns>
    public static FixedGradient3 DivideComponents(FixedVector3 a, FixedGradient3 b) => new(X: Divide(a: a.X, b: b.X), Y: Divide(a: a.Y, b: b.Y), Z: Divide(a: a.Z, b: b.Z));

    /// <summary>Returns the absolute value: the negated gradient where the value is negative.</summary><param name="a">The operand.</param><returns>The absolute value.</returns>
    public static FixedGradient Abs(FixedGradient a) => ((a.Value < FixedQ4816.Zero) ? Negate(a: a) : a);
    /// <summary>Returns the absolute value componentwise.</summary><param name="a">The operand.</param><returns>The absolute value.</returns>
    public static FixedGradient2 Abs(FixedGradient2 a) => new(X: Abs(a: a.X), Y: Abs(a: a.Y));
    /// <summary>Returns the absolute value componentwise.</summary><param name="a">The operand.</param><returns>The absolute value.</returns>
    public static FixedGradient3 Abs(FixedGradient3 a) => new(X: Abs(a: a.X), Y: Abs(a: a.Y), Z: Abs(a: a.Z));
    /// <summary>Selects by a condition.</summary><param name="condition">The condition.</param><param name="a">The choice where it holds.</param><param name="b">The choice where it does not.</param><returns>The choice.</returns>
    public static FixedGradient Select(bool condition, FixedGradient a, FixedGradient b) => (condition ? a : b);
    /// <summary>Selects by a condition.</summary><param name="condition">The condition.</param><param name="a">The choice where it holds.</param><param name="b">The choice where it does not.</param><returns>The choice.</returns>
    public static FixedGradient2 Select(bool condition, FixedGradient2 a, FixedGradient2 b) => (condition ? a : b);
    /// <summary>Selects by a condition.</summary><param name="condition">The condition.</param><param name="a">The choice where it holds.</param><param name="b">The choice where it does not.</param><returns>The choice.</returns>
    public static FixedGradient3 Select(bool condition, FixedGradient3 a, FixedGradient3 b) => (condition ? a : b);
    /// <summary>Returns the lesser, the first on a tie.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The lesser.</returns>
    public static FixedGradient Min(FixedGradient a, FixedGradient b) => ((b.Value < a.Value) ? b : a);
    /// <summary>Returns the lesser of an operand and a constant.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The lesser.</returns>
    public static FixedGradient Min(FixedGradient a, FixedQ4816 b) => ((b < a.Value) ? b : a);
    /// <summary>Returns the lesser of a constant and an operand.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The lesser.</returns>
    public static FixedGradient Min(FixedQ4816 a, FixedGradient b) => ((b.Value < a) ? b : a);
    /// <summary>Returns the greater, the first on a tie.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The greater.</returns>
    public static FixedGradient Max(FixedGradient a, FixedGradient b) => ((b.Value > a.Value) ? b : a);
    /// <summary>Returns the greater of an operand and a constant.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The greater.</returns>
    public static FixedGradient Max(FixedGradient a, FixedQ4816 b) => ((b > a.Value) ? b : a);
    /// <summary>Returns the greater of a constant and an operand.</summary><param name="a">The first operand.</param><param name="b">The second.</param><returns>The greater.</returns>
    public static FixedGradient Max(FixedQ4816 a, FixedGradient b) => ((b.Value > a) ? b : a);
    /// <summary>Clamps between constants.</summary><param name="a">The operand.</param><param name="low">The least value.</param><param name="high">The greatest value.</param><returns>The clamped operand.</returns>
    public static FixedGradient Clamp(FixedGradient a, FixedQ4816 low, FixedQ4816 high) => Min(a: Max(a: a, b: low), b: high);
    /// <summary>Clamps between operands.</summary><param name="a">The operand.</param><param name="low">The least value.</param><param name="high">The greatest value.</param><returns>The clamped operand.</returns>
    public static FixedGradient Clamp(FixedGradient a, FixedGradient low, FixedGradient high) => Min(a: Max(a: a, b: low), b: high);
    /// <summary>Clamps between constants componentwise.</summary><param name="a">The operand.</param><param name="low">The least values.</param><param name="high">The greatest values.</param><returns>The clamped operand.</returns>
    public static FixedGradient3 Clamp(FixedGradient3 a, FixedVector3 low, FixedVector3 high) =>
        new(X: Clamp(a: a.X, high: high.X, low: low.X), Y: Clamp(a: a.Y, high: high.Y, low: low.Y), Z: Clamp(a: a.Z, high: high.Z, low: low.Z));
    /// <summary>Clamps between operands componentwise.</summary><param name="a">The operand.</param><param name="low">The least values.</param><param name="high">The greatest values.</param><returns>The clamped operand.</returns>
    public static FixedGradient3 Clamp(FixedGradient3 a, FixedGradient3 low, FixedGradient3 high) =>
        new(X: Clamp(a: a.X, high: high.X, low: low.X), Y: Clamp(a: a.Y, high: high.Y, low: low.Y), Z: Clamp(a: a.Z, high: high.Z, low: low.Z));
    /// <summary>Returns the first operand: the fixed carrier saturates rather than overflowing to a non-finite value.</summary><param name="a">The operand.</param><param name="b">The fallback, unused.</param><returns><paramref name="a"/>.</returns>
    public static FixedGradient FiniteOr(FixedGradient a, FixedGradient b) => a;
    /// <summary>Interpolates, <c>a + (b − a)·t</c>.</summary><param name="a">The start.</param><param name="b">The end.</param><param name="t">The amount.</param><returns>The interpolant.</returns>
    public static FixedGradient Lerp(FixedGradient a, FixedGradient b, FixedGradient t) => Add(a: a, b: Multiply(a: Subtract(a: b, b: a), b: t));
    /// <summary>Returns the smoothstep of an operand between constant edges.</summary><param name="edge0">The lower edge.</param><param name="edge1">The upper edge.</param><param name="x">The operand.</param><returns>The smoothstep.</returns>
    public static FixedGradient Smoothstep(FixedQ4816 edge0, FixedQ4816 edge1, FixedGradient x) {
        var t = FixedQ4816.Clamp(maximum: FixedQ4816.One, minimum: FixedQ4816.Zero, value: ((x.Value - edge0) / (edge1 - edge0)));
        var slope = (((t > FixedQ4816.Zero) && (t < FixedQ4816.One)) ? (((FixedQ4816.FromInteger(value: 6L) * t) * (FixedQ4816.One - t)) / (edge1 - edge0)) : FixedQ4816.Zero);

        return new(Value: FixedQ4816.Smoothstep(edge0: edge0, edge1: edge1, value: x.Value), Gradient: Scaled(gradient: x.Gradient, factor: slope));
    }

    // Σ aᵢ·gᵢ over the components' values and one gradient component each, exact as a Q32 product sum.
    private static (bool Negative, UInt128 Magnitude) WeightedSum(ReadOnlySpan<FixedQ4816> weights, ReadOnlySpan<FixedQ4816> terms) {
        (bool Negative, UInt128 Magnitude) sum = (false, UInt128.Zero);

        for (var index = 0; (index < weights.Length); index++) {
            var product = FusedArithmetic.Product(left: weights[index].Value, right: terms[index].Value);

            sum = FusedArithmetic.CombineSigned(firstMagnitude: sum.Magnitude, firstNegative: sum.Negative, secondMagnitude: product.Magnitude, secondNegative: product.Negative);
        }
        return sum;
    }
    // The gradient Σ wᵢ·∇cᵢ / d over a vector's components cᵢ, each partial one exact product sum divided once.
    private static FixedVector3 ProjectedGradient(ReadOnlySpan<FixedQ4816> weights, ReadOnlySpan<FixedVector3> gradients, UInt128 denominator) {
        Span<FixedQ4816> terms = stackalloc FixedQ4816[weights.Length];
        Span<long> axes = stackalloc long[3];

        for (var axis = 0; (axis < 3); axis++) {
            for (var index = 0; (index < weights.Length); index++) { terms[index] = gradients[index][axis]; }
            axes[axis] = FusedArithmetic.DivideProductSum(denominator: denominator, numerator: WeightedSum(terms: terms, weights: weights));
        }
        return new FixedVector3(X: FixedQ4816.FromRawBits(value: axes[0]), Y: FixedQ4816.FromRawBits(value: axes[1]), Z: FixedQ4816.FromRawBits(value: axes[2]));
    }

    /// <summary>Returns the length: the value the carrier's exact root answers, and its gradient <c>(v·∇v) / |v|</c> as one
    /// exact product sum divided once; zero at a zero length, which every direction leaves alike.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length.</returns>
    public static FixedGradient Length(FixedGradient2 a) {
        var length = a.Value.Length;

        return (length.Value == 0L)
            ? (FixedGradient)length
            : new FixedGradient(Value: length, Gradient: ProjectedGradient(denominator: (((UInt128)(ulong)length.Value) << 16), gradients: [a.X.Gradient, a.Y.Gradient], weights: [a.X.Value, a.Y.Value]));
    }
    /// <summary>Returns the length, as the two-vector overload does.</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The length.</returns>
    public static FixedGradient Length(FixedGradient3 a) {
        var length = a.Value.Length;

        return (length.Value == 0L)
            ? (FixedGradient)length
            : new FixedGradient(Value: length, Gradient: ProjectedGradient(denominator: (((UInt128)(ulong)length.Value) << 16), gradients: [a.X.Gradient, a.Y.Gradient, a.Z.Gradient], weights: [a.X.Value, a.Y.Value, a.Z.Value]));
    }
    /// <summary>Returns the smaller of two lengths.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The smaller length.</returns>
    public static FixedGradient MinLength(FixedGradient2 a, FixedGradient2 b) => Min(a: Length(a: a), b: Length(a: b));
    /// <summary>Returns the dot product.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedGradient2 a, FixedGradient2 b) => Add(a: Multiply(a: a.X, b: b.X), b: Multiply(a: a.Y, b: b.Y));
    /// <summary>Returns the dot product with a constant.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedGradient2 a, FixedVector2 b) => Add(a: Multiply(a: a.X, b: b.X), b: Multiply(a: a.Y, b: b.Y));
    /// <summary>Returns the dot product of a constant.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedVector2 a, FixedGradient2 b) => Dot(a: b, b: a);
    /// <summary>Returns the dot product.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedGradient3 a, FixedGradient3 b) => Add(a: Add(a: Multiply(a: a.X, b: b.X), b: Multiply(a: a.Y, b: b.Y)), b: Multiply(a: a.Z, b: b.Z));
    /// <summary>Returns the dot product with a constant.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedGradient3 a, FixedVector3 b) => Add(a: Add(a: Multiply(a: a.X, b: b.X), b: Multiply(a: a.Y, b: b.Y)), b: Multiply(a: a.Z, b: b.Z));
    /// <summary>Returns the dot product of a constant.</summary><param name="a">The first vector.</param><param name="b">The second.</param><returns>The dot product.</returns>
    public static FixedGradient Dot(FixedVector3 a, FixedGradient3 b) => Dot(a: b, b: a);

    /// <summary>Returns a point's saturated projection parameter on a constant segment from the origin, as
    /// <see cref="FixedVector3.SegmentParameter"/> answers it, with the gradient <c>(s·∇p) / (s·s)</c> where the parameter
    /// is strictly inside the segment and zero where it clamps; zero for a zero segment.</summary>
    /// <param name="p">The point.</param>
    /// <param name="s">The segment's end.</param>
    /// <returns>The parameter.</returns>
    public static FixedGradient SegmentParameter(FixedGradient3 p, FixedVector3 s) {
        var t = FixedVector3.SegmentParameter(point: p.Value, segment: s);

        if ((t.Value <= 0L) || (t >= FixedQ4816.One)) { return t; }
        return new(Value: t, Gradient: ProjectedGradient(denominator: WeightedSum(terms: [s.X, s.Y, s.Z], weights: [s.X, s.Y, s.Z]).Magnitude, gradients: [p.X.Gradient, p.Y.Gradient, p.Z.Gradient], weights: [s.X, s.Y, s.Z]));
    }
    /// <summary>Returns a point's saturated projection parameter on a constant segment, as the three-vector overload does.</summary>
    /// <param name="p">The point.</param>
    /// <param name="s">The segment's end.</param>
    /// <returns>The parameter.</returns>
    public static FixedGradient SegmentParameter(FixedGradient2 p, FixedVector2 s) {
        var t = FixedVector2.SegmentParameter(point: p.Value, segment: s);

        if ((t.Value <= 0L) || (t >= FixedQ4816.One)) { return t; }
        return new(Value: t, Gradient: ProjectedGradient(denominator: WeightedSum(terms: [s.X, s.Y], weights: [s.X, s.Y]).Magnitude, gradients: [p.X.Gradient, p.Y.Gradient], weights: [s.X, s.Y]));
    }
    /// <summary>Returns a point's saturated projection parameter on a segment that carries its own gradient.</summary>
    /// <param name="p">The point.</param>
    /// <param name="s">The segment's end.</param>
    /// <returns>The parameter.</returns>
    public static FixedGradient SegmentParameter(FixedGradient3 p, FixedGradient3 s) {
        var t = FixedVector3.SegmentParameter(point: p.Value, segment: s.Value);

        if ((t.Value <= 0L) || (t >= FixedQ4816.One)) { return t; }
        return new(Value: t, Gradient: Divide(a: Dot(a: p, b: s), b: Dot(a: s, b: s)).Gradient);
    }
    /// <summary>Returns a point's saturated projection parameter on a segment that carries its own gradient.</summary>
    /// <param name="p">The point.</param>
    /// <param name="s">The segment's end.</param>
    /// <returns>The parameter.</returns>
    public static FixedGradient SegmentParameter(FixedGradient2 p, FixedGradient2 s) {
        var t = FixedVector2.SegmentParameter(point: p.Value, segment: s.Value);

        if ((t.Value <= 0L) || (t >= FixedQ4816.One)) { return t; }
        return new(Value: t, Gradient: Divide(a: Dot(a: p, b: s), b: Dot(a: s, b: s)).Gradient);
    }

    /// <summary>Turns a point back through a unit quaternion: its value as <see cref="FixedQuaternion.RotateInverse"/>
    /// turns it, and every column of its Jacobian (its derivative along one world axis) turned with it.</summary>
    /// <param name="p">The point.</param>
    /// <param name="q">The rotation.</param>
    /// <returns>The turned point.</returns>
    public static FixedGradient3 RotateInverse(FixedGradient3 p, FixedQuaternion q) {
        var value = q.RotateInverse(vector: p.Value);
        var x = q.RotateInverse(vector: new FixedVector3(X: p.X.Gradient.X, Y: p.Y.Gradient.X, Z: p.Z.Gradient.X));
        var y = q.RotateInverse(vector: new FixedVector3(X: p.X.Gradient.Y, Y: p.Y.Gradient.Y, Z: p.Z.Gradient.Y));
        var z = q.RotateInverse(vector: new FixedVector3(X: p.X.Gradient.Z, Y: p.Y.Gradient.Z, Z: p.Z.Gradient.Z));

        return new(
            X: new FixedGradient(Value: value.X, Gradient: new FixedVector3(X: x.X, Y: y.X, Z: z.X)),
            Y: new FixedGradient(Value: value.Y, Gradient: new FixedVector3(X: x.Y, Y: y.Y, Z: z.Y)),
            Z: new FixedGradient(Value: value.Z, Gradient: new FixedVector3(X: x.Z, Y: y.Z, Z: z.Z))
        );
    }
}

namespace Puck.ShaderVm;

using System.Numerics;

/// <summary>One node of a Shader VM value graph, compiled to a packed program by <see cref="ShaderExpressionCompiler"/>.</summary>
/// <remarks>
/// Sharing is by reference: a node bound to a variable and used twice is evaluated once and spilled to a local.
/// Rebuilding an identical node instead evaluates it twice.
/// </remarks>
public sealed class ShaderExpression {
    private ShaderExpression(ShaderOp op, uint operand, ShaderExpression[] children, Vector4 constant, ShaderExpressionKind kind) {
        Children = children;
        ConstantValue = constant;
        Kind = kind;
        Op = op;
        Operand = operand;
    }

    internal ShaderExpression[] Children { get; }
    internal Vector4 ConstantValue { get; }
    internal ShaderExpressionKind Kind { get; }
    internal ShaderOp Op { get; }
    internal uint Operand { get; }

    /// <summary>Gets the two-lane length of this value, replicated to every lane.</summary>
    public ShaderExpression Length2 => Unary(op: ShaderOp.Length2, value: this);
    /// <summary>Gets the three-lane length of this value, replicated to every lane.</summary>
    public ShaderExpression Length3 => Unary(op: ShaderOp.Length3, value: this);
    /// <summary>Gets this value with its first two lanes normalized and the rest cleared.</summary>
    public ShaderExpression Normalized2 => Unary(op: ShaderOp.Normalize2, value: this);
    /// <summary>Gets this value with its first three lanes normalized and the fourth cleared.</summary>
    public ShaderExpression Normalized3 => Unary(op: ShaderOp.Normalize3, value: this);
    /// <summary>Gets this value's first lane, replicated to every lane.</summary>
    public ShaderExpression X => Lane(lane: 0);
    /// <summary>Gets this value's second lane, replicated to every lane.</summary>
    public ShaderExpression Y => Lane(lane: 1);
    /// <summary>Gets this value's third lane, replicated to every lane.</summary>
    public ShaderExpression Z => Lane(lane: 2);
    /// <summary>Gets this value's fourth lane, replicated to every lane.</summary>
    public ShaderExpression W => Lane(lane: 3);

    /// <summary>Creates a node loading an execution-context input.</summary>
    /// <param name="input">The input to load.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Input(ShaderInput input) => new(
        constant: Vector4.Zero,
        children: [],
        kind: ShaderExpressionKind.Input,
        op: ShaderOp.LoadInput,
        operand: ((uint)input)
    );
    /// <summary>Creates a node loading a caller-supplied parameter.</summary>
    /// <param name="index">The parameter index.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Parameter(int index) => new(
        constant: Vector4.Zero,
        children: [],
        kind: ShaderExpressionKind.Parameter,
        op: ShaderOp.LoadParameter,
        operand: checked((uint)index)
    );
    /// <summary>Creates a node loading a four-lane constant.</summary>
    /// <param name="value">The constant value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Constant4(Vector4 value) => new(
        children: [],
        constant: value,
        kind: ShaderExpressionKind.Constant,
        op: ShaderOp.LoadConstant,
        operand: 0u
    );
    /// <summary>Creates a node loading one value replicated to every lane.</summary>
    /// <param name="value">The scalar value.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Constant(float value) => Constant4(value: new Vector4(value: value));
    /// <summary>Creates a node loading one four-lane constant from its lanes.</summary>
    /// <param name="x">The first lane.</param>
    /// <param name="y">The second lane.</param>
    /// <param name="z">The third lane.</param>
    /// <param name="w">The fourth lane.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Constant(float x, float y, float z = 0f, float w = 0f) => Constant4(value: new Vector4(w: w, x: x, y: y, z: z));
    /// <summary>Creates a node loading the constant whose index an expression supplies.</summary>
    /// <param name="index">The expression whose first lane indexes the pool.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression ConstantAt(ShaderExpression index) => Unary(op: ShaderOp.LoadConstantDynamic, value: index);

    /// <summary>Adds two values.</summary>
    public static ShaderExpression operator +(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Add, right: right);
    /// <summary>Subtracts the right value from the left.</summary>
    public static ShaderExpression operator -(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Subtract, right: right);
    /// <summary>Multiplies two values.</summary>
    public static ShaderExpression operator *(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Multiply, right: right);
    /// <summary>Divides the left value by the right.</summary>
    public static ShaderExpression operator /(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Divide, right: right);
    /// <summary>Negates a value.</summary>
    public static ShaderExpression operator -(ShaderExpression value) => Unary(op: ShaderOp.Negate, value: value);
    /// <summary>Produces one where the left value is less than the right, and zero elsewhere.</summary>
    public static ShaderExpression operator <(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Less, right: right);
    /// <summary>Produces one where the left value is greater than the right, and zero elsewhere.</summary>
    public static ShaderExpression operator >(ShaderExpression left, ShaderExpression right) => Binary(left: left, op: ShaderOp.Greater, right: right);
    /// <summary>Lifts a scalar to a node replicating it across every lane.</summary>
    /// <param name="value">The scalar value.</param>
    public static implicit operator ShaderExpression(float value) => Constant(value: value);

    /// <summary>Creates a node applying one single-operand operation.</summary>
    /// <param name="op">The operation.</param>
    /// <param name="value">The operand.</param>
    /// <param name="operand">The instruction operand.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Unary(ShaderOp op, ShaderExpression value, uint operand = 0u) => new(
        constant: Vector4.Zero,
        children: [value],
        kind: ShaderExpressionKind.Operation,
        op: op,
        operand: operand
    );
    /// <summary>Creates a node applying one two-operand operation.</summary>
    /// <param name="left">The value the operation reads first.</param>
    /// <param name="op">The operation.</param>
    /// <param name="right">The value the operation reads second.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Binary(ShaderExpression left, ShaderOp op, ShaderExpression right) => new(
        constant: Vector4.Zero,
        children: [left, right],
        kind: ShaderExpressionKind.Operation,
        op: op,
        operand: 0u
    );
    /// <summary>Creates a node applying one three-operand operation.</summary>
    /// <param name="a">The value the operation reads first.</param>
    /// <param name="b">The value the operation reads second.</param>
    /// <param name="c">The value the operation reads third.</param>
    /// <param name="op">The operation.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Ternary(ShaderExpression a, ShaderExpression b, ShaderExpression c, ShaderOp op) => new(
        constant: Vector4.Zero,
        children: [a, b, c],
        kind: ShaderExpressionKind.Operation,
        op: op,
        operand: 0u
    );
    /// <summary>Creates a node rearranging this value's lanes.</summary>
    /// <param name="x">The source lane of the result's first lane.</param>
    /// <param name="y">The source lane of the result's second lane.</param>
    /// <param name="z">The source lane of the result's third lane.</param>
    /// <param name="w">The source lane of the result's fourth lane.</param>
    /// <returns>The node.</returns>
    public ShaderExpression Swizzle(int x, int y, int z, int w) => Unary(
        op: ShaderOp.Swizzle,
        operand: ShaderIsa.PackSwizzle(w: w, x: x, y: y, z: z),
        value: this
    );
    /// <summary>Creates a node replicating one of this value's lanes across every lane.</summary>
    /// <param name="lane">The source lane.</param>
    /// <returns>The node.</returns>
    public ShaderExpression Lane(int lane) => Swizzle(w: lane, x: lane, y: lane, z: lane);
    /// <summary>Creates a node assembling one four-lane value from the first lanes of four expressions.</summary>
    /// <param name="x">The value supplying the first lane.</param>
    /// <param name="y">The value supplying the second lane.</param>
    /// <param name="z">The value supplying the third lane.</param>
    /// <param name="w">The value supplying the fourth lane.</param>
    /// <returns>The node.</returns>
    public static ShaderExpression Combine(ShaderExpression x, ShaderExpression y, ShaderExpression? z = null, ShaderExpression? w = null) {
        var value = ((x.X * Constant(x: 1f, y: 0f)) + (y.X * Constant(x: 0f, y: 1f)));

        if (z is not null) {
            value += (z.X * Constant(x: 0f, y: 0f, z: 1f));
        }
        if (w is not null) {
            value += (w.X * Constant(w: 1f, x: 0f, y: 0f, z: 0f));
        }

        return value;
    }
}

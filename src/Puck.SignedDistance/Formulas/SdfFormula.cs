using System.Globalization;
using System.Text;

namespace Puck.SignedDistance;

// The closed expression language the field's shared formulas are written in. One formula lowers to two readers: the
// fixed-point C# the query evaluator runs (Puck.Maths carriers, one rounding per carrier operation, narrow widening
// primitives where a carrier-rounded intermediate would lose the answer) and the precise HLSL the kernels run. The
// node set is closed on purpose: every op names its lowering on both targets here, so a formula cannot reach a
// construct one reader lacks. This source is also compiled into the Roslyn generator (Puck.Analyzers), which lowers
// the C# reader at build time; Puck.Shaders.Generator lowers the HLSL reader from the same objects.
internal enum SdfFormulaType { Scalar, Vector2, Vector3, Condition }

internal interface ISdfFormulaType;
internal readonly struct SdfScalar : ISdfFormulaType;
internal readonly struct SdfVector2 : ISdfFormulaType;
internal readonly struct SdfVector3 : ISdfFormulaType;
internal readonly struct SdfCondition : ISdfFormulaType;

internal enum SdfFormulaOp {
    Name, Literal, Constant, Add, Subtract, Multiply, Divide, Negate, X, Y, Z,
    Vector2, Vector3, Abs, Min, Max, Clamp, Lerp, Length, Dot, SegmentParameter, MinLength,
    ScaledRatio, Power, FiniteOr, Ellipse2D, Equal, Less, LessEqual, Greater, GreaterEqual, And, Select, Call,
}

// An operand a catalogue formula reads from its instruction: the C# argument (a CompiledInstruction lane) and the
// HLSL argument (a data0/data1 swizzle). A null side means that reader does not take the operand; a formula that
// still reads it on that side fails to compile there.
internal readonly record struct SdfFormulaOperand<T>(string? Fixed, string? Shader) where T : struct, ISdfFormulaType;

internal interface ISdfFormulaValue {
    SdfFormulaNode Node { get; }
}

internal sealed record SdfFormulaNode(
    SdfFormulaOp Op, SdfFormulaType Type, string Text, SdfFormulaNode[] Inputs, SdfFormula? Function = null, string? Shader = null
);

internal readonly record struct SdfValue<T>(SdfFormulaNode Node) : ISdfFormulaValue where T : struct, ISdfFormulaType {
    internal static SdfFormulaType Kind { get; } = typeof(T) == typeof(SdfScalar) ? SdfFormulaType.Scalar
        : typeof(T) == typeof(SdfVector2) ? SdfFormulaType.Vector2
        : typeof(T) == typeof(SdfVector3) ? SdfFormulaType.Vector3
        : typeof(T) == typeof(SdfCondition) ? SdfFormulaType.Condition
        : throw new ArgumentOutOfRangeException(nameof(T));

    internal static SdfValue<T> Of(SdfFormulaOp op, params ISdfFormulaValue[] inputs) =>
        new(new SdfFormulaNode(op, Kind, "", inputs.Select(static input => input.Node).ToArray()));

    public static SdfValue<T> operator +(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Add, a, b);
    public static SdfValue<T> operator -(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Subtract, a, b);
    public static SdfValue<T> operator -(SdfValue<T> value) => Of(SdfFormulaOp.Negate, value);
    // Scalar arithmetic; a vector scales through SdfFieldFormulas.Scale, whose carriers both define vector * scalar.
    public static SdfValue<T> operator *(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Multiply, a, b);
    public static SdfValue<T> operator /(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Divide, a, b);
    public static SdfValue<SdfCondition> operator <(SdfValue<T> a, SdfValue<T> b) => SdfValue<SdfCondition>.Of(SdfFormulaOp.Less, a, b);
    public static SdfValue<SdfCondition> operator >(SdfValue<T> a, SdfValue<T> b) => SdfValue<SdfCondition>.Of(SdfFormulaOp.Greater, a, b);
    public static SdfValue<SdfCondition> operator <=(SdfValue<T> a, SdfValue<T> b) => SdfValue<SdfCondition>.Of(SdfFormulaOp.LessEqual, a, b);
    public static SdfValue<SdfCondition> operator >=(SdfValue<T> a, SdfValue<T> b) => SdfValue<SdfCondition>.Of(SdfFormulaOp.GreaterEqual, a, b);
    // Conditions only.
    public static SdfValue<T> operator &(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.And, a, b);
}

internal class SdfFormula {
    // The Q48.16 grid both readers' literals round to: C# takes the nearest raw (ties to even, as FixedQ4816.FromDouble
    // rounds), HLSL the nearest float to the same decimal.
    private const double FixedScale = 65536.0;

    private readonly List<Parameter> m_parameters = [];
    private readonly List<Step> m_steps = [];

    private sealed record Parameter(SdfFormulaType Type, string Name, string? Fixed, string? Shader, bool Bound);
    private sealed record Step(string? Name, SdfFormulaNode Value, SdfFormulaNode? When = null);

    protected SdfFormula(string name, SdfFormulaType result) {
        Name = name;
        Result = result;
    }

    // The HLSL function name; the C# method takes the same name with its first letter raised.
    internal string Name { get; }
    private SdfFormulaType Result { get; }

    private string NameIn(bool shader) => shader ? Name : char.ToUpperInvariant(Name[0]) + Name.Substring(1);

    private static SdfValue<T> Reference<T>(string name) where T : struct, ISdfFormulaType =>
        new(new SdfFormulaNode(SdfFormulaOp.Name, SdfValue<T>.Kind, name, []));

    // A catalogue formula's input, bound to an instruction operand.
    internal SdfValue<T> Input<T>(string name, SdfFormulaOperand<T> operand) where T : struct, ISdfFormulaType {
        m_parameters.Add(new Parameter(SdfValue<T>.Kind, name, operand.Fixed, operand.Shader, Bound: true));
        return Reference<T>(name);
    }

    // A helper formula's parameter, passed by its caller.
    internal SdfValue<T> ParameterOf<T>(string name) where T : struct, ISdfFormulaType {
        m_parameters.Add(new Parameter(SdfValue<T>.Kind, name, name, name, Bound: false));
        return Reference<T>(name);
    }

    internal SdfValue<T> Bind<T>(string name, SdfValue<T> value) where T : struct, ISdfFormulaType {
        m_steps.Add(new Step(name, value.Node));
        return Reference<T>(name);
    }

    protected void AddReturn(SdfFormulaNode value, SdfFormulaNode? condition) => m_steps.Add(new Step(null, value, condition));

    private IEnumerable<SdfFormulaNode> Nodes() => m_steps.SelectMany(static step =>
        step.When is { } condition ? Descendants(condition).Concat(Descendants(step.Value)) : Descendants(step.Value));

    private static IEnumerable<SdfFormulaNode> Descendants(SdfFormulaNode node) {
        yield return node;
        foreach (var child in node.Inputs.SelectMany(Descendants)) {
            yield return child;
        }
    }

    internal IEnumerable<SdfFormula> Dependencies() => Nodes()
        .Select(static node => node.Function).OfType<SdfFormula>().Distinct();

    // The fixed-point power reads the evaluator's exact power memo and charges the caller's work counter.
    private bool UsesPower => Nodes().Any(static node => node.Op == SdfFormulaOp.Power || node.Function?.UsesPower == true);

    private IEnumerable<Parameter> Parameters(bool shader) => m_parameters.Where(parameter =>
        (shader ? parameter.Shader : parameter.Fixed) is not null);

    // The call a catalogue dispatch makes: each operand's own argument text on that target.
    internal string Dispatch(bool shader) {
        var arguments = Parameters(shader).Select(parameter => (shader ? parameter.Shader : parameter.Fixed)!);
        if (!shader && UsesPower) { arguments = arguments.Concat(["ref powerWork"]); }
        return Call(NameIn(shader), arguments);
    }

    // Every formula with the helpers it calls, each once and after its dependencies.
    internal static string EmitAll(bool shader, params SdfFormula[] formulas) {
        var written = new HashSet<SdfFormula>();
        var output = new StringBuilder();
        foreach (var formula in formulas) { Write(formula); }
        return output.ToString().TrimEnd() + "\n";

        void Write(SdfFormula formula) {
            if (!written.Add(formula)) { return; }
            foreach (var dependency in formula.Dependencies()) { Write(dependency); }
            output.Append(formula.Emit(shader)).Append('\n');
        }
    }

    internal string Emit(bool shader) {
        var source = new StringBuilder();
        var arguments = Parameters(shader).Select(parameter => TypeName(parameter.Type, shader) + " " + parameter.Name);
        if (!shader && UsesPower) { arguments = arguments.Concat(["ref PowerWork powerWork"]); }
        var prefix = shader ? (Result == SdfFormulaType.Condition ? "" : "precise ") : "private static ";
        var indent = shader ? "" : "    ";
        source.Append(indent).Append(prefix).Append(TypeName(Result, shader)).Append(' ').Append(Call(NameIn(shader), arguments)).Append(" {\n");
        var hasMemo = false;
        foreach (var step in m_steps) {
            if (!shader && !hasMemo && Descendants(step.Value).Concat(step.When is { } when ? Descendants(when) : []).Any(static node => node.Op == SdfFormulaOp.Power)) {
                source.Append(indent).Append("    var memo = CurrentPowerMemo();\n");
                hasMemo = true;
            }
            source.Append(indent).Append("    ");
            if (step.When is { } condition) {
                source.Append("if (").Append(Render(condition, shader)).Append(") { return ");
            } else if (step.Name is { } name) {
                if (shader && step.Value.Type != SdfFormulaType.Condition) { source.Append("precise "); }
                source.Append(TypeName(step.Value.Type, shader)).Append(' ').Append(name).Append(" = ");
            } else {
                source.Append("return ");
            }
            source.Append(Render(step.Value, shader)).Append(step.When is null ? ";\n" : "; }\n");
        }
        return source.Append(indent).Append("}\n").ToString();
    }

    protected SdfFormulaNode Invoke(params ISdfFormulaValue[] arguments) {
        if (m_parameters.Any(static parameter => parameter.Bound)
            || !m_parameters.Select(static parameter => parameter.Type).SequenceEqual(arguments.Select(static argument => argument.Node.Type))) {
            throw new ArgumentException($"Incorrect arguments for field formula {Name}.", nameof(arguments));
        }
        return new SdfFormulaNode(SdfFormulaOp.Call, Result, Name, arguments.Select(static argument => argument.Node).ToArray(), this);
    }

    private static string Call(string name, IEnumerable<string> arguments) => name + "(" + string.Join(", ", arguments) + ")";

    private static string TypeName(SdfFormulaType type, bool shader) => type switch {
        SdfFormulaType.Scalar => shader ? "float" : "FixedQ4816",
        SdfFormulaType.Vector2 => shader ? "float2" : "FixedVector2",
        SdfFormulaType.Vector3 => shader ? "float3" : "FixedVector3",
        SdfFormulaType.Condition => "bool",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static string Render(SdfFormulaNode node, bool shader) {
        string At(int index) => Render(node.Inputs[index], shader);
        string Binary(string op) => $"({At(0)} {op} {At(1)})";
        string Arguments() => string.Join(", ", node.Inputs.Select(input => Render(input, shader)));
        // A scalar or vector intrinsic: HLSL's lower-case overload, or the C# carrier's static method of that name.
        string Math(string name) => shader
            ? $"{name.ToLowerInvariant()}({Arguments()})"
            : $"{TypeName(node.Inputs[0].Type, shader: false)}.{name}({Arguments()})";
        string Member(string name) => At(0) + "." + (shader ? name.ToLowerInvariant() : name);
        return node.Op switch {
            SdfFormulaOp.Name => node.Text,
            SdfFormulaOp.Literal => RenderLiteral(double.Parse(node.Text, CultureInfo.InvariantCulture), shader),
            SdfFormulaOp.Constant => shader ? node.Shader! : node.Text,
            SdfFormulaOp.Add => Binary("+"),
            SdfFormulaOp.Subtract => Binary("-"),
            SdfFormulaOp.Multiply => Binary("*"),
            SdfFormulaOp.Divide => Binary("/"),
            SdfFormulaOp.Negate => $"(-{At(0)})",
            SdfFormulaOp.X => Member("X"),
            SdfFormulaOp.Y => Member("Y"),
            SdfFormulaOp.Z => Member("Z"),
            SdfFormulaOp.Vector2 or SdfFormulaOp.Vector3 => (shader ? "" : "new ") + Call(TypeName(node.Type, shader), node.Inputs.Select(input => Render(input, shader))),
            SdfFormulaOp.Abs => Math("Abs"),
            SdfFormulaOp.Min => Math("Min"),
            SdfFormulaOp.Max => Math("Max"),
            SdfFormulaOp.Clamp => Math("Clamp"),
            SdfFormulaOp.Lerp => Math("Lerp"),
            SdfFormulaOp.Dot => Math("Dot"),
            SdfFormulaOp.Length => shader ? $"length({At(0)})" : Member("Length"),
            // The carriers divide the exact dot products once (FixedVector2/3.SegmentParameter); a zero segment is its
            // origin on both.
            SdfFormulaOp.SegmentParameter => shader
                ? $"(dot({At(1)}, {At(1)}) > 0.0 ? saturate(dot({At(0)}, {At(1)}) / dot({At(1)}, {At(1)})) : 0.0)"
                : Math("SegmentParameter"),
            // The smaller of two lengths: one root of the smaller square in float, both roots taken exactly in fixed
            // point, where a squared length saturates the carrier long before its root does.
            SdfFormulaOp.MinLength => shader
                ? $"sqrt(min(dot({At(0)}, {At(0)}), dot({At(1)}, {At(1)})))"
                : $"FixedQ4816.Min({At(0)}.Length, {At(1)}.Length)",
            // |p| · min(r) / r: the fixed carrier widens the product and rounds once; the shader multiplies by the
            // packed reciprocal radius.
            SdfFormulaOp.ScaledRatio => shader
                ? $"({At(0)} * ({At(1)} * {At(3)}))"
                : $"SuperellipsoidScaledRatio({At(0)}, {At(2)}, {At(1)})",
            SdfFormulaOp.Power => shader ? $"pow({At(0)}, {At(1)})" : $"memo.Pow({At(0)}, {At(1)}, ref powerWork)",
            // A float that overflowed to infinity or NaN takes the fallback; the fixed carrier saturates instead.
            SdfFormulaOp.FiniteOr => shader
                ? $"((asuint({At(0)}) & 0x7F800000u) == 0x7F800000u ? {At(1)} : {At(0)})"
                : At(0),
            // The ellipse's projection root is bisected by a loop the expression language does not carry, so its core
            // is a primitive of each reader: SdfFieldEvaluator.SdfEllipse2D and sdfEllipse2D (field/sdf-ellipse.hlsli).
            SdfFormulaOp.Ellipse2D => shader
                ? $"sdfEllipse2D({At(0)}, float2({At(1)}, {At(2)}))"
                : $"SdfEllipse2D({At(0)}, {At(1)}, {At(2)})",
            SdfFormulaOp.Equal => Binary("=="),
            SdfFormulaOp.Less => Binary("<"),
            SdfFormulaOp.LessEqual => Binary("<="),
            SdfFormulaOp.Greater => Binary(">"),
            SdfFormulaOp.GreaterEqual => Binary(">="),
            SdfFormulaOp.And => Binary("&&"),
            SdfFormulaOp.Select => $"({At(0)} ? {At(1)} : {At(2)})",
            SdfFormulaOp.Call => Call(node.Function!.NameIn(shader), node.Inputs.Select(input => Render(input, shader))
                .Concat(!shader && node.Function!.UsesPower ? ["ref powerWork"] : [])),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };
    }

    private static string RenderLiteral(double value, bool shader) {
        if (shader) {
            var text = value.ToString("R", CultureInfo.InvariantCulture);
            return (text.IndexOf('.') >= 0 || text.IndexOf('E') >= 0) ? text : text + ".0";
        }
        var raw = (long)Math.Round(value * FixedScale, MidpointRounding.ToEven);
        return raw switch {
            0L => "FixedQ4816.Zero",
            (long)FixedScale => "FixedQ4816.One",
            _ => "FixedQ4816.FromRawBits(" + raw.ToString(CultureInfo.InvariantCulture) + "L)",
        };
    }

    internal static SdfValue<SdfScalar> Literal(double value) {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > (1L << 46)) {
            throw new ArgumentOutOfRangeException(nameof(value), value, "A field formula literal must be finite and within the fixed-point carrier.");
        }
        return new(new SdfFormulaNode(SdfFormulaOp.Literal, SdfFormulaType.Scalar, value.ToString("R", CultureInfo.InvariantCulture), []));
    }

    // A named constant both readers already hold: its C# field and its generated HLSL define.
    internal static SdfValue<SdfScalar> Constant(string fixedName, string shaderName) =>
        new(new SdfFormulaNode(SdfFormulaOp.Constant, SdfFormulaType.Scalar, fixedName, [], Shader: shaderName));
}

internal sealed class SdfFormula<T>(string name) : SdfFormula(name, SdfValue<T>.Kind) where T : struct, ISdfFormulaType {
    internal void Return(SdfValue<T> value) => AddReturn(value.Node, condition: null);
    internal void ReturnIf(SdfValue<SdfCondition> condition, SdfValue<T> value) => AddReturn(value.Node, condition.Node);
    internal SdfValue<T> Call(params ISdfFormulaValue[] arguments) => new(Invoke(arguments));
}

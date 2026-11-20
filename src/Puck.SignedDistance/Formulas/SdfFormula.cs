using System.Globalization;
using System.Text;

namespace Puck.SignedDistance;

internal enum SdfFormulaType { Scalar, Vector2, Vector3, Condition }

internal interface ISdfFormulaType;
internal readonly struct SdfScalar : ISdfFormulaType;
internal readonly struct SdfVector2 : ISdfFormulaType;
internal readonly struct SdfVector3 : ISdfFormulaType;
internal readonly struct SdfCondition : ISdfFormulaType;

internal enum SdfFormulaOp {
    Name, Literal, Add, Subtract, Multiply, Divide, Negate, X, Y, Z,
    Vector2, Vector3, Abs, Min, Max, Length, Clamp, SegmentParameter,
    ScaledRatio, Power, FiniteOr, Equal, Less, LessEqual, And, Select, Call,
}

internal readonly record struct SdfFormulaOperand<T>(string? Fixed, string? Shader) where T : struct, ISdfFormulaType;

internal interface ISdfFormulaValue {
    SdfFormulaNode Node { get; }
}

internal sealed record SdfFormulaNode(
    SdfFormulaOp Op, SdfFormulaType Type, string Text, SdfFormulaNode[] Inputs, SdfFormula? Function = null
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
    public static SdfValue<T> operator *(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Multiply, a, b);
    public static SdfValue<T> operator /(SdfValue<T> a, SdfValue<T> b) => Of(SdfFormulaOp.Divide, a, b);
    public static SdfValue<T> operator -(SdfValue<T> value) => Of(SdfFormulaOp.Negate, value);
}

internal class SdfFormula {
    private readonly List<Parameter> m_parameters = [];
    private readonly List<Step> m_steps = [];

    private sealed record Parameter(SdfFormulaType Type, string Name, string? Fixed, string? Shader, bool Bound);
    private sealed record Step(string? Name, SdfFormulaNode Value, SdfFormulaNode? When = null);

    protected SdfFormula(string name, SdfFormulaType result) {
        Name = name;
        Result = result;
    }

    internal string Name { get; }
    private SdfFormulaType Result { get; }

    private static SdfValue<T> Reference<T>(string name) where T : struct, ISdfFormulaType =>
        new(new SdfFormulaNode(SdfFormulaOp.Name, SdfValue<T>.Kind, name, []));

    internal SdfValue<T> Input<T>(string name, SdfFormulaOperand<T> operand) where T : struct, ISdfFormulaType {
        m_parameters.Add(new Parameter(SdfValue<T>.Kind, name, operand.Fixed, operand.Shader, Bound: true));
        return Reference<T>(name);
    }

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

    private bool UsesPower => Nodes().Any(static node => node.Op == SdfFormulaOp.Power || node.Function?.UsesPower == true);

    private IEnumerable<Parameter> Parameters(bool shader) => m_parameters.Where(parameter =>
        (shader ? parameter.Shader : parameter.Fixed) is not null);

    internal string Dispatch(bool shader) {
        var arguments = Parameters(shader).Select(parameter => (shader ? parameter.Shader : parameter.Fixed)!);
        if (!shader && UsesPower) { arguments = arguments.Concat(["ref powerWork"]); }
        return Call(Name, arguments);
    }

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
        var prefix = shader ? (Result == SdfFormulaType.Condition ? "" : "precise ") : "    private static ";
        var indent = shader ? "" : "    ";
        source.Append(indent).Append(prefix).Append(TypeName(Result, shader)).Append(' ').Append(Call(Name, arguments)).Append(" {\n");
        var hasMemo = false;
        foreach (var step in m_steps) {
            if (!shader && !hasMemo && Descendants(step.Value).Any(static node => node.Op == SdfFormulaOp.Power)) {
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
        string Intrinsic(string name) => Call(name, node.Inputs.Select(input => Render(input, shader)));
        string Math(string name) => Intrinsic(shader ? name.ToLowerInvariant() : TypeName(node.Type, shader: false) + "." + name);
        string Member(string name) => At(0) + "." + (shader ? name.ToLowerInvariant() : name);
        return node.Op switch {
            SdfFormulaOp.Name => node.Text,
            SdfFormulaOp.Literal => shader ? node.Text : node.Text switch {
                "0.0" => "FixedQ4816.Zero",
                "1.0" => "FixedQ4816.One",
                _ => $"FixedQ4816.FromDouble({node.Text})",
            },
            SdfFormulaOp.Add => Binary("+"),
            SdfFormulaOp.Subtract => Binary("-"),
            SdfFormulaOp.Multiply => Binary("*"),
            SdfFormulaOp.Divide => Binary("/"),
            SdfFormulaOp.Negate => $"(-{At(0)})",
            SdfFormulaOp.X => Member("X"),
            SdfFormulaOp.Y => Member("Y"),
            SdfFormulaOp.Z => Member("Z"),
            SdfFormulaOp.Vector2 or SdfFormulaOp.Vector3 => Intrinsic((shader ? "" : "new ") + TypeName(node.Type, shader)),
            SdfFormulaOp.Abs => Math("Abs"),
            SdfFormulaOp.Min => Math("Min"),
            SdfFormulaOp.Max => Math("Max"),
            SdfFormulaOp.Length => shader ? Intrinsic("length") : Member("Length"),
            SdfFormulaOp.Clamp => Math("Clamp"),
            SdfFormulaOp.SegmentParameter => shader
                ? $"(dot({At(1)}, {At(1)}) > 0.0 ? saturate(dot({At(0)}, {At(1)}) / dot({At(1)}, {At(1)})) : 0.0)"
                : Intrinsic(TypeName(node.Inputs[0].Type, shader: false) + ".SegmentParameter"),
            SdfFormulaOp.ScaledRatio => shader
                ? $"({At(0)} * ({At(1)} * {At(3)}))"
                : $"SuperellipsoidScaledRatio({At(0)}, {At(2)}, {At(1)})",
            SdfFormulaOp.Power => shader ? Intrinsic("pow") : $"memo.Pow({At(0)}, {At(1)}, ref powerWork)",
            SdfFormulaOp.FiniteOr => shader
                ? $"((asuint({At(0)}) & 0x7F800000u) == 0x7F800000u ? {At(1)} : {At(0)})"
                : At(0),
            SdfFormulaOp.Equal => Binary("=="),
            SdfFormulaOp.Less => Binary("<"),
            SdfFormulaOp.LessEqual => Binary("<="),
            SdfFormulaOp.And => Binary("&&"),
            SdfFormulaOp.Select => $"({At(0)} ? {At(1)} : {At(2)})",
            SdfFormulaOp.Call => Call(node.Text, node.Inputs.Select(input => Render(input, shader))
                .Concat(!shader && node.Function!.UsesPower ? ["ref powerWork"] : [])),
            _ => throw new ArgumentOutOfRangeException(nameof(node)),
        };
    }

    internal static SdfValue<SdfScalar> Literal(double value) =>
        new(new SdfFormulaNode(SdfFormulaOp.Literal, SdfFormulaType.Scalar, value.ToString("0.0###############", CultureInfo.InvariantCulture), []));
}

internal sealed class SdfFormula<T>(string name) : SdfFormula(name, SdfValue<T>.Kind) where T : struct, ISdfFormulaType {
    internal void Return(SdfValue<T> value) => AddReturn(value.Node, condition: null);
    internal void ReturnIf(SdfValue<SdfCondition> condition, SdfValue<T> value) => AddReturn(value.Node, condition.Node);
    internal SdfValue<T> Call(params ISdfFormulaValue[] arguments) => new(Invoke(arguments));
}

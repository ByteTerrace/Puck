using System.Globalization;
using System.Text;

namespace Puck.SignedDistance;

// The closed expression language the field's shared formulas are written in. One formula lowers to two readers: the
// fixed-point C# the query evaluator runs (Puck.Maths carriers, one rounding per carrier operation, narrow widening
// primitives where a carrier-rounded intermediate would lose the answer) and the precise HLSL the kernels run. The
// node set is closed on purpose: every op names its lowering on both targets here, so a formula cannot reach a
// construct one reader lacks. This source is also compiled into the Roslyn generator (Puck.Analyzers), which lowers
// the C# reader at build time; Puck.Shaders.Generator lowers the HLSL reader from the same objects.
internal enum SdfFormulaType { Scalar, Vector2, Vector3, Condition, Index, Rotation }

internal interface ISdfFormulaType;
internal readonly struct SdfScalar : ISdfFormulaType;
internal readonly struct SdfVector2 : ISdfFormulaType;
internal readonly struct SdfVector3 : ISdfFormulaType;
internal readonly struct SdfCondition : ISdfFormulaType;
// An unsigned header lane (a seed, a mode, a count): passed to a primitive, never computed with.
internal readonly struct SdfIndex : ISdfFormulaType;
// A unit quaternion: a point is turned back through it, and nothing else reads it.
internal readonly struct SdfRotation : ISdfFormulaType;

internal enum SdfFormulaOp {
    Name, Literal, Constant, Add, Subtract, Multiply, Divide, Negate, X, Y, Z,
    Vector2, Vector3, Abs, Min, Max, Clamp, Lerp, Length, Dot, SegmentParameter, MinLength,
    ScaledRatio, Power, FiniteOr, Ellipse2D, Equal, Less, LessEqual, Greater, GreaterEqual, And, Select, Call,
    Round, Floor, MultiplyComponents, DivideComponents, RotateInverse, Smoothstep, Fbm3, CellDistance,
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
        : typeof(T) == typeof(SdfIndex) ? SdfFormulaType.Index
        : typeof(T) == typeof(SdfRotation) ? SdfFormulaType.Rotation
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

    internal SdfFormulaType ResultType => Result;

    // The walk state a formula reads on the fixed-point side: its operands whose argument is one of the given names
    // rather than an instruction lane, in parameter order.
    internal IEnumerable<(string Name, string Type)> FixedState(params string[] names) => Parameters(shader: false)
        .Where(parameter => names.Contains(parameter.Fixed!))
        .Select(static parameter => (parameter.Fixed!, TypeName(parameter.Type, shader: false)));

    // The call a catalogue dispatch makes: each operand's own argument text on that target, a walk-state operand
    // replaced by the name the calling reader holds that state under.
    internal string Dispatch(bool shader, params (string State, string Held)[] held) {
        var arguments = Parameters(shader).Select(parameter => {
            var text = (shader ? parameter.Shader : parameter.Fixed)!;
            foreach (var (state, name) in held) {
                if (text == state) { return name; }
            }
            return text;
        });
        if (!shader && !Widening && UsesPower) { arguments = arguments.Concat(["ref powerWork"]); }
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

    // === The wide reader ===============================================================================================
    // A third lowering, for presentation: the fixed-point reader's text over double-precision lanes, one lane a point
    // (SdfFieldEvaluator's Wide carriers, which name every operation as the fixed carriers do). The nodes whose lowering
    // is not that renaming say so in Render. A guarded return cannot branch per lane: each guard selects its value
    // into the lanes it holds and the formula returns once every lane is taken, so a guard on an instruction's own
    // operands, which every lane shares, still skips what it guards. A formula that answers a condition has no wide
    // form; only material winners do, and the wide reader reads distances alone.
    [ThreadStatic]
    private static bool Widening;

    internal bool Widens => (Result != SdfFormulaType.Condition);

    internal static string Widen(string text) => text
        .Replace("FixedQ4816", "Wide").Replace("FixedVector2", "Wide2").Replace("FixedVector3", "Wide3").Replace("FixedQuaternion", "WideRotation");

    internal string DispatchWide() {
        Widening = true;
        try { return Widen(Dispatch(shader: false)); } finally { Widening = false; }
    }

    internal static string EmitAllWide(params SdfFormula[] formulas) {
        var written = new HashSet<SdfFormula>();
        var output = new StringBuilder();
        foreach (var formula in formulas) { Write(formula); }
        return output.ToString().TrimEnd() + "\n";

        void Write(SdfFormula formula) {
            if (!formula.Widens || !written.Add(formula)) { return; }
            foreach (var dependency in formula.Dependencies()) { Write(dependency); }
            output.Append(formula.EmitWide()).Append('\n');
        }
    }

    private string EmitWide() {
        Widening = true;
        try {
            var source = new StringBuilder();
            var arguments = Parameters(shader: false).Select(static parameter => TypeName(parameter.Type, shader: false) + " " + parameter.Name);
            var guarded = m_steps.Any(static step => step.When is not null);
            if (guarded && (Result != SdfFormulaType.Scalar)) { throw new InvalidOperationException($"Field formula {Name} guards a return that is not a scalar."); }
            source.Append("    private static ").Append(TypeName(Result, shader: false)).Append(' ').Append(Call(NameIn(shader: false), arguments)).Append(" {\n");
            if (guarded) { source.Append("        var taken = WideMask.None;\n        var result = FixedQ4816.Zero;\n"); }
            foreach (var step in m_steps) {
                var value = Render(step.Value, shader: false);
                source.Append("        ");
                if (step.When is { } condition) {
                    source.Append("{ var when = (").Append(Render(condition, shader: false)).Append(").Without(taken); if (when.Any) { result = Wide.Select(when, ")
                        .Append(value).Append(", result); taken |= when; if (taken.All) { return result; } } }\n");
                } else if (step.Name is { } name) {
                    source.Append(TypeName(step.Value.Type, shader: false)).Append(' ').Append(name).Append(" = ").Append(value).Append(";\n");
                } else {
                    source.Append(guarded ? $"return Wide.Select(taken, result, {value});\n" : $"return {value};\n");
                }
            }
            return Widen(source.Append("    }\n").ToString());
        } finally { Widening = false; }
    }

    // === The dual reader ===============================================================================================
    // A fourth lowering, for gradients: each formula again as an overload of the same name whose walk-state inputs (the
    // point, the running and candidate distances) are duals, values carried with their derivatives along the sample's world
    // axes. It has two targets, as the plain reader does: the bake's gradient walk (SDF_FIELD_DUAL, field/sdf-dual.hlsli's
    // SdfDual carriers and sdfd functions) and the query evaluator's (Puck.Maths' FixedGradient carriers and FixedGradients'
    // functions of the same names, which the contact gradient reads in one walk). Activity is forward: a node is active when
    // it reads an active input, except where its derivative is zero everywhere it is defined (a rounding, a comparison),
    // and an active node lowers to the dual library's function of its operation while an inactive one keeps the plain
    // reader's text. A helper is emitted once for each pattern of active arguments its callers pass, as an overload. A node
    // the fixed-point target has no dual of (a loop the kernels carry as a primitive: the noises, the ellipse's root, the
    // power) leaves the formula without a fixed-point dual, and its readers there take the stencil.
    private static readonly string[] DualStates = ["p", "current", "candidate", "distance"];

    // A formula whose fixed-point dual would need a node that target has no dual of.
    private sealed class DualUncovered(SdfFormulaOp op) : Exception($"Field formula operation {op} has no fixed-point dual.");

    internal static string EmitAllDual(params SdfFormula[] formulas) => EmitAllDual(covered: out _, formulas: formulas, shader: true);

    // The fixed-point duals of the formulas that have one, and which they are.
    internal static string EmitAllFixedDual(out HashSet<SdfFormula> covered, params SdfFormula[] formulas) => EmitAllDual(covered: out covered, formulas: formulas, shader: false);

    private static string EmitAllDual(bool shader, out HashSet<SdfFormula> covered, SdfFormula[] formulas) {
        var written = new HashSet<string>();
        var output = new StringBuilder();

        covered = [];
        foreach (var formula in formulas) {
            if (formula.Result == SdfFormulaType.Condition) { continue; }
            var mask = formula.Parameters(shader: shader).Select(parameter => parameter.Bound && DualStates.Contains(shader ? parameter.Shader : parameter.Fixed)).ToArray();
            if (!mask.Any(static active => active) || !formula.ResultActive(mask: mask, shader: shader)) { continue; }
            try {
                formula.EmitDual(mask: mask, output: output, shader: shader, written: written);
                _ = covered.Add(formula);
            } catch (DualUncovered) when (!shader) { }
        }
        return output.ToString().TrimEnd() + "\n";
    }

    private static bool Differentiable(SdfFormulaType type) =>
        type is SdfFormulaType.Scalar or SdfFormulaType.Vector2 or SdfFormulaType.Vector3;

    private static string DualTypeName(SdfFormulaType type, bool shader) => type switch {
        SdfFormulaType.Scalar => shader ? "SdfDual" : "FixedGradient",
        SdfFormulaType.Vector2 => shader ? "SdfDual2" : "FixedGradient2",
        SdfFormulaType.Vector3 => shader ? "SdfDual3" : "FixedGradient3",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // A dual library function by its kernel name (sdfdAdd), on the fixed-point target FixedGradients' method of the rest
    // of that name (FixedGradients.Add).
    private static string DualFunction(string name, bool shader) => shader ? name : "FixedGradients." + name.Substring(4);

    // Which named values a pattern of active parameters makes active, step by step.
    private Dictionary<string, bool> DualNames(bool[] mask, bool shader) {
        var names = new Dictionary<string, bool>();
        var parameters = Parameters(shader: shader).ToArray();
        for (var index = 0; index < parameters.Length; index++) { names[parameters[index].Name] = mask[index] && Differentiable(parameters[index].Type); }
        foreach (var step in m_steps) {
            if (step.Name is { } name) { names[name] = Active(step.Value, names); }
        }
        return names;
    }

    private bool ResultActive(bool[] mask, bool shader) {
        var names = DualNames(mask: mask, shader: shader);
        return m_steps.Any(step => step.Name is null && Active(step.Value, names));
    }

    private static bool Active(SdfFormulaNode node, Dictionary<string, bool> names) => node.Op switch {
        SdfFormulaOp.Name => names.TryGetValue(node.Text, out var active) && active,
        SdfFormulaOp.Literal or SdfFormulaOp.Constant or SdfFormulaOp.Round or SdfFormulaOp.Floor => false,
        SdfFormulaOp.Equal or SdfFormulaOp.Less or SdfFormulaOp.LessEqual or SdfFormulaOp.Greater or SdfFormulaOp.GreaterEqual or SdfFormulaOp.And => false,
        // The call's arguments are named by the callee's parameters, which are the same on both targets.
        SdfFormulaOp.Call => node.Function!.ResultActive(mask: node.Inputs.Select(input => Active(input, names)).ToArray(), shader: true),
        _ => node.Inputs.Any(input => Active(input, names)),
    };

    private void EmitDual(bool[] mask, HashSet<string> written, StringBuilder output, bool shader) {
        var key = Name + "/" + string.Concat(mask.Select(static active => active ? '1' : '0'));
        if (!written.Add(key)) { return; }
        try {
            var names = DualNames(mask: mask, shader: shader);
            var body = new StringBuilder();
            var parameters = Parameters(shader: shader).ToArray();
            var arguments = parameters.Select((parameter, index) => (names[parameter.Name] ? DualTypeName(parameter.Type, shader) : TypeName(parameter.Type, shader)) + " " + parameter.Name);
            var indent = shader ? "" : "    ";
            if (!shader && UsesPower) { arguments = arguments.Concat(["ref PowerWork powerWork"]); }
            body.Append(indent).Append(shader ? "" : "private static ").Append(DualTypeName(Result, shader)).Append(' ').Append(Call(NameIn(shader), arguments)).Append(" {\n");
            if (!shader && UsesPower) { body.Append(indent).Append("    var memo = CurrentPowerMemo();\n"); }
            foreach (var step in m_steps) {
                body.Append(indent).Append("    ");
                if (step.When is { } condition) {
                    body.Append("if (").Append(DualValue(condition, names, written, output, shader)).Append(") { return ").Append(DualLift(step.Value, names, written, output, shader)).Append("; }\n");
                } else if (step.Name is { } name) {
                    var active = names[name];
                    body.Append(active ? DualTypeName(step.Value.Type, shader) : TypeName(step.Value.Type, shader)).Append(' ').Append(name).Append(" = ")
                        .Append(active ? Dual(step.Value, names, written, output, shader) : DualValue(step.Value, names, written, output, shader)).Append(";\n");
                } else {
                    body.Append("return ").Append(DualLift(step.Value, names, written, output, shader)).Append(";\n");
                }
            }
            output.Append(body.Append(indent).Append("}\n")).Append('\n');
        } catch (DualUncovered) {
            _ = written.Remove(key);
            throw;
        }
    }

    // The plain value of a node: an active node's dual value, else the plain reader's text over its inputs' values.
    private static string DualValue(SdfFormulaNode node, Dictionary<string, bool> names, HashSet<string> written, StringBuilder output, bool shader) =>
        Active(node, names)
            ? Dual(node, names, written, output, shader) + (shader ? ".v" : ".Value")
            : Render(node, shader, input => DualValue(input, names, written, output, shader));

    // A node as a dual whether active or not: an inactive value lifts to a constant.
    private static string DualLift(SdfFormulaNode node, Dictionary<string, bool> names, HashSet<string> written, StringBuilder output, bool shader) =>
        Active(node, names) ? Dual(node, names, written, output, shader) : $"{DualFunction("sdfdLift", shader)}({DualValue(node, names, written, output, shader)})";

    // An active node as the dual library's call; an inactive operand passes its plain value to the mixed overloads.
    private static string Dual(SdfFormulaNode node, Dictionary<string, bool> names, HashSet<string> written, StringBuilder output, bool shader) {
        string Operand(int index) => Active(node.Inputs[index], names) ? Dual(node.Inputs[index], names, written, output, shader) : DualValue(node.Inputs[index], names, written, output, shader);
        string Lifted(int index) => DualLift(node.Inputs[index], names, written, output, shader);
        string Value(int index) => DualValue(node.Inputs[index], names, written, output, shader);
        bool ActiveAt(int index) => Active(node.Inputs[index], names);
        string Constants(int from) {
            for (var index = from; index < node.Inputs.Length; index++) {
                if (ActiveAt(index)) { throw new InvalidOperationException($"Field formula operation {node.Op} has no derivative through operand {index}."); }
            }
            return string.Concat(Enumerable.Range(from, node.Inputs.Length - from).Select(index => ", " + Value(index)));
        }
        string Function(string name, params string[] operands) => DualFunction(name, shader) + "(" + string.Join(", ", operands) + ")";
        // A primitive of each reader whose dual only the kernels carry.
        string KernelOnly(string text) => shader ? text : throw new DualUncovered(node.Op);
        return node.Op switch {
            SdfFormulaOp.Name => node.Text,
            SdfFormulaOp.Add => Function("sdfdAdd", Operand(0), Operand(1)),
            SdfFormulaOp.Subtract => Function("sdfdSubtract", Operand(0), Operand(1)),
            SdfFormulaOp.Multiply => Function("sdfdMultiply", Operand(0), Operand(1)),
            SdfFormulaOp.Divide => Function("sdfdDivide", Operand(0), Operand(1)),
            SdfFormulaOp.MultiplyComponents => Function("sdfdMultiplyComponents", Operand(0), Operand(1)),
            SdfFormulaOp.DivideComponents => Function("sdfdDivideComponents", Operand(0), Operand(1)),
            SdfFormulaOp.Min => Function("sdfdMin", Operand(0), Operand(1)),
            SdfFormulaOp.Max => Function("sdfdMax", Operand(0), Operand(1)),
            SdfFormulaOp.Dot => Function("sdfdDot", Operand(0), Operand(1)),
            SdfFormulaOp.Negate => Function("sdfdNegate", Operand(0)),
            SdfFormulaOp.X => Function("sdfdX", Operand(0)),
            SdfFormulaOp.Y => Function("sdfdY", Operand(0)),
            SdfFormulaOp.Z => Function("sdfdZ", Operand(0)),
            SdfFormulaOp.Vector2 => Function("sdfdVec2", Lifted(0), Lifted(1)),
            SdfFormulaOp.Vector3 => Function("sdfdVec3", Lifted(0), Lifted(1), Lifted(2)),
            SdfFormulaOp.Abs => Function("sdfdAbs", Operand(0)),
            SdfFormulaOp.Length => Function("sdfdLength", Operand(0)),
            SdfFormulaOp.Clamp => (ActiveAt(1) || ActiveAt(2)) ? Function("sdfdClamp", Lifted(0), Lifted(1), Lifted(2)) : Function("sdfdClamp", Lifted(0), Value(1), Value(2)),
            SdfFormulaOp.Lerp => Function("sdfdLerp", Lifted(0), Lifted(1), Lifted(2)),
            SdfFormulaOp.SegmentParameter => ActiveAt(1) ? Function("sdfdSegmentParameter", Lifted(0), Lifted(1)) : Function("sdfdSegmentParameter", Lifted(0), Value(1)),
            SdfFormulaOp.MinLength => Function("sdfdMinLength", Lifted(0), Lifted(1)),
            // The shader's |p| · (min(r) · 1/r).
            SdfFormulaOp.ScaledRatio => KernelOnly(Function("sdfdMultiply", Operand(0), (ActiveAt(1) || ActiveAt(3)) ? Function("sdfdMultiply", Operand(1), Operand(3)) : $"({Value(1)} * {Value(3)})")),
            SdfFormulaOp.Power => KernelOnly(ActiveAt(1) ? Function("sdfdPower", Lifted(0), Lifted(1)) : Function("sdfdPower", Lifted(0), Value(1))),
            SdfFormulaOp.FiniteOr => Function("sdfdFiniteOr", Lifted(0), Lifted(1)),
            SdfFormulaOp.Select => Function("sdfdSelect", Value(0), Lifted(1), Lifted(2)),
            SdfFormulaOp.Smoothstep => ActiveAt(0) || ActiveAt(1)
                ? throw new InvalidOperationException("A field formula's smoothstep edges have no derivative.")
                : Function("sdfdSmoothstep", Value(0), Value(1), Lifted(2)),
            SdfFormulaOp.RotateInverse => DualFunction("sdfdRotateInverse", shader) + "(" + Lifted(0) + Constants(1) + ")",
            SdfFormulaOp.Ellipse2D => KernelOnly("sdfEllipse2D(" + Lifted(0) + ", float2(" + Constants(1).Substring(2) + "))"),
            SdfFormulaOp.Fbm3 => KernelOnly("sdfFbm3(" + Lifted(0) + Constants(1) + ")"),
            SdfFormulaOp.CellDistance => KernelOnly("sdfCellDistance(" + Lifted(0) + Constants(1) + ")"),
            SdfFormulaOp.Call => DualCall(node, names, written, output, shader),
            _ => throw new InvalidOperationException($"Field formula operation {node.Op} has no dual lowering."),
        };
    }

    private static string DualCall(SdfFormulaNode node, Dictionary<string, bool> names, HashSet<string> written, StringBuilder output, bool shader) {
        var mask = node.Inputs.Select(input => Active(input, names)).ToArray();
        var function = node.Function!;
        function.EmitDual(mask: mask, output: output, shader: shader, written: written);
        return Call(function.NameIn(shader), node.Inputs.Select((input, index) =>
            mask[index] ? Dual(input, names, written, output, shader) : DualValue(input, names, written, output, shader))
            .Concat(!shader && function.UsesPower ? ["ref powerWork"] : []));
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
        SdfFormulaType.Condition => Widening ? "WideMask" : "bool",
        SdfFormulaType.Index => "uint",
        SdfFormulaType.Rotation => shader ? "float4" : "FixedQuaternion",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    // inputs renders each operand; by default the same reader's text, the dual reader's plain values otherwise.
    private static string Render(SdfFormulaNode node, bool shader, Func<SdfFormulaNode, string>? inputs = null) {
        var operand = inputs ?? (input => Render(input, shader));
        string At(int index) => operand(node.Inputs[index]);
        string Binary(string op) => $"({At(0)} {op} {At(1)})";
        string Arguments() => string.Join(", ", node.Inputs.Select(operand));
        // A scalar or vector intrinsic: HLSL's lower-case overload, or the C# carrier's static method of that name.
        string Math(string name) => shader
            ? $"{name.ToLowerInvariant()}({Arguments()})"
            : $"{TypeName(node.Inputs[0].Type, shader: false)}.{name}({Arguments()})";
        string Member(string name) => At(0) + "." + (shader ? name.ToLowerInvariant() : name);
        return node.Op switch {
            SdfFormulaOp.Name => node.Text,
            SdfFormulaOp.Literal => RenderLiteral(double.Parse(node.Text, CultureInfo.InvariantCulture), shader),
            SdfFormulaOp.Constant => shader ? node.Shader! : Widening ? $"((double){node.Text})" : node.Text,
            SdfFormulaOp.Add => Binary("+"),
            SdfFormulaOp.Subtract => Binary("-"),
            SdfFormulaOp.Multiply => Binary("*"),
            SdfFormulaOp.Divide => Binary("/"),
            SdfFormulaOp.Negate => $"(-{At(0)})",
            SdfFormulaOp.X => Member("X"),
            SdfFormulaOp.Y => Member("Y"),
            SdfFormulaOp.Z => Member("Z"),
            SdfFormulaOp.Vector2 or SdfFormulaOp.Vector3 => (shader ? "" : "new ") + Call(TypeName(node.Type, shader), node.Inputs.Select(operand)),
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
            SdfFormulaOp.Power => shader ? $"pow({At(0)}, {At(1)})" : Widening ? $"Wide.Pow({At(0)}, {At(1)})" : $"memo.Pow({At(0)}, {At(1)}, ref powerWork)",
            // A float that overflowed to infinity or NaN takes the fallback; the fixed carrier saturates instead.
            SdfFormulaOp.FiniteOr => shader
                ? $"((asuint({At(0)}) & 0x7F800000u) == 0x7F800000u ? {At(1)} : {At(0)})"
                : At(0),
            // The ellipse's projection root is bisected by a loop the expression language does not carry, so its core
            // is a primitive of each reader: SdfFieldEvaluator.SdfEllipse2D and sdfEllipse2D (field/sdf-ellipse.hlsli).
            SdfFormulaOp.Ellipse2D => shader
                ? $"sdfEllipse2D({At(0)}, float2({At(1)}, {At(2)}))"
                : $"SdfEllipse2D({At(0)}, {At(1)}, {At(2)})",
            SdfFormulaOp.Round => Math("Round"),
            SdfFormulaOp.Floor => Math("Floor"),
            // Component by component: HLSL's operators, the fixed vector's named methods.
            SdfFormulaOp.MultiplyComponents => shader ? Binary("*") : Math("Multiply"),
            SdfFormulaOp.DivideComponents => shader ? Binary("/") : Math("Divide"),
            SdfFormulaOp.RotateInverse => shader
                ? $"rotatePointByInverseQuaternion({At(0)}, {At(1)})"
                : $"{At(1)}.RotateInverse({At(0)})",
            SdfFormulaOp.Smoothstep => shader ? $"smoothstep({Arguments()})" : $"FixedQ4816.Smoothstep({Arguments()})",
            // The noises loop over octaves and cells, which the expression language does not carry, so each is a
            // primitive of each reader: Pcg3dLatticeNoise.Fbm3 and sdfFbm3, SdfFieldEvaluator.SampleCells and
            // sdfCellDistance (field/sdf-noise.hlsli).
            SdfFormulaOp.Fbm3 => shader
                ? $"sdfFbm3({Arguments()})"
                : Widening ? $"WideFbm3({Arguments()})"
                : $"Pcg3dLatticeNoise.Fbm3(q: {At(0)}, seed: {At(1)}, octaves: ((int){At(2)}), gain: {At(3)}, lacunarity: {At(4)})",
            SdfFormulaOp.CellDistance => shader
                ? $"sdfCellDistance({Arguments()})"
                : $"SampleCells({At(0)}, {At(1)}, ((SdfCellMode){At(2)}), {At(3)})",
            SdfFormulaOp.Equal => Binary("=="),
            SdfFormulaOp.Less => Binary("<"),
            SdfFormulaOp.LessEqual => Binary("<="),
            SdfFormulaOp.Greater => Binary(">"),
            SdfFormulaOp.GreaterEqual => Binary(">="),
            SdfFormulaOp.And => Binary(Widening ? "&" : "&&"),
            SdfFormulaOp.Select => Widening ? $"Wide.Select({At(0)}, {At(1)}, {At(2)})" : $"({At(0)} ? {At(1)} : {At(2)})",
            SdfFormulaOp.Call => Call(node.Function!.NameIn(shader), node.Inputs.Select(operand)
                .Concat(!shader && !Widening && node.Function!.UsesPower ? ["ref powerWork"] : [])),
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

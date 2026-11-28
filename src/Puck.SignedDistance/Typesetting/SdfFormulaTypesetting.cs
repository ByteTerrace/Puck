using System.Globalization;

namespace Puck.SignedDistance;

/// <summary>
/// One field formula in its readable form: the same formula tree the query evaluator, the kernels, the wide, dual and
/// affine readers lower (<c>SdfFieldFormulas</c>), lowered once more to math markup (<see cref="Puck.Text.MathMarkup"/>),
/// so what a console shows is what the engine computes and cannot drift from it.
/// </summary>
/// <param name="Reference">The reference that names it: <c>shape:</c> or <c>blend:</c> and the catalogue entry's
/// name (<c>shape:Sphere</c>, <c>blend:SmoothUnion</c>).</param>
/// <param name="Name">The formula's own name, as its helpers are named in the steps (<c>sphere</c>,
/// <c>smoothUnion</c>).</param>
/// <param name="Equation">The formula as one or a few lines of markup, each <c>d = …</c>, its local bindings written in
/// place and its helpers called by name; a guarded return carries its condition after it.</param>
/// <param name="Steps">The step by step, a line each: the formula's own bindings and returns in evaluation order (none for
/// a formula that binds nothing, whose equation is its one step), then
/// every helper it calls (transitively, each once, after its first caller) as a heading line and its own steps one
/// level deeper.</param>
/// <param name="Terms">The formula's named parameters (the instruction operands it reads), in declaration order. Each
/// appears in the markup as <c>\term{name}{symbol}</c>, so a layout can show the term's live value or highlight
/// it.</param>
public sealed record SdfTypesetFormula(
    string Reference,
    string Name,
    IReadOnlyList<string> Equation,
    IReadOnlyList<SdfTypesetStep> Steps,
    IReadOnlyList<SdfTypesetTerm> Terms
) {
    /// <summary>Returns every line of the formula's markup: its equation, then its steps, each step indented by its
    /// depth.</summary>
    /// <returns>The lines, in order.</returns>
    public IEnumerable<string> Lines() => Equation.Concat(Steps.Select(static step => (string.Concat(Enumerable.Repeat(element: "\\quad ", count: step.Depth)) + step.Markup)));
}

/// <summary>One line of a typeset formula's step by step.</summary>
/// <param name="Depth">The nesting depth: zero for the formula's own steps and helper headings, one for a helper's
/// steps.</param>
/// <param name="Markup">The line's math markup.</param>
public sealed record SdfTypesetStep(int Depth, string Markup);

/// <summary>One named parameter of a typeset formula.</summary>
/// <param name="Name">The parameter's name in the formula (<c>radius</c>, <c>smoothRadius</c>), the name
/// <c>\term</c> carries.</param>
/// <param name="Symbol">The markup the term draws as (<c>r</c>, <c>k_{0}</c>).</param>
/// <param name="Kind">What the parameter holds: <c>scalar</c>, <c>vector2</c>, <c>vector3</c>, <c>rotation</c> or
/// <c>index</c>.</param>
public sealed record SdfTypesetTerm(string Name, string Symbol, string Kind);

/// <summary>
/// The readable lowering of the field's shared formulas: each primitive's and blend's formula tree, lowered to math
/// markup with fractions, roots, absolute values, norms, sub- and superscripts and named terms, the formula's helpers
/// expanded into a step by step. Every shape and blend whose catalogue entry names a shared formula has one, so an
/// author's own shapes are typeset as the engine's are.
/// </summary>
/// <remarks>Presentation only: the markup describes the arithmetic the fixed-point and kernel readers run, operand for
/// operand, without the carriers' rounding. The three nodes that stand for a loop the expression language does not
/// carry (the ellipse's root, the noises, the swept strand's search) are written as named functions.</remarks>
public static class SdfFormulaTypesetting {
    private const string ResultSymbol = "d";
    private const string ShapePrefix = "shape:";
    private const string BlendPrefix = "blend:";

    // The symbols the shared formulas' named operands draw as; an operand this table does not name draws as its own
    // name.
    private static readonly Dictionary<string, string> Symbols = new(comparer: StringComparer.Ordinal) {
        ["p"] = "p",
        ["radius"] = "r",
        ["halfExtents"] = "b",
        ["cornerRadius"] = "c",
        ["endpoint"] = "e",
        ["major"] = "R",
        ["minor"] = "r",
        ["halfHeight"] = "h",
        ["rounding"] = "r_{e}",
        ["normal"] = "n",
        ["offset"] = "o",
        ["current"] = "a",
        ["candidate"] = "b",
        ["smoothRadius"] = "k_{0}",
        ["steps"] = "n",
        ["exponent"] = "e",
        ["radii"] = "r",
        ["liftAmount"] = "l",
        ["lift"] = "m",
    };
    // The named constants the formulas read, drawn as their meaning.
    private static readonly Dictionary<string, string> Constants = new(comparer: StringComparer.Ordinal) {
        ["SqrtHalf"] = "\\frac{1}{\\sqrt{2}}",
        ["SmoothRadiusMin"] = "k_{min}",
    };

    private static readonly Lazy<IReadOnlyList<SdfTypesetFormula>> Catalogue = new(valueFactory: Build);

    /// <summary>Gets every typeset formula: each shape's, then each blend's, in encoding order.</summary>
    public static IReadOnlyList<SdfTypesetFormula> All => Catalogue.Value;

    /// <summary>Returns the typeset formula a reference names.</summary>
    /// <param name="reference">The reference: <c>shape:</c> or <c>blend:</c> and an entry's name, compared
    /// ordinally, ignoring case.</param>
    /// <param name="formula">The typeset formula, or <see langword="null"/> when none answers.</param>
    /// <returns><see langword="true"/> when the reference names a shape or blend with a shared formula.</returns>
    public static bool TryFind(string? reference, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SdfTypesetFormula? formula) {
        formula = null;

        if (string.IsNullOrWhiteSpace(value: reference)) {
            return false;
        }

        var trimmed = reference.Trim();

        foreach (var candidate in Catalogue.Value) {
            if (string.Equals(a: candidate.Reference, b: trimmed, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                formula = candidate;

                return true;
            }
        }

        return false;
    }
    /// <summary>Returns the typeset formula of a primitive shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <returns>Its typeset formula, or <see langword="null"/> for a shape whose readers are hand-written (a side table,
    /// a texture or a loop).</returns>
    public static SdfTypesetFormula? Of(SdfShapeType shape) => (TryFind(formula: out var formula, reference: (ShapePrefix + shape)) ? formula : null);
    /// <summary>Returns the typeset formula of a blend.</summary>
    /// <param name="blend">The blend.</param>
    /// <returns>Its typeset formula.</returns>
    public static SdfTypesetFormula? Of(SdfBlendOp blend) => (TryFind(formula: out var formula, reference: (BlendPrefix + blend)) ? formula : null);

    private static IReadOnlyList<SdfTypesetFormula> Build() {
        var formulas = new List<SdfTypesetFormula>();

        foreach (var (value, facet, formula) in SdfFieldShapes.FormulaUses()) {
            if (facet == "Formula") { formulas.Add(item: Typeset(formula: formula, reference: (ShapePrefix + value))); }
        }
        foreach (var (value, facet, formula) in SdfFieldBlends.FormulaUses()) {
            if (facet == "Formula") { formulas.Add(item: Typeset(formula: formula, reference: (BlendPrefix + value))); }
        }

        return formulas;
    }
    private static SdfTypesetFormula Typeset(SdfFormula formula, string reference) {
        var inputs = formula.DeclaredInputs.Where(static input => (input.Bound && input.EveryReader)).ToArray();
        var terms = inputs.Select(static input => new SdfTypesetTerm(Kind: KindOf(type: input.Type), Name: input.Name, Symbol: SymbolOf(name: input.Name))).ToArray();
        var termNames = new HashSet<string>(collection: terms.Select(static term => term.Name), comparer: StringComparer.Ordinal);
        var steps = new List<SdfTypesetStep>();
        var written = new HashSet<SdfFormula>();
        var equation = new List<string>();
        var binds = new Dictionary<string, SdfFormulaNode>(comparer: StringComparer.Ordinal);

        foreach (var (name, value, when) in formula.DeclaredSteps) {
            var writer = new Writer(terms: termNames, substitutions: binds);

            if (name is not null) {
                binds[name] = value;
                continue;
            }

            equation.Add(item: Returned(result: ResultSymbol, value: writer.Write(node: value), when: ((when is null) ? null : writer.Write(node: when))));
        }

        // A formula that binds nothing is its own equation; its step by step is only its helpers'.
        if (binds.Count > 0) {
            WriteSteps(depth: 0, formula: formula, result: ResultSymbol, steps: steps, terms: termNames);
        }
        WriteHelpers(formula: formula, steps: steps, written: written);

        return new SdfTypesetFormula(Equation: equation, Name: DisplayName(formula: formula), Reference: reference, Steps: steps, Terms: terms);
    }
    // A formula's own steps, each binding as "name = value" and each return as "result = value", a guarded one with its
    // condition after it.
    private static void WriteSteps(SdfFormula formula, string result, int depth, List<SdfTypesetStep> steps, HashSet<string> terms) {
        var writer = new Writer(terms: terms, substitutions: null);

        foreach (var (name, value, when) in formula.DeclaredSteps) {
            steps.Add(item: new SdfTypesetStep(Depth: depth, Markup: ((name is not null)
                ? $"{LocalSymbol(name: name)} = {writer.Write(node: value)}"
                : Returned(result: result, value: writer.Write(node: value), when: ((when is null) ? null : writer.Write(node: when))))));
        }
    }
    // Every helper a formula calls, each once, after the steps of its first caller: a heading naming its parameters, then
    // its own steps a level deeper, then the helpers it calls.
    private static void WriteHelpers(SdfFormula formula, List<SdfTypesetStep> steps, HashSet<SdfFormula> written) {
        foreach (var helper in formula.Dependencies()) {
            if (!written.Add(item: helper)) { continue; }

            var parameters = helper.DeclaredInputs.Select(static input => input.Name).ToArray();
            var signature = $"{DisplayName(formula: helper)}({string.Join(separator: ", ", values: parameters.Select(selector: LocalSymbol))})";

            steps.Add(item: new SdfTypesetStep(Depth: 0, Markup: $"{signature}:"));
            WriteSteps(depth: 1, formula: helper, result: signature, steps: steps, terms: []);
            WriteHelpers(formula: helper, steps: steps, written: written);
        }
    }
    private static string Returned(string result, string value, string? when) => ((when is null)
        ? $"{result} = {value}"
        : $"{result} = {value}\\quad if {when}");
    private static string KindOf(SdfFormulaType type) => type switch {
        SdfFormulaType.Vector2 => "vector2",
        SdfFormulaType.Vector3 => "vector3",
        SdfFormulaType.Rotation => "rotation",
        SdfFormulaType.Index => "index",
        _ => "scalar",
    };
    private static string SymbolOf(string name) => (Symbols.TryGetValue(key: name, value: out var symbol) ? symbol : LocalSymbol(name: name));
    // A local name: trailing digits become a subscript (k1 is k₁), anything else draws as written.
    private static string LocalSymbol(string name) {
        var digits = name.Length;

        while ((digits > 0) && char.IsAsciiDigit(c: name[(digits - 1)])) { digits--; }

        return (((digits > 0) && (digits < name.Length)) ? $"{name[..digits]}_{{{name[digits..]}}}" : name);
    }
    // A formula's name as its readers' function name less the kernels' prefix: sdfBlendSmoothMinimum is smoothMinimum.
    private static string DisplayName(SdfFormula formula) {
        var name = formula.Name;

        foreach (var prefix in new[] { "sdfBlend", "sdf" }) {
            if (name.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal) && (name.Length > prefix.Length)) {
                name = name[prefix.Length..];
                break;
            }
        }

        return (char.ToLowerInvariant(c: name[0]) + name[1..]);
    }
    private static string Literal(double value) {
        if (value == Math.Round(a: value)) {
            return Signed(text: ((long)value).ToString(provider: CultureInfo.InvariantCulture), negative: false);
        }

        var magnitude = Math.Abs(value: value);

        foreach (var denominator in new[] { 2, 3, 4, 6, 8, 16 }) {
            var numerator = (magnitude * denominator);

            if (numerator == Math.Round(a: numerator)) {
                return Signed(negative: (value < 0d), text: $"\\frac{{{((long)numerator).ToString(provider: CultureInfo.InvariantCulture)}}}{{{denominator.ToString(provider: CultureInfo.InvariantCulture)}}}");
            }
        }

        var exponent = Math.Log2(x: magnitude);

        if ((exponent == Math.Round(a: exponent)) && (exponent < 0d)) {
            return Signed(negative: (value < 0d), text: $"2^{{−{(-(long)exponent).ToString(provider: CultureInfo.InvariantCulture)}}}");
        }

        return value.ToString(format: "G6", provider: CultureInfo.InvariantCulture).Replace(oldChar: '-', newChar: '−');

        static string Signed(string text, bool negative) => ((negative ? "−" : "") + text.Replace(oldChar: '-', newChar: '−'));
    }

    // Writes one node's markup with the parentheses its precedence needs. A component context (Axis) writes the
    // component an axis accessor of that axis reads as its whole vector and marks any other accessor, which is how a
    // vector built of one expression per component is written as that expression over the vector.
    private sealed class Writer(HashSet<string> terms, Dictionary<string, SdfFormulaNode>? substitutions) {
        private const int Atom = 9;
        private const int Power = 8;
        private const int Unary = 7;
        private const int Product = 6;
        private const int Sum = 5;
        private const int Comparison = 3;
        private const int Conjunction = 2;
        private const int Choice = 1;
        private const string Poison = "\u0000";

        private int m_axis = -1;

        public string Write(SdfFormulaNode node) => Render(node: node).Text;

        private (string Text, int Precedence) Render(SdfFormulaNode node) {
            string At(int index, int least) => Wrap(least: least, value: Render(node: node.Inputs[index]));
            string Arguments() => string.Join(separator: ", ", values: node.Inputs.Select(selector: input => Render(node: input).Text));
            (string, int) Function(string name, string arguments) => ($"{name}({arguments})", Atom);

            switch (node.Op) {
                case SdfFormulaOp.Name:
                    if ((substitutions is not null) && substitutions.TryGetValue(key: node.Text, value: out var bound)) {
                        return Render(node: bound);
                    }

                    return (terms.Contains(item: node.Text)
                        ? $"\\term{{{node.Text}}}{{{SymbolOf(name: node.Text)}}}"
                        : LocalSymbol(name: node.Text), Atom);
                case SdfFormulaOp.Literal:
                    var literal = double.Parse(s: node.Text, provider: CultureInfo.InvariantCulture);

                    return (Literal(value: literal), ((literal < 0d) ? Unary : Atom));
                case SdfFormulaOp.Constant:
                    return ((Constants.TryGetValue(key: node.Text, value: out var constant) ? constant : node.Text), Atom);
                case SdfFormulaOp.Add:
                    return ($"{At(0, Sum)} + {At(1, (Sum + 1))}", Sum);
                case SdfFormulaOp.Subtract:
                    return ($"{At(0, Sum)} − {At(1, (Sum + 1))}", Sum);
                case SdfFormulaOp.Multiply:
                    return (string.Join(separator: "·", values: Powers(factors: Flattened(node: node, op: SdfFormulaOp.Multiply).Select(selector: factor => Wrap(least: (Product + 1), value: Render(node: factor))))), Product);
                case SdfFormulaOp.MultiplyComponents:
                    return ($"{At(0, Product)}·{At(1, (Product + 1))}", Product);
                case SdfFormulaOp.Divide:
                case SdfFormulaOp.DivideComponents:
                    return ($"\\frac{{{Render(node: node.Inputs[0]).Text}}}{{{Render(node: node.Inputs[1]).Text}}}", Atom);
                case SdfFormulaOp.Negate:
                    return ($"−{At(0, Power)}", Unary);
                case SdfFormulaOp.X:
                case SdfFormulaOp.Y:
                case SdfFormulaOp.Z:
                    var axis = (node.Op - SdfFormulaOp.X);

                    if (m_axis >= 0) {
                        return ((axis == m_axis) ? Render(node: node.Inputs[0]) : (Poison, Atom));
                    }

                    return ($"{At(0, Atom)}_{{{"xyz"[axis]}}}", Atom);
                case SdfFormulaOp.Vector2:
                case SdfFormulaOp.Vector3:
                    return Vector(node: node);
                case SdfFormulaOp.Abs:
                    return ($"\\abs{{{Render(node: node.Inputs[0]).Text}}}", Atom);
                case SdfFormulaOp.Length:
                    return ($"\\norm{{{Render(node: node.Inputs[0]).Text}}}", Atom);
                case SdfFormulaOp.Min:
                case SdfFormulaOp.Max:
                    return Function(name: ((node.Op == SdfFormulaOp.Min) ? "min" : "max"), arguments: string.Join(separator: ", ", values: Flattened(node: node, op: node.Op).Select(selector: input => Render(node: input).Text)));
                case SdfFormulaOp.Clamp:
                    return Function(name: "clamp", arguments: Arguments());
                case SdfFormulaOp.Lerp:
                    return Function(name: "lerp", arguments: Arguments());
                case SdfFormulaOp.Dot:
                    return ($"{At(0, Product)}·{At(1, (Product + 1))}", Product);
                case SdfFormulaOp.SegmentParameter: {
                    var point = Render(node: node.Inputs[0]).Text;
                    var segment = Wrap(least: Product + 1, value: Render(node: node.Inputs[1]));

                    return Function(name: "clamp", arguments: $"\\frac{{{Wrap(least: Product, value: Render(node: node.Inputs[0]))}·{segment}}}{{{segment}·{segment}}}, 0, 1");
                }
                case SdfFormulaOp.MinLength:
                    return Function(name: "min", arguments: $"\\norm{{{Render(node: node.Inputs[0]).Text}}}, \\norm{{{Render(node: node.Inputs[1]).Text}}}");
                case SdfFormulaOp.ScaledRatio:
                    return ($"\\frac{{{At(0, Product)}·{At(1, (Product + 1))}}}{{{Render(node: node.Inputs[2]).Text}}}", Atom);
                case SdfFormulaOp.Power:
                    return ($"{At(0, (Power + 1))}^{{{Render(node: node.Inputs[1]).Text}}}", Power);
                case SdfFormulaOp.Sqrt:
                    return ($"\\sqrt{{{Render(node: node.Inputs[0]).Text}}}", Atom);
                case SdfFormulaOp.FiniteOr:
                case SdfFormulaOp.Detach:
                    return Render(node: node.Inputs[0]);
                case SdfFormulaOp.Ellipse2D:
                    return Function(name: "ellipse", arguments: Arguments());
                case SdfFormulaOp.Round:
                    return Function(name: "round", arguments: Arguments());
                case SdfFormulaOp.Floor:
                    return Function(name: "floor", arguments: Arguments());
                case SdfFormulaOp.RotateInverse:
                    return ($"{At(1, Atom)}^{{−1}}{At(0, Atom)}", Product);
                case SdfFormulaOp.Smoothstep:
                    return Function(name: "smoothstep", arguments: Arguments());
                case SdfFormulaOp.Fbm3:
                    return Function(name: "fbm", arguments: Render(node: node.Inputs[0]).Text);
                case SdfFormulaOp.CellDistance:
                    return Function(name: "cells", arguments: Render(node: node.Inputs[0]).Text);
                case SdfFormulaOp.SweepSearch:
                    return Function(name: "sweep", arguments: Arguments());
                case SdfFormulaOp.Equal:
                    return ($"{At(0, (Comparison + 1))} = {At(1, (Comparison + 1))}", Comparison);
                case SdfFormulaOp.Less:
                    return ($"{At(0, (Comparison + 1))} < {At(1, (Comparison + 1))}", Comparison);
                case SdfFormulaOp.LessEqual:
                    return ($"{At(0, (Comparison + 1))} ≤ {At(1, (Comparison + 1))}", Comparison);
                case SdfFormulaOp.Greater:
                    return ($"{At(0, (Comparison + 1))} > {At(1, (Comparison + 1))}", Comparison);
                case SdfFormulaOp.GreaterEqual:
                    return ($"{At(0, (Comparison + 1))} ≥ {At(1, (Comparison + 1))}", Comparison);
                case SdfFormulaOp.And:
                    return ($"{At(0, Conjunction)} and {At(1, (Conjunction + 1))}", Conjunction);
                case SdfFormulaOp.Select:
                    return ($"{At(1, (Choice + 1))} if {At(0, (Choice + 1))}, else {At(2, (Choice + 1))}", Choice);
                case SdfFormulaOp.Call:
                    return Function(name: DisplayName(formula: node.Function!), arguments: Arguments());
                default:
                    throw new ArgumentOutOfRangeException(paramName: nameof(node), actualValue: node.Op, message: "No readable lowering for this formula node.");
            }
        }
        // A vector built of one expression per component, written once over the vector when every component reads the
        // same expression of its own axis; otherwise its components in parentheses.
        private (string Text, int Precedence) Vector(SdfFormulaNode node) {
            if (m_axis < 0) {
                var components = new string[node.Inputs.Length];

                for (var axis = 0; (axis < components.Length); axis++) {
                    m_axis = axis;
                    try {
                        components[axis] = Render(node: node.Inputs[axis]).Text;
                    } finally {
                        m_axis = -1;
                    }
                }

                if (components.All(predicate: component => (!component.Contains(value: Poison, comparisonType: StringComparison.Ordinal) && (component == components[0])))) {
                    m_axis = 0;
                    try {
                        return Render(node: node.Inputs[0]);
                    } finally {
                        m_axis = -1;
                    }
                }
            }

            return ($"({string.Join(separator: ", ", values: node.Inputs.Select(selector: input => Render(node: input).Text))})", Atom);
        }
        // A product's factors with each run of one factor written as its power (h·h·h is h³).
        private static IEnumerable<string> Powers(IEnumerable<string> factors) {
            string? held = null;
            var count = 0;

            foreach (var factor in factors) {
                if (factor == held) {
                    count++;
                    continue;
                }
                if (held is not null) { yield return Raised(factor: held, count: count); }
                held = factor;
                count = 1;
            }
            if (held is not null) { yield return Raised(factor: held, count: count); }

            static string Raised(string factor, int count) => ((count == 1)
                ? factor
                : $"{(factor.StartsWith(value: '(') ? factor : $"{{{factor}}}")}^{{{count.ToString(provider: CultureInfo.InvariantCulture)}}}");
        }
        private static IEnumerable<SdfFormulaNode> Flattened(SdfFormulaNode node, SdfFormulaOp op) => node.Inputs.SelectMany(selector: input => ((input.Op == op) ? Flattened(node: input, op: op) : [input]));
        private static string Wrap((string Text, int Precedence) value, int least) => ((value.Precedence < least) ? $"({value.Text})" : value.Text);
    }
}

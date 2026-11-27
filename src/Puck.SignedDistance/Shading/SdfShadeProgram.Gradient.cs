namespace Puck.SignedDistance;

// The exact gradient of a height, by forward-mode differentiation of its expression graph. A source `gradient(point,
// height)` lowers its height over a variable standing for the point; every node of that graph carries, beside its value,
// one derivative row per component, the gradient of that component along the point's axes (the convention of the field
// walk's duals, field/sdf-dual.hlsli: a scalar's row is its gradient, a vector's rows its Jacobian). A row is null where
// it is structurally zero, so a value the point never reaches, or a branch the height discards, writes no instruction.
// Rows are ordinary nodes made through the compiler's one door (Make), so they fold, share and price as authored code.
// Where a value has no derivative (a hard min's crease, a clamp's end, a select's switch) the active branch's stands.
public sealed partial class SdfShadeProgram {
    private sealed partial class Compiler {
        private readonly Dictionary<(SdfShadePhase, SdfShadeType), Dictionary<SdfShadeExpression, Node>> m_variableCaches = [];
        // The variable a height lowering reads for its point, or null outside a gradient's height.
        private Node? m_variable;

        private Dictionary<SdfShadeExpression, Node> VariableCache(SdfShadePhase phase, SdfShadeType type) {
            if (!m_variableCaches.TryGetValue(key: (phase, type), value: out var cache)) {
                m_variableCaches.Add(key: (phase, type), value: cache = new(comparer: ReferenceEqualityComparer.Instance));
            }
            return cache;
        }
        // The variable stands in the height's graph only: its key is no digest, so no lowered node shares it, and the
        // differentiation replaces it with the point before any node leaves the gradient.
        private static Node Variable(SdfShadeType type) => new(SdfShadeOp.Input, type, [], [], uint.MaxValue, $"variable:{((int)type)}");

        // A live gradient op carries its noise's value in its first lane, from the same evaluation: a lattice beside its
        // latticeGradient, and a cells channel (F1 or F2 - F1) beside that channel's cellsGradient, read that lane rather
        // than evaluating the noise again. The lattice's value is the gradient form's trilinear sum, equal to the plain
        // reader's nested blend to rounding; the cells channels are the same arithmetic over the same search.
        private void Fuse(List<(SdfShadePhase Phase, string Name, Node Value)> outputs) {
            var live = new HashSet<Node>(comparer: ReferenceEqualityComparer.Instance);
            var pending = new Stack<Node>(collection: outputs.Select(selector: static o => o.Value));

            while (pending.TryPop(result: out var node)) {
                if (!live.Add(item: node)) { continue; }
                foreach (var arg in node.Args) { pending.Push(item: arg); }
            }
            var gradients = live.Where(predicate: static n => (n.Op is SdfShadeOp.LatticeGradient or SdfShadeOp.CellsGradient))
                .ToDictionary(keySelector: static n => (n.Op, n.Args[0].Key, n.Args[1].Key, n.Immediate), elementSelector: static n => n);

            if (gradients.Count == 0) { return; }
            var rewritten = new Dictionary<Node, Node>(comparer: ReferenceEqualityComparer.Instance);

            for (var i = 0; (i < outputs.Count); i++) { outputs[i] = (outputs[i].Phase, outputs[i].Name, Rewrite(node: outputs[i].Value)); }

            Node Rewrite(Node node) {
                if (rewritten.TryGetValue(key: node, value: out var known)) { return known; }
                Node result;

                if ((node.Op == SdfShadeOp.Lattice) && gradients.TryGetValue(key: (SdfShadeOp.LatticeGradient, node.Args[0].Key, node.Args[1].Key, 0u), value: out var lattice)) {
                    result = Make(SdfShadeOp.Component, SdfShadeType.Float, [Rewrite(node: lattice)], 0u);
                } else if ((node.Op == SdfShadeOp.Component) && (node.Args[0] is { Op: SdfShadeOp.Cells } cells) &&
                    gradients.TryGetValue(key: (SdfShadeOp.CellsGradient, cells.Args[0].Key, cells.Args[1].Key, node.Immediate), value: out var channel)) {
                    result = Make(SdfShadeOp.Component, SdfShadeType.Float, [Rewrite(node: channel)], 0u);
                } else {
                    var args = node.Args.Select(selector: Rewrite).ToArray();

                    result = (args.SequenceEqual(second: node.Args, comparer: ReferenceEqualityComparer.Instance) ? node
                        : Make(node.Op, node.Type, args, node.Immediate, node.Bits, node.Shape));
                    if ((result != node) && m_shapeDefinitions.TryGetValue(key: node.Key, value: out var shape)) { m_shapeDefinitions[result.Key] = shape; }
                }
                rewritten.Add(key: node, value: result);
                return result;
            }
        }
        private Node LowerGradient(SdfShadeExpression expression, SdfShadePhase phase, int depth) {
            if (m_variable is not null) { Fail(code: "PUCK126", message: "A gradient's height cannot hold another gradient."); }
            var arguments = (expression.Arguments ?? []);

            if (arguments.Count != 2) { Fail(code: "PUCK127", message: "'gradient' takes a point and a height over it."); }
            var point = Lower(arguments[0], phase, (depth + 1));

            if (point.Type is not (SdfShadeType.Float or SdfShadeType.Float2 or SdfShadeType.Float3 or SdfShadeType.Float4)) {
                Fail(code: "PUCK127", message: $"A gradient's point is a number or a vector of numbers, not {point.Type}.");
            }
            Node height;

            m_variable = Variable(type: point.Type);
            try { height = Lower(arguments[1], phase, (depth + 1)); } finally { m_variable = null; }
            if (height.Type != SdfShadeType.Float) { Fail(code: "PUCK127", message: $"A gradient's height is one number, not {height.Type}."); }
            return new Differentiation(compiler: this, point: point, variable: Variable(type: point.Type)).Gradient(height: height);
        }

        private sealed class Differentiation(Compiler compiler, Node variable, Node point) {
            private const float Ln2 = 0.693147180559945309417f;

            private readonly int m_tangents = SdfShadeOps.Width(type: point.Type);
            private readonly Dictionary<Node, bool> m_depends = new(comparer: ReferenceEqualityComparer.Instance);
            private readonly Dictionary<Node, Node> m_primal = new(comparer: ReferenceEqualityComparer.Instance);
            private readonly Dictionary<Node, Node?[]?> m_rows = new(comparer: ReferenceEqualityComparer.Instance);

            // A row's type: one lane a tangent, the point's own width.
            private SdfShadeType RowType => point.Type;

            public Node Gradient(Node height) => (Rows(node: height)?[0] ?? Zero());

            private bool Depends(Node node) {
                if (ReferenceEquals(objA: node, objB: variable) || (node.Key == variable.Key)) { return true; }
                if (m_depends.TryGetValue(key: node, value: out var known)) { return known; }
                var depends = node.Args.Any(predicate: Depends);

                m_depends.Add(key: node, value: depends);
                return depends;
            }
            // A height node's value at the point: the node itself where the point does not reach it, else the same
            // operation over its arguments' values, which is the node the author's own use of the point lowered to.
            private Node Primal(Node node) {
                if (!Depends(node: node)) { return node; }
                if (node.Key == variable.Key) { return point; }
                if (m_primal.TryGetValue(key: node, value: out var known)) { return known; }
                if (node.Shape is not null) { Refuse(op: node.Op); }
                var value = compiler.Make(node.Op, node.Type, node.Args.Select(selector: Primal).ToArray(), node.Immediate, node.Bits);

                m_primal.Add(key: node, value: value);
                return value;
            }
            // A height node's derivative rows, null where every row is zero.
            private Node?[]? Rows(Node node) {
                if (!Depends(node: node) || SdfShadeOps.IsUInt(type: node.Type)) { return null; }
                if (m_rows.TryGetValue(key: node, value: out var known)) { return known; }
                var rows = ((node.Key == variable.Key) ? Each(width: m_tangents, row: Unit) : Rule(node: node));

                m_rows.Add(key: node, value: rows);
                return rows;
            }
            private Node?[]? Rule(Node node) {
                var width = SdfShadeOps.Width(type: node.Type);
                var a = ((node.Args.Length > 0) ? node.Args[0] : null);
                var b = ((node.Args.Length > 1) ? node.Args[1] : null);
                var da = ((a is null) ? null : Rows(node: a));
                var db = ((b is null) ? null : Rows(node: b));
                Node A() => Primal(node: a!);
                Node B() => Primal(node: b!);
                Node?[]? Unary(Node factor) => Each(width: width, row: i => Scale(scale: At(value: factor, index: i), row: Row(rows: da, index: i)));

                switch (node.Op) {
                    case SdfShadeOp.Component: return Each(width: 1, row: _ => Row(rows: da, index: ((int)node.Immediate)));
                    case SdfShadeOp.Vector2 or SdfShadeOp.Vector3 or SdfShadeOp.Vector4: return Each(width: width, row: i => Row(rows: Rows(node: node.Args[i]), index: 0));
                    case SdfShadeOp.Add: return Each(width: width, row: i => Plus(left: Row(rows: da, index: i), right: Row(rows: db, index: i)));
                    case SdfShadeOp.Subtract: return Each(width: width, row: i => Minus(left: Row(rows: da, index: i), right: Row(rows: db, index: i)));
                    case SdfShadeOp.Negate: return Each(width: width, row: i => Negate(row: Row(rows: da, index: i)));
                    case SdfShadeOp.Multiply:
                        return Each(width: width, row: i => Plus(Scale(scale: At(value: B(), index: i), row: Row(rows: da, index: i)), Scale(scale: At(value: A(), index: i), row: Row(rows: db, index: i))));
                    case SdfShadeOp.Divide:
                        var quotient = Primal(node: node);

                        return Each(width: width, row: i => Over(row: Minus(Row(rows: da, index: i), Scale(scale: At(value: quotient, index: i), row: Row(rows: db, index: i))), divisor: At(value: B(), index: i)));
                    case SdfShadeOp.Min or SdfShadeOp.Max:
                        return Each(width: width, row: i => {
                            var less = Make(SdfShadeOp.Less, At(value: A(), index: i), At(value: B(), index: i));

                            return ((node.Op == SdfShadeOp.Min) ? Select(less, Row(rows: da, index: i), Row(rows: db, index: i)) : Select(less, Row(rows: db, index: i), Row(rows: da, index: i)));
                        });
                    case SdfShadeOp.Select:
                        var condition = Primal(node: node.Args[0]);
                        var whenTrue = Rows(node: node.Args[1]);
                        var whenFalse = Rows(node: node.Args[2]);

                        return Each(width: width, row: i => Select(condition, Row(rows: whenTrue, index: i), Row(rows: whenFalse, index: i)));
                    case SdfShadeOp.Abs:
                        return Each(width: width, row: i => Select(Make(SdfShadeOp.Less, At(value: A(), index: i), Scalar(value: 0f)), Negate(row: Row(rows: da, index: i)), Row(rows: da, index: i)));
                    case SdfShadeOp.Saturate:
                        var clamped = Primal(node: node);

                        return Each(width: width, row: i => Select(Make(SdfShadeOp.Equal, At(value: clamped, index: i), At(value: A(), index: i)), Row(rows: da, index: i), null));
                    // Piecewise constant: flat on both sides of every step.
                    case SdfShadeOp.Floor or SdfShadeOp.Ceil or SdfShadeOp.Step or SdfShadeOp.Equal or SdfShadeOp.Less or SdfShadeOp.LessEqual:
                        return null;
                    case SdfShadeOp.Mix:
                        return Rows(node: compiler.Make(SdfShadeOp.Add, a!, compiler.Make(SdfShadeOp.Multiply, compiler.Make(SdfShadeOp.Subtract, b!, a!), node.Args[2])));
                    case SdfShadeOp.Smoothstep:
                        // smoothstep(e0, e1, x) is t²(3 - 2t) of t = saturate((x - e0) / (e1 - e0)), whose slope is 6t(1 - t).
                        var ramp = compiler.Make(SdfShadeOp.Saturate, compiler.Make(SdfShadeOp.Divide, compiler.Make(SdfShadeOp.Subtract, node.Args[2], a!), compiler.Make(SdfShadeOp.Subtract, b!, a!)));
                        var dt = Rows(node: ramp);
                        var t = Primal(node: ramp);
                        var slope = Make(SdfShadeOp.Multiply, Make(SdfShadeOp.Multiply, Scalar(value: 6f), t), Make(SdfShadeOp.Subtract, Scalar(value: 1f), t));

                        return Each(width: width, row: i => Scale(scale: At(value: slope, index: i), row: Row(rows: dt, index: i)));
                    case SdfShadeOp.Pow:
                        var power = Primal(node: node);
                        Node? toBase = null;
                        Node? toExponent = null;

                        if (da is not null) {
                            var exponent = B();
                            var less = Make(SdfShadeOp.Subtract, exponent, Scalar(value: 1f));

                            toBase = (IsScalar(node: less, value: 0f) ? exponent
                                : (IsScalar(node: less, value: 1f) ? Make(SdfShadeOp.Multiply, exponent, A())
                                : Make(SdfShadeOp.Multiply, exponent, Make(SdfShadeOp.Pow, A(), less))));
                        }
                        if (db is not null) { toExponent = Make(SdfShadeOp.Multiply, power, Make(SdfShadeOp.Multiply, Make(SdfShadeOp.Log2, A()), Scalar(value: Ln2))); }
                        return Each(width: width, row: i => Plus(
                            ((toBase is null) ? null : Scale(scale: At(value: toBase, index: i), row: Row(rows: da, index: i))),
                            ((toExponent is null) ? null : Scale(scale: At(value: toExponent, index: i), row: Row(rows: db, index: i)))));
                    case SdfShadeOp.Exp2: return Unary(factor: Make(SdfShadeOp.Multiply, Primal(node: node), Scalar(value: Ln2)));
                    case SdfShadeOp.Log2: return Unary(factor: Make(SdfShadeOp.Divide, Scalar(value: (1f / Ln2)), A()));
                    case SdfShadeOp.Sin: return Unary(factor: Make(SdfShadeOp.Cos, A()));
                    case SdfShadeOp.Cos: return Unary(factor: Make(SdfShadeOp.Negate, Make(SdfShadeOp.Sin, A())));
                    case SdfShadeOp.Tan:
                        var tangent = Primal(node: node);

                        return Unary(factor: Make(SdfShadeOp.Add, Scalar(value: 1f), Make(SdfShadeOp.Multiply, tangent, tangent)));
                    case SdfShadeOp.Asin or SdfShadeOp.Acos:
                        return Unary(factor: Make(SdfShadeOp.Divide, Scalar(value: ((node.Op == SdfShadeOp.Asin) ? 1f : -1f)),
                            Make(SdfShadeOp.Sqrt, Make(SdfShadeOp.Subtract, Scalar(value: 1f), Make(SdfShadeOp.Multiply, A(), A())))));
                    case SdfShadeOp.Atan: return Unary(factor: Make(SdfShadeOp.Divide, Scalar(value: 1f), Make(SdfShadeOp.Add, Scalar(value: 1f), Make(SdfShadeOp.Multiply, A(), A()))));
                    case SdfShadeOp.Sqrt: return Unary(factor: Make(SdfShadeOp.Divide, Scalar(value: 0.5f), Primal(node: node)));
                    case SdfShadeOp.Atan2:
                        // atan2(y, x): (x dy - y dx) / (x² + y²).
                        var radius = Make(SdfShadeOp.Add, Make(SdfShadeOp.Multiply, B(), B()), Make(SdfShadeOp.Multiply, A(), A()));

                        return Each(width: width, row: i => Over(row: Minus(Scale(scale: At(value: B(), index: i), row: Row(rows: da, index: i)), Scale(scale: At(value: A(), index: i), row: Row(rows: db, index: i))), divisor: At(value: radius, index: i)));
                    case SdfShadeOp.Dot:
                        return Each(width: 1, row: _ => Plus(Combine(count: SdfShadeOps.Width(type: a!.Type), rows: da, scalar: i => At(value: B(), index: i)),
                            Combine(count: SdfShadeOps.Width(type: a.Type), rows: db, scalar: i => At(value: A(), index: i))));
                    case SdfShadeOp.Length when (SdfShadeOps.Width(type: a!.Type) == 1):
                        return Each(width: 1, row: _ => Select(Make(SdfShadeOp.Less, A(), Scalar(value: 0f)), Negate(row: Row(rows: da, index: 0)), Row(rows: da, index: 0)));
                    case SdfShadeOp.Length:
                        var length = Primal(node: node);

                        return Each(width: 1, row: _ => Over(row: Combine(count: SdfShadeOps.Width(type: a!.Type), rows: da, scalar: i => At(value: A(), index: i)), divisor: length));
                    case SdfShadeOp.Normalize when (width == 1):
                        return null;
                    case SdfShadeOp.Normalize:
                        // (dv - n (n · dv)) / |v|.
                        var unit = Primal(node: node);
                        var magnitude = Make(SdfShadeOp.Length, A());
                        var along = Combine(count: width, rows: da, scalar: j => At(value: unit, index: j));

                        return Each(width: width, row: i => Over(row: Minus(Row(rows: da, index: i), Scale(scale: At(value: unit, index: i), row: along)), divisor: magnitude));
                    case SdfShadeOp.Cross:
                        Node C(Node v, int i) => compiler.Make(SdfShadeOp.Component, SdfShadeType.Float, [v], ((uint)i));
                        Node Term(int i, int j) => compiler.Make(SdfShadeOp.Subtract, compiler.Make(SdfShadeOp.Multiply, C(a!, i), C(b!, j)), compiler.Make(SdfShadeOp.Multiply, C(a!, j), C(b!, i)));

                        return Rows(node: compiler.Make(SdfShadeOp.Vector3, Term(1, 2), Term(2, 0), Term(0, 1)));
                    case SdfShadeOp.Reflect:
                        // reflect(i, n) = i - 2 (n · i) n.
                        return Rows(node: compiler.Make(SdfShadeOp.Subtract, a!, compiler.Make(SdfShadeOp.Multiply, compiler.Make(SdfShadeOp.Multiply, Scalar(value: 2f), compiler.Make(SdfShadeOp.Dot, b!, a!)), b!)));
                    case SdfShadeOp.Rotate or SdfShadeOp.InverseRotate:
                        if (db is not null) { Fail(code: "PUCK126", message: "A gradient's height turns its point by a rotation that varies with the point; only a fixed rotation has a derivative here."); }
                        var rotation = B();

                        return Linear(rows: da, map: column => Make(node.Op, column, rotation));
                    case SdfShadeOp.ToLayer or SdfShadeOp.ToWorld or SdfShadeOp.DrawDirection:
                        return Linear(rows: da, map: column => Make(node.Op, column));
                    case SdfShadeOp.Lattice:
                        return Each(width: 1, row: _ => Chain(gradient: Make(SdfShadeOp.LatticeGradient, A(), B()), rows: da));
                    case SdfShadeOp.Cells:
                        // [F1, F2 - F1, id]: the id is constant over each cell.
                        Node Channel(uint channel) => compiler.Make(SdfShadeOp.CellsGradient, SdfShadeType.Float4, [A(), B()], channel);

                        return Each(width: 3, row: i => ((i < 2) ? Chain(gradient: Channel(channel: ((uint)i)), rows: da) : null));
                    default:
                        Refuse(op: node.Op);
                        return null;
                }
            }
            // The rows of a map linear in its vector operand: the map of each tangent's column, read back by rows.
            private Node?[]? Linear(Node?[]? rows, Func<Node, Node> map) {
                if (rows is null) { return null; }
                var columns = Enumerable.Range(start: 0, count: m_tangents).Select(selector: m => map(Make(SdfShadeOp.Vector3,
                    Enumerable.Range(start: 0, count: 3).Select(selector: j => ((Row(rows: rows, index: j) is { } row) ? At(value: row, index: m) : Scalar(value: 0f))).ToArray()))).ToArray();

                return Each(width: 3, row: i => ((m_tangents == 1) ? At(value: columns[0], index: i)
                    : Make(VectorOf(width: m_tangents), columns.Select(selector: column => At(value: column, index: i)).ToArray())));
            }
            // The chain rule through a value with a gradient [value, d/dx, d/dy, d/dz] of a three-vector operand.
            private Node? Chain(Node gradient, Node?[]? rows) => Combine(count: 3, rows: rows, scalar: j => At(value: gradient, index: (j + 1)));
            // The sum of each scalar times its row, a vector's rows weighted by a gradient (the transposed Jacobian times
            // it). Where every row lies along its own axis (a point scaled by constants, or by one value on every axis as a
            // pattern's frequency scales it) it is the scalars as a vector times the scales, one product; with unit
            // scales it is the scalars' vector alone, and a vector's own components rebuild that vector.
            private Node? Combine(int count, Func<int, Node> scalar, Node?[]? rows) {
                if (rows is null) { return null; }
                var coefficients = (((count == m_tangents) && (count > 1)) ? Enumerable.Range(start: 0, count: count).Select(selector: j => Diagonal(row: Row(rows: rows, index: j), axis: j)).ToArray() : []);

                if ((coefficients.Length == count) && coefficients.All(predicate: static c => (c is not null))) {
                    var vector = Make(VectorOf(width: count), Enumerable.Range(start: 0, count: count).Select(selector: scalar).ToArray());

                    return (coefficients.Distinct(comparer: ReferenceEqualityComparer.Instance).Count() == 1)
                        ? Scale(scale: coefficients[0]!, row: vector)
                        : Make(SdfShadeOp.Multiply, Make(VectorOf(width: count), coefficients!), vector);
                }
                return Sum(count: count, term: j => Scale(scale: scalar(j), row: Row(rows: rows, index: j)));
            }
            private static SdfShadeOp VectorOf(int width) => ((SdfShadeOp)(((int)SdfShadeOp.Vector2) + (width - 2)));
            // A row's one coefficient where it lies along its own axis: a constant zero but on that axis, or a scalar
            // times such a constant. Null for any other row.
            private Node? Diagonal(Node? row, int axis) {
                bool Along(Node node) => ((node.Op == SdfShadeOp.Constant) && (node.Bits.Length == m_tangents) &&
                    Enumerable.Range(start: 0, count: m_tangents).All(predicate: m => ((m == axis) || (node.Bits[m] == 0u))));
                Node Coefficient(Node unit) => Scalar(value: BitConverter.UInt32BitsToSingle(value: unit.Bits[axis]));

                return row switch {
                    null => null,
                    _ when Along(node: row) => Coefficient(unit: row),
                    { Op: SdfShadeOp.Multiply, Args: [{ Type: SdfShadeType.Float } scale, var unit] } when Along(node: unit) => Scale(scale: Coefficient(unit: unit), row: scale),
                    { Op: SdfShadeOp.Multiply, Args: [var unit, { Type: SdfShadeType.Float } scale] } when Along(node: unit) => Scale(scale: Coefficient(unit: unit), row: scale),
                    _ => null,
                };
            }

            private Node Make(SdfShadeOp op, params Node[] args) => compiler.Make(op, args);
            private Node Constant(SdfShadeType type, params float[] values) => compiler.Make(SdfShadeOp.Constant, type, [], 0u, values.Select(selector: BitConverter.SingleToUInt32Bits).ToArray());
            private Node Scalar(float value) => Constant(type: SdfShadeType.Float, values: value);
            private Node Zero() => Constant(type: RowType, values: new float[m_tangents]);
            private Node Unit(int axis) => Constant(type: RowType, values: Enumerable.Range(start: 0, count: m_tangents).Select(selector: m => ((m == axis) ? 1f : 0f)).ToArray());
            private Node At(Node value, int index) => ((SdfShadeOps.Width(type: value.Type) == 1) ? value : compiler.Make(SdfShadeOp.Component, SdfShadeType.Float, [value], ((uint)index)));
            private static bool IsScalar(Node node, float value) => ((node.Op == SdfShadeOp.Constant) && (node.Bits.Length == 1) && (node.Bits[0] == BitConverter.SingleToUInt32Bits(value: value)));
            // A scalar operand broadcasts its one row across a vector operation.
            private static Node? Row(Node?[]? rows, int index) => rows?[((rows.Length == 1) ? 0 : index)];
            private static Node?[]? Each(int width, Func<int, Node?> row) {
                var rows = Enumerable.Range(start: 0, count: width).Select(selector: row).ToArray();

                return (rows.Any(predicate: static r => (r is not null)) ? rows : null);
            }
            private Node? Plus(Node? left, Node? right) => ((left is null) ? right : ((right is null) ? left : Make(SdfShadeOp.Add, left, right)));
            private Node? Minus(Node? left, Node? right) => ((right is null) ? left : ((left is null) ? Negate(row: right) : Make(SdfShadeOp.Subtract, left, right)));
            private Node? Negate(Node? row) => ((row is null) ? null : Make(SdfShadeOp.Negate, row));
            private Node? Scale(Node scale, Node? row) => ((row is null) ? null
                : (IsScalar(node: scale, value: 1f) ? row : (IsScalar(node: scale, value: -1f) ? Negate(row: row) : Make(SdfShadeOp.Multiply, scale, row))));
            private Node? Over(Node? row, Node divisor) => ((row is null) ? null : Make(SdfShadeOp.Divide, row, divisor));
            private Node? Select(Node condition, Node? whenTrue, Node? whenFalse) => (((whenTrue is null) && (whenFalse is null)) ? null
                : Make(SdfShadeOp.Select, condition, (whenTrue ?? Zero()), (whenFalse ?? Zero())));
            private Node? Sum(int count, Func<int, Node?> term) => Enumerable.Range(start: 0, count: count).Select(selector: term).Aggregate(seed: ((Node?)null), func: Plus);

            [System.Diagnostics.CodeAnalysis.DoesNotReturn]
            private static void Refuse(SdfShadeOp op) => Fail(code: "PUCK126", message: op switch {
                SdfShadeOp.Shape => "A gradient's height reads 'shape' of its point, which has no derivative: a shape's coverage distance is not a height. Ramp a pattern with smoothstep instead.",
                _ => $"A gradient's height reads '{SdfShadeOps.Get(op: op).Name}' of its point, which has no derivative. Build the height from arithmetic, the smooth functions, lattice and cells.",
            });
        }
    }
}

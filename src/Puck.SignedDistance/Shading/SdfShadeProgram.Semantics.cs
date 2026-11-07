namespace Puck.SignedDistance;

public sealed partial class SdfShadeProgram {
    /// <summary>Types an ordinary op from its operands. The generator enumerates this same admitted signature set.</summary>
    public static SdfShadeType TypeOf(SdfShadeOp op, SdfShadeType[] args) {
        ArgumentNullException.ThrowIfNull(args);
        if ((args.Length == 0) || (args.Length != SdfShadeOps.Get(op: op).Arity)) {
            throw new SdfShadeException(code: "PUCK127", message: "Shade operation has the wrong operand count.");
        }
        foreach (var type in args) { _ = SdfShadeOps.Width(type: type); }
        var a = args[0];
        var b = ((args.Length > 1) ? args[1] : a);
        var c = ((args.Length > 2) ? args[2] : a);
        var allFloat = args.All(predicate: t => !SdfShadeOps.IsUInt(type: t));

        switch (op) {
            case SdfShadeOp.Quantize when (a is SdfShadeType.Float or SdfShadeType.Float3):
                return ((a == SdfShadeType.Float) ? SdfShadeType.UInt : SdfShadeType.UInt3);
            case SdfShadeOp.Hash when (a == SdfShadeType.UInt3): return SdfShadeType.UInt3;
            case SdfShadeOp.Lattice when ((a == SdfShadeType.Float3) && (b == SdfShadeType.UInt)): return SdfShadeType.Float;
            case SdfShadeOp.LatticeGradient when ((a == SdfShadeType.Float3) && (b == SdfShadeType.UInt)): return SdfShadeType.Float4;
            case SdfShadeOp.Vector2 or SdfShadeOp.Vector3 or SdfShadeOp.Vector4 when args.All(predicate: t => (t == SdfShadeType.Float)):
                return ((SdfShadeType)args.Length);
            case SdfShadeOp.Select when ((a == SdfShadeType.Float) && (b == c)): return b;
            case SdfShadeOp.Rotate or SdfShadeOp.InverseRotate when ((a == SdfShadeType.Float3) && (b == SdfShadeType.Float4)): return a;
            case SdfShadeOp.Dot when (allFloat && (a == b)): return SdfShadeType.Float;
            case SdfShadeOp.Length when allFloat: return SdfShadeType.Float;
            case SdfShadeOp.Cross or SdfShadeOp.Reflect when ((a == SdfShadeType.Float3) && (b == a)): return a;
            case SdfShadeOp.Refract or SdfShadeOp.Tilt when ((a == SdfShadeType.Float3) && (b == a) && (c == SdfShadeType.Float)): return a;
            case SdfShadeOp.ToLayer or SdfShadeOp.ToWorld or SdfShadeOp.DrawDirection when (a == SdfShadeType.Float3): return a;
            case SdfShadeOp.Lines when ((a == SdfShadeType.Float2) && (b == SdfShadeType.UInt) && (c == SdfShadeType.Float)): return SdfShadeType.Float;
            case SdfShadeOp.Shape when (a == SdfShadeType.Float3): return SdfShadeType.Float;
            case SdfShadeOp.Equal or SdfShadeOp.Less or SdfShadeOp.LessEqual when ((a == SdfShadeType.Float) && (b == a)): return a;
            case SdfShadeOp.Mix or SdfShadeOp.Smoothstep when (allFloat && ((a == b) || (a == SdfShadeType.Float) || (b == SdfShadeType.Float)) &&
                ((c == SdfShadeType.Float) || (c == ((SdfShadeType)Math.Max(val1: ((int)a), val2: ((int)b)))))):
                return ((SdfShadeType)Math.Max(val1: ((int)a), val2: ((int)b)));
            case SdfShadeOp.Add or SdfShadeOp.Subtract or SdfShadeOp.Multiply or SdfShadeOp.Divide or
                SdfShadeOp.Min or SdfShadeOp.Max or SdfShadeOp.Pow or SdfShadeOp.Step when (allFloat && ((a == b) || (a == SdfShadeType.Float) || (b == SdfShadeType.Float))):
                return ((SdfShadeType)Math.Max(val1: ((int)a), val2: ((int)b)));
            case SdfShadeOp.Negate or SdfShadeOp.Abs or SdfShadeOp.Floor or SdfShadeOp.Ceil or SdfShadeOp.Saturate or
                SdfShadeOp.Exp2 or SdfShadeOp.Log2 or SdfShadeOp.Sin or SdfShadeOp.Cos or SdfShadeOp.Tan or SdfShadeOp.Asin or
                SdfShadeOp.Acos or SdfShadeOp.Atan or SdfShadeOp.Sqrt or SdfShadeOp.Normalize when allFloat:
                return a;
        }
        throw new SdfShadeException(code: "PUCK127", message: $"'{SdfShadeOps.Get(op: op).Name}' does not accept ({string.Join(separator: ", ", values: args)}); integer conversion is explicit.");
    }

    private static bool Fold(SdfShadeOp op, SdfShadeType type, Node[] args, out uint[] bits) {
        bits = [];
        if ((args.Length == 0) || args.Any(predicate: a => (a.Op != SdfShadeOp.Constant)) || SdfShadeOps.IsUInt(type: type)) { return false; }
        if (op is not (SdfShadeOp.Add or SdfShadeOp.Subtract or SdfShadeOp.Multiply or SdfShadeOp.Divide or
            SdfShadeOp.Min or SdfShadeOp.Max or SdfShadeOp.Equal or SdfShadeOp.Less or SdfShadeOp.LessEqual or
            SdfShadeOp.Negate or SdfShadeOp.Vector2 or SdfShadeOp.Vector3 or SdfShadeOp.Vector4)) { return false; }
        bits = new uint[SdfShadeOps.Width(type: type)];
        for (var i = 0; (i < bits.Length); i++) {
            var a = BitConverter.UInt32BitsToSingle(value: args[0].Bits[Math.Min(val1: i, val2: (args[0].Bits.Length - 1))]);
            var b = ((args.Length > 1) ? BitConverter.UInt32BitsToSingle(value: args[1].Bits[Math.Min(val1: i, val2: (args[1].Bits.Length - 1))]) : 0f);
            var value = op switch {
                SdfShadeOp.Add => (a + b),
                SdfShadeOp.Subtract => (a - b),
                SdfShadeOp.Multiply => (a * b),
                SdfShadeOp.Divide => (a / b),
                SdfShadeOp.Min => MathF.Min(x: a, y: b),
                SdfShadeOp.Max => MathF.Max(x: a, y: b),
                SdfShadeOp.Equal => ((a == b) ? 1f : 0f),
                SdfShadeOp.Less => ((a < b) ? 1f : 0f),
                SdfShadeOp.LessEqual => ((a <= b) ? 1f : 0f),
                SdfShadeOp.Negate => -a,
                _ => BitConverter.UInt32BitsToSingle(value: args[i].Bits[0]),
            };
            // Preserve IEEE exceptional arithmetic at runtime: device NaN payloads are not a host constant.
            if (!float.IsFinite(f: value)) { bits = []; return false; }
            bits[i] = BitConverter.SingleToUInt32Bits(value: value);
        }
        return true;
    }
    private static float Upper(Node node) {
        if (node.Op == SdfShadeOp.Constant) { return node.Bits.Select(selector: BitConverter.UInt32BitsToSingle).Max(); }
        if ((node.Op == SdfShadeOp.Saturate) || (node.Op is SdfShadeOp.Step or SdfShadeOp.Smoothstep)) { return 1f; }
        if (node.Op == SdfShadeOp.Min) { return MathF.Min(x: Upper(node: node.Args[0]), y: Upper(node: node.Args[1])); }
        if (node.Op == SdfShadeOp.Max) { return MathF.Max(x: Upper(node: node.Args[0]), y: Upper(node: node.Args[1])); }
        if (node.Op == SdfShadeOp.Select) { return MathF.Max(x: Upper(node: node.Args[1]), y: Upper(node: node.Args[2])); }
        return float.PositiveInfinity;
    }
}

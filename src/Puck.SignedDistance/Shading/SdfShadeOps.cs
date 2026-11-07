using System.Security.Cryptography;
using System.Text;

namespace Puck.SignedDistance;

/// <summary>Operations of the presentation IR. The table supplies typing, work and HLSL for both future tiers.</summary>
public enum SdfShadeOp {
    Constant, Parameter, Input, Component, Vector2, Vector3, Vector4,
    Add, Subtract, Multiply, Divide, Min, Max, Negate, Abs, Floor, Ceil,
    Equal, Less, LessEqual, Select, Saturate, Mix, Smoothstep, Step,
    Pow, Exp2, Log2, Sin, Cos, Tan, Asin, Acos, Atan, Sqrt,
    Dot, Cross, Normalize, Length, Rotate, InverseRotate, Quantize, Hash,
    Lattice, LatticeGradient, Lines, Reflect, Refract, Tilt, ToLayer, ToWorld, DrawDirection, Shape,
}
/// <summary>One op's complete description. Template placeholders are a, b, c and result, never author text.</summary>
public sealed record SdfShadeOpInfo(SdfShadeOp Op, string Name, int Arity, uint Work, string Template, bool Commutative = false);
/// <summary>One stamp field, with its phase availability, type and cadence dependencies.</summary>
public sealed record SdfShadeInput(string Name, SdfShadeType Type, SdfShadePhase? Phase = null, SdfShadeReads Reads = SdfShadeReads.None, bool AfterSurface = false);
/// <summary>The one shade vocabulary and semantics. Every intermediate is binary32 or uint32; only exact IEEE
/// arithmetic folds on the host. The template result is precise, and neither tier contracts or reassociates ops.</summary>
public static class SdfShadeOps {
    /// <summary>Per-instruction bound for decoding, named-register selection and immediate output writes.</summary>
    public const uint DispatchWork = 256;

    /// <summary>All op descriptions in opcode order.</summary>
    public static IReadOnlyList<SdfShadeOpInfo> Table { get; } = Array.AsReadOnly(array: new[] {
        D(SdfShadeOp.Constant, "constant", 0, 1, "sdfShadeConstant(slice, immediate)"),
        D(SdfShadeOp.Parameter, "parameter", 0, 1, "sdfShadeParameter(parameters, immediate)"),
        D(SdfShadeOp.Input, "input", 0, 1, "stamp.values[immediate]"),
        D(SdfShadeOp.Component, "component", 1, 1, "a[immediate]"),
        D(SdfShadeOp.Vector2, "float2", 2, 1, "float2(a.x, b.x)"),
        D(SdfShadeOp.Vector3, "float3", 3, 1, "float3(a.x, b.x, c.x)"),
        D(SdfShadeOp.Vector4, "float4", 4, 1, "float4(a.x, b.x, c.x, d.x)"),
        D(arity: 2, commutative: true, name: "+", op: SdfShadeOp.Add, template: "(a + b)", work: 1),
        D(SdfShadeOp.Subtract, "-", 2, 1, "(a - b)"),
        D(arity: 2, commutative: true, name: "*", op: SdfShadeOp.Multiply, template: "(a * b)", work: 1),
        D(SdfShadeOp.Divide, "/", 2, 4, "(a / b)"),
        D(arity: 2, commutative: true, name: "min", op: SdfShadeOp.Min, template: "min(a, b)", work: 1),
        D(arity: 2, commutative: true, name: "max", op: SdfShadeOp.Max, template: "max(a, b)", work: 1),
        D(SdfShadeOp.Negate, "negate", 1, 1, "(-a)"),
        D(SdfShadeOp.Abs, "abs", 1, 1, "abs(a)"),
        D(SdfShadeOp.Floor, "floor", 1, 1, "floor(a)"),
        D(SdfShadeOp.Ceil, "ceil", 1, 1, "ceil(a)"),
        D(SdfShadeOp.Equal, "==", 2, 1, "(a == b)"),
        D(SdfShadeOp.Less, "<", 2, 1, "(a < b)"),
        D(SdfShadeOp.LessEqual, "<=", 2, 1, "(a <= b)"),
        D(SdfShadeOp.Select, "select", 3, 1, "(a.x != 0.0 ? b : c)"),
        D(SdfShadeOp.Saturate, "saturate", 1, 1, "saturate(a)"),
        D(SdfShadeOp.Mix, "mix", 3, 3, "(a + ((b - a) * c))"),
        D(SdfShadeOp.Smoothstep, "smoothstep", 3, 8, "smoothstep(a, b, c)"),
        D(SdfShadeOp.Step, "step", 2, 1, "step(a, b)"),
        D(SdfShadeOp.Pow, "pow", 2, 16, "pow(a, b)"),
        D(SdfShadeOp.Exp2, "exp2", 1, 8, "exp2(a)"),
        D(SdfShadeOp.Log2, "log2", 1, 8, "log2(a)"),
        D(SdfShadeOp.Sin, "sin", 1, 8, "sin(a)"),
        D(SdfShadeOp.Cos, "cos", 1, 8, "cos(a)"),
        D(SdfShadeOp.Tan, "tan", 1, 16, "tan(a)"),
        D(SdfShadeOp.Asin, "asin", 1, 16, "asin(a)"),
        D(SdfShadeOp.Acos, "acos", 1, 16, "acos(a)"),
        D(SdfShadeOp.Atan, "atan", 1, 16, "atan(a)"),
        D(SdfShadeOp.Sqrt, "sqrt", 1, 8, "sqrt(a)"),
        D(SdfShadeOp.Dot, "dot", 2, 7, "dot(a, b)"),
        D(SdfShadeOp.Cross, "cross", 2, 9, "cross(a, b)"),
        D(SdfShadeOp.Normalize, "normalize", 1, 16, "normalize(a)"),
        D(SdfShadeOp.Length, "length", 1, 15, "length(a)"),
        D(SdfShadeOp.Rotate, "rotate", 2, 30, "rotatePointByQuaternion(a, b)"),
        D(SdfShadeOp.InverseRotate, "inverseRotate", 2, 30, "rotatePointByInverseQuaternion(a, b)"),
        D(SdfShadeOp.Quantize, "quantize", 1, 2, "asuint(floor(a))"),
        D(SdfShadeOp.Hash, "hash", 1, 48, "sdfPcg3d(a)"),
        D(SdfShadeOp.Lattice, "lattice", 2, 512, "sdfLatticeNoise3(a, b)"),
        D(SdfShadeOp.LatticeGradient, "latticeGradient", 2, 640, "sdfLatticeNoiseGradient3(a, b)"),
        D(SdfShadeOp.Lines, "lines", 3, 128, "sdfLineCoverage(a, b, c)"),
        D(SdfShadeOp.Reflect, "reflect", 2, 12, "reflect(a, b)"),
        D(SdfShadeOp.Refract, "refract", 3, 32, "refract(a, b, c)"),
        D(SdfShadeOp.Tilt, "tilt", 3, 128, "sdfShadeTilt(a, b, c)"),
        D(SdfShadeOp.ToLayer, "toLayer", 1, 30, "sdfShadeToLayer(stamp, a)"),
        D(SdfShadeOp.ToWorld, "toWorld", 1, 30, "sdfShadeToWorld(stamp, a)"),
        D(SdfShadeOp.DrawDirection, "drawDirection", 1, 48, "sdfShadeDrawDirection(stamp, a)"),
        D(SdfShadeOp.Shape, "shape", 1, 0, "sdfShadeShape(slice, immediate, a, asfloat(sdfShadeInput(stamp, SDF_SHADE_INPUT_SURFACE_FOOTPRINT)).x)"),
    });
    /// <summary>The fixed stamp. Indices are generated from this table, never supplied as author addresses.</summary>
    public static IReadOnlyList<SdfShadeInput> Inputs { get; } = Array.AsReadOnly(array: new[] {
        I("surface.position", SdfShadeType.Float3), I("surface.layerPosition", SdfShadeType.Float3),
        I("surface.geometricNormal", SdfShadeType.Float3), I("surface.normal", SdfShadeType.Float3),
        I("surface.curvature"), I("surface.footprint", reads: SdfShadeReads.Footprint),
        I("surface.rayDirection", SdfShadeType.Float3), I("surface.contact", reads: SdfShadeReads.Contact),
        I("surface.thickness"), I("paint.coverage"), I("clock", reads: SdfShadeReads.Clock),
        I("material.albedo", SdfShadeType.Float3), I("material.roughness"), I("material.metal"),
        I("material.specular"), I("material.coat"), I("material.sheen"), I("material.wrap"),
        I("material.fill", SdfShadeType.Float3), I("material.emissive"),
        new SdfShadeInput("surface.albedo", SdfShadeType.Float3, AfterSurface: true),
        new SdfShadeInput("surface.roughness", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.metal", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.specular", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.coat", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.sheen", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.wrap", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.fill", SdfShadeType.Float3, AfterSurface: true),
        new SdfShadeInput("surface.emissive", SdfShadeType.Float, AfterSurface: true),
        new SdfShadeInput("surface.reveal", SdfShadeType.Float, AfterSurface: true),
        I("lanes.slot0", reads: SdfShadeReads.Slot0), I("lanes.slot1", reads: SdfShadeReads.Slot1),
        I("lanes.slot2", reads: SdfShadeReads.Slot2), I("lanes.slot3", reads: SdfShadeReads.Slot3),
        I("lanes.world0", reads: SdfShadeReads.World0), I("lanes.world1", reads: SdfShadeReads.World1),
        I("lanes.world2", reads: SdfShadeReads.World2), I("lanes.world3", reads: SdfShadeReads.World3),
        I("light.direction", SdfShadeType.Float3, SdfShadePhase.Light), I("light.position", SdfShadeType.Float3, SdfShadePhase.Light),
        I("light.color", SdfShadeType.Float3, SdfShadePhase.Light), I("light.weight", phase: SdfShadePhase.Light),
        I("light.kind", SdfShadeType.UInt, SdfShadePhase.Light), I("light.reach", phase: SdfShadePhase.Light),
        I("light.visibility", phase: SdfShadePhase.Light), I("light.occlusion", phase: SdfShadePhase.Light),
        I("light.probeShare", phase: SdfShadePhase.Light), I("light.cosine", phase: SdfShadePhase.Light),
        I("compose.radiance", SdfShadeType.Float3, SdfShadePhase.Compose), I("compose.rim", SdfShadeType.Float3, SdfShadePhase.Compose),
        I("compose.specular", SdfShadeType.Float3, SdfShadePhase.Compose), I("compose.attenuation", phase: SdfShadePhase.Compose),
        I("compose.indirect", SdfShadeType.Float3, SdfShadePhase.Compose), I("compose.reflection", SdfShadeType.Float3, SdfShadePhase.Compose),
        I("compose.environment", SdfShadeType.Float3, SdfShadePhase.Compose), I("compose.adaptation", phase: SdfShadePhase.Compose),
        I("compose.exposure", phase: SdfShadePhase.Compose),
    });

    /// <summary>Gets an operation by its opcode; rejects undefined opcodes.</summary>
    public static SdfShadeOpInfo Get(SdfShadeOp op) => Table.Single(predicate: x => (x.Op == op));
    /// <summary>Returns the exact width of a supported type.</summary>
    public static int Width(SdfShadeType type) => type switch {
        SdfShadeType.Float or SdfShadeType.UInt => 1,
        SdfShadeType.Float2 => 2,
        SdfShadeType.Float3 or SdfShadeType.UInt3 => 3,
        SdfShadeType.Float4 => 4,
        _ => throw new SdfShadeException(code: "PUCK127", message: "Unknown shade value type."),
    };
    /// <summary>Returns whether a type holds unsigned bits rather than floating-point values.</summary>
    public static bool IsUInt(SdfShadeType type) => (type is SdfShadeType.UInt or SdfShadeType.UInt3);

    /// <summary>Derives identity from the live table and the embedded helper sources, including their include closure.</summary>
    public static string Fingerprint { get; } = DescribeFingerprint();

    private static string DescribeFingerprint() {
        var text = new StringBuilder(value: "shade:binary32-rne-precise:uint32:quantize-floor-bitcast:sanitize-stage:v1\n");

        foreach (var op in Table) { text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{((uint)op.Op)}|{op.Name}|{op.Arity}|{op.Work}|{op.Template}|{op.Commutative}\n"); }
        foreach (var input in Inputs) { text.Append(System.Globalization.CultureInfo.InvariantCulture, $"{input.Name}|{((int)input.Type)}|{input.Phase}|{((uint)input.Reads)}|{input.AfterSurface}\n"); }
        var assembly = typeof(SdfShadeOps).Assembly;

        foreach (var name in assembly.GetManifestResourceNames().Where(predicate: n => n.StartsWith(comparisonType: StringComparison.Ordinal, value: "ShadeHelper.")).Order(comparer: StringComparer.Ordinal)) {
            using var stream = assembly.GetManifestResourceStream(name: name)!;
            using var reader = new StreamReader(stream: stream);

            text.Append(value: name).Append(value: '\n').Append(value: reader.ReadToEnd()).Append(value: '\n');
        }
        return Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text.ToString())));
    }
    private static SdfShadeOpInfo D(SdfShadeOp op, string name, int arity, uint work, string template, bool commutative = false) => new(Arity: arity, Commutative: commutative, Name: name, Op: op, Template: template, Work: checked((work + DispatchWork)));
    private static SdfShadeInput I(string name, SdfShadeType type = SdfShadeType.Float, SdfShadePhase? phase = null, SdfShadeReads reads = SdfShadeReads.None) => new(name, type, phase, reads);
}

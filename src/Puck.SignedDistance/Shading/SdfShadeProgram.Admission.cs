using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions;
using Puck.Abstractions.Counting;
using Puck.Assets;

namespace Puck.SignedDistance;

public sealed partial class SdfShadeProgram {
    /// <summary>Gets the requests <see cref="Compile"/> answered, compiled or reused.</summary>
    public static WorkKind CompileRequests { get; } = new(name: "sdf.shade.requests", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the definitions the expression compiler compiled: requests no earlier compile of the same source
    /// content answered.</summary>
    public static WorkKind CompileRuns { get; } = new(name: "sdf.shade.compiles", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the requests answered by an earlier compile of an equal source held by content.</summary>
    public static WorkKind CompileReuses { get; } = new(name: "sdf.shade.reused", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the process totals of <see cref="Compile"/>; read twice to measure a window.</summary>
    public static WorkCounterSet CompileWork { get; } = new(name: "sdf.shade", kinds: [CompileRequests, CompileRuns, CompileReuses]);

    // Distinct sources the content table retains. Each holds one admitted program or refusal.
    private const int HeldSources = 1024;
    // Deeper sources are compiled without a key; the compiler refuses them at its own lower depth bound.
    private const int MaxKeyDepth = 256;

    private sealed record Admission(SdfShadeProgram? Program, string? Code, string? Message);

    private static readonly CodeDerivedTable<SdfShadeDefinition, Admission> AdmittedSources = new();
    private static readonly Lock HeldGate = new();
    private static readonly ContentAddressedLruCache<ContentPin, Lazy<Admission>> HeldContent = new(capacity: HeldSources);
    private static int HeldRevision = CodeRevision.Current;

    /// <summary>Compiles expressions to canonical typed SSA, eliminates dead expressions, folds exact constants,
    /// interns common expressions and prices every live instruction before exposing a program. A source equal in
    /// content to one compiled before under the running code returns that program, or the same refusal, without
    /// compiling again, so a document change costs only the programs whose source it changed. A definition and its
    /// lists are treated as immutable once passed here.</summary>
    /// <param name="definition">The source expression graph.</param>
    /// <returns>The admitted program, shared by every request with equal source content.</returns>
    /// <exception cref="SdfShadeException">The source is malformed, references an undefined name, or exceeds a static
    /// capacity or work bound.</exception>
    public static SdfShadeProgram Compile(SdfShadeDefinition definition) {
        CompileWork.Count(kind: CompileRequests);
        var admission = (definition is null) ? Admit(definition: definition!) : AdmittedSources.GetValue(key: definition, derive: Held);

        return admission.Program ?? throw new SdfShadeException(code: admission.Code!, message: admission.Message!);
    }
    // The content door: equal source bytes under one code revision are one compile; concurrent requests (the
    // validator on the simulation thread, presentation on the frame thread) wait on the one compile in flight. The key
    // encodes every field the compiler reads (SourceKey), so a held admission is exactly what compiling the request
    // would produce.
    private static Admission Held(SdfShadeDefinition definition) {
        if (SourceKey.Of(definition: definition) is not { } key) { return Admit(definition: definition); }
        Lazy<Admission>? held;

        lock (HeldGate) {
            if (HeldRevision != CodeRevision.Current) { HeldContent.Clear(); HeldRevision = CodeRevision.Current; }
            if (HeldContent.TryGet(hash: key, value: out held)) {
                CompileWork.Count(kind: CompileReuses);
            } else {
                held = new(valueFactory: () => Admit(definition: definition), mode: LazyThreadSafetyMode.ExecutionAndPublication);
                HeldContent.Set(hash: key, value: held);
            }
        }
        return held.Value;
    }
    private static Admission Admit(SdfShadeDefinition definition) {
        CompileWork.Count(kind: CompileRuns);
        try { return new(Program: new Compiler(source: definition).Compile(), Code: null, Message: null); }
        catch (SdfShadeException error) { return new(Program: null, Code: error.Code, Message: error.Message); }
        catch (OverflowException) { return new(Program: null, Code: "PUCK129", Message: "Shade cost or structural arithmetic overflowed."); }
    }

    // An injective byte encoding of a source: two sources with equal bytes compile to the same program or refusal.
    // Every record is read by positional deconstruction, so a positional member added to any of these types fails to
    // compile here until the key encodes it. Expressions are written once per object and referenced by ordinal
    // afterwards, so the key is linear in distinct expressions however widely the source shares them; a source that
    // shares differently encodes differently and only misses the table.
    private sealed class SourceKey {
        private readonly ArrayBufferWriter<byte> m_bytes = new(initialCapacity: 1024);
        private readonly Dictionary<SdfShadeExpression, int> m_expressions = new(comparer: ReferenceEqualityComparer.Instance);
        private bool m_tooDeep;

        public static ContentPin? Of(SdfShadeDefinition definition) {
            var key = new SourceKey();
            var (name, parameters, outputs, values) = definition;

            key.Text(value: name);
            key.List(items: parameters, write: static (k, parameter) => {
                var (parameterName, type, bits) = parameter;
                k.Text(value: parameterName); k.Int(value: (int)type); k.Words(words: bits);
            });
            key.List(items: outputs, write: static (k, output) => {
                var (phase, outputName, value) = output;
                k.Int(value: (int)phase); k.Text(value: outputName); k.Expression(expression: value, depth: 0);
            });
            key.List(items: values, write: static (k, value) => {
                var (phase, valueName, expression) = value;
                k.Int(value: (int)phase); k.Text(value: valueName); k.Expression(expression: expression, depth: 0);
            });
            return key.m_tooDeep ? null : ContentPin.Compute(content: key.m_bytes.WrittenSpan);
        }
        private void Expression(SdfShadeExpression? expression, int depth) {
            if (expression is null) { Int(value: 0); return; }
            if (m_expressions.TryGetValue(key: expression, value: out var ordinal)) { Int(value: 2); Int(value: ordinal); return; }
            if (depth > MaxKeyDepth) { m_tooDeep = true; return; }
            m_expressions.Add(key: expression, value: m_expressions.Count);
            Int(value: 1);
            var (op, arguments, name, type, bits, shape) = expression;

            Text(value: op);
            if (arguments is null) { Int(value: -1); } else {
                Int(value: arguments.Count);
                foreach (var argument in arguments) { Expression(expression: argument, depth: (depth + 1)); }
            }
            Text(value: name); Int(value: (int)type); Words(words: bits);
            Shape(shape: shape);
        }
        private void Shape(SdfShadeShape? shape) {
            if (shape is null) { Int(value: 0); return; }
            Int(value: 1);
            var (type, x, y, z, taper, profile, lift, rounding, chamfer, exponent, curve) = shape;

            Int(value: (int)type); Float(value: x); Float(value: y); Float(value: z); Float(value: taper);
            Profile(profile: profile);
            Int(value: (int)lift); Float(value: rounding); Float(value: chamfer); Float(value: exponent);
            if (curve is not { } sweep) { Int(value: 0); } else {
                var (a, b, c, radiusStart, radiusEnd, bulge, strands, twist, strandOffset) = sweep;

                Int(value: 1); Vector(value: a); Vector(value: b); Vector(value: c); Float(value: radiusStart); Float(value: radiusEnd);
                Float(value: bulge); Int(value: strands); Float(value: twist); Float(value: strandOffset);
            }
        }
        private void Profile(SdfPrismProfile? profile) {
            if (profile is null) { Int(value: 0); return; }
            Int(value: 1);
            var (kind, cornerRadius, sides, vertices, path) = profile;

            Int(value: (int)kind); Float(value: cornerRadius); Int(value: sides);
            List(items: vertices, write: static (k, vertex) => k.Point(value: vertex));
            if (path is null) { Int(value: 0); return; }
            Int(value: 1);
            var (contours, tolerance, stroke, shear) = path;

            List(items: contours, write: static (k, contour) => {
                var (start, segments, closed) = contour;
                k.Point(value: start); k.Int(value: closed ? 1 : 0);
                k.List(items: segments, write: static (k, segment) => {
                    var (end, control, control2, arcCenter, clockwise) = segment;
                    k.Point(value: end); k.Point(value: control); k.Point(value: control2); k.Point(value: arcCenter);
                    k.Int(value: clockwise ? 1 : 0);
                });
            });
            Float(value: tolerance);
            if (stroke is null) { Int(value: 0); } else {
                var (radiusStart, radiusEnd, smooth, from, to) = stroke;

                Int(value: 1); Float(value: radiusStart); Float(value: radiusEnd); Int(value: smooth ? 1 : 0); Float(value: from); Float(value: to);
            }
            if (shear is null) { Int(value: 0); } else {
                var (linear, quadratic, cubic, offset, from, to, target) = shear;

                Int(value: 1); Float(value: linear); Float(value: quadratic); Float(value: cubic); Float(value: offset);
                Float(value: from); Float(value: to); Int(value: target);
            }
        }
        private void List<T>(IReadOnlyList<T?>? items, Action<SourceKey, T> write) {
            if (items is null) { Int(value: -1); return; }
            Int(value: items.Count);
            foreach (var item in items) {
                if (item is null) { Int(value: 0); } else { Int(value: 1); write(arg1: this, arg2: item); }
            }
        }
        private void Words(IReadOnlyList<uint>? words) {
            if (words is null) { Int(value: -1); return; }
            Int(value: words.Count);
            foreach (var word in words) { Int(value: unchecked((int)word)); }
        }
        private void Text(string? value) {
            if (value is null) { Int(value: -1); return; }
            Int(value: value.Length);
            foreach (var character in value) { Int(value: character); }
        }
        private void Point(Vector2? value) {
            if (value is not { } point) { Int(value: 0); return; }
            Int(value: 1); Float(value: point.X); Float(value: point.Y);
        }
        private void Vector(Vector3 value) { Float(value: value.X); Float(value: value.Y); Float(value: value.Z); }
        // Bits, not values: -0 and +0 (and every NaN payload) are distinct sources.
        private void Float(float value) => Int(value: BitConverter.SingleToInt32Bits(value: value));
        private void Int(int value) {
            BinaryPrimitives.WriteInt32LittleEndian(destination: m_bytes.GetSpan(sizeHint: 4), value: value);
            m_bytes.Advance(count: 4);
        }
    }
}

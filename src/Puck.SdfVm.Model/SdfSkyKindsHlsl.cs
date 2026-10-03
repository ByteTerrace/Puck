using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One sky layer kind as the kernels see it: its constant, its class, its parameter record's structure (the
/// record's generated HLSL structure and decoder) and the module that evaluates it.</summary>
/// <param name="Kind">The kind's value, its <c>SDF_SKY_KIND_*</c> constant.</param>
/// <param name="Name">The kind's name: its constant's suffix in upper snake case and its module's file stem.</param>
/// <param name="Class">The class the kind is evaluated in.</param>
/// <param name="Parameters">The parameter record's structure, read from its C# type.</param>
public sealed record SdfSkyKindDeclaration(SdfSkyLayerKind Kind, string Name, SdfSkyLayerClass Class, ShaderInterfaceStructure Parameters) {
    /// <summary>Gets the module's path, relative to the generated table in <c>sky/</c>: <c>kinds/&lt;name&gt;.hlsli</c>.</summary>
    public string Module => $"kinds/{Name}.hlsli";
    /// <summary>Gets the HLSL name of the function that decodes the kind's parameters from a layer:
    /// <c>&lt;structure&gt;Of</c> with a lower-case first letter.</summary>
    public string Decoder => $"{LowerFirst(text: Parameters.Name)}Of";
    /// <summary>Gets the HLSL name of the function the kind's module defines, which evaluates a layer:
    /// <c>&lt;structure&gt;Layer</c> with a lower-case first letter, taking the decoded parameters, the layer and the sample,
    /// and returning the layer's colour and alpha.</summary>
    public string Evaluator => $"{LowerFirst(text: Parameters.Name)}Layer";

    /// <summary>Declares a kind from its parameter record.</summary>
    /// <typeparam name="T">The kind's parameter record.</typeparam>
    /// <returns>The declaration.</returns>
    public static SdfSkyKindDeclaration Of<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields)] T>() where T : unmanaged, ISdfSkyKind => new(
        Class: T.Class,
        Kind: T.Kind,
        Name: T.Name,
        Parameters: ShaderInterfaceStructure.From<T>()
    );

    private static string LowerFirst(string text) => (char.ToLowerInvariant(c: text[0]) + text[1..]);
}
/// <summary>
/// The sky's kind table, the one list of the layer kinds the kernels switch on, and the generator of the two includes that
/// declare it. <see cref="FileName"/>, among the generated declarations, carries the sky's enums
/// (<see cref="SdfSkyLayerKind"/>, <see cref="SdfSkyBlend"/>, <see cref="SdfSkyMask"/>, <see cref="SdfSkyVisibility"/>,
/// <see cref="SdfSkyTier"/>, <see cref="SdfSkyPatternShape"/>, <see cref="SdfSkyProjection"/>) and capacities as
/// constants, whether each kind is a field kind, and per kind its parameter structure and the decoder that reads it from a
/// layer record's payload (<see cref="SdfSkyLayer.P0"/> to <see cref="SdfSkyLayer.P7"/>) by the C# field offsets.
/// <see cref="TableFileName"/>, in the sky's own layer, includes each kind's module (<c>sky/kinds/&lt;name&gt;.hlsli</c>)
/// and holds the evaluation switch. Each kind's lines are its own: adding a kind inserts lines and changes none of
/// another kind's, and no pass names a kind. Each text is a pure function of the table, with LF line endings.
/// <c>puck shaders generate</c> writes both, and the instruction set's fingerprint covers them
/// (<see cref="SdfIsaHlsl.DescribeFingerprint"/>).
/// </summary>
public static class SdfSkyKindsHlsl {
    /// <summary>The file name of the generated declarations, which sit with the other generated declarations in
    /// <c>Assets/Shaders/Sdf/isa</c>.</summary>
    public const string FileName = "sdf-sky-kinds.hlsli";
    /// <summary>The file name of the generated evaluation table, in the sky's layer, <c>Assets/Shaders/Sdf/sky</c>.</summary>
    public const string TableFileName = "sdf-sky-kind-table.hlsli";

    private const string Guard = "SDF_SKY_KINDS_HLSLI";
    private const string TableGuard = "SDF_SKY_KIND_TABLE_HLSLI";

    /// <summary>Gets the kind table: every <see cref="SdfSkyLayerKind"/>, each declared from its parameter record.</summary>
    public static IReadOnlyList<SdfSkyKindDeclaration> Kinds { get; } = [
        SdfSkyKindDeclaration.Of<SdfSkyGradient>(),
        SdfSkyKindDeclaration.Of<SdfSkyStars>(),
        SdfSkyKindDeclaration.Of<SdfSkyClouds>(),
        SdfSkyKindDeclaration.Of<SdfSkyAurora>(),
        SdfSkyKindDeclaration.Of<SdfSkyNoise>(),
        SdfSkyKindDeclaration.Of<SdfSkyPattern>(),
        SdfSkyKindDeclaration.Of<SdfSkyPanorama>(),
        SdfSkyKindDeclaration.Of<SdfSkyDisc>(),
    ];

    /// <summary>Generates the include for the kind table.</summary>
    /// <returns>The HLSL text.</returns>
    public static string Generate() => Generate(kinds: Kinds);
    /// <summary>Generates the include for a kind table.</summary>
    /// <param name="kinds">The kinds, each value and name once, each parameter record within a layer's payload.</param>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kinds"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two kinds share a value or a name, or a record outgrows the payload.</exception>
    public static string Generate(IReadOnlyList<SdfSkyKindDeclaration> kinds) {
        ArgumentNullException.ThrowIfNull(argument: kinds);

        if (
            (kinds.Select(selector: static kind => kind.Kind).Distinct().Count() != kinds.Count) ||
            (kinds.Select(selector: static kind => kind.Name).Distinct(comparer: StringComparer.Ordinal).Count() != kinds.Count)
        ) {
            throw new InvalidOperationException(message: "Two sky kinds share a value or a name.");
        }

        var text = new StringBuilder();

        void Line(string line = "") => text.Append(value: line).Append(value: '\n');
        void Define(string name, long value) => Line(line: string.Create(provider: CultureInfo.InvariantCulture, handler: $"#define {name} {value}u"));
        void Members<T>(string prefix) where T : struct, Enum {
            Line(line: $"// {typeof(T).FullName}.");
            foreach (var member in Enum.GetValues<T>()) {
                Define(name: $"{prefix}_{SdfIsaHlsl.UpperSnake(name: member.ToString())}", value: Convert.ToInt64(provider: CultureInfo.InvariantCulture, value: member));
            }
            Line();
        }

        Line(line: "// Generated by `puck shaders generate` from the sky's kind table (SdfSkyKindsHlsl.Kinds); regenerate it, never edit it.");
        Line(line: $"#ifndef {Guard}");
        Line(line: $"#define {Guard}");
        Line();
        Line(line: "// The sky's capacities.");
        Define(name: "SDF_SKY_MAX_LAYERS", value: SdfSky.MaxLayers);
        Define(name: "SDF_SKY_MAX_STOPS", value: SdfSky.MaxStops);
        Define(name: "SDF_SKY_MAX_UPPER_FIELD_RUNS", value: SdfSky.MaxUpperFieldRuns);
        Define(name: "SDF_SKY_DETAIL_RUNS", value: SdfSkyDetails.Runs);
        Define(name: "SDF_SKY_DETAIL_ATMOSPHERE", value: SdfSkyDetails.AtmosphereRow);
        Line();
        Members<SdfSkyBlend>(prefix: "SDF_SKY_BLEND");
        Members<SdfSkyMask>(prefix: "SDF_SKY_MASK");
        Members<SdfSkyVisibility>(prefix: "SDF_SKY_VISIBILITY");
        Members<SdfSkyTier>(prefix: "SDF_SKY_TIER");
        Members<SdfSkyPatternShape>(prefix: "SDF_SKY_PATTERN");
        Members<SdfSkyProjection>(prefix: "SDF_SKY_PROJECTION");
        Line(line: "// The kinds.");
        foreach (var kind in kinds) {
            Define(name: ConstantOf(kind: kind), value: ((long)kind.Kind));
        }
        Line();
        Line(line: "// Whether a kind is a field kind, evaluated by the sky pass at its field extent and summarized with its run; a point");
        Line(line: "// kind is evaluated by the composite at each pixel.");
        Line(line: "bool sdfSkyKindIsField(uint kind) {");
        Line(line: "    switch (kind) {");
        foreach (var kind in kinds.Where(predicate: static kind => (kind.Class == SdfSkyLayerClass.Field))) {
            Line(line: $"    case {ConstantOf(kind: kind)}:");
        }
        Line(line: "        return true;");
        Line(line: "    default:");
        Line(line: "        return false;");
        Line(line: "    }");
        Line(line: "}");
        foreach (var kind in kinds) {
            Line();
            DeclareKind(kind: kind, line: Line);
        }
        Line();
        Line(line: $"#endif // {Guard}");

        return text.ToString();
    }
    /// <summary>Generates the evaluation table for the kind table.</summary>
    /// <returns>The HLSL text.</returns>
    public static string GenerateTable() => GenerateTable(kinds: Kinds);
    /// <summary>Generates the evaluation table for a kind table: each kind's module included, and the switch that evaluates a
    /// layer by its kind's module. Include it from the sky's shared declarations (<c>sky/sdf-sky.hlsli</c>), which declare
    /// what each module reads.</summary>
    /// <param name="kinds">The kinds, each value and name once.</param>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="kinds"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Two kinds share a value or a name.</exception>
    public static string GenerateTable(IReadOnlyList<SdfSkyKindDeclaration> kinds) {
        ArgumentNullException.ThrowIfNull(argument: kinds);

        if (
            (kinds.Select(selector: static kind => kind.Kind).Distinct().Count() != kinds.Count) ||
            (kinds.Select(selector: static kind => kind.Name).Distinct(comparer: StringComparer.Ordinal).Count() != kinds.Count)
        ) {
            throw new InvalidOperationException(message: "Two sky kinds share a value or a name.");
        }

        var text = new StringBuilder();

        void Line(string line = "") => text.Append(value: line).Append(value: '\n');

        Line(line: "// Generated by `puck shaders generate` from the sky's kind table (SdfSkyKindsHlsl.Kinds); regenerate it, never edit it.");
        Line(line: "// Included by sky/sdf-sky.hlsli after the declarations each kind's module reads.");
        Line(line: $"#ifndef {TableGuard}");
        Line(line: $"#define {TableGuard}");
        Line();
        foreach (var kind in kinds) {
            Line(line: $"#include \"{kind.Module}\"");
        }
        Line();
        Line(line: "// A layer's colour and alpha at a sample, by its kind's module; a kind the table does not hold draws nothing.");
        Line(line: "float4 sdfSkyKindEvaluate(SdfSkyLayer layer, SdfSkySample sample) {");
        Line(line: "    switch (layer.Kind) {");
        foreach (var kind in kinds) {
            Line(line: $"    case {ConstantOf(kind: kind)}:");
            Line(line: $"        return {kind.Evaluator}({kind.Decoder}(layer), layer, sample);");
        }
        Line(line: "    default:");
        Line(line: "        return float4(0.0, 0.0, 0.0, 0.0);");
        Line(line: "    }");
        Line(line: "}");
        Line();
        Line(line: $"#endif // {TableGuard}");

        return text.ToString();
    }
    /// <summary>Returns the HLSL expression a decoder reads one parameter field with: the payload lanes at the field's C#
    /// offset, reinterpreted for an integer field.</summary>
    /// <param name="member">The parameter field.</param>
    /// <returns>The expression, over a record named <c>layer</c>.</returns>
    /// <exception cref="InvalidOperationException">The field lies outside the payload or across two of its vectors.</exception>
    public static string LanesOf(ShaderInterfaceBlockMember member) {
        var word = ((int)(member.Offset / ShaderValueTypes.ComponentBytes));
        var components = ((int)member.Type.ComponentCount());
        var vector = (word / 4);
        var lane = (word % 4);

        if (((member.Offset + member.Type.SizeBytes()) > SdfSkyLayer.PayloadBytes) || ((lane + components) > 4)) {
            throw new InvalidOperationException(message: $"A sky kind's field '{member.Name}' at byte {member.Offset} does not lie within one of the payload's vectors.");
        }

        var lanes = $"layer.P{vector.ToString(provider: CultureInfo.InvariantCulture)}.{"xyzw".Substring(length: components, startIndex: lane)}";

        return member.Type.ScalarKind() switch {
            ShaderScalarKind.Uint => $"asuint({lanes})",
            ShaderScalarKind.Int => $"asint({lanes})",
            _ => lanes,
        };
    }

    // A kind's lines: its structure and its decoder.
    private static void DeclareKind(SdfSkyKindDeclaration kind, Action<string> line) {
        var structure = kind.Parameters;

        if (structure.SizeBytes > SdfSkyLayer.PayloadBytes) {
            throw new InvalidOperationException(message: $"The {kind.Name} kind's parameters take {structure.SizeBytes} bytes; a layer's payload holds {SdfSkyLayer.PayloadBytes}.");
        }

        line($"// The {kind.Name} kind ({structure.Name}), a {kind.Class.ToString().ToLowerInvariant()} kind.");
        line($"struct {structure.Name} {{");
        foreach (var member in structure.Members) {
            line($"    {member.Type.Spelling()} {member.Name};");
        }
        line("};");
        line($"{structure.Name} {kind.Decoder}(SdfSkyLayer layer) {{");
        line($"    {structure.Name} parameters;");
        foreach (var member in structure.Members) {
            line($"    parameters.{member.Name} = {LanesOf(member: member)};");
        }
        line("    return parameters;");
        line("}");
    }
    private static string ConstantOf(SdfSkyKindDeclaration kind) => $"SDF_SKY_KIND_{SdfIsaHlsl.UpperSnake(name: string.Concat(str0: char.ToUpperInvariant(c: kind.Name[0]).ToString(), str1: kind.Name[1..]))}";
}

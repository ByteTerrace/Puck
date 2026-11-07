using System.Collections.ObjectModel;

namespace Puck.SignedDistance;

/// <summary>The scalar kind and width of a shade register; there are no implicit integer conversions.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<SdfShadeType>))]
public enum SdfShadeType { Float = 1, Float2 = 2, Float3 = 3, Float4 = 4, UInt = 5, UInt3 = 7 }
/// <summary>The three entry points of a material's presentation program.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<SdfShadePhase>))]
public enum SdfShadePhase { Surface, Light, Compose }
/// <summary>Inputs whose reads affect frame uploads and cadence, derived only from live canonical ops.</summary>
[Flags]
public enum SdfShadeReads : uint {
    None = 0, Clock = 1, Contact = 2, Footprint = 4,
    Slot0 = 8, Slot1 = 16, Slot2 = 32, Slot3 = 64,
    World0 = 128, World1 = 256, World2 = 512, World3 = 1024,
    WorldLanes = World0 | World1 | World2 | World3,
}
/// <summary>An expression supplied to shade admission, never executable words. The compiler validates every field.</summary>
/// <param name="Op">An op-table spelling, or constant, parameter, or input.</param>
/// <param name="Arguments">The expression's operands.</param>
/// <param name="Name">A stamp member or runtime parameter name.</param>
/// <param name="Type">The type of a literal, validated against its bits.</param>
/// <param name="Bits">32-bit literal components; integer components are never float converted.</param>
/// <param name="Shape">The structural descriptor of a shape operation.</param>
public sealed record SdfShadeExpression(string Op, IReadOnlyList<SdfShadeExpression>? Arguments = null,
    string? Name = null, SdfShadeType Type = SdfShadeType.Float, IReadOnlyList<uint>? Bits = null, SdfShadeShape? Shape = null);
/// <summary>A structural shape descriptor. Compilation uses the existing solid/paint emission and admission.</summary>
public sealed record SdfShadeShape(SdfSolidPrimitive Type, float X = 1f, float Y = 1f, float Z = 1f,
    float Taper = 0.5f, SdfPrismProfile? Profile = null, SdfLift Lift = SdfLift.Extrude,
    float Rounding = 0f, float Chamfer = 0f, float Exponent = SdfProgramBuilder.MinSuperellipsoidExponent,
    SdfSweepParameters? Curve = null) {
    internal SdfPaintShape Capture() => SdfPaintShape.Of(Type, new(x: X, y: Y, z: Z), Taper, Profile, Lift, Rounding, Chamfer, Exponent, Curve);
}
/// <summary>A runtime slot's type and initial data. Names bind source references and do not enter program identity.</summary>
public sealed record SdfShadeParameter(string Name, SdfShadeType Type, IReadOnlyList<uint> Bits);
/// <summary>An output expression; omitted outputs retain the stage's default.</summary>
public sealed record SdfShadeOutput(SdfShadePhase Phase, string Name, SdfShadeExpression Value);
/// <summary>The source-level expression graph serialized in a material. Admission always compiles it afresh.</summary>
public sealed record SdfShadeDefinition(string Name, IReadOnlyList<SdfShadeParameter> Parameters, IReadOnlyList<SdfShadeOutput> Outputs,
    IReadOnlyList<SdfShadeValue>? Values = null);
/// <summary>A dependency-ordered value in the canonical document. Reads may name only earlier values of this phase.</summary>
public sealed record SdfShadeValue(SdfShadePhase Phase, string Name, SdfShadeExpression Value);
/// <summary>A typed, slice-relative address. Only the compiler creates operands.</summary>
public readonly record struct SdfShadeOperand {
    internal SdfShadeOperand(int index, SdfShadeType type) { Index = index; Type = type; }

    /// <summary>Gets the register or vector offset within this program's own slice.</summary>
    public int Index { get; }
    /// <summary>Gets the value type at the address.</summary>
    public SdfShadeType Type { get; }
}
/// <summary>An immutable instruction admitted by the compiler, in dependency order.</summary>
public sealed class SdfShadeInstruction {
    internal SdfShadeInstruction(SdfShadeOp op, SdfShadeOperand result, SdfShadeOperand[] operands, uint immediate) {
        Op = op; Result = result; Operands = Array.AsReadOnly(array: operands); Immediate = immediate;
    }

    /// <summary>Gets the table operation.</summary>
    public SdfShadeOp Op { get; }
    /// <summary>Gets the result register.</summary>
    public SdfShadeOperand Result { get; }
    /// <summary>Gets the typed source registers, all defined earlier in this phase.</summary>
    public ReadOnlyCollection<SdfShadeOperand> Operands { get; }
    /// <summary>Gets the typed op's static selector or slice-relative constant offset.</summary>
    public uint Immediate { get; }
}
/// <summary>A shade admission refusal with the diagnostic the author sees.</summary>
public sealed class SdfShadeException(string code, string message) : ArgumentException(message) {
    /// <summary>Gets the compiler diagnostic code.</summary>
    public string Code { get; } = code;
}

namespace Puck.SignedDistance;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class SdfFieldEntryAttribute(int id) : Attribute {
    public int Id { get; } = id;
}

[AttributeUsage(AttributeTargets.Class)]
internal sealed class SdfFieldCatalogueAttribute(string name, bool unsigned) : Attribute {
    public string Name { get; } = name;
    public bool Unsigned { get; } = unsigned;
}

/// <summary>Instruction payload vectors a shader action reads.</summary>
[Flags]
public enum SdfShaderOperands {
    None = 0,
    Data0 = 1,
    Data1 = 2,
}

/// <summary>A shader dispatch arm and the payload vectors it decodes before executing.</summary>
public readonly record struct SdfShaderAction(string? Code, SdfShaderOperands Operands) {
    /// <summary>The reader does not admit this entry.</summary>
    public static SdfShaderAction Unavailable => new(Code: null, Operands: SdfShaderOperands.None);

    /// <summary>Gets whether the arm keeps the gradient walk's derivatives (SDF_FIELD_DUAL: the local point's Jacobian and
    /// the running field's gradient) true to what it does; an arm that does not marks the sample uncovered, which then
    /// takes its gradient from the four-tap stencil.</summary>
    public bool Dual { get; init; }

    // The arm that calls a shared formula's generated function with the entry's operands.
    internal static SdfShaderAction Of(SdfFormula formula) =>
        new(Code: $"result = {formula.Dispatch(shader: true)}; break;", Operands: SdfShaderOperands.None);

    // The walk state a stream formula reads and writes, under the names each kernel reader holds it by. The map reader
    // decodes an instruction's data vectors only in the arms that read them; the rigid and part readers hold both.
    private static readonly (string State, string Held)[] MapState = [("p", "localPosition"), ("distance", "result.distance")];
    private static readonly (string State, string Held)[] RigidState = [("p", "p")];
    // The same state carried with its derivatives, for the gradient walk's overloads of each formula.
    private static readonly (string State, string Held)[] DualState = [
        ("p", "sdfdMake(localPosition, localJacobian)"), ("distance", "sdfdMake(result.distance, result.gradient)"),
    ];

    /// <summary>The map reader's arm that writes each named state from its shared formula.</summary>
    internal static SdfShaderAction Map(params (string State, SdfFormula Formula)[] writes) => Write(MapState, loads: true, dual: true, writes);
    /// <summary>The rigid fold reader's arm that writes each named state from its shared formula. The gradient walk's fold
    /// reader holds its point as a dual, so the same call resolves to the formula's dual overload.</summary>
    internal static SdfShaderAction Rigid(params (string State, SdfFormula Formula)[] writes) => Write(RigidState, loads: false, dual: false, writes);
    /// <summary>The part reader's arm that writes each named state from its shared formula.</summary>
    internal static SdfShaderAction Part(params (string State, SdfFormula Formula)[] writes) => Write(MapState, loads: false, dual: true, writes);

    /// <summary>The same arm with hand-written statements ahead of it: a guard, or a value the formula reads.</summary>
    internal SdfShaderAction After(string statements, SdfShaderOperands operands = SdfShaderOperands.None) =>
        new(Code: statements.TrimEnd('\n') + "\n" + Code, Operands: Operands | operands) { Dual = Dual };

    // A dual arm writes the point and the running distance through the formula's dual overload (SdfFormula's dual
    // lowering) under SDF_FIELD_DUAL, and every other state as the plain reader does.
    private static SdfShaderAction Write((string State, string Held)[] held, bool loads, bool dual, (string State, SdfFormula Formula)[] writes) {
        string Plain((string State, SdfFormula Formula) write) =>
            $"{held.FirstOrDefault(name => name.State == write.State).Held ?? write.State} = {write.Formula.Dispatch(shader: true, held)};\n";
        var code = string.Concat(writes.Select(Plain));
        if (dual) {
            var derived = string.Concat(writes.Select(write => write.State switch {
                "p" => $"{{ SdfDual3 sdfDualMapped = {write.Formula.Dispatch(shader: true, DualState)}; localPosition = sdfDualMapped.v; localJacobian = sdfDualMapped.d; }}\n",
                "distance" => $"{{ SdfDual sdfDualEdited = {write.Formula.Dispatch(shader: true, DualState)}; result.distance = sdfDualEdited.v; result.gradient = sdfDualEdited.d; }}\n",
                _ => Plain(write),
            }));
            code = "#ifdef SDF_FIELD_DUAL\n" + derived + "#else\n" + code + "#endif\n";
        }
        var operands = !loads ? SdfShaderOperands.None
            : (code.Contains("data0") ? SdfShaderOperands.Data0 : SdfShaderOperands.None) | (code.Contains("data1") ? SdfShaderOperands.Data1 : SdfShaderOperands.None);
        return new(Code: code + "break;", Operands: operands) { Dual = true };
    }
}

/// <summary>Inputs outside the instruction's own data vectors that a field entry reads.</summary>
[Flags]
public enum SdfFieldResource {
    /// <summary>The instruction has no external dependency.</summary>
    None = 0,
    /// <summary>Clears the active dynamic slot.</summary>
    ResetSlot = 1,
    /// <summary>Reads and selects the dynamic slot named in the instruction.</summary>
    DynamicSlot = 2,
    /// <summary>Reads lanes from the active dynamic slot.</summary>
    SlotLanes = 4,
    /// <summary>Reads the primitive's resource dependencies.</summary>
    Shapes = 8,
    /// <summary>Reads its preceding relief mask rows.</summary>
    ReliefRows = 16,
    /// <summary>Reads the glyph atlas.</summary>
    GlyphAtlas = 32,
    /// <summary>Reads a sampled snapshot by handle.</summary>
    SampledHandle = 64,
    /// <summary>Reads its shape's side table.</summary>
    SideTable = 128,
    /// <summary>Reads the composition blend's resource dependencies.</summary>
    Blend = 256,
}

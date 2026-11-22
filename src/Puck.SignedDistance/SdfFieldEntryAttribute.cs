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

    // The arm that calls a shared formula's generated function with the entry's operands.
    internal static SdfShaderAction Of(SdfFormula formula) =>
        new(Code: $"result = {formula.Dispatch(shader: true)}; break;", Operands: SdfShaderOperands.None);

    // The walk state a stream formula reads and writes, under the names each kernel reader holds it by. The map reader
    // decodes an instruction's data vectors only in the arms that read them; the rigid and part readers hold both.
    private static readonly (string State, string Held)[] MapState = [("p", "localPosition"), ("distance", "result.distance")];
    private static readonly (string State, string Held)[] RigidState = [("p", "p")];

    /// <summary>The map reader's arm that writes each named state from its shared formula.</summary>
    internal static SdfShaderAction Map(params (string State, SdfFormula Formula)[] writes) => Write(MapState, loads: true, writes);
    /// <summary>The rigid fold reader's arm that writes each named state from its shared formula.</summary>
    internal static SdfShaderAction Rigid(params (string State, SdfFormula Formula)[] writes) => Write(RigidState, loads: false, writes);
    /// <summary>The part reader's arm that writes each named state from its shared formula.</summary>
    internal static SdfShaderAction Part(params (string State, SdfFormula Formula)[] writes) => Write(MapState, loads: false, writes);

    /// <summary>The same arm with hand-written statements ahead of it: a guard, or a value the formula reads.</summary>
    internal SdfShaderAction After(string statements, SdfShaderOperands operands = SdfShaderOperands.None) =>
        new(Code: statements.TrimEnd('\n') + "\n" + Code, Operands: Operands | operands);

    private static SdfShaderAction Write((string State, string Held)[] held, bool loads, (string State, SdfFormula Formula)[] writes) {
        var code = string.Concat(writes.Select(write =>
            $"{held.FirstOrDefault(name => name.State == write.State).Held ?? write.State} = {write.Formula.Dispatch(shader: true, held)};\n"));
        var operands = !loads ? SdfShaderOperands.None
            : (code.Contains("data0") ? SdfShaderOperands.Data0 : SdfShaderOperands.None) | (code.Contains("data1") ? SdfShaderOperands.Data1 : SdfShaderOperands.None);
        return new(Code: code + "break;", Operands: operands);
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

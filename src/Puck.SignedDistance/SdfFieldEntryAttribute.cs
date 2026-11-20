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

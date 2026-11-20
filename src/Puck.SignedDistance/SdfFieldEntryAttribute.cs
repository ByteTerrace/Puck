namespace Puck.SignedDistance;

[AttributeUsage(AttributeTargets.Class)]
internal sealed class SdfFieldEntryAttribute(int id, string discriminator) : Attribute {
    public int Id { get; } = id;
    public string Discriminator { get; } = discriminator;
}

/// <summary>Instruction payload vectors a shader action reads.</summary>
[Flags]
public enum SdfShaderOperands {
    None = 0,
    Data0 = 1,
    Data1 = 2,
}

/// <summary>A shader dispatch arm and the payload vectors it decodes before executing.</summary>
public readonly record struct SdfShaderAction(string Code, SdfShaderOperands Operands);

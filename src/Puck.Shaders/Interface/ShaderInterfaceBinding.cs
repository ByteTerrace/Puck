using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>One member of a constant block as a layout places it: its name, its byte offset from the start of the
/// block, the type it is stored as, and its element count.</summary>
/// <param name="Name">The member's name, including the generated padding members, which are named
/// <c>_pad</c> followed by their offset.</param>
/// <param name="Offset">The byte offset from the start of the block.</param>
/// <param name="Type">The type the member is stored as. An array element is stored as a whole 16-byte row, so an
/// array of <c>uint</c> is stored as <c>uint4</c> elements.</param>
/// <param name="Length">The element count of an array member, or zero for a member that is not an array.</param>
public readonly record struct ShaderInterfaceBlockMember(
    string Name,
    uint Offset,
    ShaderValueType Type,
    uint Length
);
/// <summary>One descriptor binding as a layout places it, in the one shape the generator's expectation and both
/// bytecode readers share, so a single comparison can hold a SPIR-V module and a DXIL container to the same
/// interface.</summary>
/// <param name="Name">The shader variable's name.</param>
/// <param name="Set">The Vulkan descriptor set, which is also the Direct3D 12 register space.</param>
/// <param name="Binding">The Vulkan binding number, which is also the Direct3D 12 register number.</param>
/// <param name="Kind">The binding kind.</param>
/// <param name="Members">The constant block's members in offset order; empty for every other kind.</param>
/// <param name="Pushed">Whether the constant block is delivered as push constants rather than bound: a SPIR-V
/// <c>PushConstant</c> variable, which carries no set or binding and is placed at set 0, binding 0. A DXIL container
/// cannot tell root constants from a bound constant buffer, so its reader never sets it.</param>
/// <param name="ElementStride">The byte stride of a buffer's elements as the bytecode reflects it: a SPIR-V buffer's
/// runtime-array <c>ArrayStride</c>, or a DXIL buffer's <c>D3D12_SHADER_INPUT_BIND_DESC.NumSamples</c>. A structured
/// buffer's stride is its element's size on both backends; a raw buffer's is whatever each backend reports for a
/// byte-address buffer (<see cref="ShaderInterfaceLayout.SpirvRawBufferStride"/>,
/// <see cref="ShaderInterfaceLayout.DxilRawBufferStride"/>). Zero for every binding that is not a buffer.</param>
public sealed record ShaderInterfaceBinding(
    string Name,
    uint Set,
    uint Binding,
    GpuBindingKind Kind,
    IReadOnlyList<ShaderInterfaceBlockMember> Members,
    bool Pushed = false,
    uint ElementStride = 0
) {
    /// <inheritdoc/>
    /// <remarks>Compares <see cref="Members"/> element by element rather than by reference.</remarks>
    public bool Equals(ShaderInterfaceBinding? other) =>
        ((other is not null) &&
        string.Equals(
            a: Name,
            b: other.Name,
            comparisonType: StringComparison.Ordinal
        ) &&
        (Set == other.Set) &&
        (Binding == other.Binding) &&
        (Kind == other.Kind) &&
        (Pushed == other.Pushed) &&
        (ElementStride == other.ElementStride) &&
        Members.SequenceEqual(second: other.Members));
    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            value1: Name,
            value2: Set,
            value3: Binding,
            value4: Kind,
            value5: Pushed,
            value6: ElementStride,
            value7: Members.Count
        );
    /// <inheritdoc/>
    public override string ToString() =>
        $"{Name} set {Set} binding {Binding} {(Pushed ? "pushed " : "")}{Kind}{((ElementStride == 0) ? "" : $" stride {ElementStride}")}{((Members.Count == 0)
            ? ""
            : $" [{string.Join(separator: ", ", values: Members.Select(selector: static member => $"{member.Name}@{member.Offset}:{member.Type.Spelling()}{((member.Length == 0) ? "" : $"[{member.Length}]")}"))}]")}";

    // A typed buffer (Buffer<T> or RWBuffer<T>; a SPIR-V image of Dim Buffer) is a texel buffer, which Vulkan binds as a
    // uniform or storage texel buffer rather than a storage buffer. No binding kind carries one, so both readers refuse
    // it by name rather than report it as a buffer kind a layout would plan as a storage buffer.
    internal static InvalidDataException TypedBuffer(string name, string reader) =>
        new(message: $"{reader} binding '{name}' is a typed buffer (Buffer<T> or RWBuffer<T>), which no binding kind carries; declare a StructuredBuffer or a ByteAddressBuffer.");
}

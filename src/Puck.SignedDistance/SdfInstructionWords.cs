using System.Numerics;
using System.Runtime.InteropServices;

namespace Puck.SignedDistance;

/// <summary>An instruction's lanes as the bytes a content hash reads, with no padding: its op, shape, blend and material,
/// both data vectors and the detail flag as a word. Two instructions give the same bytes only when every lane is the same
/// bit for bit, so a hash over a span of these (<see cref="MemoryMarshal.AsBytes{T}(ReadOnlySpan{T})"/>) keys a stream of
/// instructions exactly as a reader of them sees it: the mesh geometry's key and the program's field-input key alike.</summary>
/// <param name="instruction">The instruction.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SdfInstructionWords(in SdfInstruction instruction) {
    private readonly uint m_op = ((uint)instruction.Op);
    private readonly uint m_shape = instruction.Shape;
    private readonly uint m_blend = instruction.Blend;
    private readonly uint m_material = instruction.Material;
    private readonly Vector4 m_data0 = instruction.Data0;
    private readonly Vector4 m_data1 = instruction.Data1;
    private readonly uint m_detail = (instruction.Detail ? 1u : 0u);
}

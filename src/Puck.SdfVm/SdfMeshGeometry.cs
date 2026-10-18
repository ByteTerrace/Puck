using System.IO.Hashing;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The geometry a baked mesh group was sampled from (<see cref="SdfMeshGeometryBuilder"/>): whether it bakes in a
/// dynamic slot's frame, and a 128-bit hash of what determines its surface there.</summary>
/// <param name="Dynamic">Whether the group bakes in its slot's frame rather than in world space.</param>
/// <param name="Hash">The hash of the group's geometry.</param>
public readonly record struct SdfMeshGeometry(bool Dynamic, UInt128 Hash);

/// <summary>
/// Hashes what determines a bake group's surface, independent of where its program places it, so one body carried by two
/// worlds' programs, or by one world's program before and after a rebuild, hashes alike (<see cref="SdfMeshLibrary"/>):
/// <list type="bullet">
/// <item>each instruction's fields, a side-table entry's values (<see cref="SdfProgram.SideTableValues"/>) in place of the
/// offset its program packed the entry at;</item>
/// <item>an instance's shapes' materials by their place among the group's materials (<see cref="Materials"/>), since a
/// program numbers its palette in the order its emitters register materials;</item>
/// <item>a static instance's bound. A dynamic instance's instructions are in its slot's frame and determine its surface
/// there, while its bound is offset along world axes by the slot's pose when the program was emitted;</item>
/// <item>a dynamic instance's transform into its own slot by that role rather than the slot's number, which each program
/// assigns.</item>
/// </list>
/// </summary>
/// <param name="dynamic">Whether the group bakes in its slot's frame.</param>
public sealed class SdfMeshGeometryBuilder(bool dynamic) {
    private readonly XxHash128 m_hash = new();
    private readonly List<int> m_materials = [];

    /// <summary>Gets the materials the appended instances' shapes carry, in the order the geometry numbers them.</summary>
    public IReadOnlyList<int> Materials => m_materials;
    /// <summary>Gets the geometry of everything appended so far.</summary>
    public SdfMeshGeometry Geometry => new(Dynamic: dynamic, Hash: m_hash.GetCurrentHashAsUInt128());

    /// <summary>Appends one instance of a program: its instructions, each shape's material by its place among the
    /// group's materials, and its bound when it is static.</summary>
    /// <param name="program">The program.</param>
    /// <param name="instance">The instance's index in <see cref="SdfProgram.Instances"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="instance"/> names no instance.</exception>
    public void AppendInstance(SdfProgram program, int instance) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var range = program.Instances[instance];
        var bound = (range.IsDynamic ? Vector4.Zero : new Vector4(value: range.Center, w: range.Radius));
        var count = (range.End - range.First);

        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<Vector4>(in bound)));
        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<int>(in count)));

        for (var index = range.First; (index < range.End); index++) {
            var instruction = program.Instructions[index];

            if (instruction.Op == SdfOp.ShapeBlend) {
                var material = (int)instruction.Material;
                var place = m_materials.IndexOf(item: material);

                if (place < 0) {
                    place = m_materials.Count;
                    m_materials.Add(item: material);
                }
                instruction = (instruction with { Material = (uint)place });
            } else if (range.IsDynamic && (instruction.Op == SdfOp.TransformDynamic) && (instruction.Data0.X == range.Slot)) {
                instruction = (instruction with { Data0 = (instruction.Data0 with { X = -1f }) });
            }
            Append(index: index, instruction: instruction, program: program);
        }
    }
    /// <summary>Appends the reach a group's box is clamped to: a group whose bound passes the frame's far distance, or the
    /// group of the segments no instance owns, bakes over a box that reach wide, so the same instructions under another
    /// reach mesh another surface.</summary>
    /// <param name="reach">The reach, in world units.</param>
    public void AppendReach(float reach) => m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(in reach)));
    /// <summary>Appends one instruction of a program as it stands, its material included.</summary>
    /// <param name="program">The program.</param>
    /// <param name="index">The instruction's index in <see cref="SdfProgram.Instructions"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> names no instruction.</exception>
    public void AppendInstruction(SdfProgram program, int index) {
        ArgumentNullException.ThrowIfNull(argument: program);

        Append(index: index, instruction: program.Instructions[index], program: program);
    }

    private void Append(SdfProgram program, int index, SdfInstruction instruction) {
        var table = (((instruction.Op == SdfOp.ShapeBlend) && ((SdfShapeType)instruction.Shape is SdfShapeType.ConvexPolygon or SdfShapeType.Sweep or SdfShapeType.Path))
            ? program.SideTableValues(instructionIndex: index)
            : []);

        if (!table.IsEmpty) {
            instruction = (instruction with { Data0 = (instruction.Data0 with { X = 0f }) });
            m_hash.Append(source: MemoryMarshal.AsBytes(span: table));
        }

        var words = new InstructionWords(instruction: instruction);

        m_hash.Append(source: MemoryMarshal.AsBytes(span: new ReadOnlySpan<InstructionWords>(in words)));
    }

    // An instruction's fields as the bytes the geometry hashes.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct InstructionWords(in SdfInstruction instruction) {
        private readonly uint m_op = (uint)instruction.Op;
        private readonly uint m_shape = instruction.Shape;
        private readonly uint m_blend = instruction.Blend;
        private readonly uint m_material = instruction.Material;
        private readonly Vector4 m_data0 = instruction.Data0;
        private readonly Vector4 m_data1 = instruction.Data1;
        private readonly uint m_flags = ((instruction.Detail ? 1u : 0u) | (instruction.Secondary ? 2u : 0u));
    }
}

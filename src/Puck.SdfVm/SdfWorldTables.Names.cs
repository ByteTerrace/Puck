using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    /// <summary>The owner every object the tables and their pipeline set create is named under
    /// (<see cref="GpuObjectName.Owner"/>): the tables, images, descriptor sets and command pools by their role, and the
    /// pipelines by their kernel's pipeline name.</summary>
    internal const string ObjectOwner = "sdf.world";

    private const uint FrameGroup = ((uint)ShaderInterfaceGroup.Frame);
    private const uint PassGroup = ((uint)ShaderInterfaceGroup.Pass);

    // The debug name of one of the tables' objects: its role, a detail within the role, and its ring slot or item.
    private static GpuObjectName NameOf(string part, string? detail = null, int index = GpuObjectName.NoIndex) =>
        new(
            detail: detail,
            index: index,
            owner: ObjectOwner,
            part: part
        );
    // The bytes of a uniform buffer holding a block: whole constant-buffer views.
    private static int UniformBytes(uint blockBytes) =>
        checked(((int)(((blockBytes + (IGpuBindings.ConstantBufferAlignment - 1UL)) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)));
}

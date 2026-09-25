using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>The owner every object an engine and its pipeline set create is named under
    /// (<see cref="GpuObjectName.Owner"/>): its tables, images, descriptor sets and command pools by their role, and its
    /// pipelines by their kernel's pipeline name.</summary>
    internal const string ObjectOwner = "sdf.world";

    // The debug name of one of the engine's objects: its role, a detail within the role, and its frame slot or item.
    private static GpuObjectName NameOf(string part, string? detail = null, int index = GpuObjectName.NoIndex) =>
        new(
            detail: detail,
            index: index,
            owner: ObjectOwner,
            part: part
        );
}

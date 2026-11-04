using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly IGpuBuffer m_meshBakeSamples;

    /// <summary>Gets the residency's queue-ordered mesh-bake destination. Every view borrows this allocation; the
    /// existing batch claim chooses its one writer, and each submission copies its result before another can write.</summary>
    public IGpuBuffer MeshBakeSamples => m_meshBakeSamples;

    /// <summary>The fixed destination capacity, shared by every view and charged once to its tables.</summary>
    public const ulong MeshBakeSampleBytes = (ulong)SdfWorldPackage.MeshBakeMaxSamples * SdfWorldPackage.MeshBakeSampleWords * sizeof(uint);
}

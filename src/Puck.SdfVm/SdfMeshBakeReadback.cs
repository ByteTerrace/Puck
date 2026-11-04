using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

// The production sample handoff is a type of its own: its exact byte range and batch routing belong to mesh
// derivation, while pass recording and generic GPU buffer transport do not. One instance is retained per frame slot.
internal sealed class SdfMeshBakeReadback {
    public SdfMeshBakeBatch? Batch { get; set; }
    public IGpuReadbackBuffer? Buffer { get; set; }
    public byte[] Bytes { get; set; } = [];
    public IGpuSubmissionFence? Fence { get; set; }
    public bool Record { get; set; }
    public bool Submit { get; set; }
    public string? Version { get; set; }

    internal void Poll() {
        if ((Fence is not { } fence) || !fence.IsSignaled) { return; }
        Fence = null;
        if (Batch is not { } batch) { return; }
        var bytes = ((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint));
        if (Bytes.Length < bytes) { Bytes = new byte[bytes]; }
        Buffer!.Read(destination: Bytes.AsSpan(length: bytes, start: 0));
        Batch = null;
        batch.Bake?.Complete(batch: batch, samples: Bytes.AsSpan(length: bytes, start: 0));
    }
}

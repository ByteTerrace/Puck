using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    /// <summary>Writes the exact cache allocation's completed lighting and receiver-proof publication.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="cache">The cache bound by this graph edge, or null when the edge no longer names the current allocation.</param>
    public static void WriteIndirect(Span<byte> block, SdfIndirectCache? cache) {
        WriteUInt32(block, IndirectTier, (uint)(cache?.Layout.Tier ?? SdfIndirectTier.Off));
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectEpoch), cache?.Epoch ?? 0u);
        var allocation = (ulong)(cache?.History.Allocation ?? 0L);
        var allocationOffset = Offset(SdfWorldPackage.IndirectAllocation);
        WriteUInt32(block, allocationOffset, (uint)allocation);
        WriteUInt32(block, allocationOffset + sizeof(uint), (uint)(allocation >> 32));
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectCertificateRevision), cache?.CertificateRevision ?? 0u);
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectFrame), cache is { Frozen: true, Frame: < uint.MaxValue } ? cache.Frame + 1u : cache?.Frame ?? 0u);
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectReadGeneration), (uint)Math.Max(0, cache?.PublishedGeneration ?? 0));
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectReadPublication), cache?.PublishedStamp ?? 0u);
        WriteUInt32(block, Offset(SdfWorldPackage.IndirectReceiverProofs), cache is { Frozen: false } ? (uint)cache.Layout.ReceiverProofBudget : 0u);
        WriteIndirectPick(block, false, 0u, 0u);
    }
    /// <summary>Arms one selected receiver record, without changing ordinary identity picking.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="enabled">Whether this submitted frame copies the selected receiver.</param>
    /// <param name="x">The selected render column.</param>
    /// <param name="y">The selected render row.</param>
    public static void WriteIndirectPick(Span<byte> block, bool enabled, uint x, uint y) {
        var offset = Offset(SdfWorldPackage.IndirectPickPixel);
        WriteUInt32(block, offset, x);
        WriteUInt32(block, offset + sizeof(uint), y);
        WriteUInt32(block, offset + 2 * sizeof(uint), enabled ? 1u : 0u);
        WriteUInt32(block, offset + 3 * sizeof(uint), 0u);
    }
}

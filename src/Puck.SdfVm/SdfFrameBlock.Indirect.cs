using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public static partial class SdfFrameBlock {
    private static void WriteIndirectControls(Span<byte> block, SdfFrame frame) {
        var gains = frame.IndirectGains;
        var apply = frame.IndirectApply;

        gains.Validate();
        apply.Validate();
        if (frame.IndirectBounces is { } bounces) {
            ArgumentOutOfRangeException.ThrowIfNegative(bounces);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(bounces, SdfIndirectLayout.MaximumBounces);
        }
        var offset = Offset(member: SdfWorldPackage.IndirectSourceGains);

        WriteSingle(block: block, offset: offset, value: gains.Lights);
        WriteSingle(block: block, offset: (offset + sizeof(float)), value: gains.Emission);
        WriteSingle(block: block, offset: (offset + (2 * sizeof(float))), value: gains.Screens);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: gains.Sky);
        WriteSingle(block: block, offset: Offset(member: SdfWorldPackage.IndirectFeedbackGain), value: gains.Feedback);
        offset = Offset(member: SdfWorldPackage.IndirectApply);
        WriteVector3(block: block, offset: offset, value: apply.Tint);
        WriteSingle(block: block, offset: (offset + (3 * sizeof(float))), value: apply.Intensity);
        WriteSingle(block: block, offset: Offset(member: SdfWorldPackage.IndirectContact), value: apply.Contact);
    }

    /// <summary>Writes the exact cache allocation's completed lighting and receiver-proof publication.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="cache">The cache bound by this graph edge, or null when the edge no longer names the current allocation.</param>
    public static void WriteIndirect(Span<byte> block, SdfIndirectCache? cache) {
        WriteIndirectReceiverPreservation(block: block, preserve: false);
        WriteUInt32(block: block, offset: IndirectTier, value: ((uint)(cache?.Layout.Tier ?? SdfIndirectTier.Off)));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectEpoch), value: (cache?.Epoch ?? 0u));
        var allocation = ((ulong)(cache?.History.Allocation ?? 0L));
        var allocationOffset = Offset(member: SdfWorldPackage.IndirectAllocation);

        WriteUInt32(block: block, offset: allocationOffset, value: ((uint)allocation));
        WriteUInt32(block: block, offset: (allocationOffset + sizeof(uint)), value: ((uint)(allocation >> 32)));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectCertificateRevision), value: (cache?.CertificateRevision ?? 0u));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectFrame), value: ((cache is { Frozen: true, Frame: < uint.MaxValue }) ? (cache.Frame + 1u) : (cache?.Frame ?? 0u)));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectReadGeneration), value: ((uint)Math.Max(val1: 0, val2: (cache?.PublishedGeneration ?? 0))));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectReadPublication), value: (cache?.PublishedStamp ?? 0u));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectReceiverProofs), value: ((cache is { Frozen: false }) ? (uint)cache.ReceiverProofBudget : 0u));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectNearEnabled), value: 0u);
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectPreviousPublication), value: 0u);
        WriteIndirectPick(block: block, enabled: false, x: 0u, y: 0u);
    }
    /// <summary>Preserves completed receiver certificates while Primary repeats its visibility storage; the receiver pass keeps
    /// each only while its launch still joins the pixel's surface.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="preserve">Whether the visibility recorder, buffer, binding and extent are unchanged.</param>
    public static void WriteIndirectReceiverPreservation(Span<byte> block, bool preserve) =>
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.PreserveIndirectReceivers), value: (preserve ? 1u : 0u));
    /// <summary>Writes the frame-admitted shared proof allowance; zero defers unfinished receivers.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="count">The admitted receiver count.</param>
    public static void WriteIndirectReceiverBudget(Span<byte> block, int count) =>
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectReceiverProofs), value: checked((uint)count));
    /// <summary>Admits Near only after the current source's whole finite solve has completed under its view fence.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="cache">The exact cache allocation bound by this view.</param>
    /// <param name="frame">The rendered frame whose lighting source must still match.</param>
    /// <param name="ready">Whether the residency has fenced that exact current publication.</param>
    public static void WriteIndirectNear(Span<byte> block, SdfIndirectCache? cache, SdfFrame frame, bool ready) {
        var enabled = (ready && (cache is { Frozen: false, LightingComplete: true, PublishedStamp: > 0 })
            && (cache.Layout.Tier == SdfIndirectTier.High)
            && (cache.LightingSource is { } source) && ReferenceEquals(objA: source, objB: cache.PublishedLightingSource)
            && (cache.Lighting?.Matches(frame: frame) == true));

        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectNearEnabled), value: (enabled ? 1u : 0u));
        WriteUInt32(block: block, offset: Offset(member: SdfWorldPackage.IndirectPreviousPublication), value: (enabled ? cache!.PreviousPublishedStamp : 0u));
    }
    /// <summary>Arms one selected receiver record, without changing ordinary identity picking.</summary>
    /// <param name="block">The world pass block.</param>
    /// <param name="enabled">Whether this submitted frame copies the selected receiver.</param>
    /// <param name="x">The selected render column.</param>
    /// <param name="y">The selected render row.</param>
    public static void WriteIndirectPick(Span<byte> block, bool enabled, uint x, uint y) {
        var offset = Offset(member: SdfWorldPackage.IndirectPickPixel);

        WriteUInt32(block: block, offset: offset, value: x);
        WriteUInt32(block: block, offset: (offset + sizeof(uint)), value: y);
        WriteUInt32(block: block, offset: (offset + (2 * sizeof(uint))), value: (enabled ? 1u : 0u));
        WriteUInt32(block: block, offset: (offset + (3 * sizeof(uint))), value: 0u);
    }
}

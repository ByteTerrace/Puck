using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// GPU-only history: before overwriting this frame's tables, copy the rows changed in the preceding consumed frame.
// One settle copy after motion stops is owed; later still frames copy nothing. The initial upload seeds history after
// its ordinary copies. Keeping mesh matrices in a compact table preserves GpuRegion's CPU-shadow contract and sends
// no second matrix over the host bus.
public sealed partial class SdfWorldTables {
    private const int MeshMatrixBytes = (16 * sizeof(float));
    private const GpuStage TableReaders = GpuStage.ComputeShader | GpuStage.VertexShader | GpuStage.FragmentShader;
    private readonly IGpuBuffer m_previousDynamicTransforms;
    private IGpuBuffer m_previousMeshTransforms;
    private readonly GpuUploadRuns m_changedTransforms = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousTransformCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_changedMeshMatrices = new(capacity: GpuRegion.MaxCopyRuns);
    private readonly GpuUploadRuns m_previousMeshCopies = new(capacity: GpuRegion.MaxCopyRuns);
    private bool m_dynamicHistorySeeded;
    private bool m_meshHistorySeeded;
    private bool m_seedMeshHistory = true;
    private uint m_previousMeshDrawCount;

    private void RecordPreviousTables(nint commandBuffer, int previousSlot) {
        if (m_dynamicHistorySeeded && (m_previousTransformCopies.Count > 0)) {
            CopyTransformHistory(commandBuffer: commandBuffer, slot: previousSlot, seed: false);
        }
        if (!m_seedMeshHistory && (m_previousMeshCopies.Count > 0)) {
            CopyMeshHistory(commandBuffer: commandBuffer, slot: previousSlot, copies: m_previousMeshCopies,
                limit: Math.Min(val1: m_previousMeshDrawCount, val2: m_meshDrawCount));
        }
    }
    private void CompletePreviousTables(nint commandBuffer) {
        var first = !m_dynamicHistorySeeded;
        if (first) {
            CopyTransformHistory(commandBuffer: commandBuffer, slot: m_currentSlot, seed: true);
        }
        m_previousTransformCopies.Clear();
        if (!first) {
            CopyRuns(from: m_changedTransforms, to: m_previousTransformCopies);
        }
        m_changedTransforms.Clear();

        var seed = m_seedMeshHistory;
        m_previousMeshCopies.Clear();
        if (seed || (m_meshDrawCount > m_previousMeshDrawCount)) {
            var start = (seed ? 0 : ((int)m_previousMeshDrawCount));
            m_previousMeshCopies.Add(start: start, length: (((int)m_meshDrawCount) - start));
            CopyMeshHistory(commandBuffer: commandBuffer, slot: m_currentSlot, copies: m_previousMeshCopies, limit: m_meshDrawCount);
        }
        m_previousMeshCopies.Clear();
        if (!seed) {
            CopyRuns(from: m_changedMeshMatrices, to: m_previousMeshCopies);
        }
        m_changedMeshMatrices.Clear();
        m_seedMeshHistory = false;
        m_previousMeshDrawCount = m_meshDrawCount;
    }
    private static void CopyRuns(GpuUploadRuns from, GpuUploadRuns to) {
        for (var run = 0; (run < from.Count); run++) {
            to.Add(start: from.Start(index: run), length: from.Length(index: run));
        }
    }
    private void CopyTransformHistory(nint commandBuffer, int slot, bool seed) {
        var source = m_dynamicTransformRegion.Buffer(slot: slot);
        BeginHistoryCopy(commandBuffer: commandBuffer, source: source, destination: m_previousDynamicTransforms, initialized: m_dynamicHistorySeeded);
        if (seed) {
            CopyHistoryRange(commandBuffer: commandBuffer, source: source, destination: m_previousDynamicTransforms,
                sourceOffset: 0, destinationOffset: 0, bytes: m_previousDynamicTransforms.SizeBytes);
        } else {
            for (var run = 0; (run < m_previousTransformCopies.Count); run++) {
                var offset = (((ulong)m_previousTransformCopies.Start(index: run)) * DynamicTransformByteLength);
                CopyHistoryRange(commandBuffer: commandBuffer, source: source, destination: m_previousDynamicTransforms,
                    sourceOffset: offset, destinationOffset: offset, bytes: (((ulong)m_previousTransformCopies.Length(index: run)) * DynamicTransformByteLength));
            }
        }
        FinishHistoryCopy(commandBuffer: commandBuffer, source: source, destination: m_previousDynamicTransforms);
        m_dynamicHistorySeeded = true;
    }
    private void CopyMeshHistory(nint commandBuffer, int slot, GpuUploadRuns copies, uint limit) {
        if ((copies.Count == 0) || (limit == 0)) {
            return;
        }
        var source = m_meshRegion.Buffer(slot: slot);
        BeginHistoryCopy(commandBuffer: commandBuffer, source: source, destination: m_previousMeshTransforms, initialized: m_meshHistorySeeded);
        for (var run = 0; (run < copies.Count); run++) {
            var end = Math.Min(val1: (copies.Start(index: run) + copies.Length(index: run)), val2: ((int)limit));
            for (var draw = copies.Start(index: run); (draw < end); draw++) {
                CopyHistoryRange(commandBuffer: commandBuffer, source: source, destination: m_previousMeshTransforms,
                    sourceOffset: (((ulong)draw) * SdfMeshRegion.DrawBytes), destinationOffset: (((ulong)draw) * MeshMatrixBytes), bytes: MeshMatrixBytes);
            }
        }
        FinishHistoryCopy(commandBuffer: commandBuffer, source: source, destination: m_previousMeshTransforms);
        m_meshHistorySeeded = true;
    }
    private void CopyHistoryRange(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, ulong sourceOffset, ulong destinationOffset, ulong bytes) =>
        m_gpu.Recorder.CopyBuffer(commandBufferHandle: commandBuffer, sourceBufferHandle: source.BufferHandle,
            destinationBufferHandle: destination.BufferHandle, sizeBytes: bytes, sourceOffsetBytes: sourceOffset, destinationOffsetBytes: destinationOffset);
    private void BeginHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination, bool initialized) {
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: TableReaders,
            destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: (initialized ? GpuAccess.ShaderRead : GpuAccess.None), sourceStageMask: (initialized ? GpuStage.ComputeShader : GpuStage.TopOfPipe),
            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
    }
    private void FinishHistoryCopy(nint commandBuffer, IGpuBuffer source, IGpuBuffer destination) {
        m_gpu.Recorder.TransitionBuffer(bufferHandle: source.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: TableReaders);
        m_gpu.Recorder.TransitionBuffer(bufferHandle: destination.BufferHandle, commandBufferHandle: commandBuffer,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
    }
    private void StageMeshMotion(ReadOnlySpan<uint> words, int draws) {
        var old = m_meshRegion.Contents;
        for (var draw = 0; (draw < draws); draw++) {
            var offset = (draw * SdfMeshRegion.DrawBytes);
            var matrix = System.Runtime.InteropServices.MemoryMarshal.AsBytes(span: words.Slice(start: (draw * SdfMeshRegion.DrawWords), length: 16));
            if ((offset > (old.Length - MeshMatrixBytes)) || !matrix.SequenceEqual(other: old.Slice(start: offset, length: MeshMatrixBytes))) {
                m_changedMeshMatrices.Add(start: draw, length: 1);
            }
        }
        var bytes = (((ulong)Math.Max(val1: 1, val2: draws)) * MeshMatrixBytes);
        if (bytes <= m_previousMeshTransforms.SizeBytes) {
            return;
        }
        m_deviceContext.TryWaitIdle();
        var replacement = m_gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "previous-mesh-transforms"), sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        m_previousMeshTransforms.Dispose();
        m_previousMeshTransforms = replacement;
        m_bindingRevision++;
        m_meshHistorySeeded = false;
        m_seedMeshHistory = true;
    }
}

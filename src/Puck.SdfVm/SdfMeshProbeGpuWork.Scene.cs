using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.SdfVm;

internal sealed partial class SdfMeshProbeGpuWork {
    private readonly SdfMeshIrradianceScene.GpuArena m_sceneArena = new();
    private SceneUpload? m_sceneUpload;
    private long m_sceneUploads;
    private long m_sceneWrittenBytes;
    private long m_sceneLastBytes;
    private int m_sceneInFlight;

    public int UploadBytes(object job) => job is SceneChunk chunk ? chunk.Count * sizeof(uint) : 0;

    // Only this door publishes a scene: no query is admitted while its replacement uploads, and its root
    // is written last. The old root, prefix and geometry remain intact until that last submission retires.
    // A chunk holds at most SdfGpuWork.UploadChunkBytes, so while work is queued a chunk is read only when that many
    // bytes' copy fits what is left of the frame's allowance: a chunk read from its channel cannot be put back.
    private bool TakeSceneUpload(SdfGpuWorkAllowance allowance, [NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        var upload = m_sceneUpload!;
        if (upload.Work.IsFaulted) { upload.Work.GetAwaiter().GetResult(); }
        if (!allowance.Admits(seconds: SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes))) {
            estimatedSeconds = SdfGpuWork.UploadSecondsOf(bytes: SdfGpuWork.UploadChunkBytes);
            return false;
        }
        if (!upload.Chunks.Reader.TryRead(out var chunk) && (m_sceneInFlight != 0 || !upload.TryRoot(out chunk))) { return false; }
        m_sceneInFlight++;
        job = chunk;
        estimatedSeconds = SdfGpuWork.UploadSecondsOf(bytes: chunk.Count * sizeof(uint));
        return true;
    }

    // The scene words the region's host copy keeps: the root words the last chunk writes whole (SdfMeshProbeTrace.RootWords),
    // and the size a region starts at. Every other word is owed from its chunk and never shadowed.
    private const int RootBytes = SdfMeshProbeTrace.RootWords * sizeof(uint);

    private void RecordSceneUpload(SceneChunk chunk, in SdfGpuWorkRecording recording) {
        var upload = m_sceneUpload!;
        var export = upload.Export!;
        // Mapped, so a scene that outgrows the region never holds two copies of it while the carry runs.
        m_sceneRegion ??= m_tables.CreateProbeRegion("probe-scene", RootBytes, SdfGpuWork.SlotCount, mapped: true);
        m_sceneRegion.Shadow(chunk, RootBytes);
        if (export.Words * 4L > m_sceneRegion.ByteCount) {
            m_sceneCarry = m_sceneRegion.Grow(sizeof(uint) * GpuCapacity.Headroom(required: export.Words, ceiling: (GpuRegion.MaxByteCount / sizeof(uint)), divisor: 8));
        }
        if (chunk.Word == 0) { m_sceneRegion.Write(0, MemoryMarshal.AsBytes(chunk.Words.AsSpan(0, chunk.Count))); }
        else { m_sceneRegion.OweSourced(chunk.Word * 4L, chunk.Count * 4L); }
        recording.CopyRegion(m_sceneRegion, recording.Slot, GpuStage.ComputeShader);
        upload.Bytes += chunk.Count * sizeof(uint);
    }

    private void CompleteSceneUpload(SceneChunk chunk, SdfGpuWorkReadback readback) {
        readback.Release();
        m_sceneCarry?.Dispose();
        m_sceneCarry = null;
        lock (m_gate) {
            m_sceneInFlight--;
            if (chunk.Word != 0) { m_sceneArena.Complete(m_sceneUpload!.Export!, chunk.Word, chunk.Count); }
            chunk.Dispose();
            if (chunk.Word != 0) { return; }
            var upload = m_sceneUpload!;
            m_sceneArena.Publish(upload.Export!);
            m_scene?.Dispose();
            m_scene = upload.Scene.Acquire();
            m_scenePlacements = upload.Placements;
            m_placementVersion = upload.PlacementVersion;
            m_sceneUploads++;
            m_sceneLastBytes = upload.Bytes;
            m_sceneWrittenBytes += upload.Bytes;
            // The producer completes its channel before exposing the root, so there is no worker to join here.
            upload.Dispose();
            m_sceneUpload = null;
        }
    }

    private sealed class SceneUpload : IDisposable {
        internal readonly SdfMeshIrradianceScene Scene;
        internal readonly long PlacementVersion;
        internal readonly IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> Placements;
        internal readonly Channel<SceneChunk> Chunks = Channel.CreateBounded<SceneChunk>(new BoundedChannelOptions(SdfGpuWork.SlotCount) { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource m_cancel = new();
        private SceneChunk? m_root;
        internal readonly Task Work;
        internal SdfMeshIrradianceScene.GpuExport? Export;
        internal long Bytes;

        // `offered` tells the service a chunk, or the root, is ready to take (SdfGpuWork.Wake).
        internal SceneUpload(SdfMeshIrradianceScene scene, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, long placementVersion, SdfMeshIrradianceScene.GpuArena arena, Action offered) {
            Scene = scene.Acquire();
            PlacementVersion = placementVersion;
            Placements = scene.CaptureGpuPlacements(placements);
            Work = Task.Run(async () => {
                Export = Scene.CreateGpuExport(Placements, arena);
                foreach (var (word, count) in Export.Changed) {
                    for (var offset = 0; offset < count;) {
                        m_cancel.Token.ThrowIfCancellationRequested();
                        var take = Math.Min(count - offset, SdfGpuWork.UploadChunkBytes / sizeof(uint));
                        var chunk = new SceneChunk(word + offset, RentedArrays.Rent<uint>(minimumLength: take), take, Export);
                        try {
                            Export.Read(word + offset, chunk.Words.AsSpan(0, take));
                            await Chunks.Writer.WriteAsync(chunk, m_cancel.Token).ConfigureAwait(false);
                        } catch { chunk.Dispose(); throw; }
                        offered();
                        offset += take;
                    }
                }
                m_root = new SceneChunk(0, [Export.Root, Export.Instances], SdfMeshProbeTrace.RootWords);
                Chunks.Writer.Complete();
                offered();
            }, m_cancel.Token);
        }

        internal bool TryRoot([NotNullWhen(true)] out SceneChunk? chunk) {
            // Worker completion alone can race an empty TryRead: the writer may have queued its last item since it.
            chunk = Work.IsCompletedSuccessfully && Chunks.Reader.Completion.IsCompleted ? Interlocked.Exchange(ref m_root, null) : null;
            return chunk is not null;
        }
        internal void Cancel() => m_cancel.Cancel();
        internal bool Cancelled => m_cancel.IsCancellationRequested;
        public void Dispose() {
            Cancel();
            try { Work.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch when (Work.IsFaulted) { } // TryTake reports packing faults; teardown must still release device resources.
            finally {
                while (Chunks.Reader.TryRead(out var chunk)) { chunk.Dispose(); }
                Scene.Dispose(); m_cancel.Dispose();
            }
        }
    }

    private sealed class SceneChunk(int word, uint[] words, int count, SdfMeshIrradianceScene.GpuExport? export = null) : IGpuRegionSource, IDisposable {
        private bool m_disposed;
        internal int Word { get; } = word;
        internal uint[] Words { get; } = words;
        internal int Count { get; } = count;
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) {
            if (word >= Word && word + count <= Word + Count) {
                buffer.Write<uint>(Words.AsSpan(word - Word, count), destinationOffsetBytes);
                return;
            }
            // GpuRegion may merge across a small unchanged gap (including the creation-owed root).
            // Its source contract covers every requested word, not just this chunk's changed range.
            var leading = Math.Min(count, Math.Max(0, Word - word));
            if (leading != 0) { WriteGap(buffer, destinationOffsetBytes, word, leading); }
            var start = word + leading;
            var cached = Math.Min(count - leading, Math.Max(0, Word + Count - start));
            if (cached != 0) { buffer.Write<uint>(Words.AsSpan(start - Word, cached), destinationOffsetBytes + (ulong)leading * 4); }
            var trailing = count - leading - cached;
            if (trailing != 0) { WriteGap(buffer, destinationOffsetBytes + (ulong)(leading + cached) * 4, start + cached, trailing); }
        }
        private void WriteGap(IGpuStorageBuffer buffer, ulong destination, int word, int count) {
            using var lease = RentedArrays.Rent<uint>(minimumLength: count, array: out var merged);
            var words = merged.AsSpan(0, count);

            export!.Read(word, words);
            buffer.Write<uint>(words, destination);
        }
        public void Dispose() {
            if (m_disposed) { return; }
            m_disposed = true;
            if (export is not null) { RentedArrays.Return(array: Words); }
        }
    }
}

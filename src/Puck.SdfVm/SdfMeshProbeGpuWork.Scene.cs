using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Puck.Abstractions.Gpu;

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
    private bool TakeSceneUpload([NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        var upload = m_sceneUpload!;
        if (upload.Work.IsFaulted) { upload.Work.GetAwaiter().GetResult(); }
        if (!upload.Chunks.Reader.TryRead(out var chunk) && (m_sceneInFlight != 0 || !upload.TryRoot(out chunk))) { return false; }
        m_sceneInFlight++;
        job = chunk;
        estimatedSeconds = chunk.Count * sizeof(uint) / 1e9;
        return true;
    }

    private void RecordSceneUpload(SceneChunk chunk, in SdfGpuWorkRecording recording) {
        var upload = m_sceneUpload!;
        var export = upload.Export!;
        m_sceneRegion ??= m_tables.CreateProbeSceneRegion(sizeof(uint), SdfGpuWork.SlotCount);
        m_sceneRegion.Shadow(chunk, sizeof(uint));
        if (export.Words * 4L > m_sceneRegion.ByteCount) {
            m_sceneCarry = m_sceneRegion.Grow(Math.Min(GpuRegion.MaxByteCount, (export.Words + export.Words / 8L) * sizeof(uint)));
        }
        if (chunk.Word == 0) { m_sceneRegion.Write(0, MemoryMarshal.AsBytes(chunk.Words.AsSpan(0, chunk.Count))); }
        else { m_sceneRegion.OweSourced(chunk.Word * 4L, chunk.Count * 4L); }
        var command = recording.CommandBuffer;
        var copies = new GpuRegionCopyRecording(recording.Recorder, GpuStage.ComputeShader, () => command);
        copies.Record(m_sceneRegion, recording.Slot, handsToReaders: true);
        copies.Finish();
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

        internal SceneUpload(SdfMeshIrradianceScene scene, IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> placements, long placementVersion, SdfMeshIrradianceScene.GpuArena arena) {
            Scene = scene.Acquire();
            PlacementVersion = placementVersion;
            Placements = scene.CaptureGpuPlacements(placements);
            Work = Task.Run(async () => {
                Export = Scene.CreateGpuExport(Placements, arena);
                foreach (var (word, count) in Export.Changed) {
                    for (var offset = 0; offset < count;) {
                        m_cancel.Token.ThrowIfCancellationRequested();
                        var take = Math.Min(count - offset, SdfGpuWork.UploadChunkBytes / sizeof(uint));
                        var chunk = new SceneChunk(word + offset, ArrayPool<uint>.Shared.Rent(take), take, Export);
                        try {
                            Export.Read(word + offset, chunk.Words.AsSpan(0, take));
                            await Chunks.Writer.WriteAsync(chunk, m_cancel.Token).ConfigureAwait(false);
                        } catch { chunk.Dispose(); throw; }
                        offset += take;
                    }
                }
                m_root = new SceneChunk(0, [Export.Root], 1);
                Chunks.Writer.Complete();
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
            var merged = ArrayPool<uint>.Shared.Rent(count);
            try {
                export!.Read(word, merged.AsSpan(0, count));
                buffer.Write<uint>(merged.AsSpan(0, count), destination);
            } finally { ArrayPool<uint>.Shared.Return(merged); }
        }
        public void Dispose() {
            if (m_disposed) { return; }
            m_disposed = true;
            if (export is not null) { ArrayPool<uint>.Shared.Return(Words); }
        }
    }
}

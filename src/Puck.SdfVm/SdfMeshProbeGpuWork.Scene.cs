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

    public int UploadBytes(object job) => job is SceneChunk chunk ? chunk.Words.Length * sizeof(uint) : 0;

    // Only this door publishes a scene: no query is admitted while its replacement uploads, and its root
    // is written last. The old root, prefix and geometry remain intact until that last submission retires.
    private bool TakeSceneUpload([NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        var upload = m_sceneUpload!;
        if (upload.Work.IsFaulted) { upload.Work.GetAwaiter().GetResult(); }
        if (!upload.Chunks.Reader.TryRead(out var chunk) && !upload.TryRoot(out chunk)) { return false; }
        m_inFlight = true;
        job = chunk;
        estimatedSeconds = chunk.Words.Length * sizeof(uint) / 1e9;
        return true;
    }

    private void RecordSceneUpload(SceneChunk chunk, in SdfGpuWorkRecording recording) {
        var upload = m_sceneUpload!;
        var export = upload.Export!;
        m_sceneRegion ??= m_tables.CreateProbeSceneRegion(sizeof(uint));
        m_sceneRegion.Shadow(chunk, sizeof(uint));
        if (export.Words * 4L > m_sceneRegion.ByteCount) {
            m_sceneCarry = m_sceneRegion.Grow(Math.Min(GpuRegion.MaxByteCount, (export.Words + export.Words / 8L) * sizeof(uint)));
        }
        if (chunk.Word == 0) { m_sceneRegion.Write(0, MemoryMarshal.AsBytes(chunk.Words.AsSpan())); }
        else { m_sceneRegion.OweSourced(chunk.Word * 4L, chunk.Words.Length * 4L); }
        var command = recording.CommandBuffer;
        var copies = new GpuRegionCopyRecording(recording.Recorder, GpuStage.ComputeShader, () => command);
        copies.Record(m_sceneRegion, 0, handsToReaders: true);
        copies.Finish();
        upload.Bytes += chunk.Words.Length * sizeof(uint);
    }

    private void CompleteSceneUpload(SceneChunk chunk, SdfGpuWorkReadback readback) {
        readback.Release();
        m_sceneCarry?.Dispose();
        m_sceneCarry = null;
        lock (m_gate) {
            m_inFlight = false;
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
        internal readonly Channel<SceneChunk> Chunks = Channel.CreateBounded<SceneChunk>(new BoundedChannelOptions(2) { SingleReader = true, SingleWriter = true });
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
                        var words = new uint[take];
                        Export.Read(word + offset, words);
                        await Chunks.Writer.WriteAsync(new SceneChunk(word + offset, words), m_cancel.Token).ConfigureAwait(false);
                        offset += take;
                    }
                }
                m_root = new SceneChunk(0, [Export.Root]);
                Chunks.Writer.Complete();
            }, m_cancel.Token);
        }

        internal bool TryRoot([NotNullWhen(true)] out SceneChunk? chunk) {
            chunk = Work.IsCompletedSuccessfully ? Interlocked.Exchange(ref m_root, null) : null;
            return chunk is not null;
        }
        internal void Cancel() => m_cancel.Cancel();
        public void Dispose() {
            Cancel();
            try { Work.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            catch when (Work.IsFaulted) { } // TryTake reports packing faults; teardown must still release device resources.
            finally { Scene.Dispose(); m_cancel.Dispose(); }
        }
    }

    private sealed class SceneChunk(int word, uint[] words) : IGpuRegionSource {
        internal int Word { get; } = word;
        internal uint[] Words { get; } = words;
        public void WriteWords(IGpuStorageBuffer buffer, ulong destinationOffsetBytes, int word, int count) =>
            buffer.Write<uint>(Words.AsSpan(word - Word, count), destinationOffsetBytes);
    }
}

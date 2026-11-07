using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The volume's batched hit queries on the residency's background GPU service.</summary>
internal sealed partial class SdfMeshProbeGpuWork : ISdfGpuWorkClient {
    private readonly SdfMeshIrradiance m_volume;
    private readonly GpuDeviceServices m_gpu;
    private readonly SdfWorldTables m_tables;
    private readonly IGpuStorageBuffer m_directions;
    private readonly IGpuStorageBuffer m_zero;
    private readonly nint m_pool;
    private readonly Slot[] m_slots = new Slot[SdfGpuWork.SlotCount];
    private readonly Lock m_gate = new();
    private readonly Queue<Job> m_pending = new();
    private bool m_disposed;
    private bool m_stopping;
    private GpuRegion? m_sceneRegion;
    private IDisposable? m_sceneCarry;
    private GpuRegion? m_feedback;
    private IGpuBuffer? m_hitCache;
    private IGpuBuffer? m_meterCache;
    private int m_feedbackWords;
    private SdfMeshIrradianceScene? m_scene;
    private IReadOnlyDictionary<SdfMesh, SdfMeshRegionMesh> m_scenePlacements = new Dictionary<SdfMesh, SdfMeshRegionMesh>();
    private long m_placementVersion = -1;
    private bool m_inFlight;
    // The head job waiting for its scene's meshes to be resident, and since when.
    private static readonly TimeSpan AdmissionWait = TimeSpan.FromSeconds(2);
    private Job? m_waiting;
    private long m_waitingSince;

    internal SdfMeshProbeGpuWork(SdfWorldTables tables, SdfMeshIrradiance volume) {
        m_volume = volume;
        m_gpu = tables.Gpu;
        m_tables = tables;
        using var scope = new GpuCreationScope();
        var layout = SdfWorldInterfaces.ProbeTraceLayout.PipelineLayout(GpuShaderStage.Compute);
        var pipeline = tables.Pipeline(SdfKernel.ProbeTrace);
        var relightPipeline = tables.Pipeline(SdfKernel.ProbeRelight);
        var sizes = GpuDescriptorPoolSizes.ForGroups(layout.Groups) + GpuDescriptorPoolSizes.ForGroups(SdfWorldInterfaces.ProbeRelightLayout.PipelineLayout(GpuShaderStage.Compute).Groups);
        var total = sizes;
        for (var i = 1; i < m_slots.Length; i++) { total += sizes; }
        m_pool = scope.Own(handle: m_gpu.Bindings.CreatePool(name: default, sizes: total), release: m_gpu.Bindings.DestroyPool);
        m_directions = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: SdfMeshProbeDepth.HitCount * 16, usage: GpuBufferUsage.Storage));
        var directions = new Vector4[SdfMeshProbeDepth.HitCount];
        for (var i = 0; i < directions.Length; i++) { directions[i] = new(SdfMeshProbeDepth.RayDirections[i], SdfMeshProbeDepth.RayWeights[i]); }
        m_directions.Write<Vector4>(directions);
        m_zero = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: 4, usage: GpuBufferUsage.Storage));
        m_zero.Write<uint>([0]);
        for (var i = 0; i < m_slots.Length; i++) {
            m_slots[i] = new Slot(
                m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_pool),
                m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: relightPipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_pool));
        }
        scope.Complete();
    }

    // One coordinator submits a whole batch and sleeps on its completion; it never holds the device thread.
    // The job owns a scene claim until completion/abandonment. The service owns every slot's fence and readback token.
    // A trace also copies each origin's hits into the device hit cache at its slot, so a relight reads them there and the
    // host never uploads hits it read back.
    // The answer of a job that is given `reuse` is `reuse` itself, or a larger array when it is too small: the caller keeps
    // the returned array for its next job and reads only the rows its origins name, never past them, since the words beyond
    // are an earlier job's.
    internal uint[] Trace(SdfMeshIrradianceScene scene, Memory<Vector4> origins, uint[] cacheSlots, uint hitCapacity, uint[]? reuse = null) {
        return Submit(new Job(scene.Acquire(), origins) { CacheSlots = cacheSlots, HitCapacity = hitCapacity, Reuse = reuse });
    }
    // The origins and input are read before this returns, so a caller may overwrite the arrays its memories view at once.
    internal Result Relight(SdfMeshIrradianceScene scene, Memory<Vector4> origins, ReadOnlyMemory<uint> input, Vector4[] lighting,
        int volumeWords, in Deltas updates, uint hitCapacity, uint[]? reuse = null) {
        var job = new Job(scene.Acquire(), origins) {
            Input = input, Lighting = lighting, VolumeWords = volumeWords, Updates = updates, HitCapacity = hitCapacity, Reuse = reuse,
        };
        var words = Submit(job);
        return new Result(words, job.Output!, job.HeldReadback!);
    }
    internal void Commit(SdfMeshIrradianceScene scene, int geometry, int window, Result source, ReadOnlyMemory<int> destinations) =>
        Submit(new Job(scene.Acquire(), default) { CommitSource = source, Destinations = destinations, Geometry = geometry, Window = window });
    internal IGpuBuffer FeedbackBuffer => m_feedback!.Buffer(0);

    // The service's existing readback token also pins the job's device output. A commit may borrow it only while
    // this result is held; disposing it releases the slot exactly once after the copy's fence, or on cancellation.
    internal sealed class Result(uint[] words, IGpuBuffer buffer, SdfGpuWorkReadback readback) : IDisposable {
        public uint[] Words { get; } = words;
        public IGpuBuffer Buffer { get; } = buffer;
        private SdfGpuWorkReadback? m_readback = readback;
        public void Dispose() => Interlocked.Exchange(ref m_readback, null)?.Release();
    }
    internal uint[] Select(SdfMeshIrradianceScene scene, Memory<Vector4> centers, ReadOnlyMemory<uint> metadata, Vector4[] changes, uint hitCapacity, uint[]? reuse = null) =>
        Submit(new Job(scene.Acquire(), centers) { Input = metadata, Lighting = changes, Selection = true, HitCapacity = hitCapacity, Reuse = reuse });
    internal Task<uint[]> Meter(SdfMeshIrradianceScene scene, Memory<Vector4> origins, Vector4[] lighting, int words, int geometry, int window) =>
        SubmitAsync(new Job(scene.Acquire(), origins) { Input = new uint[] { 0 }, Lighting = lighting, Meter = true, VolumeWords = words, Geometry = geometry, Window = window });
    private uint[] Submit(Job job) => SubmitAsync(job).GetAwaiter().GetResult();
    private Task<uint[]> SubmitAsync(Job job) {
        lock (m_gate) {
            if (m_stopping || m_disposed) { job.Scene.Dispose(); throw new OperationCanceledException(); }
            m_pending.Enqueue(job);
        }
        return job.Answer.Task;
    }

    // Takes a scene that exports the words the published scene holds (SdfMeshIrradianceScene.SameGpuContent) as the
    // published one, releasing the claim on the scene it replaces: a scene trimmed of retained geometry needs no upload,
    // and the untrimmed scene's geometry is freed.
    internal void Adopt(SdfMeshIrradianceScene scene) {
        lock (m_gate) { AdoptLocked(scene: scene); }
    }
    private void AdoptLocked(SdfMeshIrradianceScene scene) {
        if (m_stopping || m_disposed || ReferenceEquals(m_scene, scene) || !SdfMeshIrradianceScene.SameGpuContent(m_scene, scene)) { return; }
        m_scene!.Dispose();
        m_scene = scene.Acquire();
        m_sceneAdoptions++;
    }
    private long m_sceneAdoptions;

    public bool TryTake(SdfWorldTables tables, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        lock (m_gate) {
            if (m_inFlight || !tables.BindsBackgroundWork) { return false; }
            if (m_sceneUpload is { } upload) {
                if (!upload.Cancelled && m_volume.KeepsGpuScene(upload.Scene)) { return TakeSceneUpload(out job, out estimatedSeconds); }
                // Stop producing immediately, but reclaim addresses only after the worker and every submitted copy
                // retire. Completed immutable geometry is retained for the next scene, so roster churn cannot restart
                // it forever. No frame thread joins a blocked writer or publishes an abandoned prefix.
                upload.Cancel();
                if (!upload.Work.IsCompleted || m_sceneInFlight != 0) { return false; }
                if (upload.Export is { } export) { m_sceneArena.Abandon(export); }
                upload.Dispose();
                m_sceneUpload = null;
            }
            if (!m_pending.TryPeek(out var pending)) { return false; }
            if (!(pending.CommitSource is null && !pending.Meter ? m_volume.KeepsGpuScene(pending.Scene) :
                m_volume.KeepsGpuPublication(pending.Scene, pending.Geometry, pending.Window)) ||
                (pending.Meter && (m_feedback is null || m_feedbackWords != pending.VolumeWords))) {
                m_pending.Dequeue();
                pending.Scene.Dispose();
                m_volume.Counters.Add(SdfMeshIrradiance.GpuCancelled, 1);
                pending.Answer.TrySetCanceled();
                return false;
            }
            if (pending.CommitSource is not null) {
                m_pending.Dequeue();
                m_inFlight = true;
                job = pending;
                estimatedSeconds = pending.Destinations.Length * 1e-8;
                return true;
            }
            if (!tables.TryProbeScene(pending.Scene, out var placements, out var meshes, out var version)) {
                // The scene's meshes normally land within an upload or two. A head that waits past AdmissionWait is
                // cancelled like any obsolete job, so its coordinator and the meter behind it are never held without
                // bound; the coordinator lists again on its next grant and asks for the scene as it then stands.
                if (!ReferenceEquals(m_waiting, pending)) { m_waiting = pending; m_waitingSince = Stopwatch.GetTimestamp(); }
                else if (Stopwatch.GetElapsedTime(m_waitingSince) > AdmissionWait) {
                    m_pending.Dequeue();
                    m_waiting = null;
                    pending.Scene.Dispose();
                    m_volume.Counters.Add(SdfMeshIrradiance.GpuCancelled, 1);
                    pending.Answer.TrySetCanceled();
                }
                return false;
            }
            m_waiting = null;
            AdoptLocked(scene: pending.Scene);
            // A changed mesh roster need not move any of this scene's addresses.
            if (ReferenceEquals(m_scene, pending.Scene) && m_placementVersion != version && pending.Scene.MatchesGpuPlacements(placements, m_scenePlacements)) {
                m_placementVersion = version;
            }
            // Serialized queries make replacing the hierarchy safe: the prior token has been released before
            // the coordinator can enqueue another. No slot descriptor or region is changed while it is read.
            if (!ReferenceEquals(m_scene, pending.Scene) || m_placementVersion != version) {
                m_sceneUpload = new SceneUpload(pending.Scene, placements, version, m_sceneArena);
                return false;
            }
            pending.Meshes = meshes;
            if (pending.Lighting is not null && !pending.Selection && !pending.Meter) {
                if (m_feedbackWords != pending.VolumeWords) {
                    if (m_feedback is not null) { tables.RetireBackgroundResource(m_feedback); }
                    m_feedback = tables.CreateProbeSceneRegion(pending.VolumeWords * 4);
                    m_feedbackWords = pending.VolumeWords;
                }
                var updates = pending.Updates;
                for (var update = 0; update < updates.Count; update++) {
                    m_feedback!.Write(updates.Words.Span[update] * 4, MemoryMarshal.AsBytes(updates.Data.Span.Slice(update * updates.Length, updates.Length)));
                }
            }
            m_pending.Dequeue();
            m_inFlight = true;
            job = pending;
            estimatedSeconds = pending.Origins.Length * (pending.Selection ? 1e-8 : SdfMeshProbeDepth.HitCount * 1e-8);
            return true;
        }
    }

    public void Record(object job, in SdfGpuWorkRecording recording) {
        if (job is SceneChunk chunk) { RecordSceneUpload(chunk, in recording); return; }
        var query = (Job)job;
        if (query.CommitSource is { } source) {
            RecordCommit(query, source, in recording);
            return;
        }
        var slot = m_slots[recording.Slot];
        var compute = query.Lighting is not null;
        var relight = compute && !query.Selection && !query.Meter;
        var pipeline = m_tables.Pipeline(query.Meter ? SdfKernel.ProbeMeter : query.Selection ? SdfKernel.ProbeSelect : relight ? SdfKernel.ProbeRelight : SdfKernel.ProbeTrace);
        var layout = compute ? SdfWorldInterfaces.ProbeRelightLayout : SdfWorldInterfaces.ProbeTraceLayout;
        var set = compute ? slot.RelightSet : slot.Set;
        var bytes = (ulong)query.Origins.Length * (ulong)(query.Meter ? SdfMeshProbeRelight.MeterOutputWords : query.Selection ? 1 : relight ? SdfMeshProbeRelight.ResultWords : SdfMeshProbeDepth.HitWords) * 4;
        if (slot.Origins is null || slot.Origins.SizeBytes != (ulong)query.Origins.Length * 16) {
            slot.Origins?.Dispose();
            slot.Origins = m_gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)query.Origins.Length * 16, usage: GpuBufferUsage.Storage);
        }
        if (slot.Hits is null || slot.Hits.SizeBytes < bytes + 4) {
            slot.Hits?.Dispose();
            slot.Hits = m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes + 4, usage: GpuBufferUsage.Storage);
        }
        slot.Identities ??= m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 8, usage: GpuBufferUsage.Storage);
        if (query.Meter) {
            var cacheBytes = (ulong)query.Origins.Length * (1 + SdfMeshProbeDepth.HitWords) * 4;
            if (m_meterCache is null || m_meterCache.SizeBytes < cacheBytes) {
                m_meterCache?.Dispose();
                m_meterCache = m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: cacheBytes, usage: GpuBufferUsage.Storage);
                var origins = query.Origins.Span;
                for (var i = 0; i < origins.Length; i++) { origins[i].W = 1; }
            }
        }
        slot.Origins.Write<Vector4>(query.Origins.Span);
        var command = recording.CommandBuffer;
        // TryTake admits one query until Complete observes its fence. These shared regions therefore need
        // one upload slot; the service slots still independently own outputs and held readback tokens.
        if (relight) { recording.CopyRegion(m_feedback!, 0, GpuStage.ComputeShader); }
        Bind(SdfKernelInterfaces.ProbeScene, m_sceneRegion!.Buffer(0), 4);
        Bind(SdfWorldPackage.MeshRegion, query.Meshes!, 4);
        Bind(SdfKernelInterfaces.ProbeOrigins, slot.Origins, 16);
        Bind(SdfKernelInterfaces.ProbeDirections, m_directions, 16);
        Bind(SdfKernelInterfaces.ProbeHitsWritten, slot.Hits, 4, written: true);
        if (query.HitCapacity != 0) { FitHitCache(query.HitCapacity, command); }
        if (compute) {
            Write(ref slot.Input, MemoryMarshal.AsBytes(query.Input.Span));
            Write(ref slot.Lighting, MemoryMarshal.AsBytes(query.Lighting.AsSpan()));
            Bind(SdfKernelInterfaces.ProbeInput, slot.Input!, 4);
            Bind(SdfKernelInterfaces.ProbeLighting, slot.Lighting!, 16);
            Bind(SdfWorldPackage.MeshIrradiance, m_feedback?.Buffer(0) ?? m_tables.DummyBuffer, 4);
            var workBytes = (ulong)(query.Origins.Length + 3) * 4;
            if (slot.Work is null || slot.Work.SizeBytes < workBytes) {
                slot.Work?.Dispose();
                slot.Work = m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: workBytes, usage: GpuBufferUsage.Storage | GpuBufferUsage.Indirect);
            }
            Bind(SdfKernelInterfaces.ProbeWorkWritten, slot.Work, 4, written: true);
            Bind(SdfKernelInterfaces.ProbeWork, slot.Work, 4);
            Bind(SdfKernelInterfaces.ProbeCacheWritten, query.Meter ? m_meterCache! : m_hitCache!, 4, written: true);
            Bind(SdfKernelInterfaces.ProbeCache, query.Meter ? m_meterCache! : m_hitCache!, 4);
        } else { Bind(SdfKernelInterfaces.ProbeIdentitiesWritten, slot.Identities, 8, written: true); }
        var recorder = recording.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Hits.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        recorder.CopyBuffer(commandBufferHandle: command, sourceBufferHandle: m_zero.BufferHandle,
            destinationBufferHandle: slot.Hits.BufferHandle, sizeBytes: 4, destinationOffsetBytes: bytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Hits.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Identities.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        if (query.Meter) {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_meterCache!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        }
        if (relight) {
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            var cache = m_tables.Pipeline(SdfKernel.ProbeCache);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: cache.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: cache.LayoutHandle);
            recorder.Dispatch(command, (uint)query.Origins.Length, 1, 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Hits.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Work!.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.IndirectCommandRead, sourceStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect,
                destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            var compact = m_tables.Pipeline(SdfKernel.ProbeCompact);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: compact.Handle);
            recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: compact.LayoutHandle);
            recorder.Dispatch(command, 1, 1, 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Work.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.IndirectCommandRead, destinationStageMask: GpuStage.ComputeShader | GpuStage.DrawIndirect);
        }
        recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set,
            group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: pipeline.LayoutHandle);
        if (relight) { recorder.DispatchIndirect(command, slot.Work!.BufferHandle, 0); }
        else { recorder.Dispatch(commandBufferHandle: command,
            groupCountX: (uint)(query.Meter ? query.Origins.Length : query.Selection ? (query.Origins.Length + SdfMeshProbeRelight.SelectGroupSize - 1) / SdfMeshProbeRelight.SelectGroupSize : (query.Origins.Length * SdfMeshProbeDepth.HitCount + SdfMeshProbeTrace.GroupSize - 1) / SdfMeshProbeTrace.GroupSize), groupCountY: 1, groupCountZ: 1); }
        recording.CopyToReadback(slot.Hits, bytes + 4);
        if (query.CacheSlots is { } cacheSlots) {
            // The readback left the hits a transfer source; each origin's hits land behind its cache slot's two token
            // words, which the adopting relight's cache pass stamps.
            var cache = m_hitCache!;
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderRead, sourceStageMask: GpuStage.ComputeShader,
                destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
            const ulong HitBytes = (ulong)SdfMeshProbeDepth.HitWords * sizeof(uint);
            for (var i = 0; i < cacheSlots.Length; i++) {
                recorder.CopyBuffer(command, slot.Hits.BufferHandle, cache.BufferHandle, HitBytes, (ulong)i * HitBytes,
                    ((ulong)cacheSlots[i] * SdfMeshProbeRelight.CacheWords + 2) * sizeof(uint));
            }
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: cache.BufferHandle,
                sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
                destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
        }
        query.Output = slot.Hits;

        void Bind(string name, IGpuBuffer buffer, uint stride, bool written = false) {
            var resource = SdfKernelInterfaces.ResourceOf(layout, name);
            m_gpu.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                descriptorSetHandle: set, elementStride: stride, kind: written ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
        }
        void Write(ref IGpuStorageBuffer? buffer, ReadOnlySpan<byte> words) {
            if (buffer is null || buffer.SizeBytes < (ulong)words.Length) {
                buffer?.Dispose();
                buffer = m_gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)words.Length, usage: GpuBufferUsage.Storage);
            }
            buffer.Write<byte>(words);
        }
    }

    // Sizes the hit cache to the volume's tier (SdfMeshIrradiance.GpuHitCapacity). Jobs are admitted one at a time and
    // the last one's fence has signaled, so no recorded work reads the cache it replaces. A new cache is cleared, so its
    // token words match no allocation token, and left readable by compute.
    private void FitHitCache(uint capacity, nint command) {
        var bytes = (ulong)capacity * SdfMeshProbeRelight.CacheWords * sizeof(uint);
        if (m_hitCache?.SizeBytes == bytes) { return; }
        m_hitCache?.Dispose();
        m_hitCache = m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        var recorder = m_gpu.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.BufferHandle,
            sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe,
            destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
        recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.BufferHandle, sizeBytes: bytes);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: m_hitCache.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead, destinationStageMask: GpuStage.ComputeShader);
    }

    private void RecordCommit(Job query, Result source, in SdfGpuWorkRecording recording) {
        var recorder = recording.Recorder;
        var command = recording.CommandBuffer;
        var destination = FeedbackBuffer;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.TransferRead, sourceStageMask: GpuStage.ComputeShader | GpuStage.Transfer,
            destinationAccessMask: GpuAccess.CopyWrite, destinationStageMask: GpuStage.Transfer);
        var destinations = query.Destinations.Span;
        for (var i = 0; i < destinations.Length; i++) {
            var slot = destinations[i];
            if (slot < 0) { continue; }
            recorder.CopyBuffer(command, source.Buffer.BufferHandle, destination.BufferHandle,
                (ulong)SdfMeshIrradianceProbe.ProbeWords * 4,
                (ulong)(i * SdfMeshProbeRelight.ResultWords + SdfMeshProbeRelight.OutputWords) * 4,
                (ulong)slot * SdfMeshIrradianceProbe.ProbeWords * 4);
        }
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: destination.BufferHandle,
            sourceAccessMask: GpuAccess.CopyWrite, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.TransferRead, destinationStageMask: GpuStage.ComputeShader | GpuStage.Transfer);
        // No payload is returned. Completion of this job's fence is the acknowledgement that releases source.
    }

    public void Complete(object job, SdfGpuWorkReadback readback) {
        if (job is SceneChunk chunk) { CompleteSceneUpload(chunk, readback); return; }
        var query = (Job)job;
        var words = readback.Length / 4;
        var readWords = (words == 0) ? [] : (query.Reuse is { } reuse && (reuse.Length >= words)) ? reuse : new uint[words];
        var holdsOutput = query.Lighting is not null && !query.Selection && !query.Meter;
        try { readback.Read(MemoryMarshal.AsBytes(readWords.AsSpan(start: 0, length: words))); }
        catch { readback.Release(); throw; }
        if (holdsOutput) { query.HeldReadback = readback; }
        else { readback.Release(); }
        // A reused array is answered whole, its fault word at words - 1; any other answer drops the fault word's slot.
        var result = ((query.CommitSource is not null) || (query.Reuse is not null)) ? readWords : readWords[..^1];
        if (query.CommitSource is null) { m_volume.Counters.Add(SdfMeshIrradiance.TraversalFaults, readWords[words - 1]); }
        if (query.Lighting is null && query.CommitSource is null) { m_volume.Counters.Add(SdfMeshIrradiance.GpuRays, query.Origins.Length * SdfMeshProbeDepth.HitCount); }
        if (readback.GpuSeconds is { } seconds) { m_volume.Counters.Add(query.Meter ? SdfMeshIrradiance.GpuMeterNanoseconds : query.Selection ? SdfMeshIrradiance.GpuSelectNanoseconds : query.Lighting is null && query.CommitSource is null ? SdfMeshIrradiance.GpuTraceNanoseconds : SdfMeshIrradiance.GpuRelightNanoseconds, (long)(seconds * 1e9)); }
        if (query.Meter) {
            for (var i = SdfMeshProbeRelight.MeterOutputWords - 1; i < result.Length; i += SdfMeshProbeRelight.MeterOutputWords) {
                if ((result[i] & 4) != 0) { m_volume.Counters.Add(SdfMeshIrradiance.CameraTraces, 1); }
                if ((result[i] & 2) != 0) { m_volume.Counters.Add(SdfMeshIrradiance.CameraRelights, 1); }
            }
        }
        lock (m_gate) { m_inFlight = false; }
        query.Scene.Dispose();
        query.Answer.SetResult(result);
    }
    public void Abandon(object job) {
        if (job is SceneChunk chunk) { lock (m_gate) { m_sceneInFlight--; chunk.Dispose(); } return; }
        var query = (Job)job;
        lock (m_gate) { m_inFlight = false; }
        query.Scene.Dispose();
        query.Answer.TrySetCanceled();
    }
    public GpuQueueCategory Category => GpuQueueCategory.Probe;
    public GpuMemoryBytes OwnedBytes {
        get {
            var bytes = m_sceneRegion?.OwnedBytes ?? default;
            var device = bytes.DeviceLocal;
            var host = bytes.HostVisible + m_directions.SizeBytes + m_zero.SizeBytes;
            device += m_feedback?.OwnedBytes.DeviceLocal ?? 0;
            device += (m_hitCache?.SizeBytes ?? 0) + (m_meterCache?.SizeBytes ?? 0);
            host += m_feedback?.OwnedBytes.HostVisible ?? 0;
            foreach (var slot in m_slots) { device += (slot.Hits?.SizeBytes ?? 0) + (slot.Identities?.SizeBytes ?? 0) + (slot.Work?.SizeBytes ?? 0); host += (slot.Origins?.SizeBytes ?? 0) + (slot.Input?.SizeBytes ?? 0) + (slot.Lighting?.SizeBytes ?? 0); }
            return new GpuMemoryBytes(DeviceLocal: device, HostVisible: host);
        }
    }
    internal string DescribeMemory() {
        var scene = m_sceneRegion?.OwnedBytes ?? default;
        var feedback = m_feedback?.OwnedBytes ?? default;
        return FormattableString.Invariant($"trace-scene-device-bytes={scene.DeviceLocal} trace-scene-upload-bytes={scene.HostVisible} trace-scene-shadow-bytes={m_sceneRegion?.ShadowBytes ?? 0} trace-scene-changes={m_sceneUploads} trace-scene-adoptions={m_sceneAdoptions} trace-scene-written-bytes={m_sceneWrittenBytes} trace-scene-last-change-bytes={m_sceneLastBytes} trace-hits-device-bytes={m_hitCache?.SizeBytes ?? 0} trace-feedback-device-bytes={feedback.DeviceLocal} trace-feedback-upload-bytes={feedback.HostVisible} trace-feedback-shadow-bytes={m_feedback?.ShadowBytes ?? 0}");
    }
    public void Stop() {
        lock (m_gate) {
            m_stopping = true;
            m_sceneUpload?.Cancel();
            while (m_pending.TryDequeue(out var pending)) { pending.Scene.Dispose(); pending.Answer.TrySetCanceled(); }
        }
    }
    public void Dispose() {
        if (m_disposed) { return; }
        Stop();
        m_disposed = true;
        m_sceneUpload?.Dispose();
        foreach (var slot in m_slots) { slot.Hits?.Dispose(); slot.Origins?.Dispose(); slot.Identities?.Dispose(); slot.Input?.Dispose(); slot.Lighting?.Dispose(); slot.Work?.Dispose(); }
        m_feedback?.Dispose();
        m_hitCache?.Dispose();
        m_meterCache?.Dispose();
        m_sceneRegion?.Dispose();
        m_sceneCarry?.Dispose();
        m_scene?.Dispose();
        m_directions.Dispose();
        m_zero.Dispose();
        m_gpu.Bindings.DestroyPool(m_pool);
    }
    /// <summary>The slot-sized updates a relight job applies to the feedback volume before it runs: the first word each
    /// update writes, then every update's words back to back, each <see cref="Length"/> words long.</summary>
    internal readonly record struct Deltas(ReadOnlyMemory<int> Words, ReadOnlyMemory<uint> Data, int Count, int Length);

    private sealed class Job(SdfMeshIrradianceScene scene, Memory<Vector4> origins) {
        public readonly SdfMeshIrradianceScene Scene = scene;
        public readonly Memory<Vector4> Origins = origins;
        public readonly TaskCompletionSource<uint[]> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IGpuBuffer? Meshes;
        public ReadOnlyMemory<uint> Input;
        public Vector4[]? Lighting;
        public int VolumeWords;
        public Deltas Updates;
        public bool Selection;
        public bool Meter;
        public IGpuBuffer? Output;
        public SdfGpuWorkReadback? HeldReadback;
        public Result? CommitSource;
        public ReadOnlyMemory<int> Destinations;
        public uint[]? CacheSlots;
        public uint[]? Reuse;
        public uint HitCapacity;
        public int Geometry;
        public int Window;
    }
    private sealed class Slot(nint set, nint relightSet) {
        public readonly nint Set = set;
        public readonly nint RelightSet = relightSet;
        public IGpuStorageBuffer? Origins;
        public IGpuBuffer? Hits;
        public IGpuBuffer? Identities;
        public IGpuStorageBuffer? Input;
        public IGpuStorageBuffer? Lighting;
        public IGpuBuffer? Work;
    }
}

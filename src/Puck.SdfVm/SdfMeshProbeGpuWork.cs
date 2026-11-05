using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The volume's batched hit queries on the residency's background GPU service.</summary>
internal sealed class SdfMeshProbeGpuWork : ISdfGpuWorkClient {
    private readonly SdfMeshIrradiance m_volume;
    private readonly GpuDeviceServices m_gpu;
    private readonly IGpuComputePipeline m_pipeline;
    private readonly IGpuStorageBuffer m_directions;
    private readonly nint m_pool;
    private readonly Slot[] m_slots = new Slot[SdfGpuWork.SlotCount];
    private readonly Lock m_gate = new();
    private Job? m_pending;
    private bool m_disposed;
    private GpuRegion? m_sceneRegion;
    private SdfMeshIrradianceScene? m_scene;
    private long m_placementVersion = -1;
    private bool m_inFlight;

    internal SdfMeshProbeGpuWork(SdfWorldTables tables, SdfMeshIrradiance volume) {
        m_volume = volume;
        m_gpu = tables.Gpu;
        using var scope = new GpuCreationScope();
        var layout = SdfWorldInterfaces.ProbeTraceLayout.PipelineLayout(GpuShaderStage.Compute);
        m_pipeline = tables.Pipeline(SdfKernel.ProbeTrace);
        var sizes = GpuDescriptorPoolSizes.ForGroups(layout.Groups);
        var total = sizes;
        for (var i = 1; i < m_slots.Length; i++) { total += sizes; }
        m_pool = scope.Own(handle: m_gpu.Bindings.CreatePool(name: default, sizes: total), release: m_gpu.Bindings.DestroyPool);
        m_directions = scope.Own(created: m_gpu.BufferFactory.CreateHostVisible(name: default,
            sizeBytes: SdfMeshProbeDepth.HitCount * 16, usage: GpuBufferUsage.Storage));
        m_directions.Write<Vector4>(SdfMeshProbeDepth.RayDirections.ToArray().Select(v => new Vector4(v, 0)).ToArray());
        for (var i = 0; i < m_slots.Length; i++) {
            m_slots[i] = new Slot(m_gpu.Bindings.AllocateSet(descriptorSetLayoutHandle: m_pipeline.GroupLayoutHandles[(int)ShaderInterfaceGroup.Pass], name: default, poolHandle: m_pool));
        }
        scope.Complete();
    }

    // One coordinator submits a whole batch and sleeps on its completion; it never holds the device thread.
    // The job owns a scene claim until completion/abandonment. The service owns every slot's fence and readback token.
    internal uint[] Trace(SdfMeshIrradianceScene scene, Vector4[] origins) {
        var job = new Job(scene.Acquire(), origins);
        lock (m_gate) {
            if (m_disposed) { job.Scene.Dispose(); throw new OperationCanceledException(); }
            if (m_pending is not null) { job.Scene.Dispose(); throw new InvalidOperationException("One volume coordinator owns the trace queue."); }
            m_pending = job;
        }
        return job.Answer.Task.GetAwaiter().GetResult();
    }

    public bool TryTake(SdfWorldTables tables, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? job, out double estimatedSeconds) {
        job = null;
        estimatedSeconds = 0;
        lock (m_gate) {
            if (m_pending is not { } pending || m_inFlight || !tables.BindsBackgroundWork) { return false; }
            if (!m_volume.KeepsGpuScene(pending.Scene)) {
                m_pending = null;
                pending.Scene.Dispose();
                m_volume.Counters.Add(SdfMeshIrradiance.GpuCancelled, 1);
                pending.Answer.TrySetCanceled();
                return false;
            }
            if (!tables.TryProbeScene(pending.Scene, out var placements, out var meshes, out var version)) { return false; }
            // Serialized queries make replacing the hierarchy safe: the prior token has been released before
            // the coordinator can enqueue another. No slot descriptor or region is changed while it is read.
            if (!ReferenceEquals(m_scene, pending.Scene) || m_placementVersion != version) {
                var words = pending.Scene.ExportGpu(placements);
                m_sceneRegion?.Dispose();
                m_sceneRegion = tables.CreateProbeSceneRegion(words.Length * 4);
                m_sceneRegion.Write(0, MemoryMarshal.AsBytes(words.AsSpan()));
                m_scene?.Dispose();
                m_scene = pending.Scene.Acquire();
                m_placementVersion = version;
            }
            pending.Meshes = meshes;
            m_pending = null;
            m_inFlight = true;
            job = pending;
            estimatedSeconds = pending.Origins.Length * SdfMeshProbeDepth.HitCount * 1e-8;
            return true;
        }
    }

    public void Record(object job, in SdfGpuWorkRecording recording) {
        var query = (Job)job;
        var slot = m_slots[recording.Slot];
        var bytes = (ulong)query.Origins.Length * SdfMeshProbeDepth.HitWords * 4;
        if (slot.Origins is null || slot.Origins.SizeBytes != (ulong)query.Origins.Length * 16) {
            slot.Origins?.Dispose();
            slot.Origins = m_gpu.BufferFactory.CreateHostVisible(name: default, sizeBytes: (ulong)query.Origins.Length * 16, usage: GpuBufferUsage.Storage);
        }
        if (slot.Hits is null || slot.Hits.SizeBytes < bytes) {
            slot.Hits?.Dispose();
            slot.Hits = m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: bytes, usage: GpuBufferUsage.Storage);
        }
        slot.Identities ??= m_gpu.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 8, usage: GpuBufferUsage.Storage);
        slot.Origins.Write<Vector4>(query.Origins);
        var command = recording.CommandBuffer;
        var copies = new GpuRegionCopyRecording(recording.Recorder, GpuStage.ComputeShader, () => command);
        copies.Record(m_sceneRegion!, recording.Slot, handsToReaders: true);
        copies.Finish();
        Bind(SdfKernelInterfaces.ProbeScene, m_sceneRegion!.Buffer(recording.Slot), 4);
        Bind(SdfWorldPackage.MeshRegion, query.Meshes!, 4);
        Bind(SdfKernelInterfaces.ProbeOrigins, slot.Origins, 16);
        Bind(SdfKernelInterfaces.ProbeDirections, m_directions, 16);
        Bind(SdfKernelInterfaces.ProbeHitsWritten, slot.Hits, 4, written: true);
        Bind(SdfKernelInterfaces.ProbeIdentitiesWritten, slot.Identities, 8, written: true);
        var recorder = recording.Recorder;
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Hits.BufferHandle,
            sourceAccessMask: GpuAccess.TransferRead, sourceStageMask: GpuStage.Transfer,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: slot.Identities.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader,
            destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
        recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: m_pipeline.Handle);
        recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: slot.Set,
            group: (uint)ShaderInterfaceGroup.Pass, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.Dispatch(commandBufferHandle: command,
            groupCountX: (uint)((query.Origins.Length * SdfMeshProbeDepth.HitCount + SdfMeshProbeTrace.GroupSize - 1) / SdfMeshProbeTrace.GroupSize), groupCountY: 1, groupCountZ: 1);
        recording.CopyToReadback(slot.Hits, bytes);

        void Bind(string name, IGpuBuffer buffer, uint stride, bool written = false) {
            var resource = SdfKernelInterfaces.ResourceOf(SdfWorldInterfaces.ProbeTraceLayout, name);
            m_gpu.Bindings.WriteBuffer(binding: resource.Binding, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes,
                descriptorSetHandle: slot.Set, elementStride: stride, kind: written ? GpuBindingKind.ReadWriteBuffer : GpuBindingKind.ReadOnlyBuffer);
        }
    }

    public void Complete(object job, SdfGpuWorkReadback readback) {
        var query = (Job)job;
        var result = new uint[readback.Length / 4];
        try { readback.Read(MemoryMarshal.AsBytes(result.AsSpan())); }
        finally { readback.Release(); }
        m_volume.Counters.Add(SdfMeshIrradiance.GpuRays, query.Origins.Length * SdfMeshProbeDepth.HitCount);
        if (readback.GpuSeconds is { } seconds) { m_volume.Counters.Add(SdfMeshIrradiance.GpuTraceNanoseconds, (long)(seconds * 1e9)); }
        lock (m_gate) { m_inFlight = false; }
        query.Scene.Dispose();
        query.Answer.SetResult(result);
    }
    public void Abandon(object job) {
        var query = (Job)job;
        lock (m_gate) { m_inFlight = false; }
        query.Scene.Dispose();
        query.Answer.TrySetCanceled();
    }
    public GpuMemoryBytes OwnedBytes {
        get {
            var bytes = m_sceneRegion?.OwnedBytes ?? default;
            var device = bytes.DeviceLocal;
            var host = bytes.HostVisible + m_directions.SizeBytes;
            foreach (var slot in m_slots) { device += (slot.Hits?.SizeBytes ?? 0) + (slot.Identities?.SizeBytes ?? 0); host += slot.Origins?.SizeBytes ?? 0; }
            return new GpuMemoryBytes(DeviceLocal: device, HostVisible: host);
        }
    }
    public void Dispose() {
        lock (m_gate) {
            if (m_disposed) { return; }
            m_disposed = true;
            if (m_pending is { } pending) { pending.Scene.Dispose(); pending.Answer.TrySetCanceled(); m_pending = null; }
        }
        foreach (var slot in m_slots) { slot.Hits?.Dispose(); slot.Origins?.Dispose(); slot.Identities?.Dispose(); }
        m_sceneRegion?.Dispose();
        m_scene?.Dispose();
        m_directions.Dispose();
        m_gpu.Bindings.DestroyPool(m_pool);
    }
    private sealed class Job(SdfMeshIrradianceScene scene, Vector4[] origins) {
        public readonly SdfMeshIrradianceScene Scene = scene;
        public readonly Vector4[] Origins = origins;
        public readonly TaskCompletionSource<uint[]> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IGpuBuffer? Meshes;
    }
    private sealed class Slot(nint set) {
        public readonly nint Set = set;
        public IGpuStorageBuffer? Origins;
        public IGpuBuffer? Hits;
        public IGpuBuffer? Identities;
    }
}

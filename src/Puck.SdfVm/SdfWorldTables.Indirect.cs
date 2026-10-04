using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly Lock m_indirectGate = new();
    private readonly List<SdfIndirectCache> m_retiredIndirect = [];

    private SdfIndirectCache? m_indirect;
    private bool m_indirectClear;

    private uint m_indirectEpoch = 1;

    /// <summary>Gets the active residency cache, absent while the lever is off.</summary>
    public SdfIndirectCache? Indirect => m_indirect;
    /// <summary>Gets the active cache's model layout.</summary>
    public SdfIndirectLayout? IndirectLayout => m_indirect?.Layout;
    /// <summary>Gets the buffer the indirect package publishes.</summary>
    public IGpuBuffer? IndirectBuffer => m_indirect?.Buffer;
    /// <summary>Gets the current epoch, invalidated by every program upload.</summary>
    public uint IndirectEpoch => m_indirectEpoch;
    /// <summary>Gets the retiring caches still tracked for graph readers, excluding the active cache.</summary>
    public int RetiringIndirectCacheCount {
        get { lock (m_indirectGate) { return m_retiredIndirect.Count; } }
    }
    /// <summary>Gets the active and retiring caches' allocations, including their region rings.</summary>
    public GpuMemoryBytes IndirectBytes {
        get {
            lock (m_indirectGate) {
                m_retiredIndirect.RemoveAll(match: static cache => cache.IsDisposed);
                return m_retiredIndirect.Aggregate((m_indirect?.Bytes ?? default), (bytes, cache) => (bytes + cache.Bytes));
            }
        }
    }

    internal SdfIndirectCache? RetainIndirect() {
        lock (m_indirectGate) { return m_indirect?.Retain(); }
    }
    internal void SetIndirect(SdfIndirectTier tier, float farDistance, WorkCounterSet work) {
        lock (m_indirectGate) {
            m_retiredIndirect.RemoveAll(match: static cache => cache.IsDisposed);
            if (((m_indirect?.Layout.Tier ?? SdfIndirectTier.Off) == tier) && ((m_indirect is null) || (m_indirect.FarDistance == farDistance))) { return; }
            var previous = m_indirect;

            m_indirect = ((tier == SdfIndirectTier.Off) ? null : new SdfIndirectCache(layout: new SdfIndirectLayout(tier: tier), gpu: m_gpu,
                profile: m_deviceContext.MemoryProfile, copyPipeline: m_regionCopyPipeline, farDistance: farDistance, work: work, epoch: m_indirectEpoch));
            m_indirectClear = (m_indirect is not null);
            m_deviceContext.TryWaitIdle();
            if (previous is not null) {
                previous.Dispose();
                if (!previous.IsDisposed) { m_retiredIndirect.Add(item: previous); }
            }
        }
    }

    private void DisposeIndirect() {
        lock (m_indirectGate) {
            m_indirect?.Dispose();
            m_indirect = null;
            m_retiredIndirect.Clear();
        }
    }
    private void ResetIndirect() {
        m_indirectEpoch = ((m_indirectEpoch % 0xFFFFFF) + 1);
        m_indirect?.Reset(epoch: m_indirectEpoch);
        m_indirectClear = (m_indirect is not null);
    }
    // The residency consumes the editor's queued reset only after it has a renderable frame.
    internal void ResetIndirectPresentation() => ResetIndirect();

    internal void PlanIndirect(SdfFrame frame) {
        if (m_indirect is not { } cache) { return; }
        cache.Plan(inputs: IndirectInputs(frame: frame));
        cache.AdmitReceivers();
        PlanIndirectLighting(cache, frame);
    }

    /// <summary>Collects demand from the existing program's conservative segment bounds and every camera.</summary>
    public static IrradianceFrameInputs IndirectInputs(SdfFrame frame) {
        ArgumentNullException.ThrowIfNull(frame);
        var cameras = frame.Views.Select(selector: view => Point(value: view.Camera.Position)).ToArray();
        var bounds = new List<IrradianceSphere>();
        var low = new Double3(X: double.PositiveInfinity, Y: double.PositiveInfinity, Z: double.PositiveInfinity);
        var high = new Double3(X: double.NegativeInfinity, Y: double.NegativeInfinity, Z: double.NegativeInfinity);
        var unbounded = false;

        for (var index = 0; (index < frame.Program.SkipSegmentCount); index++) {
            var sphere = frame.Program.SegmentSkipSphere(segment: index);
            var center = sphere.Center;

            if ((sphere.Mode == SdfProgram.BoundModeDynamic) && (sphere.Slot < frame.DynamicTransforms.Count)) { center += frame.DynamicTransforms[sphere.Slot].Position; }
            if ((sphere.Mode == SdfProgram.BoundModeNone) || !float.IsFinite(f: sphere.Radius)) { unbounded = true; continue; }
            var position = Point(value: center);
            var extent = new Double3(X: sphere.Radius, Y: sphere.Radius, Z: sphere.Radius);

            bounds.Add(item: new IrradianceSphere(Center: position, Radius: sphere.Radius));
            low = Min(a: low, b: (position - extent));
            high = Max(a: high, b: (position + extent));
        }
        if (unbounded || (bounds.Count == 0)) {
            var reach = new Double3(X: frame.FarDistance, Y: frame.FarDistance, Z: frame.FarDistance);

            foreach (var camera in cameras) { low = Min(a: low, b: (camera - reach)); high = Max(a: high, b: (camera + reach)); }
            bounds.Add(item: new IrradianceSphere(Center: default, Radius: double.PositiveInfinity));
        }
        if (cameras.Length == 0) { low = default; high = default; }
        return new IrradianceFrameInputs(Bounds: bounds, Cameras: cameras, WorldMax: high, WorldMin: low);
    }

    private static Double3 Point(Vector3 value) => new(X: value.X, Y: value.Y, Z: value.Z);
    private static Double3 Min(Double3 a, Double3 b) => new(X: Math.Min(val1: a.X, val2: b.X), Y: Math.Min(val1: a.Y, val2: b.Y), Z: Math.Min(val1: a.Z, val2: b.Z));
    private static Double3 Max(Double3 a, Double3 b) => new(X: Math.Max(val1: a.X, val2: b.X), Y: Math.Max(val1: a.Y, val2: b.Y), Z: Math.Max(val1: a.Z, val2: b.Z));
    private void RecordIndirectClear(nint commandBuffer) {
        if (!m_indirectClear || (m_indirect is not { } cache)) { return; }
        m_gpu.Recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: cache.Buffer.BufferHandle,
            sourceAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite, destinationAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.ComputeShader, destinationStageMask: GpuStage.Transfer);
        m_gpu.Recorder.ClearStorageBuffer(commandBufferHandle: commandBuffer, bufferHandle: cache.Buffer.BufferHandle, sizeBytes: cache.Buffer.SizeBytes);
        m_gpu.Recorder.TransitionBuffer(commandBufferHandle: commandBuffer, bufferHandle: cache.Buffer.BufferHandle,
            sourceAccessMask: GpuAccess.TransferWrite, destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.Transfer, destinationStageMask: GpuStage.ComputeShader);
        m_indirectClear = false;
    }
}

using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One submitted lighting-source publication. A consumer pins its buffers through declared graph dependencies
/// before the next write; sky projection and screen reduction have distinct publication owners.</summary>
/// <param name="Owner">The actual producer of this source, never a displayed view.</param>
/// <param name="Sequence">Its monotonically increasing submission sequence.</param>
/// <param name="Fence">The actual submission's fence, valid until its recording slot is reused.</param>
/// <param name="Tainted">Whether an acquired contributing source contains unfilled external content.</param>
public readonly record struct SdfEnvironmentSubmission(object Owner, long Sequence, IGpuSubmissionFence Fence, bool Tainted) {
    /// <summary>Gets the actual source publication identity.</summary>
    public GpuImagePublication Publication => new(Owner: Owner, Sequence: Sequence);
}

// One map and coefficient table per residency. The graph's environment producer writes both after acquiring its
// panorama inputs; upload owns only the packed source tables and the CPU candidate projection's host counts.
public sealed partial class SdfWorldTables {
    private readonly SkyEnvironmentPass m_skyEnvironment;

    /// <summary>Gets the map and coefficient payload bytes, one pair however many views read it.</summary>
    public static int SkyEnvironmentBytes => SdfSkyEnvironment.PayloadBytes;
    /// <summary>Gets how many environment projections have actually been submitted.</summary>
    public long SkyEnvironmentRenders => m_skyEnvironment.Renders;
    /// <summary>Gets the shared two-plane environment map. Its graph producer owns access ordering.</summary>
    public IGpuBuffer SkyEnvironmentMap => m_skyEnvironment.Map;
    /// <summary>Gets the shared nine-coefficient irradiance table. Its graph producer owns access ordering.</summary>
    public IGpuBuffer SkyEnvironmentCoefficients => m_skyEnvironment.Coefficients;
    /// <summary>Gets the latest submitted projection, or null before any projection. A pending newer submission means
    /// the buffers no longer describe an older completed publication.</summary>
    public SdfEnvironmentSubmission? SubmittedSkyEnvironment => m_skyEnvironment.Submitted;
    /// <summary>Gets the latest projection whose actual fence was observed complete. Completion is latched before its
    /// recorder reuses that fence; it is not inferred from a displayed view.</summary>
    public GpuImagePublication CompletedSkyEnvironment { get { m_skyEnvironment.Poll(); return m_skyEnvironment.Completed; } }

    internal bool SkyEnvironmentDemand => m_skyEnvironment.Demand;
    internal bool SkyEnvironmentHasImages => SdfSkyEnvironmentRefresh.HasImages(block: m_skyRecord[0], layers: m_skyLayerRecords);
    internal bool SkyEnvironmentOwes => m_skyEnvironment.Owes;
    internal ReadOnlySpan<bool> SkyEnvironmentScreens => m_skyEnvironment.Screens;

    private void PrepareSkyEnvironment(bool physical) => m_skyEnvironment.Prepare(physical: physical);
    internal ulong? SkyEnvironmentSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) {
        m_skyEnvironment.Poll();
        var known = m_skyEnvironment.ObserveSources(residency: residency, view: view, reads: reads);
        return known ? unchecked((ulong)(m_skyEnvironment.Renders + (m_skyEnvironment.Owes ? 1 : 0))) : null;
    }
    internal long SubmitSkyEnvironment(IGpuSubmissionFence fence) => m_skyEnvironment.Publish(fence: fence);
    internal void PollSkyEnvironment() => m_skyEnvironment.Poll();
    internal void WithdrawSkyEnvironment(long sequence) => m_skyEnvironment.Withdraw(sequence: sequence);

    private void RecordSkyEnvironmentDecision() {
        if (m_skyEnvironment.Projected) {
            m_work.EnterPass(pass: EnvironmentPass);
            m_work.CountEnvironmentProjection(texels: SdfSkyEnvironment.Texels, skipped: m_skyEnvironment.Skipped);
            m_work.LeavePass();
            m_skyEnvironment.Projected = false;
        } else {
            m_work.SkipPass(pass: EnvironmentPass);
        }
    }
    // A graph slot rewrites only after its own fence retires. These source regions were copied by this frame's upload.
    internal void BindSkyEnvironment(nint set, IGpuBindings bindings) {
        var layout = SdfWorldInterfaces.EnvironmentParameters.Layout;
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.Sky, buffer: m_skyRegion.Buffer(slot: CurrentSlot), bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.SkyLayers, buffer: m_skyLayerRegion.Buffer(slot: CurrentSlot), bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfWorldPackage.ScreenMappings, buffer: m_screenMappingRegion.Buffer(slot: CurrentSlot), bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.SkyEnvironment, buffer: m_skyEnvironment.Map, bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.SkyEnvironmentWritten, buffer: m_skyEnvironment.Map, bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.SkyCoefficientsWritten, buffer: m_skyEnvironment.Coefficients, bindings: bindings);
        WriteInterfaceBuffer(set: set, layout: layout, member: SdfKernelInterfaces.ScreenEmissionWritten, buffer: ScreenEmission, bindings: bindings);
        var binding = SdfKernelInterfaces.BindingOf(layout: layout, member: SdfWorldPackage.Samplers);
        for (var index = 0; index < m_samplers.Length; index++) {
            bindings.WriteSampler(arrayElement: (uint)index, binding: binding, descriptorSetHandle: set, samplerHandle: m_samplers[index]);
        }
    }

    private sealed class SkyEnvironmentPass : IDisposable {
        private readonly SdfWorldTables m_tables;
        private readonly SdfSkyEnvironmentRefresh m_refresh = new();
        private readonly GpuImagePublication[] m_sources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly GpuImagePublication[] m_renderedSources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly byte[] m_renderedMappings = new byte[MaxScreenSurfaces * ScreenMappingByteLength];
        private bool m_physical;
        private bool m_tainted;

        public SkyEnvironmentPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_tables = tables;
            Map = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "sky-environment", detail: "map"),
                sizeBytes: SdfSkyEnvironment.MapBytes, usage: GpuBufferUsage.Storage));
            Coefficients = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "sky-environment", detail: "coefficients"),
                sizeBytes: SdfSkyEnvironment.CoefficientBytes, usage: GpuBufferUsage.Storage));
        }

        public IGpuBuffer Map { get; }
        public IGpuBuffer Coefficients { get; }
        public bool[] Screens { get; } = new bool[MaxScreenSurfaces];
        public bool Demand { get; private set; }
        public bool Owes { get; private set; }
        public bool Projected { get; set; }
        public bool Skipped { get; private set; }
        public long Renders { get; private set; }
        public SdfEnvironmentSubmission? Submitted { get; private set; }
        public GpuImagePublication Completed { get; private set; }

        public void Prepare(bool physical) {
            var sky = m_tables.m_skyRecord[0];
            m_physical = physical;
            Demand = physical || sky.Ambient > 0f || sky.Reflection > 0f || sky.HazeExtinction > 0f ||
                (sky.FogExtinction > 0f && (sky.AirFlags & SdfAir.FogColorAuthored) == 0);
            Owes = m_refresh.Owes(block: sky, layers: m_tables.m_skyLayerRecords, physical: physical);
            if (m_refresh.Projected) { Projected = true; Skipped = m_refresh.Skipped; }
        }
        public bool ObserveSources(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) {
            Array.Clear(array: Screens);
            Array.Clear(array: m_sources);
            m_tainted = false;
            var known = true;
            var changed = false;
            var layers = m_tables.m_skyLayerRecords;
            var count = Math.Min((int)m_tables.m_skyRecord[0].LayerCount, layers.Length);
            for (var index = 0; index < count; index++) {
                ref var layer = ref layers[index];
                if (layer.Kind != SdfSkyLayerKind.Panorama || layer.Opacity <= 0f || !SdfSkyEnvironment.IsLit(layer: layer)) { continue; }
                var screen = SdfSky.PayloadOf<SdfSkyPanorama>(layer: ref layer).Screen;
                if ((uint)screen < MaxScreenSurfaces) { Screens[screen] = true; }
            }
            for (var screen = 0; screen < Screens.Length; screen++) {
                if (!Screens[screen]) { continue; }
                // A residency projects one canonical panorama, independent of the consuming camera's fit.
                if (residency.ScreenSources is { } sources && sources.Screens.Contains(screen) &&
                    sources.ReadOf(view: view, screen: screen) is { } producer && reads is not null &&
                    reads.IndexOf(producer: producer) is var read and >= 0) {
                    var input = reads[read];
                    m_tainted |= input.Tainted;
                    m_sources[screen] = input.Publication;
                    if (input.Lease.ImageViewHandle != 0 && !input.Publication.IsKnown) { known = false; }
                }
                var offset = screen * ScreenMappingByteLength;
                if (!m_tables.m_screenMappingRegion.Contents.Slice(offset, ScreenMappingByteLength)
                    .SequenceEqual(m_renderedMappings.AsSpan(offset, ScreenMappingByteLength))) { changed = true; }
            }
            changed |= !m_sources.AsSpan().SequenceEqual(m_renderedSources);
            Owes = m_refresh.Owes(block: m_tables.m_skyRecord[0], layers: layers, physical: m_physical, imageChanged: changed || !known);
            return known;
        }
        public long Publish(IGpuSubmissionFence fence) {
            // Record polled after the graph waited this slot and before Submit reset its fence.
            m_refresh.Rendered();
            m_sources.CopyTo(array: m_renderedSources, index: 0);
            m_tables.m_screenMappingRegion.Contents[..m_renderedMappings.Length].CopyTo(m_renderedMappings);
            Owes = false;
            Submitted = new SdfEnvironmentSubmission(Owner: m_tables, Sequence: ++Renders, Fence: fence, Tainted: m_tainted);
            return Renders;
        }
        public void Poll() {
            if (Submitted is { } submitted && submitted.Publication != Completed && submitted.Fence.IsSignaled) {
                Completed = submitted.Publication;
            }
        }
        public void Withdraw(long sequence) {
            // Recorder retirement can follow device loss, so do not query its fence here. A latched complete source
            // remains valid; an unobserved submission must be projected again by the next recorder.
            if (Submitted is { } submitted && submitted.Sequence == sequence && submitted.Publication != Completed) {
                Forget();
            }
        }
        public void Forget() {
            m_refresh.Forget();
            Owes = true;
            Submitted = null;
            Completed = default;
        }
        public void Dispose() { Forget(); Coefficients.Dispose(); Map.Dispose(); }
    }
}

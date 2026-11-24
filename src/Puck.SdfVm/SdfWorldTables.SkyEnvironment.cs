using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Maths;
using Puck.Shaders;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>One submitted lighting-source publication. A consumer pins its buffers through declared graph dependencies
/// before the next write; sky projection and screen reduction have distinct publication owners.</summary>
/// <param name="Owner">The actual producer of this source, never a displayed view.</param>
/// <param name="Sequence">Its monotonically increasing submission sequence.</param>
/// <param name="Tainted">Whether an acquired contributing source contains unfilled external content.</param>
public readonly record struct SdfEnvironmentSubmission(object Owner, long Sequence, bool Tainted) {
    /// <summary>Gets the actual source publication identity.</summary>
    public ReferenceRevision Publication => new(Owner: Owner, Revision: Sequence);
}
// One map and coefficient table per residency. The graph's environment producer writes both after acquiring its
// panorama inputs; upload owns only the packed source tables and the CPU candidate projection's host counts.
public sealed partial class SdfWorldTables {
    private readonly SkyEnvironmentPass m_skyEnvironment;

    /// <summary>Gets the map and coefficient payload bytes, one pair however many views read it.</summary>
    public static int SkyEnvironmentBytes => SdfSkyEnvironment.PayloadBytes;
    /// <summary>Gets how many environment projections have actually been submitted.</summary>
    public long SkyEnvironmentRenders => m_skyEnvironment.Publication.Renders;
    /// <summary>Gets the shared two-plane environment map. Its graph producer owns access ordering.</summary>
    public IGpuBuffer SkyEnvironmentMap => m_skyEnvironment.Map;
    /// <summary>Gets the shared nine-coefficient irradiance table. Its graph producer owns access ordering.</summary>
    public IGpuBuffer SkyEnvironmentCoefficients => m_skyEnvironment.Coefficients;
    /// <summary>Gets the latest submitted projection, or null before any projection. A pending newer submission means
    /// the buffers no longer describe an older completed publication.</summary>
    public SdfEnvironmentSubmission? SubmittedSkyEnvironment => m_skyEnvironment.Publication.Submitted;
    /// <summary>Gets the latest projection whose submission its render node proved complete while it was still the latest
    /// submitted; it is not inferred from a displayed view.</summary>
    public ReferenceRevision CompletedSkyEnvironment => m_skyEnvironment.Publication.Completed;

    internal bool SkyEnvironmentDemand => m_skyEnvironment.Demand;
    internal bool SkyEnvironmentHasImages => SdfSkyEnvironmentRefresh.HasImages(block: m_skyRecord[0], layers: m_skyLayerRecords);
    internal bool SkyEnvironmentOwes => (m_skyEnvironment.Owes || m_skyEnvironment.Forced);
    internal bool SkyEnvironmentCanRecord => m_skyEnvironment.CanRecord;
    internal UInt128 SkyEnvironmentScreens => m_skyEnvironment.Screens;

    private void PrepareSkyEnvironment(bool physical) => m_skyEnvironment.Prepare(physical: physical);

    internal ulong? SkyEnvironmentSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads, in DeliveryLatch latch) {
        var known = m_skyEnvironment.ObserveSources(latch: in latch, reads: reads, residency: residency, view: view);

        return (known ? unchecked((ulong)(m_skyEnvironment.Publication.Renders + (m_skyEnvironment.Owes ? 1 : 0))) : null);
    }
    internal long SubmitSkyEnvironment() => m_skyEnvironment.Publish();
    // Records the projection this frame's recording draws, which a view recorded after it reads (EnvironmentPublication).
    internal void RecordSkyEnvironment(long frame) => m_skyEnvironment.Publication.Record(frame: frame, tainted: m_skyEnvironment.Tainted);
    // The projection a view of package frame `frame` samples.
    internal SdfEnvironmentSubmission? SkyEnvironmentFor(long frame) => m_skyEnvironment.Publication.Current(frame: frame);
    internal void CompleteSkyEnvironment(long sequence) => m_skyEnvironment.Publication.Complete(sequence: sequence);
    /// <summary>Makes each lighting source whose submission holds unfilled external content (the sky environment's
    /// projection, the screen emission's reduction) derive itself again on its next recording, from whatever its sources
    /// are then, the capture gate's fills while a capture is armed. The tainted submission stands, and stays reported as
    /// tainted, until a new one replaces it.</summary>
    /// <returns>Whether either source was tainted.</returns>
    public bool ProjectTaintedSourcesAgain() {
        var sky = (m_skyEnvironment.Publication.Submitted is { Tainted: true });
        var screens = (m_screenEmission.Publication.Submitted is { Tainted: true });

        m_skyEnvironment.Forced |= sky;
        m_screenEmission.Forced |= screens;

        return (sky || screens);
    }
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

        for (var index = 0; (index < m_samplers.Length); index++) {
            bindings.WriteSampler(arrayElement: ((uint)index), binding: binding, descriptorSetHandle: set, samplerHandle: m_samplers[index]);
        }
    }

    private sealed class SkyEnvironmentPass : IDisposable {
        private readonly SdfWorldTables m_tables;

        private readonly SdfSkyEnvironmentRefresh m_refresh = new();
        private readonly ScreenSourceWatch m_watch;

        private bool m_physical;

        public SkyEnvironmentPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_tables = tables;
            m_watch = new ScreenSourceWatch(tables: tables);
            Publication = new EnvironmentPublication(owner: tables);
            Map = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "sky-environment", detail: "map"),
                sizeBytes: SdfSkyEnvironment.MapBytes, usage: GpuBufferUsage.Storage));
            Coefficients = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "sky-environment", detail: "coefficients"),
                sizeBytes: SdfSkyEnvironment.CoefficientBytes, usage: GpuBufferUsage.Storage));
        }

        public IGpuBuffer Coefficients { get; }
        public IGpuBuffer Map { get; }

        // The screens a lit panorama layer reads, a bit a screen.
        public UInt128 Screens => m_watch.Screens;

        public EnvironmentPublication Publication { get; }
        // Whether the latest observation read a source with unfilled external content.
        public bool Tainted => m_watch.Tainted;
        public bool Demand { get; private set; }
        public bool Owes { get; private set; }
        public bool Projected { get; set; }
        public bool Skipped { get; private set; }

        public bool CanRecord { get; private set; } = true;
        // Whether the next observation projects again whatever its sources' publications say: a tainted projection a
        // capture discards (ProjectTaintedSourcesAgain), whose filled replacements may carry no known publication.
        public bool Forced { get; set; }

        public void Prepare(bool physical) {
            var sky = m_tables.m_skyRecord[0];

            m_physical = physical;
            Demand = (physical || (sky.Ambient > 0f) || (sky.Reflection > 0f) || (sky.HazeExtinction > 0f) ||
                ((sky.FogExtinction > 0f) && ((sky.AirFlags & SdfAir.FogColorAuthored) == 0)));
            m_refresh.Advance();
            Owes = m_refresh.Owes(block: sky, layers: m_tables.m_skyLayerRecords, physical: physical);
            if (m_refresh.Projected) { Projected = true; Skipped = m_refresh.Skipped; }
        }
        public bool ObserveSources(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads, in DeliveryLatch latch) {
            m_watch.Clear();
            CanRecord = true;
            var known = true;
            var layers = m_tables.m_skyLayerRecords;
            var count = Math.Min(val1: ((int)m_tables.m_skyRecord[0].LayerCount), val2: layers.Length);

            for (var index = 0; (index < count); index++) {
                ref var layer = ref layers[index];

                if ((layer.Kind != SdfSkyLayerKind.Panorama) || (layer.Opacity <= 0f) || !SdfSkyEnvironment.IsLit(layer: layer)) { continue; }
                var screen = SdfSky.PayloadOf<SdfSkyPanorama>(layer: ref layer).Screen;

                if (((uint)screen) < MaxScreenSurfaces) { m_watch.Read(screen: screen); }
            }
            foreach (var screen in BitWords.Members(mask: Screens)) {
                // A residency projects one canonical panorama, independent of the consuming camera's fit.
                if ((residency.ScreenSources is { } sources) && sources.Screens.Contains(value: screen) &&
                    (residency.ScreenRead(reads: reads, screen: screen, view: view) is var read and >= 0) && (reads is not null)) {
                    var input = reads[read];

                    if (input.TryRead(latch: in latch, read: out var resolved) && (resolved.ImageViewHandle != 0)) {
                        m_watch.Observe(publication: resolved.Publication, screen: screen, tainted: input.Label.External);
                        if (!resolved.Publication.IsPublished()) { known = false; }
                    } else {
                        m_watch.Observe(publication: default, screen: screen, tainted: false);
                    }
                }
            }
            CanRecord &= (known || Forced);
            Owes = m_refresh.Owes(block: m_tables.m_skyRecord[0], layers: layers, physical: m_physical, imageChanged: ((m_watch.ChangedScreens() != UInt128.Zero) || !known || Forced));
            return known;
        }
        public long Publish() {
            m_refresh.Rendered();
            m_watch.Commit();
            Owes = false;
            Forced = false;
            return Publication.Publish(tainted: m_watch.Tainted);
        }
        // An unproven submission must be projected again by the next recorder.
        public void Withdraw(long sequence) {
            if (Publication.Unproven(sequence: sequence)) { Forget(); }
        }
        public void Forget() {
            m_refresh.Forget();
            Owes = true;
            Publication.Forget();
        }
        public void Dispose() { Forget(); Coefficients.Dispose(); Map.Dispose(); }
    }

    // One lighting source's submissions: the latest submitted, and the latest its render node proved complete while it was
    // still the latest submitted, since a newer submission owns the source's buffers.
    //
    // A recording that draws the source records what its submission will carry (Record) before its node commits, so a
    // view of the same frame reads the classification of the buffer it samples (Current). That holds because the source's
    // recorder and every view of its residency share one recording domain, which the runtime records on one thread in
    // schedule order, the source first, and a view the source's frame does not commit with is abandoned with it
    // (RenderGraphRuntime.Reservations.cs). A recorded classification belongs to its package frame only.
    private sealed class EnvironmentPublication(object owner) {
        private SdfEnvironmentSubmission? m_recorded;
        private long m_recordedFrame = -1;

        public SdfEnvironmentSubmission? Submitted { get; private set; }
        public ReferenceRevision Completed { get; private set; }
        public long Renders { get; private set; }

        // The submission a view of package frame `frame` samples: the one recorded this frame, else the latest submitted.
        public SdfEnvironmentSubmission? Current(long frame) => ((m_recordedFrame == frame) ? m_recorded : Submitted);
        public void Record(long frame, bool tainted) {
            m_recorded = new SdfEnvironmentSubmission(Owner: owner, Sequence: (Renders + 1), Tainted: tainted);
            m_recordedFrame = frame;
        }
        public long Publish(bool tainted) {
            Submitted = new SdfEnvironmentSubmission(Owner: owner, Sequence: ++Renders, Tainted: tainted);
            m_recorded = null;
            m_recordedFrame = -1;
            return Renders;
        }
        public void Complete(long sequence) {
            if (Submitted is { } submitted && (submitted.Sequence == sequence)) { Completed = submitted.Publication; }
        }
        // Whether the sequence is the latest submission and no proof completed it: a recorder released after a device loss
        // leaves it so. A latched complete source remains valid.
        public bool Unproven(long sequence) => ((Submitted is { } submitted) && (submitted.Sequence == sequence) && (submitted.Publication != Completed));
        public void Forget() {
            Submitted = null;
            Completed = default;
            m_recorded = null;
            m_recordedFrame = -1;
        }
    }
}

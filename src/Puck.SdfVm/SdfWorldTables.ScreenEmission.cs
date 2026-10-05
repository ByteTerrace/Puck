using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly ScreenEmissionPass m_screenEmission;

    /// <summary>Gets the one GPU image reduction shared by every direct screen light and finite solve.</summary>
    public IGpuBuffer ScreenEmission => m_screenEmission.Buffer;
    /// <summary>Gets the actual submitted screen reduction, independent of sky projection's cadence.</summary>
    public SdfEnvironmentSubmission? SubmittedScreenEmission => m_screenEmission.Submitted;
    /// <summary>Gets the latest screen reduction whose actual submission fence has completed.</summary>
    public GpuImagePublication CompletedScreenEmission { get { m_screenEmission.Poll(); return m_screenEmission.Completed; } }

    internal SdfScreenClosureMember? ScreenClosure { get; set; }
    internal bool ScreenClosureInputsChanged => m_screenEmission.IndependentChanged;
    internal uint ScreenEmissionWriteMask => (ScreenClosure?.WriteMask ?? uint.MaxValue);
    internal bool ScreenEmissionCanRecord => m_screenEmission.CanRecord;

    internal void RestartScreenClosureSources() { m_screenEmission.Forget(); m_skyEnvironment.RestartClosure(); }

    internal bool ScreenEmissionOwes => m_screenEmission.Owes;
    internal bool ScreenEmissionHasImages => m_screenBound.Any(predicate: static bound => bound);
    internal ReadOnlySpan<bool> ScreenEmissionScreens => m_screenEmission.Screens;

    internal ulong? ScreenEmissionSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) =>
        m_screenEmission.Observe(reads: reads, residency: residency, view: view);
    internal long SubmitScreenEmission(IGpuSubmissionFence fence) => m_screenEmission.Publish(fence: fence);
    internal void WithdrawScreenEmission(long sequence) => m_screenEmission.Withdraw(sequence: sequence);

    private sealed class ScreenEmissionPass : IDisposable {
        private readonly SdfWorldTables m_tables;

        private readonly GpuImagePublication[] m_sources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly GpuImagePublication[] m_renderedSources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly byte[] m_mappings = new byte[(MaxScreenSurfaces * ScreenMappingByteLength)];
        private readonly bool[] m_tainted = new bool[MaxScreenSurfaces];
        private readonly bool[] m_renderedTainted = new bool[MaxScreenSurfaces];

        public ScreenEmissionPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_tables = tables;
            Buffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "screen-emission"),
                sizeBytes: SdfScreenEmission.Bytes, usage: GpuBufferUsage.Storage));
        }

        public IGpuBuffer Buffer { get; }

        public bool[] Screens { get; } = new bool[MaxScreenSurfaces];
        public bool Owes { get; private set; } = true;

        public GpuImagePublication Completed { get; private set; }
        public bool IndependentChanged { get; private set; }
        public long Renders { get; private set; }
        public SdfEnvironmentSubmission? Submitted { get; private set; }

        public bool CanRecord { get; private set; } = true;

        public ulong? Observe(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) {
            Poll();
            Array.Clear(array: m_sources);
            Array.Clear(array: Screens);
            Array.Clear(array: m_tainted);
            var known = true;

            CanRecord = true;
            IndependentChanged = false;
            var closure = m_tables.ScreenClosure;

            if (residency.ScreenSources is { } sources) {
                foreach (var screen in sources.Screens) {
                    if (!sources.Emits(screen: screen) || (sources.ReadOf(screen: screen, view: view) is not { } producer) || (reads is null)) { continue; }
                    var index = reads.IndexOf(producer: producer);

                    if (index < 0) { continue; }
                    var input = reads[index];

                    m_tainted[screen] = input.Tainted;
                    Screens[screen] = (input.Lease.ImageViewHandle != 0);
                    m_sources[screen] = input.Publication;
                }
            }
            var changed = false;

            for (var screen = 0; (screen < MaxScreenSurfaces); screen++) {
                var bit = (1u << screen);
                var offset = (screen * ScreenMappingByteLength);
                var different = ((m_sources[screen] != m_renderedSources[screen]) || (m_tainted[screen] != m_renderedTainted[screen]) ||
                    !m_tables.m_screenMappingRegion.Contents.Slice(length: ScreenMappingByteLength, start: offset)
                        .SequenceEqual(other: m_mappings.AsSpan(length: ScreenMappingByteLength, start: offset)));

                changed |= different;
                if (closure is null) {
                    known &= (!Screens[screen] || m_sources[screen].IsKnown);
                    continue;
                }
                if ((closure.DerivedMask & bit) == 0) { IndependentChanged |= different; }
                if ((closure.WriteMask & bit) == 0) { Screens[screen] = false; continue; }
                if ((closure.DerivedMask & bit) == 0) {
                    known &= (!Screens[screen] || m_sources[screen].IsKnown);
                    continue;
                }
                if (closure.ZeroDerived) { Screens[screen] = false; m_sources[screen] = default; m_tainted[screen] = false; } else if (!closure.Accepts(screen, m_sources[screen])) { CanRecord = false; }
            }
            CanRecord &= known;
            Owes = ((closure is null) ? ((Submitted is null) || !known || changed) : (closure.WriteMask != 0));
            return (known ? unchecked((ulong)(Renders + (Owes ? 1 : 0))) : null);
        }
        public long Publish(IGpuSubmissionFence fence) {
            var writeMask = m_tables.ScreenEmissionWriteMask;

            for (var screen = 0; (screen < MaxScreenSurfaces); screen++) {
                if ((writeMask & (1u << screen)) == 0) { continue; }
                m_renderedSources[screen] = m_sources[screen];
                m_renderedTainted[screen] = m_tainted[screen];
                var offset = (screen * ScreenMappingByteLength);

                m_tables.m_screenMappingRegion.Contents.Slice(length: ScreenMappingByteLength, start: offset).CopyTo(destination: m_mappings.AsSpan(length: ScreenMappingByteLength, start: offset));
            }
            Owes = false;
            Submitted = new(Owner: this, Sequence: ++Renders, Fence: fence, Tainted: m_renderedTainted.Any(predicate: static tainted => tainted));
            m_tables.ScreenClosure?.Published();
            return Renders;
        }
        public void Poll() {
            if ((Submitted is { } submitted) && (submitted.Publication != Completed) && submitted.Fence.IsSignaled) {
                Completed = submitted.Publication;
            }
        }
        public void Withdraw(long sequence) {
            if ((Submitted is { } submitted) && (submitted.Sequence == sequence) && (submitted.Publication != Completed)) { Forget(); }
        }
        public void Forget() { Submitted = null; Completed = default; Owes = true; }
        public void Dispose() { Forget(); Buffer.Dispose(); }
    }
}

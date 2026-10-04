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
    internal bool ScreenEmissionOwes => m_screenEmission.Owes;
    internal bool ScreenEmissionHasImages => m_screenBound.Any(static bound => bound);
    internal ReadOnlySpan<bool> ScreenEmissionScreens => m_screenEmission.Screens;
    internal ulong? ScreenEmissionSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) =>
        m_screenEmission.Observe(residency, view, reads);
    internal long SubmitScreenEmission(IGpuSubmissionFence fence) => m_screenEmission.Publish(fence);
    internal void WithdrawScreenEmission(long sequence) => m_screenEmission.Withdraw(sequence);

    private sealed class ScreenEmissionPass : IDisposable {
        private readonly SdfWorldTables m_tables;
        private readonly GpuImagePublication[] m_sources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly GpuImagePublication[] m_renderedSources = new GpuImagePublication[MaxScreenSurfaces];
        private readonly byte[] m_mappings = new byte[MaxScreenSurfaces * ScreenMappingByteLength];
        private GpuImagePublication m_completed;
        private bool m_tainted;

        public ScreenEmissionPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_tables = tables;
            Buffer = scope.Own(gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "screen-emission"),
                sizeBytes: SdfScreenEmission.Bytes, usage: GpuBufferUsage.Storage));
        }
        public IGpuBuffer Buffer { get; }
        public bool[] Screens { get; } = new bool[MaxScreenSurfaces];
        public bool Owes { get; private set; } = true;
        public long Renders { get; private set; }
        public SdfEnvironmentSubmission? Submitted { get; private set; }

        public ulong? Observe(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) {
            Poll();
            Array.Clear(m_sources);
            Array.Clear(Screens);
            m_tainted = false;
            var known = true;
            if (residency.ScreenSources is { } sources) {
                foreach (var screen in sources.Screens) {
                    if (!sources.Emits(screen) || sources.ReadOf(view, screen) is not { } producer || reads is null) { continue; }
                    var index = reads.IndexOf(producer);
                    if (index < 0) { continue; }
                    var input = reads[index];
                    m_tainted |= input.Tainted;
                    Screens[screen] = input.Lease.ImageViewHandle != 0;
                    m_sources[screen] = input.Publication;
                    if (Screens[screen] && !input.Publication.IsKnown) { known = false; }
                }
            }
            Owes = Submitted is null || !known || !m_sources.AsSpan().SequenceEqual(m_renderedSources) ||
                !m_tables.m_screenMappingRegion.Contents.SequenceEqual(m_mappings);
            return known ? unchecked((ulong)(Renders + (Owes ? 1 : 0))) : null;
        }
        public long Publish(IGpuSubmissionFence fence) {
            m_sources.CopyTo(m_renderedSources, 0);
            m_tables.m_screenMappingRegion.Contents.CopyTo(m_mappings);
            Owes = false;
            Submitted = new(Owner: this, Sequence: ++Renders, Fence: fence, Tainted: m_tainted);
            return Renders;
        }
        private void Poll() {
            if (Submitted is { } submitted && submitted.Publication != m_completed && submitted.Fence.IsSignaled) {
                m_completed = submitted.Publication;
            }
        }
        public void Withdraw(long sequence) {
            if (Submitted is { } submitted && submitted.Sequence == sequence && submitted.Publication != m_completed) { Forget(); }
        }
        public void Forget() { Submitted = null; m_completed = default; Owes = true; }
        public void Dispose() { Forget(); Buffer.Dispose(); }
    }
}

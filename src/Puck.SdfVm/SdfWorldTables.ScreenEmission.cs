using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly ScreenEmissionPass m_screenEmission;

    /// <summary>Gets the one GPU image reduction every direct screen light reads.</summary>
    public IGpuBuffer ScreenEmission => m_screenEmission.Buffer;
    /// <summary>Gets the actual submitted screen reduction, independent of sky projection's cadence.</summary>
    public SdfEnvironmentSubmission? SubmittedScreenEmission => m_screenEmission.Publication.Submitted;
    /// <summary>Gets the latest screen reduction whose submission its render node proved complete while it was still the
    /// latest submitted.</summary>
    public GpuImagePublication CompletedScreenEmission => m_screenEmission.Publication.Completed;

    internal bool ScreenEmissionOwes => (m_screenEmission.Owes || m_screenEmission.Forced);
    internal bool ScreenEmissionHasImages => m_screenBound.Any(predicate: static bound => bound);
    internal ReadOnlySpan<bool> ScreenEmissionScreens => m_screenEmission.Screens;

    internal ulong? ScreenEmissionSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) =>
        m_screenEmission.Observe(reads: reads, residency: residency, view: view);
    internal long SubmitScreenEmission() => m_screenEmission.Publish();
    internal void CompleteScreenEmission(long sequence) => m_screenEmission.Publication.Complete(sequence: sequence);
    internal void WithdrawScreenEmission(long sequence) => m_screenEmission.Withdraw(sequence: sequence);

    private sealed class ScreenEmissionPass : IDisposable {
        private readonly ScreenSourceWatch m_watch;

        public ScreenEmissionPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_watch = new ScreenSourceWatch(tables: tables);
            Publication = new EnvironmentPublication(owner: this);
            Buffer = scope.Own(created: gpu.BufferFactory.CreateDeviceLocal(name: NameOf(part: "screen-emission"),
                sizeBytes: SdfScreenEmission.Bytes, usage: GpuBufferUsage.Storage));
        }

        public IGpuBuffer Buffer { get; }

        // The emitting screens this observation read with a known image.
        public bool[] Screens => m_watch.Screens;
        public bool Owes { get; private set; } = true;
        // Whether the next observation reduces again whatever its sources say (SdfWorldTables.ProjectTaintedSourcesAgain).
        public bool Forced { get; set; }

        public EnvironmentPublication Publication { get; }

        public ulong? Observe(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads) {
            m_watch.Clear();
            if (residency.ScreenSources is { } sources) {
                foreach (var screen in sources.Screens) {
                    if (!sources.Emits(screen: screen) || (sources.ReadOf(screen: screen, view: view) is not { } producer) || (reads is null)) { continue; }
                    var index = reads.IndexOf(producer: producer);

                    if (index < 0) { continue; }
                    var input = reads[index];

                    // An image with no known publication, such as a portal's level that has not rendered because its glass
                    // is out of view, is missing: it writes a zero record rather than holding every other screen's.
                    if ((input.Lease.ImageViewHandle == 0) || !input.Publication.IsKnown) { continue; }
                    m_watch.Read(screen: screen);
                    m_watch.Observe(publication: input.Publication, screen: screen, tainted: input.Tainted);
                }
            }
            Owes = ((Publication.Submitted is null) || m_watch.Changed() || Forced);
            return unchecked((ulong)(Publication.Renders + (Owes ? 1 : 0)));
        }
        public long Publish() {
            m_watch.Commit();
            Owes = false;
            Forced = false;
            return Publication.Publish(tainted: m_watch.Tainted);
        }
        public void Withdraw(long sequence) {
            if (Publication.Unproven(sequence: sequence)) { Forget(); }
        }
        public void Forget() { Publication.Forget(); Owes = true; }
        public void Dispose() { Forget(); Buffer.Dispose(); }
    }
}

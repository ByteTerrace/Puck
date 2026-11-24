using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldTables {
    private readonly ScreenEmissionPass m_screenEmission;
    // Each screen's range: the distance from its face's center past which its light, at the face's peak luminance, gives
    // a white surface facing it less than SdfPhotometry.RangeThreshold of display white at the world's brightest exposure,
    // as a point light's range is sized (SdfPhotometry.RangeOf). Shading eases the light to zero there (sdfLightWindow), so
    // nothing past it receives any. An opening shows the luminance beyond it, which no peak bounds, so its light reaches
    // everywhere. The one value shading and the motion scopes read (EmissionBoxOf): the fourth word of the screen's
    // surface row, which every writer of the row keeps (WriteScreenReach).
    private readonly float[] m_screenReach = new float[MaxScreenSurfaces];

    /// <summary>Gets the one GPU image reduction every direct screen light reads.</summary>
    public IGpuBuffer ScreenEmission => m_screenEmission.Buffer;
    /// <summary>Gets the actual submitted screen reduction, independent of sky projection's cadence.</summary>
    public SdfEnvironmentSubmission? SubmittedScreenEmission => m_screenEmission.Publication.Submitted;

    internal bool ScreenEmissionOwes => (m_screenEmission.Owes || m_screenEmission.Forced);
    internal bool ScreenEmissionHasImages => m_screenBound.Any(predicate: static bound => bound);
    internal ReadOnlySpan<bool> ScreenEmissionScreens => m_screenEmission.Screens;

    internal ulong? ScreenEmissionSignature(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads, in DeliveryLatch latch) =>
        m_screenEmission.Observe(latch: in latch, reads: reads, residency: residency, view: view);
    internal long SubmitScreenEmission() => m_screenEmission.Publish();
    // Records the reduction this frame's recording draws, which a view recorded after it reads (EnvironmentPublication).
    internal void RecordScreenEmission(long frame) => m_screenEmission.Publication.Record(frame: frame, tainted: m_screenEmission.Tainted);
    // The reduction a view of package frame `frame` samples.
    internal SdfEnvironmentSubmission? ScreenEmissionFor(long frame) => m_screenEmission.Publication.Current(frame: frame);
    internal void CompleteScreenEmission(long sequence) => m_screenEmission.Publication.Complete(sequence: sequence);
    internal void WithdrawScreenEmission(long sequence) => m_screenEmission.Withdraw(sequence: sequence);

    // Sizes each screen's range from its face's area, its peak luminance (SetScreenLuminance) and the frame's brightest
    // exposure, after the frame's screens are bound and placed.
    private void PackScreenReaches(SdfFrame frame) {
        var gain = frame.Exposure.BrightestGain;
        var surfaces = MemoryMarshal.Cast<byte, float>(span: m_screenSurfaceRegion.Contents);
        var mappings = m_screenMappingRegion.Contents;

        for (var screen = 0; (screen < MaxScreenSurfaces); screen++) {
            var row = (screen * (ScreenSurfaceByteLength / sizeof(float)));
            var area = ((4f * surfaces[(row + 3)]) * surfaces[(row + 7)]);
            var luminance = MemoryMarshal.Read<float>(source: mappings[(((screen * ScreenMappingStride) + ScreenBoundOffset) + (sizeof(float) * 2))..]);

            m_screenReach[screen] = ((m_screenFaces[screen] == SdfScreenFace.Opening)
                ? float.PositiveInfinity
                : SdfPhotometry.RangeOf(exposure: gain, weight: ((luminance * area) / MathF.PI)));
            WriteScreenReach(screen: screen);
        }
    }
    // Writes a screen's range into its surface row, owing only a changed word.
    private void WriteScreenReach(int screen) =>
        _ = m_screenSurfaceRegion.Write(bytes: MemoryMarshal.AsBytes(span: new ReadOnlySpan<float>(reference: in m_screenReach[screen])),
            offset: ((screen * ScreenSurfaceByteLength) + (sizeof(float) * 11)));

    private sealed class ScreenEmissionPass : IDisposable {
        private readonly SdfWorldTables m_tables;
        private readonly ScreenSourceWatch m_watch;

        public ScreenEmissionPass(SdfWorldTables tables, GpuDeviceServices gpu, GpuCreationScope scope) {
            m_tables = tables;
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
        // Whether the latest observation read a source with unfilled external content.
        public bool Tainted => m_watch.Tainted;

        public ulong? Observe(SdfWorldResidency residency, int view, RenderGraphExternalReads? reads, in DeliveryLatch latch) {
            m_watch.Clear();
            if (residency.ScreenSources is { } sources) {
                foreach (var screen in sources.Screens) {
                    if (!sources.Emits(screen: screen)) { continue; }
                    var index = residency.ScreenRead(reads: reads, screen: screen, view: view);

                    if ((index < 0) || (reads is null)) { continue; }
                    var input = reads[index];

                    // An image with no known publication, such as a portal's level that has not rendered because its glass
                    // is out of view, is missing: it writes a zero record rather than holding every other screen's.
                    if (!input.TryRead(latch: in latch, read: out var resolved) || (resolved.ImageViewHandle == 0) || !resolved.Publication.IsPublished()) { continue; }
                    m_watch.Read(screen: screen);
                    m_watch.Observe(publication: resolved.Publication, screen: screen, tainted: input.Label.External);
                }
            }
            Owes = ((Publication.Submitted is null) || (m_watch.ChangedScreens() != 0u) || Forced);
            return unchecked((ulong)(Publication.Renders + (Owes ? 1 : 0)));
        }
        // Publishes the reduction and judges the screens whose emission it changed against the scopes that shade
        // (NoteEmission): every screen when no earlier reduction stands or the sources are read again whole.
        public long Publish() {
            var screens = (((Publication.Submitted is null) || Forced) ? uint.MaxValue : m_watch.ChangedScreens());

            m_watch.Commit();
            Owes = false;
            Forced = false;
            var sequence = Publication.Publish(tainted: m_watch.Tainted);

            m_tables.NoteEmission(screens: screens);
            return sequence;
        }
        public void Withdraw(long sequence) {
            if (Publication.Unproven(sequence: sequence)) { Forget(); }
        }
        public void Forget() { Publication.Forget(); Owes = true; }
        public void Dispose() { Forget(); Buffer.Dispose(); }
    }
}

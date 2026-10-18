using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Hosting;
using Puck.Platform;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // Samples only already-completed compositor frames. A miss holds the last frame. An ended compositor session is
    // disposed before the binder resolves a replacement target (a returning window with the same title, or a reconnected
    // monitor); reacquisition is World policy rather than a compatibility path in the platform feed. On the D3D12 GPU
    // transport the platform copies GPU-side into shared textures — the CPU surface is never converted, only its
    // divided-cadence readback frames feed the room glow. An SDR display's B8G8R8A8 copies are sampled directly; an HDR
    // display's half-float scRGB copies convert on the device into working values at the host's paper white
    // (ConvertGpuFrame), so neither is read back. Elsewhere the CPU path converts the captured pixels.
    private void CaptureWindow(CaptureFeed feed, in FrameContext context) {
        feed.Ended = false;

        if (!feed.TryEnsureSource(adapterLuid: AdapterLuidForOpen())) {
            feed.Fault = $"{feed.Label} is unavailable";
            feed.Ended = true;
            // No source to sample: drop the shared images so the next open reallocates and re-attaches from scratch.
            feed.ReleaseGpuTargets();

            return;
        }

        if (
            feed.RidesGpu &&
            m_exportsSurfaces &&
            context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var deviceContext) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )
        ) {
            EnsureGpuTargets(
                deviceContext: deviceContext,
                feed: feed
            );

            var hdr = feed.Source!.Output.IsHdr;

            // The divided-cadence CPU frames the platform still reads back keep the AverageColor glow alive with no
            // full per-frame readback; never publish them (the sampled handle is the GPU slot, or its conversion, not
            // this surface).
            if (
                feed.Source!.TryCapture(surface: out var glowSurface) &&
                glowSurface.IsCpuPixels
            ) {
                feed.Light = (hdr
                    ? WorldImageLight.AverageScRgb(
                        paperWhiteNits: m_paperWhiteNits,
                        rgba: glowSurface.Pixels.Span
                    )
                    : WorldImageLight.Average(bgra: glowSurface.Pixels.Span));
            }
            if (hdr) {
                ConvertGpuFrame(
                    context: in context,
                    feed: feed
                );
            }

            // Live once the platform has completed its first GPU copy — the same first-frame gate the CPU path uses.
            feed.Fault = (feed.Live
                ? null
                : $"{feed.Label} awaiting a compositor frame"
            );

            return;
        }

        var output = feed.Source!.Output;

        if (feed.Pixels.Pull(
            color: ImageColorEncoding.Of(colorSpace: output.ColorSpace),
            context: in context,
            paperWhiteNits: (output.IsHdr ? m_paperWhiteNits : null),
            runtime: Runtime,
            source: feed.Source
        )) {
            feed.Fault = null;
            feed.Light = feed.Pixels.Light;
        } else if (!feed.Live) {
            feed.Fault = $"{feed.Label} awaiting a compositor frame";
        }
    }
    // Converts the HDR capture's latest published copy on the device once per copy the platform publishes: the slot is
    // bound to the conversion under its lease, which holds it against the platform's next writes until the conversion's
    // submission retires and carries the shared-fence wait that submission makes, and the converted image is what a
    // frame samples. A conversion that did not submit (its graph still building) converts the latest copy on the next
    // pull, even when the platform publishes no newer one.
    private void ConvertGpuFrame(CaptureFeed feed, in FrameContext context) {
        var source = feed.Source!;
        var revision = source.GpuRevision;

        if (
            (revision == feed.ConvertedRevision) ||
            (feed.GpuTargets is not { } ring) ||
            !ring.TryAcquire(
                frame: out var frame,
                image: out var image
            )
        ) {
            return;
        }
        if (image is null) {
            frame.Retire();

            return;
        }

        if (feed.Pixels.Convert(
            color: ImageColorEncoding.Of(colorSpace: source.Output.ColorSpace),
            context: in context,
            image: new ShaderPipelineExternalImage(
                Format: image.Format,
                Height: image.Height,
                ImageHandle: image.ImageHandle,
                ImageViewHandle: frame.ImageViewHandle,
                Layout: GpuImageLayout.External,
                Width: image.Width
            ),
            lease: frame,
            runtime: Runtime
        )) {
            feed.ConvertedRevision = revision;
        }
    }
    // Ensures the feed's THREE simultaneous-access shared textures exist and are attached to its current source at the
    // source's native extent (the sampler scales, so no GPU-side resize is needed), in the capture's own format: B8G8R8A8
    // for an SDR display, half-float RGBA for an HDR one. Reallocates on a resize
    // (GpuTargetsOutdated) or a reacquired source; AttachGpuTargets replaces first, then the superseded ring retires,
    // disposed with its fence once no submitted frame samples it. Cadence-gated by the caller, so it never runs per
    // render frame.
    [SupportedOSPlatform("windows10.0.10240")]
    private void EnsureGpuTargets(CaptureFeed feed, IGpuDeviceContext deviceContext) {
        var source = feed.Source!;
        var width = source.SourceWidth;
        var height = source.SourceHeight;

        // The source has not reported its extent yet (no first frame); nothing to allocate against.
        if (
            (width <= 0) ||
            (height <= 0)
        ) {
            return;
        }

        if (
            (feed.GpuTargets is not null) &&
            !source.GpuTargetsOutdated &&
            ReferenceEquals(
            objA: feed.GpuAttachedSource,
            objB: source
        )
        ) {
            return;
        }

        var export = new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)deviceContext));
        var images = new IGpuExportableImage[SharedTargetCount];
        var handles = new nint[images.Length];

        for (var i = 0; (i < images.Length); ++i) {
            images[i] = export.CreateSimultaneousAccessImage(
                format: source.Output.Format,
                height: ((uint)height),
                width: ((uint)width)
            );
            handles[i] = images[i].SharedHandle;
        }

        // The fence the platform signals after each copy, whose value the frame that samples a slot waits for on the GPU,
        // and the publication the platform reserves and publishes slots through and the frames acquire them from.
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: images.Length);

        var ring = new SharedTargetRing(
            fence: SharedRingFence.Create(
                export: export,
                hostsOnDirectX: true,
                renderDevice: deviceContext
            ),
            images: images,
            importedSurfaces: null,
            imports: null,
            ring: slots,
            targetDevice: null
        );
        var superseded = feed.GpuTargets;

        // Attach first (the platform contract: attach swaps the targets in safely), then retire the old ring: its images
        // and fence go once the last submitted frame sampling them has retired its lease.
        source.AttachGpuTargets(targets: new NativeImageGpuCaptureTargets(
            Format: source.Output.Format,
            SharedTargetHandles: handles,
            Width: width,
            Height: height,
            Slots: slots,
            SharedFenceHandle: ring.ProducerFenceHandle
        ));
        feed.GpuTargets = ring;
        feed.GpuAttachedSource = source;
        superseded?.Retire();
    }
    // Hands a capture source instance the capture a live verb parked for equal settings, if one is waiting.
    private bool TryClaimParkedCapture(WorldScreenSource.Producer source, [NotNullWhen(returnValue: true)] out CaptureFeed? capture) {
        for (var index = 0; (index < m_parkedCaptures.Count); index++) {
            var parked = m_parkedCaptures[index];

            if (ImageSourceSettings.Equal(
                left: parked.Source.Settings,
                right: source.Settings
            )) {
                m_parkedCaptures.RemoveAt(index: index);
                capture = parked.Feed;

                return true;
            }
        }

        capture = null;

        return false;
    }
    // One feed's PRESENTATION CLOCK, stated once so a webcam and a window capture cannot drift into different refresh
    // policies. Camera pixels are nondeterministic presentation input and must not freeze when authoritative simulation
    // time is paused or absent. The first pull after arming always runs; later pulls wait out the profile's whole period.
    private sealed class PullCadence(uint rateHz) {
        private readonly long m_cadenceTicks = Math.Max(
            val1: 1L,
            val2: (Stopwatch.Frequency / Math.Max(
                val1: rateHz,
                val2: 1u
            ))
        );

        private long m_lastPullTicks;
        private bool m_pulled;

        public void Rearm() => m_pulled = false;
        public bool ShouldPull() {
            var now = Stopwatch.GetTimestamp();

            if (
                m_pulled &&
                ((now - m_lastPullTicks) < m_cadenceTicks)
            ) {
                return false;
            }

            m_pulled = true;
            m_lastPullTicks = now;

            return true;
        }
    }
    private sealed partial class CaptureFeed {
        private PullCadence? m_cadence;

        // Whether the latest pull found no source to capture (a window or monitor that is gone): the feed refuses until a
        // later pull reacquires one.
        public bool Ended { get; set; }
        // The platform's GPU revision whose copy the HDR route last converted, or -1 before one has.
        public long ConvertedRevision { get; set; } = -1L;

        // The human label a fault reads under: a window title, or a whole-monitor index.
        public string Label => ((MonitorIndex is { } monitor)
            ? $"monitor {monitor}"
            : $"window '{Title}'"
        );
        public Vector3 Light { get; set; }
        // Whether the feed holds a frame: a completed GPU copy a frame samples directly, or a captured frame to convert (a
        // CPU frame, or an HDR GPU copy), which answers rendered only once it has converted (WorldCapturePixels.Answer).
        public bool Live => (SamplesRing
            ? ((GpuTargets?.LatestHandle() ?? 0) != 0)
            : Pixels.Captured);
        // Whether this feed rides the GPU transport: on that host, whatever display the source captures.
        public bool RidesGpu => GpuRoute;
        // Whether a frame samples the platform's GPU copy directly: an SDR display's B8G8R8A8 copy. An HDR display's
        // half-float scRGB copy converts on the device into Pixels first.
        public bool SamplesRing => (RidesGpu && !(Source?.Output.IsHdr ?? false));

        // The feed's pull clock, armed by its first pull.
        private PullCadence Cadence => (m_cadence ??= new(rateHz: Profile.RefreshRateHz));

        // Acquires the image a frame samples: an SDR GPU copy's slot, held against the platform's next writes until the
        // lease retires and carrying the shared-fence value its submission waits for; otherwise the converted image,
        // held until the frame retires.
        public GpuImageLease AcquireFrame() {
            if (!Live) {
                return 0;
            }

            if (SamplesRing) {
                return (((GpuTargets is { } ring) && ring.TryAcquire(frame: out var frame))
                    ? frame
                    : 0);
            }

            return Pixels.Acquire();
        }
        public nint Handle() {
            if (SamplesRing) {
                // The image view of the platform's latest completed GPU copy; 0 (unbound glass) until that first copy lands.
                return ((Live && (GpuTargets is { } ring))
                    ? ring.LatestHandle()
                    : 0
                );
            }

            return Pixels.Handle;
        }
        public void NotifyDeviceLost() {
            Pixels.OnDeviceLost();
            ReleaseGpuTargets();
            ConvertedRevision = -1L;
            Cadence.Rearm();
        }
        public bool ShouldPull() => Cadence.ShouldPull();
        public bool TryEnsureSource(long? adapterLuid) {
            if ((Source is { IsEnded: false })) {
                return true;
            }

            // The old target is gone: drop its final frame and clear stale state until the replacement's first frame.
            // The stale GPU attachment is left for EnsureGpuTargets to reallocate against the replacement source.
            Source?.Dispose();
            Source = null;
            Pixels.Forget();
            ConvertedRevision = -1L;
            Fault = null;

            INativeImageCaptureFeed? next;
            var reacquired = ((MonitorIndex is { } monitor)
                ? Service.TryCreateMonitorCapture(
                    monitorIndex: monitor,
                    width: Profile.Width,
                    height: Profile.Height,
                    refreshRateHz: Profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
                : Service.TryCreateWindowCapture(
                    windowTitleFragment: Title,
                    width: Profile.Width,
                    height: Profile.Height,
                    refreshRateHz: Profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
            );

            if (!reacquired) {
                return false;
            }

            Source = next;

            return true;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // One feed per declared probe that writes a texture, keyed by probe id. The probes host declares the feed at its
    // own construction and asks for a ring of the kind's output extent when its kernel run starts; the ring is
    // provisioned here, on the render thread, at the next publish (the exportable targets need the render device).
    private readonly Dictionary<string, ProbeFeed> m_probeFeeds = new(comparer: StringComparer.Ordinal);
    // One export state per named camera whose view a probe socket reads, shared by every probe socket naming the
    // same camera (mirrors ProbeFeed's own id-keyed sharing — a socket rebind or probe removal that releases the
    // export tears it down for every other socket still naming that camera too).
    private readonly Dictionary<string, ViewExportFeed> m_viewExports = new(comparer: StringComparer.Ordinal);

    /// <summary>Declares that a probe writes a texture, so a screen may show it. Idempotent.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    public void DeclareProbeOutput(string id) => GetOrAddProbeFeed(id: id).Declared = true;
    /// <summary>Reads a probe's provisioned output ring at the requested extent, recording the request so the next
    /// publish provisions (or re-provisions) it when it does not match.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    /// <param name="width">The output width the kind declares, in pixels.</param>
    /// <param name="height">The output height the kind declares, in pixels.</param>
    /// <param name="output">The ring a kernel publishes into, set only when this returns <see langword="true"/>.</param>
    /// <param name="generation">The ring's identity — fresh on every provisioning — set only when this returns
    /// <see langword="true"/>.</param>
    /// <param name="fault">Why no ring is available yet, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when a ring of that extent is provisioned.</returns>
    public bool TryGetProbeOutput(string id, int width, int height, out ProbeKernelOutput output, out object? generation, out string fault) {
        var feed = GetOrAddProbeFeed(id: id);

        feed.Request = (Width: width, Height: height);

        if ((feed.Targets is { } targets) && (feed.Output is { } provisioned) && (provisioned.Width == width) && (provisioned.Height == height)) {
            output = provisioned;
            generation = targets;
            fault = "";

            return true;
        }

        output = default;
        generation = null;
        fault = (feed.Fault ?? "probe output awaiting provisioning");

        return false;
    }
    /// <summary>Retires a probe's output ring and drops its pending request; the feed goes dark until the next
    /// <see cref="TryGetProbeOutput"/>.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    public void ReleaseProbeOutput(string id) {
        if (m_probeFeeds.TryGetValue(key: id, value: out var feed)) {
            feed.Request = null;
            feed.Release();
        }
    }
    /// <summary>Binds a declared screen to a probe's texture output. Fails for an undeclared screen or a probe that
    /// declares no output.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="id">The <c>probes[].id</c>.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryProbe(int index, string id) {
        if (!m_slots.TryGetValue(key: index, value: out var slot)) {
            return (Ok: false, Message: $"no screen {index} declared");
        }
        if (!m_probeFeeds.TryGetValue(key: id, value: out var feed) || !feed.Declared) {
            return (Ok: false, Message: $"probe '{id}' declares no texture output");
        }

        slot.ClearLive();
        slot.Probe = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing probe '{id}'");
    }

    /// <summary>Reads a named camera's offscreen view as a kernel input ring, registering the view for export on first
    /// request; the ring arrives at a later publish. Export needs the Direct3D 12 host: the offscreen engine's
    /// exportable image is opened by the probe kernel bench's Direct3D 11 <c>OpenSharedResource1</c>, and a Vulkan
    /// host's exported handle is Vulkan-to-Vulkan only (see <see cref="Puck.Vulkan.VulkanGpuExportableStorageImage"/>'s
    /// own remarks) — refused loudly here rather than silently producing a ring nothing can read.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    /// <param name="ring">The exported ring, set only when this returns <see langword="true"/>.</param>
    /// <param name="generation">The export's identity — fresh on every (re)creation, for example after device loss —
    /// set only when this returns <see langword="true"/>.</param>
    /// <param name="fault">Why no export is available yet, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the view is exported and has rendered at least once.</returns>
    public bool TryGetViewExport(string cameraName, [NotNullWhen(true)] out ProbeKernelInput.Ring? ring, out object? generation, out string fault) {
        ring = null;
        generation = null;

        if (m_disposed) {
            fault = "binder disposed";

            return false;
        }

        if (ResolveCamera(name: cameraName) is not { } camera) {
            fault = $"camera '{cameraName}' not declared";

            return false;
        }

        if (
            !m_hostsOnDirectX ||
            !OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
        ) {
            fault = "view export needs the DirectX host";

            return false;
        }

        if (m_viewServices is null) {
            fault = "the view pool is not configured";

            return false;
        }

        var feed = GetOrAddViewExport(camera: camera);
        var handle = feed.View.ExportSharedHandle;

        generation = feed.View.ExportGeneration;
        // A fresh engine (device loss, dimension change, a factory that just changed) briefly holds a zero handle
        // again — mirror that into the ring so a consumer's TryAcquireLatest refuses until this camera's next
        // rendered frame lands, instead of replaying whatever slot a PRIOR engine last published.
        feed.Slots.LatestSlot = ((0 != handle) ? 0 : -1);

        if (0 == handle) {
            fault = "view export awaiting a first rendered frame";

            return false;
        }

        ring = new ProbeKernelInput.Ring(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: ((int)camera.RenderHeight),
            SharedTargetHandles: [handle],
            Slots: feed.Slots,
            Width: ((int)camera.RenderWidth)
        );
        fault = "";

        return true;
    }
    /// <summary>Drops a view export requested through <see cref="TryGetViewExport"/> and rebuilds the view's engine
    /// without export on its next resolve. A camera still filmed by a jumbotron screen keeps rendering (the release
    /// only stops the export); one no screen films is released entirely, matching
    /// <see cref="ReleaseOrphanedCameraView"/>'s own orphan contract.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    public void ReleaseViewExport(string cameraName) {
        if (!m_viewExports.Remove(key: cameraName, value: out var feed)) {
            return;
        }

        feed.View.ExportFactory = null;

        if (0 == WiredScreensFor(name: cameraName).Count) {
            ReleaseOrphanedCameraView(name: cameraName);
        }
    }

    // Registers (idempotent) the camera's offscreen view for export — the same persistent SdfCameraView a jumbotron
    // screen would use (RegisterCameraView), so a camera already filmed by a screen gains export with no second
    // render pass. An export-only camera (no screen names it) still renders every ViewStack refresh: Register's
    // isLive predicate defaults to null (always live), so the round-robin schedules it exactly like a wired view.
    // Every caller already guards TryGetViewExport's own OS-version check before reaching here.
    [SupportedOSPlatform("windows10.0.10240")]
    private ViewExportFeed GetOrAddViewExport(WorldCamera camera) {
        if (m_viewExports.TryGetValue(key: camera.Name, value: out var existing)) {
            return existing;
        }

        RegisterCameraView(camera: camera);

        var view = m_cameraViews[camera.Name].View;
        var width = camera.RenderWidth;
        var height = camera.RenderHeight;

        view.ExportFactory = device => m_surfaceExport!.CreateSharedComputeStorageImage(
            deviceContext: device,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            width: width
        );

        var feed = new ViewExportFeed(view: view);

        m_viewExports[camera.Name] = feed;

        return feed;
    }

    private ProbeFeed GetOrAddProbeFeed(string id) {
        if (!m_probeFeeds.TryGetValue(key: id, value: out var feed)) {
            feed = new ProbeFeed(id: id);
            m_probeFeeds[id] = feed;
        }

        return feed;
    }
    // Provisions every requested ring whose extent the current one does not match, then reads each feed's liveness
    // from its ring. Runs once per publish, after the camera device is serviced, on the render thread.
    private void ServiceProbeFeeds(IGpuDeviceContext deviceContext) {
        foreach (var feed in m_probeFeeds.Values) {
            if (feed.Request is { Width: > 0, Height: > 0 } request) {
                if ((feed.Output is not { } output) || (output.Width != request.Width) || (output.Height != request.Height)) {
                    ProvisionProbeOutput(deviceContext: deviceContext, feed: feed, height: request.Height, width: request.Width);
                }
            }

            feed.Live = (feed.Output is { Slots.LatestSlot: >= 0 });
            feed.Fault = (feed.Live ? null : (feed.Fault ?? "probe awaiting a first frame"));
        }
    }
    private void ProvisionProbeOutput(IGpuDeviceContext deviceContext, ProbeFeed feed, int width, int height) {
        feed.Release();

        if (m_renderAdapterLuid is not { } adapterLuid) {
            feed.Fault = "the render adapter reports no LUID";

            return;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
            feed.Fault = "shared probe textures need Windows 10";

            return;
        }
        if (!TryProvisionSharedRing(adapterLuid: adapterLuid, deviceContext: deviceContext, fault: out var fault, format: SurfaceFormat.R8G8B8A8Unorm, height: height, images: out var images, importedViews: out var views, imports: out var imports, width: width)) {
            feed.Fault = fault;

            return;
        }

        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: images.Count);

        var targets = new CameraGpuTargetSet(
            images: images,
            importedViews: views,
            imports: imports,
            ring: slots
        );

        feed.Targets = targets;
        feed.Output = new ProbeKernelOutput(
            Width: width,
            Height: height,
            TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
            SharedTargetHandles: targets.SharedHandles,
            Slots: slots
        );
        feed.Fault = null;
    }
    private void DisposeProbeFeeds() {
        foreach (var feed in m_probeFeeds.Values) {
            feed.Release();
        }
    }
    // No GPU teardown of its own — every export's engine/image is owned by its SdfCameraView, disposed with the
    // rest of the pool by m_viewStack.Dispose(). Clearing the map only drops this binder's own bookkeeping.
    private void DisposeViewExports() => m_viewExports.Clear();

    // One probe output's feed: the ring its kernel publishes into and the render resources behind it, plus the
    // pending extent request and live/fault state. A probe emits no room light.
    private sealed class ProbeFeed(string id) {
        public bool Declared { get; set; }
        public string? Fault { get; set; }
        public string Id { get; } = id;
        public bool Live { get; set; }
        public ProbeKernelOutput? Output { get; set; }
        public (int Width, int Height)? Request { get; set; }
        public CameraGpuTargetSet? Targets { get; set; }

        public SdfScreenSourceFrame AcquireFrame() {
            if (!Live || (Targets is not { } targets)) {
                return 0;
            }

            return (targets.TryAcquire(frame: out var frame) ? frame : 0);
        }
        public nint Handle() {
            if (!Live || (Output is not { } output) || (Targets is not { } targets)) {
                return 0;
            }

            return targets.Handle(slot: output.Slots.LatestSlot);
        }
        public Vector3 Light => Vector3.Zero;
        public void Release() {
            var targets = Targets;

            Targets = null;
            Output = null;
            Live = false;
            targets?.Retire();
        }
    }
    // One camera's export state, keyed by camera name. Carries no GPU handle of its own — SdfCameraView.
    // ExportSharedHandle/ExportGeneration are read fresh from the view each call, so this class is pure bookkeeping.
    private sealed class ViewExportFeed(SdfCameraView view) {
        public SdfCameraView View { get; } = view;
        public ViewExportRing Slots { get; } = new();
    }
    // The single-image counterpart of the multi-buffer camera/probe rings above: a view export has exactly one
    // physical texture (the offscreen engine's own persistent output image, re-rendered in place every refresh), so
    // there is no write target distinct from the read slot to rotate between — LatestSlotPublication's ring model
    // (which requires at least two backing targets) does not fit. Readiness instead mirrors the export handle
    // itself: SdfWorldEngine.SubmitFrame always finishes draining the image (FinalizeForExport) before
    // SdfCameraView.ExportSharedHandle is read from outside a render pass, so a nonzero handle already means a
    // complete frame landed. TryAcquireLatest/Release exist to satisfy ISharedSlotRing's contract for the probe
    // kernel host that opens this ring; they carry no producer-side gating, so a consumer may sample the texture
    // while the next render is mid-flight (latest-wins, unfenced read — see the README).
    private sealed class ViewExportRing : ISharedSlotRing {
        public int LatestSlot { get; set; } = -1;

        public bool TryAcquireLatest(out int slot) {
            slot = ((LatestSlot >= 0) ? 0 : -1);

            return (slot >= 0);
        }
        public void Release(int slot) => _ = slot;
    }
}

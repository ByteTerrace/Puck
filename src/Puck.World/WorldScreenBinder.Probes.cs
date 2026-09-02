using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;
using Puck.World.Client;
using Puck.SdfVm.Views;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // One feed per declared probe that writes a texture, keyed by probe id. The probes host declares the feed at its
    // own construction and asks for a ring of the kind's output extent when its kernel run starts; the ring is
    // provisioned here, on the render thread, at the next publish (the exportable targets need the render device).
    private readonly Dictionary<string, ProbeFeed> m_probeFeeds = new(comparer: StringComparer.Ordinal);
    // One export state per named camera whose view a probe socket reads, shared by every probe socket naming the
    // same camera (mirrors ProbeFeed's own id-keyed sharing). Reference counting keeps the export alive until the
    // final live probe instance releases that camera.
    private readonly Dictionary<string, ViewExportFeed> m_viewExports = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, int> m_viewExportReferences = new(comparer: StringComparer.Ordinal);

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

        if (
            (0 == handle) ||
            !feed.Slots.HasCompletedFrame ||
            !ReferenceEquals(objA: feed.CompletedGeneration, objB: generation)
        ) {
            fault = "view export awaiting a first rendered frame";

            return false;
        }

        if (!ReferenceEquals(objA: feed.InputGeneration, objB: generation)) {
            feed.Input = new ProbeKernelInput.Ring(
                Format: SurfaceFormat.R8G8B8A8Unorm,
                Height: ((int)camera.RenderHeight),
                SharedTargetHandles: [handle],
                Slots: feed.Slots,
                Width: ((int)camera.RenderWidth)
            );
            feed.InputGeneration = generation;
        }

        ring = feed.Input!;
        fault = "";

        return true;
    }
    /// <summary>Retains one live probe instance's use of a named view export.</summary>
    public void RetainViewExport(string cameraName) {
        m_viewExportReferences[cameraName] = (m_viewExportReferences.TryGetValue(key: cameraName, value: out var count) ? (count + 1) : 1);
    }
    /// <summary>Drops a view export requested through <see cref="TryGetViewExport"/> and rebuilds the view's engine
    /// without export on its next resolve. A camera still filmed by a jumbotron screen keeps rendering (the release
    /// only stops the export); one no screen films is released entirely, matching
    /// <see cref="ReleaseOrphanedCameraView"/>'s own orphan contract.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    public void ReleaseViewExport(string cameraName) {
        if (m_viewExportReferences.TryGetValue(key: cameraName, value: out var references) && (references > 1)) {
            m_viewExportReferences[cameraName] = (references - 1);

            return;
        }

        _ = m_viewExportReferences.Remove(key: cameraName);

        if (!m_viewExports.Remove(key: cameraName, value: out var feed)) {
            return;
        }

        feed.Detach();
        feed.View.ExportFactory = null;

        if (ResolveCamera(name: cameraName) is { } camera) {
            var registrationName = WorldSeatAnchors.RegistrationName(
                camera: camera,
                seat: DefaultViewSeat
            );

            if (0 == WiredScreensFor(name: registrationName).Count) {
                ReleaseOrphanedCameraView(name: registrationName);
            }
        }
    }

    private bool HasViewExportReferences(string cameraName) => m_viewExportReferences.ContainsKey(key: cameraName);
    private void RetireViewExportForRecreation(string cameraName) {
        if (m_viewExports.Remove(key: cameraName, value: out var feed)) {
            feed.Detach();
        }
    }
    // Registers (idempotent) the camera's offscreen view for export — the same persistent SdfCameraView a jumbotron
    // screen would use (RegisterCameraView), so a camera already filmed by a screen gains export with no second
    // render pass. An export-only camera (no screen names it) still renders every ViewStack refresh: Register's
    // isLive predicate defaults to null (always live), so the round-robin schedules it exactly like a wired view.
    // Every caller already guards TryGetViewExport's own OS-version check before reaching here.
    [SupportedOSPlatform("windows10.0.10240")]
    private ViewExportFeed GetOrAddViewExport(WorldCamera camera) {
        RegisterCameraView(
            camera: camera,
            seat: DefaultViewSeat
        );

        var view = m_cameraViews[WorldSeatAnchors.RegistrationName(
            camera: camera,
            seat: DefaultViewSeat
        )].View;

        if (m_viewExports.TryGetValue(key: camera.Name, value: out var existing)) {
            if (ReferenceEquals(objA: existing.View, objB: view)) {
                return existing;
            }

            existing.Detach();
            _ = m_viewExports.Remove(key: camera.Name);
        }

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
    private void DisposeViewExports() {
        foreach (var feed in m_viewExports.Values) {
            feed.Detach();
        }

        m_viewExports.Clear();
        m_viewExportReferences.Clear();
    }

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
    private sealed class ViewExportFeed {
        public ViewExportFeed(SdfCameraView view) {
            View = view;
            view.TryBeginExportWrite = TryBeginWrite;
            view.EndExportWrite = EndWrite;
        }

        public object? CompletedGeneration { get; private set; }
        public SdfCameraView View { get; }
        public ViewExportRing Slots { get; } = new();
        public ProbeKernelInput.Ring? Input { get; set; }
        public object? InputGeneration { get; set; }

        private object? PendingGeneration { get; set; }

        public void Detach() {
            Slots.RetireAndWait();
            View.TryBeginExportWrite = null;
            View.EndExportWrite = null;
        }

        private void EndWrite(bool completed) {
            if (completed) {
                // Publish the generation identity before the ring's volatile ready state. A failed first submission
                // after device loss may preserve an older readable image, but it must never bless the replacement
                // engine's new handle as completed.
                CompletedGeneration = PendingGeneration;
            }

            PendingGeneration = null;
            Slots.EndWrite(completed: completed);
        }
        private bool TryBeginWrite() {
            if (!Slots.TryBeginWrite()) {
                return false;
            }

            PendingGeneration = View.ExportGeneration;

            return true;
        }
    }
    // The single-image counterpart of the multi-buffer camera/probe rings above: a view export has exactly one
    // physical texture, so producer and consumers coordinate through one atomic state instead of rotating slots.
    // Positive states count concurrent D3D11 readers; SdfCameraView reserves the writer state before submitting
    // the next D3D12 render and keeps the previous complete image when that reservation is unavailable. Export-mode
    // SubmitFrame drains the producer queue before EndWrite publishes state 1, so the cross-device reader never
    // overlaps a writer over the same texels.
    private sealed class ViewExportRing : ISharedSlotRing {
        // 0 = no completed frame, 1 = readable with no readers, 2+ = readable with (state - 1) readers,
        // -1 = retired, -2 = producer writing.
        private bool m_hadCompletedBeforeWrite;
        private int m_state;

        public bool HasCompletedFrame => (Volatile.Read(location: ref m_state) >= 1);
        public int LatestSlot => (HasCompletedFrame ? 0 : -1);

        public void EndWrite(bool completed) => Volatile.Write(location: ref m_state, value: ((completed || m_hadCompletedBeforeWrite) ? 1 : 0));
        public bool TryBeginWrite() {
            while (true) {
                var state = Volatile.Read(location: ref m_state);

                if ((state > 1) || (state < 0)) {
                    return false;
                }
                if (Interlocked.CompareExchange(comparand: state, location1: ref m_state, value: -2) == state) {
                    m_hadCompletedBeforeWrite = (state == 1);

                    return true;
                }
            }
        }
        public bool TryAcquireLatest(out int slot) {
            while (true) {
                var state = Volatile.Read(location: ref m_state);

                if ((state < 1) || (state == int.MaxValue)) {
                    slot = -1;

                    return false;
                }
                if (Interlocked.CompareExchange(comparand: state, location1: ref m_state, value: (state + 1)) == state) {
                    slot = 0;

                    return true;
                }
            }
        }
        public void Release(int slot) {
            if (slot != 0) {
                return;
            }

            while (true) {
                var state = Volatile.Read(location: ref m_state);

                if (state <= 1) {
                    return;
                }
                if (Interlocked.CompareExchange(comparand: state, location1: ref m_state, value: (state - 1)) == state) {
                    return;
                }
            }
        }
        public void RetireAndWait() {
            var spinner = new SpinWait();

            while (true) {
                var state = Volatile.Read(location: ref m_state);

                if (state == -1) {
                    return;
                }
                if ((state is 0 or 1) && (Interlocked.CompareExchange(comparand: state, location1: ref m_state, value: -1) == state)) {
                    return;
                }

                spinner.SpinOnce();
            }
        }
    }
}

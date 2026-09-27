using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Hosting;
using Puck.World.Client;

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

    // The render adapter's own kernel host, opened the first time a probe whose trigger reads a rendered source starts,
    // on the adapter the render device reported; woken once per publish.
    private IRenderedProbeKernelHost? m_renderedKernels;

    /// <summary>Declares that a probe writes a texture, so a screen may show it. Idempotent.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    public void DeclareProbeOutput(string id) => GetOrAddProbeFeed(id: id).Declared = true;
    /// <summary>Records the kernel run that writes a probe's output ring, whose order <c>world.screens</c> reports for a
    /// screen showing the probe.</summary>
    /// <param name="id">The <c>probes[].id</c> the output ring is keyed by.</param>
    /// <param name="run">The run the probes host attached.</param>
    public void BindProbeRun(string id, IProbeKernelRun run) => GetOrAddProbeFeed(id: id).Run = run;
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

        if (
            (feed.Targets is { } targets) &&
            (feed.Output is { } provisioned) &&
            (provisioned.Width == width) &&
            (provisioned.Height == height)
        ) {
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
    /// <summary>Reads a probe's provisioned output ring for <c>probe.status</c>: its extent and how its kernel's writes
    /// reach the render device.</summary>
    /// <param name="id">The <c>probes[].id</c> the ring is keyed by.</param>
    /// <param name="width">The ring's width in pixels, set only when this returns <see langword="true"/>.</param>
    /// <param name="height">The ring's height in pixels, set only when this returns <see langword="true"/>.</param>
    /// <param name="order">The ring's order, set only when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the ring is provisioned.</returns>
    public bool TryReadProbeOutput(string id, out int width, out int height, out SharedFenceOrder order) {
        if (
            m_probeFeeds.TryGetValue(
                key: id,
                value: out var feed
            ) &&
            (feed.Output is { } output)
        ) {
            (width, height, order) = (output.Width, output.Height, feed.Order);

            return true;
        }

        (width, height, order) = (0, 0, SharedFenceOrder.Pending);

        return false;
    }
    /// <summary>Returns the render adapter's own kernel host, which runs a probe whose trigger socket reads a rendered
    /// source, opening it on the adapter the render device reported the first time one asks.</summary>
    /// <param name="host">The host, set only when this returns <see langword="true"/>; owned by this binder.</param>
    /// <param name="fault">Why no host is available, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the host is open.</returns>
    public bool TryGetRenderedKernelHost([NotNullWhen(true)] out IProbeKernelHost? host, out string fault) {
        if (m_renderedKernels is { } open) {
            host = open;
            fault = "";

            return true;
        }
        if (m_disposed) {
            host = null;
            fault = "binder disposed";

            return false;
        }
        if (m_renderAdapterLuid is not { } adapterLuid) {
            host = null;
            fault = "the render adapter reports no LUID yet";

            return false;
        }
        if (!m_probeKernelHosts.TryOpen(
            adapterLuid: adapterLuid,
            fault: out fault,
            host: out var opened
        )) {
            host = null;

            return false;
        }

        m_renderedKernels = opened;
        host = opened;

        return true;
    }
    /// <summary>Retires a probe's output ring and drops its pending request; the feed goes dark until the next
    /// <see cref="TryGetProbeOutput"/>.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    public void ReleaseProbeOutput(string id) {
        if (m_probeFeeds.TryGetValue(
            key: id,
            value: out var feed
        )) {
            feed.Request = null;
            feed.Run = null;
            feed.Release();
        }
    }
    /// <summary>Binds a declared screen to a probe's texture output, whose source instance the screen shows over its row
    /// from the render graph's next frame. Fails for an undeclared screen or a probe that declares no output.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="id">The <c>probes[].id</c>.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryProbe(int index, string id) {
        if (!m_slots.ContainsKey(key: index)) {
            return (Ok: false, Message: $"no screen {index} declared");
        }
        if (
            !m_probeFeeds.TryGetValue(
            key: id,
            value: out var feed
        ) ||
            !feed.Declared
        ) {
            return (Ok: false, Message: $"probe '{id}' declares no texture output");
        }

        ShowLive(
            index: index,
            source: new WorldScreenSource.Probe(Id: id)
        );

        return (Ok: true, Message: $"screen {index} showing probe '{id}'");
    }
    /// <summary>Reads a named camera's offscreen view as a kernel input ring, registering the view for export on first
    /// request; the ring arrives at a later publish. The exported image is a Direct3D 12 simultaneous-access texture a
    /// probe kernel host's Direct3D 11 <c>OpenSharedResource1</c> opens, and each frame is published with the value its
    /// write signals on a shared fence the kernel waits for: on the Direct3D 12 host the render device's own texture and
    /// fence, and on the Vulkan host a texture and fence made on the binder's headless Direct3D 12 device, which the
    /// render device imports to write and signal (<see cref="IGpuSurfaceTransferFactory.TryImportWritable"/>).</summary>
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

        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            fault = "view export needs Windows 10";

            return false;
        }

        if (m_viewPipelines is null) {
            fault = "the views are not configured";

            return false;
        }

        var feed = GetOrAddViewExport(camera: camera);
        var node = (ViewProducerOf(name: feed.Name) as CameraViewProducer)?.Node;
        var handle = (node?.ExportSharedHandle ?? 0);

        generation = node?.ExportGeneration;

        if (
            (0 == handle) ||
            !feed.Slots.HasCompletedFrame ||
            !ReferenceEquals(
            objA: feed.CompletedGeneration,
            objB: generation
        )
        ) {
            fault = "view export awaiting a first rendered frame";

            return false;
        }

        if (!ReferenceEquals(
            objA: feed.InputGeneration,
            objB: generation
        )) {
            feed.Input = new ProbeKernelInput.Ring(
                Format: GpuPixelFormat.R8G8B8A8Unorm,
                Height: ((int)feed.Height),
                SharedFenceHandle: node!.ExportFenceHandle,
                SharedTargetHandles: [handle],
                Slots: feed.Slots,
                Width: ((int)feed.Width)
            );
            feed.InputGeneration = generation;
        }

        ring = feed.Input!;
        fault = "";

        return true;
    }
    /// <summary>Retains one live probe instance's use of a named view export.</summary>
    public void RetainViewExport(string cameraName) {
        m_viewExportReferences[cameraName] = (m_viewExportReferences.TryGetValue(
            key: cameraName,
            value: out var count
        )
            ? (count + 1)
            : 1
        );
    }
    /// <summary>Drops a view export requested through <see cref="TryGetViewExport"/> and rebuilds the view's engine
    /// without export on its next resolve. A camera still filmed by a jumbotron screen keeps rendering (the release
    /// only stops the export); one no screen films is released entirely, matching
    /// <see cref="ReleaseOrphanedCameraView"/>'s own orphan contract.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    public void ReleaseViewExport(string cameraName) {
        if (
            m_viewExportReferences.TryGetValue(
            key: cameraName,
            value: out var references
        ) &&
            (references > 1)
        ) {
            m_viewExportReferences[cameraName] = (references - 1);

            return;
        }

        _ = m_viewExportReferences.Remove(key: cameraName);

        if (!m_viewExports.Remove(
            key: cameraName,
            value: out var feed
        )) {
            return;
        }

        Detach(feed: feed);
        ReleaseOrphanedCameraView(name: feed.Name);
    }

    private bool HasViewExportReferences(string cameraName) => m_viewExportReferences.ContainsKey(key: cameraName);
    private void RetireViewExportForRecreation(string cameraName) {
        if (m_viewExports.Remove(
            key: cameraName,
            value: out var feed
        )) {
            Detach(feed: feed);
        }
    }
    // Retires the export's publication, so no reader acquires the image again, and stops its registration exporting: the
    // view's next frame replaces the exporting engine with one rendering into images of its own, so nothing writes the
    // exported image again. It never waits: a reader still holding the image finishes with it on its own device, which
    // keeps its own reference to the shared texture and fence.
    private void Detach(ViewExportFeed feed) {
        feed.Slots.Retire();

        if (m_cameraViews.TryGetValue(
            key: feed.Name,
            value: out var registration
        )) {
            registration.EndExportWrite = null;
            registration.ExportFactory = null;
            registration.TryBeginExportWrite = null;
        }
    }
    // Registers (idempotent) the camera's view for export — the same registration a screen would show
    // (RegisterCameraView), so a camera already filmed by a screen gains export with no second render. An export-only
    // camera (no screen names it) is a view the display shows directly, so it renders at its refresh like a shown one, at
    // its declared extent, the extent of the image it exports. Every caller already guards TryGetViewExport's own
    // OS-version check before reaching here.
    [SupportedOSPlatform("windows10.0.10240")]
    private ViewExportFeed GetOrAddViewExport(WorldCamera camera) {
        var name = m_registrationNames.Of(
            camera: camera,
            seat: DefaultViewSeat
        );

        RegisterCameraView(
            camera: camera,
            seat: DefaultViewSeat
        );

        var registration = m_cameraViews[name];

        if (m_viewExports.TryGetValue(
            key: camera.Name,
            value: out var existing
        )) {
            if (registration.ExportFactory is not null) {
                return existing;
            }

            Detach(feed: existing);
            _ = m_viewExports.Remove(key: camera.Name);
        }

        var width = camera.RenderWidth;
        var height = camera.RenderHeight;
        var feed = new ViewExportFeed(
            height: height,
            name: name,
            width: width
        );

        registration.ExportFactory = (m_hostsOnDirectX
            ? device => new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)device)).CreateSharedComputeImage(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: height,
                width: width
            )
            : device => CreateImportedViewExport(
                device: device,
                height: height,
                width: width
            ));
        registration.EndExportWrite = feed.EndWrite;
        registration.TryBeginExportWrite = feed.Slots.TryBeginWrite;
        m_viewExports[camera.Name] = feed;
        ReconcileViews();

        return feed;
    }
    // The Vulkan host's view export: a Direct3D 12 simultaneous-access texture and a shared fence made on the binder's
    // headless Direct3D 12 device on the render adapter, which the render device imports to write and to signal, as the
    // camera route imports the targets it samples. The export owns all three and holds the headless device until it goes.
    [SupportedOSPlatform("windows10.0.10240")]
    private ImportedViewExport CreateImportedViewExport(IGpuDeviceContext device, uint width, uint height) {
        var adapterLuid = (m_renderAdapterLuid ?? throw new InvalidOperationException(message: "a view export needs the render adapter's LUID"));
        var targetDevice = (m_cameraTargetDevice ??= new DisposeAfterDependents<IDisposable>(resource: new DirectXDeviceContext(
            adapterLuid: adapterLuid,
            deviceApi: new DirectXNativeDeviceApi(),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        )));
        var export = new DirectXGpuSurfaceExportFactory(deviceContext: ((DirectXDeviceContext)targetDevice.Resource));
        var texture = export.CreateSharedComputeImage(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            width: width
        );
        IGpuExportableFence? fence = null;

        try {
            fence = export.CreateExportableFence();

            if (!device.Services.SurfaceTransferFactory.TryImportWritable(
                format: texture.Format,
                height: height,
                image: out var imported,
                refusal: out var refusal,
                sharedFenceHandle: fence.SharedHandle,
                sharedHandle: texture.SharedHandle,
                usage: texture.Usage,
                width: width
            )) {
                throw new InvalidOperationException(message: $"the render device cannot write a view export: {refusal}");
            }

            return new ImportedViewExport(
                fence: fence,
                imported: imported,
                targetDevice: targetDevice,
                texture: texture
            );
        } catch {
            fence?.Dispose();
            texture.Dispose();

            throw;
        }
    }
    private ProbeFeed GetOrAddProbeFeed(string id) {
        if (!m_probeFeeds.TryGetValue(
            key: id,
            value: out var feed
        )) {
            feed = new ProbeFeed(id: id);
            m_probeFeeds[id] = feed;
        }

        return feed;
    }
    // Provisions every requested ring whose extent the current one does not match, then reads each feed's liveness
    // from its ring, and wakes the render adapter's kernel host so every kernel whose trigger published runs. Runs once
    // per publish, after the camera device is serviced, on the render thread.
    private void ServiceProbeFeeds(IGpuDeviceContext deviceContext) {
        foreach (var feed in m_probeFeeds.Values) {
            if (feed.Request is { Width: > 0, Height: > 0 } request) {
                if (
                    (feed.Output is not { } output) ||
                    (output.Width != request.Width) ||
                    (output.Height != request.Height)
                ) {
                    ProvisionProbeOutput(
                        deviceContext: deviceContext,
                        feed: feed,
                        height: request.Height,
                        width: request.Width
                    );
                }
            }

            feed.Live = (feed.Output is { Slots.LatestSlot: >= 0 });
            feed.Fault = (feed.Live
                ? null
                : (feed.Fault ?? "probe awaiting a first frame")
            );
        }

        m_renderedKernels?.Signal();
    }
    private void ProvisionProbeOutput(IGpuDeviceContext deviceContext, ProbeFeed feed, int width, int height) {
        feed.Release();

        if (m_renderAdapterLuid is not { } adapterLuid) {
            feed.Fault = "the render adapter reports no LUID";

            return;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            feed.Fault = "shared probe textures need Windows 10";

            return;
        }
        if (!TryProvisionSharedRing(
            adapterLuid: adapterLuid,
            deviceContext: deviceContext,
            fault: out var fault,
            fence: out var fence,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            images: out var images,
            importedViews: out var views,
            imports: out var imports,
            sharedFence: true,
            width: width
        )) {
            feed.Fault = fault;

            return;
        }

        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: images.Count);

        var targets = new SharedTargetRing(
            fence: fence,
            images: images,
            importedViews: views,
            imports: imports,
            ring: slots,
            targetDevice: m_cameraTargetDevice
        );

        feed.Targets = targets;
        feed.Output = new ProbeKernelOutput(
            Width: width,
            Height: height,
            TargetFormat: GpuPixelFormat.R8G8B8A8Unorm,
            SharedTargetHandles: targets.SharedHandles,
            Slots: slots,
            SharedFenceHandle: targets.ProducerFenceHandle
        );
        feed.Fault = null;
    }
    // Retires every probe's ring on the current device and keeps each feed's request, so after a device loss the next
    // publish provisions a fresh ring (a new generation) on the replacement. The render adapter's kernel host goes first,
    // ending every run on it, since the replacement device may sit on another adapter.
    private void ReleaseProbeFeeds() {
        m_renderedKernels?.Dispose();
        m_renderedKernels = null;

        foreach (var feed in m_probeFeeds.Values) {
            feed.Release();
        }
    }
    // No GPU teardown of its own — every export's image is owned by its view's engine, which the render graph disposes.
    // Clearing the map only drops this binder's own bookkeeping.
    private void DisposeViewExports() {
        foreach (var feed in m_viewExports.Values) {
            Detach(feed: feed);
        }

        m_viewExports.Clear();
        m_viewExportReferences.Clear();
    }

    // One probe output's feed: the ring its kernel publishes into and the render resources behind it, the run that
    // writes it, plus the pending extent request and live/fault state.
    private sealed class ProbeFeed(string id) {
        public bool Declared { get; set; }
        public string? Fault { get; set; }
        public string Id { get; } = id;
        public bool Live { get; set; }
        // How the kernel's writes reach the render device: the ring's shared fence unless the render device refused it,
        // then as the kernel opened it.
        public SharedFenceOrder Order => ((Targets is { FenceRefusal.Length: > 0 } targets)
            ? new SharedFenceOrder(
                Reason: targets.FenceRefusal,
                SharedFence: false
            )
            : (Run?.Order ?? SharedFenceOrder.Pending));
        public ProbeKernelOutput? Output { get; set; }
        public (int Width, int Height)? Request { get; set; }
        public IProbeKernelRun? Run { get; set; }
        public SharedTargetRing? Targets { get; set; }

        public GpuImageLease AcquireFrame() {
            if (
                !Live ||
                (Targets is not { } targets)
            ) {
                return 0;
            }

            return (targets.TryAcquire(frame: out var frame)
                ? frame
                : 0
            );
        }
        public nint Handle() {
            if (
                !Live ||
                (Output is not { } output) ||
                (Targets is not { } targets)
            ) {
                return 0;
            }

            return targets.Handle(slot: output.Slots.LatestSlot);
        }
        public void Release() {
            var targets = Targets;

            Targets = null;
            Output = null;
            Live = false;
            targets?.Retire();
        }
    }
    // A probe output as a source instance's feed: an imported source whose image the probe's kernel writes on its host's
    // device, published with the value it signals on the ring's shared fence, which the adapter hands out through the
    // capture gate as external content. The binder provisions and services the ring each publish, and retires it on
    // device loss, so the feed publishes, recovers and releases nothing of its own. A probe lights nothing.
    private sealed class ProbeSourceFeed(ProbeFeed feed) : IWorldImportFeed {
        private ImageSourceDescriptor m_descriptor = new(
            Cadence: ImageSourceCadence.Tick,
            Color: ImageColorEncoding.Srgb,
            Content: ImageContentClass.External,
            Format: ImagePixelFormat.R8G8B8A8Unorm,
            Height: 0,
            Producer: WorldImageProducerSettings.ProbeId,
            Transport: ImageSourceTransport.Imported,
            Width: 0
        );

        // The extent is the provisioned ring's, zero on both axes while none is; the descriptor is a new record only when
        // it moves.
        public ImageSourceDescriptor Descriptor {
            get {
                var (width, height) = ((feed.Output is { } output)
                    ? (((uint)output.Width), ((uint)output.Height))
                    : (0U, 0U));

                if (
                    (width != m_descriptor.Width) ||
                    (height != m_descriptor.Height)
                ) {
                    m_descriptor = (m_descriptor with {
                        Height = height,
                        Width = width,
                    });
                }

                return m_descriptor;
            }
        }
        public string? Fault => (feed.Live
            ? null
            : (feed.Fault ?? "probe awaiting a first frame")
        );
        public ProbeFeed Feed => feed;
        public Vector3 Light => Vector3.Zero;

        public GpuImageLease AcquireFrame() => feed.AcquireFrame();
        public void Dispose() { }
        public nint Handle() => feed.Handle();
        public void NotifyDeviceLost() { }
        public void Publish(in FrameContext context) { }
    }
    // A view export on the Vulkan host: the render device's import of a texture and a fence the binder's headless
    // Direct3D 12 device made, which the engine writes and signals as it would an image of its own. The import goes first,
    // then the fence and the texture, and the headless device last of all.
    private sealed class ImportedViewExport : IGpuExportableImage {
        private readonly IGpuExportableFence m_fence;
        private readonly IGpuExportableImage m_imported;
        private readonly DisposeAfterDependents<IDisposable> m_targetDevice;
        private readonly IGpuExportableImage m_texture;

        private bool m_disposed;

        public ImportedViewExport(IGpuExportableImage imported, IGpuExportableImage texture, IGpuExportableFence fence, DisposeAfterDependents<IDisposable> targetDevice) {
            m_fence = fence;
            m_imported = imported;
            m_targetDevice = targetDevice;
            m_texture = texture;
            targetDevice.AddDependent();
        }

        public GpuPixelFormat Format => m_imported.Format;
        public uint Height => m_imported.Height;
        public nint ImageHandle => m_imported.ImageHandle;
        public nint ImageViewHandle => m_imported.ImageViewHandle;
        public nint SharedFenceHandle => m_imported.SharedFenceHandle;
        public nint SharedHandle => m_imported.SharedHandle;
        public GpuImageUsage Usage => m_imported.Usage;
        public uint Width => m_imported.Width;

        public ulong CompleteWrite() => m_imported.CompleteWrite();
        public void Dispose() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            m_imported.Dispose();
            m_fence.Dispose();
            m_texture.Dispose();
            m_targetDevice.RemoveDependent();
        }
    }
    // One camera's export state, keyed by camera name: the registration it exports, the extent its image was made at, and
    // the one-image publication its readers share. It carries no GPU handle of its own — the view's engine's exported
    // handle, fence and identity are read fresh each call.
    private sealed class ViewExportFeed(string name, uint width, uint height) {
        public object? CompletedGeneration { get; private set; }

        public uint Height { get; } = height;

        public ProbeKernelInput.Ring? Input { get; set; }
        public object? InputGeneration { get; set; }

        public string Name { get; } = name;
        public SingleSlotPublication Slots { get; } = new();
        public uint Width { get; } = width;

        // Publishes the identity of the engine a completed frame rendered on before the ring's ready state. A failed first
        // submission after device loss may preserve an older readable image, but it must never bless the replacement
        // engine's new handle as completed.
        public void EndWrite(bool completed, object? generation, ulong fenceValue) {
            if (completed) {
                CompletedGeneration = generation;
            }

            Slots.EndWrite(
                completed: completed,
                fenceValue: fenceValue
            );
        }
    }
}

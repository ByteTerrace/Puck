using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // One feed per declared probe that writes a texture, keyed by ProbeKey. The probes host of the probe's world
    // declares the feed at its own construction and asks for a ring of the kind's output extent when its kernel run
    // starts; the ring is provisioned here, on the render thread, at the next publish (the exportable targets need the
    // render device).
    private readonly Dictionary<string, ProbeFeed> m_probeFeeds = new(comparer: StringComparer.Ordinal);
    // Each world a probe host has reconciled, with why none runs for it, or null while one does. The boot world's host
    // runs from boot. A feed of a world no host has reconciled yet waits; one of a world whose host refused stands its
    // capture fill in, naming the refusal.
    private readonly Dictionary<string, string?> m_probeWorlds = new(comparer: StringComparer.Ordinal) {
        [WorldDefinitionLoader.BootInstanceName] = null,
    };

    // One export state per camera whose view a probe socket reads, keyed by ViewExportKey and shared by every probe
    // socket naming the same camera of the same world. Reference counting keeps the export alive until the final live
    // probe instance releases that camera.
    private readonly Dictionary<string, ViewExportFeed> m_viewExports = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, int> m_viewExportReferences = new(comparer: StringComparer.Ordinal);
    // The export each camera view of another world renders into, by the view's name (WorldViewNames.NestedCamera).
    private readonly Dictionary<string, ViewExportFeed> m_nestedViewExports = new(comparer: StringComparer.Ordinal);

    /// <summary>Returns the key a probe's output ring, or the export of a camera a probe's view socket reads, is held
    /// under: the name itself for the boot world, whose host the boot runs, and the world-qualified generated name
    /// (<see cref="GeneratedName.Join"/>) for a world shown through a screen, whose own host runs it, so two worlds' equal
    /// names are two rings or exports.</summary>
    /// <param name="world">The name of the world instance whose probe host runs the probe.</param>
    /// <param name="id">The <c>probes[].id</c>, a seat-relative instance's key, or a <c>cameras[]</c> row name.</param>
    /// <returns>The key.</returns>
    public static string ProbeKey(string world, string id) => (WorldDefinitionLoader.IsBootInstance(name: world)
        ? id
        : GeneratedName.Join(world, id));
    /// <summary>Records that a probe host runs for a world shown through a screen, or why none can, so every probe source
    /// of that world either reads its host's rings or stands its capture fill in, naming the refusal, rather than waiting
    /// for a host that never comes.</summary>
    /// <param name="world">The name of the world instance.</param>
    /// <param name="refusal">Why no host runs for the world, or <see langword="null"/> when one does.</param>
    public void HostProbeWorld(string world, string? refusal) => m_probeWorlds[world] = refusal;
    /// <summary>Forgets a world shown through a screen whose probe host was retired, so its probe sources wait for the next
    /// host that reconciles it.</summary>
    /// <param name="world">The name of the world instance.</param>
    public void ForgetProbeWorld(string world) {
        if (!WorldDefinitionLoader.IsBootInstance(name: world)) {
            _ = m_probeWorlds.Remove(key: world);
        }
    }
    /// <summary>Records why a probe's kernel run cannot start until something it reads changes (a socket no host can bind,
    /// a kernel its host refused, a run that ended on a fault), so a screen showing the probe stands its capture fill in
    /// and names the reason rather than waiting for a first frame that never comes; <see langword="null"/> clears it once
    /// the run attaches.</summary>
    /// <param name="id">The probe's ring key (<see cref="ProbeKey"/>).</param>
    /// <param name="refusal">Why the run cannot start, or <see langword="null"/>.</param>
    /// <param name="waiting">Why the run has not started yet while it waits on something that is coming, or
    /// <see langword="null"/>; read only when <paramref name="refusal"/> is <see langword="null"/>.</param>
    public void ReportProbeRun(string id, string? refusal, string? waiting) {
        var feed = GetOrAddProbeFeed(id: id);

        feed.RunRefusal = refusal;
        feed.RunWaiting = waiting;
    }
    /// <summary>Collects the worlds the presentation shows through screens other than the boot world, each once, with the
    /// definition its first level last reconciled: the worlds whose probes a host of their own runs.</summary>
    /// <param name="worlds">Receives each world's instance name and definition; cleared first.</param>
    public void CollectPresentedWorlds(Dictionary<string, WorldDefinition> worlds) {
        ArgumentNullException.ThrowIfNull(argument: worlds);

        worlds.Clear();

        foreach (var screens in m_nestedOwners.Keys) {
            if (
                !WorldDefinitionLoader.IsBootInstance(name: screens.World) &&
                (screens.Definition is { } definition)
            ) {
                _ = worlds.TryAdd(
                    key: screens.World,
                    value: definition
                );
            }
        }
    }

    // The render adapter's own kernel host, opened the first time a probe whose trigger reads a rendered source starts,
    // on the adapter the render device reported; woken once per publish.
    private IRenderedProbeKernelHost? m_renderedKernels;

    /// <summary>Declares that a probe writes a texture, so a screen may show it, and whether that output is external content
    /// (<see cref="WorldCaptureFills.IsExternalProbe"/>), which a capture shows the placeholder in place of. Idempotent.</summary>
    /// <param name="id">The <c>probes[].id</c>.</param>
    /// <param name="external">Whether the output derives from a device; a probe fed only by in-world views and recorded
    /// tracks is not external.</param>
    public void DeclareProbeOutput(string id, bool external) {
        var feed = GetOrAddProbeFeed(id: id);

        feed.Declared = true;
        feed.External = external;
    }
    // Whether a screen source or a HUD frame's source shows external content. A probe is external until its row is declared
    // otherwise, so an undeclared one is withheld rather than shown.
    private bool ShowsExternal(WorldScreenSource? source) => WorldCaptureFills.IsExternal(
        probeIsExternal: (m_probeIsExternal ??= ProbeIsExternal),
        source: source
    );
    // Whether a probe's feed is external or not yet known; held as one delegate, since every frame asks it per screen.
    private bool ProbeIsExternal(string id) => (!m_probeFeeds.TryGetValue(key: id, value: out var feed) || feed.External);
    private Func<string, bool>? m_probeIsExternal;
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

        var provisioned = feed.TryProvisioned(
            generation: out generation,
            height: height,
            output: out output,
            width: width
        );

        fault = (provisioned
            ? ""
            : (feed.Fault ?? "probe output awaiting provisioning"));

        return provisioned;
    }
    /// <summary>Returns the render adapter's own kernel host, which runs a probe whose trigger socket reads a rendered
    /// source, opening it on the adapter the render device reported the first time one asks.</summary>
    /// <param name="host">The host, set only when this returns <see langword="true"/>; owned by this binder.</param>
    /// <param name="fault">Why no host is available, set only when this returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when the host is open.</returns>
    public bool TryGetRenderedKernelHost([NotNullWhen(true)] out IProbeKernelHost? host, out string fault) {
        host = null;

        if (m_disposed) {
            fault = "binder disposed";

            return false;
        }
        if (m_renderAdapterLuid is not { } adapterLuid) {
            fault = "the render adapter reports no LUID yet";

            return false;
        }

        return TryOpenRenderedKernels(
            adapterLuid: adapterLuid,
            fault: out fault,
            host: out host
        );
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
            feed.RunRefusal = null;
            feed.RunWaiting = null;
            feed.Release();
        }
    }
    /// <summary>Binds a declared screen to a probe's texture output, whose source instance the screen shows over its row
    /// from the render graph's next frame. Fails for an undeclared screen or a probe that declares no output.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="id">The <c>probes[].id</c>.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryProbe(int index, string id) {
        if (!m_slots.TryGetValue(
            key: index,
            value: out var slot
        )) {
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

        Rebind(
            live: new WorldScreenSource.Probe(Id: id),
            slot: slot
        );

        return (Ok: true, Message: $"screen {index} showing probe '{id}'");
    }
    /// <summary>Reads a named camera's offscreen view as a kernel input ring, registering the view for export on first
    /// request; the ring arrives at a later publish. The exported image is a Direct3D 12 simultaneous-access texture a
    /// probe kernel host's Direct3D 11 <c>OpenSharedResource1</c> opens, and each frame is published with the value its
    /// write signals on a shared fence the kernel waits for: on the Direct3D 12 host the render device's own texture and
    /// fence, and on the Vulkan host a texture and fence made on the binder's headless Direct3D 12 device, which the
    /// render device imports to write and signal (<see cref="IGpuSurfaceTransferFactory.TryImportWritable"/>). A camera of
    /// the boot world exports its camera registration's view; a camera of a world shown through a screen exports the view
    /// a level presenting that world films it through for its probes (<see cref="WorldNestedScreens{TChild}.TryProbeCamera"/>),
    /// moving to another level's when that one closes, which is a new generation.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    /// <param name="ring">The exported ring, set only when this returns <see langword="true"/>.</param>
    /// <param name="generation">The export's identity — fresh on every (re)creation, for example after device loss —
    /// set only when this returns <see langword="true"/>.</param>
    /// <param name="fault">Why no export is available yet, set only when this returns <see langword="false"/>.</param>
    /// <param name="world">The name of the world instance whose camera it is: the boot world's by default.</param>
    /// <returns><see langword="true"/> when the view is exported and has rendered at least once.</returns>
    public bool TryGetViewExport(string cameraName, [NotNullWhen(true)] out ProbeKernelInput.Ring? ring, out object? generation, out string fault, string world = WorldDefinitionLoader.BootInstanceName) {
        ring = null;
        generation = null;

        if (m_disposed) {
            fault = "binder disposed";

            return false;
        }

        var boot = WorldDefinitionLoader.IsBootInstance(name: world);
        WorldCamera? camera = null;
        WorldNestedCamera presented = default;

        if (boot && ((camera = ResolveCamera(name: cameraName)) is null)) {
            fault = $"camera '{cameraName}' not declared";

            return false;
        }
        if (!boot && !TryPresentedProbeCamera(
            camera: out presented,
            cameraName: cameraName,
            world: world
        )) {
            if (m_viewExports.Remove(
                key: ProbeKey(id: cameraName, world: world),
                value: out var stale
            )) {
                Detach(feed: stale);
                ReconcileViews();
            }

            fault = $"no level presenting world '{world}' films camera '{cameraName}' yet";

            return false;
        }

        if (m_viewPipelines is null) {
            fault = "the views are not configured";

            return false;
        }

        var feed = (OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)
            ? (boot
                ? GetOrAddViewExport(camera: camera!)
                : GetOrAddPresentedViewExport(
                    camera: presented,
                    key: ProbeKey(id: cameraName, world: world)
                ))
            : null);
        var image = ((feed is null)
            ? null
            : Runtime?.NodeOf(instance: feed.Name)?.ExportedImage);

        generation = image;
        ring = ((image is null)
            ? null
            : ReadyRing(
                feed: feed!,
                image: image
            ));
        fault = ((ring is not null)
            ? ""
            : ((feed is null)
                ? "view export needs Windows 10"
                : "view export awaiting a first rendered frame"));

        return (ring is not null);
    }
    /// <summary>Retains one live probe instance's use of a named view export.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    /// <param name="world">The name of the world instance whose camera it is: the boot world's by default.</param>
    public void RetainViewExport(string cameraName, string world = WorldDefinitionLoader.BootInstanceName) {
        var key = ProbeKey(id: cameraName, world: world);

        m_viewExportReferences[key] = (m_viewExportReferences.TryGetValue(
            key: key,
            value: out var count
        )
            ? (count + 1)
            : 1
        );
    }
    /// <summary>Drops a view export requested through <see cref="TryGetViewExport"/>, so the view's node renders into
    /// images of its own again from its next film. A boot camera still filmed by a jumbotron screen keeps rendering (the
    /// release only stops the export); one no screen films is released entirely, matching
    /// <see cref="ReleaseOrphanedCameraView"/>'s own orphan contract. A camera of a world shown through a screen keeps
    /// the view its level films while the level shows it, demanded only by the screens that show it.</summary>
    /// <param name="cameraName">The <c>cameras[]</c> row name.</param>
    /// <param name="world">The name of the world instance whose camera it is: the boot world's by default.</param>
    public void ReleaseViewExport(string cameraName, string world = WorldDefinitionLoader.BootInstanceName) {
        var key = ProbeKey(id: cameraName, world: world);

        if (
            m_viewExportReferences.TryGetValue(
            key: key,
            value: out var references
        ) &&
            (references > 1)
        ) {
            m_viewExportReferences[key] = (references - 1);

            return;
        }

        _ = m_viewExportReferences.Remove(key: key);

        if (!m_viewExports.Remove(
            key: key,
            value: out var feed
        )) {
            return;
        }

        Detach(feed: feed);

        if (WorldDefinitionLoader.IsBootInstance(name: world)) {
            ReleaseOrphanedCameraView(name: feed.Name);
        } else if (!m_disposed) {
            ReconcileViews();
        }
    }
    // The view a level presenting a world films one of its cameras through for its probes: the first such level's.
    private bool TryPresentedProbeCamera(string world, string cameraName, out WorldNestedCamera camera) {
        foreach (var level in m_nestedOwners.Keys) {
            if (
                string.Equals(
                    a: level.World,
                    b: world,
                    comparisonType: StringComparison.Ordinal
                ) &&
                level.TryProbeCamera(
                    camera: out camera,
                    cameraName: cameraName
                )
            ) {
                return true;
            }
        }

        camera = default;

        return false;
    }
    // Exports the view a level films a presented world's camera through for its probes: the view's node copies each
    // frame into the export (SetNestedCameraViews sets it on the node), and the view is shown outside the world while it
    // exports, so it renders at its refresh whether or not a screen shows it. An export of a level that no longer films
    // the camera moves to the one that does.
    [SupportedOSPlatform("windows10.0.10240")]
    private ViewExportFeed GetOrAddPresentedViewExport(string key, WorldNestedCamera camera) {
        if (m_viewExports.TryGetValue(
            key: key,
            value: out var existing
        )) {
            if (string.Equals(
                a: existing.Name,
                b: camera.Name,
                comparisonType: StringComparison.Ordinal
            )) {
                return existing;
            }

            Detach(feed: existing);
        }

        var width = camera.Camera.RenderWidth;
        var height = camera.Camera.RenderHeight;
        var feed = new ViewExportFeed(
            create: ExportFactory(
                height: height,
                width: width
            ),
            height: height,
            name: camera.Name,
            width: width
        );

        m_viewExports[key] = feed;
        m_nestedViewExports[camera.Name] = feed;

        if (Runtime?.NodeOf(instance: camera.Name) is { } node) {
            node.Export = feed;
        }

        ReconcileViews();

        return feed;
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
    // view's node stops copying into the exported image from its next frame (FilmViews for a boot camera, FilmLevel for
    // a camera of another world, each of which sets the node's export), so nothing writes it again. It never touches a
    // node, which a teardown may already have disposed, and never waits: a reader still holding the image finishes with
    // it on its own device, which keeps its own reference to the shared texture and fence.
    private void Detach(ViewExportFeed feed) {
        feed.Slots.Retire();

        if (m_cameraViews.TryGetValue(
            key: feed.Name,
            value: out var registration
        )) {
            registration.Export = null;
        }
        if (
            m_nestedViewExports.TryGetValue(
                key: feed.Name,
                value: out var nested
            ) &&
            ReferenceEquals(
                objA: nested,
                objB: feed
            )
        ) {
            _ = m_nestedViewExports.Remove(key: feed.Name);
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
            if (registration.Export is not null) {
                return existing;
            }

            Detach(feed: existing);
            _ = m_viewExports.Remove(key: camera.Name);
        }

        var width = camera.RenderWidth;
        var height = camera.RenderHeight;
        var feed = new ViewExportFeed(
            create: ExportFactory(
                height: height,
                width: width
            ),
            height: height,
            name: name,
            width: width
        );

        registration.Export = feed;
        m_viewExports[camera.Name] = feed;
        ReconcileViews();

        return feed;
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

        SignalRenderedKernels();
    }
    private void ProvisionProbeOutput(IGpuDeviceContext deviceContext, ProbeFeed feed, int width, int height) {
        feed.Release();
        feed.Fault = ((m_renderAdapterLuid is { } adapterLuid)
            ? ProvisionProbeRing(
                adapterLuid: adapterLuid,
                deviceContext: deviceContext,
                feed: feed,
                height: height,
                width: width
            )
            : "the render adapter reports no LUID");
        feed.Refusal = feed.Fault;
        m_extentRevision++;
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
        if (m_probeFeeds.Count > 0) {
            m_extentRevision++;
        }
    }
    // No GPU teardown of its own — every export's image is owned by its view's node, which the render graph disposes.
    // Clearing the map only drops this binder's own bookkeeping.
    private void DisposeViewExports() {
        foreach (var feed in m_viewExports.Values) {
            Detach(feed: feed);
        }

        m_viewExports.Clear();
        m_viewExportReferences.Clear();
        m_nestedViewExports.Clear();
    }

    // Retires a shared ring, when there is one; disposed once no submitted frame samples it.
    private static void RetireRing(SharedTargetRing? ring) => ring?.Retire();

    // One probe output's feed: the ring its kernel publishes into and the render resources behind it, the run that
    // writes it, plus the pending extent request and live/fault state.
    private sealed partial class ProbeFeed(string id) {
        public bool Declared { get; set; }
        // Whether the output derives from a device (a camera or a capture), which a capture withholds.
        public bool External { get; set; } = true;
        public string? Fault { get; set; }
        // Why the probe's ring could not be provisioned, until a later provision succeeds.
        public string? Refusal { get; set; }
        // Why its host's kernel run cannot start, or what it waits on, as the host last reported (ReportProbeRun).
        public string? RunRefusal { get; set; }
        public string? RunWaiting { get; set; }
        public string Id { get; } = id;
        public bool Live { get; set; }
        public ProbeKernelOutput? Output { get; set; }
        public (int Width, int Height)? Request { get; set; }
        public IProbeKernelRun? Run { get; set; }
        public SharedTargetRing? Targets { get; set; }

        public void Release() {
            var targets = Targets;

            Targets = null;
            Output = null;
            Live = false;
            RetireRing(ring: targets);
        }
        // Reads the ring provisioned at an extent, and its identity, when one is.
        public bool TryProvisioned(int width, int height, out ProbeKernelOutput output, out object? generation) {
            var ready = ((Targets is not null) && (Output is { } provisioned) && (provisioned.Width == width) && (provisioned.Height == height));

            output = (ready
                ? Output!.Value
                : default);
            generation = (ready
                ? Targets
                : null);

            return ready;
        }
    }
    // A camera view's export: the image its node copies each frame into, created on the render device, which a probe's
    // kernel host reads through the ring's latest completed slot.
    private sealed partial class ViewExportFeed(string name, uint width, uint height, Func<IGpuDeviceContext, IGpuExportableImage> create) : IShaderPipelineOutputExport {
        public uint Height { get; } = height;
        public string Name { get; } = name;
        public SingleSlotPublication Slots { get; } = new();
        public uint Width { get; } = width;

        private Func<IGpuDeviceContext, IGpuExportableImage> Factory { get; } = create;
    }
}

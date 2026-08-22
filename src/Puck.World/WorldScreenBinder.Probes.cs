using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Platform;
using Puck.Platform.Probes;
using Puck.SdfVm;

namespace Puck.World;

internal sealed partial class WorldScreenBinder {
    // One feed per declared probe that writes a texture, keyed by probe id. The probes host declares the feed at its
    // own construction and asks for a ring of the kind's output extent when its kernel run starts; the ring is
    // provisioned here, on the render thread, at the next publish (the exportable targets need the render device).
    private readonly Dictionary<string, ProbeFeed> m_probeFeeds = new(comparer: StringComparer.Ordinal);

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
}

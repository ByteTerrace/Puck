using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.Platform;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.World.Client;
using Puck.World.Qr;
using Puck.World.Server;

namespace Puck.World;

/// <summary>One diegetic screen's live state for the <c>screen.state</c> verb — whether a machine is assigned, the engine
/// that hosts it, the current source handle (nonzero = bound this frame), the stepped-frame count, and the boot fault (a
/// declared machine whose content file was missing, a webcam that would not open, a captured window not found), if any.</summary>
/// <param name="Assigned">Whether a machine is booted on the screen.</param>
/// <param name="Engine">The screen-machine engine id hosting the machine (meaningful only when <paramref name="Assigned"/>).</param>
/// <param name="Handle">The current source image-view handle (0 = unbound → the procedural fallback).</param>
/// <param name="FramesStepped">How many frames the machine has stepped since it booted.</param>
/// <param name="PendingSteps">Accepted queued-machine steps not yet completed; zero for synchronous machines.</param>
/// <param name="MaximumPendingSteps">The queued machine's finite pending-segment capacity; zero for synchronous
/// machines.</param>
/// <param name="BackpressureEvents">How many queued submissions waited for capacity since the current content was
/// loaded; zero for synchronous machines.</param>
/// <param name="Fault">A slot's live fault (a missing content file, no camera device, a window not found), or <see langword="null"/>.</param>
internal readonly record struct WorldScreenState(bool Assigned, string? Engine, nint Handle, long FramesStepped,
    long PendingSteps, int MaximumPendingSteps, long BackpressureEvents, string? Fault);

/// <summary>One screen's QR authoring, as <c>screen.source &lt;index&gt; qr</c> reads it back — the authored inputs plus everything the
/// encoder DERIVED from them, so a piped session can assert the decision the setter made rather than infer it from
/// pixels.</summary>
/// <param name="Payload">The encoded payload string.</param>
/// <param name="Level">The resolved error-correction level.</param>
/// <param name="Version">The encoder-chosen QR version (1..10) — the smallest that held the payload at
/// <paramref name="Level"/>.</param>
/// <param name="Mask">The encoder-chosen mask pattern (0..7) — the lowest-penalty of the eight.</param>
/// <param name="QuietZoneModules">The rendered quiet-zone width in modules on every side.</param>
/// <param name="Width">The rasterized buffer's width in pixels.</param>
/// <param name="Height">The rasterized buffer's height in pixels.</param>
internal readonly record struct WorldScreenQrAuthoring(string Payload, QrErrorCorrectionLevel Level, int Version,
    int Mask, int QuietZoneModules, uint Width, uint Height);

/// <summary>
/// Binds the world's declared <see cref="WorldScreen"/>s to their live GPU sources — the seam between the pure screen
/// DATA and the engine's per-index provider maps. Each declared screen owns a slot that can carry a CPU-fed test pattern,
/// an authored QR code, a live webcam feed, a desktop-window capture feed, a jumbotron view, or nothing (the engine's
/// procedural no-signal fallback). A provider is registered for EVERY declared index up front — returning the slot's current handle or 0 — so
/// a runtime <c>screen.source &lt;index&gt; camera</c>/<c>capture</c> binds without rebuilding the engine (the engine copies the
/// provider KEY SET once but polls each provider live, and a 0 handle reads as unbound).
/// A shared singleton so the render factory, the screen verbs, and <c>world.screens</c> read one instance.
/// </summary>
/// <remarks>
/// This type no longer boots, steps, cable-links, or memory-peeks a deterministic <see cref="IScreenMachine"/> —
/// <see cref="Server.WorldMachineHost"/> (a core singleton, constructed and stepped by <see cref="Server.WorldServer"/>
/// in EVERY boot shape, headless included) owns all of that now, so ROM state is sim state rather than
/// presentation-fed. This binder is a PURE READER of the host's outputs
/// (<see cref="Server.WorldMachineHost.Handle"/>/<see cref="Server.WorldMachineHost.Light"/> for the room, and
/// <see cref="Publish"/> still calls <see cref="IScreenMachine.PublishFrame"/> on the host's live instance — the one
/// GPU call this project makes on a machine's behalf, since <c>Puck.World.Server</c> cannot reach a GPU device
/// context). It also FACADES several read-only <see cref="WorldMachineHost"/> members (<c>HasMachine</c>,
/// <c>HasEngine</c>, <c>TryReadMachineInsert</c>, <c>TryMagazine</c>, <c>AudioMachine</c>, <c>TryPeek</c>,
/// <c>CaptureLinks</c>, <c>LinkOf</c>, <c>DescribeLinks</c>, <c>TryReadLinkMembers</c>) so existing presentation-side
/// callers (<c>PlayerCommandModule</c>, <c>WorldAudioDirector</c>, <c>WorldSessionCapture</c>'s <c>world.save</c>
/// fold, <c>ScreenCommandModule</c>'s read-only verbs) reach the host's state through the SAME reference they already
/// held, with no call-site churn — every one of these facades already reads authoritative server state, so no second
/// copy of it lives here. Machine LIFECYCLE mutation (insert/eject/select/options/link/unlink) is REMOVED from this
/// type entirely: <c>ScreenCommandModule</c> submits a <c>WorldScreenOp</c> through
/// <c>IServerLink.SubmitScreenOp</c> instead, landing in the ordered submission domain (see <c>WorldScreenOp</c>'s
/// own remarks). Camera/capture/window-capture/jumbotron-view/test-pattern screen sources are genuinely presentation
/// and are UNCHANGED — machines are the only capability this type gave up (see
/// <c>docs/capability-channels-STATE.md</c>'s DECIDED table for the full accounting).
/// <para>An unbound slot (a <see cref="WorldScreenSource.None"/> screen, or a live feed with no signal) registers a
/// provider returning 0, so the engine leaves its surface unbound and lights it with the procedural no-signal
/// fallback — never black. ONE webcam session is opened engine-wide and SHARED by every camera screen (two sessions
/// on one physical device flicker), so N camera screens sample one feed. Single-threaded: <see cref="Publish"/> and
/// simulation-routed screen mutations all run on the launcher's window-pump thread, so no lock guards this state.</para>
/// </remarks>
internal sealed class WorldScreenBinder : IDisposable {
    private const ulong PublishTimingReportInterval = 60UL;
    // A QR feed picks its module pixel size so the rendered image lands near QrTargetPixelExtent square whatever
    // version the payload chose, clamped to stay legibly crisp (at least QrMinModulePixels per module) and well under
    // the validator's MaxSurfaceDimension ceiling (QrMaxModulePixels keeps even a version-10 grid with a generous
    // quiet zone far below it).
    private const int QrDefaultQuietZoneModules = 4;
    private const int QrMaxModulePixels = 12;
    private const int QrMinModulePixels = 4;
    private const int QrTargetPixelExtent = 640;

    private readonly record struct ScreenPublishTiming(long CameraTicks, long MachineTicks, long WindowCaptureTicks, long PatternTicks) {
        public long TotalTicks => (((CameraTicks + MachineTicks) + WindowCaptureTicks) + PatternTicks);
    }

    // The authoritative screen-machine host — owns every booted IScreenMachine; this binder reads its outputs
    // (Handle/Light/MachineAt) and facades several of its read-only members. Never mutated through here — see this
    // type's own remarks.
    private readonly WorldMachineHost m_machines;
    private readonly ICameraCaptureService m_cameraCapture;
    private readonly INativeImageCaptureService m_windowCapture;
    // The D3D12-host GPU capture transport: on the Direct3D 12 host, window/monitor captures publish GPU-side into shared
    // simultaneous-access textures the screens sample directly (no CPU round trip); the Vulkan host keeps the CPU path.
    // The factory is non-null only on the D3D12 host, and the render adapter LUID is resolved once from the render device
    // context at the first publish (the device does not exist at construction), so capture feeds open on the render GPU.
    private readonly bool m_hostsOnDirectX;
    private readonly DirectXGpuSurfaceExportFactory? m_surfaceExport;
    private long? m_renderAdapterLuid;
    // The world's placeable-camera rows — booted from the definition and REPLACED by ReconcileCameras when a camera
    // mutation delivers, so a runtime screen.source <index> view (and every later resolve) reads the LIVE rows.
    private IReadOnlyList<WorldCamera> m_cameras;
    // The anchor source for anchored cameras (the client's snapshot-fed entity view). Anchor ids are entity indices,
    // so an Anchored view follows the same interpolated render pose the main world draws without reaching into
    // simulation state or duplicating pose math here.
    private readonly ISdfAnchorSource m_anchors;
    private readonly WorldStampPool m_stamps;
    private IReadOnlyList<DynamicTransform> m_viewTransforms = [];
    private readonly Dictionary<int, ScreenSlot> m_slots = new();
    // The screen indices declared at BOOT (construction) — the render engine's frozen provider key set, copied
    // ONCE and never grown. Distinct from m_slots.Keys, which shrinks/grows as ReconcileScreens removes/recreates
    // entries: an index in this set can always have its m_slots/m_sources/m_lights entries safely RECREATED after
    // removal (the engine's own frozen key list still names it), while a genuinely new index (never in this set)
    // still cannot bind live.
    private readonly HashSet<int> m_bootScreenIndices = new();
    private readonly Dictionary<int, Func<nint>> m_sources = new();
    private readonly Dictionary<int, Func<Vector3>> m_lights = new();
    // SdfEngineNode copies m_sources/m_lights into ITS OWN dictionary ONCE, at construction (SdfEngineNode.cs's
    // ctor — new Dictionary<int, Func<...>>(collection: screenSources)) — it never re-reads this binder's
    // dictionaries again, so writing a NEW delegate into m_sources[index] after boot is invisible to the renderer,
    // which keeps polling the delegate identity it copied at boot forever. Each boot index's cell is instead a
    // STABLE, never-replaced delegate target — ResolveHandle/ResolveLight are the SAME method-group identity for the
    // cell's whole lifetime, so the renderer's one-time copy keeps working; only the cell's OWN Slot field is
    // re-pointed when ReconcileScreens recreates a boot index's slot after a remove+reset, which every future poll
    // through the already-copied delegate observes immediately.
    private readonly Dictionary<int, ScreenSourceCell> m_sourceCells = new();
    // Reused scratch for ReconcileScreens' removal pass, so a screen mutation collects the vanished indices without
    // allocating and never mutates m_slots while enumerating it.
    private readonly List<int> m_reconcileRemovals = new();
    // The offscreen view pool backing the View (jumbotron) screens — created by ConfigureViews once the render envelope
    // is known, null until then (and forever when the world declares no View screen). The view config the pool needs is
    // stashed alongside so a runtime screen.source <index> view can register against the same envelope.
    private ViewStack? m_viewStack;
    // Persistent camera-view registrations by camera name — each holds the SdfCameraView (a real GPU resource: its
    // offscreen engine) plus the WorldCamera row it was built from, so a re-point reuses the SAME instance and a
    // camera mutation diffs against the row the LIVE view embodies (pose edit = rig property write; dimension/kind
    // change = release + recreate).
    private readonly Dictionary<string, CameraRegistration> m_cameraViews = new(comparer: StringComparer.Ordinal);
    // Reused scratch for ReconcileCameras (the registered names snapshot walked while m_cameraViews mutates).
    private readonly List<string> m_cameraReconcileScratch = new();
    private SdfViewGpuServices? m_viewServices;
    private bool m_viewHostsOnDirectX;
    private int m_viewProgramWordCapacity;
    private int m_viewInstanceCapacity;
    private int m_viewDynamicTransformCapacity;
    // A jumbotron is a diegetic 160x144 display, not another full-rate player view. ViewStack already persists the last
    // resolved handle when a budgeted view is skipped; this countdown deliberately spends the offscreen SDF render only
    // once every N produced frames. Frame-count cadence is deterministic and avoids introducing a wall clock.
    private int m_viewRefreshDivisor = 4;
    private int m_viewRefreshCountdown;
    // The ONE webcam feed shared by every camera screen (the flicker rule), opened lazily on first demand and null until
    // a camera screen exists; a failed open records m_cameraFault and leaves this null.
    private CameraFeed? m_cameraFeed;
    private bool m_cameraTried;
    private string m_cameraFault = "no camera device present";
    private bool m_disposed;
    private ulong m_publishTimingFrame;
    private ScreenPublishTiming m_publishTimingWorst;

    /// <summary>Initializes the binder over the world's declared screens: a CPU feed for each test-pattern screen, the
    /// shared webcam for each camera screen, and a window-capture session for each capture screen (absent camera /
    /// unopenable window leaves the slot unbound and the fault visible in <c>world.screens</c>/<c>screen.state</c> —
    /// loud data, no crash), plus a source + light provider for every declared index. A declared MACHINE screen
    /// registers no local producer here — <see cref="Server.WorldMachineHost"/> (a peer singleton, already booted by
    /// the time this constructor runs) owns it; this binder's providers read the host directly for those indices.</summary>
    /// <param name="screens">The world's diegetic screens (<see cref="WorldDefinition.Screens"/>).</param>
    /// <param name="machines">The authoritative screen-machine host this binder reads outputs from.</param>
    /// <param name="cameraCapture">The platform webcam service (CPU tier) the camera screens share one session of.</param>
    /// <param name="windowCapture">The platform compositor window-capture service.</param>
    /// <param name="cameras">The world's placeable cameras a View (jumbotron) screen resolves its camera name against.</param>
    /// <param name="anchors">The entity anchor source used by anchored cameras (the client's snapshot-fed view).</param>
    /// <param name="stamps">The compiled creation-look pool supplying authored entity parts.</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 — selects the GPU capture transport for
    /// window/monitor captures (the Vulkan host keeps the CPU-pixel path). Camera capture stays CPU on both.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldScreenBinder(IReadOnlyList<WorldScreen> screens, WorldMachineHost machines, ICameraCaptureService cameraCapture, INativeImageCaptureService windowCapture, IReadOnlyList<WorldCamera> cameras, ISdfAnchorSource anchors, WorldStampPool stamps, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentNullException.ThrowIfNull(argument: cameraCapture);
        ArgumentNullException.ThrowIfNull(argument: windowCapture);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: anchors);
        ArgumentNullException.ThrowIfNull(argument: stamps);

        m_machines = machines;
        m_cameraCapture = cameraCapture;
        m_windowCapture = windowCapture;
        m_cameras = cameras;
        m_anchors = anchors;
        m_stamps = stamps;
        m_hostsOnDirectX = hostsOnDirectX;
        // Windows-10240 guarded because DirectXGpuSurfaceExportFactory is platform-attributed; hostsOnDirectX already
        // implies that floor (Program.cs rejects the D3D12 backend below it), so the check only satisfies the analyzer.
        m_surfaceExport = ((hostsOnDirectX && OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240))
            ? new DirectXGpuSurfaceExportFactory()
            : null);
        var sharedCameraProfile = ResolveSharedCameraProfile(screens: screens);

        foreach (var screen in screens) {
            _ = m_bootScreenIndices.Add(item: screen.Index);

            var slot = new ScreenSlot { DeclaredSource = screen.Source, Index = screen.Index, Machines = m_machines };

            switch (screen.Source) {
                case WorldScreenSource.TestPattern pattern:
                    slot.Pattern = new PatternFeed(
                        pattern: new TestPatternSource(width: pattern.Width, height: pattern.Height),
                        surface: new CpuSurfaceSource()
                    );

                    break;
                case WorldScreenSource.Machine:
                    // WorldMachineHost already booted this index (if it could) at ITS OWN construction — nothing to
                    // do here; Handle()/Light() below read the host directly for a machine-owning index.
                    break;
                case WorldScreenSource.Camera:
                    // The declared webcam: bind the ONE shared session (opened here on first demand). An absent device
                    // leaves the slot unbound with a visible fault.
                    if (EnsureCameraFeed(profile: sharedCameraProfile) is { } cameraFeed) {
                        slot.Camera = cameraFeed;
                    } else {
                        slot.DeclaredFault = m_cameraFault;
                    }

                    break;
                case WorldScreenSource.Capture capture:
                    // The declared compositor capture (a window title or a whole monitor) resolves its live target and
                    // starts its feed. A target momentarily absent on a supported platform (the World window before its
                    // HWND is visible, a disconnected monitor) retains a pending feed and resolves on publication
                    // instead of permanently faulting during composition.
                    BootDeclaredCapture(slot: slot, capture: capture);

                    break;
                case WorldScreenSource.View view:
                    // The declared jumbotron: resolve its camera name against the world's placeable cameras. An unknown
                    // name is a loud fault (unbound); a known one holds a ViewFeed whose ViewStack registration is
                    // deferred to ConfigureViews (the offscreen render envelope is not known until the frame source has
                    // probed it).
                    if (ResolveCamera(name: view.CameraName) is { } camera) {
                        slot.View = new ViewFeed(name: camera.Name);
                    } else {
                        slot.DeclaredFault = $"camera '{view.CameraName}' not declared";
                    }

                    break;
                case WorldScreenSource.Qr qr:
                    // The declared authorable QR: the matrix is a pure function of (payload, ecLevel, quietZone), so it
                    // is encoded and rasterized ONCE here and merely re-uploaded thereafter (unlike the animated test
                    // pattern, which re-renders every publish). A bad EC letter or an over-capacity payload cannot
                    // reach here — WorldDefinitionValidator refuses both at load — so a fault below can only mean the
                    // encoder disagreed with the validator, which is recorded loudly rather than thrown.
                    if (!TryBuildQrFeed(payload: qr.Payload, ecLevel: qr.EcLevel, quietZoneModules: qr.QuietZoneModules, feed: out var declaredQr, fault: out var qrFault)) {
                        slot.DeclaredFault = qrFault;
                    } else {
                        slot.Qr = declaredQr;
                    }

                    break;
                default:
                    // None: no producer — the provider returns 0 (procedural fallback).
                    break;
            }

            m_slots[screen.Index] = slot;

            // The boot-time, never-replaced cell — m_sources/m_lights register the CELL's own ResolveHandle/
            // ResolveLight, not the slot's, so a later slot recreation only ever needs to re-point Slot below,
            // never touch these dictionaries (or the renderer's already-copied ones) again.
            var cell = new ScreenSourceCell { Slot = slot };

            m_sourceCells[screen.Index] = cell;
            m_sources[screen.Index] = cell.ResolveHandle;
            m_lights[screen.Index] = cell.ResolveLight;
        }
    }

    /// <summary>The screen-source providers keyed by screen index — the map the render spec's <c>ScreenSources</c> field
    /// takes. A provider is present for every declared screen; it returns 0 while the slot carries no producer, which the
    /// engine reads as unbound (the procedural fallback), so a runtime insert binds with no engine rebuild.</summary>
    public IReadOnlyDictionary<int, Func<nint>> ScreenSources => m_sources;

    /// <summary>The screen-light providers keyed by screen index — parallel to <see cref="ScreenSources"/>, the room glow
    /// each slot emits (its framebuffer average, or zero when unbound).</summary>
    public IReadOnlyDictionary<int, Func<Vector3>> ScreenLights => m_lights;

    /// <summary>Drops every device-owned upload and offscreen view while preserving CPU sessions, machine simulation,
    /// declarations, and view registrations. The next publish/render recreates resources on the replacement device.</summary>
    public void NotifyDeviceLost() {
        if (m_disposed) {
            return;
        }

        foreach (var slot in m_slots.Values) {
            m_machines.MachineAt(index: slot.Index)?.NotifyDeviceLost();
            slot.Pattern?.Surface.NotifyDeviceLost();

            if (slot.Qr is { } qr) {
                qr.Surface.NotifyDeviceLost();
                // Published latches "already uploaded to THIS device"; the rendered pixel buffer never depended on the
                // device, so clearing the latch simply re-uploads the same bytes to the replacement.
                qr.Published = false;
            }
        }

        m_cameraFeed?.NotifyDeviceLost();

        foreach (var slot in m_slots.Values) {
            slot.Capture?.NotifyDeviceLost();
        }

        m_viewStack?.NotifyDeviceLost();
    }

    /// <summary>The current same-device image-view handle bound to a screen index, or 0 when the index is unbound, not
    /// declared, or nothing has been published yet — the live state <c>world.screens</c> reports.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>The bound handle, or 0.</returns>
    public nint CurrentHandle(int index) => (m_slots.TryGetValue(key: index, value: out var slot) ? slot.Handle() : 0);

    /// <summary>Whether a machine is currently booted on the screen index — a facade over
    /// <see cref="Server.WorldMachineHost.HasMachine"/> (authoritative server state), reachable through the SAME
    /// reference every existing caller (<c>PlayerCommandModule</c>'s <c>player.engage</c>) already held.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public bool HasMachine(int index) => m_machines.HasMachine(index: index);

    /// <summary>Whether a screen-machine engine is registered under <paramref name="engineId"/> — a facade over
    /// <see cref="Server.WorldMachineHost.HasEngine"/>.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    public bool HasEngine(string engineId) => m_machines.HasEngine(engineId: engineId);

    /// <summary>The live machine on a screen slot as its audio drain seam, or <see langword="null"/> — a facade over
    /// <see cref="Server.WorldMachineHost.AudioMachine"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public IAudioMachine? AudioMachine(int index) => m_machines.AudioMachine(index: index);

    /// <summary>Reads back the live machine insert on a screen index — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadMachineInsert"/>, so <c>world.save</c> can fold a runtime
    /// <c>screen.insert</c> into that screen row's <see cref="WorldScreenSource.Machine"/> source.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="engine">The engine id that booted the live machine.</param>
    /// <param name="contentPath">The content file (a cartridge ROM) the live machine booted.</param>
    /// <param name="options">The options string the live machine booted with, or <see langword="null"/>.</param>
    public bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options) =>
        m_machines.TryReadMachineInsert(index: index, engine: out engine, contentPath: out contentPath, options: out options);

    /// <summary>The screen's live magazine and 0-based selector, or <see langword="false"/> — a facade over
    /// <see cref="Server.WorldMachineHost.TryMagazine"/> (the authoritative selector).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="selected">The live 0-based selector.</param>
    /// <param name="magazine">The screen's magazine.</param>
    public bool TryMagazine(int index, out int selected, out WorldScreenMagazine magazine) =>
        m_machines.TryMagazine(index: index, selected: out selected, magazine: out magazine);

    /// <summary>Applies a NON-machine magazine entry (camera/capture/view/test-pattern/none) as a screen's live
    /// source, through the same dispatch <see cref="ReconcileScreens"/>'s declared-source-change path uses.
    /// <c>ScreenCommandModule.SelectHandler</c> calls this directly, client-side, immediately after submitting the
    /// entry's SELECTOR move through the ordered domain (<c>WorldScreenOp.Select</c>) — the selector is
    /// authoritative server state; applying a non-machine entry's actual producer is genuinely presentation (see
    /// this type's own remarks). A <see cref="WorldScreenSource.Machine"/> entry is refused here — the caller must
    /// route it through <c>WorldScreenOp.Select</c> instead, which <see cref="Server.WorldMachineHost"/> boots
    /// authoritatively.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="source">The non-machine entry to apply.</param>
    /// <returns>Whether the apply succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) ApplyNonMachineSource(int index, WorldScreenSource source) {
        if (source is WorldScreenSource.Machine) {
            return (Ok: false, Message: $"screen {index}: a Machine entry boots through WorldMachineHost, not the binder");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        return ApplySource(index: index, slot: slot, source: source);
    }

    /// <summary>Reconfigures a screen's live machine across the engine's options vocabulary — a facade over
    /// <see cref="Server.WorldMachineHost.TryReconfigure"/>. Presentation calls that need this go through
    /// <c>ScreenCommandModule</c>'s <c>WorldScreenOp.SetOptions</c> submission instead; this facade remains for
    /// symmetry with the type's other read-through members.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The engine-specific options string to retarget to.</param>
    public (bool Ok, string Message) TryReconfigure(int index, string? options) => m_machines.TryReconfigure(index: index, options: options);

    /// <summary>Reads a screen's machine's current options string — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadOptions"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The current options string.</param>
    public bool TryReadOptions(int index, out string options) => m_machines.TryReadOptions(index: index, options: out options);

    /// <summary>The cable link a screen currently belongs to — a facade over <see cref="Server.WorldMachineHost.LinkOf"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public string? LinkOf(int index) => m_machines.LinkOf(index: index);

    /// <summary>Reads a live link's member screens by name — a facade over
    /// <see cref="Server.WorldMachineHost.TryReadLinkMembers"/>.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="members">The member screen indices in cable order, on success.</param>
    public bool TryReadLinkMembers(string name, out IReadOnlyList<int> members) => m_machines.TryReadLinkMembers(name: name, members: out members);

    /// <summary>The live cable-link set as document rows — a facade over
    /// <see cref="Server.WorldMachineHost.CaptureLinks"/>, the <c>world.save</c> fold source.</summary>
    public IReadOnlyList<WorldScreenLink> CaptureLinks() => m_machines.CaptureLinks();

    /// <summary>A one-line description of every live cable link — a facade over
    /// <see cref="Server.WorldMachineHost.DescribeLinks"/>.</summary>
    public string DescribeLinks() => m_machines.DescribeLinks();

    /// <summary>The live state of a declared screen for <c>screen.state</c>, or <see langword="null"/> when the index is
    /// not a declared screen — composed from <see cref="Server.WorldMachineHost.State"/> (machine metadata) plus this
    /// type's own live handle/fault for a machine-owning index, or purely local state (camera/capture/view/pattern)
    /// otherwise.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldScreenState? State(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return null;
        }

        if (m_machines.State(index: index) is { Assigned: true } machineState) {
            return new WorldScreenState(
                Assigned: true,
                Engine: machineState.Engine,
                Handle: m_machines.Handle(index: index),
                FramesStepped: machineState.FramesStepped,
                PendingSteps: machineState.PendingSteps,
                MaximumPendingSteps: machineState.MaximumPendingSteps,
                BackpressureEvents: machineState.BackpressureEvents,
                Fault: machineState.Fault
            );
        }

        return new WorldScreenState(
            Assigned: false,
            Engine: null,
            Handle: slot.Handle(),
            FramesStepped: 0,
            PendingSteps: 0,
            MaximumPendingSteps: 0,
            BackpressureEvents: 0,
            Fault: slot.CurrentFault()
        );
    }

    /// <summary>Reads one memory byte from a screen's machine — a facade over
    /// <see cref="Server.WorldMachineHost.TryPeekMessage"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>A success flag and, on failure, a message; on success the message is empty.</returns>
    public (bool Ok, string Message) TryPeek(int index, int address, out byte value) => m_machines.TryPeekMessage(index: index, address: address, value: out value);

    /// <summary>Binds a declared screen to the shared live webcam feed — the runtime <c>screen.source &lt;index&gt; camera</c> path. Any
    /// existing producer on the slot is cleared first. Fails loudly for an undeclared screen or when no camera device can
    /// be opened.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCamera(int index) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (EnsureCameraFeed(profile: WorldFeedProfile.Default) is not { } feed) {
            return (Ok: false, Message: m_cameraFault);
        }

        slot.ClearLive();
        slot.Camera = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing the webcam");
    }

    /// <summary>Binds a declared screen to a live desktop-window capture keyed by a title fragment — the runtime
    /// <c>screen.source &lt;index&gt; capture</c> path. Any existing producer on the slot is cleared first. The capture rebinds each grab, so
    /// the target window need not be open yet (it reads no signal until it appears, and rebinds if it disappears and
    /// returns); only an unopenable capture service fails here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="windowTitle">The captured window's title fragment (case-insensitive substring match).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryCapture(int index, string windowTitle) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryOpenCapture(title: windowTitle, profile: WorldFeedProfile.Default, feed: out var feed, fault: out var fault)) {
            return (Ok: false, Message: fault);
        }

        slot.ClearLive();
        slot.Capture = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} capturing '{windowTitle}'");
    }

    /// <summary>Binds a declared screen to a live whole-monitor capture keyed by index — the runtime <c>screen.source &lt;index&gt; desktop</c>
    /// path. Any existing producer on the slot is cleared first. The capture rebinds each grab, so it reads no signal
    /// until the monitor is present and reacquires if it disconnects and returns; an out-of-range index or an unopenable
    /// capture service fails here.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="monitorIndex">The 0-based monitor to capture whole (0 = primary).</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryDesktop(int index, int monitorIndex) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryOpenMonitorCapture(monitorIndex: monitorIndex, profile: WorldFeedProfile.Default, feed: out var feed, fault: out var fault)) {
            return (Ok: false, Message: fault);
        }

        slot.ClearLive();
        slot.Capture = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} capturing monitor {monitorIndex}");
    }

    /// <summary>Clears a screen's live LOCAL producer — the runtime <c>screen.eject</c> path — for either kind this
    /// binder itself owns (the webcam, a window capture). Ejecting a MACHINE is <c>ScreenCommandModule</c>'s
    /// <c>WorldScreenOp.Eject</c> submission instead (see this type's own remarks). The slot reverts to its declared
    /// test pattern or to unbound (the procedural fallback). Fails for an undeclared screen or a slot with no live
    /// local producer to clear.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>Whether the eject succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryEject(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!slot.HasLive) {
            return (Ok: false, Message: $"screen {index} has no source to eject");
        }

        slot.ClearLive();

        return (Ok: true, Message: $"screen {index} ejected");
    }


    /// <summary>Reconciles the binder's runtime source machinery to a mutated screen list — the live-application half of
    /// an <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called by the frame source when the definition
    /// revision moves. REMOVALS are reconciled first: a slot whose index is no longer declared has any engaged player
    /// disengaged (their avatar resumes normal intent), its OWNED machine/pattern/capture state disposed, and its
    /// entries dropped from <c>m_slots</c>/<c>m_sources</c>/<c>m_lights</c> — so a removed screen stops advancing,
    /// publishing, and answering screen commands (the shared webcam session and the boot-sized view POOL are NOT
    /// disposed here — the binder owns their lifetime). A removed <c>View</c> screen additionally releases its camera's
    /// offscreen render when no surviving slot still films that camera (the orphaned <see cref="ViewStack"/> entry
    /// is disposed so it stops consuming refresh budget), while a camera two jumbotrons share stays live for the
    /// survivor. Then, for a declared index whose source CHANGED, it re-applies
    /// the new source through the same insert/eject/camera/capture/view machinery a <c>screen.*</c> verb uses
    /// (best-effort — a failed bind logs a loud line, never throws). Screen SLAB geometry (adds/moves/removes) rides the
    /// program rebuild in the frame source, not this method. Capacity honesty, precisely: an index that was declared
    /// at BOOT (<see cref="m_bootScreenIndices"/>) always gets its slot/provider entries RECREATED on re-declaration
    /// after a removal — the render engine's frozen key list still names it, so this is safe. A GENUINELY new
    /// index (never in the boot set) still cannot bind live — its slab renders the
    /// procedural fallback until the next boot, since the render engine's provider key set cannot grow.</summary>
    /// <param name="screens">The mutated screen list (the live definition's screens).</param>
    public void ReconcileScreens(IReadOnlyList<WorldScreen> screens) {
        if (m_disposed) {
            return;
        }

        // Removal pass FIRST: collect every slot whose index vanished from the incoming set (never mutating m_slots mid
        // -enumeration), then disengage + dispose + drop each. The incoming screen list is tiny, so the containment
        // scan stays allocation-free (no set built per reconcile).
        m_reconcileRemovals.Clear();

        foreach (var index in m_slots.Keys) {
            if (!DeclaresIndex(screens: screens, index: index)) {
                m_reconcileRemovals.Add(item: index);
            }
        }

        // Camera names a removed View screen referenced — collected during the removal pass, reconciled after it so a
        // camera view no remaining slot references is released. Null (the common case) when no View screen was
        // removed, so a plain screen removal allocates nothing.
        HashSet<string>? removedViewCameras = null;

        foreach (var index in m_reconcileRemovals) {
            // Machine disposal and its own admin cleanup (engagement disengage, link teardown) happen server-side
            // now, inside WorldServer.Install (Server.WorldMachineHost.ReconcileScreens + WorldEngagement.
            // DisengageScreen) — see this type's own remarks. This pass only drops the presentation-side slot.
            if (m_slots.Remove(key: index, value: out var slot)) {
                // Note the camera a removed View screen filmed BEFORE DisposeOwned drops the reference, so its offscreen
                // render can be released once the whole removal pass has updated m_slots (a name shared by another
                // surviving jumbotron must NOT be released).
                if (slot.View is { } view) {
                    (removedViewCameras ??= new HashSet<string>(comparer: StringComparer.Ordinal)).Add(item: view.Name);
                }

                slot.DisposeOwned();
            }

            // These two removals are this binder's OWN bookkeeping only — SdfEngineNode copied its screen-source
            // dictionaries once, at boot, and never reads m_sources/m_lights again, so dropping an entry here has no
            // renderer-visible effect either way. m_sourceCells is deliberately NEVER
            // touched here: a boot index's cell must survive removal so a later re-declare can re-point Slot (below).
            _ = m_sources.Remove(key: index);
            _ = m_lights.Remove(key: index);
            Console.Error.WriteLine(value: $"[world.screen: {index} removed — slot disposed]");
        }

        if (removedViewCameras is not null) {
            ReleaseOrphanedCameraViews(candidates: removedViewCameras);
        }

        foreach (var screen in screens) {
            if (m_slots.TryGetValue(key: screen.Index, value: out var slot) is false) {
                if (!m_bootScreenIndices.Contains(item: screen.Index)) {
                    Console.Error.WriteLine(value: $"[world.screen: {screen.Index} added — its source applies at next boot (render provider key set frozen at boot)]");

                    continue;
                }

                // A boot-declared index that was REMOVED (a prior RemoveScreen mutation's removal pass, above) and
                // is now re-declared (a later UpsertScreen, or a whole-document rebuild whose definition still names
                // it) — recreate the slot. Safe: the render engine's frozen key list still names this index (it was
                // present at boot). DeclaredSource starts null so the Equals check below never short-circuits a
                // fresh slot. Do NOT write a new delegate into m_sources/m_lights here — SdfEngineNode copied THOSE
                // dictionaries' delegates ONCE at boot and never re-reads this binder's dictionaries again, so a
                // fresh `slot.Handle`/`slot.Light` delegate assigned post-boot is never seen and the renderer keeps
                // polling the OLD, disposed slot forever (reads as the procedural fallback). Instead, re-point the
                // boot-time cell's Slot field — the SAME cell.ResolveHandle/ResolveLight delegate identity the
                // renderer already copied reads through it, so this re-point is visible on the very next poll with
                // no renderer involvement at all.
                slot = new ScreenSlot { DeclaredSource = null, Index = screen.Index, Machines = m_machines };
                m_slots[screen.Index] = slot;

                if (m_sourceCells.TryGetValue(key: screen.Index, value: out var cell)) {
                    cell.Slot = slot;
                } else {
                    // Should not happen for a boot index (its cell is created once, at construction, and never
                    // removed) — guarded defensively rather than assumed, since a first poll before this branch
                    // would otherwise throw a KeyNotFoundException reading m_sources/m_lights.
                    cell = new ScreenSourceCell { Slot = slot };
                    m_sourceCells[screen.Index] = cell;
                    m_sources[screen.Index] = cell.ResolveHandle;
                    m_lights[screen.Index] = cell.ResolveLight;
                }
            }

            // The magazine and its live selector are authoritative server state now (Server.WorldMachineHost owns
            // both) — nothing to refresh here.
            if (Equals(objA: slot.DeclaredSource, objB: screen.Source)) {
                continue;
            }

            ApplySourceChange(index: screen.Index, slot: slot, source: screen.Source);
            slot.DeclaredSource = screen.Source;
        }
    }

    // Whether the incoming screen list still declares a slot index — a linear scan over the tiny screen list (a handful
    // of rows), so the removal pass needs no per-call HashSet allocation.
    private static bool DeclaresIndex(IReadOnlyList<WorldScreen> screens, int index) {
        foreach (var screen in screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }

    // Apply one screen's changed source through the runtime machinery and narrate it. The releases the change implies
    // live in ApplySource itself, so every caller gets them.
    private void ApplySourceChange(int index, ScreenSlot slot, WorldScreenSource source) {
        var outcome = ApplySource(index: index, slot: slot, source: source);

        Console.Error.WriteLine(value: $"[world.screen: {outcome.Message}]");
    }

    // Apply one NON-MACHINE source through the runtime machinery (the switch that maps a WorldScreenSource to
    // TryCamera/TryDesktop/TryCapture/TryView/TryEject) — shared by the reconcile-side declared-source change and
    // ApplyNonMachineSource (screen.select's non-machine branch). The caller decides what to do with DeclaredSource.
    // A Machine source drops any local presentation producer (the declared source no longer names one —
    // Server.WorldMachineHost owns the machine itself, already reconciled server-side by the time this runs) but is
    // otherwise the switch's ONE no-op arm.
    //
    // The two RELEASES belong here rather than in one caller, because they are not that caller's bookkeeping: they are
    // what "this slot now shows something else" MEANS. Every transition away from View clears the slot's jumbotron
    // reference and releases the camera registration when no surviving slot films it (TryEject/ClearLive deliberately
    // keep the DECLARED view for the eject verb, so the source change must drop it); a View→View re-point releases the
    // superseded camera inside TryView. The QR release is symmetric — a slot that no longer names a QR drops the
    // rasterized one. Left in ApplySourceChange, screen.select reached the switch and skipped both, so selecting a
    // test-pattern or none entry kept rendering the QR the slot had before (QR outranks Pattern in the slot's own
    // precedence), with screen.source <index> qr still reading the code back.
    private (bool Ok, string Message) ApplySource(int index, ScreenSlot slot, WorldScreenSource source) {
        var outcome = source switch {
            WorldScreenSource.None => (slot.HasLive ? TryEject(index: index) : (Ok: true, Message: $"screen {index} unbound")),
            WorldScreenSource.Machine => (slot.HasLive ? TryEject(index: index) : (Ok: true, Message: $"screen {index} machine (host-owned)")),
            WorldScreenSource.Camera => TryCamera(index: index),
            WorldScreenSource.Capture { MonitorIndex: { } monitorIndex } => TryDesktop(index: index, monitorIndex: monitorIndex),
            WorldScreenSource.Capture capture => TryCapture(index: index, windowTitle: capture.WindowTitle),
            WorldScreenSource.View view => ApplyViewChange(index: index, slot: slot, view: view),
            WorldScreenSource.Qr qr => TryQr(index: index, payload: qr.Payload, ecLevel: qr.EcLevel, quietZoneModules: qr.QuietZoneModules),
            _ => (Ok: false, Message: $"screen {index} source applies at next boot"),
        };

        if (source is not WorldScreenSource.View) {
            ReleaseSlotView(slot: slot);
        }

        if (source is not WorldScreenSource.Qr) {
            slot.ReleaseQr();
        }

        return outcome;
    }

    // The reconcile-side View bind: a failed bind (unknown camera, unconfigured pool) still releases the PRIOR view —
    // the declared source no longer names it — and records the fault so screen.state reads honestly.
    private (bool Ok, string Message) ApplyViewChange(int index, ScreenSlot slot, WorldScreenSource.View view) {
        var outcome = TryView(index: index, cameraName: view.CameraName);

        if (!outcome.Ok) {
            ReleaseSlotView(slot: slot);
            slot.DeclaredFault = outcome.Message;
        }

        return outcome;
    }

    // Drops a slot's jumbotron view reference and releases (or re-narrows) its camera registration — the symmetric
    // half of TryView's acquire, run whenever the slot stops filming that camera.
    private void ReleaseSlotView(ScreenSlot slot) {
        if (slot.View is not { } view) {
            return;
        }

        slot.View = null;
        ReleaseOrphanedCameraView(name: view.Name);
    }

    /// <summary>Publishes every CPU-fed screen for this produced frame. Deterministic machines have already advanced
    /// server-side, inside <c>WorldServer.Step</c> (<c>Server.WorldMachineHost.Advance</c>); this seam only uploads
    /// their latest framebuffer (the one GPU call this project makes on a machine's behalf) and services
    /// presentation-only camera/window captures on source-owned cadences.</summary>
    /// <param name="tick">The world's completed-step ordinal driving deterministic pattern animation.</param>
    /// <param name="elapsedTicks">The exact completed simulation time in engine ticks, used by feed deadlines.</param>
    /// <param name="deviceContext">The live GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    public void Publish(ulong tick, ulong elapsedTicks, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (m_disposed) {
            return;
        }

        // Resolve the render adapter LUID once — the device is created lazily, so the value is first available here (not
        // at construction). Capture feeds then open their platform capture on the render GPU so the shared textures import.
        if (m_hostsOnDirectX && (m_renderAdapterLuid is null) && OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240) && (deviceContext is IDirectXDeviceContext renderDeviceContext)) {
            m_renderAdapterLuid = renderDeviceContext.AdapterLuid;
        }

        var timingEnabled = GpuTimingControl.Shared.Armed;
        var phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

        // The shared webcam owns one producer cadence and skips uploads when its asynchronous frame version has not
        // advanced. Window captures below each own an independent deadline from their declaration.
        CaptureCamera(elapsedTicks: elapsedTicks, deviceContext: deviceContext, gpu: gpu);
        var cameraTicks = (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);
        var machineTicks = 0L;
        var windowCaptureTicks = 0L;
        var patternTicks = 0L;

        foreach (var slot in m_slots.Values) {
            if (m_machines.MachineAt(index: slot.Index) is { } machine) {
                phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
                machine.PublishFrame(deviceContext: deviceContext, gpu: gpu);
                machineTicks += (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

                continue;
            }

            // The shared webcam is published once (in CaptureCamera above), so a camera screen only rides that feed.
            if (slot.Camera is not null) {
                continue;
            }

            if (slot.Capture is { } capture) {
                if (capture.ShouldPull(elapsedTicks: elapsedTicks)) {
                    phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
                    CaptureWindow(feed: capture, deviceContext: deviceContext, gpu: gpu);
                    windowCaptureTicks += (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);
                }

                continue;
            }

            if (slot.Pattern is { } pattern) {
                phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);
                var pixels = pattern.Pattern.Render(tick: tick);

                _ = pattern.Surface.Publish(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    pixels: pixels,
                    width: (uint)pattern.Pattern.Width,
                    height: (uint)pattern.Pattern.Height,
                    format: TestPatternSource.PixelFormat
                );

                pattern.Light = AverageColor(pixels: pixels.Span);
                patternTicks += (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);

                continue;
            }

            // A QR matrix is a pure function of its payload/level/quiet zone — never the tick — so it uploads exactly
            // ONCE (the first produced frame after boot, a live screen.source <index> qr, or a device loss) instead of re-copying an
            // unchanged buffer to the GPU every frame.
            if (slot.Qr is { Published: false } qrFeed) {
                phaseStart = (timingEnabled ? Stopwatch.GetTimestamp() : 0L);

                _ = qrFeed.Surface.Publish(
                    deviceContext: deviceContext,
                    gpu: gpu,
                    pixels: qrFeed.Pixels,
                    width: qrFeed.Width,
                    height: qrFeed.Height,
                    format: TestPatternSource.PixelFormat
                );

                qrFeed.Published = true;
                patternTicks += (timingEnabled ? (Stopwatch.GetTimestamp() - phaseStart) : 0L);
            }
        }

        if (timingEnabled) {
            ++m_publishTimingFrame;
            ReportPublishTiming(sample: new ScreenPublishTiming(
                CameraTicks: cameraTicks,
                MachineTicks: machineTicks,
                WindowCaptureTicks: windowCaptureTicks,
                PatternTicks: patternTicks
            ));
        }
    }

    /// <summary>Stands up the offscreen view pool backing every declared View (jumbotron) screen — called once by the
    /// render factory AFTER the frame source has probed the render envelope (the worst-case program/instance/transform
    /// capacities every offscreen view render must fit). Registers one persistent <see cref="SdfCameraView"/> per
    /// referenced camera, posed by either its declared <see cref="FixedRig"/> or an avatar-anchored
    /// <see cref="FirstPersonRig"/>, and records each view's
    /// self-reference screen set (a screen wired to view V binds 0 inside V's own render — no feedback compounding).
    /// A no-op when the world declares no View screen (no pool is created, so a plain world pays nothing).</summary>
    /// <param name="services">The concrete GPU-services closure (<see cref="SdfViewGpuServices"/>) every offscreen
    /// camera view this binder later constructs forwards to its engine — resolved ONCE, eagerly, at the composition
    /// root and stashed here unchanged (never a retained <see cref="IServiceProvider"/> to re-resolve from later;
    /// see <see cref="RegisterCameraView"/>, this binder's one construction site).</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the offscreen kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public void ConfigureViews(SdfViewGpuServices services, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity) {
        ArgumentNullException.ThrowIfNull(argument: services);

        m_viewServices = services;
        m_viewHostsOnDirectX = hostsOnDirectX;
        m_viewProgramWordCapacity = programWordCapacity;
        m_viewInstanceCapacity = instanceCapacity;
        m_viewDynamicTransformCapacity = dynamicTransformCapacity;

        // The screen indices wired to each referenced camera name (a name shared by two jumbotrons self-references both).
        var wiredByName = new Dictionary<string, HashSet<int>>(comparer: StringComparer.Ordinal);

        foreach (var slot in m_slots.Values) {
            if ((slot.View is { } view) && (ResolveCamera(name: view.Name) is { } camera)) {
                RegisterCameraView(camera: camera);
                view.Stack = m_viewStack;
                _ = (wiredByName.TryGetValue(key: camera.Name, value: out var indices) ? indices : (wiredByName[camera.Name] = new HashSet<int>())).Add(item: slot.Index);
            }
        }

        if (m_viewStack is { } stack) {
            foreach (var (name, indices) in wiredByName) {
                stack.SetWiredScreens(name: name, screenIndices: indices);
            }
        }
    }

    /// <summary>Renders this frame's jumbotron views against the live device — called from the frame source's
    /// <see cref="ISdfFrameSource.RenderViews"/> seam AFTER the CPU-fed screens have published and BEFORE the engine polls
    /// the source providers, so a View screen's provider returns a handle to THIS frame's offscreen render. Each view's
    /// own render sees every OTHER screen surface as the room shows it (a jumbotron films the lit test pattern / booted
    /// machine beside it) and its OWN face as unbound (the self-reference rule). A no-op with no view pool.</summary>
    /// <param name="context">This frame's host frame context (resolves the offscreen device).</param>
    /// <param name="program">This frame's composed world program (the same instance the main engine renders).</param>
    /// <param name="revision">The program's revision counter — each offscreen engine re-uploads only when it advances.</param>
    /// <param name="transforms">This frame's packed dynamic transforms, identical to the main engine's.</param>
    /// <param name="time">The frame's content clock (seconds) — the views render the same animated world the room does.</param>
    /// <param name="authoritativeTick">The latest authoritative simulation tick available to presentation.</param>
    public void RenderViews(in FrameContext context, SdfProgram program, int revision, IReadOnlyList<DynamicTransform> transforms, float time, ulong authoritativeTick) {
        if (m_disposed || (m_viewStack is not { } stack)) {
            return;
        }

        m_viewTransforms = transforms;

        if (m_viewRefreshCountdown > 0) {
            m_viewRefreshCountdown--;

            return;
        }

        m_viewRefreshCountdown = (m_viewRefreshDivisor - 1);

        stack.RenderFrame(context: new ViewRenderContext(
            Host: context,
            Program: program,
            ProgramRevision: revision,
            Time: time,
            AuthoritativeTick: authoritativeTick,
            DynamicTransforms: transforms,
            // What each screen surface binds INSIDE a jumbotron's render: the same handle the room shows (the ViewStack
            // zeroes the view's own wired screens per the self-reference rule, so this need not).
            ResolveScreenSource: CurrentHandle
        ));
    }

    /// <summary>Sets the deterministic jumbotron refresh divisor. One renders every produced frame; larger values keep
    /// the last resolved image between refreshes, using <see cref="ViewStack"/>'s existing persistent-handle contract.</summary>
    /// <param name="divisor">Produced frames per offscreen refresh, from 1 through 8.</param>
    public void SetViewRefreshDivisor(int divisor) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: divisor, other: 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: divisor, other: 8);

        m_viewRefreshDivisor = divisor;
        m_viewRefreshCountdown = 0;
    }

    /// <summary>The current produced-frame divisor for jumbotron offscreen renders.</summary>
    public int ViewRefreshDivisor => m_viewRefreshDivisor;

    /// <summary>How many camera views are registered in the offscreen view pool right now — each one is a live
    /// <see cref="SdfCameraView"/> spending refresh budget. Zero when no View screen is declared (no pool) or the pool
    /// has not been configured yet. Removing the last screen wired to a camera releases its view, so this count drops
    /// (the pipe-observable witness that a removed View screen's offscreen render stopped).</summary>
    public int ActiveCameraViewCount => (m_viewStack?.ActiveViewCount ?? 0);

    /// <summary>Points a declared screen at a placeable camera — the runtime <c>screen.source &lt;index&gt; view</c> path. Any existing
    /// producer on the slot is cleared first. Requires the view pool to have been configured (it is, at startup); fails
    /// loudly for an undeclared screen, an unknown camera name, or an unconfigured pool.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="cameraName">The placeable camera to film from.</param>
    /// <returns>Whether the bind succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryView(int index, string cameraName) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (m_viewServices is null) {
            return (Ok: false, Message: "the view pool is not configured");
        }

        if (ResolveCamera(name: cameraName) is not { } camera) {
            return (Ok: false, Message: $"camera '{cameraName}' not declared");
        }

        var previousView = slot.View;

        RegisterCameraView(camera: camera);
        slot.ClearLive();
        slot.View = new ViewFeed(name: camera.Name) { Stack = m_viewStack };
        slot.DeclaredFault = null;
        m_viewStack!.SetWiredScreens(name: camera.Name, screenIndices: WiredScreensFor(name: camera.Name));

        // A re-point away from another camera releases (or re-narrows) the superseded registration AFTER the new bind,
        // so a view no slot films stops rendering (the View A → View B case).
        if ((previousView is { } previous) && !string.Equals(a: previous.Name, b: camera.Name, comparisonType: StringComparison.Ordinal)) {
            ReleaseOrphanedCameraView(name: previous.Name);
        }

        return (Ok: true, Message: $"screen {index} showing camera '{camera.Name}'");
    }

    /// <summary>Authors (or re-authors) a declared screen's QR code — the runtime <c>screen.source &lt;index&gt; qr</c> path, the live twin
    /// of a declared <see cref="WorldScreenSource.Qr"/> row. The payload is encoded and rasterized ONCE, right here, so
    /// the per-frame cost of the resulting screen is a single unchanged-buffer upload and then nothing at all. Any live
    /// producer on the slot (the webcam, a window capture) and any jumbotron view are cleared first, exactly as
    /// <c>screen.source &lt;index&gt; camera</c>/<c>view</c> clear each other, so the freshly authored code is what the screen shows
    /// next publish. Fails loudly — never throws — for an undeclared screen, an unrecognized EC-level letter, a
    /// negative quiet zone, or a payload too large for the encoder's supported version range (refused BY NAME, never
    /// truncated).</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="payload">The payload string to encode, UTF-8 byte mode.</param>
    /// <param name="ecLevel">The error-correction level letter (<c>L</c>/<c>M</c>/<c>Q</c>/<c>H</c>, case-insensitive),
    /// or <see langword="null"/> for the document default (<c>M</c>).</param>
    /// <param name="quietZoneModules">The quiet-zone width in modules, or <see langword="null"/> for the document
    /// default (4).</param>
    /// <returns>Whether the author succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryQr(int index, string payload, string? ecLevel, int? quietZoneModules) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryBuildQrFeed(payload: payload, ecLevel: (ecLevel ?? QrErrorCorrection.Letter(level: QrErrorCorrection.Default)), quietZoneModules: (quietZoneModules ?? QrDefaultQuietZoneModules), feed: out var feed, fault: out var fault)) {
            return (Ok: false, Message: fault!);
        }

        slot.ClearLive();
        ReleaseSlotView(slot: slot);
        slot.ReleaseQr();
        slot.Qr = feed;
        slot.DeclaredFault = null;

        return (Ok: true, Message: $"screen {index} showing QR v{feed!.Version} {QrErrorCorrection.Letter(level: feed.Level)} mask{feed.Mask} {feed.Width}x{feed.Height} '{ElideForEcho(payload: feed.Payload)}'");
    }

    /// <summary>Reads back a screen's QR authoring — the <c>screen.source &lt;index&gt; qr</c> query (no payload argument) that makes the
    /// decision its setter made pipe-assertable: the payload, level, encoder-chosen version and mask, quiet zone, and
    /// rendered pixel extent. Fails when the screen carries no QR (nothing authored, or the declared source is
    /// something else).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="authoring">The screen's QR authoring, on success; <see langword="default"/> otherwise.</param>
    /// <returns>Whether the screen carries a QR.</returns>
    public bool TryReadQr(int index, out WorldScreenQrAuthoring authoring) {
        if (m_slots.TryGetValue(key: index, value: out var slot) && (slot.Qr is { } qr)) {
            authoring = new WorldScreenQrAuthoring(
                Payload: qr.Payload,
                Level: qr.Level,
                Version: qr.Version,
                Mask: qr.Mask,
                QuietZoneModules: qr.QuietZoneModules,
                Width: qr.Width,
                Height: qr.Height
            );

            return true;
        }

        authoring = default;

        return false;
    }

    // Parses the level letter, encodes the payload, and rasterizes the matrix into a fresh CPU upload surface — the
    // ONE construction path both the declared row and the live verb take, so their refusals read identically. The
    // module pixel size targets a comfortable on-screen resolution whatever version the payload chose: big enough for
    // a scanner to resolve the modules, small enough to stay well under the validator's surface-dimension ceiling.
    private static bool TryBuildQrFeed(string payload, string? ecLevel, int quietZoneModules, out QrFeed? feed, out string? fault) {
        feed = null;

        if (!QrErrorCorrection.TryParse(text: ecLevel, level: out var level)) {
            fault = $"ecLevel '{ecLevel}' must be one of {QrErrorCorrection.Vocabulary}";

            return false;
        }

        if (quietZoneModules < 0) {
            fault = $"quietZoneModules {quietZoneModules} must be non-negative";

            return false;
        }

        if (!QrEncoder.TryEncode(payload: payload, level: level, matrix: out var matrix, error: out fault) || (matrix is null)) {
            return false;
        }

        var totalModules = (matrix.Size + (2 * quietZoneModules));
        var modulePixels = Math.Clamp(value: (QrTargetPixelExtent / totalModules), min: QrMinModulePixels, max: QrMaxModulePixels);
        var pixels = matrix.RenderPixels(modulePixels: modulePixels, quietZoneModules: quietZoneModules, width: out var width, height: out var height);

        feed = new QrFeed(
            pixels: pixels,
            width: (uint)width,
            height: (uint)height,
            surface: new CpuSurfaceSource(),
            payload: payload,
            level: level,
            version: matrix.Version,
            mask: matrix.MaskPattern,
            quietZoneModules: quietZoneModules,
            light: AverageColor(pixels: pixels)
        );
        fault = null;

        return true;
    }

    // A one-line preview for a QR echo — a link payload runs to hundreds of characters, which would otherwise flood the
    // console mirror's 64-line ring.
    private static string ElideForEcho(string payload) {
        const int maxLength = 48;

        return ((payload.Length <= maxLength) ? payload : $"{payload[..maxLength]}…");
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // No machine/link disposal here — Server.WorldMachineHost (a peer DI singleton, container-disposed
        // separately) owns that lifetime now.
        foreach (var slot in m_slots.Values) {
            slot.Pattern?.Surface.Dispose();
            slot.Qr?.Surface.Dispose();
            slot.Capture?.Dispose();
        }

        m_cameraFeed?.Dispose();
        m_cameraFeed = null;
        m_viewStack?.Dispose();
        m_viewStack = null;
    }

    // Resolves a placeable-camera name against the world's declared cameras (ordinal), or null when none matches.
    private WorldCamera? ResolveCamera(string name) {
        foreach (var camera in m_cameras) {
            if (string.Equals(a: camera.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return camera;
            }
        }

        return null;
    }

    // One physical default-camera session is shared to avoid device flicker. When several camera sources declare
    // different preferences, request the richest combined envelope rather than letting declaration order choose.
    private static WorldFeedProfile ResolveSharedCameraProfile(IReadOnlyList<WorldScreen> screens) {
        var profile = WorldFeedProfile.Default;
        var found = false;

        foreach (var screen in screens) {
            if (screen.Source is not WorldScreenSource.Camera camera) {
                continue;
            }

            profile = (found
                ? new WorldFeedProfile(
                    Width: Math.Max(val1: profile.Width, val2: camera.Profile.Width),
                    Height: Math.Max(val1: profile.Height, val2: camera.Profile.Height),
                    RefreshRateHz: Math.Max(val1: profile.RefreshRateHz, val2: camera.Profile.RefreshRateHz)
                )
                : camera.Profile);
            found = true;
        }

        return profile;
    }

    // Creates the view pool on first need and registers (or updates in place, idempotent per name) one persistent
    // SdfCameraView for a camera. Fixed cameras carry their own world-space look-at; anchored cameras resolve their
    // WorldAnchor's entity each frame and pose a FirstPersonRig at the resolved anchor-local offset. A camera FILMS
    // an already-lit world, so it is a budgeted offscreen render with no room glow of its own.
    private void RegisterCameraView(WorldCamera camera) {
        m_viewStack ??= new ViewStack();

        if (!m_cameraViews.TryGetValue(key: camera.Name, value: out var registration)) {
            var view = new SdfCameraView(
                services: m_viewServices!,
                hostsOnDirectX: m_viewHostsOnDirectX,
                programWordCapacity: m_viewProgramWordCapacity,
                instanceCapacity: m_viewInstanceCapacity,
                dynamicTransformCapacity: m_viewDynamicTransformCapacity,
                width: camera.RenderWidth,
                height: camera.RenderHeight
            ) {
                // The result is sampled by a 160x144 diegetic panel. Re-marching full soft shadows and AO here cost
                // almost as much as the main view's lighting despite contributing only a tiny screen-space image.
                DisableAmbientOcclusion = true,
                DisableSoftShadows = true,
            };

            ConfigureCameraView(view: view, camera: camera);

            registration = new CameraRegistration { Row = camera, View = view };
            m_cameraViews[camera.Name] = registration;
        }

        _ = m_viewStack.Register(name: camera.Name, content: registration.View, band: ScreenSlotPriority.Ambient);
    }

    /// <summary>Reconciles the live camera-view machinery to a mutated camera list — the live-application half of an
    /// <c>UpsertCamera</c>/<c>RemoveCamera</c> world mutation, called by the frame source when the definition revision
    /// moves (BEFORE <see cref="ReconcileScreens"/>, so a same-delivery View source change resolves the new rows). The
    /// stored row list is REPLACED (later resolves read live data); then, for each camera with a REGISTERED offscreen
    /// view: a pose/aim/FOV edit of the same kind writes the live rig's properties in place (the offscreen engine and
    /// its budget entry survive), a dimension or kind change releases and recreates the view (an offscreen render
    /// target cannot resize), and a removed row releases the view and unbinds every slot that filmed it. A declared
    /// View slot that faulted at boot (its camera did not exist yet) self-heals when the camera row arrives. Bounded by
    /// <see cref="ViewStack.MaxRegisteredViews"/> and the refresh-divisor budget; dimensions are validator-capped.</summary>
    /// <param name="cameras">The mutated camera list (the live definition's cameras).</param>
    public void ReconcileCameras(IReadOnlyList<WorldCamera> cameras) {
        if (m_disposed) {
            return;
        }

        m_cameras = cameras;

        // Walk a snapshot of the registered names (the release/recreate paths mutate m_cameraViews).
        m_cameraReconcileScratch.Clear();
        m_cameraReconcileScratch.AddRange(collection: m_cameraViews.Keys);

        foreach (var name in m_cameraReconcileScratch) {
            var registration = m_cameraViews[name];

            if (ResolveCamera(name: name) is not { } next) {
                ReleaseCameraRow(name: name);

                continue;
            }

            if (Equals(objA: next, objB: registration.Row)) {
                continue;
            }

            if ((next.RenderWidth != registration.Row.RenderWidth) ||
                (next.RenderHeight != registration.Row.RenderHeight)) {
                // The offscreen render target is sized (and the rig shaped) at construction: release the registration
                // (ViewStack.Release disposes the SdfCameraView and its engine) and rebuild fresh from the new row,
                // re-narrowing the survivors' self-reference set.
                m_viewStack?.Release(name: name);
                _ = m_cameraViews.Remove(key: name);
                RegisterCameraView(camera: next);
                m_viewStack?.SetWiredScreens(name: name, screenIndices: WiredScreensFor(name: name));
                Console.Error.WriteLine(value: $"[world.camera: '{name}' recreated live ({next.RenderWidth}x{next.RenderHeight})]");
            } else {
                ApplyCameraPose(registration: registration, camera: next);
                Console.Error.WriteLine(value: $"[world.camera: '{name}' pose updated live]");
            }
        }

        // Self-heal: a declared View slot left faulted (its camera name was undeclared at bind time) binds now that
        // the row exists — the same TryView machinery a screen.source <index> view runs. A live runtime producer (an inserted
        // machine overlaying the declared view) is never displaced.
        foreach (var slot in m_slots.Values) {
            if ((slot.View is null) &&
                !slot.HasLive &&
                (slot.DeclaredSource is WorldScreenSource.View declared) &&
                (ResolveCamera(name: declared.CameraName) is not null) &&
                (m_viewServices is not null)) {
                var outcome = TryView(index: slot.Index, cameraName: declared.CameraName);

                Console.Error.WriteLine(value: $"[world.camera: {outcome.Message}]");
            }
        }
    }

    // A same-dimensions pose/aim/FOV/rig/anchor edit re-wires the LIVE view in place (a freshly compiled rig plus its
    // anchor sources) — the offscreen engine, its ViewStack budget entry, and every wired slot survive untouched. The
    // registration's row snapshot advances so the next reconcile diffs against what the view now embodies.
    private void ApplyCameraPose(CameraRegistration registration, WorldCamera camera) {
        ConfigureCameraView(view: registration.View, camera: camera);

        registration.Row = camera;
    }

    // Compiles the camera axes and wires their reference-frame source.
    private void ConfigureCameraView(SdfCameraView view, WorldCamera camera) {
        var referenceOffset = Vector3.Zero;

        switch (camera.Anchor) {
            case null:
                view.AnchorSource = null;
                view.AnchorIdSource = null;

                break;
            case WorldAnchor.Entity entity:
                view.AnchorSource = m_anchors;
                view.AnchorIdSource = () => entity.Index;

                break;
            case WorldAnchor.EntityPart part:
                view.AnchorSource = new EntityPartAnchorSource(owner: this, part: part);
                view.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Placement placement:
                view.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(Position: StaticAnchorPosition(placement: placement), Orientation: Quaternion.Identity));
                view.AnchorIdSource = static () => 0;

                break;
            case WorldAnchor.Group group:
                view.AnchorSource = new FixedAnchorSource(anchor: new SdfAnchor(Position: GroupCentroid(group: group), Orientation: Quaternion.Identity));
                view.AnchorIdSource = static () => 0;

                break;
        }

        view.Rig = WorldCameraRigCompiler.Compile(rig: camera.Rig, referenceOffset: referenceOffset);
    }

    // The stamped world position of a placement anchor (the same WorldAnchorGeometry math speakers read) — needs the live
    // definition, which the anchor source carries in practice (the client).
    private Vector3 StaticAnchorPosition(WorldAnchor.Placement placement) =>
        ((m_anchors is WorldClient client) ? WorldAnchorGeometry.StaticPlacementPosition(definition: client.Definition, placementId: placement.PlacementId, shapeId: placement.ShapeId) : Vector3.Zero);

    // The one-shot centroid of a group anchor. A filmed/offscreen view bakes only this raw centroid: it DROPS the group
    // Chase.SpreadPullback widening entirely (not merely its per-frame smoothing), so an establishing shot filmed onto a
    // diegetic screen frames the centroid without widening for the group's spread. The main-window composer applies and
    // smooths the spread; documented so authors don't expect spread-widening on a filmed establishing shot.
    private Vector3 GroupCentroid(WorldAnchor.Group group) =>
        ((m_anchors is WorldClient client) ? WorldGroupAnchors.ComputeRaw(group: group, client: client, maxPopulation: WorldClient.EntityCapacity).Centroid : Vector3.Zero);
    private bool TryResolveEntityPart(WorldAnchor.EntityPart part, out SdfAnchor anchor) {
        if (m_anchors is WorldClient client) {
            return WorldEntityPartResolver.TryPackedPose(client: client, stamps: m_stamps, entityIndex: part.Index, partId: part.PartId, transforms: m_viewTransforms, pose: out anchor);
        }

        anchor = default;

        return false;
    }

    private sealed class EntityPartAnchorSource(WorldScreenBinder owner, WorldAnchor.EntityPart part) : ISdfAnchorSource {
        public bool TryResolveAnchor(int anchorId, out SdfAnchor anchor) {
            _ = anchorId;

            return owner.TryResolveEntityPart(part: part, anchor: out anchor);
        }
    }

    // A removed camera row: every slot filming it unbinds (a slot whose DECLARED source still names it — possible only
    // transiently inside one delivery, the validator rejects a durable dangling reference — keeps a visible fault), and
    // the registration is released so its offscreen engine stops spending budget.
    private void ReleaseCameraRow(string name) {
        foreach (var slot in m_slots.Values) {
            if ((slot.View is { } view) && string.Equals(a: view.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                slot.View = null;

                if (slot.DeclaredSource is WorldScreenSource.View) {
                    slot.DeclaredFault = $"camera '{name}' not declared";
                }
            }
        }

        m_viewStack?.Release(name: name);
        _ = m_cameraViews.Remove(key: name);
        Console.Error.WriteLine(value: $"[world.camera: view '{name}' released — camera removed]");
    }

    // After a slot stops filming a camera (a screen removal OR any source transition away from it),
    // recompute the surviving wired set: an empty set RELEASES the view (ViewStack.Release disposes the SdfCameraView,
    // freeing its offscreen SdfWorldEngine) and drops the cached registration so a later screen.source <index> view rebuilds it
    // fresh; a non-empty set (another jumbotron still films this camera) only re-narrows the self-reference set to the
    // survivors. The boot-sized ViewStack pool itself stays alive — only this camera's registration ends.
    private void ReleaseOrphanedCameraView(string name) {
        if (m_viewStack is not { } stack) {
            return;
        }

        var wired = WiredScreensFor(name: name);

        if (wired.Count == 0) {
            stack.Release(name: name);
            _ = m_cameraViews.Remove(key: name);
            Console.Error.WriteLine(value: $"[world.screen: camera view '{name}' released — no remaining screen references it]");
        } else {
            stack.SetWiredScreens(name: name, screenIndices: wired);
        }
    }
    private void ReleaseOrphanedCameraViews(HashSet<string> candidates) {
        foreach (var name in candidates) {
            ReleaseOrphanedCameraView(name: name);
        }
    }

    // The set of screen indices currently wired to a camera name — the self-reference set the ViewStack zeroes inside
    // that view's own render.
    private HashSet<int> WiredScreensFor(string name) {
        var indices = new HashSet<int>();

        foreach (var slot in m_slots.Values) {
            if ((slot.View is { } view) && string.Equals(a: view.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                _ = indices.Add(item: slot.Index);
            }
        }

        return indices;
    }

    // Opens (once) and returns the ONE shared webcam feed, or null when no device can be opened (m_cameraFault holds the
    // reason). Every camera screen shares this single session — two sessions on one physical device flicker.
    private CameraFeed? EnsureCameraFeed(WorldFeedProfile profile) {
        if (m_cameraFeed is not null) {
            return m_cameraFeed;
        }

        if (m_cameraTried) {
            return null;
        }

        m_cameraTried = true;

        if (!m_cameraCapture.IsSupported || !m_cameraCapture.TryOpenDefault(requestedWidth: profile.Width, requestedHeight: profile.Height, session: out var session)) {
            m_cameraFault = "no camera device present";

            return null;
        }

        m_cameraFeed = new CameraFeed(
            session: session,
            surface: new CpuSurfaceSource(),
            cadenceTicks: EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz),
            outputWidth: checked((uint)profile.Width),
            outputHeight: checked((uint)profile.Height)
        );

        return m_cameraFeed;
    }

    // The render adapter LUID a capture feed opens its platform capture on when the D3D12 GPU transport is active, or
    // null on the Vulkan/CPU path (and until the render device is first seen at publish; declared GPU-route captures
    // defer their open to the first pull, where this has resolved).
    private long? AdapterLuidForOpen() => (m_hostsOnDirectX ? m_renderAdapterLuid : null);

    // The declared-data compositor-capture boot: route by the source selector (a window title or a whole monitor). A
    // resolvable target starts live; a target momentarily absent on a supported platform retains a pending feed that
    // reacquires on publication through the same cadence-gated TryEnsureSource path; an unsupported platform faults. On
    // the D3D12 GPU transport the open is ALWAYS deferred to that pending path (the render adapter LUID the platform
    // capture must open on is not resolvable at construction), so a valid declaration retains a pending feed here.
    private void BootDeclaredCapture(ScreenSlot slot, WorldScreenSource.Capture capture) {
        if (capture.MonitorIndex is { } monitorIndex) {
            if (m_hostsOnDirectX && m_windowCapture.IsSupported && (monitorIndex >= 0)) {
                slot.Capture = NewCaptureFeed(title: "", profile: capture.Profile, source: null, monitorIndex: monitorIndex);
            } else if (TryOpenMonitorCapture(monitorIndex: monitorIndex, profile: capture.Profile, feed: out var monitorFeed, fault: out var monitorFault)) {
                slot.Capture = monitorFeed;
            } else if (m_windowCapture.IsSupported && (monitorIndex >= 0)) {
                slot.Capture = NewCaptureFeed(title: "", profile: capture.Profile, source: null, monitorIndex: monitorIndex, fault: monitorFault);
            } else {
                slot.DeclaredFault = monitorFault;
            }

            return;
        }

        if (m_hostsOnDirectX && m_windowCapture.IsSupported && !string.IsNullOrWhiteSpace(value: capture.WindowTitle)) {
            slot.Capture = NewCaptureFeed(title: capture.WindowTitle, profile: capture.Profile, source: null);
        } else if (TryOpenCapture(title: capture.WindowTitle, profile: capture.Profile, feed: out var captureFeed, fault: out var captureFault)) {
            slot.Capture = captureFeed;
        } else if (m_windowCapture.IsSupported && !string.IsNullOrWhiteSpace(value: capture.WindowTitle)) {
            slot.Capture = NewCaptureFeed(title: capture.WindowTitle, profile: capture.Profile, source: null, fault: captureFault);
        } else {
            slot.DeclaredFault = captureFault;
        }
    }

    // Constructs a capture feed carrying this binder's transport choice (GPU on the D3D12 host, CPU on Vulkan). The one
    // place window/monitor CaptureFeeds are built, so the route flag can never diverge across the open/pending sites.
    private CaptureFeed NewCaptureFeed(string title, WorldFeedProfile profile, INativeImageCaptureFeed? source, int? monitorIndex = null, string? fault = null) =>
        new(
            title: title,
            service: m_windowCapture,
            profile: profile,
            source: source,
            surface: new CpuSurfaceSource(),
            gpuRoute: m_hostsOnDirectX,
            monitorIndex: monitorIndex
        ) {
            Fault = fault,
        };

    // Resolves a live window by title and opens one compositor-owned, self-pumping feed at the declared budget. On the
    // D3D12 GPU transport the platform capture opens on the render adapter (AdapterLuidForOpen) so its shared textures
    // import cross-API.
    private bool TryOpenCapture(string title, WorldFeedProfile profile, out CaptureFeed feed, out string fault) {
        if (string.IsNullOrWhiteSpace(value: title)) {
            feed = null!;
            fault = "a window title is required";

            return false;
        }

        if (!m_windowCapture.IsSupported || !m_windowCapture.TryCreateWindowCapture(
            windowTitleFragment: title,
            width: profile.Width,
            height: profile.Height,
            refreshRateHz: profile.RefreshRateHz,
            feed: out var source,
            adapterLuid: AdapterLuidForOpen()
        )) {
            feed = null!;
            fault = $"window capture unavailable for '{title}'";

            return false;
        }

        feed = NewCaptureFeed(title: title, profile: profile, source: source);
        fault = "";

        return true;
    }

    // Resolves a whole monitor by 0-based index (0 = primary) and opens one compositor-owned, self-pumping feed at the
    // declared budget. A negative index or a monitor not present faults loudly ("monitor 2 not found"). On the D3D12 GPU
    // transport the platform capture opens on the render adapter (AdapterLuidForOpen).
    private bool TryOpenMonitorCapture(int monitorIndex, WorldFeedProfile profile, out CaptureFeed feed, out string fault) {
        if (monitorIndex < 0) {
            feed = null!;
            fault = $"monitor {monitorIndex} is not a valid index";

            return false;
        }

        if (!m_windowCapture.IsSupported || !m_windowCapture.TryCreateMonitorCapture(
            monitorIndex: monitorIndex,
            width: profile.Width,
            height: profile.Height,
            refreshRateHz: profile.RefreshRateHz,
            feed: out var source,
            adapterLuid: AdapterLuidForOpen()
        )) {
            feed = null!;
            fault = $"monitor {monitorIndex} not found";

            return false;
        }

        feed = NewCaptureFeed(title: "", profile: profile, source: source, monitorIndex: monitorIndex);
        fault = "";

        return true;
    }

    // Pulls one frame from the shared webcam session on the capture cadence and publishes it to the shared surface: a
    // disconnected device drops the feed to unbound + fault, a frame refreshes the handle + room glow, and no frame yet
    // holds the last state.
    private void CaptureCamera(ulong elapsedTicks, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if ((m_cameraFeed is not { } feed) || (feed.Session is not { } session)) {
            return;
        }

        if (session.IsEnded) {
            session.Dispose();
            feed.Session = null;
            feed.Live = false;
            feed.Fault = "camera disconnected";

            return;
        }

        var version = session.FrameVersion;

        if ((version == feed.LastFrameVersion) || !feed.ShouldPull(elapsedTicks: elapsedTicks)) {
            return;
        }

        if (session.TryCapture(surface: out var surface)) {
            var panelSurface = FitPanelSurface(surface: in surface, feed: feed);

            _ = feed.Surface.Publish(deviceContext: deviceContext, gpu: gpu, surface: in panelSurface);
            feed.LastFrameVersion = version;
            feed.Live = true;
            feed.Fault = null;
            feed.Light = AverageColor(pixels: panelSurface.Pixels.Span);
        } else {
            // The async producer advertised a new version but the grab raced it. Do not spend the declaration's whole
            // cadence on that miss; retry on the next produced frame while still avoiding more than one attempt here.
            feed.RetryPull();
        }
    }

    // The Media Foundation session owns its negotiated format and may ignore the preferred extent. A diegetic panel
    // should not upload a megapixel-scale frame it cannot display, so fit CPU pixels into the declaration's envelope
    // before the synchronous GPU upload. The buffer is retained by the feed and reused; no steady-state allocation.
    private static Surface FitPanelSurface(in Surface surface, CameraFeed feed) {
        if (!surface.IsCpuPixels || (surface.Width <= feed.OutputWidth) || (surface.Height <= feed.OutputHeight)) {
            return surface;
        }

        const int bytesPerPixel = 4;
        var targetWidth = feed.OutputWidth;
        var targetHeight = feed.OutputHeight;
        var targetByteLength = checked((int)((targetWidth * targetHeight) * bytesPerPixel));

        if ((surface.Pixels.Length < checked((int)((surface.Width * surface.Height) * bytesPerPixel)))) {
            return surface;
        }

        if ((feed.PanelPixels is null) || (feed.PanelPixels.Length != targetByteLength)) {
            feed.PanelPixels = GC.AllocateUninitializedArray<byte>(length: targetByteLength);
        }

        var source = MemoryMarshal.Cast<byte, uint>(span: surface.Pixels.Span);
        var target = MemoryMarshal.Cast<byte, uint>(span: feed.PanelPixels.AsSpan());

        for (uint y = 0; (y < targetHeight); y++) {
            var sourceY = ((y * surface.Height) / targetHeight);
            var targetRow = (y * targetWidth);
            var sourceRow = (sourceY * surface.Width);

            for (uint x = 0; (x < targetWidth); x++) {
                var sourceX = ((x * surface.Width) / targetWidth);

                target[checked((int)(targetRow + x))] = source[checked((int)(sourceRow + sourceX))];
            }
        }

        return new Surface(
            ImageViewHandle: 0,
            Width: targetWidth,
            Height: targetHeight,
            Format: surface.Format,
            Pixels: feed.PanelPixels
        );
    }

    // Samples only already-completed compositor frames. A miss holds the last frame. An ended compositor session is
    // disposed before the binder resolves a replacement target (a returning window with the same title, or a reconnected
    // monitor); reacquisition is World policy rather than a compatibility path in the platform feed. On the D3D12 GPU
    // transport the platform copies GPU-side into shared textures the screen samples directly — the CPU surface is never
    // published, only its divided-cadence readback frames feed the room glow.
    private void CaptureWindow(CaptureFeed feed, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        if (!feed.TryEnsureSource(adapterLuid: AdapterLuidForOpen())) {
            feed.Live = false;
            feed.Fault = $"{feed.Label} is unavailable";
            // No source to sample: drop the shared images so the next open reallocates and re-attaches from scratch.
            feed.ReleaseGpuTargets();

            return;
        }

        if (feed.GpuRoute && (m_surfaceExport is not null) && OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240)) {
            EnsureGpuTargets(feed: feed, deviceContext: deviceContext);

            // The divided-cadence CPU frames the platform still reads back keep the AverageColor glow alive with no
            // full per-frame readback; never publish them (the sampled handle is the GPU slot, not this surface).
            if (feed.Source!.TryCapture(surface: out var glowSurface) && glowSurface.IsCpuPixels) {
                feed.Light = AverageColor(pixels: glowSurface.Pixels.Span);
            }

            // Live once the platform has completed its first GPU copy — the same first-frame gate the CPU path uses.
            feed.Live = (feed.Source!.GpuRevision > 0L);
            feed.Fault = (feed.Live ? null : $"{feed.Label} awaiting a compositor frame");

            return;
        }

        if (feed.Source!.TryCapture(surface: out var surface)) {
            _ = feed.Surface.Publish(deviceContext: deviceContext, gpu: gpu, surface: in surface);
            feed.Live = true;
            feed.Fault = null;
            feed.Light = AverageColor(pixels: surface.Pixels.Span);
        } else if (!feed.Live) {
            feed.Fault = $"{feed.Label} awaiting a compositor frame";
        }
    }

    // Ensures the feed's THREE simultaneous-access shared textures exist and are attached to its current source at the
    // source's native extent (the sampler scales, so no GPU-side resize is needed). Reallocates on a resize
    // (GpuTargetsOutdated) or a reacquired source; AttachGpuTargets replaces first, then the superseded images are
    // disposed. Cadence-gated by the caller, so it never runs per render frame.
    [SupportedOSPlatform("windows10.0.10240")]
    private void EnsureGpuTargets(CaptureFeed feed, IGpuDeviceContext deviceContext) {
        var source = feed.Source!;
        var width = source.SourceWidth;
        var height = source.SourceHeight;

        // The source has not reported its extent yet (no first frame); nothing to allocate against.
        if ((width <= 0) || (height <= 0)) {
            return;
        }

        if ((feed.GpuTargets is not null) && !source.GpuTargetsOutdated && ReferenceEquals(objA: feed.GpuAttachedSource, objB: source)) {
            return;
        }

        var images = new IGpuExportableStorageImage[3];
        var handles = new nint[images.Length];

        for (var i = 0; (i < images.Length); ++i) {
            images[i] = m_surfaceExport!.CreateSimultaneousAccessStorageImage(
                deviceContext: deviceContext,
                format: GpuPixelFormat.B8G8R8A8Unorm,
                height: (uint)height,
                width: (uint)width
            );
            handles[i] = images[i].SharedHandle;
        }

        var superseded = feed.GpuTargets;

        // Attach first (the platform contract: attach swaps the targets in safely), then release the old allocation.
        source.AttachGpuTargets(targets: new NativeImageGpuCaptureTargets(SharedTargetHandles: handles, Width: width, Height: height));
        feed.GpuTargets = images;
        feed.GpuAttachedSource = source;

        if (superseded is not null) {
            foreach (var image in superseded) {
                image.Dispose();
            }
        }
    }

    // Reports the slowest complete screen-publication frame in each armed block. The source categories sum every slot
    // of that kind, so a tail frame immediately identifies whether live camera upload, desktop capture, emulation, or
    // procedural CPU pixels occupied the render thread without adding per-frame console IO.
    private void ReportPublishTiming(ScreenPublishTiming sample) {
        if (sample.TotalTicks >= m_publishTimingWorst.TotalTicks) {
            m_publishTimingWorst = sample;
        }

        if (0UL != (m_publishTimingFrame % PublishTimingReportInterval)) {
            return;
        }

        static double Milliseconds(long ticks) =>
            (((double)ticks * 1000.0) / Stopwatch.Frequency);

        var worst = m_publishTimingWorst;

        m_publishTimingWorst = default;

        Console.Error.WriteLine(value: $"[frame-timing] screen-publish worst-of-{PublishTimingReportInterval} total {Milliseconds(ticks: worst.TotalTicks):0.000}ms | camera {Milliseconds(ticks: worst.CameraTicks):0.000} | machine {Milliseconds(ticks: worst.MachineTicks):0.000} | window-capture {Milliseconds(ticks: worst.WindowCaptureTicks):0.000} | pattern {Milliseconds(ticks: worst.PatternTicks):0.000}");
    }

    // The framebuffer average as normalized 0..1 light, strided so the per-frame cost stays trivial. The pattern and both
    // live feeds are B8G8R8A8, so byte 2 is red, 1 green, 0 blue.
    private static Vector3 AverageColor(ReadOnlySpan<byte> pixels) {
        const int stride = (16 * 4); // every 16th pixel, 4 bytes each

        var sumRed = 0L;
        var sumGreen = 0L;
        var sumBlue = 0L;
        var samples = 0;

        for (var offset = 0; ((offset + 2) < pixels.Length); offset += stride) {
            sumBlue += pixels[(offset + 0)];
            sumGreen += pixels[(offset + 1)];
            sumRed += pixels[(offset + 2)];
            samples++;
        }

        if (samples == 0) {
            return Vector3.Zero;
        }

        var scale = (1f / (255f * samples));

        return new Vector3(x: (sumRed * scale), y: (sumGreen * scale), z: (sumBlue * scale));
    }

    // One CPU-fed test-pattern screen's owned state: the deterministic pattern producer, its GPU upload adapter, and the
    // room-light average recomputed each publish.
    private sealed class PatternFeed(TestPatternSource pattern, CpuSurfaceSource surface) {
        public Vector3 Light { get; set; }
        public TestPatternSource Pattern { get; } = pattern;
        public CpuSurfaceSource Surface { get; } = surface;
    }

    // One QR screen's owned state: the rasterized B8G8R8A8 buffer (built ONCE — a pure function of Payload/Level/
    // QuietZoneModules, never the tick), its GPU upload adapter, the encoder's resolved version/mask (what screen.source <index> qr
    // reads back), and the room glow, which is a constant for a static image. Published is the "already uploaded to
    // this device" latch Publish checks so an unchanged buffer is not re-copied every produced frame.
    private sealed class QrFeed(byte[] pixels, uint width, uint height, CpuSurfaceSource surface, string payload, QrErrorCorrectionLevel level, int version, int mask, int quietZoneModules, Vector3 light) {
        public uint Height { get; } = height;
        public QrErrorCorrectionLevel Level { get; } = level;
        public Vector3 Light { get; } = light;
        public int Mask { get; } = mask;
        public string Payload { get; } = payload;
        public byte[] Pixels { get; } = pixels;
        public bool Published { get; set; }
        public int QuietZoneModules { get; } = quietZoneModules;
        public CpuSurfaceSource Surface { get; } = surface;
        public int Version { get; } = version;
        public uint Width { get; } = width;
    }

    // One feed's PULL CLOCK, stated once so a webcam and a window capture cannot drift into different refresh
    // policies: the first pull after arming always runs, and every later one waits out the profile's whole period.
    // Rearming (a device loss, or a pull that produced nothing) makes the next pull immediate again.
    private sealed class PullCadence(ulong cadenceTicks) {
        private readonly ulong m_cadenceTicks = cadenceTicks;
        private ulong m_lastPullTicks;
        private bool m_pulled;

        public bool ShouldPull(ulong elapsedTicks) {
            if (m_pulled && ((elapsedTicks - m_lastPullTicks) < m_cadenceTicks)) {
                return false;
            }

            m_pulled = true;
            m_lastPullTicks = elapsedTicks;

            return true;
        }
        public void Rearm() => m_pulled = false;
    }

    // The ONE shared webcam feed: its live session (nulled when the device disconnects), the GPU upload adapter every
    // camera screen samples, and the live/fault/glow state the cadence maintains. A mutable class so the session flips
    // in place; the handle is 0 (unbound) until the first frame lands and whenever the feed is not live.
    private sealed class CameraFeed(ICameraCaptureSession session, CpuSurfaceSource surface, ulong cadenceTicks, uint outputWidth, uint outputHeight) : IDisposable {
        public string? Fault { get; set; }
        public long LastFrameVersion { get; set; } = -1L;
        public Vector3 Light { get; set; }
        public bool Live { get; set; }
        public uint OutputHeight { get; } = outputHeight;
        public uint OutputWidth { get; } = outputWidth;
        public byte[]? PanelPixels { get; set; }
        public ICameraCaptureSession? Session { get; set; } = session;
        public CpuSurfaceSource Surface { get; } = surface;

        private PullCadence Cadence { get; } = new(cadenceTicks: cadenceTicks);

        public nint Handle() => (Live ? Surface.CurrentHandle : 0);
        public bool ShouldPull(ulong elapsedTicks) => Cadence.ShouldPull(elapsedTicks: elapsedTicks);
        public void RetryPull() => Cadence.Rearm();
        public void NotifyDeviceLost() {
            Surface.NotifyDeviceLost();
            LastFrameVersion = -1L;
            Cadence.Rearm();
        }
        public void Dispose() {
            Session?.Dispose();
            Session = null;
            Surface.Dispose();
        }
    }

    // One compositor-capture feed: a producer (a desktop window by title, or a whole monitor by index), its GPU upload
    // adapter, and live/fault/glow state. MonitorIndex null is window mode; non-null is whole-monitor mode.
    private sealed class CaptureFeed(
        string title,
        INativeImageCaptureService service,
        WorldFeedProfile profile,
        INativeImageCaptureFeed? source,
        CpuSurfaceSource surface,
        bool gpuRoute = false,
        int? monitorIndex = null
    ) : IDisposable {
        public string? Fault { get; set; }
        public Vector3 Light { get; set; }
        public bool Live { get; set; }
        public int? MonitorIndex { get; } = monitorIndex;
        public INativeImageCaptureFeed? Source { get; private set; } = source;
        public CpuSurfaceSource Surface { get; } = surface;
        public string Title { get; } = title;

        // Whether this feed rides the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and the screen
        // samples the LatestGpuSlot image), rather than the CPU-pixel Surface. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;
        // The three simultaneous-access shared textures the platform copies into round-robin (null until the source's
        // extent is known and the first attach runs), and the source they are attached to (identity guards re-attach).
        public IReadOnlyList<IGpuExportableStorageImage>? GpuTargets { get; set; }
        public INativeImageCaptureFeed? GpuAttachedSource { get; set; }

        // The human label a fault reads under: a window title, or a whole-monitor index.
        public string Label => ((MonitorIndex is { } monitor) ? $"monitor {monitor}" : $"window '{Title}'");

        private PullCadence Cadence { get; } = new(cadenceTicks: EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz));

        public nint Handle() {
            if (GpuRoute) {
                // The sampled handle is the image-view of the platform's latest completed GPU copy; 0 (no-signal) until
                // that first copy lands. Returning a different slot handle per copy is cheap — the engine rebinds a
                // bound screen source's descriptor every frame anyway (SdfWorldEngine.SetScreenSource).
                return ((Live && (Source is { } source) && (source.LatestGpuSlot is var slot and >= 0) && (GpuTargets is { } targets) && (slot < targets.Count))
                    ? targets[slot].ImageViewHandle
                    : 0);
            }

            return (Live ? Surface.CurrentHandle : 0);
        }
        public bool ShouldPull(ulong elapsedTicks) => Cadence.ShouldPull(elapsedTicks: elapsedTicks);
        public bool TryEnsureSource(long? adapterLuid) {
            if ((Source is { IsEnded: false })) {
                return true;
            }

            // The old target is gone: drop its final frame and clear stale state until the replacement's first frame.
            // The stale GPU attachment is left for EnsureGpuTargets to reallocate against the replacement source.
            Source?.Dispose();
            Source = null;
            Live = false;
            Fault = null;

            INativeImageCaptureFeed? next;
            var reacquired = ((MonitorIndex is { } monitor)
                ? service.TryCreateMonitorCapture(
                    monitorIndex: monitor,
                    width: profile.Width,
                    height: profile.Height,
                    refreshRateHz: profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                )
                : service.TryCreateWindowCapture(
                    windowTitleFragment: Title,
                    width: profile.Width,
                    height: profile.Height,
                    refreshRateHz: profile.RefreshRateHz,
                    feed: out next,
                    adapterLuid: adapterLuid
                ));

            if (!reacquired) {
                return false;
            }

            Source = next;

            return true;
        }
        // Disposes the shared textures (device-owned) and forgets the attachment so the next pull reallocates them on the
        // live device. Called on a lost source, on device loss, and on disposal.
        public void ReleaseGpuTargets() {
            GpuAttachedSource = null;

            if (GpuTargets is not { } targets) {
                return;
            }

            GpuTargets = null;

            foreach (var image in targets) {
                image.Dispose();
            }
        }
        public void NotifyDeviceLost() {
            Surface.NotifyDeviceLost();
            ReleaseGpuTargets();
            Cadence.Rearm();
        }
        public void Dispose() {
            ReleaseGpuTargets();
            Source?.Dispose();
            Source = null;
            Surface.Dispose();
        }
    }

    // One persistent camera-view registration: the live SdfCameraView plus the WorldCamera row it currently embodies
    // (advanced by pose edits, replaced wholesale on recreate) — the diff baseline ReconcileCameras works against.
    private sealed class CameraRegistration {
        public required WorldCamera Row { get; set; }
        public required SdfCameraView View { get; init; }
    }

    // One named jumbotron view a screen samples: the shared ViewStack (set at ConfigureViews) and the camera name to
    // resolve against it. A camera FILMS an already-lit world, so its glow is the ViewStack's own (zero for a camera).
    private sealed class ViewFeed(string name) {
        public string Name { get; } = name;
        public ViewStack? Stack { get; set; }

        public nint Handle() => (Stack?.Resolve(name: Name) ?? 0);
        public Vector3 Light() => (Stack?.ResolveGlow(name: Name) ?? Vector3.Zero);
    }

    // The delegate indirection cell: ResolveHandle/ResolveLight are the STABLE delegate targets
    // m_sources/m_lights register — SdfEngineNode copies those two dictionaries' delegates ONCE, at construction, and
    // never looks at this binder's dictionaries again, so a delegate identity it never copied (one created AFTER
    // boot) is invisible to it no matter what m_sources/m_lights say. A cell is created once per boot-declared index
    // and never replaced; only its Slot field is ever reassigned (by ReconcileScreens, when a removed index is
    // re-declared), so the renderer's one-time copy of ResolveHandle/ResolveLight keeps reading whichever ScreenSlot
    // is current with no rebuild and no renderer-side change.
    private sealed class ScreenSourceCell {
        public required ScreenSlot Slot { get; set; }

        public nint ResolveHandle() => Slot.Handle();
        public Vector3 ResolveLight() => Slot.Light();
    }

    // One declared screen's slot: the persistent declared source (a test pattern, a QR code, or a jumbotron VIEW — all
    // three survive an eject), plus at most one LIVE producer — the shared webcam, or a window capture — that runtime
    // camera/capture swap and eject clears. A machine-owning index carries no local producer here
    // (Server.WorldMachineHost owns it — see this type's own remarks); Handle()/Light() check Machines FIRST, so a
    // machine's presence still wins the same precedence a co-located declared/runtime producer used to enforce
    // locally. A mutable class so the producer references flip in place with no engine rebuild.
    private sealed class ScreenSlot {
        public CameraFeed? Camera { get; set; }
        public CaptureFeed? Capture { get; set; }
        public required int Index { get; init; }
        // The authoritative screen-machine host — consulted FIRST by Handle()/Light()/CurrentFault() for this slot's
        // index, before any locally-owned producer.
        public required WorldMachineHost Machines { get; init; }
        // The WorldScreenSource this slot currently reflects — set at construction and updated by ReconcileScreens, so a
        // live UpsertScreen only re-applies its source through the runtime machinery when the source actually changed.
        public WorldScreenSource? DeclaredSource { get; set; }
        public PatternFeed? Pattern { get; set; }
        // The authored QR code (declared row or live screen.source <index> qr). Sits ABOVE Pattern and BELOW View in precedence, so a
        // a QR authoring onto a test-pattern screen is visible and the View/Qr pair follows last-author-wins (each setter
        // clears the other).
        public QrFeed? Qr { get; set; }
        public ViewFeed? View { get; set; }
        // The ctor-time fault (an absent camera, an unopenable window capture, an unknown view camera); a live feed's
        // own fault is read from the feed instead (see CurrentFault). Machine faults are Machines.State's concern.
        public string? DeclaredFault { get; set; }

        // Whether a live (ejectable) LOCAL producer is bound — the webcam or a window capture (a machine is never
        // local state on this slot; TryEject only ever ejects one of these two).
        public bool HasLive => ((Camera is not null) || (Capture is not null));

        // The current source handle: the host's machine (if this index has one), else the highest-precedence local
        // producer's, else the declared jumbotron view's, else the authored QR's, else the declared test pattern's,
        // else 0.
        public nint Handle() => (Machines.HasMachine(index: Index)
            ? Machines.Handle(index: Index)
            : ((Camera is { } camera)
                ? camera.Handle()
                : ((Capture is { } capture)
                    ? capture.Handle()
                    : ((View is { } view)
                        ? view.Handle()
                        : ((Qr is { } qr)
                            ? qr.Surface.CurrentHandle
                            : (Pattern?.Surface.CurrentHandle ?? 0))))));

        // The current emitted light, in the same precedence as Handle.
        public Vector3 Light() => (Machines.HasMachine(index: Index)
            ? Machines.Light(index: Index)
            : ((Camera is { } camera)
                ? camera.Light
                : ((Capture is { } capture)
                    ? capture.Light
                    : ((View is { } view)
                        ? view.Light()
                        : ((Qr is { } qr)
                            ? qr.Light
                            : (Pattern?.Light ?? Vector3.Zero))))));

        // The fault surfaced by screen.state's non-machine branch: a not-live camera/window feed's own reason, else
        // the ctor-time fault. A machine-owning index's fault comes from Machines.State instead (see the outer
        // type's own State(int) composer).
        public string? CurrentFault() {
            if ((Camera is { Live: false } camera) && (camera.Fault is { } cameraFault)) {
                return cameraFault;
            }

            if ((Capture is { Live: false } capture) && (capture.Fault is { } captureFault)) {
                return captureFault;
            }

            return DeclaredFault;
        }

        // Clears the live LOCAL producer (webcam/window) and reverts to the declared pattern or to unbound. The
        // shared webcam feed is NOT disposed here (other camera screens may still sample it — the binder owns its
        // lifetime); a window capture is per-slot and disposed.
        public void ClearLive() {
            Camera = null;
            Capture?.Dispose();
            Capture = null;
            DeclaredFault = null;
        }

        // Drops the authored QR and disposes the upload surface it owns — the symmetric half of TryQr's acquire, run
        // whenever the slot stops showing that code (a re-author, or a declared source that no longer names one).
        public void ReleaseQr() {
            Qr?.Surface.Dispose();
            Qr = null;
        }

        // Disposes everything this slot OWNS — its CPU test-pattern and QR surfaces and its per-slot window/monitor
        // capture — when the slot is removed entirely (a RemoveScreen mutation). The shared webcam feed and the
        // boot-sized offscreen view pool are NOT owned by a slot (the binder disposes them once), so the Camera/View
        // references are only dropped. Mirrors the binder's own Dispose slot loop.
        public void DisposeOwned() {
            Pattern?.Surface.Dispose();
            Pattern = null;
            ReleaseQr();
            Capture?.Dispose();
            Capture = null;
            Camera = null;
            View = null;
        }
    }
}

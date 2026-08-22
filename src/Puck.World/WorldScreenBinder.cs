using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.Platform;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Binds the world's declared <see cref="WorldScreen"/>s to their live GPU sources — the seam between the pure screen
/// data and the engine's per-index provider maps. Each declared screen owns a slot that can carry a CPU-fed test pattern,
/// an authored QR code, a live webcam feed, a desktop-window capture feed, a jumbotron view, or nothing (the engine's
/// procedural no-signal fallback). A provider is registered for every declared index up front — returning the slot's current handle or 0 — so
/// a runtime <c>screen.source &lt;index&gt; camera</c>/<c>capture</c> binds without rebuilding the engine (the engine copies the
/// provider key set once but polls each provider live, and a 0 handle reads as unbound).
/// A shared singleton so the render factory, the screen verbs, and <c>world.screens</c> read one instance.
/// </summary>
/// <remarks>
/// This type is a pure reader of <see cref="Server.WorldMachineHost"/>'s outputs
/// (<see cref="Server.WorldMachineHost.Handle"/>/<see cref="Server.WorldMachineHost.Light"/> for the room), and
/// <see cref="Publish"/> calls <see cref="IScreenMachine.PublishFrame"/> on the host's live instance — the one
/// GPU call this project makes on a machine's behalf, since <c>Puck.World.Server</c> cannot reach a GPU device
/// context. It also facades several read-only <see cref="WorldMachineHost"/> members (<c>HasMachine</c>,
/// <c>HasEngine</c>, <c>TryReadMachineInsert</c>, <c>TryMagazine</c>, <c>AudioMachine</c>, <c>TryPeek</c>,
/// <c>CaptureLinks</c>, <c>LinkOf</c>, <c>DescribeLinks</c>, <c>TryReadLinkMembers</c>) so presentation-side
/// callers (<c>PlayerCommandModule</c>, <c>WorldAudioDirector</c>, <c>WorldSessionCapture</c>'s <c>world.save</c>
/// fold, <c>ScreenCommandModule</c>'s read-only verbs) reach the host's state through the same reference they
/// already hold. Machine lifecycle mutation (insert/eject/select/options/link/unlink) routes through
/// <c>ScreenCommandModule</c> submitting a <c>WorldScreenOp</c> through
/// <c>IServerLink.SubmitScreenOp</c> instead, landing in the ordered submission domain (see <c>WorldScreenOp</c>'s
/// own remarks). Camera/capture/window-capture/jumbotron-view/test-pattern screen sources remain genuinely
/// presentation-owned.
/// <para>An unbound slot (a <see cref="WorldScreenSource.None"/> screen, or a live feed with no signal) registers a
/// provider returning 0, so the engine leaves its surface unbound and lights it with the procedural no-signal
/// fallback — never black. One webcam session is opened engine-wide per sensor and shared by every camera screen
/// naming that sensor. A capture device may expose both streams while supporting only one at a time; a dual open must
/// prove both streams live before it replaces the established feed. Thus N camera screens sample at most two feeds. Single-threaded:
/// <see cref="Publish"/> and simulation-routed screen mutations all run on the launcher's window-pump thread, so no
/// lock guards this state.</para>
/// </remarks>
internal sealed partial class WorldScreenBinder : IDisposable, IWorldScreenPresenter {
    // The presentation-only pull-back a window's fitted eye rides above the local seat's SIMULATION body position —
    // the authoritative position is grounded at the body's feet, not its eyes; this stays a fixed approximation
    // (never derived from a per-world camera rig) since a window's own frustum already reprojects correctly for any
    // reasonable eye height, and the fit is forgiving of a small vertical offset error the way any first-person eye
    // height guess is.
    private const float LocalEyeHeight = 1.6f;
    private const ulong PublishTimingReportInterval = 60UL;
    // A QR feed picks its module pixel size so the rendered image lands near QrTargetPixelExtent square whatever
    // version the payload chose, clamped to stay legibly crisp (at least QrMinModulePixels per module) and well under
    // the validator's MaxSurfaceDimension ceiling (QrMaxModulePixels keeps even a version-10 grid with a generous
    // quiet zone far below it).
    private const int QrDefaultQuietZoneModules = 4;
    private const int QrMaxModulePixels = 12;
    private const int QrMinModulePixels = 4;
    private const int QrTargetPixelExtent = 640;
    // Every session-sourced screen's registration name, so a lifecycle/teardown pass can re-derive the ViewStack key
    // without threading it through every call site.
    private const string SessionRegistrationPrefix = "session:";

    // The anchor source for anchored cameras (the client's snapshot-fed entity view). Anchor ids are entity indices,
    // so an Anchored view follows the same interpolated render pose the main world draws without reaching into
    // simulation state or duplicating pose math here.
    private readonly ISdfAnchorSource m_anchors;
    private readonly ICameraCaptureService m_cameraCapture;
    // The D3D12-host GPU capture transport: on the Direct3D 12 host, window/monitor captures AND the shared webcam
    // publish GPU-side into shared simultaneous-access textures the screens sample directly (no CPU round trip); the
    // Vulkan host keeps the CPU path.
    // The factory is non-null only on the D3D12 host, and the render adapter LUID is resolved once from the render device
    // context at the first publish (the device does not exist at construction), so capture feeds open on the render GPU.
    private readonly bool m_hostsOnDirectX;
    // The process's running world instances. Its observation resolver door owns destination lookup, origin adoption,
    // generation resolution and start/reuse, so a screen and a crossing cannot grow independent routing rules.
    private readonly WorldInstanceHost m_instanceHost;
    // The authoritative screen-machine host — owns every booted IScreenMachine; this binder reads its outputs
    // (Handle/Light/MachineAt) and facades several of its read-only members. Never mutated through here — see this
    // type's own remarks.
    private readonly WorldMachineHost m_machines;
    private readonly WorldStampPool m_stamps;
    private readonly DirectXGpuSurfaceExportFactory? m_surfaceExport;
    // The backend-neutral surface-transfer factory — the Vulkan host's camera GPU tier imports its shared camera
    // targets through it (the D3D12 host samples its own resources directly and never calls it for the camera).
    // Null on a headless boot, which composes no presenter and never publishes.
    private readonly IGpuSurfaceTransferFactory? m_surfaceTransfers;
    private readonly INativeImageCaptureService m_windowCapture;

    // The camera GPU tier's target factory (lazily created inside the platform-guarded open) and, on the Vulkan host,
    // the headless Direct3D 12 device the targets are allocated on — pinned to the render adapter's LUID so the
    // platform's D3D11 decode device and the Vulkan render device both reach the same physical memory.
    private DirectXGpuSurfaceExportFactory? m_cameraExport;
    private DirectXDeviceContext? m_cameraTargetDevice;

    // The player roster — resolves a seat to its bound camera device (TryGetSeatDevice) and mints the camera<N>
    // tokens screen.camera/probe.status echo. A camera is an input device seated like a gamepad; this binder never
    // opens hardware by device identity, only by (seat, sensor).
    private readonly PlayerRoster m_roster;
    // Every physical camera device seen since boot, by its reconnect-stable InputDeviceId, plus a stable first-seen
    // order for DescribeCamera. A device vanishing (unplugged) drops out of both; a later reconnect (the SAME
    // content-addressed id) re-enumerates fresh.
    private readonly Dictionary<InputDeviceId, CameraDevice> m_cameraDevices = new();
    private readonly List<CameraDevice> m_cameraDeviceOrder = new();
    // One feed per (device, sensor) a consumer has resolved — shared by every screen/probe/HUD source landing on the
    // same device and sensor.
    private readonly Dictionary<(InputDeviceId Device, WorldCameraSensor Sensor), CameraFeed> m_cameraFeeds = new();
    // The live (seat, sensor) demand set, fully recomputed every publish (ReconcileCameraDemand) from the actual
    // consumers — camera-bound screen slots, retained probe sockets, retained HUD frame sources — the richest
    // profile any of them requests for the same pair, then synced against the roster's current seating
    // (ReconcileCameraFeedsToDemand). Never mutated incrementally outside that pair of methods.
    private readonly Dictionary<(int Seat, WorldCameraSensor Sensor), WorldFeedProfile> m_cameraDemand = new();
    // The authored control state per seat (the first camera row authoring controls for a given seat wins, per
    // ResolveSeatCameraControls) — resolved at boot and re-resolved by ReconcileScreens, so an UpsertScreen mutation
    // moves the seat's device live through the per-frame service path.
    private Dictionary<int, WorldCameraControls?> m_seatCameraControls = new();
    private long m_nextCameraDeviceScanTimestamp;
    // Narrates a scan failure once per failure episode (ServiceCameraDevices) rather than every ~2s retry; cleared
    // the moment a scan succeeds again.
    private bool m_cameraDeviceScanFailed;
    // The world's placeable-camera rows — booted from the definition and REPLACED by ReconcileCameras when a camera
    // mutation delivers, so a runtime screen.source <index> view (and every later resolve) reads the LIVE rows.
    private IReadOnlyList<WorldCamera> m_cameras;
    private bool m_disposed;
    private ulong m_publishTimingFrame;
    private ScreenPublishTiming m_publishTimingWorst;
    private long? m_renderAdapterLuid;
    private int m_viewDynamicTransformCapacity;
    private bool m_viewHostsOnDirectX;
    private int m_viewInstanceCapacity;
    private int m_viewProgramWordCapacity;
    private int m_viewRefreshCountdown;
    private SdfViewGpuServices? m_viewServices;
    // The offscreen view pool backing the View (jumbotron) screens — created by ConfigureViews once the render envelope
    // is known, null until then (and forever when the world declares no View screen). The view config the pool needs is
    // stashed alongside so a runtime screen.source <index> view can register against the same envelope.
    private ViewStack? m_viewStack;

    private IReadOnlyList<DynamicTransform> m_viewTransforms = [];
    private readonly Dictionary<int, ScreenSlot> m_slots = new();
    // The screen indices declared at BOOT (construction) — the render engine's frozen provider key set, copied
    // ONCE and never grown. Distinct from m_slots.Keys, which shrinks/grows as ReconcileScreens removes/recreates
    // entries: an index in this set can always have its m_slots/m_sources/m_lights entries safely RECREATED after
    // removal (the engine's own frozen key list still names it), while a genuinely new index (never in this set)
    // still cannot bind live.
    private readonly HashSet<int> m_bootScreenIndices = new();
    private readonly Dictionary<int, Func<SdfScreenSourceFrame>> m_sources = new();
    private readonly Dictionary<int, Func<Vector3>> m_lights = new();
    // SdfEngineNode copies m_sources/m_lights into its own dictionary once, at construction, and never re-reads
    // these dictionaries again — writing a new delegate into m_sources[index] after boot is invisible to the
    // renderer. Each boot index's cell is instead a stable, never-replaced delegate target; only the cell's own
    // Slot field is re-pointed when ReconcileScreens recreates a boot index's slot after a remove+reset.
    private readonly Dictionary<int, ScreenSourceCell> m_sourceCells = new();
    // Reused scratch for ReconcileScreens' removal pass, so a screen mutation collects the vanished indices without
    // allocating and never mutates m_slots while enumerating it.
    private readonly List<int> m_reconcileRemovals = new();
    // Persistent camera-view registrations by camera name — each holds the SdfCameraView (a real GPU resource: its
    // offscreen engine) plus the WorldCamera row it was built from, so a re-point reuses the SAME instance and a
    // camera mutation diffs against the row the LIVE view embodies (pose edit = rig property write; dimension/kind
    // change = release + recreate).
    private readonly Dictionary<string, CameraRegistration> m_cameraViews = new(comparer: StringComparer.Ordinal);
    // Reused scratch for ReconcileCameras (the registered names snapshot walked while m_cameraViews mutates).
    private readonly List<string> m_cameraReconcileScratch = new();
    // A jumbotron is a diegetic 160x144 display, not another full-rate player view. ViewStack already persists the last
    // resolved handle when a budgeted view is skipped; this countdown deliberately spends the offscreen SDF render only
    // once every N produced frames. Frame-count cadence is deterministic and avoids introducing a wall clock.
    private int m_viewRefreshDivisor = 4;

    /// <summary>Initializes the binder over the world's declared screens: a CPU feed for each test-pattern screen, the
    /// shared webcam for each camera screen, and a window-capture session for each capture screen (absent camera /
    /// unopenable window leaves the slot unbound and the fault visible in <c>world.screens</c>/<c>screen.state</c> —
    /// loud data, no crash), plus a source + light provider for every declared index. A declared machine screen
    /// registers no local producer here — <see cref="Server.WorldMachineHost"/> (a peer singleton, already booted by
    /// the time this constructor runs) owns it; this binder's providers read the host directly for those indices.</summary>
    /// <param name="screens">The world's diegetic screens (<see cref="WorldDefinition.Screens"/>).</param>
    /// <param name="machines">The authoritative screen-machine host this binder reads outputs from.</param>
    /// <param name="cameraCapture">The platform webcam service (CPU tier) the camera screens share one session of.</param>
    /// <param name="windowCapture">The platform compositor window-capture service.</param>
    /// <param name="surfaceTransfers">The backend-neutral surface-transfer factory the Vulkan host's camera GPU tier
    /// imports its shared targets through, or <see langword="null"/> on a headless boot (which never publishes).</param>
    /// <param name="cameras">The world's placeable cameras a View (jumbotron) screen resolves its camera name against.</param>
    /// <param name="anchors">The entity anchor source used by anchored cameras (the client's snapshot-fed view).</param>
    /// <param name="stamps">The compiled creation-look pool supplying authored entity parts.</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 — selects the GPU capture transport for
    /// window/monitor captures (the Vulkan host keeps their CPU-pixel path). The shared camera rides its GPU tier on
    /// both hosts; this flag only picks how its shared targets are allocated and sampled.</param>
    /// <param name="instanceHost">The process's running world instances — a session-sourced face's resolved
    /// destination instance is found or started here.</param>
    /// <param name="roster">The player roster — resolves a seat to its bound camera device.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldScreenBinder(IReadOnlyList<WorldScreen> screens, WorldMachineHost machines, ICameraCaptureService cameraCapture, INativeImageCaptureService windowCapture, IGpuSurfaceTransferFactory? surfaceTransfers, IReadOnlyList<WorldCamera> cameras, ISdfAnchorSource anchors, WorldStampPool stamps, bool hostsOnDirectX, WorldInstanceHost instanceHost, PlayerRoster roster) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentNullException.ThrowIfNull(argument: cameraCapture);
        ArgumentNullException.ThrowIfNull(argument: windowCapture);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: anchors);
        ArgumentNullException.ThrowIfNull(argument: stamps);
        ArgumentNullException.ThrowIfNull(argument: instanceHost);
        ArgumentNullException.ThrowIfNull(argument: roster);

        m_machines = machines;
        m_cameraCapture = cameraCapture;
        m_surfaceTransfers = surfaceTransfers;
        m_windowCapture = windowCapture;
        m_cameras = cameras;
        m_anchors = anchors;
        m_stamps = stamps;
        m_hostsOnDirectX = hostsOnDirectX;
        m_instanceHost = instanceHost;
        m_roster = roster;
        // Windows-10240 guarded because DirectXGpuSurfaceExportFactory is platform-attributed; hostsOnDirectX already
        // implies that floor (Program.cs rejects the D3D12 backend below it), so the check only satisfies the analyzer.
        m_surfaceExport = ((hostsOnDirectX && OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        ))
            ? new DirectXGpuSurfaceExportFactory()
            : null
        );
        m_seatCameraControls = ResolveSeatCameraControls(screens: screens);

        foreach (var screen in screens) {
            _ = m_bootScreenIndices.Add(item: screen.Index);

            var slot = new ScreenSlot { Binder = this, DeclaredSource = screen.Source, Index = screen.Index, Machines = m_machines };

            switch (screen.Source) {
                case WorldScreenSource.TestPattern pattern:
                    slot.Pattern = new PatternFeed(
                        pattern: new TestPatternSource(
                            width: pattern.Width,
                            height: pattern.Height
                        ),
                        surface: new CpuSurfaceSource()
                    );

                    break;
                case WorldScreenSource.Machine:
                    // WorldMachineHost already booted this index (if it could) at ITS OWN construction — nothing to
                    // do here; Handle()/Light() below read the host directly for a machine-owning index.
                    break;
                case WorldScreenSource.Camera cameraSource:
                    // The declared webcam: bind the seat's sensor demand. Color and infrared may be two sensors on
                    // one physical camera, so the platform opens them as one coordinated graph when both are
                    // declared for the same seat. Resolution is per-frame (the seat's device may not be enumerated
                    // yet at boot) — an unassigned seat or an incompatible sensor reports through the slot's live
                    // fault (CurrentFault) rather than a fault recorded here.
                    if (!m_cameraCapture.IsSupported) {
                        slot.DeclaredFault = "no camera device present";
                    } else {
                        // Demand for this (seat, sensor) is derived from this slot at the next publish
                        // (ReconcileCameraDemand reads CameraSeat/CameraSensorKind plus DeclaredSource, already
                        // assigned above/below) — nothing to declare imperatively here.
                        slot.CameraSeat = (cameraSource.Seat ?? 1);
                        slot.CameraSensorKind = cameraSource.Sensor;
                    }

                    break;
                case WorldScreenSource.Capture capture:
                    // The declared compositor capture (a window title or a whole monitor) resolves its live target and
                    // starts its feed. A target momentarily absent on a supported platform (the World window before its
                    // HWND is visible, a disconnected monitor) retains a pending feed and resolves on publication
                    // instead of permanently faulting during composition.
                    BootDeclaredCapture(
                        capture: capture,
                        slot: slot
                    );

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
                    if (!TryBuildQrFeed(
                        payload: qr.Payload,
                        ecLevel: qr.EcLevel,
                        quietZoneModules: qr.QuietZoneModules,
                        feed: out var declaredQr,
                        fault: out var qrFault
                    )) {
                        slot.DeclaredFault = qrFault;
                    } else {
                        slot.Qr = declaredQr;
                    }

                    break;
                case WorldScreenSource.Session session:
                    // Resolution/attachment is headless-safe (no GPU) and runs NOW, at boot, in every shape — the
                    // observation lease and the destination instance exist regardless of presentation. The offscreen
                    // GPU view registration is deferred to ConfigureViews (below), exactly like a declared View
                    // camera's SdfCameraView. A fresh slot has no previous feed to preserve, so a direct assignment
                    // (null on refusal, the resolved feed on success) is the whole job here.
                    slot.Session = ResolveSession(
                        session: session,
                        slot: slot
                    );

                    break;
                case WorldScreenSource.Text text:
                    // No image producer: the decal tier bypasses the source table entirely — the frame source's
                    // ScreenDecals providers read this record back through TextSourceAt each produced frame.
                    slot.Text = text;

                    break;
                case WorldScreenSource.Probe probe:
                    // The declared probe output: the feed exists from here on, dark until the probes host declares
                    // the probe writes a texture and its kernel publishes a first frame.
                    slot.Probe = GetOrAddProbeFeed(id: probe.Id);

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
            m_sources[screen.Index] = cell.ResolveFrame;
            m_lights[screen.Index] = cell.ResolveLight;
        }
    }

    /// <summary>Gets the number of camera views registered in the offscreen view pool right now — each one is a live
    /// <see cref="SdfCameraView"/> spending refresh budget. Zero when no View screen is declared (no pool) or the pool
    /// has not been configured yet. Removing the last screen wired to a camera releases its view, so this count drops
    /// (the pipe-observable witness that a removed View screen's offscreen render stopped).</summary>
    public int ActiveCameraViewCount => (m_viewStack?.ActiveViewCount ?? 0);
    /// <summary>Gets the screen-light providers keyed by screen index — parallel to <see cref="ScreenSources"/>, the room glow
    /// each slot emits (its framebuffer average, or zero when unbound).</summary>
    public IReadOnlyDictionary<int, Func<Vector3>> ScreenLights => m_lights;
    /// <summary>Gets the screen-source providers keyed by screen index — the map the render spec's <c>ScreenSources</c> field
    /// takes. A provider is present for every declared screen; it returns 0 while the slot carries no producer, which the
    /// engine reads as unbound (the procedural fallback), so a runtime insert binds with no engine rebuild.</summary>
    public IReadOnlyDictionary<int, Func<SdfScreenSourceFrame>> ScreenSources => m_sources;
    /// <summary>Gets the current produced-frame divisor for jumbotron offscreen renders.</summary>
    public int ViewRefreshDivisor => m_viewRefreshDivisor;

    // Apply one NON-MACHINE source through the runtime machinery — shared by the reconcile-side declared-source
    // change and ApplyNonMachineSource (screen.select's non-machine branch). A Machine source drops any local
    // presentation producer (Server.WorldMachineHost owns the machine itself) but is otherwise a no-op arm.
    //
    // Every transition away from View clears the slot's jumbotron reference and releases the camera registration
    // when no surviving slot films it; a View->View re-point releases the previously-registered camera inside
    // TryView. A slot that no longer names a QR drops the rasterized one the same way.
    private (bool Ok, string Message) ApplySource(int index, ScreenSlot slot, WorldScreenSource source) {
        var outcome = source switch {
            WorldScreenSource.None => (slot.HasLive
            ? TryEject(index: index)
            : (Ok: true, Message: $"screen {index} unbound")),
            WorldScreenSource.Machine => (slot.HasLive
            ? TryEject(index: index)
            : (Ok: true, Message: $"screen {index} machine (host-owned)")),
            WorldScreenSource.Camera cameraSource => TryCamera(
            index: index,
            sensor: cameraSource.Sensor,
            seat: (cameraSource.Seat ?? 1)
        ),
            WorldScreenSource.Capture { MonitorIndex: { } monitorIndex } => TryDesktop(
            index: index,
            monitorIndex: monitorIndex
        ),
            WorldScreenSource.Capture capture => TryCapture(
            index: index,
            windowTitle: capture.WindowTitle
        ),
            WorldScreenSource.View view => ApplyViewChange(
            index: index,
            slot: slot,
            view: view
        ),
            WorldScreenSource.Qr qr => TryQr(
            index: index,
            payload: qr.Payload,
            ecLevel: qr.EcLevel,
            quietZoneModules: qr.QuietZoneModules
        ),
            // The document's OWN authored session record, verbatim — see ApplySessionSource's own remarks for why
            // this must not narrow through TrySession's (destination, camera)-only verb surface.
            WorldScreenSource.Session session => ApplySessionSource(
            index: index,
            session: session
        ),
            WorldScreenSource.Text text => ApplyTextSource(
            index: index,
            slot: slot,
            text: text
        ),
            WorldScreenSource.Probe probe => TryProbe(
            index: index,
            id: probe.Id
        ),
            _ => (Ok: false, Message: $"screen {index} source applies at next boot"),
        };

        if (source is not WorldScreenSource.View) {
            ReleaseSlotView(slot: slot);
        }

        if (source is not WorldScreenSource.Qr) {
            slot.ReleaseQr();
        }

        if (source is not WorldScreenSource.Session) {
            ReleaseSlotSession(slot: slot);
        }

        if (source is not WorldScreenSource.Text) {
            slot.Text = null;
        }

        return outcome;
    }
    // Apply one screen's changed source through the runtime machinery and narrate it. The releases the change implies
    // live in ApplySource itself, so every caller gets them.
    private void ApplySourceChange(int index, ScreenSlot slot, WorldScreenSource source) {
        var outcome = ApplySource(
            index: index,
            slot: slot,
            source: source
        );

        Console.Error.WriteLine(value: $"[world.screen: {outcome.Message}]");
    }
    // The reconcile/verb-side text bind: drop any live local producer (the decal shades instead of an image), then
    // record the text for the frame source's decal providers. The engine change-detects the resulting cells, so
    // re-applying identical text uploads nothing.
    private (bool Ok, string Message) ApplyTextSource(int index, ScreenSlot slot, WorldScreenSource.Text text) {
        if (slot.HasLive) {
            var ejected = TryEject(index: index);

            if (!ejected.Ok) {
                return ejected;
            }
        }

        slot.Text = text;

        return (Ok: true, Message: $"screen {index} text ({text.Lines.Count} line(s))");
    }
    // The framebuffer average as normalized 0..1 light, strided so the per-frame cost stays trivial. The pattern and both
    // live feeds are B8G8R8A8, so byte 2 is red, 1 green, 0 blue.
    private static Vector3 AverageColor(ReadOnlySpan<byte> pixels) {
        const int Stride = (16 * 4); // every 16th pixel, 4 bytes each

        var sumRed = 0L;
        var sumGreen = 0L;
        var sumBlue = 0L;
        var samples = 0;

        for (var offset = 0; ((offset + 2) < pixels.Length); offset += Stride) {
            sumBlue += pixels[(offset + 0)];
            sumGreen += pixels[(offset + 1)];
            sumRed += pixels[(offset + 2)];
            samples++;
        }

        if (samples == 0) {
            return Vector3.Zero;
        }

        var scale = (1f / (255f * samples));

        return new Vector3(
            x: (sumRed * scale),
            y: (sumGreen * scale),
            z: (sumBlue * scale)
        );
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

    /// <summary>Applies a non-machine magazine entry (including camera, capture, view, test-pattern, text, and none) as a screen's live
    /// source, through the same dispatch <see cref="ReconcileScreens"/>'s declared-source-change path uses.
    /// <c>ScreenCommandModule.SelectHandler</c> calls this directly, client-side, immediately after submitting the
    /// entry's selector move through the ordered domain (<c>WorldScreenOp.Select</c>) — the selector is
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

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        return ApplySource(
            index: index,
            slot: slot,
            source: source
        );
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
            slot.Session?.Dispose();
        }

        DisposeCamera();
        DisposeProbeFeeds();
        DisposeViewExports();
        DisposeFrameCaptures();

        // After the feeds: the camera's and every probe's shared targets live on this headless device, so it must
        // outlive them.
        if (
            (m_cameraTargetDevice is { } cameraTargetDevice) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )
        ) {
            cameraTargetDevice.Dispose();
            m_cameraTargetDevice = null;
        }

        m_viewStack?.Dispose();
        m_viewStack = null;
    }
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

        CameraDeviceLost();

        // The Vulkan camera route's headless D3D12 device and the cached render LUID describe the OLD render adapter.
        // Release the device only after the feed dropped every target allocated on it, then let the next Publish read
        // the replacement renderer's LUID (which may identify a different physical adapter after recovery).
        if (
            (m_cameraTargetDevice is { } cameraTargetDevice) &&
            OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )
        ) {
            cameraTargetDevice.Dispose();
            m_cameraTargetDevice = null;
        }

        m_renderAdapterLuid = null;

        foreach (var slot in m_slots.Values) {
            slot.Capture?.NotifyDeviceLost();
        }

        NotifyFrameCapturesDeviceLost();

        m_viewStack?.NotifyDeviceLost();
    }
    /// <summary>Reconciles the binder's runtime source machinery to a mutated screen list — the live-application half of
    /// an <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called by the frame source when the definition
    /// revision moves. Removals are reconciled first: a slot whose index is no longer declared has any engaged player
    /// disengaged (their avatar resumes normal intent), its owned machine/pattern/capture state disposed, and its
    /// entries dropped from <c>m_slots</c>/<c>m_sources</c>/<c>m_lights</c> — so a removed screen stops advancing,
    /// publishing, and answering screen commands (the shared webcam session and the boot-sized view pool are not
    /// disposed here — the binder owns their lifetime). A removed <c>View</c> screen additionally releases its camera's
    /// offscreen render when no surviving slot still films that camera (the orphaned <see cref="ViewStack"/> entry
    /// is disposed so it stops consuming refresh budget), while a camera two jumbotrons share stays live for the
    /// survivor. Then, for a declared index whose source changed, it re-applies
    /// the new source through the same insert/eject/camera/capture/view machinery a <c>screen.*</c> verb uses
    /// (best-effort — a failed bind logs a loud line, never throws). Screen slab geometry (adds/moves/removes) rides the
    /// program rebuild in the frame source, not this method. Capacity honesty, precisely: an index that was declared
    /// at boot (<see cref="m_bootScreenIndices"/>) always gets its slot/provider entries recreated on re-declaration
    /// after a removal — the render engine's frozen key list still names it, so this is safe. A genuinely new
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
            if (!DeclaresIndex(
                index: index,
                screens: screens
            )) {
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
            // DissolveScreen) — see this type's own remarks. This pass only drops the presentation-side slot.
            if (m_slots.Remove(
                key: index,
                value: out var slot
            )) {
                // Note the camera a removed View screen filmed BEFORE DisposeOwned drops the reference, so its offscreen
                // render can be released once the whole removal pass has updated m_slots (a name shared by another
                // surviving jumbotron must NOT be released).
                if (slot.View is { } view) {
                    (removedViewCameras ??= new HashSet<string>(comparer: StringComparer.Ordinal)).Add(item: view.Name);
                }

                if (slot.Session is { } session) {
                    ReleaseSession(
                        feed: session,
                        index: index,
                        reason: "screen removed"
                    );
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
            if (m_slots.TryGetValue(
                key: screen.Index,
                value: out var slot
            ) is false) {
                if (!m_bootScreenIndices.Contains(item: screen.Index)) {
                    Console.Error.WriteLine(value: $"[world.screen: {screen.Index} added — its source applies at next boot (render provider key set frozen at boot)]");

                    continue;
                }

                // A boot-declared index that was removed and is now re-declared — recreate the slot; safe because
                // the render engine's frozen key list still names this index. DeclaredSource starts null so the
                // Equals check below never short-circuits a fresh slot. Never write a new delegate into
                // m_sources/m_lights here (see m_sourceCells' own remarks) — re-point the boot-time cell's Slot
                // field instead, which the renderer's already-copied delegate reads through immediately.
                slot = new ScreenSlot { Binder = this, DeclaredSource = null, Index = screen.Index, Machines = m_machines };
                m_slots[screen.Index] = slot;

                if (m_sourceCells.TryGetValue(
                    key: screen.Index,
                    value: out var cell
                )) {
                    cell.Slot = slot;
                } else {
                    // Should not happen for a boot index (its cell is created once, at construction, and never
                    // removed) — guarded defensively rather than assumed, since a first poll before this branch
                    // would otherwise throw a KeyNotFoundException reading m_sources/m_lights.
                    cell = new ScreenSourceCell { Slot = slot };
                    m_sourceCells[screen.Index] = cell;
                    m_sources[screen.Index] = cell.ResolveFrame;
                    m_lights[screen.Index] = cell.ResolveLight;
                }
            }

            // The magazine and its live selector are authoritative server state now (Server.WorldMachineHost owns
            // both) — nothing to refresh here.
            if (Equals(
                objA: slot.DeclaredSource,
                objB: screen.Source
            )) {
                continue;
            }

            ApplySourceChange(
                index: screen.Index,
                slot: slot,
                source: screen.Source
            );
            slot.DeclaredSource = screen.Source;
        }

        // One physical camera has one authored control state. Re-resolve it from the mutated list; the per-frame service
        // path lands it on the device at the next live frame (vendor writes are firmware-ignored on an idle stream).
        m_seatCameraControls = ResolveSeatCameraControls(screens: screens);
    }
    /// <summary>Clears a screen's live local producer — the runtime <c>screen.eject</c> path — for either kind this
    /// binder itself owns (the webcam, a window capture). Ejecting a machine is <c>ScreenCommandModule</c>'s
    /// <c>WorldScreenOp.Eject</c> submission instead (see this type's own remarks). The slot reverts to its declared
    /// test pattern or to unbound (the procedural fallback). Fails for an undeclared screen or a slot with no live
    /// local producer to clear.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>Whether the eject succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryEject(int index) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!slot.HasLive) {
            return (Ok: false, Message: $"screen {index} has no source to eject");
        }

        slot.ClearLive();

        return (Ok: true, Message: $"screen {index} ejected");
    }

    // One CPU-fed test-pattern screen's owned state: the deterministic pattern producer, its GPU upload adapter, and the
    // room-light average recomputed each publish.
    private sealed class PatternFeed(TestPatternSource pattern, CpuSurfaceSource surface) {
        public Vector3 Light { get; set; }

        public TestPatternSource Pattern { get; } = pattern;
        public CpuSurfaceSource Surface { get; } = surface;
    }
    // The delegate indirection cell: ResolveFrame/ResolveLight are the stable delegate targets m_sources/m_lights
    // register. A cell is created once per boot-declared index and never replaced; only its Slot field is ever
    // reassigned (by ReconcileScreens, when a removed index is re-declared), so the renderer's one-time copy of
    // ResolveFrame/ResolveLight keeps reading whichever ScreenSlot is current.
    private sealed class ScreenSourceCell {
        public required ScreenSlot Slot { get; set; }

        public SdfScreenSourceFrame ResolveFrame() => Slot.AcquireFrame();
        public Vector3 ResolveLight() => Slot.Light();
    }
    // One declared screen's slot: the persistent declared source (a test pattern, a QR code, or a jumbotron VIEW —
    // all three survive an eject), plus at most one LIVE producer — the shared webcam, or a window capture — that
    // runtime camera/capture swap and eject clears. A machine-owning index carries no local producer here
    // (Server.WorldMachineHost owns it); Handle()/Light() check Machines first. A mutable class so the producer
    // references flip in place with no engine rebuild.
    private sealed class ScreenSlot {
        // The bound (seat, sensor) demand — the slot's camera resolves through Binder every frame rather than
        // caching a CameraFeed directly, since the seat's device (and even the seat itself) can change live with no
        // notification to this slot (see WorldScreenBinder.TryResolveCamera).
        public int? CameraSeat { get; set; }
        public WorldCameraSensor? CameraSensorKind { get; set; }
        public CaptureFeed? Capture { get; set; }
        public ProbeFeed? Probe { get; set; }
        // The ctor-time fault (an absent camera, an unopenable window capture, an unknown view camera); a live feed's
        // own fault is read from the feed instead (see CurrentFault). Machine faults are Machines.State's concern.
        public string? DeclaredFault { get; set; }
        // The WorldScreenSource this slot currently reflects — set at construction and updated by ReconcileScreens, so a
        // live UpsertScreen only re-applies its source through the runtime machinery when the source actually changed.
        public WorldScreenSource? DeclaredSource { get; set; }
        // Whether a live (ejectable) local producer is bound — the webcam, a probe output, or a window capture (a
        // machine is never local state on this slot).
        public bool HasLive => ((CameraSeat is not null) || (Capture is not null) || (Probe is not null));
        public required int Index { get; init; }
        // The owning binder — resolves this slot's (CameraSeat, CameraSensorKind) demand to a live frame every call,
        // since a camera consumer's binding names a SEAT, never a device.
        public required WorldScreenBinder Binder { get; init; }
        // The authoritative screen-machine host — consulted FIRST by Handle()/Light()/CurrentFault() for this slot's
        // index, before any locally-owned producer.
        public required WorldMachineHost Machines { get; init; }
        public PatternFeed? Pattern { get; set; }
        // The authored QR code (declared row or live screen.source <index> qr). Sits ABOVE Pattern and BELOW View in precedence, so a
        // a QR authoring onto a test-pattern screen is visible and the View/Qr pair follows last-author-wins (each setter
        // clears the other).
        public QrFeed? Qr { get; set; }
        public SessionFeed? Session { get; set; }
        // The live decal-text source (declared row or a live source change) — no producer, no handle: the decal tier
        // reads it back through TextSourceAt instead of the source table.
        public WorldScreenSource.Text? Text { get; set; }
        public ViewFeed? View { get; set; }

        // Clears the live LOCAL producer (webcam/window) and reverts to the declared pattern or to unbound. The
        // shared webcam feed is NOT disposed here (other camera screens may still sample it — the binder owns its
        // lifetime); a window capture is per-slot and disposed.
        public void ClearLive() {
            CameraSeat = null;
            CameraSensorKind = null;
            Probe = null;
            Capture?.Dispose();
            Capture = null;
            DeclaredFault = null;
        }
        // The fault surfaced by screen.state's non-machine branch: a not-live camera/window feed's own reason, else
        // the ctor-time fault. A machine-owning index's fault comes from Machines.State instead (see the outer
        // type's own State(int) composer).
        public string? CurrentFault() {
            if (
                (CameraSeat is { } cameraSeat) &&
                (Binder.CameraFaultFor(seat: cameraSeat, sensor: CameraSensorKind!.Value) is { } cameraFault)
            ) {
                return cameraFault;
            }

            if (
                (Capture is { Live: false } capture) &&
                (capture.Fault is { } captureFault)
            ) {
                return captureFault;
            }

            if (
                (Probe is { Live: false } probe) &&
                (probe.Fault is { } probeFault)
            ) {
                return probeFault;
            }

            return DeclaredFault;
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
            CameraSeat = null;
            CameraSensorKind = null;
            Probe = null;
            View = null;
        }
        // The current source for one submitted frame: the host's machine (if this index has one), else the highest-
        // precedence local producer's, else the declared jumbotron view's, authored QR, declared test pattern, or 0.
        // Only the shared-camera branch carries a retirement callback; every engine-owned/stable source is handle-only.
        public SdfScreenSourceFrame AcquireFrame() => (Machines.HasMachine(index: Index)
            ? Machines.Handle(index: Index)
            : ((CameraSeat is { } cameraSeat)
                ? Binder.AcquireCameraFrame(seat: cameraSeat, sensor: CameraSensorKind!.Value)
                : ((Probe is { } probe)
                    ? probe.AcquireFrame()
                    : ((Capture is { } capture)
                        ? capture.Handle()
                        : ((View is { } view)
                            ? view.Handle()
                            : ((Session is { } session)
                                ? session.Handle()
                                : ((Qr is { } qr)
                                    ? qr.Surface.CurrentHandle
                                    : (Pattern?.Surface.CurrentHandle ?? 0)
        )))))));
        // Diagnostic handle lookup only; unlike AcquireFrame it never submits GPU work and therefore does not acquire
        // an asynchronously-written camera slot.
        public nint Handle() => (Machines.HasMachine(index: Index)
            ? Machines.Handle(index: Index)
            : ((CameraSeat is { } cameraSeat)
                ? Binder.CameraHandleFor(seat: cameraSeat, sensor: CameraSensorKind!.Value)
                : ((Probe is { } probe)
                    ? probe.Handle()
                    : ((Capture is { } capture)
                        ? capture.Handle()
                        : ((View is { } view)
                            ? view.Handle()
                            : ((Session is { } session)
                                ? session.Handle()
                                : ((Qr is { } qr)
                                    ? qr.Surface.CurrentHandle
                                    : (Pattern?.Surface.CurrentHandle ?? 0)
        )))))));
        // The current emitted light, in the same precedence as Handle.
        public Vector3 Light() => (Machines.HasMachine(index: Index)
            ? Machines.Light(index: Index)
            : ((CameraSeat is { } cameraSeat)
                ? Binder.CameraLightFor(seat: cameraSeat, sensor: CameraSensorKind!.Value)
                : ((Probe is { } probe)
                    ? probe.Light
                    : ((Capture is { } capture)
                        ? capture.Light
                        : ((View is { } view)
                            ? view.Light()
                            : ((Session is { } session)
                                ? session.Light()
                                : ((Qr is { } qr)
                                    ? qr.Light
                                    : (Pattern?.Light ?? Vector3.Zero)
        )))))));
        // Drops the authored QR and disposes the upload surface it owns — the symmetric half of TryQr's acquire, run
        // whenever the slot stops showing that code (a re-author, or a declared source that no longer names one).
        public void ReleaseQr() {
            Qr?.Surface.Dispose();
            Qr = null;
        }
    }
}

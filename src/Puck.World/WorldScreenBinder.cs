using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Sources;
using Puck.Commands;
using Puck.Platform;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.SdfVm.Views;
using Puck.SignedDistance;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// Binds the world's declared <see cref="WorldScreen"/>s to their live GPU sources — the seam between the pure screen
/// data and the engine's per-index provider maps. Each declared screen owns a slot that can carry a registered image
/// producer's feed (<see cref="WorldImageProducers"/>: the test pattern, a QR code, the shared webcam, a desktop
/// capture, or any producer the host registers), a machine output, a jumbotron view, a probe output, a session view,
/// or nothing (the engine's procedural no-signal fallback). Every external image resolves through the capture gate
/// (<see cref="WorldCaptureGate"/>), so a capture shows its declared fill and never its pixels. A provider is registered for every declared index up front — returning the slot's current handle or 0 — so
/// a runtime <c>screen.source &lt;index&gt; camera</c>/<c>capture</c> binds without rebuilding the engine (the engine copies the
/// provider key set once but polls each provider live, and a 0 handle reads as unbound).
/// A shared singleton so the render factory, the screen verbs, and <c>world.screens</c> read one instance.
/// </summary>
/// <remarks>
/// This type is a pure reader of <see cref="Server.WorldMachineHost"/>'s outputs
/// (<see cref="Server.WorldMachineHost.Handle"/>/<see cref="Server.WorldMachineHost.Light"/> for the room), and
/// <see cref="Publish"/> calls <see cref="IMachineVideoOutput.PublishFrame"/> on the host's optional output — the one
/// GPU call this project makes on a machine's behalf, since <c>Puck.World.Server</c> cannot reach a GPU device
/// context. It also facades several read-only <see cref="WorldMachineHost"/> members (<c>HasMachine</c>,
/// <c>HasEngine</c>, <c>TryReadMachineInsert</c>, <c>TryMagazine</c>, <c>AudioMachine</c>, <c>TryPeek</c>,
/// <c>LinkOf</c>, <c>DescribeLinks</c>, <c>TryReadLinkMembers</c>) so presentation-side
/// callers (<c>PlayerCommandModule</c>, <c>WorldAudioDirector</c>,
/// <c>ScreenCommandModule</c>'s read-only verbs) reach the host's state through the same reference they
/// already hold. Machine lifecycle mutation (insert/eject/select/options/link/unlink) routes through
/// <c>ScreenCommandModule</c> submitting a <c>WorldScreenOp</c> through
/// <c>IServerLink.SubmitScreenOp</c> instead, landing in the ordered submission domain (see <c>WorldScreenOp</c>'s
/// own remarks). Producer, jumbotron-view, probe and session screen sources remain genuinely presentation-owned.
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
    // The quiet zone a live screen.source <index> qr uses when the verb names none.
    private const int QrDefaultQuietZoneModules = 4;
    // The seat a screen row, a probe export, or a console bind resolves a seat-relative camera against: none of
    // them carries an enclosing seat, so they film seat 1's view.
    private const int DefaultViewSeat = 1;

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
    // The authoritative screen-machine host — owns every booted IMachineRuntime; this binder reads its outputs
    // (Handle/Light/MachineAt) and facades several of its read-only members. Never mutated through here — see this
    // type's own remarks.
    private readonly WorldMachineHost m_machines;
    private readonly WorldStampPool m_stamps;
    private readonly WorldPerceptionAnchor m_perception;
    // Resolved lazily: the facts read the input router, which the composition root registers after this binder.
    private readonly Func<WorldOverlayFacts> m_facts;
    // Keeps external images out of captures; see WorldCaptureGate.
    private readonly WorldCaptureGate m_captureGate;
    // One uploaded 1x1 image per capture-fill color a filled external source resolves to, and the one delegate the gate
    // resolves fills through (cached so a resolve allocates nothing).
    private readonly Func<uint, GpuImageLease> m_fillImage;

    private readonly Dictionary<uint, CpuSurfaceSource> m_fills = new();
    // The producers every producer source opens through: the four the engine ships, then any the host registers.
    private readonly WorldImageProducers m_producers = new();

    // Whether the host exports shared Direct3D 12 surfaces: a Direct3D 12 host on a platform that has them.
    private readonly bool m_exportsSurfaces;
    // The backend-neutral surface-transfer factory — the Vulkan host's camera GPU tier imports its shared camera
    // targets through it (the D3D12 host samples its own resources directly and never calls it for the camera).
    // Null on a headless boot, which composes no presenter and never publishes.
    private readonly IGpuSurfaceTransferFactory? m_surfaceTransfers;
    private readonly INativeImageCaptureService m_windowCapture;

    // On the Vulkan host, the camera GPU tier's headless Direct3D 12 device the targets are allocated on — pinned to the render adapter's LUID so the
    // platform's D3D11 decode device and the Vulkan render device both reach the same physical memory. Each target set
    // made on it is a dependent, so retiring it disposes the device only once the last of their images is released.
    private DisposeAfterDependents<IDisposable>? m_cameraTargetDevice;

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

    private readonly CameraDeviceScanner m_cameraDeviceScanner;

    // Narrates a scan failure once per failure episode (ServiceCameraDevices) rather than every ~2s retry; cleared
    // the moment a scan succeeds again.
    private bool m_cameraDeviceScanFailed;
    // The world's placeable-camera rows — booted from the definition and REPLACED by ReconcileCameras when a camera
    // mutation delivers, so a runtime screen.source <index> view (and every later resolve) reads the LIVE rows.
    private IReadOnlyList<WorldCamera> m_cameras;
    private bool m_disposed;
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

    private DynamicTransform[] m_viewTransforms = [];
    private readonly Dictionary<int, ScreenSlot> m_slots = new();
    // The screen indices declared at BOOT (construction) — the render engine's frozen provider key set, copied
    // ONCE and never grown. Distinct from m_slots.Keys, which shrinks/grows as ReconcileScreens removes/recreates
    // entries: an index in this set can always have its m_slots/m_sources/m_lights entries safely RECREATED after
    // removal (the engine's own frozen key list still names it), while a genuinely new index (never in this set)
    // still cannot bind live.
    private readonly HashSet<int> m_bootScreenIndices = new();
    private readonly Dictionary<int, Func<GpuImageLease>> m_sources = new();
    private readonly Dictionary<int, Func<Vector3>> m_lights = new();
    // One publication per named producer output, even when several screens fan out from it.
    private readonly HashSet<(string Instance, string Output)> m_publishedMachineOutputs = new();
    // Every producer output this binder has published on the current device; RetireMachineOutputs releases their uploads.
    private readonly PublishedMachineOutputs m_presentedMachineOutputs = new();
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
    private readonly HashSet<string> m_parkedViews = new(comparer: StringComparer.Ordinal);
    // Reused scratch for ReconcileCameras (the registered names snapshot walked while m_cameraViews mutates).
    private readonly List<string> m_cameraReconcileScratch = new();
    // A jumbotron is a diegetic 160x144 display, not another full-rate player view. ViewStack already persists the last
    // resolved handle when a budgeted view is skipped; this countdown deliberately spends the offscreen SDF render only
    // once every N produced frames. Frame-count cadence is deterministic and avoids introducing a wall clock.
    private int m_viewRefreshDivisor = 4;

    /// <summary>Initializes the binder over the world's declared screens: each producer source opens its feed through the
    /// registered producer (an absent camera or an unopenable window leaves the slot unbound and the fault visible in
    /// <c>world.screens</c>/<c>screen.state</c> — loud data, no crash), plus a source + light provider for every
    /// declared index. A declared machine screen
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
    /// <param name="perception">The per-seat perception anchor a seat-relative camera anchor resolves through.</param>
    /// <param name="facts">The overlay facts a ranked camera anchor list selects through (resolved on first use).</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 — selects the GPU capture transport for
    /// window/monitor captures (the Vulkan host keeps their CPU-pixel path). The shared camera rides its GPU tier on
    /// both hosts; this flag only picks how its shared targets are allocated and sampled.</param>
    /// <param name="instanceHost">The process's running world instances — a session-sourced face's resolved
    /// destination instance is found or started here.</param>
    /// <param name="roster">The player roster — resolves a seat to its bound camera device.</param>
    /// <param name="renderProbe">The render probe each offscreen view's GPU work is registered with while the view
    /// is registered, so <c>world.counters gpu</c> reports it, and whose render root says whether a capture is armed;
    /// <see langword="null"/> when nothing reads it (a headless boot).</param>
    /// <param name="alwaysFillsCaptures">Whether every frame is a capture frame: an offscreen host, which serves
    /// captures and <c>puck parity</c>, so external content never reaches any frame it produces.</param>
    /// <param name="producers">The image producers the host registers beside the four the engine ships, or
    /// <see langword="null"/> for none; each must match a shape in <see cref="WorldImageProducerVocabulary"/>.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldScreenBinder(IReadOnlyList<WorldScreen> screens, WorldMachineHost machines, ICameraCaptureService cameraCapture, INativeImageCaptureService windowCapture, IGpuSurfaceTransferFactory? surfaceTransfers, IReadOnlyList<WorldCamera> cameras, ISdfAnchorSource anchors, WorldStampPool stamps, WorldPerceptionAnchor perception, Func<WorldOverlayFacts> facts, bool hostsOnDirectX, WorldInstanceHost instanceHost, PlayerRoster roster, WorldRenderProbe? renderProbe = null, bool alwaysFillsCaptures = false, IReadOnlyList<IWorldImageProducer>? producers = null) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        m_renderProbe = renderProbe;
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentNullException.ThrowIfNull(argument: cameraCapture);
        ArgumentNullException.ThrowIfNull(argument: windowCapture);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: anchors);
        ArgumentNullException.ThrowIfNull(argument: stamps);
        ArgumentNullException.ThrowIfNull(argument: perception);
        ArgumentNullException.ThrowIfNull(argument: facts);
        ArgumentNullException.ThrowIfNull(argument: instanceHost);
        ArgumentNullException.ThrowIfNull(argument: roster);

        m_machines = machines;
        m_cameraCapture = cameraCapture;
        m_cameraDeviceScanner = new CameraDeviceScanner(
            cameraCapture,
            TimeSpan.FromSeconds(seconds: 2)
        );
        m_surfaceTransfers = surfaceTransfers;
        m_windowCapture = windowCapture;
        m_cameras = cameras;
        m_anchors = anchors;
        m_stamps = stamps;
        m_perception = perception;
        m_facts = facts;
        m_hostsOnDirectX = hostsOnDirectX;
        m_instanceHost = instanceHost;
        m_roster = roster;
        // Windows-10240 guarded because DirectXGpuSurfaceExportFactory is platform-attributed; hostsOnDirectX already
        // implies that floor (Program.cs rejects the D3D12 backend below it), so the check only satisfies the analyzer.
        m_exportsSurfaces = (hostsOnDirectX && OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        ));
        m_seatCameraControls = ResolveSeatCameraControls(screens: screens);
        m_captureGate = new WorldCaptureGate(
            alwaysFills: alwaysFillsCaptures,
            captureArmed: () => (m_renderProbe?.Render?.PendingCapturePath is not null)
        );
        m_fillImage = FillImage;
        m_producers.Register(producer: new WorldTestPatternProducer());
        m_producers.Register(producer: new WorldQrProducer());
        m_producers.Register(producer: new CameraProducer(binder: this));
        m_producers.Register(producer: new CaptureProducer(binder: this));

        foreach (var producer in (producers ?? [])) {
            m_producers.Register(producer: producer);
        }

        foreach (var screen in screens) {
            _ = m_bootScreenIndices.Add(item: screen.Index);

            var slot = new ScreenSlot { Binder = this, DeclaredSource = screen.Source, Index = screen.Index, MachineSource = (screen.Source as WorldScreenSource.Machine), Machines = m_machines };

            switch (screen.Source) {
                case WorldScreenSource.Producer producer:
                    // A producer the host cannot open (no webcam, a window capture on a platform without one) leaves
                    // the slot unbound with its fault visible in world.screens/screen.state — loud data, no crash.
                    OpenProducer(
                        slot: slot,
                        source: producer
                    );

                    break;
                case WorldScreenSource.Machine:
                    // WorldMachineHost already booted this index (if it could) at ITS OWN construction — nothing to
                    // do here; Handle()/Light() below read the host directly for a machine-owning index.
                    break;
                case WorldScreenSource.View view:
                    // The declared jumbotron: resolve its camera name against the world's placeable cameras. An unknown
                    // name is a loud fault (unbound); a known one holds a ViewFeed whose ViewStack registration is
                    // deferred to ConfigureViews (the offscreen render envelope is not known until the frame source has
                    // probed it).
                    if (ResolveCamera(name: view.CameraName) is { } camera) {
                        slot.View = new ViewFeed(name: WorldSeatAnchors.RegistrationName(
                            camera: camera,
                            seat: DefaultViewSeat
                        ));
                    } else {
                        slot.DeclaredFault = $"camera '{view.CameraName}' not declared";
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
    public IReadOnlyDictionary<int, Func<GpuImageLease>> ScreenSources => m_sources;
    /// <summary>Gets the current produced-frame divisor for jumbotron offscreen renders.</summary>
    public int ViewRefreshDivisor => m_viewRefreshDivisor;

    // Apply one NON-MACHINE source through the runtime machinery — shared by the reconcile-side declared-source
    // change and ApplyNonMachineSource (screen.select's non-machine branch). A Machine source drops any local
    // presentation producer (Server.WorldMachineHost owns the machine itself) but is otherwise a no-op arm.
    //
    // Every transition away from View clears the slot's jumbotron reference and releases the camera registration
    // when no surviving slot films it; a View->View re-point releases the previously-registered camera inside
    // TryView. A slot whose source is no longer a producer drops its declared producer feed the same way.
    private (bool Ok, string Message) ApplySource(int index, ScreenSlot slot, WorldScreenSource source) {
        slot.MachineSource = (source as WorldScreenSource.Machine);

        var outcome = source switch {
            WorldScreenSource.None => (slot.HasLive
            ? TryEject(index: index)
            : (Ok: true, Message: $"screen {index} unbound")),
            WorldScreenSource.Machine => (slot.HasLive
            ? TryEject(index: index)
            : (Ok: true, Message: $"screen {index} machine (host-owned)")),
            WorldScreenSource.Producer producer => ApplyProducer(
            index: index,
            slot: slot,
            source: producer
        ),
            WorldScreenSource.View view => ApplyViewChange(
            index: index,
            slot: slot,
            view: view
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

        if (source is not WorldScreenSource.Producer) {
            slot.ReleaseDeclared();
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
    // Releases the upload every producer output this binder published holds on the current device. An instance removed
    // since it was published resolves to nothing, because its host released the upload when the instance went.
    // Retires the Vulkan camera route's headless device after the feeds retired their target sets: it is disposed once
    // the last image made on it is released, which a submitted frame's lease can defer.
    private void RetireCameraTargetDevice() {
        m_cameraTargetDevice?.Retire();
        m_cameraTargetDevice = null;
    }
    private void RetireMachineOutputs() =>
        m_presentedMachineOutputs.Retire(resolve: (instance, output) => m_machines.VideoOutput(
            instance: instance,
            output: output
        ));

    /// <summary>Applies a non-machine magazine entry (a producer, a view, a probe, a session, text, or none) as a screen's live
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

        // The machines and links belong to Server.WorldMachineHost, which outlives the render device; only the
        // uploads this binder published on that device are released here.
        RetireMachineOutputs();

        foreach (var slot in m_slots.Values) {
            slot.DeclaredFeed?.Dispose();
            slot.LiveFeed?.Dispose();
            slot.Session?.Dispose();
        }

        foreach (var fill in m_fills.Values) {
            fill.Dispose();
        }

        m_fills.Clear();

        DisposeCamera();
        ReleaseProbeFeeds();
        DisposeViewExports();
        DisposeFrameCaptures();
        RetireCameraTargetDevice();
        UnregisterAllViewWork();
        m_viewStack?.Dispose();
        m_viewStack = null;
    }
    /// <summary>Drops every device-owned upload, shared target ring and offscreen view while preserving CPU sessions,
    /// machine simulation, declarations, probe requests and view registrations. The next publish/render recreates
    /// resources on the replacement device.</summary>
    public void NotifyDeviceLost() {
        if (m_disposed) {
            return;
        }

        RetireMachineOutputs();

        foreach (var slot in m_slots.Values) {
            slot.DeclaredFeed?.NotifyDeviceLost();
        }

        foreach (var fill in m_fills.Values) {
            fill.NotifyDeviceLost();
        }

        CameraDeviceLost();
        ReleaseProbeFeeds();

        // The Vulkan camera route's headless D3D12 device and the cached render LUID describe the old render adapter; the
        // next Publish reads the replacement renderer's LUID, which may identify a different physical adapter.
        RetireCameraTargetDevice();
        m_renderAdapterLuid = null;

        foreach (var slot in m_slots.Values) {
            slot.LiveFeed?.NotifyDeviceLost();
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
    /// procedural fallback until the next boot, since the render engine's provider key set cannot grow.
    /// Not migrated onto <c>Puck.World.Client.KeyedReconciler</c> — the removal, source-change, and boot-index
    /// re-declaration passes interleave lifetime rules the generic shape cannot express.</summary>
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
    /// <summary>Clears a screen's live local producer — the runtime <c>screen.eject</c> path — for any live feed this
    /// binder itself owns (the webcam, a window capture, any external producer, a probe output). Ejecting a machine is
    /// <c>ScreenCommandModule</c>'s <c>WorldScreenOp.Eject</c> submission instead (see this type's own remarks). The slot
    /// reverts to its declared producer feed (a test pattern, a QR code) or to unbound (the procedural fallback). Fails for an undeclared screen or a slot with no live
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

    // The delegate indirection cell: ResolveFrame/ResolveLight are the stable delegate targets m_sources/m_lights
    // register. A cell is created once per boot-declared index and never replaced; only its Slot field is ever
    // reassigned (by ReconcileScreens, when a removed index is re-declared), so the renderer's one-time copy of
    // ResolveFrame/ResolveLight keeps reading whichever ScreenSlot is current.
    private sealed class ScreenSourceCell {
        public required ScreenSlot Slot { get; set; }

        public GpuImageLease ResolveFrame() => Slot.AcquireFrame();
        public Vector3 ResolveLight() => Slot.Light();
    }
    // One declared screen's slot: at most one declared producer feed (a test pattern, a QR code — anything not
    // external, which survives an eject), at most one live feed (the shared webcam, a window capture, any external
    // producer — what an eject clears), and the typed machine, probe, view and session paths. A machine-owning index
    // carries no local producer here (Server.WorldMachineHost owns it); every read checks Machines first. Every
    // external image is resolved through the binder's capture gate, so a capture shows the fill instead. A mutable class
    // so the producer references flip in place with no engine rebuild.
    private sealed class ScreenSlot {
        public required WorldScreenBinder Binder { get; init; }
        // The declared producer feed: a producer whose content is not external.
        public IWorldImageFeed? DeclaredFeed { get; set; }
        // The ctor-time or bind-time fault (a producer that would not open, an unknown view camera); a live feed's own
        // fault is read from the feed instead (see CurrentFault). Machine faults are Machines.State's concern.
        public string? DeclaredFault { get; set; }
        // The WorldScreenSource this slot currently reflects — set at construction and updated by ReconcileScreens, so a
        // live UpsertScreen only re-applies its source through the runtime machinery when the source actually changed.
        public WorldScreenSource? DeclaredSource { get; set; }
        // Whether a live (ejectable) local producer is bound — an external feed or a probe output (a machine is never
        // local state on this slot).
        public bool HasLive => ((LiveFeed is not null) || (Probe is not null));
        public required int Index { get; init; }
        // The live feed: a producer whose content is external.
        public IWorldImageFeed? LiveFeed { get; set; }
        public WorldScreenSource.Machine? MachineSource { get; set; }
        // The authoritative screen-machine host — consulted FIRST by Handle()/Light()/CurrentFault() for this slot's
        // index, before any locally-owned producer.
        public required WorldMachineHost Machines { get; init; }
        public ProbeFeed? Probe { get; set; }
        public SessionFeed? Session { get; set; }
        // The live decal-text source (declared row or a live source change) — no producer, no handle: the decal tier
        // reads it back through TextSourceAt instead of the source table.
        public WorldScreenSource.Text? Text { get; set; }
        public ViewFeed? View { get; set; }

        private IMachineVideoOutput? MachineOutput() => ((MachineSource is { } source)
            ? Machines.VideoOutput(
                instance: source.Instance,
                output: source.Output
            )
            : null
        );

        // The current source for one submitted frame: the host's machine (if this index has one), else the live feed,
        // the probe output, the jumbotron view, the session view, or the declared feed, else 0. A probe output is
        // external content: it processes a camera's frames.
        public GpuImageLease AcquireFrame() {
            if (MachineOutput() is { } machine) {
                return machine.NativeImageViewHandle;
            }

            if (LiveFeed is { } live) {
                return Binder.Resolve(feed: live);
            }

            if (Probe is { } probe) {
                return (Binder.FillsExternal
                    ? Binder.FillImage(rgba: ImageSourceDescriptor.DefaultCaptureFill)
                    : probe.AcquireFrame()
                );
            }

            if (View is { } view) {
                return view.Handle();
            }

            if (Session is { } session) {
                return session.Handle();
            }

            return ((DeclaredFeed is { } declared)
                ? Binder.Resolve(feed: declared)
                : 0
            );
        }
        // Clears the live local producer (webcam, capture, probe) and reverts to the declared feed or to unbound. The
        // webcam feed itself is shared and outlives this slot's reference; a window capture is per-slot and disposed.
        public void ClearLive() {
            LiveFeed?.Dispose();
            LiveFeed = null;
            Probe = null;
            DeclaredFault = null;
        }
        // The fault surfaced by screen.state's non-machine branch: a live feed's own reason, else a not-live probe's,
        // else the declared feed's, else the bind-time fault. A machine-owning index's fault comes from Machines.State
        // instead (see the outer type's own State(int) composer).
        public string? CurrentFault() {
            if (LiveFeed?.Fault is { } liveFault) {
                return liveFault;
            }

            if (
                (Probe is { Live: false } probe) &&
                (probe.Fault is { } probeFault)
            ) {
                return probeFault;
            }

            return (DeclaredFeed?.Fault ?? DeclaredFault);
        }
        // Disposes everything this slot owns when the slot is removed entirely (a RemoveScreen mutation). The shared
        // webcam feed and the boot-sized offscreen view pool are not owned by a slot (the binder disposes them once),
        // so only their references drop.
        public void DisposeOwned() {
            ReleaseDeclared();
            LiveFeed?.Dispose();
            LiveFeed = null;
            Probe = null;
            View = null;
        }
        // Diagnostic handle lookup only; unlike AcquireFrame it never submits GPU work and therefore does not acquire
        // an asynchronously-written camera slot. It is also what a jumbotron's own render binds for this screen, so it
        // resolves through the capture gate exactly as AcquireFrame does.
        public nint Handle() {
            if (MachineOutput() is { } machine) {
                return machine.NativeImageViewHandle;
            }

            if (LiveFeed is { } live) {
                return Binder.ResolveHandle(feed: live);
            }

            if (Probe is { } probe) {
                return (Binder.FillsExternal
                    ? Binder.FillImage(rgba: ImageSourceDescriptor.DefaultCaptureFill).ImageViewHandle
                    : probe.Handle()
                );
            }

            if (View is { } view) {
                return view.Handle();
            }

            if (Session is { } session) {
                return session.Handle();
            }

            return ((DeclaredFeed is { } declared)
                ? Binder.ResolveHandle(feed: declared)
                : 0
            );
        }
        // The current emitted light, in the same precedence as Handle; a filled external image lights the room with
        // its fill.
        public Vector3 Light() {
            if (MachineOutput() is { } machine) {
                return machine.EmittedLight;
            }

            if (LiveFeed is { } live) {
                return Binder.ResolveLight(feed: live);
            }

            if (Probe is { } probe) {
                return (Binder.FillsExternal
                    ? WorldImageLight.OfFill(rgba: ImageSourceDescriptor.DefaultCaptureFill)
                    : probe.Light
                );
            }

            if (View is { } view) {
                return view.Light();
            }

            if (Session is { } session) {
                return session.Light();
            }

            return ((DeclaredFeed is { } declared)
                ? Binder.ResolveLight(feed: declared)
                : Vector3.Zero
            );
        }
        // Drops the declared producer feed and disposes what it owns — run whenever the slot stops showing it (a
        // re-author, or a declared source that is no longer a producer).
        public void ReleaseDeclared() {
            DeclaredFeed?.Dispose();
            DeclaredFeed = null;
        }
    }
}

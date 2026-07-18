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

/// <summary>
/// Binds the world's declared <see cref="WorldScreen"/>s to their live GPU sources — the seam between the pure screen
/// DATA and the engine's per-index provider maps. Each declared screen owns a slot that can carry a CPU-fed test pattern,
/// a booted deterministic machine resolved against a registered <see cref="IScreenMachineEngine"/> (declared with present
/// content, or runtime-inserted), a live webcam feed, a desktop-window capture feed, or nothing (the engine's procedural
/// no-signal fallback). A provider is registered for EVERY declared index up front — returning the slot's current handle
/// or 0 — so a runtime <c>screen.insert</c>/
/// <c>screen.camera</c>/<c>screen.capture</c> binds without rebuilding the engine (the engine copies the provider KEY SET
/// once but polls each provider live, and a 0 handle reads as unbound). <see cref="WorldSimulation"/> steps machines once
/// per exact host tick with that tick's OR-merged engagement input; each produced frame this binder uploads the latest
/// machine/pattern image and pulls live feeds on each source's declared engine-tick cadence.
/// A shared singleton so the render factory, the screen verbs, and <c>world.screens</c> read one instance.
/// </summary>
/// <remarks>An unbound slot (a <see cref="WorldScreenSource.None"/> screen, a machine that has been ejected, or a live feed
/// with no signal) registers a provider returning 0, so the engine leaves its surface unbound and lights it with the
/// procedural no-signal fallback — never black. ONE webcam session is opened engine-wide and SHARED by every camera
/// screen (two sessions on one physical device flicker), so N camera screens sample one feed. Single-threaded:
/// <see cref="AdvanceMachines"/>, <see cref="Publish"/>, and simulation-routed screen mutations all run on the
/// launcher's window-pump thread, so no lock guards this state.</remarks>
internal sealed class WorldScreenBinder : IDisposable {
    private const ulong PublishTimingReportInterval = 60UL;

    private readonly record struct ScreenPublishTiming(long CameraTicks, long MachineTicks, long WindowCaptureTicks, long PatternTicks) {
        public long TotalTicks => CameraTicks + MachineTicks + WindowCaptureTicks + PatternTicks;
    }

    private readonly WorldEngagement m_engagement;
    private readonly ICameraCaptureService m_cameraCapture;
    private readonly INativeImageCaptureService m_windowCapture;
    // The D3D12-host GPU capture transport: on the Direct3D 12 host, window/monitor captures publish GPU-side into shared
    // simultaneous-access textures the screens sample directly (no CPU round trip); the Vulkan host keeps the CPU path.
    // The factory is non-null only on the D3D12 host, and the render adapter LUID is resolved once from the render device
    // context at the first publish (the device does not exist at construction), so capture feeds open on the render GPU.
    private readonly bool m_hostsOnDirectX;
    private readonly DirectXGpuSurfaceExportFactory? m_surfaceExport;
    private long? m_renderAdapterLuid;
    private readonly IReadOnlyList<WorldCamera> m_cameras;
    // The anchor source for avatar-eye cameras (the client's snapshot-fed entity view). Anchor ids are entity indices,
    // so an AvatarEye view follows the same interpolated render pose the main world draws without reaching into
    // simulation state or duplicating pose math here.
    private readonly ISdfAnchorSource m_anchors;
    // The registered screen-machine engines by id (ordinal) — the composition root's DI-collected IScreenMachineEngine
    // set. A declared or inserted machine resolves its engine against this; the sole registered engine is the mechanical
    // default when an insert omits one.
    private readonly Dictionary<string, IScreenMachineEngine> m_engines;
    private readonly Dictionary<int, ScreenSlot> m_slots = new();
    private readonly Dictionary<int, Func<nint>> m_sources = new();
    private readonly Dictionary<int, Func<Vector3>> m_lights = new();
    // Reused scratch for ReconcileScreens' removal pass, so a screen mutation collects the vanished indices without
    // allocating and never mutates m_slots while enumerating it.
    private readonly List<int> m_reconcileRemovals = new();
    // The offscreen view pool backing the View (jumbotron) screens — created by ConfigureViews once the render envelope
    // is known, null until then (and forever when the world declares no View screen). The view config the pool needs is
    // stashed alongside so a runtime screen.view can register against the same envelope.
    private ViewStack? m_viewStack;
    // Persistent SdfCameraView instances by camera name — a camera view owns a real GPU resource (its offscreen engine),
    // so a re-point to an already-registered camera reuses the SAME instance rather than constructing a fresh one (which
    // would orphan the built engine). Mirrors the overworld's m_cameraViews.
    private readonly Dictionary<string, SdfCameraView> m_cameraViews = new(comparer: StringComparer.Ordinal);
    private IServiceProvider? m_viewServices;
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

    /// <summary>Initializes the binder over the world's declared screens: a CPU feed for each test-pattern screen, a
    /// booted machine for each declared machine screen whose content file exists, the shared webcam for each camera
    /// screen, and a window-capture session for each capture screen (missing content / absent camera / unopenable window leaves the slot
    /// unbound and the fault visible in <c>world.screens</c>/<c>screen.state</c> — loud data, no crash), plus a source +
    /// light provider for every declared index.</summary>
    /// <param name="screens">The world's diegetic screens (<see cref="WorldDefinition.Screens"/>).</param>
    /// <param name="engagement">The engagement route the machines pull their per-tick controller image from.</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) a declared or inserted machine resolves against.</param>
    /// <param name="cameraCapture">The platform webcam service (CPU tier) the camera screens share one session of.</param>
    /// <param name="windowCapture">The platform compositor window-capture service.</param>
    /// <param name="cameras">The world's placeable cameras a View (jumbotron) screen resolves its camera name against.</param>
    /// <param name="anchors">The entity anchor source used by avatar-eye cameras (the client's snapshot-fed view).</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 — selects the GPU capture transport for
    /// window/monitor captures (the Vulkan host keeps the CPU-pixel path). Camera capture stays CPU on both.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldScreenBinder(IReadOnlyList<WorldScreen> screens, WorldEngagement engagement, IEnumerable<IScreenMachineEngine> engines, ICameraCaptureService cameraCapture, INativeImageCaptureService windowCapture, IReadOnlyList<WorldCamera> cameras, ISdfAnchorSource anchors, bool hostsOnDirectX) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: engagement);
        ArgumentNullException.ThrowIfNull(argument: engines);
        ArgumentNullException.ThrowIfNull(argument: cameraCapture);
        ArgumentNullException.ThrowIfNull(argument: windowCapture);
        ArgumentNullException.ThrowIfNull(argument: cameras);
        ArgumentNullException.ThrowIfNull(argument: anchors);

        m_engagement = engagement;
        m_cameraCapture = cameraCapture;
        m_windowCapture = windowCapture;
        m_cameras = cameras;
        m_anchors = anchors;
        m_hostsOnDirectX = hostsOnDirectX;
        // Windows-10240 guarded because DirectXGpuSurfaceExportFactory is platform-attributed; hostsOnDirectX already
        // implies that floor (Program.cs rejects the D3D12 backend below it), so the check only satisfies the analyzer.
        m_surfaceExport = ((hostsOnDirectX && OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 10240))
            ? new DirectXGpuSurfaceExportFactory()
            : null);
        m_engines = new Dictionary<string, IScreenMachineEngine>(comparer: StringComparer.Ordinal);
        var sharedCameraProfile = ResolveSharedCameraProfile(screens: screens);

        foreach (var engine in engines) {
            m_engines[engine.Id] = engine;
        }

        foreach (var screen in screens) {
            var slot = new ScreenSlot { Index = screen.Index, DeclaredSource = screen.Source };

            switch (screen.Source) {
                case WorldScreenSource.TestPattern pattern:
                    slot.Pattern = new PatternFeed(
                        pattern: new TestPatternSource(width: pattern.Width, height: pattern.Height),
                        surface: new CpuSurfaceSource()
                    );

                    break;
                case WorldScreenSource.Machine machine:
                    // The declared-data boot: a present content file assembles the machine through its declared engine at
                    // wiring; a missing file or an unknown engine id leaves the slot unbound with a visible fault.
                    BootDeclaredMachine(slot: slot, machine: machine);

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
                default:
                    // None: no producer — the provider returns 0 (procedural fallback).
                    break;
            }

            m_slots[screen.Index] = slot;
            m_sources[screen.Index] = slot.Handle;
            m_lights[screen.Index] = slot.Light;
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
            slot.Machine?.NotifyDeviceLost();
            slot.Pattern?.Surface.NotifyDeviceLost();
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

    /// <summary>Whether a machine is currently booted on the screen index — the guard the <c>player.engage</c> verb
    /// checks before routing input (a screen with no machine has nothing to control).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public bool HasMachine(int index) => (m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is not null));

    /// <summary>Reads back the live machine insert on a screen index — its engine id, content path, and options — so
    /// <c>world.save</c> can fold a runtime <c>screen.insert</c> into that screen row's <see cref="WorldScreenSource.Machine"/>
    /// source (§2.1 session write-back). Returns <see langword="false"/> when the slot has no booted machine or the
    /// booting content path is unknown (a producer that was not an insert), leaving the screen's declared source untouched.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="engine">The engine id that booted the live machine (as supplied to the insert, else the resolved default).</param>
    /// <param name="contentPath">The content file (a cartridge ROM) the live machine booted.</param>
    /// <param name="options">The options string the live machine booted with, or <see langword="null"/>.</param>
    public bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options) {
        if (m_slots.TryGetValue(key: index, value: out var slot) &&
            (slot.Machine is not null) &&
            (slot.MachineContentPath is { } path) &&
            (slot.MachineSourceEngine is { } engineId)) {
            engine = engineId;
            contentPath = path;
            options = slot.MachineOptions;

            return true;
        }

        engine = string.Empty;
        contentPath = string.Empty;
        options = null;

        return false;
    }

    /// <summary>Whether a screen-machine engine is registered under <paramref name="engineId"/> — the check the
    /// <c>screen.insert</c> verb uses to tell an optional engine id from an options token.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    public bool HasEngine(string engineId) => m_engines.ContainsKey(key: engineId);

    /// <summary>The live state of a declared screen for <c>screen.state</c>, or <see langword="null"/> when the index is
    /// not a declared screen.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldScreenState? State(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return null;
        }

        var queued = (slot.Machine as IQueuedScreenMachine);

        return new WorldScreenState(
            Assigned: (slot.Machine is not null),
            Engine: slot.MachineEngine,
            Handle: slot.Handle(),
            FramesStepped: (queued?.CompletedSteps ?? slot.FramesStepped),
            PendingSteps: (queued?.PendingSteps ?? 0L),
            MaximumPendingSteps: (queued?.MaximumPendingSteps ?? 0),
            BackpressureEvents: (queued?.BackpressureEvents ?? 0L),
            Fault: (queued?.QueueFault ?? slot.CurrentFault())
        );
    }

    /// <summary>Reads one memory byte from a screen's machine (the <c>screen.peek</c> read) — a side-effect-free host
    /// poll through the machine's optional <see cref="IMachineMemoryPeek"/> capability, never a write into machine state.
    /// Reports (loudly) whether a machine was present and whether it supports the peek: an assigned machine without the
    /// capability fails distinctly from an empty slot.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>A success flag and, on failure, a message; on success the message is empty.</returns>
    public (bool Ok, string Message) TryPeek(int index, int address, out byte value) {
        value = 0;

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Machine is not { } machine) {
            return (Ok: false, Message: $"screen {index} has no machine to read");
        }

        if (machine is not IMachineMemoryPeek peek) {
            return (Ok: false, Message: $"screen {index}'s machine does not support memory peek");
        }

        value = peek.PeekByte(address: address);

        return (Ok: true, Message: "");
    }

    /// <summary>Boots (or live-swaps) a machine onto a declared screen from a content file path — the runtime
    /// <c>screen.insert</c> path, the same boot code the declared-data path runs. The engine is resolved by
    /// <paramref name="engineId"/>, or, when it is <see langword="null"/>, defaults to the sole registered engine (a
    /// mechanical default, not a decision). Any existing producer on the slot (a machine, a webcam, a window capture) is
    /// cleared and replaced. Fails loudly (a message, no crash) for an undeclared screen, an unresolved engine, an
    /// unreadable content file, or an options string the engine rejects.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="contentPath">The content file (a cartridge ROM) to boot.</param>
    /// <param name="engineId">The screen-machine engine id, or <see langword="null"/> for the sole-registered default.</param>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    /// <returns>Whether the insert succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryInsert(int index, string contentPath, string? engineId, string? options) {
        if (m_disposed) {
            return (Ok: false, Message: "binder disposed");
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (!TryResolveEngine(engineId: engineId, engine: out var engine, error: out var engineError)) {
            return (Ok: false, Message: engineError);
        }

        if (!TryReadContent(contentPath: contentPath, content: out var content, fault: out var fault)) {
            return (Ok: false, Message: fault!);
        }

        IScreenMachine created;

        try {
            created = engine.Create(options: options, contentBytes: content, savePath: null);
        } catch (ArgumentException exception) {
            return (Ok: false, Message: exception.Message);
        }

        slot.ClearLive();
        slot.Machine = created;
        slot.MachineEngine = engine.Id;
        // Remember what booted this machine so world.save can fold a live insert back into the screen row's Machine
        // source (§2.1 session write-back). The engine id argument is preserved verbatim when supplied (so a declared
        // machine round-trips its authored id); a bare screen.insert records the resolved default id.
        slot.MachineContentPath = contentPath;
        slot.MachineSourceEngine = ((engineId is { Length: > 0 }) ? engineId : engine.Id);
        slot.MachineOptions = options;
        slot.DeclaredFault = null;
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} booted {engine.Id} '{Path.GetFileName(path: contentPath)}'{(string.IsNullOrWhiteSpace(value: options) ? "" : $" ({options})")}");
    }

    /// <summary>Binds a declared screen to the shared live webcam feed — the runtime <c>screen.camera</c> path. Any
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
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} showing the webcam");
    }

    /// <summary>Binds a declared screen to a live desktop-window capture keyed by a title fragment — the runtime
    /// <c>screen.capture</c> path. Any existing producer on the slot is cleared first. The capture rebinds each grab, so
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
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} capturing '{windowTitle}'");
    }

    /// <summary>Binds a declared screen to a live whole-monitor capture keyed by index — the runtime <c>screen.desktop</c>
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
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} capturing monitor {monitorIndex}");
    }

    /// <summary>Clears a screen's live producer — the runtime <c>screen.eject</c> path, for ANY producer kind (a machine,
    /// the webcam, a window capture). The slot reverts to its declared test pattern or to unbound (the procedural
    /// fallback). Fails for an undeclared screen or a slot with no live producer to clear.</summary>
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
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} ejected");
    }

    /// <summary>Reconciles the binder's runtime source machinery to a mutated screen list — the live-application half of
    /// an <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called by the frame source when the definition
    /// revision moves. REMOVALS are reconciled first: a slot whose index is no longer declared has any engaged player
    /// disengaged (their avatar resumes normal intent), its OWNED machine/pattern/capture state disposed, and its
    /// entries dropped from <c>m_slots</c>/<c>m_sources</c>/<c>m_lights</c> — so a removed screen stops advancing,
    /// publishing, and answering screen commands (the shared webcam session and the boot-sized view POOL are NOT
    /// disposed here — the binder owns their lifetime). A removed <c>View</c> screen additionally releases its camera's
    /// offscreen render when no surviving slot still films that camera (§CR-3: the orphaned <see cref="ViewStack"/> entry
    /// is disposed so it stops consuming refresh budget), while a camera two jumbotrons share stays live for the
    /// survivor. Then, for a declared index whose source CHANGED, it re-applies
    /// the new source through the same insert/eject/camera/capture/view machinery a <c>screen.*</c> verb uses
    /// (best-effort — a failed bind logs a loud line, never throws). Screen SLAB geometry (adds/moves/removes) rides the
    /// program rebuild in the frame source, not this method. Capacity honesty: an index the binder has no slot for was
    /// added past the boot provider key set the engine froze, so its source cannot bind live — its slab renders the
    /// procedural fallback until the next boot.</summary>
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
        // camera view no remaining slot references is released (§CR-3). Null (the common case) when no View screen was
        // removed, so a plain screen removal allocates nothing.
        HashSet<string>? removedViewCameras = null;

        foreach (var index in m_reconcileRemovals) {
            // Disengage any player routed here BEFORE the slot is gone, so their avatar resumes normal intent rather
            // than being held idle against a machine that no longer exists.
            m_engagement.DisengageScreen(screenIndex: index);

            if (m_slots.Remove(key: index, value: out var slot)) {
                // Note the camera a removed View screen filmed BEFORE DisposeOwned drops the reference, so its offscreen
                // render can be released once the whole removal pass has updated m_slots (a name shared by another
                // surviving jumbotron must NOT be released).
                if (slot.View is { } view) {
                    (removedViewCameras ??= new HashSet<string>(comparer: StringComparer.Ordinal)).Add(item: view.Name);
                }

                slot.DisposeOwned();
            }

            _ = m_sources.Remove(key: index);
            _ = m_lights.Remove(key: index);
            Console.Error.WriteLine(value: $"[world.screen: {index} removed — slot disposed]");
        }

        if (removedViewCameras is not null) {
            ReleaseOrphanedCameraViews(candidates: removedViewCameras);
        }

        foreach (var screen in screens) {
            if (m_slots.TryGetValue(key: screen.Index, value: out var slot) is false) {
                Console.Error.WriteLine(value: $"[world.screen: {screen.Index} added — its source applies at next boot (render provider key set frozen at boot)]");

                continue;
            }

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

    // Apply one screen's changed source through the runtime machinery. Each Try* already faults loudly rather than
    // throwing; a test-pattern or unconfigured-machine source has no runtime setter, so it takes effect at the next boot.
    private void ApplySourceChange(int index, ScreenSlot slot, WorldScreenSource source) {
        var outcome = source switch {
            WorldScreenSource.None => (slot.HasLive ? TryEject(index: index) : (Ok: true, Message: $"screen {index} unbound")),
            WorldScreenSource.Machine { ContentPath: { Length: > 0 } path } machine => TryInsert(index: index, contentPath: path, engineId: machine.Engine, options: machine.Options),
            WorldScreenSource.Machine => (Ok: false, Message: $"screen {index} machine unconfigured — no content path (applies at next boot)"),
            WorldScreenSource.Camera => TryCamera(index: index),
            WorldScreenSource.Capture { MonitorIndex: { } monitorIndex } => TryDesktop(index: index, monitorIndex: monitorIndex),
            WorldScreenSource.Capture capture => TryCapture(index: index, windowTitle: capture.WindowTitle),
            WorldScreenSource.View view => TryView(index: index, cameraName: view.CameraName),
            _ => (Ok: false, Message: $"screen {index} test-pattern source applies at next boot"),
        };

        Console.Error.WriteLine(value: $"[world.screen: {outcome.Message}]");
    }

    /// <summary>Advances every booted deterministic machine by one host-owned fixed simulation step. Called from
    /// <see cref="WorldSimulation"/> after player intents have resolved, so every tick observes the controller image for
    /// that exact tick rather than stretching the latest render-frame sample across a batch.</summary>
    /// <param name="stepTicks">The exact engine-tick budget of one host simulation step.</param>
    public void AdvanceMachines(ulong stepTicks) {
        if (m_disposed) {
            return;
        }

        foreach (var slot in m_slots.Values) {
            if (slot.Machine is not { } machine) {
                continue;
            }

            var input = m_engagement.MergedPad(screenIndex: slot.Index);

            if (machine is IQueuedScreenMachine queued) {
                var submission = queued.Submit(deltaTicks: stepTicks, input: in input);

                if ((submission == QueuedMachineSubmission.Rejected) && machine.IsAssigned) {
                    throw new InvalidOperationException(
                        message: $"Screen {slot.Index}'s queued machine rejected an authoritative tick/input segment" +
                                 ((queued.QueueFault is { } fault) ? $" ({fault})." : ".")
                    );
                }

                slot.FramesStepped = queued.CompletedSteps;
            } else if (machine.Step(deltaTicks: stepTicks, input: in input)) {
                ++slot.FramesStepped;
            }

            // SEAM (named-deferred, README "Known screen limitations"): every IScreenMachineEngine's host already
            // implements the optional Puck.Abstractions.Machines.IAudioMachine capability (MachineHost/
            // AdvancedMachineHost drain their core's audio ring for free, gated on attachment). A spatialized
            // surface-audio device is not built — no engine-wide speaker/mixer layer exists yet, mirroring
            // WorldScreenBinder's own webcam-session pattern — so this loop drains nothing today. The device, once
            // built, hooks in exactly here: `(machine as IAudioMachine)?.ReadSamples(...)` positioned at the slot's
            // WorldScreen world frame (Origin/Right/Up), the same per-slot data this binder already owns.
        }
    }

    /// <summary>Publishes every CPU-fed screen for this produced frame. Deterministic machines have already advanced in
    /// <see cref="AdvanceMachines"/>; this seam only uploads their latest framebuffer and services presentation-only
    /// camera/window captures on source-owned cadences.</summary>
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
            if (slot.Machine is { } machine) {
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
    /// <param name="services">The application services (resolves the neutral GPU compute factories for the offscreen engines).</param>
    /// <param name="hostsOnDirectX">Whether the host backend is Direct3D 12 (selects the offscreen kernel bytecode).</param>
    /// <param name="programWordCapacity">The main engine's probed program-word floor.</param>
    /// <param name="instanceCapacity">The main engine's probed instance floor.</param>
    /// <param name="dynamicTransformCapacity">The main engine's dynamic-transform slot count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public void ConfigureViews(IServiceProvider services, bool hostsOnDirectX, int programWordCapacity, int instanceCapacity, int dynamicTransformCapacity) {
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
    public void RenderViews(in FrameContext context, SdfProgram program, int revision, IReadOnlyList<DynamicTransform> transforms, float time) {
        if (m_disposed || (m_viewStack is not { } stack)) {
            return;
        }

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
    /// (the pipe-observable witness that a removed View screen's offscreen render stopped, §CR-3).</summary>
    public int ActiveCameraViewCount => (m_viewStack?.ActiveViewCount ?? 0);

    /// <summary>Points a declared screen at a placeable camera — the runtime <c>screen.view</c> path. Any existing
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

        RegisterCameraView(camera: camera);
        slot.ClearLive();
        slot.View = new ViewFeed(name: camera.Name) { Stack = m_viewStack };
        slot.DeclaredFault = null;
        m_viewStack!.SetWiredScreens(name: camera.Name, screenIndices: WiredScreensFor(name: camera.Name));

        return (Ok: true, Message: $"screen {index} showing camera '{camera.Name}'");
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var slot in m_slots.Values) {
            slot.Machine?.Dispose();
            slot.Pattern?.Surface.Dispose();
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
    // SdfCameraView for a camera. Fixed cameras carry their own world-space look-at; avatar-eye cameras resolve the
    // population entry named by AvatarIndex each frame and pose a FirstPersonRig at the declared local eye offset. A
    // camera FILMS an already-lit world, so it is a budgeted offscreen render with no room glow of its own.
    private void RegisterCameraView(WorldCamera camera) {
        m_viewStack ??= new ViewStack();

        if (!m_cameraViews.TryGetValue(key: camera.Name, value: out var view)) {
            view = new SdfCameraView(
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

            switch (camera) {
                case WorldCamera.Fixed fixedCamera:
                    view.Rig = new FixedRig {
                        Eye = fixedCamera.Position,
                        FovRadians = fixedCamera.FieldOfViewRadians,
                        Target = fixedCamera.LookAt,
                    };

                    break;
                case WorldCamera.AvatarEye avatarEye:
                    view.AnchorSource = m_anchors;
                    view.AnchorIdSource = () => avatarEye.AvatarIndex;
                    view.Rig = new FirstPersonRig {
                        EyeOffset = avatarEye.EyeOffset,
                        FovRadians = avatarEye.FieldOfViewRadians,
                    };

                    break;
                default:
                    throw new ArgumentOutOfRangeException(paramName: nameof(camera), actualValue: camera, message: "Unknown world camera kind.");
            }

            m_cameraViews[camera.Name] = view;
        }

        _ = m_viewStack.Register(name: camera.Name, content: view, band: ScreenSlotPriority.Ambient);
    }

    // After a screen removal, a camera view no remaining View slot references is orphaned — its offscreen SDF engine
    // would keep spending refresh budget and GPU work on a jumbotron nobody shows until binder shutdown. For each camera
    // a removed View screen filmed, recompute the surviving wired set: an empty set RELEASES the view (ViewStack.Release
    // disposes the SdfCameraView, freeing its offscreen SdfWorldEngine) and drops the cached instance so a later
    // screen.view rebuilds it fresh; a non-empty set (another jumbotron still films this camera) only re-narrows the
    // self-reference set to the survivors. The boot-sized ViewStack pool itself stays alive — only this camera's
    // registration ends.
    private void ReleaseOrphanedCameraViews(HashSet<string> candidates) {
        if (m_viewStack is not { } stack) {
            return;
        }

        foreach (var name in candidates) {
            var wired = WiredScreensFor(name: name);

            if (wired.Count == 0) {
                stack.Release(name: name);
                _ = m_cameraViews.Remove(key: name);
                Console.Error.WriteLine(value: $"[world.screen: camera view '{name}' released — no remaining screen references it]");
            } else {
                stack.SetWiredScreens(name: name, screenIndices: wired);
            }
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
            Fault = fault
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
        var targetByteLength = checked((int)(targetWidth * targetHeight * bytesPerPixel));

        if ((surface.Pixels.Length < checked((int)(surface.Width * surface.Height * bytesPerPixel)))) {
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

            // Live once the platform has completed its first GPU copy — mirrors the CPU-path first-frame gate.
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

    // The declared-data machine boot: resolve the engine by id, read the content file, and assemble the machine — each
    // failure (unknown engine, missing/unreadable file, rejected options) leaves the slot unbound with a visible fault.
    private void BootDeclaredMachine(ScreenSlot slot, WorldScreenSource.Machine machine) {
        if (!m_engines.TryGetValue(key: machine.Engine, value: out var engine)) {
            slot.DeclaredFault = $"no screen-machine engine '{machine.Engine}'";

            return;
        }

        if (!TryReadContent(contentPath: machine.ContentPath, content: out var content, fault: out var fault)) {
            slot.DeclaredFault = fault;

            return;
        }

        try {
            slot.Machine = engine.Create(options: machine.Options, contentBytes: content, savePath: null);
            slot.MachineEngine = engine.Id;
        } catch (ArgumentException exception) {
            slot.DeclaredFault = exception.Message;
        }
    }

    // Resolve a screen-machine engine by id. Two engines are registered today (the SM83 GamingBrick and the native
    // ARM7TDMI AdvancedGamingBrick), so an unnamed request errors loudly, listing the registered choices; omitting the
    // id only resolves mechanically when exactly one engine is registered.
    private bool TryResolveEngine(string? engineId, out IScreenMachineEngine engine, out string error) {
        if (engineId is { } id) {
            if (m_engines.TryGetValue(key: id, value: out var named)) {
                engine = named;
                error = "";

                return true;
            }

            engine = null!;
            error = $"no screen-machine engine '{id}'";

            return false;
        }

        if (m_engines.Count == 1) {
            engine = m_engines.Values.First();
            error = "";

            return true;
        }

        engine = null!;
        error = ((m_engines.Count == 0)
            ? "no screen-machine engine registered"
            : $"which engine? {m_engines.Count} registered — name one of: {string.Join(separator: ", ", values: m_engines.Keys)}");

        return false;
    }

    // Read a machine's content file from disk, or report a friendly fault (unconfigured, a missing file, or an I/O
    // error) — the loud data path shared by declared-data boot and runtime insert.
    private static bool TryReadContent(string contentPath, out byte[] content, out string? fault) {
        if (string.IsNullOrEmpty(value: contentPath)) {
            content = [];
            fault = "no content configured";

            return false;
        }

        if (!File.Exists(path: contentPath)) {
            content = [];
            fault = $"content '{contentPath}' not found";

            return false;
        }

        try {
            content = File.ReadAllBytes(path: contentPath);
            fault = null;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            content = [];
            fault = $"content '{contentPath}' unreadable ({exception.Message})";

            return false;
        }
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

        private ulong CadenceTicks { get; } = cadenceTicks;
        private ulong LastPullTicks { get; set; }
        private bool Pulled { get; set; }

        public nint Handle() => (Live ? Surface.CurrentHandle : 0);
        public bool ShouldPull(ulong elapsedTicks) {
            if (Pulled && ((elapsedTicks - LastPullTicks) < CadenceTicks)) {
                return false;
            }

            Pulled = true;
            LastPullTicks = elapsedTicks;

            return true;
        }
        public void RetryPull() => Pulled = false;
        public void NotifyDeviceLost() {
            Surface.NotifyDeviceLost();
            LastFrameVersion = -1L;
            Pulled = false;
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
        public INativeImageCaptureFeed? Source { get; private set; } = source;
        public CpuSurfaceSource Surface { get; } = surface;
        public string Title { get; } = title;
        public int? MonitorIndex { get; } = monitorIndex;

        // Whether this feed rides the D3D12 GPU transport (the platform copies GPU-side into GpuTargets and the screen
        // samples the LatestGpuSlot image), rather than the CPU-pixel Surface. Fixed at construction by the host backend.
        public bool GpuRoute { get; } = gpuRoute;
        // The three simultaneous-access shared textures the platform copies into round-robin (null until the source's
        // extent is known and the first attach runs), and the source they are attached to (identity guards re-attach).
        public IReadOnlyList<IGpuExportableStorageImage>? GpuTargets { get; set; }
        public INativeImageCaptureFeed? GpuAttachedSource { get; set; }

        // The human label a fault reads under: a window title, or a whole-monitor index.
        public string Label => ((MonitorIndex is { } monitor) ? $"monitor {monitor}" : $"window '{Title}'");

        private ulong CadenceTicks { get; } = EngineTicks.PerRate(ratePerSecond: profile.RefreshRateHz);
        private ulong LastPullTicks { get; set; }
        private bool Pulled { get; set; }

        public nint Handle() {
            if (GpuRoute) {
                // The sampled handle is the image-view of the platform's latest completed GPU copy; 0 (no-signal) until
                // that first copy lands. Returning a different slot handle per copy is cheap and change-detected.
                return ((Live && (Source is { } source) && (source.LatestGpuSlot is var slot and >= 0) && (GpuTargets is { } targets) && (slot < targets.Count))
                    ? targets[slot].ImageViewHandle
                    : 0);
            }

            return (Live ? Surface.CurrentHandle : 0);
        }
        public bool ShouldPull(ulong elapsedTicks) {
            if (Pulled && ((elapsedTicks - LastPullTicks) < CadenceTicks)) {
                return false;
            }

            Pulled = true;
            LastPullTicks = elapsedTicks;

            return true;
        }
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
            Pulled = false;
        }
        public void Dispose() {
            ReleaseGpuTargets();
            Source?.Dispose();
            Source = null;
            Surface.Dispose();
        }
    }

    // One named jumbotron view a screen samples: the shared ViewStack (set at ConfigureViews) and the camera name to
    // resolve against it. A camera FILMS an already-lit world, so its glow is the ViewStack's own (zero for a camera).
    private sealed class ViewFeed(string name) {
        public string Name { get; } = name;
        public ViewStack? Stack { get; set; }

        public nint Handle() => (Stack?.Resolve(name: Name) ?? 0);
        public Vector3 Light() => (Stack?.ResolveGlow(name: Name) ?? Vector3.Zero);
    }

    // One declared screen's slot: the persistent declared source (a test pattern or a jumbotron VIEW — both survive an
    // eject), plus at most one LIVE producer — a booted machine, the shared webcam, or a window capture — that runtime
    // insert/camera/capture swap and eject clears. Precedence is Machine > Camera > Capture > View > Pattern, so a
    // runtime source overlays the declared source and an eject reveals it again. A mutable class so the producer
    // references flip in place with no engine rebuild.
    private sealed class ScreenSlot {
        public CameraFeed? Camera { get; set; }
        public CaptureFeed? Capture { get; set; }
        public required int Index { get; init; }
        // The WorldScreenSource this slot currently reflects — set at construction and updated by ReconcileScreens, so a
        // live UpsertScreen only re-applies its source through the runtime machinery when the source actually changed.
        public WorldScreenSource? DeclaredSource { get; set; }
        public IScreenMachine? Machine { get; set; }
        public PatternFeed? Pattern { get; set; }
        public ViewFeed? View { get; set; }
        // The engine id hosting the assigned machine (for screen.state), or null when no machine is bound.
        public string? MachineEngine { get; set; }
        // The insert that booted the assigned machine — the content path, the engine id as supplied (verbatim when a
        // caller named one, else the resolved default), and the options string — so world.save can fold a runtime
        // insert back into the screen row's Machine source. All null when no machine is bound.
        public string? MachineContentPath { get; set; }
        public string? MachineSourceEngine { get; set; }
        public string? MachineOptions { get; set; }
        // The ctor-time fault (a missing content file, an absent camera, an unopenable window capture, an unknown view
        // camera); a live feed's own fault is read from the feed instead (see CurrentFault).
        public string? DeclaredFault { get; set; }
        public long FramesStepped { get; set; }

        // Whether a live (ejectable) producer is bound — a machine, the webcam, or a window capture.
        public bool HasLive => ((Machine is not null) || (Camera is not null) || (Capture is not null));

        // The current source handle: the highest-precedence live producer's, else the declared jumbotron view's, else the
        // declared test pattern's, else 0.
        public nint Handle() => ((Machine is { } machine)
            ? machine.NativeImageViewHandle
            : ((Camera is { } camera)
                ? camera.Handle()
                : ((Capture is { } capture)
                    ? capture.Handle()
                    : ((View is { } view)
                        ? view.Handle()
                        : (Pattern?.Surface.CurrentHandle ?? 0)))));

        // The current emitted light, in the same precedence as Handle.
        public Vector3 Light() => ((Machine is { } machine)
            ? machine.EmittedLight
            : ((Camera is { } camera)
                ? camera.Light
                : ((Capture is { } capture)
                    ? capture.Light
                    : ((View is { } view)
                        ? view.Light()
                        : (Pattern?.Light ?? Vector3.Zero)))));

        // The fault surfaced by screen.state: a not-live camera/window feed's own reason, else the ctor-time fault.
        public string? CurrentFault() {
            if ((Camera is { Live: false } camera) && (camera.Fault is { } cameraFault)) {
                return cameraFault;
            }

            if ((Capture is { Live: false } capture) && (capture.Fault is { } captureFault)) {
                return captureFault;
            }

            return DeclaredFault;
        }

        // Clears the live producer (machine/webcam/window) and reverts to the declared pattern or to unbound. The shared
        // webcam feed is NOT disposed here (other camera screens may still sample it — the binder owns its lifetime); a
        // machine and a window capture are per-slot and disposed.
        public void ClearLive() {
            Machine?.Dispose();
            Machine = null;
            MachineEngine = null;
            MachineContentPath = null;
            MachineSourceEngine = null;
            MachineOptions = null;
            Camera = null;
            Capture?.Dispose();
            Capture = null;
            DeclaredFault = null;
        }

        // Disposes everything this slot OWNS — its booted machine, its CPU test-pattern surface, and its per-slot window/
        // monitor capture — when the slot is removed entirely (a RemoveScreen mutation). The shared webcam feed and the
        // boot-sized offscreen view pool are NOT owned by a slot (the binder disposes them once), so the Camera/View
        // references are only dropped. Mirrors the binder's own Dispose slot loop, extended to the pattern surface.
        public void DisposeOwned() {
            Machine?.Dispose();
            Machine = null;
            Pattern?.Surface.Dispose();
            Pattern = null;
            Capture?.Dispose();
            Capture = null;
            Camera = null;
            View = null;
        }
    }
}

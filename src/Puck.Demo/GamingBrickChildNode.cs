using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Demo.Audio;
using Puck.Demo.Camera;
using Puck.Demo.Forge;
using Puck.Hosting;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Platform;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>One slice of a brick's input timeline: hold <paramref name="Buttons"/> for <paramref name="Ticks"/>
/// engine ticks. The unit an input-timeline source feeds a brick in place of per-frame sampling.</summary>
/// <param name="Ticks">The fixed-step tick budget the buttons are held over.</param>
/// <param name="Buttons">The joypad image held for the whole slice.</param>
internal readonly record struct JoypadSegment(ulong Ticks, JoypadButtons Buttons);

/// <summary>Fills <paramref name="destination"/> with a brick's pending input segments (up to its length — the
/// fast-forward cap) and returns the count written.</summary>
internal delegate int JoypadSegmentFiller(Span<JoypadSegment> destination);

/// <summary>
/// A GamingBrick machine as a per-viewport WORLD child (the <see cref="GamingBrickSource"/> consumer): it steps the
/// emulated machine by the frame's fixed-step tick budget — converted to CPU T-cycles through an exact integer
/// accumulator, so the machine remains a pure function of the engine's deterministic clock and its sampled inputs —
/// then uploads the 160×144 framebuffer and resamples it (nearest-filtered, so pixels stay crisp) into a rect-sized,
/// General-layout storage image: the same integer-copy source contract a world compositor composites into a
/// viewport slot as a hosted child. Input arrives through <see cref="InputSource"/>, sampled once per produced
/// frame before the machine steps.
/// </summary>
internal sealed class GamingBrickChildNode : ISteppableRenderNode {
    /// <summary>The SM83 machine's native framebuffer width (160). Exposed as a constant so the overworld render node
    /// names the pane's native extent through THIS type (which it already references) rather than coupling directly to
    /// the emulator's <see cref="Framebuffer"/> type — the render node sits at its exact analyzer coupling ceiling.</summary>
    public const int NativeScreenWidth = Framebuffer.ScreenWidth;
    /// <summary>The SM83 machine's native framebuffer height (144). See <see cref="NativeScreenWidth"/>.</summary>
    public const int NativeScreenHeight = Framebuffer.ScreenHeight;

    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;  // output format
    /// <summary>The machine's CPU T-cycle rate (2²² per second); with <see cref="EngineTicks.PerSecond"/> it forms the
    /// exact rational the tick accumulator carries remainders in.</summary>
    private const ulong MachineCyclesPerSecond = 4_194_304UL;
    private const uint OutputBindingIndex = 0;                 // resample.comp: Output at binding 0 (register u0)
    private const int ResamplePushByteLength = 32;             // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }
    private const uint SourceBindingIndex = 1;                 // resample.comp: Source combined-image-sampler at binding 1 (t0/s0)
    private const uint WorkgroupEdge = 8;
    /// <summary>The most timeline segments consumed per produced frame — a machine behind the shared timeline
    /// fast-forwards at roughly this multiple of realtime until it converges. Sized against the measured emulation
    /// budget (~1.8 ms per machine-frame post-M3; machine-fleet-plan.md), and machines now step in parallel, so one
    /// late boot catching up beside two live machines stays comfortably inside the frame.</summary>
    private const int MaxSegmentsPerFrame = 4;
    // The monochrome-bridge length after a REAL swap TO color: long enough to cover the game re-detecting (next VBlank)
    // and re-drawing its color palettes (a few of its own frames), short enough not to linger over an already-color
    // picture. A stand steps roughly one machine frame per engine frame, so this is ~a dozen machine frames.
    private const int ModeSwapBridgeFrames = 12;
    // The brick's four shades (white → black), the same ramp the DMG PPU emits — the target of a monochrome
    // PRESENTATION so a demoted Color stand matches a natively-booted DMG stand.
    private static readonly uint[] DmgPresentationShades = [0xFFFFFFu, 0xAAAAAAu, 0x555555u, 0x000000u];

    private readonly IServiceProvider m_appServices;
    private readonly int m_brickOrdinal;
    private byte[]? m_cartridgeRom;
    // The FAIRNESS speed policy (source.speed == "dmg"): the tick->cycle budget is pinned to the DMG rate regardless
    // of the KEY1 double-speed latch, so the budget is a function of CONFIG (never of emulated state) and every
    // machine in the run consumes identical cycle counts per engine tick. Mutable ONLY through the debug
    // buff/debuff verb (ToggleSpeedPolicy) — the realtime half of the fairness knob.
    private bool m_dmgSpeed;
    private readonly NodeDescriptor m_descriptor;
    private readonly CameraFit m_fit;
    // The device costume the stand currently PRESENTS as (the live promote/demote knob). With a per-ROM recipe the swap
    // also retargets the EMULATED hardware live (Machine.SwitchModel) and pokes the game's detection flag so it renders
    // the new mode's authored art; without one it stays a presentation-only re-interpretation. Either way the game keeps
    // running with zero lost progress. A monochrome presentation desaturates the framebuffer to the brick's shade ramp;
    // a Color presentation shows the native output as-is. Set live by ChangeModel; forces one represent so the swap
    // shows even on an unstepped frame.
    private ConsoleModel m_presentationModel;
    private bool m_forceRepresent;
    // The transient monochrome bridge after a REAL swap TO color: the game has not re-set its color palettes yet, so the
    // first frames render wrong; keep desaturating until the native color redraw lands (a fixed count — no host-visible
    // "redrew natively" signal exists). Counts down per produced frame. A swap to monochrome needs none (desaturation is
    // coherent immediately); a presentation-only swap needs none (the machine already renders the shown mode).
    private int m_bridgeFramesRemaining;
    // The machine and its cached services first come into existence at AssignCartridge (insert time) for a stand that
    // starts EMPTY (no pre-inserted ROM) — null until then — and are replaced wholesale only by ClearSaveData's
    // save-wiping reboot. A runtime-inserted brick's pane still renders every frame (the dark/unassigned screen) via
    // UploadFramebuffer's synthetic blank buffer, so the GPU-resource lifecycle never depends on assignment.
    private IFramebuffer? m_framebuffer;
    private readonly IServiceProvider m_gpuServices;
    private IJoypad? m_joypad;
    private IKey1? m_key1;
    private MachineInstance? m_machine;
    private readonly ReadOnlyMemory<byte> m_resampleBytecode;
    private readonly byte[] m_resamplePush = new byte[ResamplePushByteLength];
    private readonly byte[] m_rgba;
    private Vector3 m_averageColor;
    private readonly JoypadSegment[] m_segmentBuffer = new JoypadSegment[MaxSegmentsPerFrame];

    private readonly uint? m_allocationHeight;
    private readonly uint? m_allocationWidth;

    // ~5 seconds at 60 fps between battery-save disk writes while dirty (see the ProduceFrame flush).
    private const int SaveFlushIntervalFrames = 300;

    private nint m_boundSourceView;
    // The speaker path (Windows; null elsewhere or with no device): one OS output stream per booted machine, drained
    // after each stepped frame. Output-only by construction — the sink is the emulator's determinism-excluded ring.
    private CabinetAudioOutput? m_audioOutput;
    private IAudioSink? m_audioSink;
    private WebcamCameraSensor? m_cameraSensor;
    // The world↔machine membrane: a per-frame host feed installed for a "world" peripheral cartridge (the world-lens
    // cart). Fed a slice of the deterministic room state (WorldLensSource) captured on the render thread and applied
    // (written into the machine's WRAM sensor page) just before the machine steps — INPUT, never hidden state — and, on
    // the return path, the game-driven sprite tile read back after the step (WorldLensGameTile) so the host can move a
    // driving player's presentation avatar (machine→world). Concrete (not the interface) so the read-back is reachable.
    private SensorPagePeripheral? m_peripheralFeed;
    private WorldLensState m_pendingWorldState;
    private ICartridge? m_cartridge;
    private IGpuComputeCommandPool? m_commandPool;
    // The fourth-wall exit + 128-bit victory conditions are load-once run-document data at construction, but they are
    // now LIVE-EDITABLE through SetExitCondition / SetVictoryCondition (the console condition.* verbs — "the recursion":
    // the win/reveal gate that unlocked the editor is itself re-forgeable). Non-readonly so an edit can re-parse the
    // address/target/share, clear the one-shot fired latch (a re-edited cabinet may win again), and re-seed a meta
    // share into the running machine. The per-frame polls (ProduceFrame's exit / solo reads) re-read these fields each
    // frame, so a swapped condition takes effect the next frame with no rebuild here.
    private ushort m_exitAddress;
    private BrickExitCondition? m_exitCondition;
    private bool m_exitFired;
    // The 128-bit win condition (top 16 bytes of the cartridge's highest SRAM address). Solo precomputes its target for
    // the per-frame compare and fires locally; meta leaves the target null (the overworld room XORs the cabinets'
    // regions) and only keeps the 16-byte scratch the room reads through TryReadVictoryRegion.
    private BrickVictoryCondition? m_victoryCondition;
    private byte[]? m_victoryTarget;
    // META only: this cabinet's authored 128-bit share, seeded into the running framework game's victory-share WRAM slot
    // at every (re)assembly so the game's on-win hook converges the top-16 SRAM region on it. Document-driven +
    // re-forgeable (never baked into the shared-per-type cart ROM). Live-editable: a meta condition.set re-seeds it into
    // the running machine's slot at once (SeedVictoryShare) so the change takes effect on the running game.
    private byte[]? m_victoryShare;
    private byte[]? m_victoryRegion;
    private bool m_victoryFired;
    private int m_framesSinceSaveFlush;
    private string? m_peripheral;
    private string? m_savePath;
    private SystemMemory? m_systemMemory;
    private GamingBrickChildNode? m_mirror;
    // The serial link cable — host wiring like the mirror above, never sim state: a linked pair shares ONE
    // SerialLinkSession and advances TOGETHER through ExecuteLinkedStep on the PRIMARY end, so the pair is one step
    // unit (the invariant the parallel fleet-stepping split needs: a linked pair may never step on two threads).
    // Both ends hold the same session; only the primary drives it.
    private GamingBrickChildNode? m_linkPartner;
    private bool m_linkPrimary;
    private SerialLinkSession? m_linkSession;
    private GamingBrickPadService? m_padService;
    private bool m_padServiceResolved;
    private int m_pendingSegmentCount;
    private bool m_rgbaFresh;
    private bool m_stepExecuted;
    private bool m_stepPrepared;
    private IGpuComputeRecorder? m_computeRecorder;
    private ulong m_cycleRemainder;
    private IGpuDescriptorAllocator? m_descriptorAllocator;
    private IGpuDeviceContext? m_deviceContext;
    private nint m_deviceHandle;
    private bool m_disposed;
    private IGpuComputeServices? m_gpu;
    private uint m_height;
    private uint m_liveHeight;
    private uint m_liveWidth;
    private bool m_outputInitialized;
    private IGpuStorageImage? m_outputImage;
    private nint m_pool;
    private IGpuQueueSubmitter? m_queueSubmitter;
    private IGpuComputePipeline? m_resamplePipeline;
    private nint m_resampleSet;
    private IGpuShaderModule? m_resampleShaderModule;
    private bool m_resourcesReady;
    private nint m_sampler;
    private IGpuSurfaceUpload? m_upload;
    private uint m_width;

    /// <summary>Initializes a new instance of the <see cref="GamingBrickChildNode"/> class. ROM LOADING stays eager and
    /// fail-fast at document load regardless — the caller reads every ROM (pre-inserted and library) up front into a
    /// shared table and passes this stand's slice as <paramref name="cartridgeRom"/>. MACHINE ASSEMBLY, however, only
    /// happens here when <paramref name="cartridgeRom"/> is non-null (the pre-inserted behavior); a null
    /// ROM leaves the stand UNASSIGNED — no machine, a synthetic dark pane — until <see cref="AssignCartridge"/> runs
    /// at insert time.</summary>
    /// <param name="gpuServices">The world's neutral GPU compute services (the child produces on the world's device).</param>
    /// <param name="appServices">The application services (resolve the shared pad-routing service).</param>
    /// <param name="source">The document gaming-brick source (console costume + fit policy; ROM path is read by the
    /// caller, not here — see <paramref name="cartridgeRom"/>).</param>
    /// <param name="cartridgeRom">This stand's cartridge ROM image, already loaded by the caller's shared table, or
    /// <see langword="null"/> for a stand that starts EMPTY (no machine assembles until <see cref="AssignCartridge"/>).</param>
    /// <param name="sourceId">The node's stable identity (e.g. <c>"gaming-brick:0"</c>), for the descriptor name.</param>
    /// <param name="brickOrdinal">This brick's ordinal among the document's brick panes, in viewport-slot order — the
    /// key the pad-routing service maps a player index onto.</param>
    /// <param name="directX">Whether the world device is Direct3D 12 (selects the DXIL resample kernel).</param>
    /// <param name="allocationWidth">An optional FIXED output-image width. When set (with <paramref name="allocationHeight"/>),
    /// the output image allocates once at this extent and each frame renders only the live target rect into its
    /// top-left — the child-side mirror of the compositor's full-size source textures, so an ANIMATED pane region never
    /// reallocates GPU images mid-transition. Null follows the target extent (the static-region document path).</param>
    /// <param name="allocationHeight">The fixed output-image height paired with <paramref name="allocationWidth"/>.</param>
    /// <param name="savePath">An optional battery-save path for the cartridge (conventionally <c>&lt;romPath&gt;.sav</c>).
    /// When set and the cartridge is battery-backed, an existing file loads into the external RAM at machine assembly
    /// (a power-on cartridge read), and the RAM flushes back on change (debounced), on reboot, and on dispose — so the
    /// in-game save survives process exits, live model changes, and save-clearing reboots. Null keeps the save
    /// in-memory only.</param>
    /// <param name="exitCondition">An optional fourth-wall exit instrumentation (see
    /// <see cref="BrickExitCondition"/>): after each stepped frame the named work-RAM byte is polled, and the first
    /// time the comparison holds the host is asked to shut down cleanly. Null polls nothing.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public GamingBrickChildNode(IServiceProvider gpuServices, IServiceProvider appServices, GamingBrickSource source, string sourceId, int brickOrdinal, bool directX, byte[]? cartridgeRom = null, uint? allocationWidth = null, uint? allocationHeight = null, string? savePath = null, BrickExitCondition? exitCondition = null) {
        ArgumentNullException.ThrowIfNull(argument: gpuServices);
        ArgumentNullException.ThrowIfNull(argument: appServices);
        ArgumentNullException.ThrowIfNull(argument: source);
        ArgumentNullException.ThrowIfNull(argument: sourceId);

        m_allocationHeight = allocationHeight;
        m_allocationWidth = allocationWidth;
        m_appServices = appServices;
        m_brickOrdinal = brickOrdinal;

        m_descriptor = new NodeDescriptor(
            Name: sourceId,
            SurfaceId: SurfaceId.New()
        );
        m_dmgSpeed = string.Equals(source.Speed, "dmg", StringComparison.OrdinalIgnoreCase);
        m_fit = source.Fit;
        m_gpuServices = gpuServices;
        m_peripheral = source.Peripheral;
        m_savePath = savePath;

        // The condition arrives pre-validated (the document validator gates the address/op/value); a caller-built
        // condition that fails to parse is simply inert rather than a crash at frame time.
        if ((exitCondition is not null) && exitCondition.TryParseAddress(address: out var exitAddress)) {
            m_exitAddress = exitAddress;
            m_exitCondition = exitCondition;
        }

        // The 128-bit win condition (read from the source, like the peripheral/fit/speed fields) arrives pre-validated.
        // Solo precomputes its target bytes for the per-frame compare; meta leaves the target null here (the room XORs
        // the cabinets' regions) and keeps only the region scratch. A META condition also parses this cabinet's SHARE
        // bytes: the host SEEDS them into the running framework game's victory-share WRAM slot at boot (see
        // SeedVictoryShare), and the game's on-win hook copies that slot into the top-16 SRAM region the room reads —
        // so the per-cabinet share stays DOCUMENT-DRIVEN (source.Victory.Share) and RE-FORGEABLE without baking it into
        // the shared-per-type cart ROM.
        if (source.Victory is { } victoryCondition) {
            m_victoryCondition = victoryCondition;
            m_victoryRegion = new byte[VictoryGate.RegionByteCount];

            if (!victoryCondition.IsMeta) {
                var target = new byte[VictoryGate.RegionByteCount];

                if (victoryCondition.TryParseTarget(destination: target)) {
                    m_victoryTarget = target;
                }
            }
            else {
                var share = new byte[VictoryGate.RegionByteCount];

                if (victoryCondition.TryParseShare(destination: share)) {
                    m_victoryShare = share;
                }
            }
        }
        // The machine boots as runAs when set (the demote/promote knob — the costume stays source.Model), else as
        // the costume itself. A "runAs": "dmg" Color stand seeds the DMG boot handoff, so a dual-mode cartridge takes
        // its monochrome code path and stays bit-locked with a real DMG machine.
        BootModel = ParseModel(value: (source.RunAs ?? source.Model));
        // The stand opens presenting as it boots; the promote/demote knob retargets only this, never the machine.
        m_presentationModel = BootModel;
        m_rgba = new byte[Framebuffer.ScreenWidth * Framebuffer.ScreenHeight * 4];
        m_resampleBytecode = File.ReadAllBytes(path: Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Shaders",
            "Resample",
            (directX ? "resample.comp.dxil" : "resample.comp.spv")
        ));

        if (cartridgeRom is not null) {
            AssembleMachine(cartridgeRom: cartridgeRom, model: BootModel);
        }
    }

    /// <summary>Whether this stand's machine has assembled — <see langword="false"/> for an empty stand that has not
    /// yet had a cartridge inserted (<see cref="AssignCartridge"/>).</summary>
    public bool IsAssigned => (m_machine is not null);

    /// <summary>Assembles this stand's machine from a just-inserted cartridge — the runtime-insert half of machine
    /// assembly (the pre-inserted half is the constructor). A no-op safeguard against double-assignment: once
    /// assigned, a stand's machine is replaced only by the save-wiping reboot (<see cref="ClearSaveData"/>); the
    /// promote/demote knob (<see cref="ChangeModel"/>) never rebuilds it. Mirrors the constructor's assembly exactly,
    /// so a runtime-inserted machine is indistinguishable from a pre-inserted one from this call onward.</summary>
    /// <param name="cartridgeRom">The inserted cartridge's ROM image (already loaded in the shared table).</param>
    /// <param name="savePath">The inserted cartridge's battery-save path (see the constructor's
    /// <c>savePath</c>), or <see langword="null"/> for an in-memory-only save.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridgeRom"/> is <see langword="null"/>.</exception>
    internal void AssignCartridge(byte[] cartridgeRom, string? savePath = null) {
        ArgumentNullException.ThrowIfNull(argument: cartridgeRom);

        if (m_machine is not null) {
            return;
        }

        m_savePath = savePath;

        AssembleMachine(cartridgeRom: cartridgeRom, model: BootModel);
    }

    /// <summary>Loads (or SWAPS to) a cartridge live: flushes and disposes any running machine, then assembles the new
    /// cart's machine. The contextual-North insert and the cycle live-swap both route here. <paramref name="peripheral"/>
    /// binds the cart's sensor feed (e.g. <c>"camera"</c> for the Pocket Camera cart's webcam).</summary>
    /// <param name="cartridgeRom">The cart's ROM image.</param>
    /// <param name="savePath">The cart's battery-save path, or <see langword="null"/> for an in-memory save.</param>
    /// <param name="peripheral">The cart's peripheral feed, or <see langword="null"/> for none.</param>
    internal void LoadCartridge(byte[] cartridgeRom, string? savePath = null, string? peripheral = null) {
        ArgumentNullException.ThrowIfNull(argument: cartridgeRom);

        TeardownMachine();

        m_peripheral = peripheral;
        m_savePath = savePath;

        AssembleMachine(cartridgeRom: cartridgeRom, model: BootModel);
    }

    /// <summary>Ejects this cabinet's cartridge: flushes its save, disposes the machine, and returns the pane to a dark
    /// screen — the contextual-North eject. The stand is empty until a cart is loaded again.</summary>
    internal void Eject() {
        if (m_machine is null) {
            return;
        }

        TeardownMachine();
        RepackFramebuffer(); // m_framebuffer is null now, so this packs solid black — a dark, empty cabinet.
        m_rgbaFresh = true;
    }

    // Flushes the save, disposes the machine + camera sensor, and clears every cached service — shared by eject and the
    // swap path. Leaves the node UNASSIGNED (a dark pane) until AssembleMachine runs again. The link cable severs
    // FIRST: the session holds the dying machine's serial port, and the partner must not keep a peer reference into a
    // disposed container (unplugging the cable is what ejecting a linked cartridge means).
    private void TeardownMachine() {
        if (m_machine is null) {
            return;
        }

        Unlink(node: this);
        FlushSaveData(force: true);
        m_audioOutput?.Dispose();
        m_audioOutput = null;
        m_audioSink = null;
        m_cameraSensor?.Dispose();
        m_cameraSensor = null;
        m_peripheralFeed = null; // the sensor-page feed caches the disposed machine's memory — the next assemble rebinds it
        m_machine.Dispose();
        m_machine = null;
        m_cartridge = null;
        m_framebuffer = null;
        m_joypad = null;
        m_key1 = null;
        m_systemMemory = null;
        m_cartridgeRom = null;
    }

    // Shared by the constructor (pre-inserted) and AssignCartridge (runtime-inserted): assembles the machine, caches its
    // services, and repacks the framebuffer once so the pane has real (if dark, pre-boot) pixels immediately.
    private void AssembleMachine(byte[] cartridgeRom, ConsoleModel model) {
        m_cartridgeRom = cartridgeRom;
        var machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: model, cartridgeRom: cartridgeRom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        AttachMachine(machine: machine, model: model);
        InstallCameraPeripheral();
        InstallWorldLensPeripheral();
        var cartridge = m_cartridge!;

        // The power-on cartridge read: a persisted battery save loads into the external RAM (and, when the mapper has
        // battery-backed timed hardware, the clock footer appended after it) before the machine runs — a configuration
        // input to the deterministic timeline, exactly like the ROM image itself. The clock RESUMES where the last
        // flush left it (the footer's wall timestamp is interop metadata, ignored here): time pauses while powered
        // off and never goes backward, so a game's own elapsed-time bookkeeping (a Gen-II real-time-clock cartridge) stays consistent.
        if ((m_savePath is { } savePath) && cartridge.Header.HasBattery && File.Exists(path: savePath)) {
            try {
                var save = File.ReadAllBytes(path: savePath);
                var ramByteCount = cartridge.ExternalRamByteCount;

                if (ramByteCount > 0) {
                    cartridge.ImportExternalRam(source: save.AsSpan(start: 0, length: Math.Min(save.Length, ramByteCount)));
                }

                if ((cartridge.PersistentClockByteCount > 0) && (save.Length >= (ramByteCount + cartridge.PersistentClockByteCount))) {
                    cartridge.ImportPersistentClock(source: save.AsSpan(start: ramByteCount, length: cartridge.PersistentClockByteCount));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"[battery-save: '{savePath}' unreadable ({exception.Message}) — booting with fresh external RAM]");
            }
        }

        SeedVictoryShare(machine: machine);

        RepackFramebuffer();

        m_rgbaFresh = true;
    }

    // Seeds this cabinet's authored 128-bit meta share into the freshly-assembled framework game's victory-share WRAM
    // slot (FrameworkMemoryMap.VictoryShareSource, 0xC0F0) BEFORE the game boots — a per-cabinet host poke, exactly the
    // mechanism the mode-swap "boot shim" uses (SystemMemory.PokeCpuByte into work RAM). The framework's boot work-RAM
    // clear deliberately steps OVER this slot, so the seed survives to the game's win, where its on-win hook copies the
    // slot into the top-16 SRAM region the room's meta gate reads. Only a META condition seeds (solo converges on its
    // own target), and only into a running FRAMEWORK game — the sole cart that owns the slot; a non-framework cart is
    // left untouched, since it may use 0xC0F0..0xC0FF as ordinary work RAM. Re-seeded on every (re)assembly, so a live
    // re-forge of the share document field takes effect the next time the cart is inserted.
    private void SeedVictoryShare(MachineInstance machine) {
        if (m_victoryShare is not { } share) {
            return;
        }

        // Only a FRAMEWORK game carries the VictoryModule that reads the VictoryShareSource slot on its win edge. A
        // non-framework cart (a hand-authored game, SDF art, the Pocket Camera, the world-lens) never reads
        // 0xC0F0..0xC0FF but may use that page as ordinary work RAM, so seeding it would clobber live state — the real
        // vector is a meta condition.set onto a cabinet holding a non-framework cart. Skip it: that cart simply never
        // contributes a share, and a framework cart's boot-time/live seed is unchanged.
        if (!RunningCartIsFrameworkGame()) {
            return;
        }

        var memory = machine.GetRequiredService<SystemMemory>();

        for (var index = 0; (index < share.Length); index++) {
            memory.PokeCpuByte(address: (ushort)(Puck.Demo.Forge.Framework.FrameworkMemoryMap.VictoryShareSource + index), value: share[index]);
        }
    }

    // The two SM83 opcodes the framework prologue is recognized by: `nop` (0x00) and `jp nn` (0xC3).
    private const byte Sm83NopOpcode = 0x00;
    private const byte Sm83JumpAbsoluteOpcode = 0xC3;

    // Whether the CURRENTLY loaded cart is a framework game — the only cart that owns the VictoryShareSource WRAM slot,
    // so the one it is meaningful (rather than destructive) to seed. Recognized by the framework cartridge's fixed
    // prologue fingerprint (Forge/Framework/FrameworkCartridge assembles every framework game through it, and validates
    // it): the header trampoline at 0x0100 is `nop; jp Hw.EntryAddress`, the VBlank vector at 0x0040 jumps to the
    // handler at Hw.VBlankHandlerAddress, and the routine at Hw.EntryAddress opens with `jp boot`. Read off m_cartridgeRom
    // so it stays honest across a live cart-cycle, not just the boot cart.
    private bool RunningCartIsFrameworkGame() {
        var rom = m_cartridgeRom;

        if ((rom is null) || (rom.Length <= Puck.Demo.Forge.Framework.Hw.EntryAddress)) {
            return false;
        }

        return (
            (rom[0x0100] == Sm83NopOpcode) &&
            (rom[0x0101] == Sm83JumpAbsoluteOpcode) &&
            (rom[0x0102] == (byte)(Puck.Demo.Forge.Framework.Hw.EntryAddress & 0xFF)) &&
            (rom[0x0103] == (byte)(Puck.Demo.Forge.Framework.Hw.EntryAddress >> 8)) &&
            (rom[0x0040] == Sm83JumpAbsoluteOpcode) &&
            (rom[0x0041] == (byte)(Puck.Demo.Forge.Framework.Hw.VBlankHandlerAddress & 0xFF)) &&
            (rom[0x0042] == (byte)(Puck.Demo.Forge.Framework.Hw.VBlankHandlerAddress >> 8)) &&
            (rom[Puck.Demo.Forge.Framework.Hw.EntryAddress] == Sm83JumpAbsoluteOpcode)
        );
    }

    private void AttachMachine(MachineInstance machine, ConsoleModel model) {
        m_machine = machine;
        BootModel = model;
        // A freshly assembled/rebuilt machine (construction, shelf insert, save-clear reboot) presents as it boots and
        // owns no in-flight swap bridge — a prior stand's swapped presentation must not survive a machine rebuild.
        m_presentationModel = model;
        m_bridgeFramesRemaining = 0;
        m_cartridge = machine.GetRequiredService<ICartridge>();
        m_framebuffer = machine.GetRequiredService<IFramebuffer>();
        m_joypad = machine.GetRequiredService<IJoypad>();
        m_key1 = machine.GetRequiredService<IKey1>();
        m_systemMemory = ((m_exitCondition is not null) ? machine.GetRequiredService<SystemMemory>() : null);

        // Real speakers for a booted machine: open this cabinet's output stream and turn the machine's sink on. The
        // sink stays OFF (rate zero, no ring, no per-cycle resample work) when no stream opened — a silent host pays
        // nothing. Host configuration, never emulated state (the IAudioSink contract).
        m_audioOutput = CabinetAudioOutput.TryOpen();
        m_audioSink = ((m_audioOutput is not null) ? machine.GetRequiredService<IAudioSink>() : null);
        m_audioSink?.Configure(sampleRate: CabinetAudioOutput.SampleRate);
    }

    // Binds the PC webcam to a freshly assembled Pocket Camera cartridge as its M64282FP image source — the cartridge-
    // sensor seam. It fires only for a camera cartridge (an ordinary game has no sensor and is left untouched), and only
    // when the source doesn't opt out with peripheral "none" (which keeps the deterministic built-in sensor, so a capture
    // stays reproducible). A missing camera service or webcam is not fatal: the WebcamCameraSensor degrades to a flat
    // field, and "none" or a non-camera cartridge simply keeps whatever sensor the machine composed with.
    private void InstallCameraPeripheral() {
        m_cameraSensor?.Dispose();
        m_cameraSensor = null;

        if (m_cartridge is not PocketCameraCartridge camera) {
            return;
        }

        if (string.Equals(a: m_peripheral, b: "none", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return;
        }

        if ((m_appServices.GetService(serviceType: typeof(ICameraCaptureService)) is ICameraCaptureService service)) {
            m_cameraSensor = new WebcamCameraSensor(service: service);
            camera.Sensor = m_cameraSensor;
        }
    }

    // Binds the world→machine membrane for a "world" peripheral cartridge (the world-lens cart): a fresh
    // SensorPagePeripheral, rebound per machine because it caches the machine's SystemMemory (a rebuilt machine's is a
    // different instance). A non-"world" cart leaves the feed null — the sensor page is only ever written for a cart
    // that reads it, so an ordinary game's WRAM is untouched.
    private void InstallWorldLensPeripheral() {
        m_peripheralFeed = (string.Equals(a: m_peripheral, b: "world", comparisonType: StringComparison.OrdinalIgnoreCase)
            ? new SensorPagePeripheral()
            : null);
    }

    // The battery-save flush: persist the external RAM plus, when the mapper has battery-backed timed hardware, the
    // clock footer (stamped with the host's wall time for FOREIGN-emulator interop only — our own import ignores
    // it). Periodic (unforced) flushes are gated on dirty RAM so an idle game never writes; a FORCED flush
    // (dispose/reboot — power-off moments) also writes for a clock-only change, so the session's clock ticks
    // persist. Ordered so a failed write retries (dirty is acknowledged only after the write lands); an I/O failure
    // is reported and never takes the frame loop down.
    private void FlushSaveData(bool force = false) {
        if ((m_savePath is not { } savePath) || (m_cartridge is not { } cartridge) || !cartridge.Header.HasBattery) {
            return;
        }

        var hasClock = (cartridge.PersistentClockByteCount > 0);
        var hasRam = (cartridge.ExternalRamByteCount > 0);

        if (!(cartridge.ExternalRamDirty || (force && hasClock)) || !(hasRam || hasClock)) {
            return;
        }

        try {
            byte[][] parts = [
                (hasRam ? cartridge.ExportExternalRam() : []),
                (hasClock ? cartridge.ExportPersistentClock(unixTimestampSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : []),
            ];

            File.WriteAllBytes(path: savePath, bytes: [.. parts[0], .. parts[1]]);
            cartridge.MarkExternalRamClean();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[brick] battery-save flush to '{savePath}' failed ({exception.Message}); retrying on the next flush.");
        }
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <summary>Gets the hardware the machine actually EMULATES — fixed at boot from the document's <c>runAs</c>/
    /// <c>model</c>, the identity the choir grouping keys on. <see cref="ChangeModel"/> never touches it; only the
    /// save-wiping reboot re-seats it. See <see cref="PresentationModel"/> for the live-swappable costume.</summary>
    internal ConsoleModel BootModel { get; private set; }

    /// <summary>Gets the device costume the stand currently PRESENTS as (the live promote/demote target) — the
    /// fleet-status debug verb reads it. Display state only; the emulated <see cref="BootModel"/> is unchanged.</summary>
    internal ConsoleModel PresentationModel => m_presentationModel;

    /// <summary>Gets whether the FAIRNESS speed pin is currently applied (see <see cref="ToggleSpeedPolicy"/>).</summary>
    internal bool IsSpeedPinned => m_dmgSpeed;

    /// <summary>Gets whether this node staged a fresh framebuffer this frame (its <see cref="ExecuteStep"/> ran and
    /// repacked). A parked choir follower reads its LEADER's flag to know whether a re-upload is due this frame.</summary>
    public bool FrameStaged { get; private set; }

    /// <summary>Gets the joypad buttons last actually applied to the machine (the final <see cref="ExecuteStep"/>
    /// segment's buttons, whichever input source fed it) — the authoritative per-frame state a render-side control
    /// animation should mirror, rather than re-deriving from raw input. A parked choir follower reads its LEADER's
    /// value (its machine IS the leader's state). <see cref="JoypadButtons.None"/> before the first step.</summary>
    public JoypadButtons CurrentButtons => (m_mirror?.CurrentButtons ?? m_ownButtons);
    private JoypadButtons m_ownButtons;

    /// <summary>Gets the native image view handle of the machine's unresampled 160×144 framebuffer, already
    /// SHADER-READABLE (the resample pipeline's sampled source) — a diegetic screen surface samples this directly, so
    /// no separate resample is needed. 0 before the first upload (unassigned stand) or after a device loss.</summary>
    public nint NativeImageViewHandle => m_boundSourceView;

    /// <summary>Gets the average color of the framebuffer as last presented (post monochrome/costume desaturation),
    /// normalized to 0..1 — the light this stand's diegetic screen emits into the room. Zero for an unassigned stand.</summary>
    public Vector3 AverageColor => m_averageColor;

    /// <summary>Gets or sets the per-frame input provider; sampled once before the machine steps. Null means no
    /// buttons held — the machine still runs (attract screens, intros).</summary>
    public Func<JoypadButtons>? InputSource { get; set; }

    /// <summary>Gets or sets the per-frame WORLD slice this brick projects into a world-lens cartridge's sensor page.
    /// Sampled on the render thread (with the input) and applied only when a <c>"world"</c> peripheral cart is loaded;
    /// otherwise inert. The source reads deterministic simulation state (the overworld room player's position), so the
    /// sensor page stays honest INPUT and replay is unaffected. Null feeds a zeroed slice.</summary>
    public Func<WorldLensState>? WorldLensSource { get; set; }

    /// <summary>Gets the game-driven sprite tile (0..19 × 0..17) the world-lens ROM published on its last step — the
    /// machine→world return path. The host maps it back to a room position to move a driving player's presentation
    /// avatar. Default (0,0) before the first step; under world authority it tracks the fed sensor position.</summary>
    public (byte TileX, byte TileY) WorldLensGameTile { get; private set; }

    /// <summary>Gets or sets the per-frame power gate: while it answers <see langword="false"/> the machine does not
    /// step (its reset framebuffer is what the pane shows), and the emulated timeline starts only once it answers
    /// <see langword="true"/> — the overworld's boot switch. Null means always powered (the document-viewport path).</summary>
    public Func<bool>? PowerSource { get; set; }

    /// <summary>Gets or sets what happens when the instrumented exit condition first holds (see the constructor's
    /// <c>exitCondition</c>): null (the default) requests a clean host shutdown; a handler REPLACES the shutdown —
    /// the overworld's immersed mode uses it to break the fourth wall (reveal the room) instead of exiting. Invoked at
    /// most once, on the render thread.</summary>
    public Action? ExitConditionMet { get; set; }

    /// <summary>Gets or sets what happens when a SOLO 128-bit win condition first holds (the cartridge's top-16 SRAM bytes
    /// reached the gate constant): null (the default) requests a clean host shutdown; a handler REPLACES it — the
    /// overworld's immersed mode uses it to break the fourth wall. Invoked at most once, on the render thread. Meta
    /// conditions fire at the room level (the XOR across cabinets), not here.</summary>
    public Action? VictoryConditionMet { get; set; }

    /// <summary>Reads the cartridge's top-16 SRAM bytes — the highest address, the win-condition region — into
    /// <paramref name="destination"/> without disturbing the running game (bank-independent, side-effect-free). Returns
    /// <see langword="false"/> when no win-conditioned machine is present or the cartridge has under 16 bytes of RAM. The
    /// seam the overworld's meta gate XORs across cabinets; also the source of the solo poll's read.</summary>
    /// <param name="destination">A <see cref="VictoryGate.RegionByteCount"/>-byte span to fill.</param>
    /// <returns>Whether a region was read.</returns>
    public bool TryReadVictoryRegion(Span<byte> destination) {
        if ((m_victoryCondition is null) || (m_cartridge is not { } cartridge) || (destination.Length != VictoryGate.RegionByteCount)) {
            return false;
        }

        var ramByteCount = cartridge.ExternalRamByteCount;

        if (ramByteCount < VictoryGate.RegionByteCount) {
            return false;
        }

        cartridge.ReadExternalRam(offset: (ramByteCount - VictoryGate.RegionByteCount), destination: destination);

        return true;
    }

    /// <summary>Forces this cabinet's game to its WIN by writing its AUTHORED victory bytes — a meta cabinet's private
    /// <c>share</c>, or a solo cabinet's <c>target</c> — into the top-16 SRAM win region the meta gate reads, exactly as
    /// the game's own on-win hook would (export the external RAM, overwrite its highest 16 bytes, import it back). The
    /// room's REAL meta XOR / solo poll then counts it. The node half of the <c>win</c> control verb — it lets a script
    /// drive "complete X games → the editor reveal" end to end without real gameplay input. Returns <see langword="false"/>
    /// when the cabinet has no victory condition or no assembled cartridge.</summary>
    /// <returns><see langword="null"/> on success, else a short human reason the win could not be forced (for the verb's
    /// stderr narration) — so an agent driving the console sees WHY (no condition, not booted, or the running cart has no
    /// victory region because it isn't a framework game).</returns>
    internal string? ForceVictoryWin() {
        // Meta converges its region on its private share; solo converges directly on its target.
        var bytes = (m_victoryShare ?? m_victoryTarget);

        if (bytes is null) {
            return "no victory condition (condition.set it, or cycle to a game with a meta/solo victory)";
        }

        if (m_cartridge is not { } cartridge) {
            return "no cartridge assembled (boot it first)";
        }

        var ram = cartridge.ExportExternalRam();

        if (ram.Length < VictoryGate.RegionByteCount) {
            return "the running cart has no victory region (cart-cycle it to a framework game, types 4-8)";
        }

        // The region is the HIGHEST SRAM address (exactly what TryReadVictoryRegion + the meta gate read), so overwrite
        // the tail and import it back — the running game sees the converged region on its next poll.
        bytes.AsSpan().CopyTo(destination: ram.AsSpan(start: (ram.Length - VictoryGate.RegionByteCount)));
        cartridge.ImportExternalRam(source: ram);

        // Re-arm a solo one-shot so a re-forced win can fire its reveal again (the meta gate re-reads the region each frame).
        m_victoryFired = false;

        return null;
    }

    /// <summary>Gets this cabinet's current fourth-wall EXIT condition (load-once document data, live-editable through
    /// <see cref="SetExitCondition"/>), or <see langword="null"/> when none is set — the source the <c>condition.show</c>
    /// verb echoes for assertions.</summary>
    internal BrickExitCondition? ExitCondition => m_exitCondition;

    /// <summary>Gets this cabinet's current 128-bit VICTORY condition (load-once document data, live-editable through
    /// <see cref="SetVictoryCondition"/>), or <see langword="null"/> when none is set — the source the
    /// <c>condition.show</c> verb echoes for assertions.</summary>
    internal BrickVictoryCondition? VictoryCondition => m_victoryCondition;

    /// <summary>Replaces (or clears, with <see langword="null"/>) this cabinet's fourth-wall EXIT condition LIVE — the
    /// scriptable half of "the recursion" (a player re-forging the win/reveal gate in-game). Re-parses the work-RAM
    /// address, and CLEARS the fired one-shot so a re-edited cabinet can fire the reveal again. Acquires the machine's
    /// <see cref="SystemMemory"/> lazily when a condition is set on a cabinet that started with none (it is only cached
    /// at assembly for a documented exit condition). The per-frame poll re-reads these fields, so the swap takes effect
    /// the next frame with no rebuild. A malformed condition (unparseable address) leaves the poll inert rather than
    /// throwing — the caller has already usage-checked it.</summary>
    /// <param name="condition">The new exit condition, or <see langword="null"/> to clear.</param>
    internal void SetExitCondition(BrickExitCondition? condition) {
        m_exitFired = false;

        if ((condition is not null) && condition.TryParseAddress(address: out var address)) {
            m_exitAddress = address;
            m_exitCondition = condition;

            // The exit poll needs the machine's SystemMemory; it is only cached at assembly when the cabinet booted
            // WITH a documented exit condition, so a live edit onto a previously-condition-less cabinet fetches it now
            // (SystemMemory is always registered for a Humble machine).
            m_systemMemory ??= m_machine?.GetRequiredService<SystemMemory>();
        }
        else {
            m_exitAddress = 0;
            m_exitCondition = null;
        }
    }

    /// <summary>Replaces (or clears, with <see langword="null"/>) this cabinet's 128-bit VICTORY condition LIVE. Re-parses
    /// the solo target (or the meta share), CLEARS the fired one-shot so a re-edited cabinet can win again, and for a META
    /// condition RE-SEEDS the new share into the running framework game's victory-share WRAM slot (the Stage-2
    /// <see cref="SeedVictoryShare"/> / <see cref="Puck.Demo.Forge.Framework.FrameworkMemoryMap.VictoryShareSource"/>
    /// poke) so the change takes effect on the running game — its on-win hook then converges the top-16 SRAM region on
    /// the new share. The room-level XOR watch (<c>MetaVictoryWatch</c>) reads the console-source records, not this
    /// field, so the caller REBUILDS it after a group/target/share edit (this method only touches this cabinet's own
    /// state + running machine).</summary>
    /// <param name="condition">The new victory condition, or <see langword="null"/> to clear.</param>
    internal void SetVictoryCondition(BrickVictoryCondition? condition) {
        m_victoryFired = false;
        m_victoryCondition = condition;
        m_victoryTarget = null;
        m_victoryShare = null;

        if (condition is null) {
            m_victoryRegion = null;

            return;
        }

        m_victoryRegion ??= new byte[VictoryGate.RegionByteCount];

        if (!condition.IsMeta) {
            var target = new byte[VictoryGate.RegionByteCount];

            if (condition.TryParseTarget(destination: target)) {
                m_victoryTarget = target;
            }
        }
        else {
            var share = new byte[VictoryGate.RegionByteCount];

            if (condition.TryParseShare(destination: share)) {
                m_victoryShare = share;

                // Re-seed the running machine's WRAM share slot at once, so a meta re-forge takes effect on the game
                // already running (its next win converges the region on the new share) — the same per-cabinet poke the
                // boot-time seed uses; no reboot.
                if (m_machine is { } machine) {
                    SeedVictoryShare(machine: machine);
                    Console.Error.WriteLine(value: $"[condition] brick {m_brickOrdinal}: re-seeded meta share into the running machine's WRAM slot 0x{Puck.Demo.Forge.Framework.FrameworkMemoryMap.VictoryShareSource:X4} (the game's next win converges the region on it).");
                }
            }
        }
    }

    /// <summary>Gets or sets the input-timeline source. When set it REPLACES per-frame input sampling: each produced
    /// frame consumes up to <see cref="MaxSegmentsPerFrame"/> pending (ticks, buttons) segments, so a machine whose
    /// timeline cursor is behind (a late-booted overworld stand) fast-forwards until it converges with the stream's head
    /// — the mechanism that keeps same-costume machines bit-identical regardless of when they booted. Null keeps the
    /// classic path (<see cref="InputSource"/> / pad service, one frame-budget step per frame).</summary>
    public JoypadSegmentFiller? SegmentSource { get; set; }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (
            m_disposed ||
            (0 == context.TargetWidth) ||
            (0 == context.TargetHeight)
        ) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return default;
        }

        EnsureResources(gpuDevice: gpuDevice, height: context.TargetHeight, width: context.TargetWidth);

        var extentChanged = UpdateLiveExtent(height: context.TargetHeight, width: context.TargetWidth);
        var powered = (PowerSource?.Invoke() ?? true);

        // Debounced battery-save flush: at most one disk write per interval while the save RAM stays dirty (games
        // use save RAM as work RAM too, so flush-per-write would thrash); dispose/reboot flush the tail.
        if (++m_framesSinceSaveFlush >= SaveFlushIntervalFrames) {
            m_framesSinceSaveFlush = 0;
            FlushSaveData();
        }

        // Advance the machine while powered: by this frame's fixed-step budget with the frame's sampled input
        // (classic mode), or by the pending slices of the shared input timeline (segment mode). Either way RunTicks
        // does the exact tick → T-cycle conversion, so the emulated timeline is a pure function of the consumed
        // sequence. A render-only frame (DeltaTicks 0) steps nothing and re-presents. When the parent already drove
        // the prepare/execute split this frame (the parallel fleet-stepping path), the machine has advanced and only
        // the GPU work remains here.
        var stepped = false;

        if (m_mirror is { } mirror) {
            // Parked choir follower (machine-fleet-plan.md lever 2): the leader's machine IS this pane's state —
            // byte-identical at park time and fed the same stream since — so a fresh leader frame is a fresh frame
            // here; only the upload/resample below runs, against the leader's staged bytes.
            stepped = mirror.FrameStaged;
        }
        else if (m_stepPrepared) {
            stepped = m_stepExecuted;
            m_stepExecuted = false;
            m_stepPrepared = false;
        }
        else if (powered && (context.DeltaTicks > 0)) {
            if (PrepareInputs(context: in context)) {
                ExecuteStep();

                stepped = m_stepExecuted;
            }

            m_stepExecuted = false;
            m_stepPrepared = false;
        }

        // The fourth-wall exit poll: after a stepped frame, one work-RAM read decides whether the instrumented
        // in-game moment arrived — a pure READ of emulated state (through the machine's current WRAM banking),
        // fired once. A host that set ExitConditionMet owns what "the wall breaks" means (the overworld's immersed
        // reveal); with no handler the default is the same clean shutdown host.exitAfterSeconds uses.
        if (stepped && !m_exitFired && (m_exitCondition is { } exit) && (m_systemMemory is { } memory)) {
            var observed = memory.ReadWorkRam(address: m_exitAddress);

            if (ExitConditionHolds(observed: observed, op: exit.Op, value: exit.Value)) {
                m_exitFired = true;

                Console.Error.WriteLine(value: $"[fourth-wall] {(exit.Label ?? "exit condition")}: [{exit.Address}] = {observed} ({exit.Op} {exit.Value}) — {((ExitConditionMet is null) ? "requesting shutdown" : "breaking the wall")}.");

                if (ExitConditionMet is { } onMet) {
                    onMet();
                } else {
                    (m_appServices.GetService(serviceType: typeof(ITerminalControl)) as ITerminalControl)?.RequestExit();
                }
            }
        }

        // The 128-bit win poll (solo): after a stepped frame, read the cartridge's top-16 SRAM bytes and fire once the
        // game has driven them onto the gate constant. Meta conditions carry no target here — the room XORs the
        // cabinets' regions — so m_victoryTarget stays null and this is skipped. A deterministic READ, fired once.
        if (stepped && !m_victoryFired && (m_victoryTarget is { } victoryTarget) && (m_victoryRegion is { } region) && TryReadVictoryRegion(destination: region)) {
            if (VictoryGate.RegionEquals(region: region, target: victoryTarget)) {
                m_victoryFired = true;

                Console.Error.WriteLine(value: $"[win] {(m_victoryCondition!.Label ?? "victory")}: SRAM top-16 == {m_victoryCondition.Target} — {((VictoryConditionMet is null) ? "requesting shutdown" : "breaking the wall")}.");

                if (VictoryConditionMet is { } onWin) {
                    onWin();
                } else {
                    (m_appServices.GetService(serviceType: typeof(ITerminalControl)) as ITerminalControl)?.RequestExit();
                }
            }
        }

        PumpAudio(stepped: stepped);

        if (m_outputInitialized && !stepped && !extentChanged && !m_forceRepresent && (m_bridgeFramesRemaining == 0)) {
            return CurrentSurface();
        }

        // A live presentation swap (ChangeModel) re-presents once even without a step; consume the one-shot here.
        m_forceRepresent = false;

        // The transient monochrome bridge after a real swap TO color: count down one per PRODUCED frame, so the pane
        // keeps the mono look (and keeps uploading) until the game has re-drawn natively in color.
        if (m_bridgeFramesRemaining > 0) {
            --m_bridgeFramesRemaining;
        }

        UploadFramebuffer();
        Render();

        return CurrentSurface();
    }

    private static bool ExitConditionHolds(byte observed, string op, int value) =>
        op switch {
            "==" => (observed == value),
            "!=" => (observed != value),
            ">=" => (observed >= value),
            "<=" => (observed <= value),
            ">" => (observed > value),
            _ => (observed < value),
        };

    /// <summary>The serial half of the fleet-stepping split (machine-fleet-plan.md lever 1): gathers this frame's
    /// inputs on the RENDER thread — the timeline-access rule; a segment <see cref="SegmentSource"/> fill advances a
    /// shared-timeline cursor and the pad service is a shared drainer, so neither may run concurrently — and stages
    /// them as pending state for <see cref="ExecuteStep"/>. Returns whether there is stepping work, so the parent
    /// only fans out machines that will actually run.</summary>
    /// <param name="context">The frame context (tick budget + input sampling key).</param>
    /// <returns><see langword="true"/> when the machine has pending work for <see cref="ExecuteStep"/>.</returns>
    public bool PrepareStep(in FrameContext context) {
        if (m_disposed) {
            return false;
        }

        FrameStaged = false;

        if ((m_mirror is not null) || (0 == context.DeltaTicks) || !(PowerSource?.Invoke() ?? true)) {
            return false;
        }

        var pending = PrepareInputs(context: in context);

        m_stepExecuted = false;
        m_stepPrepared = true;

        return pending;
    }

    /// <summary>The parallel half of the fleet-stepping split: consumes the inputs staged by
    /// <see cref="PrepareStep"/>, advances the machine, and repacks the framebuffer staging bytes. Touches ONLY this
    /// brick's machine and private state (machines share nothing), so the parent may run one of these per task; all
    /// GPU work stays behind in <see cref="ProduceFrame"/> on the render thread.</summary>
    public void ExecuteStep() {
        var count = m_pendingSegmentCount;

        m_pendingSegmentCount = 0;

        if ((0 == count) || (m_joypad is not { } joypad)) {
            return;
        }

        // A linked cabinet never advances its machine ALONE — the pair steps together through the primary's
        // ExecuteLinkedStep (one budget, one deterministic interleave). Reaching here linked means the parent skipped
        // the pair pre-pass; doing nothing is the safe answer (the pane re-presents, the cable stays coherent).
        if (m_linkPartner is not null) {
            return;
        }

        ApplyWorldFeed();

        for (var index = 0; (index < count); index++) {
            joypad.SetButtons(pressed: m_segmentBuffer[index].Buttons);
            RunTicks(ticks: m_segmentBuffer[index].Ticks);
        }

        PublishWorldReadback();

        // The LAST segment's buttons are this frame's held state at the moment the machine stopped — the same value
        // a real cartridge's D-pad/buttons would show if you froze the frame. A shorter final segment (the common
        // case: one segment per already-converged frame) still lands on the frame's true held buttons.
        FinishStep(buttons: m_segmentBuffer[count - 1].Buttons);
    }

    // World→machine membrane: project this frame's captured world slice into the machine's sensor page BEFORE it
    // steps, so a world-lens cartridge reads a live view of the room it sits in each VBlank. Touches only THIS
    // machine's WRAM, so it is safe on the parallel fleet-stepping worker thread; the slice itself was sampled on
    // the render thread (PrepareInputs). The position updates once per engine frame — one write per frame is exact.
    private void ApplyWorldFeed() {
        if ((m_peripheralFeed is { } feed) && (m_machine is { } machine)) {
            feed.BeforeStep(world: in m_pendingWorldState, machine: machine);
        }
    }

    // Machine→world (the return half): publish the game-driven sprite tile the ROM wrote this frame, so the host can
    // follow it with the driving player's presentation avatar. Under world authority it just mirrors the sensor
    // position the host wrote; under game authority it is the joypad-integrated position.
    private void PublishWorldReadback() {
        if ((m_peripheralFeed is { } readback) && (m_machine is { } stepped)) {
            WorldLensGameTile = readback.ReadGameTile(machine: stepped);
        }
    }

    // The stepped-frame epilogue shared by the solo and linked execute paths: record the frame's held buttons, repack
    // the staged framebuffer bytes, and raise the staged/executed flags ProduceFrame consumes.
    private void FinishStep(JoypadButtons buttons) {
        m_ownButtons = buttons;

        RepackFramebuffer();

        FrameStaged = true;
        m_rgbaFresh = true;
        m_stepExecuted = true;
    }

    /// <summary>Parks this brick behind a choir leader (machine-fleet-plan.md lever 2): verifies the two machines are
    /// BYTE-IDENTICAL right now (both at the shared timeline's head with identical machine configs), then stops
    /// stepping this machine and mirrors the leader's staged framebuffer — a W-wide converged choir costs one stepped
    /// machine plus W presents. Refuses (and keeps stepping) if the states differ, so the amortization can never
    /// silently change what a pane shows. The parked machine stays assembled; waking it for a divergence event is a
    /// <c>Restore(leader.Snapshot())</c> away (the dormancy seam, lever 3 — same mechanism, attention-keyed).</summary>
    /// <param name="leader">The brick whose machine keeps stepping for the choir.</param>
    /// <returns><see langword="true"/> when the states matched and the park took effect.</returns>
    internal bool TryParkBehind(GamingBrickChildNode leader) {
        // Both sides must be assigned to compare (an unassigned stand has no machine, hence no state to match); the
        // choir grouping key already requires an identical ROM+model, so both are-or-aren't assigned together in
        // practice, but the guard keeps this call total rather than throwing on a not-yet-inserted stand.
        if ((m_machine is null) || (leader.m_machine is null)) {
            return false;
        }

        // A linked machine's serial state genuinely diverges (cable bits are inputs its twin never sees) — it can
        // neither park nor lead a choir.
        if ((m_linkPartner is not null) || (leader.m_linkPartner is not null)) {
            return false;
        }

        if (!m_machine.Machine.Snapshot().ContentEquals(other: leader.m_machine.Machine.Snapshot())) {
            return false;
        }

        m_mirror = leader;

        return true;
    }

    /// <summary>The divergence-event wake (lever 3's mechanism): restores this machine from its leader's CURRENT
    /// state — byte-identical to what this pane has been mirroring — copies the leader's tick→cycle remainder so
    /// future stepping stays aligned, and resumes independent stepping. A no-op when not parked.</summary>
    internal void Unpark() {
        if ((m_mirror is not { } leader) || (m_machine is null) || (leader.m_machine is null)) {
            return;
        }

        m_machine.Machine.Restore(snapshot: leader.m_machine.Machine.Snapshot());
        m_cycleRemainder = leader.m_cycleRemainder;
        m_mirror = null;

        RepackFramebuffer();

        m_rgbaFresh = true;
    }

    /// <summary>Gets whether this cabinet's machine is connected to another cabinet's over the serial link cable.</summary>
    internal bool IsLinked => (m_linkPartner is not null);

    /// <summary>Gets whether this cabinet is the PRIMARY end of its link — the end whose <see cref="ExecuteLinkedStep"/>
    /// advances the pair. Always <see langword="false"/> when unlinked.</summary>
    internal bool IsLinkPrimary => m_linkPrimary;

    /// <summary>Gets the linked partner cabinet, or <see langword="null"/> when unlinked.</summary>
    internal GamingBrickChildNode? LinkPartner => m_linkPartner;

    /// <summary>Gets the linked partner's brick ordinal (for status display), or <c>-1</c> when unlinked.</summary>
    internal int LinkPartnerOrdinal => (m_linkPartner?.m_brickOrdinal ?? -1);

    /// <summary>Connects two cabinets' machines with the serial link cable (a shared <see cref="SerialLinkSession"/>).
    /// <paramref name="first"/> becomes the PRIMARY end: the pair thereafter advances only through its
    /// <see cref="ExecuteLinkedStep"/>, one shared wall-time budget per frame, in the session's deterministic
    /// instruction interleave. Refuses (returns <see langword="false"/>) when the two are the same cabinet, either has
    /// no machine, either is already linked, or either is parked in a choir — a linked machine steps for itself.</summary>
    /// <param name="first">The primary end.</param>
    /// <param name="second">The secondary end.</param>
    /// <returns>Whether the cable connected.</returns>
    internal static bool TryLink(GamingBrickChildNode first, GamingBrickChildNode second) {
        if (ReferenceEquals(objA: first, objB: second)
            || (first.m_machine is not { } firstMachine) || (second.m_machine is not { } secondMachine)
            || (first.m_linkPartner is not null) || (second.m_linkPartner is not null)
            || (first.m_mirror is not null) || (second.m_mirror is not null)) {
            return false;
        }

        var session = new SerialLinkSession(first: firstMachine, second: secondMachine);

        first.m_linkPartner = second;
        first.m_linkPrimary = true;
        first.m_linkSession = session;
        second.m_linkPartner = first;
        second.m_linkPrimary = false;
        second.m_linkSession = session;

        return true;
    }

    /// <summary>Unplugs a cabinet's link cable: the shared session is disposed (both serial ports lose their peer) and
    /// both cabinets resume stepping independently, phase-aligned — the frozen end inherits the live end's tick→cycle
    /// remainder, the same hand-off <see cref="Unpark"/> performs. A no-op for an unlinked cabinet.</summary>
    /// <param name="node">Either end of the pair.</param>
    internal static void Unlink(GamingBrickChildNode node) {
        if (node.m_linkPartner is not { } partner) {
            return;
        }

        var primary = (node.m_linkPrimary ? node : partner);
        var secondary = (node.m_linkPrimary ? partner : node);

        primary.m_linkSession?.Dispose();

        // While linked, only the primary's accumulator converted ticks to cycles; hand it to the secondary so both
        // resume on the same phase.
        secondary.m_cycleRemainder = primary.m_cycleRemainder;

        node.m_linkPartner = null;
        node.m_linkPrimary = false;
        node.m_linkSession = null;
        partner.m_linkPartner = null;
        partner.m_linkPrimary = false;
        partner.m_linkSession = null;
    }

    /// <summary>The linked-pair execute: advances BOTH machines of a link pair together through the shared
    /// <see cref="SerialLinkSession"/> — called on the PRIMARY end after <see cref="PrepareStep"/> ran on both
    /// cabinets, in place of the two independent <see cref="ExecuteStep"/> calls. Both sides must have staged exactly
    /// this frame's ONE segment (the link precondition keeps both at the shared timeline's head, or pad-sampled);
    /// anything else — an unpowered side, a paused stream — skips the frame for BOTH, share-fate, deterministically.
    /// One shared wall-time budget (the primary's segment, converted through the primary's accumulator and speed
    /// policy) advances the pair; the session's interleave keeps every exchanged serial bit replay-identical.</summary>
    /// <param name="partner">The secondary end (must be this cabinet's <see cref="LinkPartner"/>).</param>
    internal void ExecuteLinkedStep(GamingBrickChildNode partner) {
        if ((m_linkSession is not { } session) || !m_linkPrimary || !ReferenceEquals(objA: m_linkPartner, objB: partner)) {
            return;
        }

        var mineCount = m_pendingSegmentCount;
        var theirsCount = partner.m_pendingSegmentCount;

        m_pendingSegmentCount = 0;
        partner.m_pendingSegmentCount = 0;

        if ((mineCount != 1) || (theirsCount != 1) || (m_joypad is not { } joypad) || (partner.m_joypad is not { } partnerJoypad)) {
            return;
        }

        ApplyWorldFeed();
        partner.ApplyWorldFeed();

        joypad.SetButtons(pressed: m_segmentBuffer[0].Buttons);
        partnerJoypad.SetButtons(pressed: partner.m_segmentBuffer[0].Buttons);

        session.Run(tCycles: TakeCycleBudget(ticks: m_segmentBuffer[0].Ticks));

        PublishWorldReadback();
        partner.PublishWorldReadback();
        FinishStep(buttons: m_segmentBuffer[0].Buttons);
        partner.FinishStep(buttons: partner.m_segmentBuffer[0].Buttons);
    }

    /// <summary>The realtime half of the FAIRNESS knob (debug buff/debuff): toggles the DMG speed pin, so from the
    /// next step the tick→cycle budget is pinned (or restored to the hardware policy). The budget stays a function
    /// of configuration — this changes the configuration, never derives from emulated state.</summary>
    /// <returns>The new pin state.</returns>
    internal bool ToggleSpeedPolicy() {
        m_dmgSpeed = !m_dmgSpeed;

        return m_dmgSpeed;
    }

    /// <summary>The live device swap (the lightning bolt / mushroom): retargets this stand to <paramref name="model"/>
    /// with NO reboot and no lost progress, in every direction (DMG↔CGB↔AGB). When a per-ROM recipe is known for the
    /// running cartridge, it is a GENUINE hardware swap — <see cref="Machine.SwitchModel"/> re-gates the emulated color
    /// path, repages the switchable RAM / drops double speed on a demote, and pokes the game's cached hardware-detection
    /// flag so the running game re-detects and re-renders in the new mode's OWN authored art (its shared-RAM progress
    /// untouched; the Color-only banks survive un-paged, cartridge-move style). Without a recipe it degrades to a
    /// presentation-only re-interpretation — the framebuffer desaturates to the brick's shade ramp for a monochrome
    /// costume — because flipping the hardware blind would garble a game still running its old code path. Called between
    /// frames (render-thread verb dispatch, never mid-step), so the machine is idle at an instruction boundary.</summary>
    /// <param name="model">The device to switch to.</param>
    internal void ChangeModel(ConsoleModel model) {
        if (m_presentationModel == model) {
            return;
        }

        var previousModel = m_presentationModel;

        // The GENUINE swap: with a recipe to flip the running game's code path, retarget the emulated hardware and poke
        // its detection flag; the game then re-renders natively in the new mode. No recipe → a bare presentation change.
        var pokes = ((m_cartridge is { } cartridge) ? GamingBrickModeTables.PokesFor(title: cartridge.Header.Title, target: model) : []);
        var realSwap = ((m_machine is not null) && (pokes.Length > 0));

        if (realSwap) {
            m_machine!.Machine.SwitchModel(model: model, pokes: pokes);
        }

        m_presentationModel = model;

        // Bridge ONLY on a real monochrome→color promote: that is the one transition where the game must still re-run
        // its color-palette setup, so the first frames render wrong and are held behind the monochrome look until the
        // native color redraw lands. A color→color swap (CGB↔AGB) never leaves color — no bridge, or the pane would
        // flash grayscale for nothing — and a →monochrome swap desaturates coherently from frame one.
        m_bridgeFramesRemaining = ((realSwap && !previousModel.SupportsColor() && model.SupportsColor()) ? ModeSwapBridgeFrames : 0);

        // Re-present the CURRENT frame through the new costume at once, and force one upload so the swap lands even on
        // a render-only (unstepped) frame — the game itself never paused.
        RepackFramebuffer();

        m_rgbaFresh = true;
        m_forceRepresent = true;
    }

    /// <summary>The clear-save debug verb: deletes the persisted battery save (when any) and reboots the machine as
    /// its current <see cref="BootModel"/> with FRESH external RAM — the game boots as if the cartridge's battery
    /// were replaced. The caller re-seats the timeline cursor because this is deliberately still a reboot.</summary>
    internal void ClearSaveData() {
        if (m_savePath is { } savePath) {
            try {
                File.Delete(path: savePath);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"[brick] deleting battery save '{savePath}' failed ({exception.Message}).");

                return;
            }
        }

        // No flush: the cleared save must not resurrect. The rebuilt machine finds no file to import and boots blank.
        RebootCore(model: BootModel);
    }

    private void RebootCore(ConsoleModel model) {
        if (m_cartridgeRom is not { } cartridgeRom) {
            return;
        }

        Unlink(node: this); // a reboot is a power cycle — the cable unplugs before the machine goes away
        m_mirror = null;
        m_machine?.Dispose();

        BootModel = model;

        AssembleMachine(cartridgeRom: cartridgeRom, model: model);

        m_cycleRemainder = 0UL;
        m_pendingSegmentCount = 0;
    }

    // Stage this frame's input into the segment buffer: timeline mode drains up to the fast-forward cap of pending
    // segments (a caught-up machine gets exactly this frame's segment; a late-booted one replays history several
    // segments per frame until it converges); classic mode stages one segment holding the frame's sampled buttons
    // over the frame's whole tick budget.
    private bool PrepareInputs(in FrameContext context) {
        // Sample the world slice for the sensor-page feed HERE, on the render thread with the input (the world state is
        // shared, deterministic sim state — off-thread reads in ExecuteStep are not allowed). Only when a world-lens
        // cart is loaded, so an ordinary cart never pays for it.
        if (m_peripheralFeed is not null) {
            m_pendingWorldState = (WorldLensSource?.Invoke() ?? default);
        }

        if (SegmentSource is { } segmentSource) {
            m_pendingSegmentCount = segmentSource(m_segmentBuffer.AsSpan());
        }
        else {
            m_segmentBuffer[0] = new JoypadSegment(Ticks: context.DeltaTicks, Buttons: SampleInput(frameKey: context.RenderTicks));
            m_pendingSegmentCount = 1;
        }

        return (m_pendingSegmentCount > 0);
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // The GPU resources belong to the (lost) world device; the next EnsureResources rebuilds them. The machine is
        // CPU state and survives untouched — the game does not reset because the GPU did.
        m_resourcesReady = false;
        m_outputInitialized = false;
        m_boundSourceView = 0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        // Unplug the link cable before the machine goes away (the partner must not keep a peer into a disposed
        // container), then the power-off flush persists whatever the last frames wrote.
        Unlink(node: this);
        FlushSaveData(force: true);

        m_deviceContext?.TryWaitIdle();
        m_upload?.Dispose();
        m_commandPool?.Dispose();
        m_resamplePipeline?.Dispose();

        if ((0 != m_sampler) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroySampler(deviceHandle: m_deviceHandle, samplerHandle: m_sampler);
            m_sampler = 0;
        }

        if ((0 != m_pool) && (m_descriptorAllocator is not null)) {
            m_descriptorAllocator.DestroyPool(deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_pool = 0;
        }

        m_outputImage?.Dispose();
        m_resampleShaderModule?.Dispose();
        m_audioOutput?.Dispose();
        m_cameraSensor?.Dispose();
        m_machine?.Dispose();
    }

    // The speaker drain: after a stepped frame, move whatever the machine's mixer buffered out to the OS stream. A
    // pure READ of the sink's host-facing ring — output-only, so host audio can never perturb the simulation.
    private void PumpAudio(bool stepped) {
        if (stepped && (m_audioOutput is { } audioOutput) && (m_audioSink is { } audioSink)) {
            audioOutput.Pump(sink: audioSink);
        }
    }

    // Advance the machine by a tick budget converted to CPU T-cycles through an exact integer rational (remainder
    // carried, zero drift), so the emulated timeline is a pure function of the consumed tick/input sequence. Under
    // the Color double-speed mode (KEY1) the CPU consumes T-cycles at twice the rate per dot, so the HARDWARE policy
    // doubles the budget to keep WALL-CLOCK pacing constant — the picture stays ~59.73 fps on real hardware in either
    // speed, and here too. The DMG (fairness) policy pins the budget instead: a double-speed section runs at half
    // wall-rate rather than gaining ground, and the budget never depends on emulated state.
    private void RunTicks(ulong ticks) {
        if (m_machine is not { } machine) {
            // Unassigned (no cartridge yet): nothing to step. PowerSource gates this in practice (a boot requires an
            // inserted cartridge, which is exactly when a machine exists), but this stays total rather than throwing.
            return;
        }

        machine.Machine.Run(tCycles: TakeCycleBudget(ticks: ticks));
    }

    // The tick→cycle conversion RunTicks and the linked-pair step share: consume a tick budget against this brick's
    // exact integer accumulator (remainder carried, zero drift) and return the machine cycle budget it buys under the
    // current speed policy. Callable only with a machine assigned (m_key1 is cached beside it).
    private ulong TakeCycleBudget(ulong ticks) {
        var cyclesPerSecond = ((!m_dmgSpeed && m_key1!.IsDoubleSpeed) ? (2UL * MachineCyclesPerSecond) : MachineCyclesPerSecond);
        var scaled = ((ticks * cyclesPerSecond) + m_cycleRemainder);

        m_cycleRemainder = (scaled % EngineTicks.PerSecond);

        return (scaled / EngineTicks.PerSecond);
    }

    // An explicit InputSource (tests, scripted runs) wins; otherwise the shared pad-routing service answers for this
    // brick's ordinal under the run's routing policy (multicast, per-player, or the Overworld mirror).
    private JoypadButtons SampleInput(ulong frameKey) {
        if (InputSource is { } input) {
            return input();
        }

        if (!m_padServiceResolved) {
            m_padServiceResolved = true;
            m_padService = (m_appServices.GetService(serviceType: typeof(GamingBrickPadService)) as GamingBrickPadService);
        }

        return (m_padService?.SampleBrick(brickOrdinal: m_brickOrdinal, frameKey: frameKey) ?? default);
    }

    // Internal so the overworld's choir grouping computes the SAME boot-model key this constructor uses.
    internal static ConsoleModel ParseModel(string value) =>
        value.ToLowerInvariant() switch {
            "dmg" => ConsoleModel.Dmg,
            "agb" => ConsoleModel.Agb,
            _ => ConsoleModel.Cgb,
        };

    // A rect-sized, General-layout storage image: the integer-copy source contract SdfEngineNode composites.
    private Surface CurrentSurface() {
        return new Surface(
            Format: SurfaceFormat.R8G8B8A8Unorm,
            Height: m_height,
            ImageViewHandle: m_outputImage!.ImageViewHandle,
            Width: m_width
        );
    }

    // Repack the framebuffer's 0x00RRGGBB pixels as the R,G,B,A bytes the upload wants (opaque alpha) into the one
    // reused staging array — the only CPU copy on the path. Runs inside ExecuteStep (possibly off the render thread)
    // right after the machine advances, so the upload below can consume the staged bytes without touching the machine.
    // An UNASSIGNED stand (no machine yet — an empty stand awaiting an insert) has no framebuffer to read: it packs
    // solid opaque black instead, so its pane shows a dark screen rather than GPU-uninitialized garbage.
    private void RepackFramebuffer() {
        var rgba = m_rgba;

        if (m_framebuffer is not { } framebuffer) {
            for (var offset = 3; (offset < rgba.Length); offset += 4) {
                rgba[offset] = 0xFF; // alpha only; R,G,B stay zero (black) from the array's default init
            }

            m_averageColor = Vector3.Zero; // an empty stand has no screen and emits no light
            return;
        }

        var pixels = framebuffer.Pixels;
        // A monochrome PRESENTATION desaturates the native image to the brick's four-shade ramp (matching a stand
        // that truly booted DMG); a Color presentation passes the native pixels through. The transient bridge after a
        // real swap TO color also desaturates — holding the mono look until the game has re-drawn its color palettes,
        // so the pane never flashes the wrong-palette frames a just-promoted machine emits before it re-detects.
        var mono = (!m_presentationModel.SupportsColor() || (m_bridgeFramesRemaining > 0));
        var sumRed = 0;
        var sumGreen = 0;
        var sumBlue = 0;

        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];
            var red = (byte)(pixel >> 16);
            var green = (byte)(pixel >> 8);
            var blue = (byte)pixel;

            if (mono) {
                var shade = DmgPresentationShade(red: red, green: green, blue: blue);

                red = (byte)(shade >> 16);
                green = (byte)(shade >> 8);
                blue = (byte)shade;
            }

            rgba[offset] = red;
            rgba[offset + 1] = green;
            rgba[offset + 2] = blue;
            rgba[offset + 3] = 0xFF;

            sumRed += red;
            sumGreen += green;
            sumBlue += blue;
        }

        // The PRESENTED framebuffer's average color drives this stand's diegetic screen light: the room glows the
        // game's dominant hue, and a mono/costume swap is reflected automatically (the average is of the desaturated
        // bytes just written). Normalized to 0..1.
        var scale = (1f / (255f * pixels.Length));

        m_averageColor = new Vector3((sumRed * scale), (sumGreen * scale), (sumBlue * scale));
    }

    // Rec.709 luma weights, scaled to sum to 256 so the reconstruction is a single >> 8 (no float, no divide).
    private const int LumaRedWeight = 54;
    private const int LumaGreenWeight = 183;
    private const int LumaBlueWeight = 19;
    private const int LumaShift = 8;

    // Quarter cuts across the 0..255 luma range, brightest bucket first (white, light, dark, black).
    private const int LumaWhiteCutoff = 192;
    private const int LumaLightCutoff = 128;
    private const int LumaDarkCutoff = 64;

    // Map a native pixel to the brick's four-shade ramp (the same [white, light, dark, black] the DMG PPU emits),
    // by Rec.709 luma quantized to four buckets — a demoted Color stand reads as the SAME grayscale a real brick
    // would show, so the costume swap looks like the hardware it names.
    private static uint DmgPresentationShade(byte red, byte green, byte blue) {
        var luma = (((red * LumaRedWeight) + (green * LumaGreenWeight) + (blue * LumaBlueWeight)) >> LumaShift);

        return DmgPresentationShades[(luma >= LumaWhiteCutoff) ? 0 : ((luma >= LumaLightCutoff) ? 1 : ((luma >= LumaDarkCutoff) ? 2 : 3))];
    }

    // Upload the staged RGBA bytes and (re)bind the sampled source view. A parked choir follower uploads its LEADER's
    // staged bytes (always current after the step barrier); otherwise repack first only when this node's staging
    // bytes are not already fresh from ExecuteStep (the first-ever upload, or an extent-change re-present of an
    // unstepped machine).
    private void UploadFramebuffer() {
        var rgba = m_rgba;

        if (m_mirror is { } mirror) {
            rgba = mirror.m_rgba;
            m_averageColor = mirror.m_averageColor; // a parked follower shows its leader's pixels — and its glow color
        }
        else if (!m_rgbaFresh) {
            RepackFramebuffer();
        }

        m_rgbaFresh = false;

        var source = m_upload!.Upload(
            deviceContext: m_deviceContext!,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: Framebuffer.ScreenHeight,
            pixels: rgba,
            width: Framebuffer.ScreenWidth
        );

        if (source != m_boundSourceView) {
            m_descriptorAllocator!.WriteCombinedImageSampler(arrayElement: 0, binding: SourceBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: source, samplerHandle: m_sampler);
            m_boundSourceView = source;
        }
    }

    // Tracks the frame's live target extent (the pane's current rect, always <= the allocated image extent): a change
    // rewrites the fit push constants and forces a re-render, so an animated pane rescales every frame it grows.
    private bool UpdateLiveExtent(uint width, uint height) {
        var liveWidth = Math.Min(width, m_width);
        var liveHeight = Math.Min(height, m_height);

        if ((m_liveWidth == liveWidth) && (m_liveHeight == liveHeight)) {
            return false;
        }

        m_liveHeight = liveHeight;
        m_liveWidth = liveWidth;

        WriteFit();

        return true;
    }

    // ResampleParams: sample the machine's fixed 160×144 output into the LIVE output rect (always within the allocated
    // image). Sample = stretch; Fill = center-crop the source to the output's aspect.
    private void WriteFit() {
        var originX = 0f;
        var originY = 0f;
        var sizeX = 1f;
        var sizeY = 1f;

        if ((CameraFit.Fill == m_fit) && (m_liveWidth > 0) && (m_liveHeight > 0)) {
            var outAspect = ((float)m_liveWidth / m_liveHeight);
            var sourceAspect = ((float)Framebuffer.ScreenWidth / Framebuffer.ScreenHeight);

            if (sourceAspect > outAspect) {
                sizeX = (outAspect / sourceAspect);
                originX = ((1f - sizeX) * 0.5f);
            } else {
                sizeY = (sourceAspect / outAspect);
                originY = ((1f - sizeY) * 0.5f);
            }
        }

        var words = MemoryMarshal.Cast<byte, uint>(span: m_resamplePush.AsSpan());
        var floats = MemoryMarshal.Cast<byte, float>(span: m_resamplePush.AsSpan());

        words[0] = m_liveWidth;
        words[1] = m_liveHeight;
        floats[2] = originX;
        floats[3] = originY;
        floats[4] = sizeX;
        floats[5] = sizeY;
        words[6] = 1u; // cellSize (no pixelation — the source already is the pixel grid)
        words[7] = 0u; // quantizeLevels (off)
    }

    private void EnsureResources(IGpuDeviceContext gpuDevice, uint height, uint width) {
        // A fixed allocation extent wins over the live target: the image allocates once and animated rects render into
        // its top-left (UpdateLiveExtent), exactly like the compositor's full-size SDF source textures.
        width = (m_allocationWidth ?? width);
        height = (m_allocationHeight ?? height);

        if (
            m_resourcesReady &&
            (m_width == width) &&
            (m_height == height)
        ) {
            return;
        }

        m_gpu ??= (IGpuComputeServices)m_gpuServices.GetService(serviceType: typeof(IGpuComputeServices))!;

        if (!m_resourcesReady) {
            m_computeRecorder = m_gpu.ComputeRecorder;
            m_deviceContext = gpuDevice;
            m_deviceHandle = gpuDevice.DeviceHandle;
            m_descriptorAllocator = m_gpu.DescriptorAllocator;
            m_queueSubmitter = m_gpu.QueueSubmitter;

            GpuComputeBinding[] resampleBindings = [
                new GpuComputeBinding(Binding: OutputBindingIndex, Kind: GpuComputeBindingKind.StorageImage),
                new GpuComputeBinding(Binding: SourceBindingIndex, Kind: GpuComputeBindingKind.SampledImage),
            ];

            m_resampleShaderModule = m_gpu.ShaderModuleFactory.Create(deviceContext: gpuDevice, stage: GpuShaderStage.Compute, bytecode: m_resampleBytecode);
            // Nearest filtering end to end: emulator pixels magnify as crisp cells, never bilinear smears.
            m_resamplePipeline = m_gpu.ComputePipelineFactory.Create(
                bindings: resampleBindings,
                computeShaderModule: m_resampleShaderModule,
                deviceContext: gpuDevice,
                pushConstantBinding: new GpuPushConstantBinding(data: m_resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute),
                samplerFilter: GpuSamplerFilter.Nearest
            );
            m_sampler = m_descriptorAllocator.CreateSampler(deviceHandle: m_deviceHandle, filter: GpuSamplerFilter.Nearest);

            var poolSizes = GpuDescriptorPoolSizes.ForSets(resampleBindings);

            m_pool = m_descriptorAllocator.CreatePool(deviceHandle: m_deviceHandle, sizes: poolSizes);
            m_resampleSet = m_descriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: m_deviceHandle, poolHandle: m_pool);
            m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: gpuDevice);
            m_upload = m_gpu.SurfaceTransferFactory.CreateUpload(deviceContext: gpuDevice);
        }

        // (Re)create the rect-sized output and rebind it; the pipeline, sampler, pool, and upload are extent-independent.
        m_outputImage?.Dispose();
        m_outputImage = m_gpu.StorageImageFactory.Create(deviceContext: gpuDevice, format: Format, height: height, width: width);
        m_descriptorAllocator!.WriteStorageImage(arrayElement: 0, binding: OutputBindingIndex, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, imageViewHandle: m_outputImage.ImageViewHandle);
        m_outputInitialized = false;
        m_height = height;
        m_width = width;
        m_liveHeight = 0;
        m_liveWidth = 0; // force the next UpdateLiveExtent to rewrite the fit push
        m_resourcesReady = true;
    }

    private void Render() {
        var recorder = m_computeRecorder!;
        var commandBuffer = m_commandPool!.CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        if (!m_outputInitialized) {
            // Bring the freshly created output into the General (UAV) working layout the resample writes and the
            // compositor reads; it then persists there (written each frame, read under the parent's per-frame barrier).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: m_deviceHandle, imageHandle: m_outputImage!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);

            m_outputInitialized = true;
        }

        recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, pipelineHandle: m_resamplePipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: m_resampleSet, deviceHandle: m_deviceHandle, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: commandBuffer, data: m_resamplePush, deviceHandle: m_deviceHandle, offset: 0, pipelineLayoutHandle: m_resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle, groupCountX: ((Math.Max(1u, m_liveWidth) + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountY: ((Math.Max(1u, m_liveHeight) + (WorkgroupEdge - 1)) / WorkgroupEdge), groupCountZ: 1);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: m_deviceHandle);

        // Fire-and-forget on the shared queue: enqueued ahead of the parent compositor's submit, which barriers this
        // node's output writes before its composite read.
        m_queueSubmitter!.Submit(commandBufferHandles: [commandBuffer], deviceContext: m_deviceContext!);
    }
}

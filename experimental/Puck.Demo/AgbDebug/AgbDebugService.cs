using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.AdvancedGamingBrick;
using Puck.Commands;
using Puck.Hosting;
using Puck.HumbleGamingBrick;

namespace Puck.Demo.AgbDebug;

/// <summary>
/// The demo's native AGB (ARM7TDMI) debug subsystem — the control plane behind the <c>agb.*</c> console
/// verb family and the fullscreen single-game AGB scene. It owns at most one live <see cref="AdvancedGamingBrickHost"/>,
/// the runtime BIOS override, the pending cartridge, and the paused/frame bookkeeping the execution-control verbs read
/// and mutate. It is reached by <see cref="AgbDebugCommandModule"/> through <see cref="IServiceProvider"/> — the
/// sanctioned "cannot add to the render node's host interface without exceeding its analyzer coupling ceiling" escape
/// (the render node and the overworld frame source both sit at their exact CA1506 ceilings; see
/// <see cref="Puck.Commands.ICommandModule"/>'s remarks and the Tracker precedent) — and it exposes only PRIMITIVE
/// render-facing members (<see cref="Active"/>, <see cref="ViewHandle"/>, <see cref="Glow"/>) the render node forwards,
/// so integrating it never adds a facade TYPE to either ceilinged class.
/// <para>Determinism note: the running scene is tick-locked exactly like the SM83 cabinets — each produced frame it
/// advances the machine by precisely the master cycles the engine tick budget buys (an exact rational, remainder
/// carried across frames), never one whole frame per produced frame, so emulated time is a pure function of the
/// (tick, input) sequence rather than the vsync-variable render cadence. The staged buttons bind to that same tick
/// budget — a <c>JoypadSegment</c> of one. The <c>agb.step</c>/<c>agb.frame</c>/<c>agb.until</c> verbs advance the
/// machine explicitly and stay tick-independent by design; the ARM core is fully deterministic given the cycles it
/// runs, so <c>agb.frame</c>'s framebuffer hash reproduces exactly.</para>
/// </summary>
internal sealed class AgbDebugService {
    // Real AGB hardware caps a forward `agb.until` run: ~50M instructions is a few seconds of emulated time — enough to
    // reach any boot-time PC without hanging the console shell if the target never occurs.
    private const long UntilInstructionCap = 50_000_000L;
    private const int TraceInstructionCap = 1000;
    // In-memory savestate slots the agb.snap/agb.restore verbs address (default slot 0). A reboot clears them, since a
    // snapshot only restores into a machine of the same BIOS/ROM identity.
    private const int SnapshotSlotCount = 4;
    // The AGB master-clock rate (2²⁴ cycles/second). With EngineTicks.PerSecond it forms the exact rational the
    // tick→cycle accumulator carries a remainder in, so the emulated timeline never drifts against the engine clock.
    private const ulong MachineCyclesPerSecond = 16_777_216UL;

    private readonly IServiceProvider m_services;
    private readonly byte[] m_replacementBios = new byte[ReplacementBios.ImageSize]; // the zeroed default (direct-boot safe)
    private readonly AgbMachineSnapshot?[] m_snapshots = new AgbMachineSnapshot?[SnapshotSlotCount];
    // The build-once, machine-neutral time-travel layer (rewind delta-ring + runahead lookahead + fast-forward). Pure
    // HOST state, never serialized and never able to perturb the simulation; the same MachineTimeTravel component the
    // queued hosts drive, here bound to the AGB debug scene's KEYINPUT-image input. Rebound per boot (a fresh machine
    // identity invalidates all captured history), null while no scene is live.
    private MachineTimeTravel<AgbSceneInput>? m_timeTravel;
    private AdvancedGamingBrickHost? m_host;
    private bool m_active;
    private bool m_paused;
    // The BIOS override set live by `agb.bios` (takes effect on the NEXT boot), plus its source path for status.
    private byte[]? m_biosOverride;
    private string? m_biosOverridePath;
    // The ROM the next arg-less boot uses (a --rom .gba / run-doc native cabinet seeds it; an explicit `agb.debug <path>`
    // wins over it). Null falls through to the built-in micro-ROM.
    private string? m_pendingRomPath;
    private string m_romName = "(none)";
    private string m_romSource = "(none)";
    private string m_biosKind = "replacement (zeroed stub)";
    // The buttons the next stepped frame binds to its tick budget (the AGB analogue of a JoypadSegment), and the
    // exact-rational tick→cycle accumulator's carried remainder — zero drift across frames.
    private JoypadButtons m_pendingButtons;
    // The recorded solar light level `agb.light` stages. It rides the recorded per-frame input image (AgbSceneInput), so
    // it is replayed on rewind and predicted by runahead exactly like the held buttons — never a direct cartridge poke
    // that would escape the record. Zero (darkest) matches the sensor's own reset.
    private byte m_recordedLight;
    private ulong m_cycleRemainder;

    public AgbDebugService(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);

        m_services = services;
    }

    /// <summary>Whether the fullscreen AGB scene is active (a native machine is booted and takes over the view).</summary>
    public bool Active => (m_active && (m_host is not null));

    /// <summary>The shader-readable framebuffer view handle for the diegetic slab (0 when inactive).</summary>
    public nint ViewHandle => (Active ? m_host!.ViewHandle : 0);

    /// <summary>The room-light glow the AGB screen emits (zero when inactive).</summary>
    public Vector3 Glow => (Active ? m_host!.AverageColor : Vector3.Zero);

    /// <summary>The native AGB screen aspect (240×160 = 3:2) — the slab is authored to it.</summary>
    public float Aspect => AdvancedGamingBrickHost.ScreenAspect;

    /// <summary>Seeds the ROM an arg-less boot uses (a <c>--rom &lt;path.gba&gt;</c> launch or a run-document native AGB
    /// cabinet). Inert while a scene is already live; the value is consumed at the next <see cref="Enter"/>.</summary>
    /// <param name="romPath">The cartridge ROM path.</param>
    public void SetPendingRom(string? romPath) => m_pendingRomPath = romPath;

    /// <summary>Stages the buttons the next stepped frame binds to its tick budget — the AGB analogue of a
    /// <c>JoypadSegment</c>: instead of latching a bitmask sampled at the produced-frame rate, the render node hands in
    /// this frame's held buttons, and <see cref="ProduceFrame"/> holds them across exactly the master cycles the
    /// frame's engine-tick budget buys. Left/Right shoulder buttons have no SM83 equivalent and stay released.</summary>
    /// <param name="buttons">The buttons currently held (the same value the SM83 cabinets consume).</param>
    public void SetJoypad(JoypadButtons buttons) => m_pendingButtons = buttons;

    // The active-low KEYINPUT image for a held-button set (clear bit = pressed): bit 0=A, 1=B, 2=Select, 3=Start,
    // 4=Right, 5=Left, 6=Up, 7=Down. The two shoulder bits (8=R, 9=L) have no SM83 equivalent and stay released.
    private static ushort ToKeyInput(JoypadButtons buttons) {
        var keys = 0x03FF; // every bit set = released

        keys = Press(keys, buttons, JoypadButtons.A, bit: 0);
        keys = Press(keys, buttons, JoypadButtons.B, bit: 1);
        keys = Press(keys, buttons, JoypadButtons.Select, bit: 2);
        keys = Press(keys, buttons, JoypadButtons.Start, bit: 3);
        keys = Press(keys, buttons, JoypadButtons.Right, bit: 4);
        keys = Press(keys, buttons, JoypadButtons.Left, bit: 5);
        keys = Press(keys, buttons, JoypadButtons.Up, bit: 6);
        keys = Press(keys, buttons, JoypadButtons.Down, bit: 7);

        return (ushort)keys;
    }
    private static int Press(int keys, JoypadButtons held, JoypadButtons button, int bit) =>
        (held.HasFlag(flag: button) ? keys & ~(1 << bit) : keys);

    // Consume a tick budget against the exact integer rational (AGB cycles/sec ÷ EngineTicks.PerSecond), carrying the
    // remainder across frames so the master-cycle total never drifts. Mirrors GamingBrickChildNode.TakeCycleBudget.
    private ulong TakeCycleBudget(ulong ticks) {
        var scaled = ((ticks * MachineCyclesPerSecond) + m_cycleRemainder);

        m_cycleRemainder = (scaled % EngineTicks.PerSecond);

        return (scaled / EngineTicks.PerSecond);
    }

    /// <summary>The per-produced-frame render-thread work: latches the staged buttons, and — while running — advances
    /// the machine by exactly the master cycles this frame's engine-tick budget buys (remainder carried), then drains
    /// its audio, and uploads the framebuffer to a shader-readable view. Emulated time is therefore a pure function of
    /// the (tick, input) sequence, never the produced-frame cadence: a render-only frame (<c>DeltaTicks</c> 0) or a
    /// paused scene latches the current buttons but steps nothing and simply re-presents. A no-op when the scene is
    /// down or the GPU device is unavailable this frame.</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device and carries the tick budget).</param>
    public void ProduceFrame(in FrameContext context) {
        if ((m_host is not { } host) || !m_active) {
            return;
        }

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return;
        }

        // Bind this frame's staged buttons to KEYINPUT (so a paused agb.step still sees the live pad), then — while
        // running — step exactly the frame's tick budget converted to master cycles. The agb.step/agb.frame/agb.until
        // verbs advance the machine explicitly and stay tick-independent by design.
        var keys = ToKeyInput(buttons: m_pendingButtons);
        var input = new AgbSceneInput(Keys: keys, Light: m_recordedLight);

        // Bind the full held-input image (buttons + recorded solar light) to the machine each produced frame — so a
        // paused agb.step/agb.frame still sees the live pad and staged light, and the recorded/replayed image matches
        // exactly what advanced the machine.
        host.SetKeyInput(keys: keys);
        host.SetLightLevel(level: m_recordedLight);

        if (!m_paused && (context.DeltaTicks > 0UL)) {
            // Fast-forward is a host-level multiplier on this frame's cycle budget (never a timing hack inside the core):
            // the machine advances FastForwardFactor frames of emulated time per produced frame, and only the final
            // framebuffer is presented below, so presentation frames are skipped.
            var factor = (ulong)(m_timeTravel?.FastForwardFactor ?? 1);
            var budget = (long)TakeCycleBudget(ticks: (context.DeltaTicks * factor));

            _ = host.RunCycles(cycles: budget);

            host.PumpAudio();

            // Rewind: record this advanced frame's full input image + the post-frame tick-to-cycle accumulator into the
            // ring. Runahead: keep the lookahead N frames ahead on the predicted (currently-held) full image. Both only
            // READ the real machine, so its trajectory is identical whether either feature is on or off, and only the
            // real machine's audio was pumped above.
            if (m_timeTravel is { } timeTravel) {
                timeTravel.Record(input: in input, budget: budget, hostAccumulator: m_cycleRemainder);
                timeTravel.AdvanceLookahead(predicted: in input);
            }
        }

        var gpu = (IGpuComputeServices)m_services.GetService(serviceType: typeof(IGpuComputeServices))!;

        // Present the lookahead's framebuffer while runahead is live and primed; otherwise the real machine's own.
        if ((m_timeTravel is { } present) && present.TryGetDisplayFramebuffer(framebuffer: out var lookahead)) {
            host.Produce(gpu: gpu, deviceContext: gpuDevice, source: lookahead);
        } else {
            host.Produce(gpu: gpu, deviceContext: gpuDevice);
        }
    }

    /// <summary>Drops the GPU upload after a device loss; the machine survives.</summary>
    public void OnDeviceLost() => m_host?.OnDeviceLost();

    /// <summary>Tears the machine (and its GPU upload) down at host shutdown — called by the render node's Dispose
    /// BEFORE the GPU device is destroyed, so no upload leaks past the device. Idempotent.</summary>
    public void Shutdown() {
        m_active = false;
        m_paused = false;
        m_timeTravel?.Dispose();
        m_timeTravel = null;
        m_host?.Dispose();
        m_host = null;
    }

    // ---- Mode entry / exit --------------------------------------------------------------------------------------

    /// <summary>Toggles the fullscreen AGB scene: enters with <paramref name="romPathArg"/> (or the pending/built-in
    /// ROM) when down, leaves when up. Returns the console narration.</summary>
    /// <param name="romPathArg">An optional explicit ROM path.</param>
    public string Toggle(string? romPathArg) => (m_active ? Exit() : Enter(romPathArg: romPathArg));

    /// <summary>Enters the fullscreen AGB scene, booting the resolved ROM. Explicit path → pending ROM (a native
    /// cabinet / <c>--rom</c>) → the built-in micro-ROM. Direct-boots and starts running (not paused).</summary>
    /// <param name="romPathArg">An optional explicit ROM path.</param>
    public string Enter(string? romPathArg) {
        var (rom, name, source, error) = ResolveRom(romPathArg: romPathArg);

        if (rom is null) {
            return (error ?? "[agb.debug: could not resolve a ROM]");
        }

        m_host?.Dispose();

        var (bios, biosKind) = ResolveBios();

        try {
            m_host = AdvancedGamingBrickHost.Build(bios: bios, rom: rom);
        } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            m_host = null;

            return $"[agb.debug: failed to boot '{name}' — {exception.Message}]";
        }

        m_active = true;
        m_paused = false;
        m_romName = name;
        m_romSource = source;
        m_biosKind = biosKind;
        m_pendingButtons = default;
        m_recordedLight = 0;
        m_cycleRemainder = 0UL;

        // A fresh machine has a new BIOS/ROM identity; stale slots from a previous boot would refuse to restore, so
        // clear them rather than leave a trap. The rewind ring and any runahead lookahead are likewise invalidated —
        // rebind the neutral time-travel layer onto the fresh machine (armed to capture, matching the always-on rewind
        // history the debug scene has offered).
        Array.Clear(array: m_snapshots);
        m_timeTravel?.Dispose();
        m_timeTravel = new MachineTimeTravel<AgbSceneInput>(core: m_host, keyframeIntervalFrames: 120, cyclesPerSecond: MachineCyclesPerSecond);
        m_timeTravel.SetRewindEnabled(enabled: true);

        return $"[agb.debug on] booted {name} ({source}); bios={biosKind}, running — agb.pause to freeze, agb.status for state";
    }

    /// <summary>Leaves the fullscreen AGB scene and tears the machine down. Returns the narration.</summary>
    public string Exit() {
        if (!m_active) {
            return "[agb.debug: not active]";
        }

        m_active = false;
        m_paused = false;
        m_timeTravel?.Dispose();
        m_timeTravel = null;
        m_host?.Dispose();
        m_host = null;

        return "[agb.debug off] the native AGB scene closed";
    }

    /// <summary>Sets the BIOS image used on the NEXT boot from a file path (the runtime override honored above
    /// <c>PUCK_AGB_BIOS</c>). A wrong-sized or unreadable file is rejected and the current default kept.</summary>
    /// <param name="path">The 16&#160;KiB BIOS image path.</param>
    public string SetBios(string path) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            return "[agb.bios: usage — agb.bios <path-to-16KiB-bios>]";
        }

        if (!File.Exists(path: path)) {
            return $"[agb.bios: '{path}' not found]";
        }

        try {
            var bytes = File.ReadAllBytes(path: path);

            if (bytes.Length != ReplacementBios.ImageSize) {
                return $"[agb.bios: '{path}' is {bytes.Length} bytes; expected exactly {ReplacementBios.ImageSize}]";
            }

            m_biosOverride = bytes;
            m_biosOverridePath = path;

            return $"[agb.bios] override set to '{path}' — takes effect on the next boot (agb.debug <rom> to reboot)";
        } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            return $"[agb.bios: '{path}' unreadable — {exception.Message}]";
        }
    }

    /// <summary>Sets the booted cartridge's recorded solar-sensor light level, 0 (darkest) to 255 (brightest) — the
    /// diegetic-sun-feeds-cartridge seam: a fixed verb value today, a world-lighting provider swap later, without
    /// touching the core. Recorded per-frame host input (like the held buttons), so it stays constant until the next
    /// call and any GPIO poll in between replays deterministically. A no-op (reported, not an error) on a cartridge
    /// with no solar sensor.</summary>
    /// <param name="levelArg">The 0-255 light level.</param>
    public string SetLightLevel(string levelArg) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!CommandArgs.TryParseInt(text: levelArg, value: out var level) || (level < 0) || (level > 255)) {
            return $"[agb.light: usage — agb.light <0-255>, got '{levelArg}']";
        }

        // Stage the level onto the RECORDED per-frame input image (applied by ProduceFrame each frame), not a direct
        // cartridge poke: it now rides the rewind/runahead record, so a rewind replays the light timeline and no
        // history invalidation is needed — the mutation is a first-class recorded input, not an unrecorded escape.
        m_recordedLight = (byte)level;

        return (host.HasSolarSensor
            ? $"[agb.light {level}] recorded into the input stream (0=darkest, 255=brightest); the cartridge's solar sensor samples it on its next RESET and rewind replays it"
            : $"[agb.light {level}] recorded, but the booted cartridge has no solar sensor — no observable effect");
    }

    // ---- Execution-control verbs --------------------------------------------------------------------------------

    // The advance kinds the single paused-advance seam integrates with time-travel.
    private enum AdvanceKind {
        Frame,
        Instruction,
    }

    // THE paused authority-advance seam every agb.frame/step/until/trace routes through, so no debugger path can move
    // the machine while leaving the rewind ring or runahead lookahead behind, or skipping the staged input image
    // (H-03/H-09/M-11). Binds the full staged input image (buttons AND recorded solar light) — matching ProduceFrame's
    // per-frame apply (H-02) — advances the authority the requested way, and integrates time-travel:
    //   Frame       — runs `count` whole native frames, recording each into the ring and rebasing the lookahead exactly
    //                 like ProduceFrame, so status never reports a negative lead after paused frames (H-03).
    //   Instruction — runs up to `count` instructions, stopping early once `stopAtPc` matches R15 (agb.until). Each
    //                 instruction executes through `step` — a bare host.Step() by default, or a caller-supplied
    //                 stepper that brackets the same call with its own capture (agb.trace's before/after register and
    //                 opcode read) — so there is exactly one place that steps the authority regardless of caller
    //                 (M-11: agb.trace no longer calls host.Step() outside this seam). An instruction boundary is
    //                 unreplayable by the frame ring, so the whole batch drops the rewind history once and forces the
    //                 lookahead to rebase from the now-advanced machine on its next advance (the pane never serves the
    //                 stale fork in the meantime) (H-09).
    // Returns the instruction count executed (Instruction) or 0 (Frame).
    private long AdvanceAuthority(AdvanceKind kind, in AgbSceneInput input, long count, uint? stopAtPc = null, Action<AdvancedGamingBrickHost>? step = null) {
        var host = m_host!;

        host.SetKeyInput(keys: input.Keys);
        host.SetLightLevel(level: input.Light);

        if (kind == AdvanceKind.Frame) {
            for (var i = 0L; (i < count); ++i) {
                _ = host.RunFrame();

                // A whole-frame step consumes no tick accumulator, so the current remainder is the frame's landing phase.
                m_timeTravel?.Record(input: in input, budget: AdvancedGamingBrickMachine.CyclesPerFrame, hostAccumulator: m_cycleRemainder);
            }

            // Keep the persistent lookahead N frames ahead on the just-applied image (rebasing it if runahead was armed
            // while paused) — the H-03 fix: a paused frame advance maintains runahead exactly as production does.
            m_timeTravel?.AdvanceLookahead(predicted: in input);

            return 0L;
        }

        var stepInstruction = (step ?? (static advancingHost => advancingHost.Step()));
        var executed = 0L;

        while ((executed < count) && (!stopAtPc.HasValue || (host.ProgramCounter != stopAtPc.Value))) {
            stepInstruction(host);
            ++executed;
        }

        // Instruction granularity is unreplayable by the frame ring; drop the history once for the whole batch and
        // re-sync the lookahead from the real machine on its next advance (the pane never serves the stale fork).
        m_timeTravel?.InvalidateForUnreplayableAdvance();

        return executed;
    }

    /// <summary>Freezes the host cadence (the machine stops advancing until resumed or single-stepped).</summary>
    public string Pause() =>
        (Require(host: out var host) ?? SetPaused(host: host, paused: true, verb: "pause"));

    /// <summary>Resumes host cadence (the machine advances one frame per produced frame again).</summary>
    public string Resume() =>
        (Require(host: out _) ?? SetPaused(host: m_host!, paused: false, verb: "resume"));

    private string SetPaused(AdvancedGamingBrickHost host, bool paused, string verb) {
        m_paused = paused;

        return $"[agb.{verb}] {(paused ? "paused" : "running")} at PC=0x{host.ProgramCounter:X8} frame={host.FrameCount}";
    }

    /// <summary>Single-steps <paramref name="count"/> instructions while paused; echoes the final PC/CPSR.</summary>
    /// <param name="count">The instruction count (default 1, min 1).</param>
    public string Step(int count) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!m_paused) {
            return "[agb.step: pause first (agb.pause) — stepping only makes sense while the host cadence is frozen]";
        }

        var n = Math.Max(val1: 1, val2: count);
        var input = new AgbSceneInput(Keys: ToKeyInput(buttons: m_pendingButtons), Light: m_recordedLight);

        _ = AdvanceAuthority(kind: AdvanceKind.Instruction, input: in input, count: n);

        return $"[agb.step {n}] PC=0x{host.ProgramCounter:X8} CPSR=0x{host.Cpsr:X8} ({DescribeState(host: host)}) — rewind history dropped (instruction stepping is outside frame replay; runahead rebases next advance)";
    }

    /// <summary>Runs <paramref name="count"/> whole frames while paused; echoes the frame count + framebuffer hash.</summary>
    /// <param name="count">The frame count (default 1, min 1).</param>
    public string Frame(int count) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!m_paused) {
            return "[agb.frame: pause first (agb.pause) — the running scene already advances a frame each produced frame]";
        }

        var n = Math.Max(val1: 1, val2: count);
        var input = new AgbSceneInput(Keys: ToKeyInput(buttons: m_pendingButtons), Light: m_recordedLight);

        // Route through the one seam: it applies the full staged image, runs n whole native frames recording each into
        // the ring (so `rewind` has history even when the scene is driven purely by paused stepping), and rebases the
        // runahead lookahead so a primed pane never falls behind the authority (H-03/H-09).
        _ = AdvanceAuthority(kind: AdvanceKind.Frame, input: in input, count: n);

        return $"[agb.frame {n}] frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} fb=0x{host.FramebufferHash():X16}";
    }

    /// <summary>Captures the whole-machine savestate into an in-memory slot (default 0); echoes the frame/PC/hash and
    /// the image size. Works whether running or paused — the capture is instantaneous.</summary>
    /// <param name="slot">The slot to store into (0..3).</param>
    public string Snap(int slot) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if ((slot < 0) || (slot >= SnapshotSlotCount)) {
            return $"[agb.snap: slot must be 0..{(SnapshotSlotCount - 1)}]";
        }

        var snapshot = host.Snapshot();

        m_snapshots[slot] = snapshot;

        return $"[agb.snap {slot}] captured frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} cycle={host.Cycles} fb=0x{host.FramebufferHash():X16} ({snapshot.Size} bytes)";
    }

    /// <summary>Restores the whole-machine savestate from an in-memory slot (default 0); echoes the restored
    /// frame/PC/hash. Rewinds or fast-forwards the machine to the captured instant with no reboot.</summary>
    /// <param name="slot">The slot to restore from (0..3).</param>
    public string Restore(int slot) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if ((slot < 0) || (slot >= SnapshotSlotCount)) {
            return $"[agb.restore: slot must be 0..{(SnapshotSlotCount - 1)}]";
        }

        if (m_snapshots[slot] is not { } snapshot) {
            return $"[agb.restore {slot}: slot empty — agb.snap {slot} first]";
        }

        try {
            host.RestoreState(snapshot: snapshot);
        } catch (InvalidOperationException exception) {
            return $"[agb.restore {slot}: cannot restore — {exception.Message}]";
        }

        return $"[agb.restore {slot}] restored frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} cycle={host.Cycles} fb=0x{host.FramebufferHash():X16}";
    }

    /// <summary>Rewinds through the delta-ring history by a machine-frame count or a duration (<c>rewind 50</c> /
    /// <c>rewind 2s</c>), clamped to the oldest captured frame. Drives the machine-neutral
    /// <see cref="MachineTimeTravel{TInput}"/> layer.</summary>
    /// <param name="argument">A frame count, or a duration suffixed with <c>s</c>.</param>
    public string Rewind(string argument) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (m_timeTravel is not { } timeTravel) {
            return "[rewind: no scene]";
        }

        if (!TryParseFrames(argument: argument, frames: out var frames, label: out var label)) {
            return "[rewind: usage — rewind <frames|Ns> (e.g. rewind 50 or rewind 2s)]";
        }

        var rewound = timeTravel.RewindBy(frames: frames, hostAccumulator: out var landedAccumulator);

        if (rewound <= 0) {
            return "[rewind: no history captured yet — run the scene (or agb.frame while paused) first]";
        }

        // Restore the tick-to-cycle accumulator phase the landed frame was produced under, atomically with the core, so
        // the first future produced frame reproduces the recorded budget rather than the abandoned future's phase (H-04).
        m_cycleRemainder = landedAccumulator;

        return string.Create(provider: CultureInfo.InvariantCulture, handler:
            $"[rewind {label}] rewound {rewound} frame(s); frame={host.FrameCount} cycle={host.Cycles} fb=0x{host.FramebufferHash():X16}");
    }

    /// <summary>Reports the rewind ring's depth, span, memory footprint, and the live runahead/fast-forward settings.</summary>
    public string RewindStatus() {
        if (Require(host: out _) is { } guard) {
            return guard;
        }

        if (m_timeTravel is not { } timeTravel) {
            return "[rewind.status: no scene]";
        }

        var status = timeTravel.GetStatus();

        if (status.DepthFrames == 0) {
            return "[rewind.status] ring empty — no frames captured yet";
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler:
            ($"[rewind.status] depth={status.DepthFrames} frame(s) across {status.SegmentCount} segment(s) (~{status.SpanSeconds:F1}s) | " +
            $"~{(status.ByteFootprint / 1024)}KiB | runahead={((status.RunaheadFrames > 0) ? $"{status.RunaheadFrames}f(lead {status.RunaheadLeadFrames})" : "off")} | ff=x{status.FastForwardFactor}"));
    }

    /// <summary>Arms/disarms two-instance runahead (<c>runahead &lt;n|off&gt;</c>). The real machine is untouched, so it
    /// stays the authoritative, sole-audio sim.</summary>
    /// <param name="argument">A positive frame count, or <c>off</c>.</param>
    public string Runahead(string argument) {
        if (Require(host: out _) is { } guard) {
            return guard;
        }

        if (m_timeTravel is not { } timeTravel) {
            return "[runahead: no scene]";
        }

        if (string.IsNullOrWhiteSpace(value: argument) || string.Equals(a: argument, b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            timeTravel.SetRunahead(frames: 0);

            return "[runahead off] the pane shows the real machine again";
        }

        if (!int.TryParse(s: argument, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var n) || (n < 1)) {
            return $"[runahead: usage — runahead <n|off> (n = frames ahead, 1..{MachineTimeTravel<AgbSceneInput>.MaxRunaheadFrames})]";
        }

        n = Math.Min(val1: n, val2: MachineTimeTravel<AgbSceneInput>.MaxRunaheadFrames);
        timeTravel.SetRunahead(frames: n);

        return $"[runahead {n}] lookahead forked; the pane now shows the machine {n} frame(s) ahead on predicted input (real machine unchanged, audio from real only)";
    }

    /// <summary>Sets or clears the host-level fast-forward multiplier (<c>fastforward &lt;factor|off&gt;</c>): the machine
    /// advances <c>factor</c> frames of emulated time per produced frame, presenting only the final frame.</summary>
    /// <param name="argument">A factor of at least 1, or <c>off</c> (= 1).</param>
    public string FastForward(string argument) {
        if (Require(host: out _) is { } guard) {
            return guard;
        }

        if (m_timeTravel is not { } timeTravel) {
            return "[fastforward: no scene]";
        }

        if (string.IsNullOrWhiteSpace(value: argument) || string.Equals(a: argument, b: "off", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            timeTravel.SetFastForward(factor: 1);

            return "[fastforward off] back to realtime (x1)";
        }

        if (!int.TryParse(s: argument, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out var factor) || (factor < 1)) {
            return "[fastforward: usage — fastforward <factor|off> (factor >= 1)]";
        }

        // An over-cap factor is a clean verb error, never a silent clamp or a worker-loop-killing overflow (H-07).
        if (factor > MachineTimeTravel<AgbSceneInput>.MaxFastForwardFactor) {
            return $"[fastforward: refused — factor {factor} exceeds the supported maximum x{MachineTimeTravel<AgbSceneInput>.MaxFastForwardFactor}]";
        }

        timeTravel.SetFastForward(factor: factor);

        return $"[fastforward x{factor}] the machine now advances {factor}x realtime; presentation frames are skipped";
    }

    // Parses a rewind argument: a bare frame count, or a duration suffixed with 's' converted to native frames (the
    // presentation-only navigation math that only picks which stored frame to land on).
    private static bool TryParseFrames(string argument, out int frames, out string label) {
        frames = 0;
        label = argument;

        if (string.IsNullOrWhiteSpace(value: argument)) {
            return false;
        }

        var text = argument.Trim();

        if (text.EndsWith(value: "s", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (!float.TryParse(s: text[..^1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var seconds) || (seconds < 0f)) {
                return false;
            }

            frames = (int)((seconds * MachineCyclesPerSecond) / AdvancedGamingBrickMachine.CyclesPerFrame);
            label = $"{seconds.ToString(provider: CultureInfo.InvariantCulture)}s";

            return true;
        }

        if (!int.TryParse(s: text, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out frames) || (frames < 0)) {
            return false;
        }

        label = $"{frames}f";

        return true;
    }

    /// <summary>Runs instructions until R15 equals <paramref name="targetHex"/> (a 0x-prefixed PC), capped at 50M —
    /// FORWARD ONLY (the core has no rewind). Echoes hit-or-cap and the final PC.</summary>
    /// <param name="targetHex">The target program counter as a hex string.</param>
    public string Until(string targetHex) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!TryParseHex(text: targetHex, value: out var target)) {
            return "[agb.until: usage — agb.until <hex-pc> (e.g. agb.until 0x08000010)]";
        }

        var input = new AgbSceneInput(Keys: ToKeyInput(buttons: m_pendingButtons), Light: m_recordedLight);
        var executed = AdvanceAuthority(kind: AdvanceKind.Instruction, input: in input, count: UntilInstructionCap, stopAtPc: target);
        var hit = (host.ProgramCounter == target);

        return $"[agb.until 0x{target:X8}] {(hit ? "HIT" : "CAP")} after {executed} instruction(s); PC=0x{host.ProgramCounter:X8} CPSR=0x{host.Cpsr:X8} — rewind history dropped (instruction stepping is outside frame replay; runahead rebases next advance)";
    }

    /// <summary>Traces the next <paramref name="count"/> instructions (capped at 1000): per instruction the fetched
    /// opcode, PC, ARM/THUMB state, CPSR, and the register deltas it produced. Mirrors the POST <c>--statetrace</c>
    /// shape, plus the opcode text the side-effect-free bus peek now makes possible. Routes through the same paused
    /// authority seam as <see cref="Step"/>/<see cref="Until"/> (M-11), so the staged buttons/light image applies
    /// before the traced instructions execute rather than being invisible to them.</summary>
    /// <param name="count">The instruction count (default 1, min 1, max 1000).</param>
    public string Trace(int count) {
        if (Require(host: out _) is { } guard) {
            return guard;
        }

        var n = Math.Clamp(value: count, min: 1, max: TraceInstructionCap);
        var before = new uint[16];
        var builder = new StringBuilder();
        var input = new AgbSceneInput(Keys: ToKeyInput(buttons: m_pendingButtons), Light: m_recordedLight);
        var index = 0;

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[agb.trace {n}] (PC state BEFORE each instruction; deltas after)");

        // Reuses AdvanceAuthority's instruction-shaped advance (input application + loop + post-batch invalidate) and
        // brackets its host.Step() call with the same before/after register + opcode capture Trace always produced
        // (M-11: no second, unstaged host.Step() call site).
        _ = AdvanceAuthority(kind: AdvanceKind.Instruction, input: in input, count: n, step: tracedHost => {
            for (var r = 0; (r < 16); ++r) {
                before[r] = tracedHost.GetRegister(index: r);
            }

            var pc = tracedHost.ProgramCounter;
            var thumb = ((tracedHost.Cpsr & 0x20u) != 0u);
            // R15 is the fetch-stage address (the architectural PC+8/PC+4 offset); the instruction Step() is about
            // to execute is one fetch-step behind it — the address FetchWord actually read into the execute slot.
            var opcodeAddress = (pc - (thumb ? 2u : 4u));
            var opcodeText = (thumb
                ? $"{tracedHost.DebugRead16(address: opcodeAddress):X4}"
                : $"{tracedHost.DebugRead32(address: opcodeAddress):X8}");

            tracedHost.Step();

            builder.Append(value: '\n');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  {index,4}: PC=0x{pc:X8} {(thumb ? 'T' : 'A')} op=0x{opcodeText} CPSR=0x{tracedHost.Cpsr:X8}");

            for (var r = 0; (r < 16); ++r) {
                var now = tracedHost.GetRegister(index: r);

                if (now != before[r]) {
                    builder.Append(provider: CultureInfo.InvariantCulture, handler: $" r{r}:0x{before[r]:X8}->0x{now:X8}");
                }
            }

            ++index;
        });

        return builder.ToString();
    }

    /// <summary>Dumps r0–r15 + CPSR (with the decoded NZCV/ARM-THUMB/mode), plus the banked SPSR when the current
    /// mode has one (omitted in User/System mode, which has none).</summary>
    public string Regs() {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        var builder = new StringBuilder();

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[agb.regs] CPSR=0x{host.Cpsr:X8} ({DescribeState(host: host)})");

        if (host.TryGetSpsr(spsr: out var spsr)) {
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $" SPSR=0x{spsr:X8}");
        }

        for (var r = 0; (r < 16); ++r) {
            builder.Append(value: (((r % 4) == 0) ? '\n' : ' '));
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  r{r,-2}=0x{host.GetRegister(index: r):X8}");
        }

        return builder.ToString();
    }

    /// <summary>Dumps one I/O register halfword (a 0x-prefixed offset) or, with no argument, the whole 0x000–0x3FE I/O
    /// block — the same <c>IO &lt;offset&gt; &lt;value&gt;</c> shape as the POST <c>--iodump</c>.</summary>
    /// <param name="offsetArg">An optional 0x-prefixed offset; null dumps the whole block.</param>
    public string Io(string? offsetArg) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (offsetArg is not null) {
            if (!TryParseHex(text: offsetArg, value: out var offset) || (offset >= 0x400u)) {
                return "[agb.io: usage — agb.io [0x000..0x3FE] (omit for the whole block)]";
            }

            return $"[agb.io] IO {(offset & ~1u):X3} {host.DebugReadIo(offset: offset & ~1u):X4}";
        }

        var builder = new StringBuilder();

        builder.Append(value: "[agb.io] whole I/O block (halfwords):");

        for (var offset = 0u; (offset < 0x400u); offset += 2u) {
            builder.Append(value: '\n');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  IO {offset:X3} {host.DebugReadIo(offset: offset):X4}");
        }

        return builder.ToString();
    }

    /// <summary>Hex-dumps <paramref name="lengthArg"/> bytes (default 16, max 256) from <paramref name="addressArg"/> —
    /// the side-effect-free <c>agb.peek &lt;addr&gt; [len]</c> read over the whole 32-bit bus.</summary>
    /// <param name="addressArg">A 0x-prefixed CPU address.</param>
    /// <param name="lengthArg">An optional byte count.</param>
    public string Peek(string addressArg, string? lengthArg) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!TryParseHex(text: addressArg, value: out var address)) {
            return "[agb.peek: usage — agb.peek <0xADDR> [len] (len 1..256, default 16)]";
        }

        var length = 16;

        if ((lengthArg is not null) && (!int.TryParse(s: lengthArg, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out length) || (length < 1))) {
            return "[agb.peek: len must be a positive integer]";
        }

        length = Math.Min(val1: length, val2: 256);

        return DebugConsoleFormat.HexDump(label: "agb.peek", baseAddress: address, length: length, read: host.DebugRead8);
    }

    /// <summary>Forces bytes into writable memory from <paramref name="addressArg"/> — the <c>agb.poke &lt;addr&gt;
    /// &lt;bytes&gt;</c> debug mutation (outside replay determinism, so it drops the rewind ring).</summary>
    /// <param name="addressArg">A 0x-prefixed CPU address.</param>
    /// <param name="byteArgs">One or more hex byte tokens (each 0..0xFF).</param>
    public string Poke(string addressArg, string[] byteArgs) {
        ArgumentNullException.ThrowIfNull(byteArgs);

        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        if (!TryParseHex(text: addressArg, value: out var address) || (byteArgs.Length == 0)) {
            return "[agb.poke: usage — agb.poke <0xADDR> <byte> [byte ...] (each byte 0x00..0xFF)]";
        }

        if (!DebugConsoleFormat.TryParseBytes(tokens: byteArgs, bytes: out var bytes)) {
            return "[agb.poke: every byte must be 0x00..0xFF]";
        }

        for (var index = 0; (index < bytes.Length); ++index) {
            host.DebugWrite8(address: (address + (uint)index), value: bytes[index]);
        }

        // A poked byte is an unrecorded input; drop the rewind ring so time-travel never replays past the mutation.
        m_timeTravel?.Reset();

        return $"[agb.poke] wrote {bytes.Length} byte(s) at 0x{address:X8} (rewind history dropped)";
    }

    /// <summary>Disassembles <paramref name="countArg"/> instructions (default 8, max 64) from
    /// <paramref name="addressArg"/> (default R15) — the <c>agb.dis [addr] [n]</c> view. ARM vs THUMB follows the
    /// current CPSR T bit.</summary>
    /// <param name="addressArg">An optional 0x-prefixed start address; default is the current R15.</param>
    /// <param name="countArg">An optional instruction count.</param>
    public string Dis(string? addressArg, string? countArg) {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        var thumb = ((host.Cpsr & 0x20u) != 0u);
        var address = host.ProgramCounter;

        if ((addressArg is not null) && !TryParseHex(text: addressArg, value: out address)) {
            return "[agb.dis: usage — agb.dis [0xADDR] [n] (n 1..64, default 8; ARM/THUMB follows CPSR)]";
        }

        var count = 8;

        if ((countArg is not null) && (!int.TryParse(s: countArg, style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out count) || (count < 1))) {
            return "[agb.dis: n must be a positive integer]";
        }

        count = Math.Min(val1: count, val2: 64);

        var builder = new StringBuilder();

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[agb.dis {(thumb ? "THUMB" : "ARM")}] from 0x{address:X8}");

        for (var index = 0; (index < count); ++index) {
            if (thumb) {
                var halfword = host.DebugRead16(address: address);

                builder.Append(value: '\n');
                builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  0x{address:X8}: {halfword:X4}      {ArmDisassembler.DecodeThumb(address: address, instruction: halfword)}");
                address += 2u;
            } else {
                var wordValue = host.DebugRead32(address: address);

                builder.Append(value: '\n');
                builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  0x{address:X8}: {wordValue:X8}  {ArmDisassembler.DecodeArm(address: address, instruction: wordValue)}");
                address += 4u;
            }
        }

        return builder.ToString();
    }

    /// <summary>Reports the booted ROM, BIOS kind, paused state, frame count, master cycle counter, and framebuffer hash.</summary>
    public string Status() {
        if (Require(host: out var host) is { } guard) {
            return guard;
        }

        return string.Create(provider: CultureInfo.InvariantCulture, handler:
            $"[agb.status] rom={m_romName} ({m_romSource}) | bios={m_biosKind} [{host.BiosIdentity.Description}] | {(m_paused ? "PAUSED" : "running")} | frame={host.FrameCount} | cycles={host.Cycles} | PC=0x{host.ProgramCounter:X8} | fb=0x{host.FramebufferHash():X16}");
    }

    // ---- Resolution helpers -------------------------------------------------------------------------------------

    // Resolves the boot ROM: an explicit path, else the pending native/--rom ROM, else the built-in micro-ROM.
    private (byte[]? Rom, string Name, string Source, string? Error) ResolveRom(string? romPathArg) {
        var path = (string.IsNullOrWhiteSpace(value: romPathArg) ? m_pendingRomPath : romPathArg);

        if (!string.IsNullOrWhiteSpace(value: path)) {
            if (!File.Exists(path: path)) {
                return (null, "", "", $"[agb.debug: ROM '{path}' not found]");
            }

            try {
                return (File.ReadAllBytes(path: path), Path.GetFileName(path: path), $"file:{path}", null);
            } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                return (null, "", "", $"[agb.debug: ROM '{path}' unreadable — {exception.Message}]");
            }
        }

        return (AgbMicroRoms.Generate(kind: AgbMicroRoms.DefaultKind), $"built-in:{AgbMicroRoms.DefaultKind}", "generated", null);
    }

    // Resolves the BIOS for a boot: the runtime override (agb.bios) wins, then PUCK_AGB_BIOS (the emulator-level
    // convention), else the zeroed replacement stub (direct-boot safe).
    private (ReadOnlyMemory<byte> Bios, string Kind) ResolveBios() {
        if (m_biosOverride is { } overrideBios) {
            return (overrideBios, $"real (override: {m_biosOverridePath})");
        }

        var envPath = Environment.GetEnvironmentVariable(variable: "PUCK_AGB_BIOS");

        if (!string.IsNullOrEmpty(value: envPath) && File.Exists(path: envPath)) {
            try {
                var bytes = File.ReadAllBytes(path: envPath);

                if (bytes.Length == ReplacementBios.ImageSize) {
                    return (bytes, $"real (PUCK_AGB_BIOS: {envPath})");
                }

                Console.Error.WriteLine(value: $"[agb.bios: ignoring PUCK_AGB_BIOS='{envPath}' — {bytes.Length} bytes, expected {ReplacementBios.ImageSize}]");
            } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                Console.Error.WriteLine(value: $"[agb.bios: PUCK_AGB_BIOS='{envPath}' unreadable — {exception.Message}]");
            }
        }

        return (m_replacementBios, "replacement (zeroed stub)");
    }
    private string? Require(out AdvancedGamingBrickHost host) {
        host = m_host!;

        return ((m_host is null) ? "[agb: no scene — run `agb.debug [rom]` first]" : null);
    }

    // A compact CPU-state descriptor: the NZCV flags + ARM/THUMB + IRQ/FIQ mask state, decoded from CPSR.
    private static string DescribeState(AdvancedGamingBrickHost host) {
        var cpsr = host.Cpsr;
        var flags = new StringBuilder();

        flags.Append(value: (((cpsr & 0x80000000u) != 0u) ? 'N' : '-'));
        flags.Append(value: (((cpsr & 0x40000000u) != 0u) ? 'Z' : '-'));
        flags.Append(value: (((cpsr & 0x20000000u) != 0u) ? 'C' : '-'));
        flags.Append(value: (((cpsr & 0x10000000u) != 0u) ? 'V' : '-'));

        var thumb = ((cpsr & 0x20u) != 0u);

        return $"{flags} {(thumb ? "THUMB" : "ARM")} mode=0x{(cpsr & 0x1Fu):X2}";
    }
    private static bool TryParseHex(string text, out uint value) {
        var span = (text.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) ? text.AsSpan(start: 2) : text.AsSpan());

        return uint.TryParse(s: span, style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out value);
    }
}

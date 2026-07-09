using System.Globalization;
using System.Numerics;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.AdvancedGamingBrick;
using Puck.Hosting;
using Puck.HumbleGamingBrick;

namespace Puck.Demo.AgbDebug;

/// <summary>
/// The demo's native Game Boy Advance (ARM7TDMI) debug subsystem — the control plane behind the <c>agb.*</c> console
/// verb family and the fullscreen single-game AGB scene. It owns at most one live <see cref="AdvancedGamingBrickHost"/>,
/// the runtime BIOS override, the pending cartridge, and the paused/frame bookkeeping the execution-control verbs read
/// and mutate. It is reached by <see cref="AgbDebugCommandModule"/> through <see cref="IServiceProvider"/> — the
/// sanctioned "cannot add to the render node's host interface without exceeding its analyzer coupling ceiling" escape
/// (the render node and the overworld frame source both sit at their exact CA1506 ceilings; see
/// <see cref="Puck.Commands.ICommandModule"/>'s remarks and the Tracker precedent) — and it exposes only PRIMITIVE
/// render-facing members (<see cref="Active"/>, <see cref="ViewHandle"/>, <see cref="Glow"/>) the render node forwards,
/// so integrating it never adds a facade TYPE to either ceilinged class.
/// <para>Determinism note: the debug scene is greenfield presentation — the machine advances one whole frame per
/// produced frame while running (the host owns cadence), and the joypad arrives as the same per-tick sampled input the
/// SM83 cabinets use. The ARM core itself is fully deterministic given the cycles it runs, so <c>agb.frame</c>'s
/// framebuffer hash reproduces exactly.</para>
/// </summary>
internal sealed class AgbDebugService {
    // Real GBA hardware caps a forward `agb.until` run: ~50M instructions is a few seconds of emulated time — enough to
    // reach any boot-time PC without hanging the console shell if the target never occurs.
    private const long UntilInstructionCap = 50_000_000L;
    private const int TraceInstructionCap = 1000;
    // In-memory savestate slots the agb.snap/agb.restore verbs address (default slot 0). A reboot clears them, since a
    // snapshot only restores into a machine of the same BIOS/ROM identity.
    private const int SnapshotSlotCount = 4;

    private readonly IServiceProvider m_services;
    private readonly byte[] m_replacementBios = new byte[ReplacementBios.ImageSize]; // the zeroed default (direct-boot safe)
    private readonly AgbMachineSnapshot?[] m_snapshots = new AgbMachineSnapshot?[SnapshotSlotCount];
    private readonly long[] m_snapshotFrames = new long[SnapshotSlotCount];

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
    private ushort m_keyInput = 0x03FF; // active-low: every button released

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

    /// <summary>Maps the per-tick sampled joypad state onto the active-low KEYINPUT register the running machine reads
    /// before its next frame. Left/Right shoulder buttons have no SM83 equivalent and stay released.</summary>
    /// <param name="buttons">The buttons currently held (the same value the SM83 cabinets consume).</param>
    public void SetJoypad(JoypadButtons buttons) {
        var keys = 0x03FF; // every bit set = released

        keys = Press(keys, buttons, JoypadButtons.A, bit: 0);
        keys = Press(keys, buttons, JoypadButtons.B, bit: 1);
        keys = Press(keys, buttons, JoypadButtons.Select, bit: 2);
        keys = Press(keys, buttons, JoypadButtons.Start, bit: 3);
        keys = Press(keys, buttons, JoypadButtons.Right, bit: 4);
        keys = Press(keys, buttons, JoypadButtons.Left, bit: 5);
        keys = Press(keys, buttons, JoypadButtons.Up, bit: 6);
        keys = Press(keys, buttons, JoypadButtons.Down, bit: 7);

        m_keyInput = (ushort)keys;
    }

    private static int Press(int keys, JoypadButtons held, JoypadButtons button, int bit) =>
        (held.HasFlag(flag: button) ? (keys & ~(1 << bit)) : keys);

    /// <summary>The per-produced-frame render-thread work: applies input, advances the machine one whole frame while
    /// running (never while paused — the host owns cadence), and uploads the framebuffer to a shader-readable view. A
    /// no-op when the scene is down or the GPU device is unavailable this frame.</summary>
    /// <param name="context">The frame context (its host resolves the live GPU device).</param>
    public void ProduceFrame(in FrameContext context) {
        if ((m_host is not { } host) || !m_active) {
            return;
        }

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var gpuDevice)) {
            return;
        }

        host.SetKeyInput(keys: m_keyInput);

        if (!m_paused) {
            _ = host.RunFrame();
        }

        var gpu = (IGpuComputeServices)m_services.GetService(serviceType: typeof(IGpuComputeServices))!;

        host.Produce(gpu: gpu, deviceContext: gpuDevice);
    }

    /// <summary>Drops the GPU upload after a device loss; the machine survives.</summary>
    public void OnDeviceLost() => m_host?.OnDeviceLost();

    /// <summary>Tears the machine (and its GPU upload) down at host shutdown — called by the render node's Dispose
    /// BEFORE the GPU device is destroyed, so no upload leaks past the device. Idempotent.</summary>
    public void Shutdown() {
        m_active = false;
        m_paused = false;
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
            return error ?? "[agb.debug: could not resolve a ROM]";
        }

        m_host?.Dispose();

        var (bios, biosKind) = ResolveBios();

        try {
            m_host = AdvancedGamingBrickHost.Build(bios: bios, rom: rom);
        }
        catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            m_host = null;

            return $"[agb.debug: failed to boot '{name}' — {exception.Message}]";
        }

        m_active = true;
        m_paused = false;
        m_romName = name;
        m_romSource = source;
        m_biosKind = biosKind;
        m_keyInput = 0x03FF;

        // A fresh machine has a new BIOS/ROM identity; stale slots from a previous boot would refuse to restore, so
        // clear them rather than leave a trap.
        Array.Clear(array: m_snapshots);
        Array.Clear(array: m_snapshotFrames);

        return $"[agb.debug on] booted {name} ({source}); bios={biosKind}, running — agb.pause to freeze, agb.status for state";
    }

    /// <summary>Leaves the fullscreen AGB scene and tears the machine down. Returns the narration.</summary>
    public string Exit() {
        if (!m_active) {
            return "[agb.debug: not active]";
        }

        m_active = false;
        m_paused = false;
        m_host?.Dispose();
        m_host = null;

        return "[agb.debug off] the native AGB scene closed";
    }

    /// <summary>Sets the BIOS image used on the NEXT boot from a file path (the runtime override honored above
    /// <c>PUCK_GBA_BIOS</c>). A wrong-sized or unreadable file is rejected and the current default kept.</summary>
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
        }
        catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            return $"[agb.bios: '{path}' unreadable — {exception.Message}]";
        }
    }

    // ---- Execution-control verbs --------------------------------------------------------------------------------

    /// <summary>Freezes the host cadence (the machine stops advancing until resumed or single-stepped).</summary>
    public string Pause() =>
        Require(out var host) ?? SetPaused(host: host, paused: true, verb: "pause");

    /// <summary>Resumes host cadence (the machine advances one frame per produced frame again).</summary>
    public string Resume() =>
        Require(out _) ?? SetPaused(host: m_host!, paused: false, verb: "resume");

    private string SetPaused(AdvancedGamingBrickHost host, bool paused, string verb) {
        m_paused = paused;

        return $"[agb.{verb}] {(paused ? "paused" : "running")} at PC=0x{host.ProgramCounter:X8} frame={host.FrameCount}";
    }

    /// <summary>Single-steps <paramref name="count"/> instructions while paused; echoes the final PC/CPSR.</summary>
    /// <param name="count">The instruction count (default 1, min 1).</param>
    public string Step(int count) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if (!m_paused) {
            return "[agb.step: pause first (agb.pause) — stepping only makes sense while the host cadence is frozen]";
        }

        var n = Math.Max(1, count);

        for (var i = 0; (i < n); ++i) {
            host.Step();
        }

        return $"[agb.step {n}] PC=0x{host.ProgramCounter:X8} CPSR=0x{host.Cpsr:X8} ({DescribeState(host: host)})";
    }

    /// <summary>Runs <paramref name="count"/> whole frames while paused; echoes the frame count + framebuffer hash.</summary>
    /// <param name="count">The frame count (default 1, min 1).</param>
    public string Frame(int count) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if (!m_paused) {
            return "[agb.frame: pause first (agb.pause) — the running scene already advances a frame each produced frame]";
        }

        var n = Math.Max(1, count);

        for (var i = 0; (i < n); ++i) {
            _ = host.RunFrame();
        }

        return $"[agb.frame {n}] frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} fb=0x{host.FramebufferHash():X16}";
    }

    /// <summary>Captures the whole-machine savestate into an in-memory slot (default 0); echoes the frame/PC/hash and
    /// the image size. Works whether running or paused — the capture is instantaneous.</summary>
    /// <param name="slot">The slot to store into (0..3).</param>
    public string Snap(int slot) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if ((slot < 0) || (slot >= SnapshotSlotCount)) {
            return $"[agb.snap: slot must be 0..{SnapshotSlotCount - 1}]";
        }

        var snapshot = host.Snapshot();

        m_snapshots[slot] = snapshot;
        m_snapshotFrames[slot] = host.FrameCount;

        return $"[agb.snap {slot}] captured frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} cycle={host.Cycles} fb=0x{host.FramebufferHash():X16} ({snapshot.Size} bytes)";
    }

    /// <summary>Restores the whole-machine savestate from an in-memory slot (default 0); echoes the restored
    /// frame/PC/hash. Rewinds or fast-forwards the machine to the captured instant with no reboot.</summary>
    /// <param name="slot">The slot to restore from (0..3).</param>
    public string Restore(int slot) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if ((slot < 0) || (slot >= SnapshotSlotCount)) {
            return $"[agb.restore: slot must be 0..{SnapshotSlotCount - 1}]";
        }

        if (m_snapshots[slot] is not { } snapshot) {
            return $"[agb.restore {slot}: slot empty — agb.snap {slot} first]";
        }

        try {
            host.RestoreState(snapshot: snapshot, frameCount: m_snapshotFrames[slot]);
        }
        catch (InvalidOperationException exception) {
            return $"[agb.restore {slot}: cannot restore — {exception.Message}]";
        }

        return $"[agb.restore {slot}] restored frame={host.FrameCount} PC=0x{host.ProgramCounter:X8} cycle={host.Cycles} fb=0x{host.FramebufferHash():X16}";
    }

    /// <summary>Runs instructions until R15 equals <paramref name="targetHex"/> (a 0x-prefixed PC), capped at 50M —
    /// FORWARD ONLY (the core has no rewind). Echoes hit-or-cap and the final PC.</summary>
    /// <param name="targetHex">The target program counter as a hex string.</param>
    public string Until(string targetHex) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if (!TryParseHex(text: targetHex, value: out var target)) {
            return "[agb.until: usage — agb.until <hex-pc> (e.g. agb.until 0x08000010)]";
        }

        long executed = 0;

        while ((executed < UntilInstructionCap) && (host.ProgramCounter != target)) {
            host.Step();
            ++executed;
        }

        var hit = (host.ProgramCounter == target);

        return $"[agb.until 0x{target:X8}] {(hit ? "HIT" : "CAP")} after {executed} instruction(s); PC=0x{host.ProgramCounter:X8} CPSR=0x{host.Cpsr:X8}";
    }

    /// <summary>Traces the next <paramref name="count"/> instructions (capped at 1000): per instruction the fetched
    /// opcode, PC, ARM/THUMB state, CPSR, and the register deltas it produced. Mirrors the POST <c>--statetrace</c>
    /// shape, plus the opcode text the side-effect-free bus peek now makes possible.</summary>
    /// <param name="count">The instruction count (default 1, min 1, max 1000).</param>
    public string Trace(int count) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        var n = Math.Clamp(value: count, min: 1, max: TraceInstructionCap);
        var before = new uint[16];
        var builder = new StringBuilder();

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[agb.trace {n}] (PC state BEFORE each instruction; deltas after)");

        for (var i = 0; (i < n); ++i) {
            for (var r = 0; (r < 16); ++r) {
                before[r] = host.GetRegister(index: r);
            }

            var pc = host.ProgramCounter;
            var thumb = ((host.Cpsr & 0x20u) != 0u);
            // R15 is the fetch-stage address (the architectural PC+8/PC+4 offset); the instruction Step() is about
            // to execute is one fetch-step behind it — the address FetchWord actually read into the execute slot.
            var opcodeAddress = pc - (thumb ? 2u : 4u);
            var opcodeText = thumb
                ? $"{host.DebugRead16(address: opcodeAddress):X4}"
                : $"{host.DebugRead32(address: opcodeAddress):X8}";

            host.Step();

            builder.Append(value: '\n');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  {i,4}: PC=0x{pc:X8} {(thumb ? 'T' : 'A')} op=0x{opcodeText} CPSR=0x{host.Cpsr:X8}");

            for (var r = 0; (r < 16); ++r) {
                var now = host.GetRegister(index: r);

                if (now != before[r]) {
                    builder.Append(provider: CultureInfo.InvariantCulture, handler: $" r{r}:0x{before[r]:X8}->0x{now:X8}");
                }
            }
        }

        return builder.ToString();
    }

    /// <summary>Dumps r0–r15 + CPSR (with the decoded NZCV/ARM-THUMB/mode), plus the banked SPSR when the current
    /// mode has one (omitted in User/System mode, which has none).</summary>
    public string Regs() {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        var builder = new StringBuilder();

        builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[agb.regs] CPSR=0x{host.Cpsr:X8} ({DescribeState(host: host)})");

        if (host.TryGetSpsr(out var spsr)) {
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $" SPSR=0x{spsr:X8}");
        }

        for (var r = 0; (r < 16); ++r) {
            builder.Append(value: ((r % 4) == 0) ? '\n' : ' ');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  r{r,-2}=0x{host.GetRegister(index: r):X8}");
        }

        return builder.ToString();
    }

    /// <summary>Dumps one I/O register halfword (a 0x-prefixed offset) or, with no argument, the whole 0x000–0x3FE I/O
    /// block — the same <c>IO &lt;offset&gt; &lt;value&gt;</c> shape as the POST <c>--iodump</c>.</summary>
    /// <param name="offsetArg">An optional 0x-prefixed offset; null dumps the whole block.</param>
    public string Io(string? offsetArg) {
        if (Require(out var host) is { } guard) {
            return guard;
        }

        if (offsetArg is not null) {
            if (!TryParseHex(text: offsetArg, value: out var offset) || (offset >= 0x400u)) {
                return "[agb.io: usage — agb.io [0x000..0x3FE] (omit for the whole block)]";
            }

            return $"[agb.io] IO {(offset & ~1u):X3} {host.DebugReadIo(offset: (offset & ~1u)):X4}";
        }

        var builder = new StringBuilder();

        builder.Append(value: "[agb.io] whole I/O block (halfwords):");

        for (var offset = 0u; (offset < 0x400u); offset += 2u) {
            builder.Append(value: '\n');
            builder.Append(provider: CultureInfo.InvariantCulture, handler: $"  IO {offset:X3} {host.DebugReadIo(offset: offset):X4}");
        }

        return builder.ToString();
    }

    /// <summary>Reports the booted ROM, BIOS kind, paused state, frame count, master cycle counter, and framebuffer hash.</summary>
    public string Status() {
        if (Require(out var host) is { } guard) {
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
            }
            catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                return (null, "", "", $"[agb.debug: ROM '{path}' unreadable — {exception.Message}]");
            }
        }

        return (AgbMicroRoms.Generate(kind: AgbMicroRoms.DefaultKind), $"built-in:{AgbMicroRoms.DefaultKind}", "generated", null);
    }

    // Resolves the BIOS for a boot: the runtime override (agb.bios) wins, then PUCK_GBA_BIOS (the emulator-level
    // convention), else the zeroed replacement stub (direct-boot safe).
    private (ReadOnlyMemory<byte> Bios, string Kind) ResolveBios() {
        if (m_biosOverride is { } overrideBios) {
            return (overrideBios, $"real (override: {m_biosOverridePath})");
        }

        var envPath = Environment.GetEnvironmentVariable(variable: "PUCK_GBA_BIOS");

        if (!string.IsNullOrEmpty(value: envPath) && File.Exists(path: envPath)) {
            try {
                var bytes = File.ReadAllBytes(path: envPath);

                if (bytes.Length == ReplacementBios.ImageSize) {
                    return (bytes, $"real (PUCK_GBA_BIOS: {envPath})");
                }

                Console.Error.WriteLine(value: $"[agb.bios: ignoring PUCK_GBA_BIOS='{envPath}' — {bytes.Length} bytes, expected {ReplacementBios.ImageSize}]");
            }
            catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
                Console.Error.WriteLine(value: $"[agb.bios: PUCK_GBA_BIOS='{envPath}' unreadable — {exception.Message}]");
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

        flags.Append(value: ((cpsr & 0x80000000u) != 0u) ? 'N' : '-');
        flags.Append(value: ((cpsr & 0x40000000u) != 0u) ? 'Z' : '-');
        flags.Append(value: ((cpsr & 0x20000000u) != 0u) ? 'C' : '-');
        flags.Append(value: ((cpsr & 0x10000000u) != 0u) ? 'V' : '-');

        var thumb = ((cpsr & 0x20u) != 0u);

        return $"{flags} {(thumb ? "THUMB" : "ARM")} mode=0x{(cpsr & 0x1Fu):X2}";
    }

    private static bool TryParseHex(string text, out uint value) {
        var span = (text.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) ? text.AsSpan(start: 2) : text.AsSpan());

        return uint.TryParse(s: span, style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out value);
    }
}

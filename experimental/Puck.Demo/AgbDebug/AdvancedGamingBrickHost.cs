using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.AdvancedGamingBrick;
using Puck.Demo.Audio;
using Puck.Hosting;
using Puck.Snapshots;

namespace Puck.Demo.AgbDebug;

/// <summary>
/// A single native ARM7TDMI machine hosted for the demo — the real
/// <see cref="AdvancedGamingBrickMachine"/> core, direct-booted from a cartridge ROM, plus the minimal GPU path that
/// publishes its 240×160 <c>0xAARRGGBB</c> framebuffer as a shader-readable image view a diegetic screen samples, and
/// the OS speaker stream that drains its APU. It owns its own DI scope (so two machines never share a peripheral,
/// mirroring the POST battery's <c>PostMachine</c>), and exposes the raw stepping/inspection surface the <c>agb.*</c>
/// execution-control verbs drive. The host owns no cadence policy of its own: the caller decides when to advance it —
/// by an exact master-cycle budget (<see cref="RunCycles"/>) each engine frame while running, or one instruction /
/// one whole frame at a time while paused.
/// </summary>
internal sealed class AdvancedGamingBrickHost : IDisposable, ITimeTravelMachineCore<AgbSceneInput> {
    /// <summary>The native AGB screen width in pixels.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The native AGB screen height in pixels.</summary>
    public const int ScreenHeight = 160;
    /// <summary>The native AGB screen aspect (240×160 = 3:2) — the diegetic slab is sized to it.</summary>
    public const float ScreenAspect = ((float)ScreenWidth / ScreenHeight);

    private readonly AgbMachineInstance m_instance;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbCartridge m_cartridge;
    private readonly AgbBus? m_concreteBus;
    private readonly CabinetAudioOutput? m_audioOutput;
    private readonly AdvancedAudioMachine? m_audioMachine;
    private readonly byte[] m_rgba = new byte[((ScreenWidth * ScreenHeight) * 4)];
    private readonly StateWriter m_timeTravelWriter = new(capacity: 4096);
    private IGpuSurfaceUpload? m_upload;
    private nint m_viewHandle;
    private Vector3 m_averageColor;
    private bool m_disposed;

    private AdvancedGamingBrickHost(AgbMachineInstance instance) {
        m_instance = instance;
        m_machine = instance.Machine;
        m_cartridge = instance.GetRequiredService<AgbCartridge>();
        m_concreteBus = (instance.Machine.Bus as AgbBus);

        // Real speakers for the takeover scene: open its own OS output stream (the OS mixes each open stream, exactly
        // like a booted SM83 cabinet) and enable the APU's host resample ring at that rate. When no device opens
        // (headless / non-Windows) the APU output stays off (rate zero) and PumpAudio moves nothing — a silent scene
        // pays nothing. Host configuration, never emulated state: the output ring is snapshot-excluded, so whether or
        // when the host drains it can never perturb the simulation. The neutral adapter is built once here (never
        // per pump) so PumpAudio's hot path allocates nothing.
        m_audioOutput = CabinetAudioOutput.TryOpen();

        if (m_audioOutput is not null) {
            m_machine.Apu.ConfigureOutput(sampleRate: CabinetAudioOutput.SampleRate);
            m_audioMachine = new AdvancedAudioMachine(apu: m_machine.Apu);
        }
    }

    /// <summary>Builds an isolated, direct-booted machine for a BIOS image and cartridge ROM. Mirrors the POST
    /// battery's <c>PostMachine.Build</c> (both are thin wrappers over the core's <see cref="AgbMachineFactory"/>) so
    /// the two cores boot identically.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image (a zeroed stub is valid under direct boot).</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The assembled, direct-booted host. The caller owns it and must dispose it.</returns>
    public static AdvancedGamingBrickHost Build(ReadOnlyMemory<byte> bios, byte[] rom) {
        var instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: rom));

        instance.Machine.DirectBoot();

        return new AdvancedGamingBrickHost(instance: instance);
    }

    /// <summary>The number of whole ~280,896-cycle frames the master clock has advanced since boot — derived from the
    /// cycle counter, the single source of truth, so it stays correct across the tick-driven partial-frame budgets the
    /// running scene consumes and is repositioned automatically by a snapshot restore (which restores the clock).</summary>
    public long FrameCount => (Cycles / AdvancedGamingBrickMachine.CyclesPerFrame);

    /// <summary>The pre-flight identity (classification + content hash) of the BIOS this machine booted with.</summary>
    public AgbBiosIdentity BiosIdentity => m_machine.BiosIdentity;

    /// <summary>The master-clock cycle counter (0 when the bus is a bare test bus).</summary>
    public long Cycles => (m_concreteBus?.Cycles ?? 0L);

    /// <summary>The most recent framebuffer's average colour (0..1) — the light a diegetic screen emits into the room.</summary>
    public Vector3 AverageColor => m_averageColor;

    /// <summary>The shader-readable framebuffer view handle (0 until the first
    /// <see cref="Produce(IGpuComputeServices, IGpuDeviceContext)"/> call).</summary>
    public nint ViewHandle => m_viewHandle;

    /// <summary>Executes exactly one instruction (or a pending exception entry).</summary>
    public void Step() => m_machine.Step();

    /// <summary>Runs one whole frame (~280,896 cycles) — the tick-independent unit the <c>agb.frame</c> debug verb
    /// advances while paused. Returns the number of instructions executed.</summary>
    public int RunFrame() => m_machine.RunFrame();

    /// <summary>Advances the machine by an exact master-cycle budget — the seam the running scene drives each engine
    /// frame with the cycles that frame's tick budget bought, so emulated time is tick-locked, not coupled to the
    /// produced-frame rate. Returns the number of instructions executed.</summary>
    /// <param name="cycles">The master-cycle budget to advance this frame.</param>
    public int RunCycles(long cycles) => m_machine.RunCycles(cycles: cycles);

    /// <summary>Drains the APU's host output ring to the speaker stream — a pure read of host-facing audio, called
    /// after the machine advances. A no-op when no stream opened. Output-only, so host audio can never perturb the
    /// simulation.</summary>
    public void PumpAudio() => m_audioOutput?.Pump(machine: m_audioMachine!);

    /// <summary>Captures the machine's entire mutable state into a self-contained snapshot (the whole-machine
    /// savestate). Restore it into this host to rewind.</summary>
    public AgbMachineSnapshot Snapshot() => m_machine.Snapshot();

    /// <summary>Restores a snapshot into this machine, repositioning every component and the master clock — which
    /// re-derives <see cref="FrameCount"/> and <see cref="Cycles"/>. Throws if the snapshot's identity (BIOS/ROM) does
    /// not match this machine.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    public void RestoreState(AgbMachineSnapshot snapshot) {
        m_machine.Restore(snapshot: snapshot);
    }

    /// <summary>Sets the active-low KEYINPUT register (clear bit = pressed).</summary>
    public void SetKeyInput(ushort keys) => m_machine.SetKeyInput(keys: keys);

    /// <summary>Sets the cartridge's recorded light-level reading for the Boktai-style solar sensor, 0 (darkest) to
    /// 255 (brightest) — recorded per-frame host input, held constant until the next call, same discipline as
    /// <see cref="SetKeyInput"/>. A no-op on a cartridge with no solar sensor.</summary>
    public void SetLightLevel(byte level) => m_cartridge.SetLightLevel(level: level);

    /// <summary>Gets whether the booted cartridge exposes the Boktai-style solar sensor.</summary>
    public bool HasSolarSensor => m_cartridge.HasSolar;

    // ---- Machine-neutral time-travel seam (ITimeTravelMachineCore<AgbSceneInput>) --------------------------------
    // The AGB debug scene drives the SAME build-once MachineTimeTravel layer the queued hosts drive; this host's
    // held-input image is the full AgbSceneInput (KEYINPUT + recorded solar light + tilt), so a rewind replays and a
    // lookahead predicts the sensor stream exactly like the neutral hosts. Explicit implementation keeps the host's own
    // inspection surface (Snapshot/RestoreState/RunFrame/Cycles) unshadowed.

    /// <inheritdoc/>
    long ITimeTravelMachineCore<AgbSceneInput>.CycleCount => Cycles;

    /// <inheritdoc/>
    long ITimeTravelMachineCore<AgbSceneInput>.NativeFrameIndex => FrameCount;

    /// <inheritdoc/>
    ReadOnlySpan<uint> ITimeTravelMachineCore<AgbSceneInput>.Framebuffer => m_machine.Framebuffer;

    /// <inheritdoc/>
    int ITimeTravelMachineCore<AgbSceneInput>.CaptureState(ref byte[] buffer) {
        m_timeTravelWriter.Reset();
        m_machine.SerializeState(writer: m_timeTravelWriter);

        var written = m_timeTravelWriter.WrittenSpan;

        if (buffer.Length < written.Length) {
            buffer = new byte[written.Length];
        }

        written.CopyTo(destination: buffer);

        return written.Length;
    }

    /// <inheritdoc/>
    void ITimeTravelMachineCore<AgbSceneInput>.RestoreState(byte[] buffer, int length) =>
        m_machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    void ITimeTravelMachineCore<AgbSceneInput>.ApplyInput(in AgbSceneInput input) {
        m_machine.SetKeyInput(keys: input.Keys);
        m_cartridge.SetLightLevel(level: input.Light);
        m_cartridge.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    void ITimeTravelMachineCore<AgbSceneInput>.RunCycles(long cycles) => _ = m_machine.RunCycles(cycles: cycles);

    /// <inheritdoc/>
    ITimeTravelLookahead<AgbSceneInput> ITimeTravelMachineCore<AgbSceneInput>.CreateLookahead() =>
        new AgbSceneLookahead(instance: m_instance.Fork());

    /// <summary>Reads a general-purpose register (0–15) as the visible mode bank sees it.</summary>
    public uint GetRegister(int index) => m_machine.Cpu.GetRegister(index: index);

    /// <summary>The current program counter (R15).</summary>
    public uint ProgramCounter => m_machine.Cpu.GetRegister(index: 15);

    /// <summary>The current program status register.</summary>
    public uint Cpsr => m_machine.Cpu.Cpsr;

    /// <summary>Attempts to read the banked SPSR of the current mode (false in User/System mode, which has none).</summary>
    public bool TryGetSpsr(out uint spsr) => m_machine.Cpu.TryGetSpsr(spsr: out spsr);

    /// <summary>Reads an I/O register halfword without advancing the clock.</summary>
    public ushort DebugReadIo(uint offset) => (m_concreteBus?.DebugReadIo(offset: offset) ?? 0);

    /// <summary>Reads one byte without advancing the clock or any bus state — the side-effect-free peek behind
    /// <c>agb.peek</c>.</summary>
    /// <param name="address">The 32-bit CPU address.</param>
    /// <returns>The byte at <paramref name="address"/>.</returns>
    public byte DebugRead8(uint address) => (m_concreteBus?.DebugRead8(address: address) ?? 0);

    /// <summary>Forces one byte into a writable bus region without advancing the clock — the debug mutation behind
    /// <c>agb.poke</c>, outside replay determinism. A no-op on a bare test bus.</summary>
    /// <param name="address">The 32-bit CPU address.</param>
    /// <param name="value">The byte to store.</param>
    public void DebugWrite8(uint address, byte value) => m_concreteBus?.DebugWrite8(address: address, value: value);

    /// <summary>Reads a halfword without advancing the clock, prefetch buffer, open-bus latch, or DMA/EEPROM burst
    /// state — see <see cref="AgbBus.DebugRead16"/> for the region-by-region side-effect notes.</summary>
    public ushort DebugRead16(uint address) => (m_concreteBus?.DebugRead16(address: address) ?? 0);

    /// <summary>Reads a word without advancing the clock, prefetch buffer, open-bus latch, or DMA/EEPROM burst
    /// state — see <see cref="AgbBus.DebugRead16"/> for the region-by-region side-effect notes.</summary>
    public uint DebugRead32(uint address) => (m_concreteBus?.DebugRead32(address: address) ?? 0);

    /// <summary>The FNV-1a 64-bit hash of the current framebuffer — a deterministic render fingerprint (the same
    /// probe the POST render-hash stage uses: <see cref="MemoryMarshal.AsBytes{T}(ReadOnlySpan{T})"/> over the span).</summary>
    public ulong FramebufferHash() {
        const ulong offsetBasis = 0xCBF29CE484222325ul;
        const ulong prime = 0x100000001B3ul;

        var bytes = MemoryMarshal.AsBytes(span: m_machine.Framebuffer);
        var hash = offsetBasis;

        foreach (var value in bytes) {
            hash = ((hash ^ value) * prime);
        }

        return hash;
    }

    /// <summary>Uploads the current framebuffer to a shader-readable image view and refreshes the average colour — the
    /// per-frame GPU work, run on the render thread where a device context is available. Idempotent within a frame; the
    /// caller decides whether the machine stepped first.</summary>
    /// <param name="gpu">The world's GPU compute services (for the surface-upload factory).</param>
    /// <param name="deviceContext">The live GPU device context.</param>
    public void Produce(IGpuComputeServices gpu, IGpuDeviceContext deviceContext) =>
        Produce(gpu: gpu, deviceContext: deviceContext, source: m_machine.Framebuffer);

    /// <summary>Uploads an explicit <c>0xAARRGGBB</c> framebuffer (of the native 240×160 dimensions) to a
    /// shader-readable image view and refreshes the average colour — the seam runahead presentation uses to show the
    /// LOOKAHEAD machine's framebuffer while the real machine stays the authority. A wrong-sized source falls back to
    /// this host's own machine framebuffer, so the pane never shows garbage.</summary>
    /// <param name="gpu">The world's GPU compute services (for the surface-upload factory).</param>
    /// <param name="deviceContext">The live GPU device context.</param>
    /// <param name="source">The 240×160 <c>0xAARRGGBB</c> framebuffer to present.</param>
    public void Produce(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, ReadOnlySpan<uint> source) {
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(deviceContext);

        Repack(pixels: ((source.Length == (ScreenWidth * ScreenHeight)) ? source : m_machine.Framebuffer));

        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: deviceContext);
        m_viewHandle = m_upload.Upload(
            deviceContext: deviceContext,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: ScreenHeight,
            pixels: m_rgba,
            width: ScreenWidth
        );
    }

    /// <summary>Drops the GPU upload after a device loss; the machine (CPU state) survives untouched.</summary>
    public void OnDeviceLost() {
        m_upload?.Dispose();
        m_upload = null;
        m_viewHandle = 0;
    }

    // Repack a 0xAARRGGBB framebuffer as the R,G,B,A bytes the upload wants (alpha forced opaque — the PPU leaves it
    // clear), and accumulate the average colour for the diegetic screen light. The source is the machine's own
    // framebuffer normally, or the runahead lookahead's when presentation is showing the predicted frame.
    private void Repack(ReadOnlySpan<uint> pixels) {
        var rgba = m_rgba;
        var sumRed = 0;
        var sumGreen = 0;
        var sumBlue = 0;

        for (var index = 0; (index < pixels.Length); ++index) {
            var offset = (index * 4);
            var pixel = pixels[index];
            var red = (byte)(pixel >> 16);
            var green = (byte)(pixel >> 8);
            var blue = (byte)pixel;

            rgba[offset] = red;
            rgba[(offset + 1)] = green;
            rgba[(offset + 2)] = blue;
            rgba[(offset + 3)] = 0xFF;

            sumRed += red;
            sumGreen += green;
            sumBlue += blue;
        }

        var scale = ((pixels.Length == 0) ? 0f : (1f / (255f * pixels.Length)));

        m_averageColor = new Vector3(x: (sumRed * scale), y: (sumGreen * scale), z: (sumBlue * scale));
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_upload?.Dispose();
        m_audioOutput?.Dispose();
        m_instance.Dispose();
    }
}

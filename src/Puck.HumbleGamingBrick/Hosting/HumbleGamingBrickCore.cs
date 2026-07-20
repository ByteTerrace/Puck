using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83-family GamingBrick core adapted to the machine-neutral <see cref="IQueuedMachineCore"/>: it assembles the
/// machine, loads any battery save, and exposes the run/framebuffer/input/save surface a <see cref="QueuedMachineWorker"/>
/// drives, plus the bus peek/poke the host surfaces through <see cref="IMachineMemoryPeek"/>. Every machine-facing call —
/// stepping and the debug peek/poke alike — runs on the worker's single execution thread, so a peek/poke never races the
/// running core.
/// </summary>
internal sealed class HumbleGamingBrickCore : IQueuedMachineCore {
    // The machine's CPU T-cycle rate (2^22 per second); with EngineTicks.PerSecond it forms the exact rational the tick
    // accumulator carries remainders in.
    private const ulong MachineCyclesPerSecond = 4_194_304UL;
    // One native ~59.7 Hz video frame in LCD dots (154 scanlines of 456 dots) — the PPU's frame period, independent of the
    // KEY1 double-speed CPU multiplier, so it keys the native-frame count in every model.
    private const ulong DotsPerFrame = (154UL * 456UL);

    private readonly MachineInstance m_machine;
    private readonly IAudioSink m_audioSink;
    private readonly ICartridge m_cartridge;
    private readonly IFramebuffer m_framebuffer;
    private readonly IJoypad m_joypad;
    private readonly IKey1 m_key1;
    private readonly SystemBus m_systemBus;
    private readonly ITiltSensor m_tiltSensor;
    private readonly StateWriter m_timeTravelWriter = new(capacity: 4096);
    private readonly string? m_savePath;
    private readonly bool m_dmgSpeed;

    /// <summary>Assembles and save-loads the machine.</summary>
    /// <param name="model">The hardware model to emulate.</param>
    /// <param name="cartridgeRom">The cartridge ROM image.</param>
    /// <param name="savePath">The cartridge's battery-save path, or <see langword="null"/> for an in-memory-only save.</param>
    /// <param name="dmgSpeed">When <see langword="true"/>, the FAIRNESS pin: the tick-to-cycle budget stays at the DMG rate
    /// regardless of the KEY1 double-speed latch, so the budget is a function of configuration alone.</param>
    public HumbleGamingBrickCore(ConsoleModel model, byte[] cartridgeRom, string? savePath, bool dmgSpeed) {
        m_savePath = savePath;
        m_dmgSpeed = dmgSpeed;
        m_machine = MachineFactory.Create(
            configuration: new MachineConfiguration(model: model, cartridgeRom: cartridgeRom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );
        m_audioSink = m_machine.GetRequiredService<IAudioSink>();
        m_cartridge = m_machine.GetRequiredService<ICartridge>();
        m_framebuffer = m_machine.GetRequiredService<IFramebuffer>();
        m_joypad = m_machine.GetRequiredService<IJoypad>();
        m_key1 = m_machine.GetRequiredService<IKey1>();
        m_systemBus = m_machine.GetRequiredService<SystemBus>();
        m_tiltSensor = m_machine.GetRequiredService<ITiltSensor>();

        LoadBatterySave();
    }

    /// <inheritdoc/>
    public ulong CyclesPerSecond =>
        ((!m_dmgSpeed && m_key1.IsDoubleSpeed) ? (2UL * MachineCyclesPerSecond) : MachineCyclesPerSecond);

    /// <inheritdoc/>
    public long NativeFrameIndex =>
        (long)(m_machine.Machine.Clock.CycleCount / DotsPerFrame);

    /// <inheritdoc/>
    public long CycleCount =>
        (long)m_machine.Machine.Clock.CycleCount;

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer =>
        m_framebuffer.Pixels;

    /// <inheritdoc/>
    public void ApplyInput(in MachinePadState input) {
        m_joypad.SetButtons(pressed: BrickPad.ToJoypad(pad: in input));

        // Recorded per-segment sensor input: a no-op on any cartridge that never reads the tilt sensor.
        m_tiltSensor.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    public void RunCycles(long cycles) =>
        m_machine.Machine.Run(tCycles: (ulong)cycles);

    /// <inheritdoc/>
    public int CaptureState(ref byte[] buffer) {
        m_timeTravelWriter.Reset();
        m_machine.Machine.SerializeState(writer: m_timeTravelWriter);

        var written = m_timeTravelWriter.WrittenSpan;

        if (buffer.Length < written.Length) {
            buffer = new byte[written.Length];
        }

        written.CopyTo(destination: buffer);

        return written.Length;
    }

    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) =>
        m_machine.Machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    public ITimeTravelLookahead<MachinePadState> CreateLookahead() =>
        new HumbleGamingBrickLookahead(instance: m_machine.Fork(), oneFrameCycles: DotsPerFrame);

    /// <summary>Reads one byte from anywhere in the bus address space for the host's <see cref="IMachineMemoryPeek"/> —
    /// a side-effect-free poll (no clock advance, no lock masking), never a write into machine state.</summary>
    /// <param name="address">A 16-bit bus address.</param>
    /// <returns>The byte, or 0 for an out-of-range address.</returns>
    public byte PeekByte(int address) =>
        (((address < 0x0000) || (address > 0xFFFF)) ? (byte)0 : m_systemBus.DebugReadByte(address: (ushort)address));

    /// <summary>Forces one byte into a writable bus region for the host's <see cref="IMachineMemoryPeek"/> — a debug
    /// mutation outside replay determinism (the host drops rewind history). A no-op for an out-of-range address.</summary>
    /// <param name="address">A 16-bit bus address.</param>
    /// <param name="value">The byte to store.</param>
    public void PokeByte(int address, byte value) {
        if ((address >= 0x0000) && (address <= 0xFFFF)) {
            m_systemBus.DebugWriteByte(address: (ushort)address, value: value);
        }
    }

    /// <inheritdoc/>
    public bool Reconfigure(string? options, out string reason) {
        ConsoleModel model;

        try {
            (model, _) = GamingBrickEngine.ParseOptions(options: options);
        } catch (ArgumentException exception) {
            reason = exception.Message;

            return false;
        }

        // The live device swap (dmg<->cgb<->agb): retarget the emulated hardware WITHOUT a reboot, poking the game's
        // cached detection flag (from the recipe table, keyed by title) so a dual-mode cartridge re-renders natively.
        // The fairness pin is construction-fixed (it sizes the tick->cycle budget for determinism), so options only
        // move the model here; a bare capability flip with no recipe is honest, not a fake native retarget.
        var title = m_cartridge.Header.Title;
        var pokes = ConsoleModeRecipes.PokesFor(title: title, target: model);

        m_machine.Machine.SwitchModel(model: model, pokes: pokes);

        reason = ((pokes.Length > 0)
            ? string.Empty
            : $"no live detection recipe for '{title}'; the running game keeps its boot code path");

        return true;
    }

    /// <inheritdoc/>
    public void ConfigureAudio(int sampleRate) =>
        m_audioSink.Configure(sampleRate: sampleRate);

    /// <inheritdoc/>
    public int DrainAudioSamples(Span<short> destination) =>
        m_audioSink.ReadSamples(destination: destination);

    /// <inheritdoc/>
    public float MotorLevel =>
        m_cartridge.MotorLevel;

    /// <inheritdoc/>
    public void FlushSave(bool force) {
        if ((m_savePath is not { } savePath) || !m_cartridge.Header.HasBattery) {
            return;
        }

        var hasClock = (m_cartridge.PersistentClockByteCount > 0);
        var hasRam = (m_cartridge.ExternalRamByteCount > 0);

        if (!(m_cartridge.ExternalRamDirty || (force && hasClock)) || !(hasRam || hasClock)) {
            return;
        }

        try {
            byte[][] parts = [
                (hasRam ? m_cartridge.ExportExternalRam() : []),
                (hasClock ? m_cartridge.ExportPersistentClock(unixTimestampSeconds: DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : []),
            ];

            File.WriteAllBytes(path: savePath, bytes: [.. parts[0], .. parts[1]]);
            m_cartridge.MarkExternalRamClean();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[machine-host] battery-save flush to '{savePath}' failed ({exception.Message}); retrying on the next flush.");
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        FlushSave(force: true);
        m_machine.Dispose();
    }

    // The power-on cartridge read: a persisted battery save loads into the external RAM (and, for a mapper with
    // battery-backed timed hardware, the clock footer appended after it) before the machine runs — a configuration input to
    // the deterministic timeline. The clock RESUMES where the last flush left it; any embedded wall timestamp is ignored.
    private void LoadBatterySave() {
        if ((m_savePath is not { } savePath) || !m_cartridge.Header.HasBattery || !File.Exists(path: savePath)) {
            return;
        }

        try {
            var save = File.ReadAllBytes(path: savePath);
            var ramByteCount = m_cartridge.ExternalRamByteCount;

            if (ramByteCount > 0) {
                m_cartridge.ImportExternalRam(source: save.AsSpan(start: 0, length: Math.Min(val1: save.Length, val2: ramByteCount)));
            }

            if ((m_cartridge.PersistentClockByteCount > 0) && (save.Length >= (ramByteCount + m_cartridge.PersistentClockByteCount))) {
                m_cartridge.ImportPersistentClock(source: save.AsSpan(start: ramByteCount, length: m_cartridge.PersistentClockByteCount));
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[machine-host] battery save '{savePath}' unreadable ({exception.Message}); booting with fresh external RAM.");
        }
    }
}

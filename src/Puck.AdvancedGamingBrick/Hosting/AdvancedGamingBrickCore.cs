using Puck.Abstractions.Machines;
using Puck.Hosting;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The native ARM7TDMI AdvancedGamingBrick core adapted to the machine-neutral <see cref="IQueuedMachineCore"/>: it builds
/// and direct-boots the machine, loads any battery save, and exposes the run/framebuffer/input/save surface a
/// <see cref="QueuedMachineWorker"/> drives. All machine-facing calls run on the worker's execution thread.
/// </summary>
internal sealed class AdvancedGamingBrickCore : IQueuedMachineCore {
    private const ulong MachineCyclesPerSecond = 16_777_216UL;

    private readonly AgbMachineInstance m_instance;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbCartridge m_cartridge;
    private readonly StateWriter m_timeTravelWriter = new(capacity: 4096);
    private readonly string? m_savePath;

    /// <summary>Builds, save-loads, and direct-boots the native machine.</summary>
    /// <param name="bios">The 16 KiB BIOS image.</param>
    /// <param name="cartridgeRom">The native AGB cartridge image.</param>
    /// <param name="savePath">The optional battery-save path.</param>
    public AdvancedGamingBrickCore(byte[] bios, byte[] cartridgeRom, string? savePath) {
        m_savePath = savePath;
        m_instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: cartridgeRom));
        m_machine = m_instance.Machine;
        m_cartridge = m_instance.GetRequiredService<AgbCartridge>();

        LoadBatterySave();
        m_machine.DirectBoot();
    }

    /// <inheritdoc/>
    public ulong CyclesPerSecond =>
        MachineCyclesPerSecond;

    /// <inheritdoc/>
    public long NativeFrameIndex =>
        (m_machine.Cycles / AdvancedGamingBrickMachine.CyclesPerFrame);

    /// <inheritdoc/>
    public long CycleCount =>
        m_machine.Cycles;

    /// <inheritdoc/>
    public ReadOnlySpan<uint> Framebuffer =>
        m_machine.Framebuffer;

    /// <inheritdoc/>
    public void ApplyInput(in MachinePadState input) {
        m_machine.SetKeyInput(keys: AdvancedPad.ToKeyInput(pad: in input));

        // Sensor channels: recorded per-segment host input, held constant for the whole cycle budget like every other
        // pad field — never a live read from inside the core. A no-op on a cartridge with no matching sensor.
        m_cartridge.SetLightLevel(level: input.LightLevel);
        m_cartridge.SetTilt(x: input.Tilt.X, y: input.Tilt.Y);
    }

    /// <inheritdoc/>
    public void RunCycles(long cycles) =>
        _ = m_machine.RunCycles(cycles: cycles);

    /// <inheritdoc/>
    public int CaptureState(ref byte[] buffer) {
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
    public void RestoreState(byte[] buffer, int length) =>
        m_machine.RestoreState(reader: new StateReader(buffer: buffer, start: 0, length: length));

    /// <inheritdoc/>
    public ITimeTravelLookahead<MachinePadState> CreateLookahead() =>
        new AdvancedGamingBrickLookahead(instance: m_instance.Fork());

    /// <inheritdoc/>
    public void ConfigureAudio(int sampleRate) =>
        m_machine.Apu.ConfigureOutput(sampleRate: sampleRate);

    /// <inheritdoc/>
    public int DrainAudioSamples(Span<short> destination) =>
        m_machine.Apu.DrainSamples(destination: destination);

    /// <inheritdoc/>
    public float MotorLevel =>
        m_cartridge.MotorLevel;

    /// <inheritdoc/>
    public void FlushSave(bool force) {
        if ((m_savePath is not { } savePath) || (m_cartridge is not { HasSave: true } cartridge) ||
            (!cartridge.SaveDirty && !(force && !File.Exists(path: savePath)))) {
            return;
        }

        try {
            File.WriteAllBytes(path: savePath, bytes: cartridge.SaveData.ToArray());
            cartridge.MarkSaveClean();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[advanced-machine-host] battery-save flush to '{savePath}' failed ({exception.Message}); retrying on the next flush.");
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        FlushSave(force: true);
        m_instance.Dispose();
    }

    private void LoadBatterySave() {
        if ((m_savePath is not { } savePath) || !m_cartridge.HasSave || !File.Exists(path: savePath)) {
            return;
        }

        try {
            if (!m_cartridge.LoadSave(data: File.ReadAllBytes(path: savePath))) {
                Console.Error.WriteLine(value: $"[advanced-machine-host] battery save '{savePath}' has an incompatible size; booting with fresh backup memory.");
            }
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine(value: $"[advanced-machine-host] battery save '{savePath}' unreadable ({exception.Message}); booting with fresh backup memory.");
        }
    }
}

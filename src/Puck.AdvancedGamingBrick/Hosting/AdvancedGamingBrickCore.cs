using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The native ARM7TDMI AdvancedGamingBrick core adapted to the machine-neutral <see cref="IQueuedMachineCore"/>: it builds
/// and starts the machine in its configured boot mode, loads any battery save, and exposes the run/framebuffer/input/save surface a
/// <see cref="QueuedMachineWorker"/> or a caller's own update loop drives. All machine-facing calls must run on one owning thread.
/// Restoring state requests a battery flush independently of the emulated dirty flag. Saves are flushed to a
/// temporary file beside their destination and then replace it; write failures retain the previous save for retry.
/// </summary>
public sealed partial class AdvancedGamingBrickCore : IQueuedMachineCore {
    private const ulong MachineCyclesPerSecond = 16_777_216UL;

    private readonly AgbMachineInstance m_instance;
    private readonly AdvancedGamingBrickMachine m_machine;
    private readonly AgbCartridge m_cartridge;
    private readonly StateWriter m_timeTravelWriter = new(capacity: 4096);
    private readonly string? m_savePath;
    // Host persistence state: the disk cannot rewind with an emulated snapshot's SaveDirty flag.
    private bool m_saveNeedsFlush;

    /// <summary>Builds a native machine with bundled Puck firmware, without a renderer or background worker.</summary>
    /// <param name="cartridgeRom">The native AGB cartridge image.</param>
    /// <param name="bootMode">Cold startup by default; fast startup skips presentation while retaining BIOS services.</param>
    /// <param name="savePath">The optional battery-save path.</param>
    public AdvancedGamingBrickCore(byte[] cartridgeRom, MachineBootMode bootMode = MachineBootMode.Cold, string? savePath = null)
        : this(configuration: AgbFirmware.CreateConfiguration(cartridgeRom: cartridgeRom, bootMode: bootMode), savePath: savePath) { }

    /// <summary>Builds, save-loads, and direct-boots the native machine.</summary>
    /// <param name="bios">An explicit 16 KiB BIOS image. Zeroed images support only BIOS-independent diagnostics.</param>
    /// <param name="cartridgeRom">The native AGB cartridge image.</param>
    /// <param name="savePath">The optional battery-save path.</param>
    public AdvancedGamingBrickCore(byte[] bios, byte[] cartridgeRom, string? savePath = null)
        : this(configuration: new AgbMachineConfiguration(bios: bios, rom: cartridgeRom), savePath: savePath) { }

    /// <summary>Builds a core for an external host's own update loop. No renderer, worker thread or disk save is
    /// required. The host supplies cycle budgets and input, drains output, and disposes the core on its owning thread.</summary>
    /// <param name="configuration">Explicit BIOS, cartridge, startup mode and per-machine options.</param>
    /// <param name="savePath">Optional battery-save path; null keeps saves in memory.</param>
    public AdvancedGamingBrickCore(AgbMachineConfiguration configuration, string? savePath = null) {
        m_savePath = savePath;
        m_instance = AgbMachineFactory.Create(configuration: configuration);
        m_machine = m_instance.Machine;
        m_cartridge = m_instance.GetRequiredService<AgbCartridge>();

        LoadBatterySave();
        if (configuration.BootMode == MachineBootMode.Fast) {
            m_machine.DirectBoot();
        }
    }

    /// <summary>Gets the owned machine instance for peripheral, cartridge and link access. Use it only on the
    /// core's owning thread; the core retains responsibility for disposal.</summary>
    public AgbMachineInstance Instance => m_instance;

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
    // The sensor channels ride the same seam as the buttons: recorded per-segment host input, held constant for the whole
    // cycle budget like every other pad field — never a live read from inside the core.
    public void ApplyInput(in MachinePadState input) =>
        AdvancedPad.Apply(
            cartridge: m_cartridge,
            machine: m_machine,
            pad: in input
        );
    /// <inheritdoc/>
    public void RunCycles(long cycles) =>
        _ = m_machine.RunCycles(cycles: cycles);
    /// <inheritdoc/>
    public int CaptureState(ref byte[] buffer) {
        m_timeTravelWriter.Reset();
        m_machine.SerializeState(writer: m_timeTravelWriter);
        return SnapshotBuffer.CopyWrittenState(
            buffer: ref buffer,
            writer: m_timeTravelWriter
        );
    }
    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) {
        m_machine.RestoreState(reader: new StateReader(
        buffer: buffer,
        length: length,
        start: 0
    ));
        m_saveNeedsFlush = true;
    }
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
        if (
            (m_savePath is not { } savePath) ||
            (m_cartridge is not { HasSave: true } cartridge) ||
            (!cartridge.SaveDirty && !m_saveNeedsFlush && !force)
        ) {
            return;
        }

        try {
            WriteBatterySave(path: savePath, data: cartridge.SaveData);
            cartridge.MarkSaveClean();
            m_saveNeedsFlush = false;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            m_saveNeedsFlush = true;
            Console.Error.WriteLine(value: $"[advanced-machine-host] battery-save flush to '{savePath}' failed ({exception.Message}); retrying on the next flush.");
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        FlushSave(force: true);
        m_instance.Dispose();
    }

    private static void WriteBatterySave(string path, ReadOnlySpan<byte> data) {
        var destination = Path.GetFullPath(path: path);
        var temporary = Path.Combine(path1: Path.GetDirectoryName(path: destination)!, path2: $".agb-save-{Guid.NewGuid():N}.tmp");
        try {
            // The temporary lives on the destination filesystem. Finish and flush it before the rename so a
            // failed write leaves the previous save intact. Only a successful replacement clears dirty state.
            using (var stream = new FileStream(path: temporary, mode: FileMode.CreateNew, access: FileAccess.Write, share: FileShare.None)) {
                stream.Write(buffer: data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(sourceFileName: temporary, destFileName: destination, overwrite: true);
        } finally {
            try {
                File.Delete(path: temporary);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine(value: $"[advanced-machine-host] temporary save cleanup failed ({exception.Message}).");
            }
        }
    }

    private void LoadBatterySave() {
        if (
            (m_savePath is not { } savePath) ||
            !m_cartridge.HasSave ||
            !File.Exists(path: savePath)
        ) {
            return;
        }

        try {
            if (!m_cartridge.LoadSave(data: File.ReadAllBytes(path: savePath))) {
                Console.Error.WriteLine(value: $"[advanced-machine-host] battery save '{savePath}' has an incompatible size; booting with fresh backup memory.");
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[advanced-machine-host] battery save '{savePath}' unreadable ({exception.Message}); booting with fresh backup memory.");
        }
    }
}

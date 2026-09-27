using System.Globalization;
using Puck.Assets;

namespace Puck.HumbleGamingDeck;

/// <summary>
/// The synchronous Deck core behind the queued host: it advances the machine by master ticks, folds the first two seats
/// of the held seat image onto the two controller ports, presents the last completed frame as <c>0x00RRGGBB</c> through
/// <see cref="HgdPalette"/>, drains the presentation-side audio stage, persists battery RAM, and captures and restores
/// the machine's whole state. It is usable directly, without a worker or renderer.
/// </summary>
public sealed class HumbleGamingDeckCore : IQueuedMachineCore {
    private readonly MachineInstance<HgdMachine, HgdMachineConfiguration> m_instance;
    private readonly HgdMachine m_machine;

    private readonly StateWriter m_writer = new(capacity: 4096);
    private readonly uint[] m_colours = new uint[(HgdPpu.Width * HgdPpu.Height)];

    private readonly string? m_savePath;

    private byte[] m_persistedSave = [];
    private long m_colouredFrame = -1;

    /// <summary>Initializes a new instance of the <see cref="HumbleGamingDeckCore"/> class from a cartridge image with the
    /// NTSC model and the default power-on profile.</summary>
    /// <param name="cartridgeImage">The iNES or NES 2.0 image.</param>
    /// <param name="savePath">The battery-save path, or <see langword="null"/> to keep battery RAM in memory only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="cartridgeImage"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The image's header is damaged, ambiguous, or names a board the Deck does not
    /// implement.</exception>
    public HumbleGamingDeckCore(byte[] cartridgeImage, string? savePath = null)
        : this(
        configuration: new HgdMachineConfiguration(cartridge: HgdCartridge.Load(image: (cartridgeImage ?? throw new ArgumentNullException(paramName: nameof(cartridgeImage))))),
        savePath: savePath
    ) { }
    /// <summary>Initializes a new instance of the <see cref="HumbleGamingDeckCore"/> class.</summary>
    /// <param name="configuration">The cartridge, model, and power-on profile.</param>
    /// <param name="savePath">The battery-save path, or <see langword="null"/> to keep battery RAM in memory only.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public HumbleGamingDeckCore(HgdMachineConfiguration configuration, string? savePath = null) {
        ArgumentNullException.ThrowIfNull(argument: configuration);

        var powerOn = configuration.PowerOn;

        CheckpointIdentity = MachineCheckpointIdentity.Compute(
            cartridge: configuration.Cartridge.Image,
            descriptor: string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"puck.hgd.core.v1/{HgdMachineIdentity.CurrentVersion}/{configuration.Model}/{powerOn.Name}/{powerOn.AlignmentPhase}/{powerOn.WorkRamFill}"
            ),
            firmware: []
        );
        m_savePath = savePath;
        m_instance = HgdMachineFactory.Create(configuration: configuration);
        m_machine = m_instance.Machine;
        LoadBatterySave();
    }

    /// <summary>Gets the machine this core drives. Use it only on the core's owning thread.</summary>
    public HgdMachine Machine => m_machine;
    /// <inheritdoc/>
    public string CheckpointIdentity {
        get;
    }
    /// <inheritdoc/>
    /// <remarks>The rate is the master clock's: <see cref="RunCycles"/> takes master ticks.</remarks>
    public MachineCycleRate CycleRate => m_machine.Clock.Rate;
    /// <inheritdoc/>
    public long CycleCount => ((long)m_machine.MasterTicks);
    /// <inheritdoc/>
    public long NativeFrameIndex => m_machine.FrameIndex;
    /// <inheritdoc/>
    /// <remarks>The last completed frame, 256 by 240, converted from pixel codes when a new frame has completed.</remarks>
    public ReadOnlySpan<uint> Framebuffer {
        get {
            if (m_colouredFrame != m_machine.FrameIndex) {
                HgdPalette.Convert(
                    codes: m_machine.Ppu.Frame,
                    colours: m_colours
                );
                m_colouredFrame = m_machine.FrameIndex;
            }

            return m_colours;
        }
    }

    /// <inheritdoc/>
    public void ApplyInput(in MachinePads input) =>
        HgdPad.Apply(
            controllers: m_machine.Controllers,
            pads: in input
        );
    /// <inheritdoc/>
    /// <remarks>The budget is in master ticks; a nonpositive budget does nothing.</remarks>
    public void RunCycles(long cycles) {
        if (cycles > 0) {
            m_machine.RunCycles(masterTicks: ((ulong)cycles));
        }
    }
    /// <inheritdoc/>
    public int CaptureState(ref byte[] buffer) {
        m_writer.Reset();
        m_machine.SerializeState(writer: m_writer);

        return SnapshotBuffer.CopyWrittenState(
            buffer: ref buffer,
            writer: m_writer
        );
    }
    /// <inheritdoc/>
    public void RestoreState(byte[] buffer, int length) {
        m_machine.RestoreState(reader: new StateReader(
            buffer: buffer,
            length: length,
            start: 0
        ));
        m_colouredFrame = -1;
    }
    /// <inheritdoc/>
    public ITimeTravelLookahead<MachinePads> CreateLookahead() =>
        new HumbleGamingDeckLookahead(fork: m_instance.Fork());
    /// <inheritdoc/>
    public void ConfigureAudio(int sampleRate) =>
        m_machine.Audio.Configure(sampleRate: sampleRate);
    /// <inheritdoc/>
    public int DrainAudioSamples(Span<short> destination) =>
        m_machine.Audio.Read(destination: destination);
    /// <inheritdoc/>
    /// <remarks>Reads the CPU address map without side effects; an address outside $0000-$FFFF reads as 0.</remarks>
    public byte PeekByte(int address) =>
        (((address < 0) || (address > 0xFFFF)) ? ((byte)0) : m_machine.Bus.Peek(address: ((ushort)address)));
    /// <inheritdoc/>
    /// <remarks>Only work RAM accepts a poke; any other address ignores it.</remarks>
    public void PokeByte(int address, byte value) {
        if ((address >= 0) && (address < 0x2000)) {
            m_machine.Bus.Poke(
                address: ((ushort)address),
                value: value
            );
        }
    }
    /// <inheritdoc/>
    public void FlushSave(bool force) {
        var battery = m_machine.Bus.Mapper.BatteryRam;

        if ((m_savePath is not { } savePath) || battery.IsEmpty || (!force && battery.SequenceEqual(other: m_persistedSave))) {
            return;
        }

        try {
            AtomicFile.WriteAllBytes(
                bytes: battery,
                path: savePath
            );
            m_persistedSave = battery.ToArray();
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"[humble-gaming-deck] battery-save flush to '{savePath}' failed ({exception.Message}); retrying on the next flush.");
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        FlushSave(force: true);
        m_instance.Dispose();
    }

    private void LoadBatterySave() {
        var battery = m_machine.Bus.Mapper.BatteryRam;

        if ((m_savePath is not { } savePath) || battery.IsEmpty || !File.Exists(path: savePath)) {
            return;
        }

        var saved = File.ReadAllBytes(path: savePath);

        if (saved.Length != battery.Length) {
            Console.Error.WriteLine(value: $"[humble-gaming-deck] battery save '{savePath}' holds {saved.Length} bytes, the board keeps {battery.Length}; starting from power-on RAM.");

            return;
        }

        saved.CopyTo(destination: battery);
        m_persistedSave = saved;
    }
}

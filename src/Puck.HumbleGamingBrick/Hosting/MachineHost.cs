using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83-family GamingBrick as an <see cref="IMachineRuntime"/> with optional video, audio, input, content, and
/// hardware-access capabilities. A thin adapter builds a <see cref="HumbleGamingBrickCore"/> and forwards the neutral
/// surface to the shared <see cref="QueuedMachineWorker"/> substrate: the machine is advanced by an exact integer tick
/// budget (converted to CPU T-cycles through a remainder-carrying accumulator, so it stays a pure function of the engine's
/// deterministic clock and its sampled input), and its unresampled 160x144 framebuffer is uploaded to a shader-readable
/// GPU image whose stable view handle a screen source samples directly. It carries the queued/backpressure behavior of the
/// substrate — a host that recognizes <see cref="IQueuedMachineRuntime"/> keeps commercial-ROM CPU work off its
/// simulation/render pump. Hardware observations and writes marshal to the same worker or coupled-link boundary.
/// The synchronous core remains available separately for embedding without a worker or GPU.
/// </summary>
public sealed partial class MachineHost : QueuedMachineHost, IMachineMemoryPeek, IReconfigurableMachine {
    /// <summary>The machine's native framebuffer width (160).</summary>
    public const int ScreenWidth = Framebuffer.ScreenWidth;
    /// <summary>The machine's native framebuffer height (144).</summary>
    public const int ScreenHeight = Framebuffer.ScreenHeight;
    /// <summary>The finite number of exact tick/input segments that may be accepted but incomplete.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    // The CURRENT revision — construction-fixed at boot, then live-mutable through TryReconfigure (the device swap).
    // The dmgSpeed fairness pin is construction-fixed (it sizes the deterministic tick->cycle budget).
    private ConsoleModel m_model;

    private readonly bool m_dmgSpeed;
    private readonly MachineBootOptions m_boot;
    private readonly byte[]? m_bootRom;

    /// <summary>Initializes a new machine host. When <paramref name="cartridgeRom"/> is non-null the machine assembles
    /// at once; a null ROM leaves the host UNASSIGNED (a dark framebuffer) until <see cref="QueuedMachineHost.LoadContent"/> runs.</summary>
    /// <param name="model">The hardware revision to emulate.</param>
    /// <param name="cartridgeRom">The cartridge ROM image, or <see langword="null"/> to start empty.</param>
    /// <param name="savePath">The cartridge's battery-save path (conventionally <c>&lt;romPath&gt;.sav</c>), or
    /// <see langword="null"/> for an in-memory-only save.</param>
    /// <param name="dmgSpeed">When <see langword="true"/>, the FAIRNESS pin: the tick-to-cycle budget stays at the DMG
    /// rate regardless of the KEY1 double-speed latch, so the budget is a function of configuration alone and every
    /// machine consumes identical cycle counts per engine tick.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio from this host —
    /// a silent host performs zero presentation-side audio synthesis.</param>
    /// <param name="bootMode">Cold startup executes Puck firmware; fast startup skips its presentation.</param>
    /// <param name="bootRomPath">An external boot image, or null for bundled Puck firmware for the current revision.</param>
    /// <param name="bootRomImage">An already prepared external boot image. Mutually exclusive with bootRomPath.</param>
    public MachineHost(ConsoleModel model, byte[]? cartridgeRom = null, string? savePath = null, bool dmgSpeed = false, int audioSampleRate = 0, MachineBootMode bootMode = MachineBootMode.Cold, string? bootRomPath = null, byte[]? bootRomImage = null)
        : base(
        width: ScreenWidth,
        height: ScreenHeight,
        maximumPendingSteps: DefaultMaximumPendingSteps,
        workerName: "Puck GamingBrick",
        audioSampleRate: audioSampleRate,
        savePath: savePath
    ) {
        m_model = model;
        m_dmgSpeed = dmgSpeed;
        m_boot = new(Mode: bootMode, ImagePath: bootRomPath);
        if (bootRomPath is not null && bootRomImage is not null) {
            throw new ArgumentException("Supply a prepared boot image or a boot image path, not both.", nameof(bootRomImage));
        }
        m_bootRom = bootRomImage?.ToArray() ?? (bootRomPath is null ? null : File.ReadAllBytes(path: bootRomPath));
        _ = HgbFirmware.CreateConfiguration(model: model, cartridgeRom: [], bootMode: bootMode, bootRom: m_bootRom);

        if (cartridgeRom is not null) {
            LoadContent(
                data: cartridgeRom,
                savePath: savePath
            );
        }
    }

    /// <inheritdoc/>
    public byte PeekByte(int address) =>
        Worker.PeekByte(address: address);
    /// <inheritdoc/>
    public void PeekBytes(int address, Span<byte> destination) =>
        Worker.PeekBytes(
        address: address,
        destination: destination
    );
    /// <inheritdoc/>
    public void PokeByte(int address, byte value) =>
        Worker.PokeByte(
        address: address,
        value: value
    );

    /// <inheritdoc/>
    public string Options =>
        GamingBrickEngine.FormatOptions(
        dmgSpeed: m_dmgSpeed,
        model: m_model,
        boot: m_boot
    );

    /// <inheritdoc/>
    public bool TryReconfigure(string? options, out string reason) {
        // Validate the options against the engine's ONE grammar before marshaling — a typo is rejected loudly here so the
        // worker never runs a half-parsed swap. The parse also yields the model we adopt on a successful retarget so the
        // Options readback (and world.save fold) reflect what the machine is now running.
        ConsoleModel model;

        try {
            var parsed = GamingBrickEngine.ParseOptions(options: options, bootDefaults: m_boot);
            if (parsed.Boot != m_boot) {
                reason = "Firmware and startup mode are construction-fixed; create a new machine to change them.";
                return false;
            }
            model = parsed.Model;
        } catch (ArgumentException exception) {
            reason = exception.Message;

            return false;
        }

        // Only hardware moves live. Image selection was checked above; the core never reloads a host path.
        var (ok, workerReason) = Worker.Reconfigure(options: GamingBrickEngine.FormatOptions(
            model: model, dmgSpeed: m_dmgSpeed, boot: new MachineBootOptions(Mode: m_boot.Mode, ImagePath: null)));

        if (ok) {
            m_model = model;
        }

        reason = workerReason;

        return ok;
    }

    /// <inheritdoc/>
    protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) =>
        new HumbleGamingBrickCore(
        cartridgeRom: data,
        bootMode: m_boot.Mode,
        bootRom: m_bootRom,
        dmgSpeed: m_dmgSpeed,
        model: m_model,
        savePath: savePath
    );
}

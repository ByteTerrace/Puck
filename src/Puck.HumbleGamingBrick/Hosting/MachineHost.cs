using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The SM83-family GamingBrick as an <see cref="IScreenMachine"/> — the first implementation of the neutral
/// screen-machine contract. A thin adapter that builds a <see cref="HumbleGamingBrickCore"/> and forwards the neutral
/// surface to the shared <see cref="QueuedMachineWorker"/> substrate: the machine is advanced by an exact integer tick
/// budget (converted to CPU T-cycles through a remainder-carrying accumulator, so it stays a pure function of the engine's
/// deterministic clock and its sampled input), and its unresampled 160x144 framebuffer is uploaded to a shader-readable
/// GPU image whose stable view handle a screen source samples directly. It carries the queued/backpressure behavior of the
/// substrate — a host that recognizes <see cref="IQueuedScreenMachine"/> keeps commercial-ROM CPU work off its
/// simulation/render pump — and answers a work-RAM peek.
/// <para>
/// This is the generic core, without the overworld's presentation costume, viewport resample, fleet-choir mirroring,
/// serial link, peripherals, or audio output — a machine that steps, shows a frame, and answers a work-RAM peek.
/// </para>
/// </summary>
public sealed class MachineHost : QueuedMachineHost, IMachineMemoryPeek, IReconfigurableMachine {
    /// <summary>The machine's native framebuffer width (160).</summary>
    public const int ScreenWidth = Framebuffer.ScreenWidth;
    /// <summary>The machine's native framebuffer height (144).</summary>
    public const int ScreenHeight = Framebuffer.ScreenHeight;
    /// <summary>The finite number of exact tick/input segments that may be accepted but incomplete.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    // The CURRENT model — construction-fixed at boot, then live-mutable through TryReconfigure (the dmg<->cgb<->agb
    // device swap). The dmgSpeed fairness pin is construction-fixed (it sizes the deterministic tick->cycle budget).
    private ConsoleModel m_model;

    private readonly bool m_dmgSpeed;

    /// <summary>Initializes a new machine host. When <paramref name="cartridgeRom"/> is non-null the machine assembles
    /// at once; a null ROM leaves the host UNASSIGNED (a dark framebuffer) until <see cref="QueuedMachineHost.LoadContent"/> runs.</summary>
    /// <param name="model">The hardware model to emulate (<see cref="ConsoleModel.Dmg"/>/<see cref="ConsoleModel.Cgb"/>/
    /// <see cref="ConsoleModel.Agb"/>).</param>
    /// <param name="cartridgeRom">The cartridge ROM image, or <see langword="null"/> to start empty.</param>
    /// <param name="savePath">The cartridge's battery-save path (conventionally <c>&lt;romPath&gt;.sav</c>), or
    /// <see langword="null"/> for an in-memory-only save.</param>
    /// <param name="dmgSpeed">When <see langword="true"/>, the FAIRNESS pin: the tick-to-cycle budget stays at the DMG
    /// rate regardless of the KEY1 double-speed latch, so the budget is a function of configuration alone and every
    /// machine consumes identical cycle counts per engine tick.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio from this host —
    /// a silent host performs zero presentation-side audio synthesis.</param>
    public MachineHost(ConsoleModel model, byte[]? cartridgeRom = null, string? savePath = null, bool dmgSpeed = false, int audioSampleRate = 0)
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
    public void PokeByte(int address, byte value) =>
        Worker.PokeByte(
        address: address,
        value: value
    );

    /// <inheritdoc/>
    public string Options =>
        GamingBrickEngine.FormatOptions(
        dmgSpeed: m_dmgSpeed,
        model: m_model
    );

    /// <inheritdoc/>
    public bool TryReconfigure(string? options, out string reason) {
        // Validate the options against the engine's ONE grammar before marshaling — a typo is rejected loudly here so the
        // worker never runs a half-parsed swap. The parse also yields the model we adopt on a successful retarget so the
        // Options readback (and world.save fold) reflect what the machine is now running.
        ConsoleModel model;

        try {
            (model, _) = GamingBrickEngine.ParseOptions(options: options);
        } catch (ArgumentException exception) {
            reason = exception.Message;

            return false;
        }

        var (ok, workerReason) = Worker.Reconfigure(options: options);

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
        dmgSpeed: m_dmgSpeed,
        model: m_model,
        savePath: savePath
    );
}

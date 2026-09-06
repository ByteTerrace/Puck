using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// Hosts one native ARM7TDMI AdvancedGamingBrick behind <see cref="IScreenMachine"/> and
/// <see cref="IQueuedScreenMachine"/> — a thin adapter that builds an <see cref="AdvancedGamingBrickCore"/> and forwards
/// the neutral surface to the shared <see cref="QueuedMachineWorker"/> substrate. The substrate owns the machine-owning
/// worker thread, the bounded eight-segment FIFO with producer backpressure, the triple-buffer publication rotation, and
/// the native-frame-keyed save-flush debounce; this class only wires the core's BIOS and cartridge.
/// </summary>
public sealed class AdvancedMachineHost : QueuedMachineHost {
    /// <summary>The native framebuffer width.</summary>
    public const int ScreenWidth = 240;
    /// <summary>The native framebuffer height.</summary>
    public const int ScreenHeight = 160;
    /// <summary>The finite number of exact tick/input segments that may be accepted but incomplete. This is a segment
    /// bound, not a wall-clock duration.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    private readonly byte[] m_bios;

    /// <summary>Creates an empty host or direct-boots <paramref name="cartridgeRom"/> when supplied.</summary>
    /// <param name="cartridgeRom">The native AGB cartridge image, or <see langword="null"/> for an empty host.</param>
    /// <param name="savePath">The optional battery-save path.</param>
    /// <param name="biosImage">An explicitly supplied 16 KiB BIOS image. A zeroed image is suitable only for
    /// BIOS-independent diagnostics; it implements neither software interrupts nor IRQ dispatch.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second the neutral
    /// <see cref="IAudioMachine"/> surface reports, or 0 (the default) when no consumer wants audio from this host —
    /// a silent host performs zero presentation-side audio synthesis.</param>
    /// <exception cref="ArgumentNullException"><paramref name="biosImage"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="biosImage"/> is not 16 KiB.</exception>
    public AdvancedMachineHost(byte[] biosImage, byte[]? cartridgeRom = null, string? savePath = null, int audioSampleRate = 0)
        : base(
        width: ScreenWidth,
        height: ScreenHeight,
        maximumPendingSteps: DefaultMaximumPendingSteps,
        workerName: "Puck AdvancedGamingBrick",
        audioSampleRate: audioSampleRate,
        savePath: savePath
    ) {
        ArgumentNullException.ThrowIfNull(argument: biosImage);
        if (biosImage.Length != ReplacementBios.ImageSize) {
            throw new ArgumentException(
                message: $"The BIOS image must be {ReplacementBios.ImageSize} bytes; got {biosImage.Length}.",
                paramName: nameof(biosImage)
            );
        }

        m_bios = biosImage.ToArray();

        if (cartridgeRom is not null) {
            LoadContent(
                data: cartridgeRom,
                savePath: savePath
            );
        }
    }

    /// <inheritdoc/>
    protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) =>
        new AdvancedGamingBrickCore(
        bios: m_bios,
        cartridgeRom: data,
        savePath: savePath
    );
}

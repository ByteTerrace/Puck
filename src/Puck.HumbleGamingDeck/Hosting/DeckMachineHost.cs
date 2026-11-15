namespace Puck.HumbleGamingDeck;

/// <summary>
/// The Humble Gaming Deck as a queued machine runtime: a <see cref="HumbleGamingDeckCore"/> behind the shared
/// <see cref="QueuedMachineWorker"/>, advanced by exact engine-tick budgets converted to master ticks, presenting its
/// 256 by 240 picture and stereo audio, with two controller ports: <c>controls</c> for $4016 and <c>controls-2</c> for
/// $4017. Its debug memory window reads and writes the CPU address map on the worker thread, between steps.
/// </summary>
public sealed class DeckMachineHost : QueuedMachineHost {
    /// <summary>The picture's width in pixels.</summary>
    public const int ScreenWidth = HgdPpu.Width;
    /// <summary>The picture's height in lines.</summary>
    public const int ScreenHeight = HgdPpu.Height;

    private static readonly string[] InputPortNames = ["controls", "controls-2"];

    private readonly HgdPowerOnProfile m_powerOn;

    /// <summary>Initializes a new instance of the <see cref="DeckMachineHost"/> class. With no cartridge the host starts
    /// empty until <see cref="QueuedMachineHost.LoadContent"/> runs.</summary>
    /// <param name="cartridgeImage">The iNES or NES 2.0 image, or <see langword="null"/> to start empty.</param>
    /// <param name="savePath">The battery-save path, or <see langword="null"/> to keep battery RAM in memory only.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second, or 0 for no audio.</param>
    /// <param name="powerOn">The power-on profile; <see langword="null"/> selects the default.</param>
    /// <exception cref="InvalidDataException"><paramref name="cartridgeImage"/>'s header or payload is damaged or ambiguous.</exception>
    /// <exception cref="NotSupportedException">The image names an unimplemented mapper, submapper, console, timing
    /// family, or memory layout.</exception>
    public DeckMachineHost(byte[]? cartridgeImage = null, string? savePath = null, int audioSampleRate = 0, HgdPowerOnProfile? powerOn = null)
        : base(
        audioSampleRate: audioSampleRate,
        height: ScreenHeight,
        inputPorts: InputPortNames,
        savePath: savePath,
        width: ScreenWidth,
        workerName: "Puck HumbleGamingDeck"
    ) {
        m_powerOn = (powerOn ?? new HgdPowerOnProfile());
        if (cartridgeImage is not null) {
            LoadContent(
                data: cartridgeImage,
                savePath: savePath
            );
        }
    }

    /// <inheritdoc/>
    protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) =>
        new HumbleGamingDeckCore(
            configuration: new HgdMachineConfiguration(
                cartridge: HgdCartridge.Load(image: data),
                powerOn: m_powerOn
            ),
            savePath: savePath
        );
}

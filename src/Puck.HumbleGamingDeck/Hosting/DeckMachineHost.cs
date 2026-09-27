using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingDeck;

/// <summary>
/// The Humble Gaming Deck as a queued machine runtime: a <see cref="HumbleGamingDeckCore"/> behind the shared
/// <see cref="QueuedMachineWorker"/>, advanced by exact engine-tick budgets converted to master ticks, presenting its
/// 256 by 240 picture and stereo audio, with two controller ports: <c>controls</c> for $4016 and <c>controls-2</c> for
/// $4017. Its debug memory window reads and writes the CPU address map on the worker thread, between steps.
/// </summary>
public sealed class DeckMachineHost : QueuedMachineHost, IMachineMemoryPeek {
    /// <summary>The picture's width in pixels.</summary>
    public const int ScreenWidth = HgdPpu.Width;
    /// <summary>The picture's height in lines.</summary>
    public const int ScreenHeight = HgdPpu.Height;
    /// <summary>The finite number of exact tick and input segments that may be accepted but incomplete.</summary>
    public const int DefaultMaximumPendingSteps = 8;

    private static readonly string[] InputPortNames = ["controls", "controls-2"];

    private readonly HgdPowerOnProfile m_powerOn;

    /// <summary>Initializes a new instance of the <see cref="DeckMachineHost"/> class. With no cartridge the host starts
    /// empty until <see cref="QueuedMachineHost.LoadContent"/> runs.</summary>
    /// <param name="cartridgeImage">The iNES or NES 2.0 image, or <see langword="null"/> to start empty.</param>
    /// <param name="savePath">The battery-save path, or <see langword="null"/> to keep battery RAM in memory only.</param>
    /// <param name="audioSampleRate">The audio output rate in frames per emulated second, or 0 for no audio.</param>
    /// <param name="powerOn">The power-on profile; <see langword="null"/> selects the default.</param>
    /// <exception cref="InvalidDataException"><paramref name="cartridgeImage"/>'s header is damaged, ambiguous, or names a
    /// board the Deck does not implement.</exception>
    public DeckMachineHost(byte[]? cartridgeImage = null, string? savePath = null, int audioSampleRate = 0, HgdPowerOnProfile? powerOn = null)
        : base(
        audioSampleRate: audioSampleRate,
        height: ScreenHeight,
        inputPorts: InputPortNames,
        maximumPendingSteps: DefaultMaximumPendingSteps,
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
    protected override IQueuedMachineCore CreateCore(byte[] data, string? savePath) =>
        new HumbleGamingDeckCore(
            configuration: new HgdMachineConfiguration(
                cartridge: HgdCartridge.Load(image: data),
                powerOn: m_powerOn
            ),
            savePath: savePath
        );
}

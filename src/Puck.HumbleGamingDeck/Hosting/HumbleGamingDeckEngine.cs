using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingDeck;

/// <summary>
/// The Humble Gaming Deck's <see cref="IMachineEngine"/>: engine id <c>humble-gaming-deck</c>, which builds a
/// <see cref="DeckMachineHost"/> from a cartridge. The NTSC model and the default power-on profile are the only
/// configuration today, so the option string must be empty.
/// </summary>
public sealed class HumbleGamingDeckEngine : IMachineEngine {
    /// <summary>The engine's identifier.</summary>
    public const string EngineId = "humble-gaming-deck";

    /// <inheritdoc/>
    public string Id => EngineId;
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        AudioOutputs: [
            new(
                Contract: "puck.machine.audio.v1",
                Description: "The machine's stereo audio stream.",
                Name: "audio"
            ),
        ],
        Configuration: new(
            Fields: [
                new(
                    "content",
                    MachineFieldKind.Object,
                    "The mounted cartridge image.",
                    Fields: [
                        new(
                            "path",
                            MachineFieldKind.String,
                            "Cartridge path relative to the declaring document.",
                            Required: true,
                            Role: MachineFieldRole.ContentPath
                        ),
                    ]
                ),
            ],
            Id: "puck.humble-gaming-deck.config.v1"
        ),
        Description: "Deterministic NES/Famicom machine on an integer master clock.",
        Id: EngineId,
        InputPorts: [
            new(
                Contract: "puck.machine.pad.v1",
                Description: "The standard controller in the $4016 port.",
                Name: "controls"
            ),
            new(
                Contract: "puck.machine.pad.v1",
                Description: "The standard controller in the $4017 port.",
                Name: "controls-2"
            ),
        ],
        MemorySpaces: [],
        Operations: [],
        VideoOutputs: [
            new(
                Contract: "puck.machine.video.v1",
                Description: "The native 256 by 240 picture.",
                Name: "video"
            ),
        ]
    );

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="options"/> is not empty.</exception>
    /// <exception cref="InvalidDataException"><paramref name="contentBytes"/>'s header or payload is damaged or ambiguous.</exception>
    /// <exception cref="NotSupportedException">The image names an unimplemented mapper, submapper, console, timing
    /// family, or memory layout.</exception>
    /// <exception cref="IOException">An existing battery save cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to an existing battery save is denied.</exception>
    public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        if (!string.IsNullOrWhiteSpace(value: options)) {
            throw new ArgumentException(
                message: $"Unknown {EngineId} option '{options.Trim()}'; the engine takes no options.",
                paramName: nameof(options)
            );
        }

        return new DeckMachineHost(
            audioSampleRate: audioSampleRate,
            cartridgeImage: contentBytes,
            savePath: savePath
        );
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The configuration does not satisfy the descriptor, or a declared asset was not
    /// prepared.</exception>
    /// <exception cref="InvalidDataException">The cartridge's header or payload is damaged or ambiguous.</exception>
    /// <exception cref="NotSupportedException">The image names an unimplemented mapper, submapper, console, timing
    /// family, or memory layout.</exception>
    /// <exception cref="IOException">An existing battery save cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to an existing battery save is denied.</exception>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        MachineConfigurationValidation.Validate(
            descriptor: Descriptor.Configuration,
            value: request.Configuration
        );

        return new DeckMachineHost(
            audioSampleRate: request.AudioSampleRate,
            cartridgeImage: (request.Configuration.TryGetProperty(
                propertyName: "content",
                value: out _
            )
                ? request.RequireAsset(fieldPath: "content.path").Image.ToArray()
                : null),
            savePath: request.SavePath
        );
    }
}

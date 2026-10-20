using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingDeck;

/// <summary>
/// The NTSC NES/Famicom screen-machine engine. Its stable id is <c>humble-gaming-deck</c>; its content is an iNES or
/// NES 2.0 image mounted as raw bytes, and every machine it builds is a <see cref="DeckMachineHost"/> with two
/// controller ports, <c>controls</c> for $4016 and <c>controls-2</c> for $4017. The cartridge starts through its reset
/// vector, so the engine takes no firmware and no options: power-on state is the default
/// <see cref="HgdPowerOnProfile"/>.
/// </summary>
public sealed class HumbleGamingDeckEngine : IMachineEngine, IMachineOperationProvider {
    /// <summary>The engine identifier a world machine row names.</summary>
    public const string EngineId = "humble-gaming-deck";

    /// <inheritdoc/>
    public string Id => EngineId;
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        EngineId,
        "Deterministic NTSC NES/Famicom machine on an integer master clock.",
        new(
            "puck.humble-gaming-deck.configuration.v1",
            [
                new(
                    "content",
                    MachineFieldKind.Object,
                    "The mounted iNES or NES 2.0 cartridge image.",
                    Fields: [
                        new(
                            "path",
                            MachineFieldKind.String,
                            "Cartridge path relative to the declaring document.",
                            Required: true,
                            Role: MachineFieldRole.ContentPath
                        )
                    ]
                )
            ]
        ),
        [new(
            Contract: "puck.machine.video.v1",
            Description: "The native 256 by 240 picture.",
            Name: "video"
        )],
        [new(
            Contract: "puck.machine.audio.v1",
            Description: "The machine's stereo audio stream.",
            Name: "audio"
        )],
        [
            new(
                Contract: "puck.machine.pad.v1",
                Description: "The standard controller in port one ($4016).",
                Name: "controls"
            ),
            new(
                Contract: "puck.machine.pad.v1",
                Description: "The standard controller in port two ($4017).",
                Name: "controls-2"
            ),
        ],
        [
            new(
                "content.insert",
                "Mounts a cartridge image through the host's replacement transaction.",
                new(
                    "puck.humble-gaming-deck.content-insert.v1",
                    [
                        new(
                            "content",
                            MachineFieldKind.Object,
                            "The cartridge image.",
                            Required: true,
                            Fields: [
                                new(
                                    "path",
                                    MachineFieldKind.String,
                                    "Content path prepared by the host.",
                                    Required: true,
                                    Role: MachineFieldRole.ContentPath
                                )
                            ]
                        )
                    ]
                )
            ),
            new(
                "content.eject",
                "Removes the cartridge through the host's replacement transaction.",
                new(
                    Fields: [],
                    Id: "puck.humble-gaming-deck.content-eject.v1"
                )
            ),
            new(
                "machine.reset",
                "Power-cycles the current machine configuration through the host's replacement transaction.",
                new(
                    Fields: [],
                    Id: "puck.humble-gaming-deck.machine-reset.v1"
                )
            ),
        ],
        []
    );

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="options"/> holds a token (the engine has none), or
    /// <paramref name="contentBytes"/> is not an image the Deck can run.</exception>
    public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        if (!string.IsNullOrWhiteSpace(value: options)) {
            throw new ArgumentException(
                message: $"Unknown {EngineId} option '{options.Trim()}'; the engine takes no options.",
                paramName: nameof(options)
            );
        }

        return CreateHost(
            audioSampleRate: audioSampleRate,
            image: contentBytes,
            savePath: savePath
        );
    }
    /// <inheritdoc/>
    /// <exception cref="ArgumentException">The configuration is invalid, its content asset was not prepared, or the
    /// image is not one the Deck can run.</exception>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        MachineConfigurationValidation.Validate(
            descriptor: Descriptor.Configuration,
            value: request.Configuration
        );

        return CreateHost(
            audioSampleRate: request.AudioSampleRate,
            image: (request.Configuration.TryGetProperty(
                propertyName: "content",
                value: out _
            )
                ? request.RequireAsset(fieldPath: "content.path").Image.ToArray()
                : null
            ),
            savePath: request.SavePath
        );
    }
    /// <inheritdoc/>
    public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) =>
        MachineOperationValidation.PrepareStandardOperation(
            current: current,
            descriptor: Descriptor,
            providerName: EngineId,
            request: request
        );

    // The image is parsed before the host exists, so a damaged or unsupported cartridge is refused by name without
    // starting a worker; the loader's own diagnostics become the refusal.
    private static DeckMachineHost CreateHost(byte[]? image, string? savePath, int audioSampleRate) {
        if (image is not null) {
            try {
                _ = HgdCartridge.Load(image: image);
            } catch (Exception exception) when ((exception is InvalidDataException or NotSupportedException)) {
                throw new ArgumentException(
                    innerException: exception,
                    message: $"{EngineId} cannot run the cartridge: {exception.Message}"
                );
            }
        }

        return new DeckMachineHost(
            audioSampleRate: audioSampleRate,
            cartridgeImage: image,
            savePath: savePath
        );
    }
}

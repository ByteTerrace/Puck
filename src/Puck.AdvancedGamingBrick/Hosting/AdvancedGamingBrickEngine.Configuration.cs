using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickEngine {
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        "advanced-gaming-brick",
        "Deterministic native ARM7TDMI machine.",
        new(
            "puck.advanced-gaming-brick.config.v1",
            [
            new(
                    "boot",
                    MachineFieldKind.String,
                    "Cold runs firmware; fast starts at cartridge handoff.",
                    Choices: ["cold", "fast"]
                ),
            new(
                    "content",
                    MachineFieldKind.Object,
                    "The mounted cartridge or cartridge source.",
                    Fields: [
                new(
                            "path",
                            MachineFieldKind.String,
                            "Cartridge path relative to the declaring document.",
                            Required: true,
                            Role: MachineFieldRole.ContentPath
                        )
            ]
                ),
            new(
                    "firmware",
                    MachineFieldKind.Object,
                    "Optional external firmware; absent uses bundled Puck firmware.",
                    Fields: [
                new(
                            "path",
                            MachineFieldKind.String,
                            "External BIOS path relative to the declaring document.",
                            Required: true,
                            Role: MachineFieldRole.AssetPath
                        )
            ]
                )
        ]
        ),
        [new(
                Contract: "puck.machine.video.v1",
                Description: "The native 240 by 160 display.",
                Name: "video"
            )],
        [new(
                Contract: "puck.machine.audio.v1",
                Description: "The machine's stereo audio stream.",
                Name: "audio"
            )],
        [new(
                Contract: "puck.machine.pad.v1",
                Description: "Buttons and supported cartridge sensors.",
                Name: "controls"
            )],
        [
            new(
                "content.insert",
                "Mounts prepared content through the host's replacement transaction.",
                new(
                    "puck.advanced-gaming-brick.content-insert.v1",
                    [
                new(
                            "content",
                            MachineFieldKind.Object,
                            "The prepared cartridge source.",
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
                "Removes mounted content through the host's replacement transaction.",
                new(
                    Fields: [],
                    Id: "puck.advanced-gaming-brick.content-eject.v1"
                )
            ),
            new(
                "machine.reset",
                "Recreates the current machine configuration through the host's replacement transaction.",
                new(
                    Fields: [],
                    Id: "puck.advanced-gaming-brick.machine-reset.v1"
                )
            )
        ],
        AdvancedGamingBrickCore.HardwareSpaces
    );

    /// <inheritdoc/>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        MachineConfigurationValidation.Validate(
            descriptor: Descriptor.Configuration,
            value: request.Configuration
        );
        var configuration = request.Configuration;
        var bios = (configuration.TryGetProperty(
            propertyName: "firmware",
            value: out _
        )
            ? request.RequireAsset(fieldPath: "firmware.path").Image.ToArray()
            : AgbFirmware.GetImage()
        );

        if (AgbBiosProfile.Identify(image: bios).Kind == AgbBiosKind.ReplacementStub) {
            throw new ArgumentException(
                message: "The supplied BIOS is zero-filled; configured machines require firmware that implements BIOS services.",
                paramName: nameof(request)
            );
        }
        var boot = ((configuration.TryGetProperty(
            propertyName: "boot",
            value: out var bootValue
        ) && (bootValue.GetString() == "fast"))
            ? MachineBootMode.Fast
            : MachineBootMode.Cold
        );

        return new AdvancedMachineHost(
            biosImage: bios,
            cartridgeRom: (configuration.TryGetProperty(
                propertyName: "content",
                value: out _
            )
            ? request.RequireAsset(fieldPath: "content.path").Image.ToArray()
            : null),
            savePath: request.SavePath,
            audioSampleRate: request.AudioSampleRate,
            bootMode: boot
        );
    }
}

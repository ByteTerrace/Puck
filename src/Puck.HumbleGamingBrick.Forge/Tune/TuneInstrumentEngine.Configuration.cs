using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Forge.Tune;

public sealed partial class TuneInstrumentEngine {
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        "tune-instrument",
        "A player-operated instrument compiled to a native SM83 jukebox.",
        new(
            "puck.tune-instrument.configuration.v1",
            [
            new(
                    "content",
                    MachineFieldKind.Object,
                    "The instrument's audio document.",
                    Fields: [
                new(
                            "path",
                            MachineFieldKind.String,
                            "Audio document path relative to the declaring document.",
                            Required: true,
                            Role: MachineFieldRole.ContentPath
                        )
            ]
                )
        ]
        ),
        [new(
                Contract: "puck.machine.video.v1",
                Description: "The instrument's transport display.",
                Name: "video"
            )],
        [new(
                Contract: "puck.machine.audio.v1",
                Description: "The instrument's stereo audio stream.",
                Name: "audio"
            )],
        [new(
                Contract: "puck.machine.pad.v1",
                Description: "The instrument's transport controls.",
                Name: "controls"
            )],
        [
            new(
                "content.insert",
                "Mounts prepared content through the host's replacement transaction.",
                new(
                    "puck.tune-instrument.content-insert.v1",
                    [
                new(
                            "content",
                            MachineFieldKind.Object,
                            "The prepared instrument source.",
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
                    Id: "puck.tune-instrument.content-eject.v1"
                )
            ),
            new(
                "machine.reset",
                "Recreates the current machine configuration through the host's replacement transaction.",
                new(
                    Fields: [],
                    Id: "puck.tune-instrument.machine-reset.v1"
                )
            )
        ],
        []
    );

    /// <inheritdoc/>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        MachineConfigurationValidation.Validate(
            descriptor: Descriptor.Configuration,
            value: request.Configuration
        );
        return new TuneInstrumentMachine(
            audioSampleRate: request.AudioSampleRate,
            content: (request.Configuration.TryGetProperty(
                propertyName: "content",
                value: out _
            )
            ? request.RequireAsset(fieldPath: "content.path").Image.ToArray()
            : null),
            savePath: request.SavePath
        );
    }
}

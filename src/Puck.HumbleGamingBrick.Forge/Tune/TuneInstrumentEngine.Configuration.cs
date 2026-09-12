using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick.Forge.Tune;

public sealed partial class TuneInstrumentEngine {
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        "tune-instrument", "A player-operated instrument compiled to a native SM83 jukebox.",
        new("puck.tune-instrument.config.v1", [
            new("content", MachineFieldKind.Object, "The instrument's audio document.", Fields: [
                new("path", MachineFieldKind.String, "Audio document path relative to the declaring document.", Required: true, Role: MachineFieldRole.ContentPath)
            ])
        ]),
        [new("video", "puck.machine.video.v1", "The instrument's transport display.")],
        [new("audio", "puck.machine.audio.v1", "The instrument's stereo audio stream.")],
        [new("controls", "puck.machine.pad.v1", "The instrument's transport controls.")],
        [], []
    );

    /// <inheritdoc/>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        MachineConfigurationValidation.Validate(Descriptor.Configuration, request.Configuration);
        return new TuneInstrumentMachine(
            audioSampleRate: request.AudioSampleRate,
            content: request.Configuration.TryGetProperty("content", out _) ? request.RequireAsset("content.path").Image.ToArray() : null,
            savePath: request.SavePath
        );
    }
}

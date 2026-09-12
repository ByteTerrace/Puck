using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

public sealed partial class AdvancedGamingBrickEngine {
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        "advanced-gaming-brick", "Deterministic native ARM7TDMI machine.",
        new("puck.advanced-gaming-brick.config.v1", [
            new("boot", MachineFieldKind.String, "Cold runs firmware; fast starts at cartridge handoff.", Choices: ["cold", "fast"]),
            new("content", MachineFieldKind.Object, "The mounted cartridge or cartridge source.", Fields: [
                new("path", MachineFieldKind.String, "Cartridge path relative to the declaring document.", Required: true, Role: MachineFieldRole.ContentPath)
            ]),
            new("firmware", MachineFieldKind.Object, "Optional external firmware; absent uses bundled Puck firmware.", Fields: [
                new("path", MachineFieldKind.String, "External BIOS path relative to the declaring document.", Required: true, Role: MachineFieldRole.AssetPath)
            ])
        ]),
        [new("video", "puck.machine.video.v1", "The native 240 by 160 display.")],
        [new("audio", "puck.machine.audio.v1", "The machine's stereo audio stream.")],
        [new("controls", "puck.machine.pad.v1", "Buttons and supported cartridge sensors.")],
        [], AdvancedGamingBrickCore.HardwareSpaces
    );

    /// <inheritdoc/>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        MachineConfigurationValidation.Validate(Descriptor.Configuration, request.Configuration);
        var configuration = request.Configuration;
        var bios = configuration.TryGetProperty("firmware", out _)
            ? request.RequireAsset("firmware.path").Image.ToArray() : AgbFirmware.GetImage();
        if (AgbBiosProfile.Identify(bios).Kind == AgbBiosKind.ReplacementStub) {
            throw new ArgumentException("The supplied BIOS is zero-filled; configured machines require firmware that implements BIOS services.", nameof(request));
        }
        var boot = configuration.TryGetProperty("boot", out var bootValue) && bootValue.GetString() == "fast"
            ? MachineBootMode.Fast : MachineBootMode.Cold;
        return new AdvancedMachineHost(
            biosImage: bios,
            cartridgeRom: configuration.TryGetProperty("content", out _) ? request.RequireAsset("content.path").Image.ToArray() : null,
            savePath: request.SavePath, audioSampleRate: request.AudioSampleRate, bootMode: boot
        );
    }
}

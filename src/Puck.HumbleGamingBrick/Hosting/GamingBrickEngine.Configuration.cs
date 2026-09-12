using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

public sealed partial class GamingBrickEngine {
    /// <inheritdoc/>
    public MachineEngineDescriptor Descriptor { get; } = new(
        "gaming-brick", "Deterministic SM83 machine with selectable hardware revision.",
        new("puck.gaming-brick.config.v1", [
            new("model", MachineFieldKind.String, "Hardware family or revision; defaults to dmg.", Choices: [.. ModelTokens.Keys]),
            new("dmgSpeed", MachineFieldKind.Boolean, "Keep a DMG-rate tick budget across CPU speed changes."),
            new("boot", MachineFieldKind.String, "Cold runs firmware; fast starts at cartridge handoff.", Choices: ["cold", "fast"]),
            new("content", MachineFieldKind.Object, "The mounted cartridge or cartridge source.", Fields: [
                new("path", MachineFieldKind.String, "Cartridge path relative to the declaring document.", Required: true, Role: MachineFieldRole.ContentPath)
            ]),
            new("firmware", MachineFieldKind.Object, "Optional external firmware; absent uses bundled Puck firmware.", Fields: [
                new("path", MachineFieldKind.String, "External firmware path relative to the declaring document.", Required: true, Role: MachineFieldRole.AssetPath)
            ])
        ]),
        [new("video", "puck.machine.video.v1", "The native 160 by 144 display.")],
        [new("audio", "puck.machine.audio.v1", "The machine's stereo audio stream.")],
        [new("controls", "puck.machine.pad.v1", "Buttons and supported cartridge sensors.")],
        [], HumbleGamingBrickCore.HardwareSpaces
    );

    /// <inheritdoc/>
    public IMachineRuntime CreateMachine(MachineCreationRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        MachineConfigurationValidation.Validate(Descriptor.Configuration, request.Configuration);
        var configuration = request.Configuration;
        var model = configuration.TryGetProperty("model", out var modelValue) ? ModelTokens[modelValue.GetString()!] : DefaultModel;
        var boot = configuration.TryGetProperty("boot", out var bootValue) && bootValue.GetString() == "fast"
            ? MachineBootMode.Fast : MachineBootMode.Cold;
        return new MachineHost(
            model: model,
            cartridgeRom: configuration.TryGetProperty("content", out _) ? request.RequireAsset("content.path").Image.ToArray() : null,
            savePath: request.SavePath,
            dmgSpeed: configuration.TryGetProperty("dmgSpeed", out var speed) && speed.GetBoolean(),
            audioSampleRate: request.AudioSampleRate,
            bootMode: boot,
            bootRomImage: configuration.TryGetProperty("firmware", out _) ? request.RequireAsset("firmware.path").Image.ToArray() : null
        );
    }
}

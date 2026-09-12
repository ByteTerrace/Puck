using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The native ARM7TDMI AdvancedGamingBrick screen-machine engine. Its stable id is
/// <c>advanced-gaming-brick</c>; cartridges cold-boot with bundled Puck firmware by default.
/// <c>fast</c> skips startup without discarding BIOS services; a final <c>bios=&lt;path&gt;</c> selects an external image.
/// <c>stub</c> explicitly selects a zeroed image for diagnostics that never call BIOS services or dispatch IRQs.
/// </summary>
public sealed partial class AdvancedGamingBrickEngine : IMachineEngine {
    /// <inheritdoc/>
    public string Id => "advanced-gaming-brick";

    /// <inheritdoc/>
    public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var (bios, mode) = ResolveStartup(options: options);

        return new AdvancedMachineHost(
            audioSampleRate: audioSampleRate,
            biosImage: bios,
            bootMode: mode,
            cartridgeRom: contentBytes,
            savePath: savePath
        );
    }

    private static (byte[] Bios, MachineBootMode Mode) ResolveStartup(string? options) {
        if (string.Equals(a: options?.Trim(), b: "stub", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return (Bios: new byte[ReplacementBios.ImageSize], Mode: MachineBootMode.Fast);
        }

        var boot = MachineBootOptions.Parse(options: options, machineTokens: out var tokens);
        if (tokens.Length != 0) {
            throw new ArgumentException(message: $"Unknown advanced-gaming-brick option '{tokens[0]}'; expected cold, fast, a final bios=<path>, or the standalone diagnostic stub option.", paramName: nameof(options));
        }

        return (Bios: ResolveBios(path: boot.ImagePath), Mode: boot.Mode);
    }

    private static byte[] ResolveBios(string? path) {
        if (path is null) {
            return AgbFirmware.GetImage();
        }
        if (!File.Exists(path: path)) {
            throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' not found");
        }

        try {
            var bios = File.ReadAllBytes(path: path);

            if (bios.Length != ReplacementBios.ImageSize) {
                throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' must be {ReplacementBios.ImageSize} bytes; got {bios.Length}");
            }

            if (AgbBiosProfile.Identify(image: bios).Kind == AgbBiosKind.ReplacementStub) {
                throw new ArgumentException(message: "The BIOS image is zero-filled and cannot execute BIOS services. Use a working BIOS image, or stub explicitly for BIOS-independent diagnostics.", paramName: nameof(path));
            }

            return bios;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            throw new ArgumentException(
                message: $"advanced-gaming-brick BIOS '{path}' unreadable ({exception.Message})",
                innerException: exception
            );
        }
    }
}

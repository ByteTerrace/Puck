using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The native ARM7TDMI AdvancedGamingBrick screen-machine engine. Its stable id is
/// <c>advanced-gaming-brick</c>; cartridges direct-boot against either the legal zeroed replacement BIOS or an explicit
/// <c>bios=&lt;path&gt;</c> image supplied by the host.
/// </summary>
public sealed class AdvancedGamingBrickEngine : IScreenMachineEngine {
    /// <inheritdoc/>
    public string Id => "advanced-gaming-brick";

    /// <inheritdoc/>
    public IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var bios = ResolveBios(options: options);

        return new AdvancedMachineHost(cartridgeRom: contentBytes, savePath: savePath, biosImage: bios, audioSampleRate: audioSampleRate);
    }

    private static byte[] ResolveBios(string? options) {
        if (string.IsNullOrWhiteSpace(value: options) ||
            options.Equals(value: "direct", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return new byte[ReplacementBios.ImageSize];
        }

        const string biosPrefix = "bios=";

        if (!options.StartsWith(value: biosPrefix, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException(message: $"unknown advanced-gaming-brick option '{options}' — expected direct, bios=<path>, or no option");
        }

        var path = options[biosPrefix.Length..].Trim();

        if (!File.Exists(path: path)) {
            throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' not found");
        }

        try {
            var bios = File.ReadAllBytes(path: path);

            if (bios.Length != ReplacementBios.ImageSize) {
                throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' must be {ReplacementBios.ImageSize} bytes; got {bios.Length}");
            }

            return bios;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' unreadable ({exception.Message})", innerException: exception);
        }
    }
}

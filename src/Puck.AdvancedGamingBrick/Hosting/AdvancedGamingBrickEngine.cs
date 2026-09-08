using Puck.Abstractions.Machines;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The native ARM7TDMI AdvancedGamingBrick screen-machine engine. Its stable id is
/// <c>advanced-gaming-brick</c>; cartridges direct-boot against an explicit <c>bios=&lt;path&gt;</c> image.
/// <c>stub</c> explicitly selects a zeroed image for diagnostics that never call BIOS services or dispatch IRQs.
/// </summary>
public sealed class AdvancedGamingBrickEngine : IScreenMachineEngine {
    /// <inheritdoc/>
    public string Id => "advanced-gaming-brick";

    /// <inheritdoc/>
    public IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var bios = ResolveBios(options: options);

        return new AdvancedMachineHost(
            audioSampleRate: audioSampleRate,
            biosImage: bios,
            cartridgeRom: contentBytes,
            savePath: savePath
        );
    }

    private static byte[] ResolveBios(string? options) {
        if (
            options is not null && options.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "stub"
        )
        ) {
            return new byte[ReplacementBios.ImageSize];
        }

        const string BiosPrefix = "bios=";

        if (string.IsNullOrWhiteSpace(value: options) || !options.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: BiosPrefix
        )) {
            throw new ArgumentException(message: "Advanced GamingBrick requires bios=<path>. Use stub only for BIOS-independent diagnostics; direct boot does not replace BIOS services.", paramName: nameof(options));
        }

        var path = options[BiosPrefix.Length..].Trim();

        if (!File.Exists(path: path)) {
            throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' not found");
        }

        try {
            var bios = File.ReadAllBytes(path: path);

            if (bios.Length != ReplacementBios.ImageSize) {
                throw new ArgumentException(message: $"advanced-gaming-brick BIOS '{path}' must be {ReplacementBios.ImageSize} bytes; got {bios.Length}");
            }

            if (AgbBiosProfile.Identify(image: bios).Kind == AgbBiosKind.ReplacementStub) {
                throw new ArgumentException(message: "The BIOS image is zero-filled and cannot execute BIOS services. Use a working BIOS image, or stub explicitly for BIOS-independent diagnostics.", paramName: nameof(options));
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

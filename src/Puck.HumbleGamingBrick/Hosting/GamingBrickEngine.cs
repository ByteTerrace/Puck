using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The <see cref="IScreenMachineEngine"/> for the SM83-family GamingBrick — the first implementation of the neutral
/// screen-machine contract. Its <see cref="Id"/> is <c>gaming-brick</c>, and its options vocabulary is a hardware model
/// (<c>dmg</c>/<c>cgb</c>/<c>agb</c>, default <c>dmg</c>) plus an optional <c>dmgspeed</c> fairness pin, in any order.
/// A host resolves this engine by id and hands it cartridge bytes; the machine it builds is a <see cref="MachineHost"/>.
/// </summary>
public sealed class GamingBrickEngine : IScreenMachineEngine {
    /// <summary>The <c>dmgspeed</c> option token — the fairness speed pin (a fixed per-tick cycle budget regardless of the
    /// KEY1 double-speed latch).</summary>
    private const string DmgSpeedToken = "dmgspeed";

    /// <inheritdoc/>
    public string Id => "gaming-brick";

    /// <inheritdoc/>
    public IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var (model, dmgSpeed) = ParseOptions(options: options);

        return new MachineHost(model: model, cartridgeRom: contentBytes, savePath: savePath, dmgSpeed: dmgSpeed, audioSampleRate: audioSampleRate);
    }

    // Parse the space-separated options string (order-independent): a model keyword sets the console costume; the
    // 'dmgspeed' token applies the fairness pin. An unknown token throws so a typo is loud, not silently defaulted.
    private static (ConsoleModel Model, bool DmgSpeed) ParseOptions(string? options) {
        var model = ConsoleModel.Dmg;
        var dmgSpeed = false;

        if (string.IsNullOrWhiteSpace(value: options)) {
            return (Model: model, DmgSpeed: dmgSpeed);
        }

        foreach (var token in options.Split(separator: (char[]?)null, options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (token.Equals(value: "dmg", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                model = ConsoleModel.Dmg;
            } else if (token.Equals(value: "cgb", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                model = ConsoleModel.Cgb;
            } else if (token.Equals(value: "agb", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                model = ConsoleModel.Agb;
            } else if (token.Equals(value: DmgSpeedToken, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                dmgSpeed = true;
            } else {
                throw new ArgumentException(message: $"unknown gaming-brick option '{token}' — expected dmg|cgb|agb or {DmgSpeedToken}");
            }
        }

        return (Model: model, DmgSpeed: dmgSpeed);
    }
}

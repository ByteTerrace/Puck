using Puck.Abstractions.Machines;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The <see cref="IScreenMachineEngine"/> for the SM83-family GamingBrick — the first implementation of the neutral
/// screen-machine contract. Its <see cref="Id"/> is <c>gaming-brick</c>, and its options vocabulary is a hardware model
/// (<c>dmg</c>/<c>cgb</c>/<c>agb</c>, default <c>dmg</c>) plus an optional <c>dmgspeed</c> fairness pin, in any order.
/// A host resolves this engine by id and hands it cartridge bytes; the machine it builds is a <see cref="MachineHost"/>.
/// It also carries <see cref="IMachineLinkingEngine"/>: two of its machines can be cable-linked over the deterministic
/// <see cref="SerialLinkSession"/> interleave.
/// </summary>
public sealed class GamingBrickEngine : IScreenMachineEngine, IMachineLinkingEngine {
    /// <summary>The <c>dmgspeed</c> option token — the fairness speed pin (a fixed per-tick cycle budget regardless of the
    /// KEY1 double-speed latch).</summary>
    internal const string DmgSpeedToken = "dmgspeed";

    /// <inheritdoc/>
    public string Id => "gaming-brick";

    /// <inheritdoc/>
    public IScreenMachine Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) {
        var (model, dmgSpeed) = ParseOptions(options: options);

        return new MachineHost(model: model, cartridgeRom: contentBytes, savePath: savePath, dmgSpeed: dmgSpeed, audioSampleRate: audioSampleRate);
    }

    /// <inheritdoc/>
    public bool TryLink(IReadOnlyList<IScreenMachine> machines, out IMachineLink? link, out string reason) {
        link = null;

        if (machines is null || (machines.Count < 2)) {
            reason = "a cable link needs two or more machines";

            return false;
        }

        for (var index = 0; (index < machines.Count); index++) {
            if (machines[index] is not MachineHost) {
                reason = $"member {index} is not a gaming-brick machine";

                return false;
            }
        }

        // The neutral queued host owns each machine's core on its OWN worker thread, and SerialLinkSession requires the
        // two MachineInstances driven from ONE thread through its instruction-atomic interleave. Wiring that safely means
        // quiescing both workers and lending their cores to the pair-stepper — a further Puck.HumbleGamingBrick seam
        // (risk 1 in the arc plan). Until it lands, a link is reported DORMANT with this reason rather than moving bytes
        // through an unsafe cross-thread step.
        reason = "live cable linking of running gaming-brick machines is not yet wired for the queued host";

        return false;
    }

    /// <summary>Parses the space-separated options string (order-independent) into a hardware model and the fairness
    /// pin — the ONE options grammar, shared by <see cref="Create"/> and a host's live reconfigure. A model keyword sets
    /// the console costume; the <c>dmgspeed</c> token applies the fairness pin. An unknown token throws so a typo is
    /// loud, not silently defaulted.</summary>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for defaults.</param>
    /// <returns>The parsed model and fairness pin.</returns>
    /// <exception cref="ArgumentException">A token is not a recognized option.</exception>
    internal static (ConsoleModel Model, bool DmgSpeed) ParseOptions(string? options) {
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

    /// <summary>Formats a model + fairness pin back into the canonical options string — the inverse of
    /// <see cref="ParseOptions"/>, so a host's <c>screen.options</c> echo and <c>world.save</c> readback speak the same
    /// vocabulary an author wrote.</summary>
    /// <param name="model">The current model.</param>
    /// <param name="dmgSpeed">Whether the fairness pin is set.</param>
    /// <returns>The options string (e.g. <c>cgb</c> or <c>dmg dmgspeed</c>).</returns>
    internal static string FormatOptions(ConsoleModel model, bool dmgSpeed) {
        var modelToken = model switch {
            ConsoleModel.Cgb => "cgb",
            ConsoleModel.Agb => "agb",
            _ => "dmg",
        };

        return (dmgSpeed ? $"{modelToken} {DmgSpeedToken}" : modelToken);
    }
}

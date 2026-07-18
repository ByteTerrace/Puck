using System.CommandLine;
using Puck.Commands;
using Puck.Launcher;
using Puck.Scene;

namespace Puck.Demo;

/// <summary>
/// The live <c>present-rate</c> console verb: sets the window pump's display-aware pacer target to one of the safe enumerated
/// present-rate tiers (<see cref="PresentRateTiers.Names"/>: <c>sixty</c>/<c>one-twenty</c>/<c>display</c>), or (with no
/// argument) echoes the current tier and the full valid set. It writes the resolved target Hz onto the launcher's
/// <see cref="PresentPacingControl"/>, which the pump re-reads next frame — presentation pacing ONLY, so the fixed-step
/// simulation is never touched. The durable form is the run-doc <c>host.presentRate</c> field; this verb is the
/// mid-session assist layer (mirroring the <c>render-scale</c> verb over <c>host.revealedRenderScale</c>).
/// </summary>
internal sealed class PresentRateCommandModule(PresentPacingControl pacing) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Sets the present-rate target live: present-rate [sixty|one-twenty|display] — 60 Hz, 120 Hz, or automatic display pacing from verified VRR capabilities/active signal timing. No argument echoes the current tier and valid set. Presentation pacing only; the fixed-step sim is untouched. The durable form is host.presentRate.",
            handler: (_, args) => new CommandResult(SetPresentRate(name: ((args.Length > 0) ? args[0] : null))),
            name: "present-rate"
        );
    }

    private string SetPresentRate(string? name) {
        // No argument: echo the current tier (read back from the pacing control) and the full valid set — the knob is a
        // fixed menu, not a free value.
        if (string.IsNullOrWhiteSpace(value: name)) {
            return $"[present-rate: {PresentRateTiers.NameForHertz(targetHertz: pacing.TargetHertz)} | tiers: {PresentRateTiers.ValidNames}]";
        }

        if (!PresentRateTiers.TryParse(name: name, tier: out var tier)) {
            return $"[present-rate: unknown tier '{name}' — valid: {PresentRateTiers.ValidNames}]";
        }

        var hertz = PresentRateTiers.TargetHertz(tier: tier);

        pacing.SetTargetHertz(targetHertz: hertz);

        return ((hertz > 0U)
            ? $"[present-rate: {PresentRateTiers.Name(tier: tier)} — the pacer targets {hertz} Hz]"
            : $"[present-rate: {PresentRateTiers.Name(tier: tier)} — automatic from verified VRR capabilities or active signal timing]");
    }

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors GardenCommandModule.WithArgs).
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}

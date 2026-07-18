using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Puck.Bench;
using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// Wires the bench's completion seam to the keyboard celebration: when a scored <c>bench.run</c> finishes, the
/// overall score plays as a tier-colored wave across the physical keyboard — green at reference-or-better, amber
/// close, ember below. Presentation only; an aborted or refused run celebrates nothing.
/// </summary>
/// <param name="bench">The bench runtime whose completion is celebrated.</param>
/// <param name="lighting">The lighting install that performs it.</param>
internal sealed class BenchCelebrationInstaller(BenchRuntime bench, LightingInstaller lighting) : IHostedService {
    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken) {
        bench.RunCompleted += OnRunCompleted;

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) {
        bench.RunCompleted -= OnRunCompleted;

        return Task.CompletedTask;
    }

    private void OnRunCompleted(BenchRunOutcome outcome) {
        if (outcome.Succeeded) {
            lighting.Celebrate(score: outcome.Score.Overall);
        }
    }
}

/// <summary>
/// The <c>light.celebrate</c> console verb: fires the keyboard score celebration on demand — the same show a
/// completed <c>bench.run</c> plays without requiring a benchmark run.
/// </summary>
/// <param name="lighting">The lighting install the celebration plays on.</param>
internal sealed class LightingCommandModule(LightingInstaller lighting) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Fires the keyboard score celebration: light.celebrate [score] — a tier-colored wave sweeps the board and settles into a glow (green at reference-or-better, amber close, ember below; default 10000). The same show a scored bench.run fires on completion. Presentation only; a no-op without a lamp-array keyboard.",
            handler: (_, args) => new CommandResult(Celebrate(args: args)),
            name: "light.celebrate"
        );
    }

    private string Celebrate(string[] args) {
        var score = 10_000;

        if ((args.Length > 0) && !int.TryParse(s: args[0], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out score)) {
            return $"[light.celebrate: '{args[0]}' is not a score]";
        }

        if (!lighting.HasKeyboard) {
            return "[light.celebrate: no lamp-array keyboard present — nothing to light]";
        }

        lighting.Celebrate(score: score);

        return $"[light.celebrate: fired for {score.ToString(provider: CultureInfo.InvariantCulture)} — watch the keyboard]";
    }

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors PresentRateCommandModule.WithArgs).
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

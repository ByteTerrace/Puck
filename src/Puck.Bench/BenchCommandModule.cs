using System.CommandLine;
using System.Globalization;
using System.Text;

using Puck.Commands;

namespace Puck.Bench;

/// <summary>
/// The benchmark console verbs (§9) — the primary, console-first entry points, reachable over the on-screen panel AND
/// stdin in the one running session: <c>bench.list</c>, <c>bench.run [suite] [samples]</c>, <c>bench.abort</c>,
/// <c>bench.sweep &lt;switch&gt;=&lt;v1,v2,...&gt; [suite]</c>. The module is GENERIC — it speaks only
/// <see cref="BenchRuntime"/>, never a backend, the SDF VM, or the demo — so any host that composes a
/// <see cref="BenchRuntime"/> can register it. The per-scene and final result tables are printed by a report sink that
/// subscribes to <see cref="BenchRuntime.SceneCompleted"/>/<see cref="BenchRuntime.RunCompleted"/>; these verbs only
/// start/stop a run and echo the immediate status line.
/// </summary>
public sealed class BenchCommandModule : ICommandModule {
    private readonly BenchRuntime m_bench;

    /// <summary>Creates the module over the host's benchmark harness.</summary>
    /// <param name="bench">The composed benchmark runtime.</param>
    public BenchCommandModule(BenchRuntime bench) {
        ArgumentNullException.ThrowIfNull(argument: bench);

        m_bench = bench;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return Plain(
            description: "Lists the benchmark scenes and suites: bench.list.",
            handler: _ => new CommandResult(ListText()),
            name: "bench.list"
        );
        yield return WithArgs(
            description: "Runs a scored benchmark suite: bench.run [suite] [samples] (suite defaults to 'standard'; add 'samples' to dump raw per-frame arrays).",
            handler: (_, args) => new CommandResult(RunFromArgs(args: args)),
            name: "bench.run"
        );
        yield return Plain(
            description: "Aborts the active benchmark run (restores the environment, reports nothing scored): bench.abort.",
            handler: _ => new CommandResult(m_bench.Abort()),
            name: "bench.abort"
        );
        yield return WithArgs(
            description: "Sweeps a suite once per switch value: bench.sweep <switch>=<v1,v2,...> [suite].",
            handler: (_, args) => new CommandResult(SweepFromArgs(args: args)),
            name: "bench.sweep"
        );
        yield return WithArgs(
            description: "Compares two puck.bench.v1 reports and prints the per-scene diff table: bench.compare <a> <b> (each a report path or the alias 'latest'/'prev', resolved by filename sort under bench-reports/). Read-only; refuses on a scoreFormula mismatch. A CI regression-gating exit code is deliberately deferred (the headless --bench-compare is the same diff).",
            handler: (_, args) => new CommandResult(CompareFromArgs(args: args)),
            name: "bench.compare"
        );
    }

    // Parses bench.compare <a> <b>: two report paths (or the aliases 'latest'/'prev'). The full diff table streams to
    // Console.Out (the room.bench long-output precedent); the verb echoes only the one-line summary.
    private static string CompareFromArgs(string[] args) {
        if (args.Length < 2) {
            return "[bench.compare: usage — bench.compare <a> <b> (report paths, or the aliases 'latest'/'prev')]";
        }

        return BenchReportComparer.Run(pathA: args[0], pathB: args[1], writer: Console.Out).Summary;
    }

    // Parses bench.run [suite] [samples]: the suite is the first non-'samples' token, 'samples' anywhere requests a raw
    // dump (usage strings + forgiving parsing beat parser errors on a game console).
    private string RunFromArgs(string[] args) {
        var suite = BenchRuntime.StandardSuite;
        var includeSamples = false;

        foreach (var token in args) {
            if (string.Equals(a: token, b: "samples", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                includeSamples = true;
            } else {
                suite = token;
            }
        }

        return m_bench.StartRun(suite: suite, includeSamples: includeSamples);
    }

    // Parses bench.sweep <switch>=<v1,v2,...> [suite].
    private string SweepFromArgs(string[] args) {
        if (args.Length == 0) {
            return "[bench.sweep: usage — bench.sweep <switch>=<v1,v2,...> [suite]]";
        }

        var separator = args[0].IndexOf(value: '=');

        if (separator <= 0) {
            return "[bench.sweep: usage — bench.sweep <switch>=<v1,v2,...> [suite]]";
        }

        var switchName = args[0][..separator];
        var valuesText = args[0][(separator + 1)..];
        var values = valuesText.Split(separator: ',', options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (values.Length == 0) {
            return $"[bench.sweep: give at least one value — bench.sweep {switchName}=<v1,v2,...>]";
        }

        var suite = ((args.Length > 1) ? args[1] : BenchRuntime.StandardSuite);

        return m_bench.StartSweep(switchName: switchName, values: values, suite: suite);
    }

    // Renders the fixed-width scene list + suite summary.
    private string ListText() {
        var scenes = m_bench.Scenes;

        if (scenes.Count == 0) {
            return "[bench.list: no scenes registered — the host attached none]";
        }

        var builder = new StringBuilder();

        builder.Append(value: "[bench] suite ").Append(value: BenchRuntime.StandardSuite).Append(value: " — ").Append(value: scenes.Count).Append(value: " scene(s)").Append(value: '\n');
        builder.Append(value: "[bench] scene                    category  warm  samples  weight").Append(value: '\n');

        foreach (var scene in scenes) {
            builder
                .Append(value: "[bench]   ")
                .Append(value: scene.Name.PadRight(totalWidth: 24))
                .Append(value: scene.Category.PadRight(totalWidth: 9))
                .Append(value: scene.WarmFrames.ToString(provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 5))
                .Append(value: scene.SampleFrames.ToString(provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 8))
                .Append(value: "  ")
                .Append(value: scene.Weight.ToString(format: "0.00", provider: CultureInfo.InvariantCulture).PadLeft(totalWidth: 5))
                .Append(value: '\n');
        }

        return builder.ToString().TrimEnd(trimChar: '\n');
    }

    // A bare-verb command definition.
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking command definition: one trailing token list parsed by the handler (usage strings beat parser
    // errors on a game console) — the AddonCommandModule idiom.
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

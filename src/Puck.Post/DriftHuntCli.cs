using Puck.Hosting;

namespace Puck.Post;

/// <summary>
/// The command-line seam for the drift HUNT, kept OUT of <c>Program.cs</c> so the composition root's class-coupling
/// stays under its ceiling (the hunt's argument parsing and node construction reference several hunt types; funnelling
/// them through this one helper means <c>Main</c> references only <see cref="DriftHuntCli"/>). Two surfaces:
/// <see cref="TryRunOrchestrator"/> handles the PARENT <c>--hunt-drift</c> mode (which returns before the GPU host is
/// built), and <see cref="IsRenderMode"/> + <see cref="CreateRenderNode"/> handle the CHILD <c>--hunt-render</c> mode
/// (a one-shot render node hosted in place of the battery).
/// </summary>
internal static class DriftHuntCli {
    /// <summary>Runs the parent orchestrator when <c>--hunt-drift [rounds] [seedBase]</c> is present, honoring the
    /// named <c>--hunt-top</c>/<c>--hunt-timeout</c> knobs.</summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="exitCode">The orchestrator's exit code when it ran; 0 otherwise.</param>
    /// <returns><see langword="true"/> when the orchestrator ran (the caller should return <paramref name="exitCode"/>
    /// without building the battery host); <see langword="false"/> to continue to the normal battery/child path.</returns>
    public static bool TryRunOrchestrator(string[] args, out int exitCode) {
        var huntDriftIndex = Array.FindIndex(array: args, match: static argument => string.Equals(a: argument, b: "--hunt-drift", comparisonType: StringComparison.OrdinalIgnoreCase));

        if (huntDriftIndex < 0) {
            exitCode = 0;

            return false;
        }

        // Positional [rounds] [seedBase] after the flag: bare integers only, stopping at the first flag/non-integer so
        // the named `--hunt-top`/`--hunt-timeout` knobs are never mis-read as positionals.
        var positionals = new List<int>();

        for (var index = (huntDriftIndex + 1); ((index < args.Length) && (positionals.Count < 2)); index++) {
            if (args[index].StartsWith(value: "--", comparisonType: StringComparison.Ordinal) || !int.TryParse(s: args[index], result: out var positional)) {
                break;
            }

            positionals.Add(item: positional);
        }

        var topValue = ArgValue(args: args, name: "--hunt-top");
        var timeoutValue = ArgValue(args: args, name: "--hunt-timeout");

        exitCode = DriftHunt.Run(
            rounds: ((positionals.Count > 0) ? positionals[0] : 24),
            seedBase: ((positionals.Count > 1) ? positionals[1] : 0),
            timeoutSeconds: ((timeoutValue is null) ? DriftHunt.DefaultTimeoutSeconds : int.Parse(s: timeoutValue)),
            topN: ((topValue is null) ? 8 : int.Parse(s: topValue))
        );

        return true;
    }

    /// <summary>Whether this process is a drift-hunt render child (<c>--hunt-render</c>).</summary>
    /// <param name="args">The process arguments.</param>
    /// <returns><see langword="true"/> when the child render node should be hosted instead of the battery.</returns>
    public static bool IsRenderMode(string[] args) =>
        Array.Exists(array: args, match: static argument => string.Equals(a: argument, b: "--hunt-render", comparisonType: StringComparison.OrdinalIgnoreCase));

    /// <summary>Builds the child render node from <c>--hunt-seed</c>/<c>--hunt-artifacts</c>/<c>--hunt-rank</c>.</summary>
    /// <param name="services">The application service provider.</param>
    /// <param name="args">The process arguments.</param>
    /// <param name="runResult">The shared exit-code carrier.</param>
    /// <returns>The one-shot render node.</returns>
    public static IRenderNode CreateRenderNode(IServiceProvider services, string[] args, PostRunResult runResult) {
        var rankValue = ArgValue(args: args, name: "--hunt-rank");
        var seedValue = ArgValue(args: args, name: "--hunt-seed");

        return new DriftHuntRenderNode(
            artifactsDirectory: ArgValue(args: args, name: "--hunt-artifacts"),
            rank: ((rankValue is null) ? 0 : int.Parse(s: rankValue)),
            runResult: runResult,
            seed: ((seedValue is null) ? 0 : int.Parse(s: seedValue)),
            services: services
        );
    }

    private static string? ArgValue(string[] args, string name) {
        for (var index = 0; (index < (args.Length - 1)); index++) {
            if (string.Equals(a: args[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return args[(index + 1)];
            }
        }

        return null;
    }
}

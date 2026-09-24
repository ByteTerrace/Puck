using System.Text.RegularExpressions;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    // The exit code and stderr prefix a World boot uses when the host cannot provide a resource the run requires: its
    // selected backend has no usable device or the operating system does not offer it, or a listener cannot bind
    // (Program.cs runs its host through LauncherHostRun with the label "world"). Only the first kind is UNSUPPORTED;
    // see ListenerRefusal for the second.
    private const int WorldUnsupportedExitCode = Puck.Launcher.LauncherHostRun.UnsupportedExitCode;

    private static readonly string WorldUnsupportedPrefix = Puck.Launcher.LauncherHostRun.UnsupportedLinePrefix(label: "world");

    // One proof as the runner executes it: an offscreen manifest runs once per declared backend, every other shape once
    // with no backend named.
    internal sealed record CanaryProof(CanaryManifest Manifest, string? Backend) {
        public string Label => ((Backend is null)
            ? Manifest.Id
            : $"{Manifest.Id} on {Backend}");
    }

    internal static IReadOnlyList<CanaryProof> ExpandProofs(IReadOnlyList<CanaryManifest> manifests) =>
        manifests.SelectMany(selector: static manifest => ((manifest.BootShape == CanaryBootShape.Offscreen)
            ? manifest.Backends.Select(selector: backend => new CanaryProof(
                Backend: backend,
                Manifest: manifest
            ))
            : [new CanaryProof(
                Backend: null,
                Manifest: manifest
            )])).ToArray();

    // The boot-shape arguments of one leg process. An offscreen leg lets its world's host.presentation decide the shape
    // (a --headless value would force windowed or none over it), names the backend explicitly, and carries the run's
    // validation-layer choice for that backend.
    private static string[] BootShapeArguments(CanaryManifest manifest, string? backend, bool debugLayers) => manifest.BootShape switch {
        CanaryBootShape.Offscreen => ["--backend", backend!, .. (debugLayers ? [WorldOffscreenLeg.DebugLayersFlag] : Array.Empty<string>())],
        CanaryBootShape.Headless => ["--headless", "true"],
        _ => ["--headless", "false"],
    };

    // A pipeline.wait outcome narrated through the World's pipeline report: "[pipeline: <name> wait <phase> <outcome>".
    [GeneratedRegex(pattern: @"^\[pipeline: (?<name>\S+) wait (?<phase>compiled|installed|captured|submitted \d+|counted \d+|resized \d+ \d+) (?<outcome>reached|failed|unsupported|timed out)")]
    internal static partial Regex PipelineWaitOutcome();
    // A pipeline load the World could not compile because a shader tool is absent: "[pipeline: <name> unsupported: …".
    [GeneratedRegex(pattern: @"^\[pipeline: \S+ unsupported: ")]
    internal static partial Regex PipelineUnsupported();

    // The resource a refused bind names after the host prefix, as ListenEndpointUnavailableException words it.
    [GeneratedRegex(pattern: @"^\S+ listener \S+ unavailable: ")]
    private static partial Regex ListenerUnavailable();

    /// <summary>Names a listener the World could not bind, or returns <see langword="null"/> when the leg process
    /// reported none. Every endpoint a canary World binds is one the runner picked, so a refused bind is the run's own
    /// infrastructure failing (a port taken between the runner's probe and the World's bind), never a capability the
    /// machine lacks: the leg fails as infrastructure rather than UNSUPPORTED, which would set the proof aside instead
    /// of judging it. QUIC itself is a prerequisite the runner's machine is provisioned with, not a capability a
    /// manifest declares.</summary>
    /// <param name="stderr">The standard error lines of one leg process.</param>
    /// <returns>The <c>[world.host: unsupported: &lt;transport&gt; listener &lt;endpoint&gt; unavailable: …]</c> line,
    /// or <see langword="null"/>.</returns>
    internal static string? ListenerRefusal(IEnumerable<string> stderr) =>
        stderr.FirstOrDefault(predicate: static line => (line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldUnsupportedPrefix
        ) && ListenerUnavailable().IsMatch(input: line[WorldUnsupportedPrefix.Length..])));
    /// <summary>Names why a finished leg could not exercise its environment, or returns <see langword="null"/> when it
    /// could: the World announced its backend unusable and exited with the unsupported code, or a pipeline could not
    /// compile because a shader tool is absent. A refused listener is neither (see <see cref="ListenerRefusal"/>), and
    /// in a leg that hides the compiler an absent shader tool is the observation the leg makes, never the environment
    /// lacking one.</summary>
    /// <param name="exitCode">The leg process's exit code.</param>
    /// <param name="transcript">The leg's captured streams.</param>
    /// <param name="compilerHidden">Whether the leg booted the World with the shader compiler hidden
    /// (<see cref="CanaryLeg.HideShaderCompiler"/>).</param>
    /// <returns>The announcing line, or <see langword="null"/>.</returns>
    internal static string? UnsupportedReason(int exitCode, CanaryTranscript transcript, bool compilerHidden = false) {
        if (
            (exitCode == WorldUnsupportedExitCode) &&
            (transcript.Stderr.FirstOrDefault(predicate: static line => (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: WorldUnsupportedPrefix
            ) && !ListenerUnavailable().IsMatch(input: line[WorldUnsupportedPrefix.Length..]))) is { } host)
        ) {
            return host;
        }

        return (compilerHidden
            ? null
            : transcript.Stderr.FirstOrDefault(predicate: static line => (PipelineUnsupported().IsMatch(input: line) || ((PipelineWaitOutcome().Match(input: line) is { Success: true } match) && (match.Groups["outcome"].Value == "unsupported")))));
    }
    /// <summary>Checks a leg's pipeline waits: every armed <c>pipeline.wait</c> reports exactly one outcome, and none ran
    /// out its deadline. Each outcome is listed by instance and phase, so a failure names the phase it stopped at.
    /// Reached and failed outcomes are observations the manifest's own assertions judge.</summary>
    /// <param name="transcript">The leg's captured streams.</param>
    /// <returns>No result when the leg armed no wait; otherwise one result per outcome plus the accounting result.</returns>
    internal static IReadOnlyList<CanaryAssertionResult> PipelineWaitInvariants(CanaryTranscript transcript) {
        var armed = transcript.Stdout.Count(predicate: static line => line.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: "[pipeline.wait:"
        ));
        var outcomes = transcript.Stderr.Select(selector: static line => PipelineWaitOutcome().Match(input: line)).Where(predicate: static match => match.Success).ToArray();

        if (
            (armed == 0) &&
            (outcomes.Length == 0)
        ) {
            return [];
        }

        var results = new List<CanaryAssertionResult>(capacity: (outcomes.Length + 1)) {
            new(
            Detail: $"every pipeline.wait reported one outcome: {outcomes.Length} outcome(s) for {armed} armed wait(s)",
            Passed: (outcomes.Length == armed)
        ),
        };

        foreach (var outcome in outcomes) {
            results.Add(item: new CanaryAssertionResult(
                Detail: $"pipeline.wait {outcome.Groups["name"].Value} {outcome.Groups["phase"].Value}: {outcome.Groups["outcome"].Value}",
                Passed: (outcome.Groups["outcome"].Value != "timed out")
            ));
        }

        return results;
    }
}

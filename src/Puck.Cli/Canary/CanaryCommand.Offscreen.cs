using System.Text.RegularExpressions;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    // The exit code and stderr prefix a World boot uses when the host cannot provide a resource the run requires: its
    // selected backend has no usable device or the operating system does not offer it, or a listener cannot bind
    // (Program.cs runs its host through LauncherHostRun with the label "world"). Only the first kind is UNSUPPORTED;
    // see ListenerRefusal for the second.
    private const int WorldUnsupportedExitCode = Puck.Launcher.LauncherHostRun.UnsupportedExitCode;

    private static readonly string WorldUnsupportedPrefix = Puck.Launcher.LauncherHostRun.UnsupportedLinePrefix(label: "world");

    // One proof as the runner executes it: a manifest that declares backends runs once per selected backend, every other
    // one once with no backend named.
    internal sealed record CanaryProof(CanaryManifest Manifest, string? Backend) {
        public string Label => ((Backend is null)
            ? Manifest.Id
            : $"{Manifest.Id} on {Backend}");
    }

    /// <summary>The backends a run boots its backend-declaring proofs on: every backend a leg boots
    /// (<see cref="WorldOffscreenLeg.Backends"/>), or only the one <c>--backend</c> names.</summary>
    /// <param name="backend">The <c>--backend</c> value, or <see langword="null"/> for every backend.</param>
    /// <returns>The selected backends, in the order a run boots them.</returns>
    /// <exception cref="ArgumentException"><paramref name="backend"/> names no backend a leg boots.</exception>
    internal static IReadOnlyList<string> SelectBackends(string? backend) => ((backend is null)
        ? WorldOffscreenLeg.Backends
        : (WorldOffscreenLeg.Backends.Contains(
            comparer: StringComparer.Ordinal,
            value: backend
        )
            ? [backend]
            : throw new ArgumentException(
                message: $"'{backend}' is not a backend a leg boots; use {string.Join(separator: " or ", values: WorldOffscreenLeg.Backends)}.",
                paramName: nameof(backend)
            )));
    /// <summary>Expands each manifest into the proofs a run executes: one per selected backend for a manifest that
    /// declares backends, in its authored order, and one naming no backend for every other manifest.</summary>
    /// <param name="manifests">The selected manifests, in authored order.</param>
    /// <param name="backends">The backends the run boots (<see cref="SelectBackends"/>).</param>
    /// <returns>The proofs, in authored order.</returns>
    internal static IReadOnlyList<CanaryProof> ExpandProofs(IReadOnlyList<CanaryManifest> manifests, IReadOnlyList<string> backends) =>
        manifests.SelectMany(selector: manifest => ((manifest.Backends.Count != 0)
            ? manifest.Backends.Where(predicate: backend => backends.Contains(
                comparer: StringComparer.Ordinal,
                value: backend
            )).Select(selector: backend => new CanaryProof(
                Backend: backend,
                Manifest: manifest
            ))
            : [new CanaryProof(
                Backend: null,
                Manifest: manifest
            )])).ToArray();
    /// <summary>Names the backends <paramref name="proofs"/> boot on, so a run on one backend never reads as a run on
    /// both: <c>on vulkan and directx</c>, or <c>on vulkan only (--backend vulkan), not on directx</c>.</summary>
    /// <param name="proofs">The run's proofs.</param>
    /// <returns>The scope, or <see langword="null"/> when no proof names a backend.</returns>
    internal static string? BackendScope(IReadOnlyList<CanaryProof> proofs) {
        var ran = WorldOffscreenLeg.Backends.Where(predicate: backend => proofs.Any(predicate: proof => string.Equals(
            a: proof.Backend,
            b: backend,
            comparisonType: StringComparison.Ordinal
        ))).ToArray();

        if (ran.Length == 0) {
            return null;
        }

        var skipped = WorldOffscreenLeg.Backends.Except(second: ran).ToArray();
        var named = string.Join(
            separator: " and ",
            values: ran
        );

        return ((skipped.Length == 0)
            ? $"on {named}"
            : $"on {named} only (--backend {named}), not on {string.Join(separator: " and ", values: skipped)}");
    }

    // The boot-shape arguments of one leg process. An offscreen leg lets its world's host.presentation decide the shape
    // (a --headless value would force windowed or none over it), names the backend explicitly, and carries the run's
    // validation-layer choice for that backend. A windowed leg forces a window, and when its proof declares backends it
    // names the backend and carries the validation-layer choice the same way.
    private static string[] BootShapeArguments(CanaryManifest manifest, string? backend, bool debugLayers) => manifest.BootShape switch {
        CanaryBootShape.Offscreen => BackendArguments(
            backend: backend!,
            debugLayers: debugLayers
        ),
        CanaryBootShape.Headless => ["--headless", "true"],
        _ => ["--headless", "false", .. ((backend is null)
            ? []
            : BackendArguments(
                backend: backend,
                debugLayers: debugLayers
            ))],
    };
    private static string[] BackendArguments(string backend, bool debugLayers) => ["--backend", backend, .. (debugLayers ? [WorldOffscreenLeg.DebugLayersFlag] : Array.Empty<string>())];

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

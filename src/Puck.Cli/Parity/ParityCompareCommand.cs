using Puck.Assets;

namespace Puck.Cli.Parity;

/// <summary><c>puck parity compare &lt;leftDir&gt; &lt;rightDir&gt;</c> — reads two <c>puck.parity.manifest.v1</c>
/// runs (a pinned capture pipeline's output, not authored here) and, per scheduled capture, runs a content gate,
/// an exact stateHash check, and a per-tile pixel check (<see cref="ParityComparator"/>). A gate failure is
/// never "parity held"; a per-tile check catches a localized defect a whole-frame mean would dilute away. Every
/// verdict prints a line naming its station, tick, and outcome; a failed capture's evidence — both frames, a
/// delta heatmap, and a one-line-per-verdict summary — lands under <c>--out</c>.</summary>
internal static class ParityCompareCommand {
    private const string ManifestFileName = "manifest.json";
    private const string ScratchPrefix = "puck-parity-compare-";

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }
        if (!TryParse(args: args, contractPath: out var contractPath, error: out var parseError, leftDir: out var leftDir, outDir: out var outDir, rightDir: out var rightDir)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 3;
        }
        if (!ParityManifestLoader.TryLoadContract(contract: out var contract, error: out var contractError, path: contractPath)) {
            Console.Error.WriteLine(value: $"ERROR: {contractError}");

            return 3;
        }

        var leftManifestPath = Path.Combine(path1: leftDir, path2: ManifestFileName);
        var rightManifestPath = Path.Combine(path1: rightDir, path2: ManifestFileName);

        if (!File.Exists(path: leftManifestPath)) {
            Console.Error.WriteLine(value: $"ERROR: left manifest '{leftManifestPath}' does not exist.");

            return 3;
        }
        if (!File.Exists(path: rightManifestPath)) {
            Console.Error.WriteLine(value: $"ERROR: right manifest '{rightManifestPath}' does not exist.");

            return 3;
        }
        if (!ParityManifestLoader.TryLoadManifest(error: out var leftError, manifest: out var leftManifest, path: leftManifestPath)) {
            Console.Error.WriteLine(value: $"ERROR: {leftError}");

            return 3;
        }
        if (!ParityManifestLoader.TryLoadManifest(error: out var rightError, manifest: out var rightManifest, path: rightManifestPath)) {
            Console.Error.WriteLine(value: $"ERROR: {rightError}");

            return 3;
        }
        if (!ParityComparator.TryCompare(contract: contract, error: out var compareError, left: leftManifest, leftDir: leftDir, outcomes: out var outcomes, right: rightManifest, rightDir: rightDir)) {
            Console.Error.WriteLine(value: $"ERROR: {compareError}");

            return 3;
        }

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var resolvedOutDir = (outDir ?? CliScratchDirectories.CreateRunDirectory(scratchPrefix: ScratchPrefix));

        Directory.CreateDirectory(path: resolvedOutDir);
        Console.WriteLine(value: $"parity compare: evidence directory {resolvedOutDir}");

        var failedCount = 0;

        foreach (var outcome in outcomes) {
            foreach (var verdict in outcome.Verdicts) {
                Console.WriteLine(value: $"parity compare: {outcome.Station} tick={outcome.Tick} {verdict.Name} {verdict.Detail}");
            }

            if (outcome.Failed) {
                failedCount++;

                WriteEvidence(outDir: resolvedOutDir, outcome: outcome);
            }
        }

        if (failedCount != 0) {
            Console.Error.WriteLine(value: $"FAIL: {failedCount} of {outcomes.Count} capture(s) failed at least one verdict; evidence in {resolvedOutDir}.");

            return 2;
        }

        Console.WriteLine(value: $"PASS: {outcomes.Count} capture(s) held every verdict (content gate, stateHash, per-tile pixel).");

        return 0;
    }

    private static void WriteEvidence(ParityCaptureOutcome outcome, string outDir) {
        var captureDirectory = Path.Combine(path1: outDir, path2: $"{outcome.Station}-{outcome.Tick}");

        Directory.CreateDirectory(path: captureDirectory);

        if (outcome.LeftFrame is { } leftFrame) {
            PngEncoder.Write(height: leftFrame.Height, path: Path.Combine(path1: captureDirectory, path2: "left.png"), rgba: leftFrame.RgbaPixels, width: leftFrame.Width);
        }
        if (outcome.RightFrame is { } rightFrame) {
            PngEncoder.Write(height: rightFrame.Height, path: Path.Combine(path1: captureDirectory, path2: "right.png"), rgba: rightFrame.RgbaPixels, width: rightFrame.Width);
        }
        if ((outcome.HeatmapRgba is { } heatmap) && (outcome.LeftFrame is { } extent)) {
            PngEncoder.Write(height: extent.Height, path: Path.Combine(path1: captureDirectory, path2: "delta-heatmap.png"), rgba: heatmap, width: extent.Width);
        }

        File.WriteAllLines(
            contents: outcome.Verdicts.Select(selector: verdict => $"{outcome.Station} tick={outcome.Tick} {verdict.Name} {verdict.Detail}"),
            path: Path.Combine(path1: captureDirectory, path2: "summary.txt")
        );
    }
    private static bool TryParse(string[] args, out string leftDir, out string rightDir, out string contractPath, out string? outDir, out string error) {
        leftDir = string.Empty;
        rightDir = string.Empty;
        contractPath = string.Empty;
        outDir = null;
        error = string.Empty;

        var scanner = new ArgScanner();

        scanner.Value(name: "contract");
        scanner.Value(name: "out");

        if (!scanner.Parse(args: args)) {
            error = scanner.Error!;

            return false;
        }
        if (scanner.Positionals.Count != 2) {
            error = "the only accepted form is: parity compare <leftDir> <rightDir> --contract <file> [--out <dir>]";

            return false;
        }

        contractPath = (scanner.Get(name: "contract") ?? string.Empty);

        if (contractPath.Length == 0) {
            error = "--contract <file> is required.";

            return false;
        }
        if (!File.Exists(path: contractPath)) {
            error = $"--contract file '{contractPath}' does not exist.";

            return false;
        }

        leftDir = scanner.Positionals[0];
        rightDir = scanner.Positionals[1];

        if (!Directory.Exists(path: leftDir)) {
            error = $"left directory '{leftDir}' does not exist.";

            return false;
        }
        if (!Directory.Exists(path: rightDir)) {
            error = $"right directory '{rightDir}' does not exist.";

            return false;
        }

        outDir = scanner.Get(name: "out");

        return true;
    }
    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                parity compare <leftDir> <rightDir> --contract <file> [--out <dir>]

                  leftDir, rightDir   two directories each holding one puck.parity.manifest.v1 (manifest.json)
                                      plus the PNG frames it names — the pinned output of a parity capture run
                  --contract <file>   puck.parity.contract.v1: tile size, per-station census floors, per-station
                                      per-tile mean/max pixel thresholds (see ParityContractModel.cs)
                  --out <dir>         where failed-capture evidence is written; a fresh temp directory if omitted

                Per capture, in order: a content gate (cameraInside, a missing frame, or a census below its
                station's floor refuses the capture before any pixel comparison), an exact stateHash check, and
                a per-tile pixel check (any tile exceeding its station's mean or max threshold fails the
                capture). The gate, state, and pixel checks are independent verdicts — a gate failure skips the
                other two; state and pixel are always both computed and both printed once the gate holds. Every
                verdict prints one line naming its station, tick, and outcome.

                Exit codes: 0 every capture held every verdict, 2 at least one verdict failed, 3 a malformed
                manifest, contract, or argument (distinct from a parity failure).
                """);

        return 3;
    }
}

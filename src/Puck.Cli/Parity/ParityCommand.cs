using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.Assets;

namespace Puck.Cli.Parity;

/// <summary><c>puck parity</c> — the narrow cross-backend composed-frame check. For every corpus entry — the
/// authored pattern worlds under <c>tests/Puck.Parity/</c> plus the shipped default world — it boots the
/// real <c>Puck.World</c> windowed once per graphics backend, arms <c>world.screenshot</c> at the same fenced
/// simulation moment in each run, and compares the backend pair under <see cref="ParityEnvelope"/>. Two different
/// patterns rendered by the same backend must FAIL the same envelope, so a comparator that cannot refuse cannot
/// report green.</summary>
internal static class ParityCommand {
    private const string ScratchPrefix = "puck-parity-";

    private static readonly TimeSpan SuiteBudget = TimeSpan.FromSeconds(value: 600);
    // The authored pattern corpus: each entry stresses one contract slice on purpose, so a divergence names the
    // slice that moved instead of "the game looked different". The shipped default world rides along as the one
    // integration entry (null path = no --world override).
    private static readonly (string Name, string? WorldPath)[] Corpus = [
        ("gradient", "tests/Puck.Parity/parity-gradient.world.json"),
        ("edges", "tests/Puck.Parity/parity-edges.world.json"),
        ("modifiers", "tests/Puck.Parity/parity-modifiers.world.json"),
        ("glyphs", "tests/Puck.Parity/parity-glyphs.world.json"),
        ("film-grain", "tests/Puck.Parity/parity-film-grain.world.json"),
        ("shipped", null),
    ];

    public static int Run(string[] args) {
        if ((Array.IndexOf(array: args, value: "-h") >= 0) || (Array.IndexOf(array: args, value: "--help") >= 0)) {
            return Usage();
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return 2;
        }
        if ((args.Length != 0) && (args[0] == "--generate")) {
            return RunGenerate(args: args[1..], repositoryRoot: repositoryRoot);
        }
        if (!TryParse(args: args, error: out var parseError, extraWorldPath: out var extraWorldPath)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 2;
        }

        var suiteClock = Stopwatch.StartNew();

        CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);

        var runDirectory = CreateRunDirectory();

        Console.WriteLine(value: $"parity: artifacts {runDirectory}");

        if (!TryBuildWorld(artifact: out var artifact, repositoryRoot: repositoryRoot, suiteClock: suiteClock)) {
            return 2;
        }

        var entries = Corpus
            .Select(selector: entry => (entry.Name, WorldPath: ((entry.WorldPath is null) ? null : Path.Combine(path1: repositoryRoot, path2: entry.WorldPath))))
            .ToList();

        if (extraWorldPath is not null) {
            entries.Add(item: ("extra", extraWorldPath));
        }

        foreach (var (name, worldPath) in entries) {
            foreach (var backend in ((string[])["vulkan", "directx"])) {
                var leg = RunLeg(
                    artifact: artifact,
                    backend: backend,
                    entry: name,
                    runDirectory: runDirectory,
                    suiteClock: suiteClock,
                    worldPath: worldPath
                );

                if (leg != 0) {
                    return leg;
                }
            }
        }

        var frames = new Dictionary<string, PngImage>(comparer: StringComparer.Ordinal);

        foreach (var (name, _) in entries) {
            foreach (var backend in ((string[])["vulkan", "directx"])) {
                if (!TryDecode(runDirectory: runDirectory, name: FrameName(backend: backend, entry: name), image: out var image)) {
                    return 2;
                }

                frames[$"{name}-{backend}"] = image;
            }
        }

        if (frames.Values.Select(selector: static image => (image.Width, image.Height)).Distinct().Count() != 1) {
            Console.Error.WriteLine(value: "ERROR: the captured frames disagree on extent; the legs did not observe the same window.");

            return 2;
        }

        var failed = false;

        foreach (var (name, _) in entries) {
            var verdict = ParityEnvelope.Compare(left: frames[$"{name}-vulkan"], right: frames[$"{name}-directx"]);

            Console.WriteLine(value: $"parity: {name} vulkan vs directx — {Describe(verdict: verdict)} => {(verdict.Passed ? "PASS" : "FAIL")}");
            failed |= !verdict.Passed;
        }

        // The comparator's own red leg: two different authored patterns from the SAME backend. If the envelope
        // accepts this pair it can no longer refuse anything, and every green above is void.
        var discriminator = ParityEnvelope.Compare(left: frames["gradient-vulkan"], right: frames["edges-vulkan"]);

        Console.WriteLine(value: $"parity: discriminator (gradient vs edges, same backend) — {Describe(verdict: discriminator)} => {(discriminator.Passed ? "UNEXPECTED PASS" : "FAIL (expected)")}");

        if (failed) {
            Console.Error.WriteLine(value: "FAIL: a backend pair composed measurably different frames — under the relaxed envelope this is a real divergence, not codegen noise.");

            return 1;
        }
        if (discriminator.Passed) {
            Console.Error.WriteLine(value: "FAIL: the comparator accepted two different patterns as one frame, so a green parity verdict from it proves nothing.");

            return 1;
        }

        Console.WriteLine(value: $"PASS: {entries.Count} corpus entr{((entries.Count == 1) ? "y" : "ies")} held cross-backend parity (envelope mean<={ParityEnvelope.MaxMeanDelta.ToString(provider: CultureInfo.InvariantCulture)} LSB, diff<={(ParityEnvelope.MaxDiffFraction * 100.0).ToString(provider: CultureInfo.InvariantCulture)}%), and the comparator refused the cross-pattern pair.");

        return 0;
    }

    private static int RunGenerate(string[] args, string repositoryRoot) {
        if (!TryParseHashes(args: args, error: out var parseError, hashes: out var hashes)) {
            Console.Error.WriteLine(value: $"ERROR: {parseError}");

            return 2;
        }

        foreach (var path in ParityCorpusGenerator.Generate(hashes: hashes, repositoryRoot: repositoryRoot)) {
            Console.WriteLine(value: $"parity: wrote {path}");
        }

        return 0;
    }
    private static bool TryParseHashes(string[] args, out IReadOnlyDictionary<string, string> hashes, out string error) {
        var parsed = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        hashes = parsed;
        error = string.Empty;

        if (args.Length == 0) {
            return true;
        }
        if ((args.Length != 2) || (args[0] != "--hashes")) {
            error = "the only accepted form is: parity --generate [--hashes id=hex64,...]";

            return false;
        }

        foreach (var pair in args[1].Split(options: StringSplitOptions.RemoveEmptyEntries, separator: ',')) {
            var equals = pair.IndexOf(value: '=');

            if (equals <= 0) {
                error = $"--hashes entry '{pair}' is not of the form id=hex64.";

                return false;
            }

            parsed[pair[..equals]] = pair[(equals + 1)..];
        }

        return true;
    }
    private static string Describe(ParityVerdict verdict) =>
        $"mean {verdict.MeanDelta.ToString(format: "0.###", provider: CultureInfo.InvariantCulture)} LSB, diff {(verdict.DiffFraction * 100.0).ToString(format: "0.##", provider: CultureInfo.InvariantCulture)}%, max {verdict.MaxDelta}";
    private static string FrameName(string entry, string backend) => $"{entry}-{backend}.png";
    private static bool TryBuildWorld(string repositoryRoot, Stopwatch suiteClock, out string artifact) {
        var worldProject = Path.Combine(path1: repositoryRoot, path2: "src", path3: "Puck.World", path4: "Puck.World.csproj");

        artifact = Path.Combine(paths: [repositoryRoot, "src", "Puck.World", "bin", "Release", "net10.0", "Puck.World.dll"]);

        Console.WriteLine(value: "parity: building Puck.World once (Release).");

        CliProcessResult build;

        try {
            build = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: ["build", worldProject, "-c", "Release", "--nologo", "--no-restore", "-p:NuGetAudit=false"],
                input: string.Empty,
                timeout: CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock)
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the Puck.World build: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }

        if (build.TimedOut || (build.ExitCode != 0)) {
            Console.Error.WriteLine(value: (build.TimedOut
                ? $"ERROR: the Puck.World build exceeded the {SuiteBudget.TotalSeconds:0}-second whole-suite budget."
                : $"ERROR: the Puck.World build exited {build.ExitCode}."));

            return false;
        }
        if (!File.Exists(path: artifact)) {
            Console.Error.WriteLine(value: $"ERROR: the Puck.World build exited 0 but did not produce the exact artifact {artifact}.");

            return false;
        }

        return true;
    }
    // Boots one windowed leg on the named backend and arms one screenshot at a fenced simulation moment.
    // Returns 0 with the frame written, or 2 with the refusal already reported.
    private static int RunLeg(string artifact, string backend, string entry, string runDirectory, Stopwatch suiteClock, string? worldPath) {
        var frame = FrameName(backend: backend, entry: entry);
        var script = new StringBuilder();

        script.AppendLine(value: "world.wait 60");
        script.AppendLine(value: $"world.screenshot {Path.Combine(path1: runDirectory, path2: frame).Replace(newChar: '/', oldChar: '\\')}");
        script.AppendLine(value: "world.wait 30");
        // Runner-owned completion: follows every authored line, so the exact-count check below proves the whole
        // script was consumed rather than the process merely living until --exit-after-seconds.
        script.AppendLine(value: "wire.errors");

        var remaining = CliProcess.RemainingBudget(budget: SuiteBudget, clock: suiteClock);

        if (remaining <= TimeSpan.FromSeconds(value: 1)) {
            Console.Error.WriteLine(value: $"ERROR: the {SuiteBudget.TotalSeconds:0}-second whole-suite budget was exhausted before the {entry} {backend} leg started.");

            return 2;
        }

        Console.WriteLine(value: $"parity: running {entry} on {backend}.");

        CliProcessResult process;

        try {
            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    .. ((worldPath is null) ? [] : new[] { "--world", worldPath }),
                    "--backend", backend,
                    // Generous relative to the ~2 seconds of authored fences: a cold boot spends several seconds
                    // compiling shaders before the first tick, and the closing wire.errors must still land inside
                    // the window.
                    "--exit-after-seconds", "20",
                    "--state-dir", Path.Combine(path1: runDirectory, path2: $"state-{entry}-{backend}"),
                    "--headless", "false",
                ],
                input: script.ToString(),
                timeout: remaining
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            Console.Error.WriteLine(value: $"ERROR: could not start the {entry} {backend} leg: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return 2;
        }

        File.WriteAllText(
            path: Path.Combine(path1: runDirectory, path2: $"{entry}-{backend}-stdout.log"),
            contents: process.Stdout,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );
        File.WriteAllText(
            path: Path.Combine(path1: runDirectory, path2: $"{entry}-{backend}-stderr.log"),
            contents: process.Stderr,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        );

        if (process.TimedOut || (process.ExitCode != 0)) {
            Console.Error.WriteLine(value: (process.TimedOut
                ? $"ERROR: the {entry} {backend} leg exceeded the remaining suite budget."
                : $"ERROR: the {entry} {backend} leg exited {process.ExitCode}; transcripts are beside the frames."));

            return 2;
        }

        var terminal = process.OutputLines.LastOrDefault(predicate: static line => line.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "[wire.errors:"));

        if ((terminal is null) || (terminal.Stream != CliProcessOutputStream.Stdout) || !string.Equals(a: terminal.Line, b: "[wire.errors: 0 rejected]", comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"ERROR: the {entry} {backend} leg did not accept every scripted command (expected exactly '[wire.errors: 0 rejected]' on stdout); transcripts are beside the frames.");

            return 2;
        }
        if (!File.Exists(path: Path.Combine(path1: runDirectory, path2: frame))) {
            Console.Error.WriteLine(value: $"ERROR: the {entry} {backend} leg exited green but never wrote {frame}; transcripts are beside the frames.");

            return 2;
        }

        return 0;
    }
    private static bool TryDecode(string runDirectory, string name, out PngImage image) {
        var path = Path.Combine(path1: runDirectory, path2: name);

        try {
            image = PngDecoder.Decode(pngBytes: File.ReadAllBytes(path: path));

            return true;
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"ERROR: could not decode {path}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");
            image = default;

            return false;
        }
    }
    private static bool TryParse(string[] args, out string? extraWorldPath, out string error) {
        extraWorldPath = null;
        error = string.Empty;

        if (args.Length == 0) {
            return true;
        }
        if ((args.Length == 2) && (args[0] == "--world")) {
            if (!File.Exists(path: args[1])) {
                error = $"--world names '{args[1]}', which does not exist.";

                return false;
            }

            extraWorldPath = Path.GetFullPath(path: args[1]);

            return true;
        }

        error = "the only accepted form is: parity [--world <path.world.json>]";

        return false;
    }
    private static string CreateRunDirectory() {
        var temp = Path.GetTempPath();

        for (var attempt = 0; (attempt < 8); attempt++) {
            var path = Path.Combine(path1: temp, path2: $"{ScratchPrefix}{Guid.NewGuid():N}");

            if (Directory.Exists(path: path)) {
                continue;
            }

            Directory.CreateDirectory(path: path);

            return path;
        }

        throw new IOException(message: "Could not create a fresh random parity run directory after 8 attempts.");
    }
    private static int Usage() {
        Console.Error.WriteLine(
            value:
                """
                parity [--world <path.world.json>]
                parity --generate [--hashes id=hex64,...]

                  no arguments           run the authored pattern corpus plus the shipped default world
                  --world <path>         additionally run the named world document as a corpus entry
                  --generate             regenerate tests/Puck.Parity/*.world.json from their pattern definitions
                  --hashes id=hex64,...  pin a regenerated creation's canonical hash (see tests/Puck.Parity/README.md)

                For every corpus entry — the targeted pattern worlds under tests/Puck.Parity/
                (gradient, edges, modifiers) and the shipped default world — this boots the real
                Puck.World windowed twice, once per backend, screenshots the same fenced simulation
                moment in each run, and compares the backend pair under the relaxed parity envelope
                (benign ±1-LSB shader-codegen noise passes; a missing, relocated, or recolored region
                fails). Two different patterns from the same backend must FAIL the envelope — a
                comparator that cannot refuse cannot report green.

                Requires a display and both a Vulkan and a Direct3D 12 device on this machine.

                Exit codes: 0 parity held and the discriminator refused, 1 an observation failed, 2 refusal/infrastructure/usage.
                """);

        return 2;
    }
}

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Puck.Cli.Bench;

// Replays the manifest's instruction-service evidence against the pinned llvm-mca scheduling models. This verifies
// the raw instruction rows only: reviewed control-flow formulas, Native AOT symbol extraction, and memory profiles
// remain separate evidence and this command never manufactures them from a successful tool run.
internal static class StateEvidenceCommand {
    internal sealed record McaRow(long Latency, decimal ReciprocalThroughput);
    internal sealed record SourceInventory(string Path, string ExpectedSha256, string? ActualSha256, bool Matches);
    internal sealed record KernelInventory(string Id, string EntryPoint, string Symbol, IReadOnlyList<SourceInventory> Sources, IReadOnlyDictionary<string, IReadOnlyList<string>> UnresolvedTargets);
    internal sealed record EvidenceInventory(string Schema, string ManifestDigest, string Sdk, string RollForward, IReadOnlyList<KernelInventory> Kernels, IReadOnlyDictionary<string, IReadOnlyList<string>> MemoryGaps);

    public static Command Create() {
        var llvmMca = new Option<string?>("--llvm-mca") {
            Description = "llvm-mca executable. Default: PATH, then C:/Program Files/LLVM/bin/llvm-mca.exe on Windows.",
        };
        var inventory = new Option<bool>("--inventory") {
            Description = "Emit a machine-readable inventory of kernel source integrity and unresolved target/memory evidence instead of running llvm-mca.",
        };
        var output = new Option<string?>("--output") {
            Description = "Write --inventory JSON to this path instead of standard output.",
        };
        var command = new Command(
            description: "Replay the reference schedule's instruction forms against every pinned llvm-mca target.",
            name: "state-evidence"
        ) { llvmMca, inventory, output };

        command.SetAction(action: (parse, cancellationToken) => (parse.GetValue(option: inventory)
            ? WriteInventoryAsync(parse.GetValue(option: output), cancellationToken)
            : RunAsync(cancellationToken: cancellationToken, llvmMca: parse.GetValue(option: llvmMca))));
        return command;
    }

    private static async Task<int> WriteInventoryAsync(string? output, CancellationToken cancellationToken) {
        var root = FindRepositoryRoot();

        if (root is null) {
            Console.Error.WriteLine(value: "state-evidence: --inventory must run inside a checkout containing global.json.");
            return 2;
        }
        var report = BuildInventory(repositoryRoot: root);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });

        if (output is null) {
            Console.WriteLine(value: json);
        } else {
            await File.WriteAllTextAsync(Path.GetFullPath(path: output), (json + Environment.NewLine), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        return (report.Kernels.All(predicate: static kernel => kernel.Sources.All(predicate: static source => source.Matches)) ? 0 : 1);
    }

    internal static EvidenceInventory BuildInventory(string repositoryRoot) {
        var kernels = ReferenceScheduleManifest.Kernels.Select(selector: kernel => new KernelInventory(
            kernel.Id,
            kernel.EntryPoint,
            kernel.Symbol,
            [.. kernel.Sources.Select(selector: source => InspectSource(repositoryRoot: repositoryRoot, source: source))],
            kernel.Targets.Where(predicate: static target => (target.Value.Unresolved.Count > 0))
                .ToDictionary(static target => target.Key, static target => target.Value.Unresolved)
        )).ToArray();
        var memory = ReferenceScheduleManifest.MemoryClasses.ToDictionary(
            static entry => entry.Class.ToString(),
            static entry => MemoryGaps(entry: entry)
        );
        var target = ReferenceScheduleManifest.Targets[0];

        return new EvidenceInventory(
            ReferenceScheduleManifest.Schema,
            ReferenceScheduleManifest.Digest,
            target.Build.Sdk,
            target.Build.GlobalJsonRollForward,
            kernels,
            memory
        );
    }

    private static IReadOnlyList<string> MemoryGaps(MemoryClassEvidence entry) {
        var gaps = new List<string>(capacity: 3);

        if (!entry.StartupCycles.IsKnown) { gaps.Add(item: (entry.StartupCycles.Reason ?? "startup cycles unmodeled")); }
        if (!entry.AdditionalLatencyCycles.IsKnown) { gaps.Add(item: (entry.AdditionalLatencyCycles.Reason ?? "additional latency unmodeled")); }
        if (entry.Bandwidth is null) { gaps.Add(item: (entry.BandwidthUnmodeled ?? "bandwidth unmodeled")); }
        return gaps;
    }
    private static SourceInventory InspectSource(string repositoryRoot, KernelSource source) {
        var path = Path.Combine(path1: repositoryRoot, path2: source.Path.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));
        string? actual = null;

        if (File.Exists(path: path)) {
            using var stream = File.OpenRead(path: path);

            actual = Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));
        }
        return new SourceInventory(source.Path, source.Sha256, actual, string.Equals(a: source.Sha256, b: actual, comparisonType: StringComparison.Ordinal));
    }
    private static string? FindRepositoryRoot() {
        for (var directory = new DirectoryInfo(path: Environment.CurrentDirectory); (directory is not null); directory = directory.Parent) {
            if (File.Exists(path: Path.Combine(path1: directory.FullName, path2: "global.json"))) { return directory.FullName; }
        }
        return null;
    }
    private static long Ceiling(decimal value) => decimal.ToInt64(d: decimal.Ceiling(d: value));
    private static string? Resolve(string? requested) {
        if (!string.IsNullOrWhiteSpace(value: requested)) { return Path.GetFullPath(path: requested); }

        var executable = (OperatingSystem.IsWindows() ? "llvm-mca.exe" : "llvm-mca");

        foreach (var directory in (Environment.GetEnvironmentVariable(variable: "PATH") ?? string.Empty).Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: Path.PathSeparator
        )) {
            var candidate = Path.Combine(path1: directory, path2: executable);

            if (File.Exists(path: candidate)) { return candidate; }
        }
        if (OperatingSystem.IsWindows()) {
            var installed = Path.Combine(
                path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.ProgramFiles),
                path2: "LLVM/bin/llvm-mca.exe"
            );

            if (File.Exists(path: installed)) { return installed; }
        }
        return null;
    }
    private static async Task<int> RunAsync(string? llvmMca, CancellationToken cancellationToken) {
        var executable = Resolve(requested: llvmMca);

        if ((executable is null) || !File.Exists(path: executable)) {
            Console.Error.WriteLine(value: "state-evidence: llvm-mca was not found; pass the pinned executable with --llvm-mca.");
            return 2;
        }
        var expectedVersions = ReferenceScheduleManifest.Targets
            .Select(selector: target => target.Analysis.Scheduler)
            .Distinct(comparer: StringComparer.Ordinal)
            .ToArray();

        if (expectedVersions.Length != 1) {
            Console.Error.WriteLine(value: "state-evidence: the manifest does not pin one scheduler version across its cohort.");
            return 2;
        }
        var versionRun = await TryRunAsync(
            arguments: ["--version"],
            cancellationToken: cancellationToken,
            executable: executable,
            standardInput: null
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!versionRun.Success) {
            Console.Error.WriteLine(value: $"state-evidence: could not read the pinned scheduler version: {versionRun.Error}");
            return 2;
        }
        var version = versionRun.Output;
        var expectedVersion = expectedVersions[0].Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ' '
        )[^1];

        if (!TryReadLlvmVersion(output: version, version: out var actualVersion) || !string.Equals(a: expectedVersion, b: actualVersion, comparisonType: StringComparison.Ordinal)) {
            Console.Error.WriteLine(value: $"state-evidence: expected {expectedVersions[0]}, but {executable} reported {version.Split(options: StringSplitOptions.RemoveEmptyEntries, separator: '\n').FirstOrDefault()?.Trim()}.");
            return 2;
        }

        var failures = 0;

        foreach (var target in ReferenceScheduleManifest.Targets) {
            var forms = ReferenceScheduleManifest.InstructionForms[target.Family];
            var source = (string.Join(separator: '\n', values: forms.Select(selector: form => form.Form)) + "\n");

            var run = await TryRunAsync(
                cancellationToken: cancellationToken,
                executable: executable,
                arguments: [
                    $"-mtriple={target.Analysis.Triple}",
                    $"-mcpu={target.Analysis.CpuModel}",
                    "--instruction-info",
                    "--iterations=1",
                ],
                standardInput: source
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (!run.Success) {
                Console.Error.WriteLine(value: $"state-evidence: {target.Id}: llvm-mca refused the recorded forms: {run.Error.Trim()}");
                failures++;
                continue;
            }
            var rows = ParseRows(output: run.Output);

            if (rows.Count != forms.Count) {
                Console.Error.WriteLine(value: $"state-evidence: {target.Id}: llvm-mca returned {rows.Count} instruction rows for {forms.Count} recorded forms.");
                failures++;
                continue;
            }
            var targetFailures = 0;

            for (var index = 0; (index < forms.Count); ++index) {
                var expected = forms[index].Service[target.Id];
                var actual = rows[index];
                var service = Math.Max(
                    val1: 1L,
                    val2: Math.Max(
                        val1: actual.Latency,
                        val2: Ceiling(value: actual.ReciprocalThroughput)
                    )
                );

                if (service == expected.Service) { continue; }
                Console.Error.WriteLine(value: $"state-evidence: {target.Id}: '{forms[index].Form}' records q={expected.Service}, llvm-mca reports q={service} (L={actual.Latency}, T={actual.ReciprocalThroughput.ToString(provider: CultureInfo.InvariantCulture)}).");
                targetFailures++;
            }
            failures += targetFailures;
            Console.WriteLine(value: $"state-evidence: {target.Id}: {(forms.Count - targetFailures)}/{forms.Count} instruction services reproduced.");
        }
        if (failures == 0) {
            Console.WriteLine(value: "state-evidence: instruction-service evidence reproduced; kernel formulas, unresolved helper paths, and memory profiles are outside this check.");
        }
        return ((failures == 0) ? 0 : 1);
    }

    internal static bool TryReadLlvmVersion(string output, out string version) {
        const string Marker = "LLVM version ";

        foreach (var line in output.Split(options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries, separator: '\n')) {
            if (!line.StartsWith(comparisonType: StringComparison.Ordinal, value: Marker)) { continue; }
            version = line[Marker.Length..].Trim();
            return ((version.Length > 0) && !version.Any(predicate: char.IsWhiteSpace));
        }
        version = string.Empty;
        return false;
    }
    internal static IReadOnlyList<McaRow> ParseRows(string output) {
        var rows = new List<McaRow>();
        var sawInstructionInfo = false;
        var inInstructionTable = false;

        foreach (var line in output.Split(separator: '\n')) {
            if (!inInstructionTable) {
                if (line.Trim().Equals(comparisonType: StringComparison.Ordinal, value: "Instruction Info:")) {
                    sawInstructionInfo = true;
                    continue;
                }
                inInstructionTable = (sawInstructionInfo && line.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "Instructions:"
                ) && line.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "[1]"
                ));
                continue;
            }
            if (string.IsNullOrWhiteSpace(value: line)) { break; }
            var fields = line.Split(
                options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
                separator: ((char[]?)null)
            );

            if ((fields.Length < 4) || !long.TryParse(
                s: fields[0],
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out _
            ) || !long.TryParse(
                s: fields[1],
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out var latency
            ) || !decimal.TryParse(
                s: fields[2],
                style: NumberStyles.AllowDecimalPoint,
                provider: CultureInfo.InvariantCulture,
                result: out var throughput
            )) {
                continue;
            }
            rows.Add(item: new(
                Latency: latency,
                ReciprocalThroughput: throughput
            ));
        }
        return rows;
    }

    private static async Task<(bool Success, string Output, string Error)> TryRunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken
    ) {
        var start = new ProcessStartInfo(fileName: executable) {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardInput = (standardInput is not null),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments) { start.ArgumentList.Add(item: argument); }
        using var process = new Process { StartInfo = start };

        try {
            if (!process.Start()) { return (false, string.Empty, $"could not start {executable}"); }
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            return (false, string.Empty, exception.Message);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            token1: cancellationToken,
            token2: CancellationToken.None
        );

        timeout.CancelAfter(delay: TimeSpan.FromSeconds(value: 30D));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken: timeout.Token);
        var error = process.StandardError.ReadToEndAsync(cancellationToken: timeout.Token);

        try {
            if (standardInput is not null) {
                await process.StandardInput.WriteAsync(
                    buffer: standardInput.AsMemory(),
                    cancellationToken: timeout.Token
                ).ConfigureAwait(continueOnCapturedContext: false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(cancellationToken: timeout.Token).ConfigureAwait(continueOnCapturedContext: false);
            return (
                (process.ExitCode == 0),
                await output.ConfigureAwait(continueOnCapturedContext: false),
                await error.ConfigureAwait(continueOnCapturedContext: false)
            );
        } catch (OperationCanceledException) {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            return (false, string.Empty, (cancellationToken.IsCancellationRequested ? "cancelled" : "timed out after 30 seconds"));
        } catch (IOException exception) {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            await process.WaitForExitAsync(cancellationToken: CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
            return (false, string.Empty, exception.Message);
        }
    }
}

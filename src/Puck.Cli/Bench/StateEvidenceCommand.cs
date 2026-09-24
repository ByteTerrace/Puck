using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using Puck.Assets;

namespace Puck.Cli.Bench;

// Replays the manifest's instruction-service evidence against the pinned llvm-mca scheduling models. This verifies
// the raw instruction rows only: reviewed control-flow formulas, Native AOT symbol extraction, and memory profiles
// remain separate evidence and this command never manufactures them from a successful tool run.
internal static class StateEvidenceCommand {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(value: 30D);

    // The checkout this verb reads the manifest's kernel sources and reference build from.
    internal static string? RepositoryRoot => Puck.RepositoryPaths.FindRoot();

    internal sealed record McaRow(long Latency, decimal ReciprocalThroughput);
    internal sealed record SourceInventory(string Path, string ExpectedSha256, string? ActualSha256, bool Matches);
    internal sealed record KernelInventory(string Id, string EntryPoint, string Symbol, IReadOnlyList<SourceInventory> Sources, IReadOnlyDictionary<string, IReadOnlyList<string>> UnresolvedTargets);
    internal sealed record CoverageInventory(string Vocabulary, int Registered, int Priced, IReadOnlyList<string> Unmodeled);
    internal sealed record EvidenceInventory(string Schema, string ManifestDigest, string Sdk, string RollForward, IReadOnlyList<KernelInventory> Kernels, IReadOnlyList<CoverageInventory> Coverage, IReadOnlyDictionary<string, IReadOnlyList<string>> MemoryGaps);

    /// <summary>Creates the <c>state-evidence</c> verb, whose tool runs are bounded on <paramref name="clock"/>.</summary>
    /// <param name="clock">The CLI host's clock.</param>
    /// <returns>The verb.</returns>
    public static Command Create(TimeProvider clock) {
        var capture = new Option<string?>("--capture") {
            Description = "Capture the reference evidence into this directory: compile both pinned Native AOT lowerings, walk each declared kernel's maximum permitted path, and render the manifest sections that walk establishes.",
        };
        var ilc = new Option<string?>("--ilc") {
            Description = "ILCompiler executable. Default: the manifest's pinned package under the NuGet package root.",
        };
        var llvmMca = new Option<string?>("--llvm-mca") {
            Description = "llvm-mca executable. Default: PATH, then C:/Program Files/LLVM/bin/llvm-mca.exe on Windows.",
        };
        var llvmObjdump = new Option<string?>("--llvm-objdump") {
            Description = "llvm-objdump executable. Default: PATH, then C:/Program Files/LLVM/bin/llvm-objdump.exe on Windows.",
        };
        var nuGetPackages = new Option<string?>("--nuget-packages") {
            Description = "NuGet package root the pinned ILCompiler and runtime packs are read from. Default: NUGET_PACKAGES, then the user profile's package root.",
        };
        var reuseLowering = new Option<bool>("--reuse-lowering") {
            Description = "Walk an object already present in the capture directory instead of recompiling it. The capture names every object it reuses.",
        };
        var inventory = new Option<bool>("--inventory") {
            Description = "Emit a machine-readable inventory of kernel source integrity and unresolved target/memory evidence instead of running llvm-mca.",
        };
        var output = new Option<string?>("--output") {
            Description = "Write --inventory JSON to this path instead of standard output.",
        };
        var command = new Command(
            description: "Replay the reference schedule's instruction forms against every pinned llvm-mca target, or capture the evidence afresh.",
            name: "state-evidence"
        ) { capture, ilc, llvmMca, llvmObjdump, nuGetPackages, reuseLowering, inventory, output };

        command.SetAction(action: (parse, cancellationToken) => {
            if (parse.GetValue(option: inventory)) { return WriteInventoryAsync(parse.GetValue(option: output), cancellationToken); }
            if (parse.GetValue(option: capture) is not { } directory) { return RunAsync(cancellationToken: cancellationToken, clock: clock, llvmMca: parse.GetValue(option: llvmMca)); }

            var root = RepositoryRoot;

            if (root is null) {
                Console.Error.WriteLine(value: "state-evidence: --capture must run inside the Puck checkout.");
                return Task.FromResult(result: 2);
            }
            return ReferenceCapture.RunAsync(
                clock: clock,
                cancellationToken: cancellationToken,
                directory: Path.GetFullPath(path: directory),
                options: new(
                    Ilc: parse.GetValue(option: ilc),
                    LlvmMca: parse.GetValue(option: llvmMca),
                    LlvmObjdump: parse.GetValue(option: llvmObjdump),
                    NuGetPackages: parse.GetValue(option: nuGetPackages),
                    ReuseLowering: parse.GetValue(option: reuseLowering)
                ),
                repositoryRoot: root
            );
        });
        return command;
    }

    private static async Task<int> WriteInventoryAsync(string? output, CancellationToken cancellationToken) {
        var root = RepositoryRoot;

        if (root is null) {
            Console.Error.WriteLine(value: "state-evidence: --inventory must run inside the Puck checkout.");
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
        var coverage = ReferenceSchedule.Coverage.Select(selector: static entry => new CoverageInventory(
            entry.Vocabulary,
            entry.Registered,
            entry.Priced,
            entry.Unmodeled
        )).ToArray();

        return new EvidenceInventory(
            ReferenceScheduleManifest.Schema,
            ReferenceScheduleManifest.Digest,
            target.Build.Sdk,
            target.Build.GlobalJsonRollForward,
            kernels,
            coverage,
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
            actual = ContentPin.OfFile(path: path).Hex;
        }
        return new SourceInventory(source.Path, source.Sha256, actual, string.Equals(a: source.Sha256, b: actual, comparisonType: StringComparison.Ordinal));
    }
    private static long Ceiling(decimal value) => decimal.ToInt64(d: decimal.Ceiling(d: value));
    private static async Task<int> RunAsync(string? llvmMca, TimeProvider clock, CancellationToken cancellationToken) {
        var executable = ReferenceTools.Find(
            name: "llvm-mca",
            requested: llvmMca
        );

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
        var versionRun = await ReferenceTools.RunAsync(
            arguments: ["--version"],
            cancellationToken: cancellationToken,
            clock: clock,
            executable: executable,
            standardInput: null,
            timeout: Timeout
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

            var run = await ReferenceTools.RunAsync(
                clock: clock,
                cancellationToken: cancellationToken,
                executable: executable,
                arguments: [
                    $"-mtriple={target.Analysis.Triple}",
                    $"-mcpu={target.Analysis.CpuModel}",
                    "--instruction-info",
                    "--iterations=1",
                ],
                standardInput: source,
                timeout: Timeout
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
        foreach (var entry in ReferenceSchedule.Coverage) {
            Console.WriteLine(value: $"state-evidence: {entry.Vocabulary}: {entry.Priced}/{entry.Registered} operation(s) priced, {entry.Unmodeled.Count} unmodeled.");
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
}

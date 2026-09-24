using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli.Bench;

/// <summary>Where the capture reads its tools from. Every member has a documented default; nothing here falls back
/// to a different tool or a different version when the pinned one is absent.</summary>
/// <param name="Ilc">The ILCompiler executable. Default: the pinned package under the NuGet package root.</param>
/// <param name="LlvmObjdump">The disassembler. Default: PATH, then the Windows LLVM installation.</param>
/// <param name="LlvmMca">The scheduling-model tool. Default: PATH, then the Windows LLVM installation.</param>
/// <param name="NuGetPackages">The NuGet package root the pinned compiler and runtime packs are read from.
/// Default: <c>NUGET_PACKAGES</c>, then the user profile's package root.</param>
/// <param name="ReuseLowering">Whether an object already present in the capture directory is read instead of being
/// recompiled. The capture says which object it reused.</param>
internal sealed record ReferenceToolOptions(string? Ilc, string? LlvmObjdump, string? LlvmMca, string? NuGetPackages, bool ReuseLowering);
/// <summary>One of the pinned reference lowerings: the Native AOT object every target of one ISA family is priced
/// from, and the targets that price it.</summary>
/// <param name="Key">The lowering's short name, which also names its object and response file.</param>
/// <param name="Architecture">The architecture the disassembly is decoded as.</param>
/// <param name="Family">The ISA family the family-balanced aggregation groups its targets under.</param>
/// <param name="RuntimeIdentifier">The runtime identifier the object is compiled for.</param>
/// <param name="InstructionSet">The explicit instruction-set target.</param>
/// <param name="Triple">The target triple the tools are driven with.</param>
/// <param name="ScalarIntegerAddForm">The instruction form every price of this family normalizes to.</param>
/// <param name="Targets">The pinned targets, one per scheduling model.</param>
internal sealed record ReferenceLoweringPlan(
    string Key,
    ReferenceArchitecture Architecture,
    string Family,
    string RuntimeIdentifier,
    string InstructionSet,
    string Triple,
    string ScalarIntegerAddForm,
    IReadOnlyList<ReferenceEvidenceTarget> Targets
);
/// <summary>Captures the reference schedule's evidence: it compiles the two pinned Native AOT lowerings, walks each
/// declared kernel's maximum permitted path through them, and renders the manifest sections that walk establishes.
/// </summary>
/// <remarks>The kernel declarations — entry point, symbol, formula, declared loop bounds, included overhead and
/// input domain — are reviewed and pinned in the committed manifest; the capture regenerates only the evidence they
/// are read against, and reports every place the regenerated evidence differs from what is pinned.</remarks>
internal static class ReferenceCapture {
    private const string CompilerPackagePrefix = "runtime.";
    private const string CompilerPackageSuffix = ".microsoft.dotnet.ilcompiler";
    private const int FormBatch = 150;
    private const string ManifestPath = "src/Puck.State/ReferenceSchedule.json";
    private const string NoWarn = "--nowarn:IL2104;IL3053;IL2026;IL2050;IL2055;IL2057;IL2059;IL2060;IL2062;IL2063;IL2064;IL2065;IL2066;IL2067;IL2068;IL2069;IL2070;IL2072;IL2075;IL2077;IL2080;IL2087;IL2090;IL2091;IL3050;IL3051;IL3054;IL2110;IL2111;IL2112;IL2113;IL2114;IL2116;IL2118;IL2119;IL2120;IL2121;IL2122";
    // The State project every other State project sits beneath, so its output carries the whole family.
    private const string StateProject = "src/Puck.State.Search/Puck.State.Search.csproj";
    private const string StateOutput = "src/Puck.State.Search/bin/Release/net10.0";
    private const string Tab = "\t";

    // The assemblies the reference lowering roots. Rooting an assembly compiles every method it declares, which is
    // what puts a kernel's entry point and its helpers in the object; the manifest's compilation line records it.
    private static readonly string[] Roots = ["Puck.State", "Puck.State.Generators", "Puck.State.Rules", "Puck.State.Search", "Puck.State.Topology", "Puck.State.Vectors"];

    /// <summary>Captures the reference evidence into one directory and compares it with the committed manifest.
    /// Returns zero when every declared kernel's evidence is reproduced, one when any of it differs, and two when a
    /// pinned tool or version is absent.</summary>
    /// <param name="repositoryRoot">The checkout the reference code is compiled from.</param>
    /// <param name="directory">Where the objects, the scheduling-model answers and the rendered manifest are written.</param>
    /// <param name="options">Where the capture reads its tools from.</param>
    /// <param name="clock">The clock every tool run's timeout runs on.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    public static async Task<int> RunAsync(
        string repositoryRoot,
        string directory,
        ReferenceToolOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        if (!TryPlan(
            plans: out var plans,
            refusal: out var planRefusal
        )) {
            Console.Error.WriteLine(value: $"state-evidence: {planRefusal}");
            return 2;
        }

        var sdk = ReferenceScheduleManifest.Targets[0].Build;

        if (VerifyCheckout(
            build: sdk,
            repositoryRoot: repositoryRoot
        ) is { } checkoutRefusal) {
            Console.Error.WriteLine(value: $"state-evidence: {checkoutRefusal}");
            return 2;
        }

        var packages = ResolvePackageRoot(requested: options.NuGetPackages);

        if (!TryResolveCompiler(
            build: sdk,
            executable: out var ilc,
            packages: packages,
            refusal: out var compilerRefusal,
            requested: options.Ilc
        )) {
            Console.Error.WriteLine(value: $"state-evidence: {compilerRefusal}");
            return 2;
        }

        var objdump = await ResolveLlvmAsync(
            clock: clock,
            cancellationToken: cancellationToken,
            name: "llvm-objdump",
            pinned: ReferenceScheduleManifest.Targets[0].Analysis.Disassembler,
            requested: options.LlvmObjdump
        ).ConfigureAwait(continueOnCapturedContext: false);
        var mca = await ResolveLlvmAsync(
            clock: clock,
            cancellationToken: cancellationToken,
            name: "llvm-mca",
            pinned: ReferenceScheduleManifest.Targets[0].Analysis.Scheduler,
            requested: options.LlvmMca
        ).ConfigureAwait(continueOnCapturedContext: false);

        if ((objdump.Refusal ?? mca.Refusal) is { } toolRefusal) {
            Console.Error.WriteLine(value: $"state-evidence: {toolRefusal}");
            return 2;
        }

        Directory.CreateDirectory(path: directory);

        if (!options.ReuseLowering && (await BuildStateAsync(
            cancellationToken: cancellationToken,
            clock: clock,
            repositoryRoot: repositoryRoot
        ).ConfigureAwait(continueOnCapturedContext: false) is { } buildRefusal)) {
            Console.Error.WriteLine(value: $"state-evidence: {buildRefusal}");
            return 2;
        }

        var evidence = new Dictionary<string, LoweringEvidence>(comparer: StringComparer.Ordinal);

        foreach (var plan in plans) {
            var objectPath = Path.Combine(
                path1: directory,
                path2: $"Puck.State.{plan.Key}.obj"
            );

            if (options.ReuseLowering && File.Exists(path: objectPath)) {
                Console.WriteLine(value: $"state-evidence: {plan.Key}: reusing the lowering already at {objectPath.Replace(newChar: '/', oldChar: '\\')}.");
            } else if (await CompileAsync(
                cancellationToken: cancellationToken,
                clock: clock,
                directory: directory,
                ilc: ilc!,
                objectPath: objectPath,
                packages: packages,
                plan: plan,
                repositoryRoot: repositoryRoot
            ).ConfigureAwait(continueOnCapturedContext: false) is { } compileRefusal) {
                Console.Error.WriteLine(value: $"state-evidence: {compileRefusal}");
                return 2;
            }
            evidence.Add(
                key: plan.Family,
                value: await MeasureAsync(
                    cancellationToken: cancellationToken,
                    clock: clock,
                    directory: directory,
                    mca: mca.Executable!,
                    objdump: objdump.Executable!,
                    objectPath: objectPath,
                    plan: plan
                ).ConfigureAwait(continueOnCapturedContext: false)
            );
        }

        var manifest = Render(
            evidence: evidence,
            plans: plans,
            repositoryRoot: repositoryRoot
        );
        var rendered = Path.Combine(
            path1: directory,
            path2: "ReferenceSchedule.json"
        );

        await File.WriteAllTextAsync(
            cancellationToken: cancellationToken,
            contents: ReferenceJson.Render(node: manifest),
            path: rendered
        ).ConfigureAwait(continueOnCapturedContext: false);

        var differences = Compare(
            captured: manifest,
            repositoryRoot: repositoryRoot
        );

        foreach (var difference in differences) { Console.Error.WriteLine(value: $"state-evidence: {difference}"); }
        Console.WriteLine(value: $"state-evidence: rendered the captured manifest to {rendered.Replace(newChar: '/', oldChar: '\\')}.");
        Console.WriteLine(value: ((differences.Count == 0)
            ? "state-evidence: the capture reproduces every declared kernel's pinned evidence."
            : $"state-evidence: the capture differs from the pinned manifest in {differences.Count} place(s)."));
        foreach (var entry in ReferenceSchedule.Coverage) {
            Console.WriteLine(value: $"state-evidence: {entry.Vocabulary}: {entry.Priced}/{entry.Registered} operation(s) priced, {entry.Unmodeled.Count} unmodeled.");
        }
        return ((differences.Count == 0) ? 0 : 1);
    }

    private sealed record KernelEvidence(string Model, ReferencePrice Price, IReadOnlyList<string> Trace, IReadOnlyDictionary<string, string> Simulation, IReadOnlyList<string> ResourcePressure);
    private sealed record LoweringEvidence(
        ReferenceLoweringPlan Plan,
        IReadOnlyDictionary<string, ReferenceMeasurement> ScalarIntegerAdd,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReferenceMeasurement>> Service,
        IReadOnlyDictionary<string, IReadOnlyList<KernelEvidence>> Kernels
    );

    private static async Task<string?> BuildStateAsync(string repositoryRoot, TimeProvider clock, CancellationToken cancellationToken) {
        var run = await ReferenceTools.RunAsync(
            arguments: [
                "build",
                Path.Combine(
                    path1: repositoryRoot,
                    path2: StateProject
                ),
                "-c",
                "Release",
                "--nologo",
            ],
            clock: clock,
            cancellationToken: cancellationToken,
            executable: "dotnet",
            standardInput: null,
            timeout: TimeSpan.FromMinutes(value: 20D)
        ).ConfigureAwait(continueOnCapturedContext: false);

        return (run.Success
            ? null
            : $"the reference build of {StateProject} failed: {run.Error.Trim()}{run.Output.Split(separator: '\n').LastOrDefault(predicate: static line => (line.Trim().Length > 0))}");
    }
    private static async Task<string?> CompileAsync(
        ReferenceLoweringPlan plan,
        string repositoryRoot,
        string directory,
        string objectPath,
        string ilc,
        string packages,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        var output = Path.GetFullPath(path: Path.Combine(
            path1: repositoryRoot,
            path2: StateOutput
        ));
        var assemblies = Roots.Select(selector: root => Path.Combine(
            path1: output,
            path2: $"{root}.dll"
        )).ToArray();

        if (assemblies.FirstOrDefault(predicate: static path => !File.Exists(path: path)) is { } absent) { return $"the reference build produced no {StateOutput}/{Path.GetFileName(path: absent)}."; }

        var pack = RuntimePackDirectory(
            build: plan.Targets[0].Build,
            packages: packages,
            runtimeIdentifier: plan.RuntimeIdentifier
        );
        var runtime = Path.Combine(
            path1: pack,
            path2: "lib/net10.0"
        );
        // The framework's own managed assemblies are split across the pack: the reference set sits under lib, while
        // the core library, the type loader, the reflection executor and the stack-trace metadata sit beside the
        // native runtime. The compiler needs both, and everything else under native is native code.
        var core = Path.Combine(
            path1: pack,
            path2: "native"
        );

        if (!Directory.Exists(path: runtime) || !Directory.Exists(path: core)) { return $"the pinned runtime pack '{plan.Targets[0].Build.RuntimePack}' is not installed at {pack}; install it or pass --nuget-packages."; }

        var lines = new List<string>(collection: assemblies) { $"-o:{objectPath}", "--targetos:win", $"--targetarch:{plan.Key}", "-O", "--methodbodyfolding:none", $"--instruction-set:{plan.InstructionSet}", "--nativelib" };

        foreach (var root in Roots) { lines.Add(item: $"--root:{root}"); }
        lines.Add(item: "--noscan");
        lines.Add(item: NoWarn);
        foreach (var reference in Directory.EnumerateFiles(
            path: output,
            searchPattern: "Puck.*.dll"
        ).Order(comparer: StringComparer.Ordinal)) {
            if (Roots.Contains(
                comparer: StringComparer.Ordinal,
                value: Path.GetFileNameWithoutExtension(path: reference)
            )) {
                continue;
            }
            lines.Add(item: $"-r:{reference}");
        }
        foreach (var reference in Directory.EnumerateFiles(
            path: runtime,
            searchPattern: "*.dll"
        ).Order(comparer: StringComparer.Ordinal)) {
            lines.Add(item: $"-r:{reference}");
        }
        foreach (var reference in Directory.EnumerateFiles(
            path: core,
            searchPattern: "System.Private.*.dll"
        ).Order(comparer: StringComparer.Ordinal)) {
            lines.Add(item: $"-r:{reference}");
        }

        var responsePath = Path.Combine(
            path1: directory,
            path2: $"Puck.State.{plan.Key}.rsp"
        );

        await File.WriteAllLinesAsync(
            cancellationToken: cancellationToken,
            contents: lines,
            path: responsePath
        ).ConfigureAwait(continueOnCapturedContext: false);

        var run = await ReferenceTools.RunAsync(
            arguments: [$"@{responsePath}"],
            clock: clock,
            cancellationToken: cancellationToken,
            executable: ilc,
            standardInput: null,
            timeout: TimeSpan.FromMinutes(value: 30D)
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!run.Success) { return $"{plan.Key}: the pinned compiler refused the reference lowering: {run.Error.Trim()}"; }
        Console.WriteLine(value: $"state-evidence: {plan.Key}: compiled the reference lowering for {plan.RuntimeIdentifier} at {plan.InstructionSet}.");
        return null;
    }
    private static IReadOnlyList<string> Compare(JsonNode captured, string repositoryRoot) {
        var differences = new List<string>();
        var pinned = JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: repositoryRoot,
            path2: ManifestPath
        )))!;

        Compare(
            differences: differences,
            left: pinned["targets"],
            path: "targets",
            right: captured["targets"]
        );
        Compare(
            differences: differences,
            left: pinned["instructionService"]!["forms"],
            path: "instructionService.forms",
            right: captured["instructionService"]!["forms"]
        );

        var pinnedKernels = ((JsonArray)pinned["kernels"]!).ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static kernel => kernel,
            keySelector: static kernel => ((string)kernel!["id"]!)
        );

        foreach (var kernel in ((JsonArray)captured["kernels"]!)) {
            var id = ((string)kernel!["id"]!);

            if (!pinnedKernels.TryGetValue(
                key: id,
                value: out var previous
            )) {
                differences.Add(item: $"kernels.{id}: the capture declares a kernel the pinned manifest does not.");
                continue;
            }
            foreach (var section in new[] { "sources", "targets", "representativeService" }) {
                Compare(
                    differences: differences,
                    left: previous![section],
                    path: $"kernels.{id}.{section}",
                    right: kernel[section]
                );
            }
        }
        return differences;
    }
    private static void Compare(List<string> differences, string path, JsonNode? left, JsonNode? right) {
        if (JsonNode.DeepEquals(
            node1: left,
            node2: right
        )) {
            return;
        }
        if ((left is JsonObject leftMembers) && (right is JsonObject rightMembers)) {
            foreach (var name in leftMembers.Select(selector: static member => member.Key).Union(second: rightMembers.Select(selector: static member => member.Key)).Order(comparer: StringComparer.Ordinal)) {
                Compare(
                    differences: differences,
                    left: leftMembers[propertyName: name],
                    path: $"{path}.{name}",
                    right: rightMembers[propertyName: name]
                );
            }
            return;
        }
        if ((left is JsonArray leftEntries) && (right is JsonArray rightEntries) && (leftEntries.Count == rightEntries.Count)) {
            for (var index = 0; (index < leftEntries.Count); ++index) {
                Compare(
                    differences: differences,
                    left: leftEntries[index],
                    path: $"{path}[{index}]",
                    right: rightEntries[index]
                );
            }
            return;
        }
        differences.Add(item: $"{path}: pinned {Summarize(node: left)}, captured {Summarize(node: right)}.");
    }
    private static async Task<LoweringEvidence> MeasureAsync(
        ReferenceLoweringPlan plan,
        string objectPath,
        string objdump,
        string mca,
        string directory,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        var disassembly = new Dictionary<string, IReadOnlyList<ReferenceInstruction>>(comparer: StringComparer.Ordinal);
        var walker = new ReferenceWalker(
            architecture: plan.Architecture,
            disassemble: symbol => {
                if (disassembly.TryGetValue(
                    key: symbol,
                    value: out var known
                )) {
                    return known;
                }

                var run = ReferenceTools.RunAsync(
                    arguments: ["-d", "-r", "--no-show-raw-insn", $"--disassemble-symbols={symbol}", objectPath],
                    clock: clock,
                    cancellationToken: cancellationToken,
                    executable: objdump,
                    standardInput: null,
                    timeout: TimeSpan.FromMinutes(value: 2D)
                ).GetAwaiter().GetResult();
                var rows = (run.Success
                    ? ReferenceLowering.Decode(
                        architecture: plan.Architecture,
                        disassembly: run.Output
                    )
                    : []);

                disassembly.Add(
                    key: symbol,
                    value: rows
                );
                return rows;
            }
        );

        foreach (var kernel in ReferenceScheduleManifest.Kernels) { _ = walker.Graph(symbol: kernel.Symbol); }

        var forms = new SortedSet<string>(comparer: StringComparer.Ordinal) { plan.ScalarIntegerAddForm };

        foreach (var kernel in ReferenceScheduleManifest.Kernels) { forms.UnionWith(other: walker.Forms(symbol: kernel.Symbol)); }
        Console.WriteLine(value: $"state-evidence: {plan.Key}: read {disassembly.Count(predicate: static entry => (entry.Value.Count > 0))} symbol bodies carrying {forms.Count} instruction forms.");

        var add = new Dictionary<string, ReferenceMeasurement>(comparer: StringComparer.Ordinal);
        var kernels = new Dictionary<string, List<KernelEvidence>>(comparer: StringComparer.Ordinal);
        var service = new Dictionary<string, IReadOnlyDictionary<string, ReferenceMeasurement>>(comparer: StringComparer.Ordinal);

        foreach (var target in plan.Targets) {
            var model = target.Analysis.CpuModel;
            var measured = await ServiceAsync(
                clock: clock,
                cancellationToken: cancellationToken,
                directory: directory,
                forms: forms,
                mca: mca,
                model: model,
                triple: plan.Triple
            ).ConfigureAwait(continueOnCapturedContext: false);

            service.Add(
                key: target.Id,
                value: measured
            );
            if (measured.TryGetValue(
                key: plan.ScalarIntegerAddForm,
                value: out var baseline
            )) {
                add.Add(
                    key: target.Id,
                    value: baseline
                );
            }
            foreach (var kernel in ReferenceScheduleManifest.Kernels) {
                var loopBounds = kernel.LoopBounds.ToDictionary(
                    comparer: StringComparer.Ordinal,
                    elementSelector: static loop => loop.Iterations,
                    keySelector: static loop => loop.Symbol
                );
                var price = walker.Price(
                    loopBounds: loopBounds,
                    service: measured,
                    symbol: kernel.Symbol
                );
                var trace = walker.Trace(symbol: kernel.Symbol);
                var simulation = (((price.Issues.Count == 0) && (trace.Count > 0))
                    ? await SimulateAsync(
                        clock: clock,
                        cancellationToken: cancellationToken,
                        mca: mca,
                        model: model,
                        trace: trace,
                        triple: plan.Triple
                    ).ConfigureAwait(continueOnCapturedContext: false)
                    : (new Dictionary<string, string>(comparer: StringComparer.Ordinal), ((IReadOnlyList<string>)[])));

                if (!kernels.TryGetValue(
                    key: kernel.Id,
                    value: out var rows
                )) {
                    kernels[kernel.Id] = rows = [];
                }
                rows.Add(item: new(
                    Model: target.Id,
                    Price: price,
                    ResourcePressure: simulation.Item2,
                    Simulation: simulation.Item1,
                    Trace: trace
                ));
                Console.WriteLine(value: $"state-evidence: {target.Id}: {kernel.Id}: {((price.Issues.Count == 0) ? $"{price.Cycles} cycles over {trace.Count} instruction(s)" : $"unresolved, {price.Issues.Count} reason(s)")}.");
                // The manifest keeps the first few reasons per target; the run names every one, since each is a
                // bound or a body someone has to supply before the kernel has a price.
                foreach (var issue in price.Issues) { Console.WriteLine(value: $"state-evidence:   {issue}"); }
            }
        }
        return new(
            Kernels: kernels.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static entry => ((IReadOnlyList<KernelEvidence>)entry.Value),
                keySelector: static entry => entry.Key
            ),
            Plan: plan,
            ScalarIntegerAdd: add,
            Service: service
        );
    }
    private static string ResolvePackageRoot(string? requested) {
        if (!string.IsNullOrWhiteSpace(value: requested)) { return Path.GetFullPath(path: requested); }

        var configured = Environment.GetEnvironmentVariable(variable: "NUGET_PACKAGES");

        return (!string.IsNullOrWhiteSpace(value: configured)
            ? Path.GetFullPath(path: configured)
            : Path.Combine(
                path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.UserProfile),
                path2: ".nuget/packages"
            ));
    }
    private static async Task<(string? Executable, string? Refusal)> ResolveLlvmAsync(string name, string pinned, string? requested, TimeProvider clock, CancellationToken cancellationToken) {
        var expected = pinned.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ' '
        )[^1];
        var executable = ReferenceTools.Find(
            name: name,
            requested: requested
        );

        if (executable is null) { return (null, $"the pinned {pinned} was not found on PATH or in the LLVM installation; pass --{name}."); }

        var run = await ReferenceTools.RunAsync(
            arguments: ["--version"],
            clock: clock,
            cancellationToken: cancellationToken,
            executable: executable,
            standardInput: null,
            timeout: TimeSpan.FromSeconds(value: 30D)
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!run.Success) { return (null, $"could not read {executable}'s version: {run.Error.Trim()}"); }
        if (!StateEvidenceCommand.TryReadLlvmVersion(
            output: run.Output,
            version: out var actual
        ) || !string.Equals(
            a: expected,
            b: actual,
            comparisonType: StringComparison.Ordinal
        )) {
            return (null, $"expected {pinned}, but {executable} reported version '{actual}'.");
        }
        return (executable, null);
    }
    private static string RuntimePackDirectory(ReferenceBuild build, string packages, string runtimeIdentifier) {
        var parts = build.RuntimePack.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ' '
        );

        return Path.GetFullPath(path: Path.Combine(
            path1: packages,
            path2: parts[0].ToLowerInvariant(),
            path3: parts[^1],
            path4: $"runtimes/{runtimeIdentifier}"
        ));
    }
    private static async Task<IReadOnlyDictionary<string, ReferenceMeasurement>> ServiceAsync(
        string mca,
        string triple,
        string model,
        IReadOnlySet<string> forms,
        string directory,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        var cachePath = Path.Combine(
            path1: directory,
            path2: $"mca.{model}.json"
        );
        var served = new Dictionary<string, ReferenceMeasurement>(comparer: StringComparer.Ordinal);

        if (File.Exists(path: cachePath)) {
            foreach (var row in JsonNode.Parse(json: await File.ReadAllTextAsync(
                cancellationToken: cancellationToken,
                path: cachePath
            ).ConfigureAwait(continueOnCapturedContext: false))!.AsObject()) {
                if (!forms.Contains(item: row.Key)) { continue; }
                served.Add(
                    key: row.Key,
                    value: new(
                        Latency: ((long)row.Value![0]!),
                        ReciprocalThroughput: ((decimal)row.Value[1]!)
                    )
                );
            }
        }

        var pending = forms.Where(predicate: form => !served.ContainsKey(key: form)).ToArray();

        for (var start = 0; (start < pending.Length); start += FormBatch) {
            var batch = pending[start..Math.Min(
                val1: (start + FormBatch),
                val2: pending.Length
            )];
            var rows = await MeasureAsync(
                batch: batch,
                cancellationToken: cancellationToken,
                clock: clock,
                mca: mca,
                model: model,
                triple: triple
            ).ConfigureAwait(continueOnCapturedContext: false);

            if (rows is not null) {
                foreach (var row in rows) {
                    served.Add(
                        key: row.Key,
                        value: row.Value
                    );
                }
                continue;
            }
            // A batch the model refuses is re-asked one form at a time, so one unserved form does not take the
            // published service of the hundred and forty-nine beside it with it.
            foreach (var form in batch) {
                if (await MeasureAsync(
                    batch: [form],
                    cancellationToken: cancellationToken,
                    clock: clock,
                    mca: mca,
                    model: model,
                    triple: triple
                ).ConfigureAwait(continueOnCapturedContext: false) is not { } single) {
                    continue;
                }
                foreach (var row in single) {
                    served.Add(
                        key: row.Key,
                        value: row.Value
                    );
                }
            }
        }

        var cache = new JsonObject();

        foreach (var row in served.OrderBy(keySelector: static entry => entry.Key, comparer: StringComparer.Ordinal)) {
            cache.Add(
                propertyName: row.Key,
                value: new JsonArray(row.Value.Latency, row.Value.ReciprocalThroughput)
            );
        }
        await File.WriteAllTextAsync(
            cancellationToken: cancellationToken,
            contents: cache.ToJsonString(),
            path: cachePath
        ).ConfigureAwait(continueOnCapturedContext: false);
        Console.WriteLine(value: $"state-evidence: {model}: {served.Count}/{forms.Count} instruction form(s) served.");
        return served;
    }
    private static async Task<IReadOnlyDictionary<string, ReferenceMeasurement>?> MeasureAsync(
        string mca,
        string triple,
        string model,
        IReadOnlyList<string> batch,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        var source = new StringBuilder();

        foreach (var form in batch) { source.Append(value: Tab).Append(value: form).Append(value: '\n'); }

        var run = await ReferenceTools.RunAsync(
            arguments: [$"-mtriple={triple}", $"-mcpu={model}", "-instruction-info", "-iterations=1", "-"],
            clock: clock,
            cancellationToken: cancellationToken,
            executable: mca,
            standardInput: source.ToString(),
            timeout: TimeSpan.FromMinutes(value: 2D)
        ).ConfigureAwait(continueOnCapturedContext: false);

        if (!run.Success) { return null; }

        var rows = StateEvidenceCommand.ParseRows(output: run.Output);

        if (rows.Count != batch.Count) { return null; }

        var measured = new Dictionary<string, ReferenceMeasurement>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < batch.Count); ++index) {
            measured[batch[index]] = new(
                Latency: rows[index].Latency,
                ReciprocalThroughput: rows[index].ReciprocalThroughput
            );
        }
        return measured;
    }
    private static async Task<(IReadOnlyDictionary<string, string> Summary, IReadOnlyList<string> ResourcePressure)> SimulateAsync(
        string mca,
        string triple,
        string model,
        IReadOnlyList<string> trace,
        TimeProvider clock,
        CancellationToken cancellationToken
    ) {
        var source = new StringBuilder();

        foreach (var form in trace) { source.Append(value: Tab).Append(value: form).Append(value: '\n'); }

        var run = await ReferenceTools.RunAsync(
            arguments: [$"-mtriple={triple}", $"-mcpu={model}", "-iterations=1", "-bottleneck-analysis", "-"],
            clock: clock,
            cancellationToken: cancellationToken,
            executable: mca,
            standardInput: source.ToString(),
            timeout: TimeSpan.FromMinutes(value: 2D)
        ).ConfigureAwait(continueOnCapturedContext: false);
        var summary = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var pressure = new List<string>();

        if (!run.Success) { return (summary, pressure); }

        var inside = false;

        foreach (var line in run.Output.Split(separator: '\n').Select(selector: static line => line.TrimEnd(trimChar: '\r'))) {
            foreach (var key in new[] { "Total Cycles:", "Total uOps:", "Dispatch Width:", "uOps Per Cycle:", "IPC:", "Block RThroughput:" }) {
                if (line.StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: key
                )) {
                    summary[key[..^1]] = line[key.Length..].Trim();
                }
            }
            if (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "Resource pressure per iteration:"
            )) {
                inside = true;
                continue;
            }
            if (!inside) { continue; }
            if (line.Trim().Length == 0) { break; }
            pressure.Add(item: line.TrimEnd());
        }
        return (summary, pressure);
    }
    private static string Summarize(JsonNode? node) => (node switch {
        null => "nothing",
        JsonArray entries => $"{entries.Count} entry/entries",
        JsonObject members => $"an object of {members.Count} member(s)",
        _ => node.ToJsonString(),
    });
    private static bool TryPlan(out IReadOnlyList<ReferenceLoweringPlan> plans, out string? refusal) {
        var rows = new List<ReferenceLoweringPlan>();

        foreach (var family in ReferenceScheduleManifest.Targets.GroupBy(keySelector: static target => target.Family, comparer: StringComparer.Ordinal)) {
            var targets = family.ToArray();
            var first = targets[0];
            var key = first.Id.Split(separator: '.')[0];

            foreach (var target in targets) {
                if (!string.Equals(
                    a: key,
                    b: target.Id.Split(separator: '.')[0],
                    comparisonType: StringComparison.Ordinal
                ) || (target.Build != first.Build) || !string.Equals(
                    a: first.Analysis.Triple,
                    b: target.Analysis.Triple,
                    comparisonType: StringComparison.Ordinal
                ) || !string.Equals(
                    a: first.ScalarIntegerAddForm,
                    b: target.ScalarIntegerAddForm,
                    comparisonType: StringComparison.Ordinal
                )) {
                    plans = [];
                    refusal = $"the pinned targets of family '{family.Key}' do not agree on one reference build, triple and normalization form, so the family has no single lowering to capture.";
                    return false;
                }
            }
            rows.Add(item: new(
                Architecture: (first.Build.RuntimeIdentifier.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "-arm64"
                )
                    ? ReferenceArchitecture.Arm64
                    : ReferenceArchitecture.X64),
                Family: family.Key,
                InstructionSet: first.Build.InstructionSet,
                Key: key,
                RuntimeIdentifier: first.Build.RuntimeIdentifier,
                ScalarIntegerAddForm: first.ScalarIntegerAddForm,
                Targets: targets,
                Triple: first.Analysis.Triple
            ));
        }
        plans = rows;
        refusal = null;
        return true;
    }
    private static bool TryResolveCompiler(
        ReferenceBuild build,
        string packages,
        string? requested,
        out string? executable,
        out string? refusal
    ) {
        var parts = build.IlCompiler.Split(
            options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries,
            separator: ' '
        );
        var host = parts[^2].TrimStart(trimChar: '(');
        var version = parts[1];
        var expected = Path.Combine(
            path1: packages,
            path2: ((CompilerPackagePrefix + host) + CompilerPackageSuffix),
            path3: version,
            path4: (OperatingSystem.IsWindows() ? "tools/ilc.exe" : "tools/ilc")
        );

        executable = (string.IsNullOrWhiteSpace(value: requested)
            ? expected
            : Path.GetFullPath(path: requested));
        if (File.Exists(path: executable)) {
            refusal = null;
            return true;
        }
        refusal = $"the pinned {build.IlCompiler} is not installed at {executable}; install it or pass --ilc.";
        executable = null;
        return false;
    }
    private static string? VerifyCheckout(ReferenceBuild build, string repositoryRoot) {
        var path = Path.Combine(
            path1: repositoryRoot,
            path2: "global.json"
        );

        if (!File.Exists(path: path)) { return $"the checkout at {repositoryRoot} carries no global.json, so the manifest's pinned SDK {build.Sdk} pins nothing."; }

        using var document = JsonDocument.Parse(json: File.ReadAllText(path: path));

        if (!document.RootElement.TryGetProperty(
            propertyName: "sdk",
            value: out var sdk
        )) {
            return $"the checkout's global.json declares no sdk section, so the manifest's pinned SDK {build.Sdk} pins nothing.";
        }

        var version = (sdk.TryGetProperty(
            propertyName: "version",
            value: out var declared
        )
            ? declared.GetString()
            : null);
        var rollForward = (sdk.TryGetProperty(
            propertyName: "rollForward",
            value: out var policy
        )
            ? policy.GetString()
            : null);

        if (!string.Equals(
            a: build.Sdk,
            b: version,
            comparisonType: StringComparison.Ordinal
        ) || !string.Equals(
            a: build.GlobalJsonRollForward,
            b: rollForward,
            comparisonType: StringComparison.Ordinal
        )) {
            return $"the manifest pins SDK {build.Sdk} with rollForward {build.GlobalJsonRollForward}, but the checkout's global.json declares {version} with rollForward {rollForward}.";
        }
        return null;
    }
    private static JsonNode Render(IReadOnlyList<ReferenceLoweringPlan> plans, IReadOnlyDictionary<string, LoweringEvidence> evidence, string repositoryRoot) {
        var manifest = JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: repositoryRoot,
            path2: ManifestPath
        )))!;
        var targets = new JsonArray();
        var forms = new JsonObject();

        foreach (var plan in plans) {
            var measured = evidence[plan.Family];

            foreach (var target in plan.Targets) {
                var pinned = ((JsonArray)manifest["targets"]!).First(predicate: row => string.Equals(
                    a: ((string)row!["id"]!),
                    b: target.Id,
                    comparisonType: StringComparison.Ordinal
                ))!.DeepClone();

                if (measured.ScalarIntegerAdd.TryGetValue(
                    key: target.Id,
                    value: out var add
                )) {
                    pinned["scalarIntegerAddService"] = new JsonObject {
                        ["form"] = plan.ScalarIntegerAddForm,
                        ["latency"] = add.Latency,
                        ["reciprocalThroughput"] = ReferenceJson.Rational(value: ReferenceRational.Of(value: add.ReciprocalThroughput)),
                        ["q"] = add.Service,
                    };
                }
                targets.Add(value: pinned);
            }
            forms.Add(
                propertyName: plan.Family,
                value: RenderForms(
                    evidence: measured,
                    plan: plan
                )
            );
        }
        manifest["targets"] = targets;
        manifest["instructionService"]!["forms"] = forms;

        var kernels = new JsonArray();

        foreach (var kernel in ReferenceScheduleManifest.Kernels) {
            var pinned = ((JsonArray)manifest["kernels"]!).First(predicate: row => string.Equals(
                a: ((string)row!["id"]!),
                b: kernel.Id,
                comparisonType: StringComparison.Ordinal
            ))!.DeepClone();
            var sources = new JsonArray();

            foreach (var source in kernel.Sources) {
                sources.Add(value: new JsonObject {
                    ["path"] = source.Path,
                    ["sha256"] = Convert.ToHexStringLower(inArray: SHA256.HashData(source: File.ReadAllBytes(path: Path.Combine(
                        path1: repositoryRoot,
                        path2: source.Path
                    )))),
                });
            }
            pinned["sources"] = sources;

            var prices = new JsonObject();
            var normalized = new Dictionary<string, List<ReferenceRational>>(comparer: StringComparer.Ordinal);
            var resolved = true;

            foreach (var plan in plans) {
                var measured = evidence[plan.Family];

                foreach (var row in measured.Kernels[kernel.Id]) {
                    if (row.Price.Issues.Count > 0) {
                        prices.Add(
                            propertyName: row.Model,
                            value: new JsonObject { ["unresolved"] = new JsonArray([.. row.Price.Issues.Take(count: 6).Select(selector: static reason => JsonValue.Create(value: reason))]) }
                        );
                        resolved = false;
                        continue;
                    }

                    var ratio = ReferenceRational.Of(
                        denominator: measured.ScalarIntegerAdd[row.Model].Service,
                        numerator: row.Price.Cycles
                    );

                    if (!normalized.TryGetValue(
                        key: plan.Family,
                        value: out var samples
                    )) {
                        normalized[plan.Family] = samples = [];
                    }
                    samples.Add(item: ratio);
                    prices.Add(
                        propertyName: row.Model,
                        value: new JsonObject {
                            ["cycles"] = row.Price.Cycles,
                            ["normalizedToScalarIntegerAdd"] = ReferenceJson.Rational(value: ratio),
                            ["maximumPathInstructions"] = row.Trace.Count,
                            ["mca"] = new JsonObject {
                                ["totalCycles"] = row.Simulation.GetValueOrDefault(key: "Total Cycles"),
                                ["totalMicroOperations"] = row.Simulation.GetValueOrDefault(key: "Total uOps"),
                                ["instructionsPerCycle"] = row.Simulation.GetValueOrDefault(key: "IPC"),
                                ["blockReciprocalThroughput"] = row.Simulation.GetValueOrDefault(key: "Block RThroughput"),
                                ["resourcePressure"] = new JsonArray([.. row.ResourcePressure.Take(count: 3).Select(selector: static line => JsonValue.Create(value: line))]),
                            },
                        }
                    );
                }
            }
            pinned["targets"] = prices;
            if (resolved && (normalized.Count == plans.Count)) {
                var medians = new JsonObject();
                var samples = new List<ReferenceRational>();

                foreach (var plan in plans) {
                    var median = ReferenceRational.Median(samples: normalized[plan.Family]);

                    medians.Add(
                        propertyName: plan.Family,
                        value: ReferenceJson.Rational(value: median)
                    );
                    samples.Add(item: median);
                }
                pinned["representativeService"] = new JsonObject {
                    ["familyMedians"] = medians,
                    ["cycles"] = ReferenceRational.Median(samples: samples).Ceiling,
                };
            } else {
                pinned["representativeService"] = new JsonObject { ["unmodeled"] = "at least one pinned evidence target left this kernel unresolved; see targets" };
            }
            kernels.Add(value: pinned);
        }
        manifest["kernels"] = kernels;
        return manifest;
    }
    // Every model of a family answers for every form any model's maximum path carries: a form one model serves and
    // another does not is a gap in the evidence, and it has to be visible rather than simply absent.
    private static JsonArray RenderForms(ReferenceLoweringPlan plan, LoweringEvidence evidence) {
        var union = new SortedSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rows in evidence.Kernels.Values) {
            foreach (var row in rows) {
                if (row.Price.Issues.Count > 0) { continue; }
                foreach (var form in row.Trace) {
                    if (evidence.Service[row.Model].ContainsKey(key: form)) { union.Add(item: form); }
                }
            }
        }

        var forms = new JsonArray();

        foreach (var form in union) {
            var service = new JsonObject();

            foreach (var target in plan.Targets) {
                if (!evidence.Service[target.Id].TryGetValue(
                    key: form,
                    value: out var measurement
                )) {
                    continue;
                }
                service.Add(
                    propertyName: target.Id,
                    value: new JsonObject {
                        ["latency"] = measurement.Latency,
                        ["reciprocalThroughput"] = ReferenceJson.Rational(value: ReferenceRational.Of(value: measurement.ReciprocalThroughput)),
                        ["q"] = measurement.Service,
                    }
                );
            }
            forms.Add(value: new JsonObject {
                ["form"] = form,
                ["service"] = service,
            });
        }
        return forms;
    }
}

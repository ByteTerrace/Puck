using System.Text.Json;
using Puck.Cli.Affected;
using Puck.Cli.Baselines;

namespace Puck.Cli.Gate;

internal enum GateStepKind { Build, CopyCli, Puck, DeviceSuite, Counters, Baseline, Canaries, Parity }
internal sealed record GateStep(string Name, GateStepKind Kind, string[] Arguments, bool Heavy = false, bool Gpu = false, bool Record = false, bool Sources = false);
/// <summary>The ordered batch qualification, shared by execution, help and the documentation laws.</summary>
internal static class GatePlan {
    // The test selection of every device suite: the test classes that carry the Gpu trait, which GPU001 holds every
    // class that opens a device to. The CPU runs take the complement (AffectedCommand.CpuSelection); nothing else spells
    // either selection. The solution build runs first, so each suite runs its built binaries.
    public static readonly string[] GpuSelection = ["--filter-trait", "Category=Gpu"];
    // The device-law suites, each run with GpuSelection.
    public static readonly IReadOnlyList<(string Suite, string[] Selection)> DeviceSuites = [
        ("Puck.World.Tests", GpuSelection),
        ("Puck.DirectX.Tests", GpuSelection),
        ("Puck.Vulkan.Tests", GpuSelection),
        ("Puck.Platform.Windows.Tests", GpuSelection),
    ];
    public static readonly IReadOnlyList<GateStep> Steps = [
        new("build", GateStepKind.Build, ["build", "Puck.slnx", "-c", CliOptions.DefaultConfiguration, CliOptions.NoNodeReuse, "-v", "q", "-nologo"], Heavy: true),
        new("copy CLI", GateStepKind.CopyCli, []),
        new("affected", GateStepKind.Puck, ["affected", "--merge-base", "<merge base>", "--run", "--suite-jobs", "<suite-jobs>"], Heavy: true),
        new("format", GateStepKind.Puck, ["format", "--check", "--file-list", "<file list>"], Sources: true),
        new("lengths", GateStepKind.Puck, ["lengths", "--check"]),
        new("comment-smells", GateStepKind.Puck, ["comment-smells", "--check"]),
        new("docs links", GateStepKind.Puck, ["docs", "links"]),
        new("schema", GateStepKind.Puck, ["schema", "--check"]),
        new("architecture", GateStepKind.Puck, ["architecture", "--check"]),
        new("registry", GateStepKind.Puck, ["registry", "--check"]),
        new("vocabulary", GateStepKind.Puck, ["vocabulary", "--check"]),
        new("shaders generate", GateStepKind.Puck, ["shaders", "generate", "--check"]),
        new("branding", GateStepKind.Puck, ["branding", "--check"]),
        new("formats", GateStepKind.Puck, ["formats", "--check"]),
        new("canary-ceilings", GateStepKind.Puck, ["canary-ceilings", "--check"]),
        new("derivations", GateStepKind.Puck, ["derivations", "--check"]),
        .. BaselinesCommand.Artifacts.OrderBy(keySelector: static artifact => artifact.Name, comparer: StringComparer.Ordinal)
            .Select(selector: static artifact => new GateStep(("baselines " + artifact.Name), GateStepKind.Baseline, artifact.CheckArguments(), Heavy: true)),
        new("affected canaries", GateStepKind.Canaries, ["canary", "--gpu-jobs", "<gpu-jobs>", "<canaries>"], Heavy: true, Gpu: true),
        new("parity", GateStepKind.Parity, ["parity"], Heavy: true, Gpu: true),
        .. DeviceSuites.Select(selector: static device => DeviceSuite(selection: device.Selection, suite: device.Suite)),
        new("counters", GateStepKind.Counters, ["counters", "--check", "--world", "<world>", "--ceilings", "<ceilings>"], Heavy: true, Gpu: true),
        new("docs citations", GateStepKind.Puck, ["docs", "citations"], Heavy: true, Gpu: true),
        new("affected record", GateStepKind.Puck, ["affected", "--record"], Heavy: true, Gpu: true, Record: true),
    ];
    /// <summary>Every check omitted from the batch needs an explicit decision here.</summary>
    public static readonly IReadOnlyDictionary<string, string> CheckExclusions = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["compile"] = "Needs a tree and output; affected runs the shipped catalog check when reached.",
        ["packages"] = "Its valued --check needs the document whose generated package section is checked.",
        ["embed"] = "Needs an authored embedding root; checked by its owning change.",
        ["migrate"] = "Needs a named migration and source path; no migration is implied by qualification.",
    };

    private static GateStep DeviceSuite(string suite, string[] selection) => new(suite, GateStepKind.DeviceSuite,
        [.. AffectedCommand.TestArguments(suite: suite), .. selection], Heavy: true, Gpu: true);
    // An argument a shell would split or pipe is quoted, so a printed step runs as written.
    private static string Quoted(string argument) => ((argument.IndexOfAny(anyOf: [' ', '|', ';']) >= 0)
        ? $"\"{argument}\""
        : argument);

    public static string Detail() => ("Steps, in order:\n" + string.Join(separator: "\n", values: Steps.Select(selector: (step, index) =>
        $"  {(index + 1)}. {step.Name}: {((step.Kind == GateStepKind.CopyCli) ? "copy the freshly built CLI into the run directory" : (((step.Kind is GateStepKind.Build or GateStepKind.DeviceSuite) ? "dotnet " : "puck ") + string.Join(separator: ' ', values: step.Arguments.Select(selector: Quoted))))}{(step.Record ? " (only --gpu --record, after every prior step passes)" : ((step.Kind == GateStepKind.Baseline) ? " (only when affected reaches its inputs)" : (step.Gpu ? " (only --gpu)" : (step.Sources ? " (only when sources changed)" : string.Empty))))}.")));
    /// <summary>Expands every recorded workload in ordinal order; unrecorded worlds are not qualification steps.</summary>
    public static IEnumerable<GateStep> CounterWorkloads(string repositoryRoot, GateStep step) {
        const string DirectoryName = "tests/Puck.Counters";
        const string CeilingsSuffix = ".ceilings.json";
        var directory = Path.Combine(path1: repositoryRoot, path2: DirectoryName);

        if (!Directory.Exists(path: directory)) { yield break; }
        foreach (var ledger in Directory.EnumerateFiles(path: directory, searchPattern: ("*" + CeilingsSuffix)).Order(comparer: StringComparer.Ordinal)) {
            var name = Path.GetFileName(path: ledger)[..^CeilingsSuffix.Length];
            var stem = ((DirectoryName + "/") + name);
            var ceilings = (stem + CeilingsSuffix);
            using var document = JsonDocument.Parse(File.ReadAllText(path: ledger));
            var world = document.RootElement.GetProperty(propertyName: "workload").GetString()
                ?? throw new InvalidDataException(message: $"Recorded counters ledger '{ceilings}' has no workload path.");
            var script = (stem + ".script.txt");
            // Some recorded workloads share a script; the ceilings own that script identity.
            if (!File.Exists(path: Path.Combine(path1: repositoryRoot, path2: script))) {
                script = (document.RootElement.TryGetProperty(propertyName: "script", value: out var recorded) ? recorded.GetString() : null);
            }
            yield return step with {
                Name = ((step.Name + " ") + name),
                Kind = GateStepKind.Puck,
                Arguments = [.. step.Arguments.Select(selector: argument => argument switch {
                    "<world>" => world,
                    "<ceilings>" => ceilings,
                    _ => argument,
                }), .. ((script is null) ? (string[])[] : ["--script", script])],
            };
        }
    }
    /// <summary>Expands the plan into the steps one run takes, in order.</summary>
    /// <param name="repositoryRoot">The checkout the run gates.</param>
    /// <param name="mergeBase">The resolved merge base the affected step reads the change against.</param>
    /// <param name="fileList">The JSON list of changed sources the format step checks.</param>
    /// <param name="sources">Whether any C# or <c>.puck</c> source changed.</param>
    /// <param name="gpu">Whether the run takes the GPU steps.</param>
    /// <param name="record">Whether the run ends by recording canary coverage.</param>
    /// <param name="affected">The affected plan, which chooses the baseline, canary and parity steps.</param>
    /// <param name="suiteJobs">The most suites the affected step runs at once.</param>
    /// <param name="gpuJobs">The most canary legs the affected step keeps on the GPU at once, with
    /// <paramref name="gpu"/>.</param>
    /// <returns>The steps.</returns>
    public static IEnumerable<GateStep> Expand(string repositoryRoot, string mergeBase, string fileList, bool sources, bool gpu, bool record, AffectedPlan affected, int suiteJobs, int gpuJobs) {
        foreach (var step in Steps) {
            if ((step.Gpu && !gpu) || (step.Record && !record) || (step.Sources && !sources)) { continue; }
            if ((step.Kind == GateStepKind.Baseline) && !affected.Baselines.Any(predicate: artifact => (artifact.Name == step.Arguments[1]))) { continue; }
            if ((step.Kind == GateStepKind.Parity) && !affected.Parity) { continue; }
            if (step.Kind == GateStepKind.Canaries) {
                if (affected.Canaries.Count > 0) { yield return step with { Arguments = ["canary", "--gpu-jobs", gpuJobs.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), .. affected.Canaries] }; }
                continue;
            }
            if (step.Kind == GateStepKind.Counters) {
                foreach (var workload in CounterWorkloads(repositoryRoot: repositoryRoot, step: step)) { yield return workload; }
                continue;
            }
            yield return step with {
                Arguments = [.. step.Arguments.Select(selector: argument => argument switch {
                "<merge base>" => mergeBase,
                "<file list>" => fileList,
                "<suite-jobs>" => suiteJobs.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
                _ => argument,
            })],
            };
        }
    }
}

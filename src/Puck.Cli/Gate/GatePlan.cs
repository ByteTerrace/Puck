using System.Text.Json;
using Puck.Cli.Affected;

namespace Puck.Cli.Gate;

internal enum GateStepKind { Build, CopyCli, Puck, DeviceSuite, Counters }
internal sealed record GateStep(string Name, GateStepKind Kind, string[] Arguments, bool Heavy = false, bool Gpu = false, bool Record = false, bool Sources = false);
/// <summary>The ordered batch qualification, shared by execution, help and the documentation laws.</summary>
internal static class GatePlan {
    // The device-law suites and the test arguments that select each one's device tests: World.Tests by name, the
    // others whole. MTP1's merge replaces this with the Gpu trait filter; nothing else spells the selection.
    public static readonly IReadOnlyList<(string Suite, string[] Selection)> DeviceSuites = [
        ("Puck.World.Tests", ["--filter", "FullyQualifiedName~DeviceLaw|FullyQualifiedName~RenderedProbeKernelHostLawTests|FullyQualifiedName~SharedFenceLawTests|FullyQualifiedName~HeadlessVulkanDeviceValidationLawTests|FullyQualifiedName~HeadlessVulkanLifecycleLawTests"]),
        ("Puck.DirectX.Tests", []),
        ("Puck.Vulkan.Tests", []),
        ("Puck.Platform.Windows.Tests", []),
    ];
    public static readonly IReadOnlyList<GateStep> Steps = [
        new("build", GateStepKind.Build, ["build", "Puck.slnx", "-c", CliOptions.DefaultConfiguration, CliOptions.NoNodeReuse, "-v", "q", "-nologo"], Heavy: true),
        new("copy CLI", GateStepKind.CopyCli, []),
        new("affected", GateStepKind.Puck, ["affected", "--merge-base", "<merge base>", "--run"], Heavy: true),
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
        ["baselines corpus-inventory"] = "Runs the author-expression corpus inventory suite; checked by the corpus change.",
        ["baselines state"] = "Runs committed state baselines twice; checked by the world-state change.",
        ["baselines maths-ledger"] = "Runs the Maths default tier to regenerate law records; checked by the Maths change.",
        ["baselines browser-parity"] = "Runs browser-state baseline recording tests; checked by the browser change.",
    };

    private static GateStep DeviceSuite(string suite, string[] selection) => new(suite, GateStepKind.DeviceSuite,
        [.. AffectedCommand.TestArguments(suite: suite), .. selection], Heavy: true, Gpu: true);
    // An argument a shell would split or pipe is quoted, so a printed step runs as written.
    private static string Quoted(string argument) => ((argument.IndexOfAny(anyOf: [' ', '|', ';']) >= 0)
        ? $"\"{argument}\""
        : argument);

    public static string Detail() => ("Steps, in order:\n" + string.Join(separator: "\n", values: Steps.Select(selector: (step, index) =>
        $"  {(index + 1)}. {step.Name}: {((step.Kind == GateStepKind.CopyCli) ? "copy the freshly built CLI into the run directory" : (((step.Kind is GateStepKind.Build or GateStepKind.DeviceSuite) ? "dotnet " : "puck ") + string.Join(separator: ' ', values: step.Arguments.Select(selector: Quoted))))}{(step.Record ? " (only --gpu --record, after every prior step passes)" : (step.Gpu ? " (only --gpu)" : (step.Sources ? " (only when sources changed)" : string.Empty)))}.")));
    /// <summary>Expands every recorded workload in ordinal order; unrecorded worlds are not qualification steps.</summary>
    public static IEnumerable<GateStep> CounterWorkloads(string repositoryRoot, GateStep step) {
        const string DirectoryName = "tests/Puck.Counters";
        const string WorldSuffix = ".world.json";
        var directory = Path.Combine(path1: repositoryRoot, path2: DirectoryName);

        if (!Directory.Exists(path: directory)) { yield break; }
        foreach (var world in Directory.EnumerateFiles(path: directory, searchPattern: ("*" + WorldSuffix)).Order(comparer: StringComparer.Ordinal)) {
            var name = Path.GetFileName(path: world)[..^WorldSuffix.Length];
            var stem = ((DirectoryName + "/") + name);
            var ceilings = (stem + ".ceilings.json");

            if (!File.Exists(path: Path.Combine(path1: repositoryRoot, path2: ceilings))) { continue; }
            var script = (stem + ".script.txt");
            // Some recorded workloads share a script; the ceilings own that script identity.
            if (!File.Exists(path: Path.Combine(path1: repositoryRoot, path2: script))) {
                using var document = JsonDocument.Parse(File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: ceilings)));

                script = (document.RootElement.TryGetProperty(propertyName: "script", value: out var recorded) ? recorded.GetString() : null);
            }
            yield return step with {
                Name = ((step.Name + " ") + name),
                Kind = GateStepKind.Puck,
                Arguments = [.. step.Arguments.Select(selector: argument => argument switch {
                    "<world>" => (stem + WorldSuffix),
                    "<ceilings>" => ceilings,
                    _ => argument,
                }), .. ((script is null) ? (string[])[] : ["--script", script])],
            };
        }
    }
    public static IEnumerable<GateStep> Expand(string repositoryRoot, string mergeBase, string fileList, bool sources, bool gpu, bool record) {
        foreach (var step in Steps) {
            if ((step.Gpu && !gpu) || (step.Record && !record) || (step.Sources && !sources)) { continue; }
            if (step.Kind == GateStepKind.Counters) {
                foreach (var workload in CounterWorkloads(repositoryRoot: repositoryRoot, step: step)) { yield return workload; }
                continue;
            }
            yield return step with {
                Arguments = [.. step.Arguments.Select(selector: argument => argument switch {
                "<merge base>" => mergeBase,
                "<file list>" => fileList,
                _ => argument,
            }), .. ((gpu && (step.Name == "affected")) ? (string[])["--gpu"] : [])],
            };
        }
    }
}

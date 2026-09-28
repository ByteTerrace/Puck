using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for placing a shader deleted since the base through the base's own stand-ins: the stand-in map reads the tree
/// the base recorded (an <see cref="IAffectedTree"/>), never the working tree, so a deleted kernel stands for the loader
/// the base named it from, a deleted include stands for the loaders of the base's kernels whose closure reached it, and a
/// shader neither the base's index nor its stand-ins reach stays deleted.
/// </summary>
public sealed class AffectedDeletedStandInsLawTests {
    private const string Canary = "engine-canary";
    private const string Include = "src/Engine/Assets/common.hlsli";
    private const string Kernel = "src/Engine/Assets/old-kernel.comp.hlsl";
    private const string Loader = "src/Engine/Loader.cs";
    private const string Orphan = "src/Engine/Assets/orphan.hlsl";

    private static readonly AffectedProject Engine = new(Directory: "src/Engine", IsSuite: false, Name: "Engine", References: []);
    // The tree the base recorded: a project whose shader items compile one kernel, the include that kernel reaches, the
    // C# that loads the kernel by name, and a shader nothing compiles or loads.
    private static readonly AffectedMemoryTree Base = new(files: new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["src/Engine/Engine.csproj"] = """<Project><ItemGroup><ComputeShaderSource Include="Assets/**/*.comp.hlsl" /></ItemGroup></Project>""",
        [Include] = "static const uint Common = 1;",
        [Kernel] = "#include \"common.hlsli\"\n[numthreads(1, 1, 1)] void main() {}",
        [Loader] = "Load(\"old-kernel\");",
        [Orphan] = "float4 main() : SV_Target { return 0; }",
    });

    private static AffectedPlan Select(params string[] deleted) {
        var recorded = new Dictionary<string, IReadOnlySet<string>>(comparer: StringComparer.Ordinal) {
            [Loader] = new HashSet<string>(collection: [Canary]),
        };

        return AffectedSelection.Select(
            canaries: [],
            canariesReaching: static _ => new HashSet<string>(),
            catalogInputs: static (_, _) => false,
            changed: deleted,
            consumersOf: static _ => [],
            coverage: new Dictionary<string, IReadOnlySet<string>>(),
            declaresTests: static _ => throw new InvalidOperationException(message: "A deleted file is never read."),
            deleted: new HashSet<string>(collection: deleted, comparer: StringComparer.Ordinal),
            projects: [Engine],
            recorded: recorded,
            recordedStandInsFor: AffectedStandIns.Create(
                indexed: [.. recorded.Keys],
                projects: [Engine],
                tree: Base
            ),
            standInsFor: static _ => throw new InvalidOperationException(message: "A deleted file stands for what the base said, never the working tree."),
            worldClosure: new HashSet<string>(collection: ["Engine"], comparer: StringComparer.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void ADeletedKernelIsPlacedByTheLoaderTheBaseNamedItFrom() {
        var plan = Select(Kernel);

        Assert.Equal(actual: plan.Canaries, expected: [Canary]);
        Assert.Empty(collection: plan.Deleted);
        Assert.Empty(collection: plan.Unmapped);
    }
    [Fact]
    public void ADeletedIncludeABaseKernelReachedIsPlacedByThatKernelsLoader() {
        var plan = Select(Include);

        Assert.Equal(actual: plan.Canaries, expected: [Canary]);
        Assert.Empty(collection: plan.Deleted);
    }
    [Fact]
    public void AShaderNeitherTheBasesIndexNorItsStandInsReachStaysDeleted() {
        var plan = Select(Orphan);

        Assert.Empty(collection: plan.Canaries);
        Assert.Equal(actual: plan.Deleted, expected: [Orphan]);
        Assert.Empty(collection: plan.Unmapped);
    }
    /// <summary>The base's stand-in map reads nothing from disk: its kernels and their closures come from the tree it is
    /// given.</summary>
    [Fact]
    public void TheBasesKernelsAndClosuresAreReadFromItsTreeAlone() {
        var kernel = Assert.Single(collection: AffectedStandIns.Kernels(projects: [Engine], tree: Base));

        Assert.Equal(actual: kernel.Path, expected: Kernel);
        Assert.Equal(actual: kernel.Closure, expected: [Kernel, Include]);
    }
}

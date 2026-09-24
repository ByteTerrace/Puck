using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldArtifactClosure"/> names every checkout path a World build reads, so
/// a source change the key does not see can never be answered with a stale build. The law asks MSBuild itself: it
/// evaluates <c>src/Puck.World</c> and every project it reaches, through every <c>ProjectReference</c>, and collects
/// each file-backed item those evaluations name. Every project MSBuild reaches must be one the walk reached, and every
/// input inside the checkout, apart from <c>bin</c> and <c>obj</c> output, must lie under a walked root.</summary>
public sealed class WorldArtifactClosureLawTests {
    // Injected into every project of the graph: recurses through its ProjectReference items, then returns its own
    // project file and every item kind a Puck build reads from disk.
    private const string CollectTargets = """
        <Project>
          <Target Name="PuckCollectInputs" Returns="@(_PuckInput)">
            <MSBuild Projects="@(ProjectReference)" Targets="PuckCollectInputs" Properties="Configuration=Release;DesignTimeBuild=true;CustomAfterMicrosoftCommonTargets=$(CustomAfterMicrosoftCommonTargets)" RemoveProperties="TargetFramework;RuntimeIdentifier" SkipNonexistentTargets="true">
              <Output TaskParameter="TargetOutputs" ItemName="_PuckChildInput" />
            </MSBuild>
            <ItemGroup>
              <_PuckInput Include="$(MSBuildProjectFullPath)" Kind="Project" />
              <_PuckInput Include="@(Compile->'%(FullPath)');@(AdditionalFiles->'%(FullPath)');@(None->'%(FullPath)');@(Content->'%(FullPath)');@(EmbeddedResource->'%(FullPath)')" Kind="Item" />
              <_PuckInput Include="@(ShaderBytecode->'%(FullPath)');@(ShaderInclude->'%(FullPath)');@(FragmentShaderSource->'%(FullPath)');@(VertexShaderSource->'%(FullPath)');@(ComputeShaderSource->'%(FullPath)')" Kind="Item" />
              <_PuckInput Include="@(PuckWorldSource->'%(FullPath)')" Kind="Item" />
              <_PuckInput Include="$(MSBuildProjectDirectory)\$(ApplicationIcon)" Condition="'$(ApplicationIcon)' != ''" Kind="Item" />
              <_PuckInput Include="@(_PuckChildInput)" />
            </ItemGroup>
          </Target>
        </Project>
        """;
    private const string CollectProject = """
        <Project>
          <Target Name="Collect">
            <MSBuild Projects="$(PuckWorldProject)" Targets="PuckCollectInputs" Properties="Configuration=Release;DesignTimeBuild=true;CustomAfterMicrosoftCommonTargets=$(MSBuildThisFileDirectory)collect.targets">
              <Output TaskParameter="TargetOutputs" ItemName="Collected" />
            </MSBuild>
            <WriteLinesToFile File="$(MSBuildThisFileDirectory)inputs.txt" Lines="@(Collected->'%(Kind)|%(Identity)')" Overwrite="true" />
          </Target>
        </Project>
        """;

    private static bool Under(string path, IReadOnlyList<string> roots) =>
        roots.Any(predicate: root => (string.Equals(
            a: path,
            b: root,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ) || path.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: $"{root}/"
        )));

    [Fact]
    public void EveryInputTheWorldBuildReadsLiesUnderAWalkedRoot() {
        var root = RepositoryPaths.RequireRoot();
        using var scratch = new TemporaryDirectory();

        scratch.WriteText(
            name: "collect.targets",
            text: CollectTargets
        );

        var project = scratch.WriteText(
            name: "collect.proj",
            text: CollectProject
        );
        var evaluation = CliProcess.RunCaptured(
            arguments: ["msbuild", project, "-nologo", "-v:q", $"-p:PuckWorldProject={Path.Combine(path1: root, path2: WorldArtifactClosure.WorldProject)}"],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(value: 5)
        );

        Assert.True(
            condition: (evaluation.ExitCode == 0),
            userMessage: $"{evaluation.Stdout}{Environment.NewLine}{evaluation.Stderr}"
        );

        var (roots, projects) = WorldArtifactClosure.Walk(repositoryRoot: root);
        var reached = new List<string>();
        var inputs = new List<string>();

        foreach (var line in File.ReadAllLines(path: scratch.PathOf(name: "inputs.txt"))) {
            var separator = line.IndexOf(value: '|');
            var relative = Path.GetRelativePath(
                path: line[(separator + 1)..],
                relativeTo: root
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            );

            if (
                relative.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".."
            ) ||
                Path.IsPathRooted(path: relative) ||
                relative.Split(separator: '/').Any(predicate: static segment => (segment is "bin" or "obj"))
            ) {
                continue;
            }

            (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "Project|"
            )
                ? reached
                : inputs).Add(item: relative);
        }

        Assert.Contains(
            collection: reached,
            expected: "src/Puck.Cli/Puck.Cli.csproj"
        );
        Assert.True(
            condition: (inputs.Count > 1000),
            userMessage: $"the evaluation reported only {inputs.Count} inputs, too few to be the World's whole graph"
        );
        Assert.Empty(collection: reached.Where(predicate: path => !projects.Contains(
            comparer: StringComparer.OrdinalIgnoreCase,
            value: path
        )).Distinct(comparer: StringComparer.OrdinalIgnoreCase));
        Assert.Empty(collection: inputs.Where(predicate: path => !Under(
            path: path,
            roots: roots
        )).Distinct(comparer: StringComparer.OrdinalIgnoreCase));
    }
}

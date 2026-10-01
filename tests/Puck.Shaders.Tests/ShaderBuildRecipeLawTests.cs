using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Puck.Shaders.Tests;

/// <summary>
/// The build and the runtime shader compiler run one DXC recipe. <c>build/Shaders.targets</c> compiles every stage
/// source with the options <c>build/ShaderRecipe.targets</c> holds for its stage and target, and that file is
/// <see cref="ShaderCompiler.GenerateBuildRecipe"/>'s output, so a stage source compiles with exactly the arguments
/// <see cref="ShaderCompiler.StepsOf"/> gives the compiler, followed by the output and the source.
/// </summary>
public sealed partial class ShaderBuildRecipeLawTests {
    [Fact]
    public void TheBuildCompilesAStageSourceWithTheArgumentsTheCompilerRuns() {
        var recipe = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: ShaderCompiler.BuildRecipePath)).ReplaceLineEndings(replacementText: "\n");

        Assert.Equal(expected: ShaderCompiler.GenerateBuildRecipe(), actual: recipe);

        var properties = XDocument.Parse(text: recipe).Descendants(name: "PropertyGroup").Elements().ToDictionary(
            elementSelector: static element => element.Value,
            keySelector: static element => element.Name.LocalName
        );
        var targets = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"));
        var commands = ExecCommand().Matches(input: targets).Select(selector: static match => (Item: match.Groups["item"].Value, Extension: match.Groups["extension"].Value, Property: match.Groups["property"].Value)).ToArray();

        // Every DXC compile of the build (every invocation but the version probe) is one of these six, and each runs
        // its stage's options and nothing else.
        Assert.Equal(expected: 6, actual: commands.Length);
        Assert.Equal(expected: 6, actual: Regex.Count(input: targets, pattern: "&quot;\\$\\(DxcCommand\\)&quot; (?!--version)"));

        foreach (var stage in Enum.GetValues<ShaderStage>()) {
            var steps = ShaderCompiler.StepsOf(
                entryPoint: ShaderCompiler.BuildEntryPointOf(stage: stage),
                stage: stage
            );

            foreach (var (step, spirv) in ((ReadOnlySpan<(ShaderCompileStep, bool)>)[(steps[0], true), (steps[1], false)])) {
                var property = ShaderCompiler.BuildRecipePropertyOf(
                    spirv: spirv,
                    stage: stage
                );
                var command = Assert.Single(collection: commands, predicate: command => (command.Property == property));

                Assert.Equal(expected: ShaderCompiler.DxcTool, actual: step.Tool);
                Assert.Equal(actual: command.Item, expected: $"{stage}ShaderSource");
                Assert.Equal(actual: command.Extension, expected: (spirv ? "spv" : "dxil"));
                Assert.Equal(expected: step.Options, actual: properties[property].Split(separator: ' '));
            }
        }
    }

    // One build invocation: DXC, a recipe property, and -Fo the stage source's bytecode beside it, then the source.
    [GeneratedRegex(pattern: "Command=\"&quot;\\$\\(DxcCommand\\)&quot; \\$\\((?<property>PuckDxc\\w+)\\) -Fo &quot;%\\((?<item>\\w+)\\.RootDir\\)%\\(\\k<item>\\.Directory\\)%\\(\\k<item>\\.Filename\\)\\.(?<extension>spv|dxil)&quot; &quot;%\\(\\k<item>\\.FullPath\\)&quot;\"")]
    private static partial Regex ExecCommand();
}

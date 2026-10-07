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
        var declarations = XDocument.Parse(text: targets).Descendants(name: "_PuckShaderBytecode").ToArray();
        var commands = declarations.Select(selector: declaration => {
            var output = BytecodeItem().Match(input: declaration.Attribute(name: "Include")!.Value);
            var options = RecipeProperty().Match(input: declaration.Element(name: "Recipe")!.Value);

            Assert.True(condition: output.Success);
            Assert.True(condition: options.Success);
            Assert.Equal(expected: "$([System.IO.Path]::ChangeExtension('%(FullPath)', '.hlsl'))", actual: declaration.Element(name: "SourcePath")!.Value);
            return (Item: output.Groups["item"].Value, Extension: output.Groups["extension"].Value, Property: options.Groups["property"].Value);
        }).ToArray();

        // Every stage/backend recipe reaches the bounded compiler task through the selected bytecode items.
        Assert.Equal(expected: 6, actual: commands.Length);
        Assert.Equal(expected: 6, actual: Regex.Count(input: targets, pattern: "&quot;\\$\\(DxcCommand\\)&quot; (?!--version)"));
        var compile = Assert.Single(collection: XDocument.Parse(text: targets).Descendants(name: "Target")
            .Single(predicate: target => (target.Attribute(name: "Name")!.Value == "CompileShaders"))
            .Elements(name: "PuckCompileShaderBytecode"));

        Assert.Equal(expected: "@(_PuckCompiledBytecode)", actual: compile.Attribute(name: "BytecodeFiles")!.Value);
        Assert.Equal(expected: "$(_PuckShaderToken)", actual: compile.Attribute(name: "Token")!.Value);
        var worker = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "build/PuckCompileShaderBytecode.cs"));

        Assert.Contains(actualString: worker, expectedSubstring: "Command = (((((bytecode.GetMetadata(metadataName: \"Recipe\") + \" -Fo \\\"\") + TemporaryPath(bytecode: bytecode)) + \"\\\" \\\"\") + bytecode.GetMetadata(metadataName: \"SourcePath\")) + \"\\\"\"),");
        Assert.Contains(actualString: worker, expectedSubstring: "private string TemporaryPath(ITaskItem bytecode) => (((bytecode.GetMetadata(metadataName: \"FullPath\") + \".\") + Token) + \".tmp\");");

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

    [GeneratedRegex(pattern: @"^@\((?<item>\w+) -> '%\(RootDir\)%\(Directory\)%\(Filename\)\.(?<extension>spv|dxil)'\)$")]
    private static partial Regex BytecodeItem();
    [GeneratedRegex(pattern: "^\"\\$\\(DxcCommand\\)\" \\$\\((?<property>PuckDxc\\w+)\\)$")]
    private static partial Regex RecipeProperty();
}

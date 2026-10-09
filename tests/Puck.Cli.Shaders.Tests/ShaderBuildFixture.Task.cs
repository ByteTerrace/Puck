using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Puck.Cli.Testing;

// The task is shipped as source for MSBuild's inline compiler. Compile that exact source so cancellation and the
// engine's failure/late-grant cases can use controlled public IBuildEngine9 behavior without an MSBuild scheduler.
internal sealed partial class ShaderBuildFixture {
    private static readonly Lazy<Type> BuildTask = new(valueFactory: () => {
        var references = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(separator: Path.PathSeparator)
            .Append(element: typeof(ITask).Assembly.Location)
            .Append(element: typeof(Microsoft.Build.Utilities.Task).Assembly.Location)
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
            .Select(selector: static path => MetadataReference.CreateFromFile(path: path));
        var compilation = CSharpCompilation.Create(
            assemblyName: ("ShaderBuildTaskLaw" + Guid.NewGuid().ToString(format: "N")),
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary),
            references: references,
            syntaxTrees: [CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "build/PuckShaderBuild.cs")))]
        );
        using var output = new MemoryStream();
        var result = compilation.Emit(peStream: output);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: '\n', values: result.Diagnostics));

        return Assembly.Load(rawAssembly: output.ToArray()).GetType(name: "PuckShaderBuild", throwOnError: true)!;
    });

    public ITask CreateBuildTask(IBuildEngine engine) {
        var task = ((ITask)Activator.CreateInstance(type: BuildTask.Value)!);

        task.BuildEngine = engine;
        void Set(string name, object value) => task.GetType().GetProperty(name: name)!.SetValue(obj: task, value: value);
        Set(name: "Host", value: "dotnet");
        Set(name: "Tool", value: RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders.Generator/bin/{Configuration}/net10.0/Puck.Shaders.Generator.dll"));
        Set(name: "Mode", value: "compile");
        Set(name: "ProjectDirectory", value: Root);
        Set(name: "LockFile", value: PathOf(path: "obj/shader-publish.lock"));
        Set(name: "RequestFile", value: PathOf(path: "obj/shader-build.txt"));
        Set(name: "CacheDirectory", value: PathOf(path: "cache"));
        Set(name: "DxcCommand", value: CompilerPath);
        Set(name: "Outputs", value: Directory.GetFiles(path: PathOf(path: "Assets/Shaders"), searchPattern: "*.comp.hlsl").Select(selector: static source => {
            var item = new Microsoft.Build.Utilities.TaskItem(itemSpec: Path.ChangeExtension(extension: ".spv", path: source));

            item.SetMetadata(metadataName: "Stage", metadataValue: "Compute");
            item.SetMetadata(metadataName: "Backend", metadataValue: "Spirv");
            item.SetMetadata(metadataName: "SourcePath", metadataValue: source);

            return ((ITaskItem)item);
        }).ToArray());

        return task;
    }
}

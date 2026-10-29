using System.Reflection;
using System.Xml.Linq;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Puck.Cli.Testing;

// The task is shipped as source for MSBuild's inline compiler, one operation of the repository's one inline task
// (build/PuckBuildTasks.cs). Compile that exact source once so both in-process task tests and isolated MSBuild
// fixture runs execute the compiled task without paying RoslynCodeTaskFactory on every cold MSBuild process.
internal sealed partial class ShaderBuildFixture {
    private static readonly Lazy<(Type ShaderBuildType, string AssemblyPath)> CompiledTask = new(valueFactory: () => {
        var references = ((string)AppContext.GetData(name: "TRUSTED_PLATFORM_ASSEMBLIES")!).Split(separator: Path.PathSeparator)
            .Append(element: typeof(ITask).Assembly.Location)
            .Append(element: typeof(Microsoft.Build.Utilities.Task).Assembly.Location)
            .Distinct(comparer: StringComparer.OrdinalIgnoreCase)
            .Select(selector: static path => MetadataReference.CreateFromFile(path: path));
        var compilation = CSharpCompilation.Create(
            assemblyName: ("ShaderBuildTaskLaw" + Guid.NewGuid().ToString(format: "N")),
            options: new CSharpCompilationOptions(outputKind: OutputKind.DynamicallyLinkedLibrary),
            references: references,
            syntaxTrees: [CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "build/PuckBuildTasks.cs")))]
        );
        using var output = new MemoryStream();
        var result = compilation.Emit(peStream: output);

        Assert.True(condition: result.Success, userMessage: string.Join(separator: '\n', values: result.Diagnostics));
        var bytes = output.ToArray();
        var path = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-shader-build-task-{Environment.ProcessId}-{Guid.NewGuid():N}.dll");

        File.WriteAllBytes(bytes: bytes, path: path);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => {
            try { File.Delete(path: path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };

        return (Assembly.Load(rawAssembly: bytes).GetType(name: "PuckShaderBuild", throwOnError: true)!, path);
    });
    private static readonly Lazy<byte[]?> WindowsCompilerBytes = new(valueFactory: () => {
        if (!OperatingSystem.IsWindows()) {
            return null;
        }
        var mscorlib = Path.Combine(path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.Windows), path2: "Microsoft.NET/Framework64/v4.0.30319/mscorlib.dll");
        if (!File.Exists(path: mscorlib)) {
            return null;
        }
        var systemDll = Path.Combine(path1: Path.GetDirectoryName(path: mscorlib)!, path2: "System.dll");
        var compilation = CSharpCompilation.Create(
            assemblyName: "fixture-dxc",
            options: new CSharpCompilationOptions(outputKind: OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Release),
            references: [MetadataReference.CreateFromFile(path: mscorlib), MetadataReference.CreateFromFile(path: systemDll)],
            syntaxTrees: [CSharpSyntaxTree.ParseText(text: """
                using System;
                using System.Diagnostics;
                using System.IO;
                using System.Text;
                using System.Threading;

                internal static class Program {
                    private static int Main(string[] args) {
                        var fo = Array.IndexOf(args, "-Fo");
                        var output = args[fo + 1];
                        var source = args[args.Length - 1];
                        var mode = File.ReadAllText("compiler-mode.txt").Trim();
                        File.WriteAllText("started/" + Process.GetCurrentProcess().Id, source);
                        if (mode == "hold" || mode == "fail-peer") {
                            while (!File.Exists("release") && (mode != "fail-peer" || Directory.GetFiles("started").Length < 2)) {
                                Thread.Sleep(10);
                            }
                            if (mode == "fail-peer" && source.EndsWith("a.comp.hlsl", StringComparison.Ordinal)) {
                                File.WriteAllText(output, "unpublished failure");
                                Console.Error.WriteLine(source + ":1:1: error: deliberate compiler failure");
                                return 1;
                            }
                            if (mode == "fail-peer") {
                                while (!File.Exists("release")) {
                                    Thread.Sleep(10);
                                }
                            }
                        }
                        var utf8 = new UTF8Encoding(false);
                        File.WriteAllText(output, "compiled " + File.ReadAllText(source), utf8);
                        for (;;) {
                            try {
                                using (var stream = File.Open("compiles.txt", FileMode.Append, FileAccess.Write, FileShare.None)) {
                                    var bytes = utf8.GetBytes(source + "\n");
                                    stream.Write(bytes, 0, bytes.Length);
                                    break;
                                }
                            } catch (IOException) {
                                Thread.Sleep(10);
                            }
                        }
                        return 0;
                    }
                }
                """)]
        );
        using var stream = new MemoryStream();
        var emitted = compilation.Emit(peStream: stream);

        Assert.True(condition: emitted.Success, userMessage: string.Join(separator: '\n', values: emitted.Diagnostics));

        return stream.ToArray();
    });

    partial void ConfigureFastBuild(XElement project, ref bool configuredTask) {
        project.Add(content: new XElement(name: "UsingTask",
            new XAttribute(name: "TaskName", value: "PuckBuildTask"),
            new XAttribute(name: "AssemblyFile", value: CompiledTask.Value.AssemblyPath)));
        configuredTask = true;
    }
    partial void TryWriteFastCompiler(ref string? compilerPath) {
        if (WindowsCompilerBytes.Value is not { } bytes) {
            return;
        }
        var path = PathOf(path: "fixture-dxc.exe");
        File.WriteAllBytes(bytes: bytes, path: path);
        compilerPath = Puck.Abstractions.PuckPaths.Normalize(path: path);
    }

    public ITask CreateBuildTask(IBuildEngine engine) {
        var task = ((ITask)Activator.CreateInstance(type: CompiledTask.Value.ShaderBuildType)!);

        task.BuildEngine = engine;
        void Set(string name, object value) => task.GetType().GetProperty(name: name)!.SetValue(obj: task, value: value);
        Set(name: "Host", value: "dotnet");
        Set(name: "Tool", value: RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders.Generator/bin/{Configuration}/net11.0/Puck.Shaders.Generator.dll"));
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

using System.Reflection;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Puck.Cli.Tests;

public sealed partial class ShaderBuildTargetsLawTests {
    [Fact]
    public async Task ConcurrentTasksKeepTheirRequestFilesUntilTheirOwnGeneratorExits() {
        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "hold");
        var firstEngine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var secondEngine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        using var firstRequested = new ManualResetEventSlim();
        using var secondRequested = new ManualResetEventSlim();

        ((ShaderCoreEngine)firstEngine).Grant = count => { firstRequested.Set(); return count; };
        ((ShaderCoreEngine)secondEngine).Grant = count => { secondRequested.Set(); return count; };
        var first = fixture.CreateBuildTask(engine: firstEngine);
        var second = fixture.CreateBuildTask(engine: secondEngine);
        var firstBuild = Task.Run(function: first.Execute, cancellationToken: TestContext.Current.CancellationToken);
        Task<bool>? secondBuild = null;

        try {
            _ = WaitFor(find: () => (firstRequested.IsSet ? "the first generator read its manifest" : null));
            secondBuild = Task.Run(function: second.Execute, cancellationToken: TestContext.Current.CancellationToken);
            _ = WaitFor(find: () => (secondRequested.IsSet ? "the second generator read its manifest" : null));

            Assert.Equal(expected: 2, actual: Directory.GetFiles(path: fixture.PathOf(path: "obj"), searchPattern: "shader-build.txt*").Length);
        } finally {
            fixture.Write(path: "release", text: "release both generators' compiler children");
            _ = await firstBuild;
            if (secondBuild is not null) { _ = await secondBuild; }
        }

        Assert.True(condition: await firstBuild);
        Assert.True(condition: await secondBuild!);
        Assert.Empty(collection: Directory.GetFiles(path: fixture.PathOf(path: "obj"), searchPattern: "shader-build.txt*"));
        Assert.Equal(expected: 0, actual: ((ShaderCoreEngine)firstEngine).Held);
        Assert.Equal(expected: 0, actual: ((ShaderCoreEngine)secondEngine).Held);
    }
    [Fact]
    public async Task CancellationStopsAdmissionAndJoinsTheActualCompilerChildren() {
        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "hold");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);

        budget.Grant = _ => 1;
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);

        try {
            _ = WaitFor(find: () => (((fixture.Started().Length > 0) || build.IsCompleted) ? "a compiler child or an early exit" : null));
            Assert.False(condition: build.IsCompleted, userMessage: string.Join(separator: '\n', values: budget.Messages));
            ((ICancelableTask)compiler).Cancel();
            if (await Task.WhenAny(task1: build, task2: Task.Delay(TimeSpan.FromSeconds(value: 30), TestContext.Current.CancellationToken)) != build) {
                fixture.Write(path: "release", text: "watchdog release");
            }
        } finally {
            if (!build.IsCompleted) {
                ((ICancelableTask)compiler).Cancel();
            }
        }

        Assert.False(condition: await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30)));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "release")), userMessage: "Cancellation did not join the compiler children before the watchdog released them.");
        // The generator joined its compiles and said so: the host's process-tree kill, its fallback, would leave no line.
        Assert.Contains(collection: budget.Messages, expected: "Shader build cancelled: every compile it started has ended.");
        fixture.RequireChildrenExited();
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets"), searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AnInitialCoreRefusalEndsTheGeneratorWithoutWaitingForCancellation(bool throws) {
        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "normal");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);
        using var requested = new ManualResetEventSlim();

        budget.Grant = _ => {
            requested.Set();
            return (throws ? throw new InvalidOperationException(message: "deliberate budget refusal") : 0);
        };
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);
        var stoppedWithoutCancellation = false;

        try {
            _ = WaitFor(find: () => ((requested.IsSet || build.IsCompleted) ? "a request or early exit" : null));
            Assert.True(condition: requested.IsSet, userMessage: string.Join(separator: '\n', values: budget.Messages));
            stoppedWithoutCancellation = (await Task.WhenAny(task1: build, task2: Task.Delay(TimeSpan.FromSeconds(value: 10), TestContext.Current.CancellationToken)) == build);
        } finally {
            if (!build.IsCompleted) { ((ICancelableTask)compiler).Cancel(); }
            _ = await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30));
        }

        Assert.True(condition: stoppedWithoutCancellation, userMessage: "The host silently dropped the refusal and left the generator waiting for its first core.");
        Assert.False(condition: await build);
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Empty(collection: fixture.Started());
    }
    [Fact]
    public async Task ABuildNeverMakesAnUncancellableSecondEngineRequest() {
        using var fixture = new Fixture();

        fixture.ParallelProject(mode: "normal");
        var engine = DispatchProxy.Create<IBuildEngine9, ShaderCoreEngine>();
        var budget = ((ShaderCoreEngine)engine);
        using var unblock = new ManualResetEventSlim();
        var requests = 0;

        budget.Grant = _ => {
            if (Interlocked.Increment(location: ref requests) > 1) {
                unblock.Wait(cancellationToken: TestContext.Current.CancellationToken);
            }
            return 1;
        };
        var compiler = fixture.CreateBuildTask(engine: engine);
        var build = Task.Run(function: compiler.Execute, cancellationToken: TestContext.Current.CancellationToken);

        try {
            Assert.True(condition: await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30)), userMessage: string.Join(separator: '\n', values: budget.Messages));
        } finally {
            unblock.Set();
            if (!build.IsCompleted) { ((ICancelableTask)compiler).Cancel(); }
            _ = await build.WaitAsync(cancellationToken: TestContext.Current.CancellationToken, timeout: TimeSpan.FromSeconds(seconds: 30));
        }

        Assert.Equal(actual: requests, expected: 1);
        Assert.Equal(expected: 0, actual: budget.Held);
        Assert.Equal(expected: 3, actual: fixture.Compiles());
    }

    // The task is shipped as source for MSBuild's inline compiler. Compile that exact source so cancellation and the
    // engine's failure/late-grant cases can use controlled public IBuildEngine9 behavior without an MSBuild scheduler.
    internal sealed partial class Fixture {
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

    public class ShaderCoreEngine : DispatchProxy {
        private int m_held;

        public int Held => Volatile.Read(location: ref m_held);

        public Func<int, int> Grant { get; set; } = static count => count;
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            switch (targetMethod!.Name) {
                case "RequestCores":
                    var granted = Grant(arg: ((int)args![0]!));

                    _ = Interlocked.Add(location1: ref m_held, value: granted);
                    return granted;
                case "ReleaseCores":
                    var held = Interlocked.Add(location1: ref m_held, value: -((int)args![0]!));

                    Assert.True(condition: (held >= 0), userMessage: "The task released cores it did not hold.");
                    return null;
                case "LogErrorEvent":
                case "LogMessageEvent":
                case "LogWarningEvent":
                    Messages.Enqueue(item: (((BuildEventArgs)args![0]!).Message ?? ""));
                    return null;
                default:
                    return (targetMethod.ReturnType.IsValueType ? Activator.CreateInstance(type: targetMethod.ReturnType) : null);
            }
        }
    }
}

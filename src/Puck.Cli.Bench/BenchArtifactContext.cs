using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Puck.Cli.Bench;

// Runs bench world against a World build's engine instead of the CLI's own, so a frozen before/after pair measures two
// builds rather than this CLI twice. The bench code and the CLI's own assemblies (Puck.Cli*) load from the CLI;
// every other assembly the build carries, its engine and the packages it was built against, loads from the build;
// what the build lacks falls back to the CLI, and the runtime framework is shared. The load context is never
// unloaded: the process ends with the measurement.
internal sealed class BenchArtifactContext : AssemblyLoadContext {
    private static readonly HashSet<string> FrameworkAssemblies = Directory.EnumerateFiles(path: RuntimeEnvironment.GetRuntimeDirectory(), searchPattern: "*.dll")
        .Select(selector: Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);

    private readonly AssemblyDependencyResolver m_build;
    private readonly AssemblyDependencyResolver m_cli;

    private BenchArtifactContext(string artifact) : base(name: "bench world artifact", isCollectible: false) {
        m_build = new AssemblyDependencyResolver(componentAssemblyPath: artifact);
        m_cli = new AssemblyDependencyResolver(componentAssemblyPath: CliPaths.Tool);
    }

    protected override Assembly? Load(AssemblyName assemblyName) {
        var name = (assemblyName.Name ?? throw new FileLoadException(message: "An assembly was requested without a name."));

        if (FrameworkAssemblies.Contains(item: name)) { return null; }
        var own = name.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: "Puck.Cli");
        var path = ((own ? null : m_build.ResolveAssemblyToPath(assemblyName: assemblyName)) ?? m_cli.ResolveAssemblyToPath(assemblyName: assemblyName));

        return ((path is null) ? null : LoadFromAssemblyPath(assemblyPath: path));
    }
    protected override nint LoadUnmanagedDll(string unmanagedDllName) {
        var path = (m_build.ResolveUnmanagedDllToPath(unmanagedDllName: unmanagedDllName) ?? m_cli.ResolveUnmanagedDllToPath(unmanagedDllName: unmanagedDllName));

        return ((path is null) ? 0 : LoadUnmanagedDllFromPath(unmanagedDllPath: path));
    }

    /// <summary>Runs <see cref="WorldBenchmarks.MeasureRepeats"/> with a World build's engine.</summary>
    /// <param name="artifact">The build's <c>Puck.World.dll</c>.</param>
    /// <param name="path">The world document's full path.</param>
    /// <param name="repeats">The repeat count.</param>
    /// <returns>The measurement's JSON payload.</returns>
    /// <exception cref="InvalidOperationException">The build's engine cannot run this CLI's bench code.</exception>
    public static string MeasureRepeats(string artifact, string path, int repeats) {
        var context = new BenchArtifactContext(artifact: Path.GetFullPath(path: artifact));

        try {
            var bench = context.LoadFromAssemblyPath(assemblyPath: typeof(BenchArtifactContext).Assembly.Location);
            var method = (bench.GetType(name: typeof(WorldBenchmarks).FullName!, throwOnError: true)!.GetMethod(name: nameof(WorldBenchmarks.MeasureRepeats), bindingAttr: (BindingFlags.Public | BindingFlags.Static))
                ?? throw new MissingMethodException(className: typeof(WorldBenchmarks).FullName, methodName: nameof(WorldBenchmarks.MeasureRepeats)));

            return (string)method.Invoke(obj: null, parameters: [path, repeats])!;
        } catch (TargetInvocationException exception) {
            throw new InvalidOperationException(message: $"the World build's engine could not run this CLI's bench code: {exception.GetBaseException().Message}");
        } catch (Exception exception) when (exception is FileLoadException or FileNotFoundException or TypeLoadException or MissingMemberException or BadImageFormatException) {
            throw new InvalidOperationException(message: $"the World build's engine could not run this CLI's bench code: {exception.Message}");
        }
    }
}

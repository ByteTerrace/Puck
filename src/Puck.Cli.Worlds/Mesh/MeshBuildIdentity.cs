using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Puck.Cli.Mesh;

// Only shared-runtime values cross the boundary. Neither a target object nor a reflected member survives unloading.
internal sealed record MeshBuildIdentity(
    UInt128 Value,
    string EntryVersion,
    UInt128 Code,
    UInt128 MaskKernel,
    UInt128 BakeKernel,
    IReadOnlyDictionary<string, UInt128> Members,
    IReadOnlyDictionary<string, string> Reasons
) {
    internal static MeshBuildIdentity Read(string buildDirectory, string extension) {
        var directory = Path.GetFullPath(buildDirectory);
        var context = new TargetBuildContext(directory);
        try {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "Puck.SdfVm.dll"));
            var closureType = RequiredType(assembly, "Puck.SdfVm.SdfMeshBakeClosure");
            var kernelsType = RequiredType(assembly, "Puck.SdfVm.SdfKernelSet");
            var identityType = RequiredType(assembly, "Puck.SdfVm.SdfMeshBakeIdentity");
            var code = Invoke(RequiredMethod(closureType, "Of", typeof(string)), directory);
            var passes = Invoke(RequiredMethod(kernelsType, "PassesDirectory", typeof(string)), Path.Combine(directory, "Assets", "Shaders", "Sdf"));
            var kernels = Invoke(RequiredMethod(kernelsType, "Load", typeof(string), typeof(string)), extension, passes);
            var identity = Invoke(RequiredMethod(identityType, "Of", closureType, kernelsType), code, kernels);
            return new MeshBuildIdentity(
                Value: Property<UInt128>(identity, "Value"),
                EntryVersion: Property<string>(identity, "EntryVersion"),
                Code: Property<UInt128>(identity, "Code"),
                MaskKernel: Property<UInt128>(identity, "MaskKernel"),
                BakeKernel: Property<UInt128>(identity, "BakeKernel"),
                Members: Snapshot(Property<IReadOnlyDictionary<string, UInt128>>(identity, "Members")),
                Reasons: Snapshot(Property<IReadOnlyDictionary<string, string>>(code, "Reasons"))
            );
        } catch (TargetInvocationException exception) {
            // Copy only text: retaining a target exception would retain its collectible assembly context too.
            throw new InvalidOperationException($"The target build's identity code refused inspection: {exception.GetBaseException().Message}");
        } catch (Exception exception) when (exception is MissingMemberException or InvalidCastException or TypeLoadException) {
            throw new InvalidOperationException($"The target build does not expose the required mesh identity inspection API: {exception.Message}");
        } finally { context.Unload(); }
    }

    private static Type RequiredType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;

    private static MethodInfo RequiredMethod(Type type, string name, params Type[] parameters) =>
        type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, parameters)
        ?? throw new MissingMethodException(type.FullName, name);

    private static object Invoke(MethodInfo method, params object?[] arguments) =>
        method.Invoke(null, arguments) ?? throw new InvalidOperationException($"Target method {method.DeclaringType?.FullName}.{method.Name} returned no value.");

    private static T Property<T>(object value, string name) =>
        (T)(value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value)
            ?? throw new MissingMemberException(value.GetType().FullName, name));

    private static IReadOnlyDictionary<string, T> Snapshot<T>(IReadOnlyDictionary<string, T> source) {
        var copied = new SortedDictionary<string, T>(StringComparer.Ordinal);
        foreach (var (name, value) in source) { copied.Add(name, value); }
        return new ReadOnlyDictionary<string, T>(copied);
    }

    // Extension discovery's existing context is deliberately host-first and non-collectible. Inspection needs the
    // opposite ownership: this target's complete Puck/dependency graph, sharing only the actual runtime framework.
    private sealed class TargetBuildContext(string directory) : AssemblyLoadContext(isCollectible: true) {
        private static readonly HashSet<string> FrameworkAssemblies = Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        private AssemblyDependencyResolver? m_resolver;

        protected override Assembly? Load(AssemblyName assemblyName) {
            var name = assemblyName.Name ?? throw new FileLoadException("The target requested an assembly without a name.");
            if (FrameworkAssemblies.Contains(name)) { return null; }
            var local = Path.Combine(directory, name + ".dll");
            if (File.Exists(local)) { return LoadFromAssemblyPath(local); }
            m_resolver ??= new AssemblyDependencyResolver(Path.Combine(directory, "Puck.SdfVm.dll"));
            var resolved = m_resolver.ResolveAssemblyToPath(assemblyName);
            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if ((resolved is not null) && Path.GetFullPath(resolved).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) {
                return LoadFromAssemblyPath(resolved);
            }
            // Never fall back to the CLI's Puck assemblies, hash implementation or other application dependencies.
            throw new FileNotFoundException($"Target build '{directory}' is missing its dependency '{assemblyName}'.", local);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
            throw new DllNotFoundException($"Mesh identity inspection cannot load native library '{unmanagedDllName}'.");
    }
}

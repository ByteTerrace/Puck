using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Puck.Cli.Mesh;

/// <summary>The bake identity a World build keys its stored meshes by, read from the build's own assemblies and bake
/// kernels in a collectible context: <c>puck mesh identity</c> prints it, and a player build's mesh cache set is named by
/// it. Only shared-runtime values cross the boundary; neither a target object nor a reflected member survives
/// unloading.</summary>
/// <param name="Value">The identity: the name of the store set the build's meshes land in.</param>
/// <param name="EntryVersion">The entry format's version tag.</param>
/// <param name="Code">The hash of the code the bake runs.</param>
/// <param name="MaskKernel">The hash of the mask kernel's bytecode.</param>
/// <param name="BakeKernel">The hash of the bake kernel's bytecode.</param>
/// <param name="Members">Each code member the bake runs, with its hash.</param>
/// <param name="Reasons">For each member, the member that reached it from a root of the bake.</param>
/// <param name="Stages">Each persisted stage's identity, by stage name.</param>
public sealed record MeshBuildIdentity(
    UInt128 Value,
    string EntryVersion,
    UInt128 Code,
    UInt128 MaskKernel,
    UInt128 BakeKernel,
    IReadOnlyDictionary<string, UInt128> Members,
    IReadOnlyDictionary<string, string> Reasons,
    IReadOnlyDictionary<string, UInt128> Stages
) {
    /// <summary>Reads a build's bake identity for one backend's bake kernels.</summary>
    /// <param name="buildDirectory">The build's directory (its <c>Puck.SdfVm.dll</c>'s).</param>
    /// <param name="extension">The backend's shader bytecode extension (<c>CliWorldDocuments.TryExtension</c>).</param>
    /// <returns>The identity.</returns>
    /// <exception cref="InvalidOperationException">The build refused inspection or lacks the inspection API.</exception>
    /// <exception cref="IOException">A file of the build cannot be read.</exception>
    public static MeshBuildIdentity Read(string buildDirectory, string extension) {
        var directory = Path.GetFullPath(buildDirectory);
        var context = new TargetBuildContext(directory);
        try {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(directory, "Puck.SdfVm.dll"));
            var closureType = RequiredType(assembly, "Puck.SdfVm.SdfMeshBakeClosure");
            var kernelsType = RequiredType(assembly, "Puck.SdfVm.SdfKernelSet");
            var identityType = RequiredType(assembly, "Puck.SdfVm.SdfMeshBakeIdentity");
            var code = Invoke(RequiredMethod(closureType, "Of", typeof(string)), directory);
            var passes = Invoke(RequiredMethod(kernelsType, "PassesDirectory", typeof(string)), Path.Combine(directory, "Assets", "Shaders", "Sdf"));
            var target = ShaderTargetOf(kernelsType, extension);
            var kernels = Invoke(RequiredMethod(kernelsType, "Load", target.GetType(), typeof(string)), target, passes);
            var identity = Invoke(RequiredMethod(identityType, "Of", closureType, kernelsType), code, kernels);
            return new MeshBuildIdentity(
                Value: Property<UInt128>(identity, "Value"),
                EntryVersion: Property<string>(identity, "EntryVersion"),
                Code: Property<UInt128>(identity, "Code"),
                MaskKernel: Property<UInt128>(identity, "MaskKernel"),
                BakeKernel: Property<UInt128>(identity, "BakeKernel"),
                Members: Snapshot(Property<IReadOnlyDictionary<string, UInt128>>(identity, "Members")),
                Reasons: Snapshot(Property<IReadOnlyDictionary<string, string>>(code, "Reasons")),
                Stages: new Dictionary<string, UInt128>(StringComparer.Ordinal) {
                    ["sampling"] = Property<UInt128>(identity, "Sampling"),
                    ["contouring"] = Property<UInt128>(identity, "Contouring"),
                    ["simplification"] = Property<UInt128>(identity, "Simplification"),
                    ["landing"] = Property<UInt128>(identity, "Value"),
                }
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

    // The target's ShaderTarget is its own type in its own load context, so it is minted there through the target's
    // ShaderTargets.Of, never cast from the CLI's copy.
    private static object ShaderTargetOf(Type kernelsType, string extension) {
        var load = kernelsType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SingleOrDefault(method => (method.Name == "Load") && method.GetParameters() is [{ ParameterType.IsEnum: true }, { } directory] && (directory.ParameterType == typeof(string)))
            ?? throw new MissingMethodException(kernelsType.FullName, "Load");
        var targetType = load.GetParameters()[0].ParameterType;
        var targets = RequiredType(targetType.Assembly, $"{targetType.Namespace}.ShaderTargets");
        var hostsOnDirectX = string.Equals(extension, Puck.Abstractions.Gpu.ShaderBytecode.FileExtension(hostsOnDirectX: true), StringComparison.Ordinal);
        return Invoke(RequiredMethod(targets, "Of", typeof(bool)), hostsOnDirectX);
    }

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
            if ((resolved is not null) && Path.GetFullPath(resolved).StartsWith(prefix, Puck.Abstractions.PuckPaths.Comparison)) {
                return LoadFromAssemblyPath(resolved);
            }
            // Never fall back to the CLI's Puck assemblies, hash implementation or other application dependencies.
            throw new FileNotFoundException($"Target build '{directory}' is missing its dependency '{assemblyName}'.", local);
        }

        // A native library the target ships loads from the target; one the CLI ships is refused, as a managed one is;
        // anything else (kernel32, libc) is the operating system's and takes the default probe.
        protected override nint LoadUnmanagedDll(string unmanagedDllName) {
            m_resolver ??= new AssemblyDependencyResolver(Path.Combine(directory, "Puck.SdfVm.dll"));
            if (m_resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } resolved) { return LoadUnmanagedDllFromPath(resolved); }
            if (Directory.EnumerateFiles(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(unmanagedDllName) + ".*", SearchOption.AllDirectories).Any()) {
                throw new DllNotFoundException($"Target build '{directory}' is missing its native library '{unmanagedDllName}'.");
            }
            return nint.Zero;
        }
    }
}

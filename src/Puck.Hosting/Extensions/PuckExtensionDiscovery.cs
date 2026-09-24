using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Puck.Abstractions;

namespace Puck.Hosting;

/// <summary>
/// Discovers installed extensions and composes them with a host's built-in ones. An extensions directory holds one
/// subdirectory per installed extension, <c>&lt;name&gt;/&lt;name&gt;.dll</c> beside that assembly's dependency manifest;
/// the assembly names its entry types with <see cref="PuckExtensionAttribute"/>, and each loads into its own
/// <see cref="PuckExtensionLoadContext"/>. Every host — the World, the silo, and the silo a CLI runs — discovers through
/// this type, so the same installation composes the same set everywhere.
/// </summary>
/// <remarks>Discovery runs once, while a host composes, and never on a simulation or frame path. Anything it cannot
/// install is refused by path rather than skipped, so a host never runs with a different capability set than its
/// installation names.</remarks>
public static class PuckExtensionDiscovery {
    /// <summary>Gets the directories every host searches when no directory is named: <c>extensions</c> under the working
    /// directory, then <c>extensions</c> beside the host's own assemblies, each once.</summary>
    /// <returns>The distinct full paths, whether or not they exist.</returns>
    public static IReadOnlyList<string> DefaultDirectories() => Distinct(directories: [
        Path.Combine(
            path1: Directory.GetCurrentDirectory(),
            path2: "extensions"
        ),
        Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "extensions"
        ),
    ]);

    private static string[] Distinct(IEnumerable<string> directories) => [.. directories
        .Select(selector: static directory => Path.TrimEndingDirectorySeparator(path: Path.GetFullPath(path: directory)))
        .Distinct(comparer: PuckPaths.Comparer)];

    /// <summary>Composes a host's built-in extensions with every extension installed in the given directories, activated
    /// in assembly-name order. The composed set owns them all; a refused composition disposes every extension it
    /// received, built-in or activated, before the refusal propagates (see <see cref="PuckExtensionSet.Compose"/>).</summary>
    /// <param name="builtIns">The extensions the host composes from its own references.</param>
    /// <param name="directories">The extensions directories to search; a missing directory contributes nothing.</param>
    /// <returns>The host's composed set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builtIns"/> or <paramref name="directories"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="PuckExtensionException">An assembly sits directly in an extensions directory; a subdirectory lacks
    /// its <c>&lt;name&gt;.dll</c>; two directories install the same assembly name; an assembly cannot load, declares no
    /// <see cref="PuckExtensionAttribute"/>, or names a type that is not a constructible <see cref="IPuckExtension"/> or
    /// whose constructor fails; or composition is refused.</exception>
    [RequiresUnreferencedCode(message: "Installed extension assemblies are loaded from disk, outside trim analysis.")]
    public static PuckExtensionSet Compose(IEnumerable<IPuckExtension> builtIns, IEnumerable<string> directories) {
        ArgumentNullException.ThrowIfNull(argument: builtIns);
        ArgumentNullException.ThrowIfNull(argument: directories);
        return PuckExtensionSet.Compose(extensions: builtIns.Concat(second: Installed(directories: directories)));
    }

    // Yields each installed extension as it activates, so a refusal part-way leaves the ones already yielded with the
    // composition that disposes them.
    [RequiresUnreferencedCode(message: "Installed extension assemblies are loaded from disk, outside trim analysis.")]
    private static IEnumerable<IPuckExtension> Installed(IEnumerable<string> directories) {
        var candidates = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var directory in Distinct(directories: directories)) {
            if (!Directory.Exists(path: directory)) { continue; }
            foreach (var stray in Directory.EnumerateFiles(
                path: directory,
                searchPattern: "*.dll"
            )) {
                throw new PuckExtensionException(message: $"'{stray}' sits directly in an extensions directory; install each extension as <name>/<name>.dll.");
            }
            foreach (var installation in Directory.EnumerateDirectories(path: directory)) {
                var name = Path.GetFileName(path: installation);
                var assembly = Path.Combine(
                    path1: installation,
                    path2: $"{name}.dll"
                );

                if (!File.Exists(path: assembly)) { throw new PuckExtensionException(message: $"Extension directory '{installation}' carries no '{name}.dll'."); }
                if (!candidates.TryAdd(
                    key: name,
                    value: assembly
                )) {
                    throw new PuckExtensionException(message: $"Extension '{name}' is installed twice: '{candidates[name]}' and '{assembly}'.");
                }
            }
        }

        var installed = new Dictionary<string, PuckExtensionLoadContext>(comparer: StringComparer.Ordinal);

        foreach (var (name, assembly) in candidates) {
            installed.Add(
                key: name,
                value: new PuckExtensionLoadContext(
                    assemblyPath: assembly,
                    installed: installed
                )
            );
        }

        foreach (var context in installed.Values.OrderBy(
            comparer: StringComparer.Ordinal,
            keySelector: static context => context.Name
        )) {
            foreach (var extension in Activate(context: context)) { yield return extension; }
        }
    }
    [RequiresUnreferencedCode(message: "Installed extension assemblies are loaded from disk, outside trim analysis.")]
    private static IEnumerable<IPuckExtension> Activate(PuckExtensionLoadContext context) {
        PuckExtensionAttribute[] declarations;

        try { declarations = [.. context.Primary.GetCustomAttributes<PuckExtensionAttribute>()]; } catch (Exception error) when ((error is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException)) {
            throw new PuckExtensionException(
                inner: error,
                message: $"Extension '{context.AssemblyPath}' could not load: {error.Message}"
            );
        }
        if (declarations.Length == 0) { throw new PuckExtensionException(message: $"Extension '{context.AssemblyPath}' declares no [PuckExtension] entry type."); }
        foreach (var declaration in declarations) {
            var type = declaration.ExtensionType;

            if (
                !typeof(IPuckExtension).IsAssignableFrom(c: type) ||
                type.IsAbstract ||
                (type.GetConstructor(types: Type.EmptyTypes) is null)
            ) {
                throw new PuckExtensionException(message: $"Extension '{context.AssemblyPath}' names {type.FullName}, which is not a constructible {nameof(IPuckExtension)}.");
            }
            IPuckExtension extension;

            try { extension = ((IPuckExtension)Activator.CreateInstance(type: type)!); } catch (System.Reflection.TargetInvocationException error) {
                throw new PuckExtensionException(
                    inner: (error.InnerException ?? error),
                    message: $"Extension '{context.AssemblyPath}' could not construct {type.FullName}: {(error.InnerException ?? error).Message}"
                );
            }
            yield return extension;
        }
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;

namespace Puck.Hosting;

/// <summary>
/// The load context of one installed extension. It resolves an assembly in this order: an installed extension's own
/// assembly, from that extension's context, so extensions that build on each other share one type identity; an
/// assembly the host carries at a compatible version, from <see cref="AssemblyLoadContext.Default"/>, so every contract
/// a host and its extensions exchange has one identity; a dependency that an upstream installed extension (one whose
/// assembly this extension references) resolves, from that extension's context; and finally this extension's own
/// published dependency graph.
/// </summary>
/// <remarks>Contexts are not collectible. Discovery installs contributions for the host's lifetime and returns no unload
/// owner; a collectible context could be finalized before a later hosted-service callback resolves one of its
/// dependencies.</remarks>
public sealed class PuckExtensionLoadContext : AssemblyLoadContext {
    private readonly IReadOnlyDictionary<string, PuckExtensionLoadContext> m_installed;
    private readonly Lazy<Assembly> m_primary;
    private readonly AssemblyDependencyResolver m_resolver;
    private readonly Lazy<PuckExtensionLoadContext[]> m_upstream;

    /// <summary>Initializes a new instance of the <see cref="PuckExtensionLoadContext"/> class for one installed
    /// extension assembly. Nothing loads until <see cref="Primary"/> is first read.</summary>
    /// <param name="assemblyPath">The full path of the extension's own assembly, beside its dependency manifest.</param>
    /// <param name="installed">Every installed extension's context keyed by assembly name, this one included; the
    /// caller fills it before any context loads.</param>
    /// <exception cref="ArgumentException"><paramref name="assemblyPath"/> is <see langword="null"/> or blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="installed"/> is <see langword="null"/>.</exception>
    [RequiresUnreferencedCode(message: "An extension assembly loaded from disk is outside trim analysis.")]
    public PuckExtensionLoadContext(string assemblyPath, IReadOnlyDictionary<string, PuckExtensionLoadContext> installed) : base(
        isCollectible: false,
        name: Path.GetFileNameWithoutExtension(path: assemblyPath)
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: assemblyPath);
        ArgumentNullException.ThrowIfNull(argument: installed);
        AssemblyPath = assemblyPath;
        m_installed = installed;
        m_resolver = new AssemblyDependencyResolver(componentAssemblyPath: assemblyPath);
        m_primary = new Lazy<Assembly>(valueFactory: () => LoadFromAssemblyPath(assemblyPath: AssemblyPath));
        m_upstream = new Lazy<PuckExtensionLoadContext[]>(valueFactory: () => [.. Primary.GetReferencedAssemblies()
            .Select(selector: reference => (((reference.Name is { } name) && m_installed.TryGetValue(
                key: name,
                value: out var owner
            ) && !ReferenceEquals(
                objA: owner,
                objB: this
            ))
                ? owner
                : null
            ))
            .OfType<PuckExtensionLoadContext>()
            .OrderBy(
                comparer: StringComparer.Ordinal,
                keySelector: static owner => owner.Name
            )]);
    }

    /// <summary>Gets the full path of the extension's own assembly.</summary>
    public string AssemblyPath { get; }
    /// <summary>Gets the extension's own assembly, loading it on first read.</summary>
    /// <exception cref="BadImageFormatException">The file is not a loadable assembly.</exception>
    /// <exception cref="FileLoadException">The assembly cannot be loaded.</exception>
    public Assembly Primary => m_primary.Value;

    private static Assembly? FromHost(AssemblyName assemblyName) {
        try { return Default.LoadFromAssemblyName(assemblyName: assemblyName); } catch (Exception error) when ((error is FileNotFoundException or FileLoadException)) {
            // The host does not carry this assembly, or carries an older version than the extension requires.
            return null;
        }
    }

    /// <inheritdoc/>
    [UnconditionalSuppressMessage(
        category: "Trimming",
        checkId: "IL2026",
        Justification = "Every context is created through the constructor, which already carries RequiresUnreferencedCode."
    )]
    protected override Assembly? Load(AssemblyName assemblyName) {
        if (assemblyName.Name is not { } name) { return null; }
        if (m_installed.TryGetValue(
            key: name,
            value: out var owner
        )) {
            return owner.Primary;
        }
        if (FromHost(assemblyName: assemblyName) is { } shared) { return shared; }
        foreach (var upstream in m_upstream.Value) {
            if (upstream.m_resolver.ResolveAssemblyToPath(assemblyName: assemblyName) is not null) { return upstream.LoadFromAssemblyName(assemblyName: assemblyName); }
        }
        return ((m_resolver.ResolveAssemblyToPath(assemblyName: assemblyName) is { } path)
            ? LoadFromAssemblyPath(assemblyPath: path)
            : null
        );
    }
    /// <inheritdoc/>
    protected override nint LoadUnmanagedDll(string unmanagedDllName) => ((m_resolver.ResolveUnmanagedDllToPath(unmanagedDllName: unmanagedDllName) is { } path)
        ? LoadUnmanagedDllFromPath(unmanagedDllPath: path)
        : nint.Zero
    );
}

using System.Reflection;

namespace Puck.Assets;

/// <summary>
/// The Puck code a root assembly can run: the root and every <c>Puck.*</c> assembly it references, directly or
/// transitively, keyed by name in ordinal order. It identifies a derivation by the code that makes it, so a key built on
/// it moves whenever that code does and needs no recorded fingerprint.
/// </summary>
public static class AssemblyReach {
    private static bool IsPuck(string? name) =>
        ((name is not null) && name.StartsWith(comparisonType: StringComparison.Ordinal, value: "Puck."));

    /// <summary>Returns <paramref name="root"/> and every <c>Puck.*</c> assembly it reaches through its references.</summary>
    /// <param name="root">The assembly the walk starts from.</param>
    /// <param name="include">Which references to follow, or null for <c>Puck.*</c>. Compiler identities include
    /// non-framework dependencies too, since their code can affect a compiled document.</param>
    /// <param name="unavailable">Receives references removed by trimming; null requires every included reference
    /// to load. Identity callers record an absent module rather than silently omitting it.</param>
    /// <returns>The reached assemblies, keyed by name in ordinal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static SortedDictionary<string, Assembly> Of(Assembly root, Func<AssemblyName, bool>? include = null, Action<AssemblyName>? unavailable = null) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var reached = new SortedDictionary<string, Assembly>(comparer: StringComparer.Ordinal);
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Assembly>();

        pending.Push(item: root);

        while (pending.TryPop(result: out var assembly)) {
            if (!seen.Add(item: (assembly.GetName().Name ?? string.Empty))) {
                continue;
            }
            reached.Add(key: (assembly.GetName().Name ?? string.Empty), value: assembly);

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if ((include?.Invoke(arg: reference) ?? IsPuck(name: reference.Name)) && !seen.Contains(item: reference.Name!)) {
                    try {
                        pending.Push(item: Assembly.Load(assemblyRef: reference));
                    } catch (Exception exception) when (unavailable is not null && exception is FileNotFoundException or FileLoadException or BadImageFormatException) {
                        _ = seen.Add(item: (reference.Name ?? string.Empty));
                        unavailable(obj: reference);
                    }
                }
            }
        }

        return reached;
    }
}

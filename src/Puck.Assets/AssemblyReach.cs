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
    /// <returns>The reached assemblies, keyed by name in ordinal order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root"/> is <see langword="null"/>.</exception>
    public static SortedDictionary<string, Assembly> Of(Assembly root) {
        ArgumentNullException.ThrowIfNull(argument: root);

        var reached = new SortedDictionary<string, Assembly>(comparer: StringComparer.Ordinal);
        var pending = new Stack<Assembly>();

        pending.Push(item: root);

        while (pending.TryPop(result: out var assembly)) {
            if (!reached.TryAdd(key: (assembly.GetName().Name ?? string.Empty), value: assembly)) {
                continue;
            }

            foreach (var reference in assembly.GetReferencedAssemblies()) {
                if (IsPuck(name: reference.Name) && !reached.ContainsKey(key: reference.Name!)) {
                    pending.Push(item: Assembly.Load(assemblyRef: reference));
                }
            }
        }

        return reached;
    }
}

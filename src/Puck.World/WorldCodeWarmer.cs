using System.Reflection;
using System.Runtime.CompilerServices;

namespace Puck.World;

/// <summary>
/// Compiles the engine's own methods ahead of their first call, on one background thread at the lowest priority, so
/// a rendering World's frame never waits for the JIT the first time a body crosses a portal or an addon writes a row.
/// A ReadyToRun-published World carries most of that code compiled already and prepares it at little cost; an
/// ordinary build carries IL, whose first call of each method otherwise compiles it on the thread that makes it. A
/// prepared method still tiers up as one compiled at its first call does. Generic methods and the methods of generic
/// types are left to their first call, which instantiates them.
/// </summary>
public static class WorldCodeWarmer {
    private const BindingFlags Declared = (BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>Starts preparing every method of an assembly and of the <c>Puck</c> assemblies it references, directly or
    /// through one another (<see cref="PuckClosure"/>), on a background thread at the lowest priority (<see cref="Run"/>).</summary>
    /// <param name="entry">The assembly whose <c>Puck</c> references are prepared, with it.</param>
    /// <param name="error">Where the summary line is written.</param>
    /// <returns>The started thread.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> or <paramref name="error"/> is
    /// <see langword="null"/>.</exception>
    public static Thread Start(Assembly entry, TextWriter error) {
        ArgumentNullException.ThrowIfNull(argument: entry);
        ArgumentNullException.ThrowIfNull(argument: error);

        var thread = new Thread(start: () => Run(assemblies: () => PuckClosure(entry: entry), error: error)) {
            IsBackground = true,
            Name = "Puck.World Code Warmer",
            Priority = ThreadPriority.Lowest,
        };

        thread.Start();

        return thread;
    }
    /// <summary>Prepares the methods of the assemblies a source names (<see cref="Prepare"/>) and writes one line on
    /// <paramref name="error"/>: how many it prepared and how long that took, or why it stopped. A warmer that stops
    /// leaves the rest to their first calls; nothing it prepared is undone.</summary>
    /// <param name="assemblies">The source of the assemblies, read on the calling thread.</param>
    /// <param name="error">Where the line is written.</param>
    /// <returns>How many methods were prepared, or -1 when the warmer stopped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblies"/> or <paramref name="error"/> is
    /// <see langword="null"/>.</exception>
    public static int Run(Func<IEnumerable<Assembly>> assemblies, TextWriter error) {
        ArgumentNullException.ThrowIfNull(argument: assemblies);
        ArgumentNullException.ThrowIfNull(argument: error);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        try {
            var prepared = Prepare(assemblies: assemblies());

            error.WriteLine(value: $"[world.warm] prepared {prepared} method(s) in {clock.Elapsed.TotalSeconds:0.00} s");

            return prepared;
        } catch (Exception exception) {
            // A background warmer must never end the process: an assembly it cannot load or a type it cannot read only
            // leaves those methods to their first calls.
            error.WriteLine(value: $"[world.warm] stopped after {clock.Elapsed.TotalSeconds:0.00} s: {exception.GetType().Name}: {exception.Message}");

            return -1;
        }
    }
    /// <summary>Returns an assembly and every <c>Puck</c> assembly it references, directly or through one another, each
    /// once, loading each referenced one.</summary>
    /// <param name="entry">The first assembly.</param>
    /// <returns>The assemblies, the entry first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.</exception>
    /// <exception cref="FileNotFoundException">A referenced <c>Puck</c> assembly is not deployed.</exception>
    public static IReadOnlyList<Assembly> PuckClosure(Assembly entry) {
        ArgumentNullException.ThrowIfNull(argument: entry);

        var found = new List<Assembly> { entry };
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal) { (entry.GetName().Name ?? string.Empty) };

        for (var index = 0; (index < found.Count); index++) {
            foreach (var reference in found[index].GetReferencedAssemblies()) {
                if ((reference.Name is { } name) && name.StartsWith(value: "Puck", comparisonType: StringComparison.Ordinal) && seen.Add(item: name)) {
                    found.Add(item: Assembly.Load(assemblyRef: reference));
                }
            }
        }

        return found;
    }
    /// <summary>Prepares every method and constructor with IL of its own that the non-generic types of the assemblies
    /// declare: an abstract, generic, runtime-implemented or platform-invoke one has none.</summary>
    /// <param name="assemblies">The assemblies.</param>
    /// <returns>How many methods were prepared.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblies"/> is <see langword="null"/>.</exception>
    public static int Prepare(IEnumerable<Assembly> assemblies) {
        ArgumentNullException.ThrowIfNull(argument: assemblies);

        var prepared = 0;

        foreach (var assembly in assemblies) {
            foreach (var type in assembly.GetTypes()) {
                if (type.ContainsGenericParameters) {
                    continue;
                }
                foreach (var method in type.GetMethods(bindingAttr: Declared)) {
                    prepared += (TryPrepare(method: method) ? 1 : 0);
                }
                foreach (var constructor in type.GetConstructors(bindingAttr: Declared)) {
                    prepared += (TryPrepare(method: constructor) ? 1 : 0);
                }
            }
        }

        return prepared;
    }
    // Prepares one method that has IL of its own; false, preparing nothing, for one that has none.
    private static bool TryPrepare(MethodBase method) {
        if (
            method.IsAbstract ||
            method.ContainsGenericParameters ||
            ((method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) ||
            ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
        ) {
            return false;
        }

        RuntimeHelpers.PrepareMethod(method: method.MethodHandle);

        return true;
    }
}

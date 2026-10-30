[assembly: System.Reflection.Metadata.MetadataUpdateHandler(handlerType: typeof(Puck.Abstractions.CodeRevision))]

namespace Puck.Abstractions;

/// <summary>
/// The revision of the running process's code: the one invalidation contract for every product of code. It starts at
/// zero and moves each time the runtime applies a metadata update to a loaded assembly (a hot-reload edit under
/// <c>dotnet watch</c>). A method-body edit changes what the next call runs, but not what an earlier call already
/// derived, so:
/// <list type="bullet">
/// <item>a cache held in memory (a compiled camera rig, a presentation derived from a delivered definition, a retained
/// emission) compares <see cref="Current"/> beside its other inputs and derives again when it moved, or holds its
/// entries in a <see cref="CodeDerivedTable{TKey, TValue}"/>, which forgets them when it moves;</item>
/// <item>a product persisted across processes folds <see cref="AppliedTo"/> into its identity, which stays empty while
/// no applied update touched the code the product reads, so a hot-reloaded process never reads or writes a product of
/// edited code under the identity of the code as built.</item>
/// </list>
/// Outside hot reload nothing moves, so either check costs one read.
/// </summary>
public static class CodeRevision {
    private static readonly Lock Gate = new();
    private static int Applications;
    private static Update[] Updates = [];
    private static string? Token;

    // One applied update: the revision it moved Current to and the types it changed, or null when the runtime did not say.
    private readonly record struct Update(int Revision, Type[]? Types);

    /// <summary>Raised after an update is applied and <see cref="Current"/> has moved, on the thread that applied it,
    /// with the types the update changed, or <see langword="null"/> when the runtime did not say.</summary>
    public static event Action<Type[]?>? Applied;

    /// <summary>Gets the number of metadata updates applied to this process.</summary>
    public static int Current => Volatile.Read(location: ref Applications);

    /// <summary>Returns the applied identity of the code a product reads: empty while no update applied to this process
    /// changed a type <paramref name="reads"/> selects; otherwise a token unique to this process and the latest update
    /// that did. A persisted identity appends it, so a product of edited code is keyed apart from the build's own and
    /// from every other process's edits, and a built process derives its ordinary identities. An update whose types the
    /// runtime did not name touches everything.</summary>
    /// <param name="reads">Whether the product reads a type (each updated type is offered, and so is every type it is
    /// nested in).</param>
    /// <returns>The applied identity, or empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="reads"/> is <see langword="null"/>.</exception>
    public static string AppliedTo(Func<Type, bool> reads) {
        ArgumentNullException.ThrowIfNull(argument: reads);

        var updates = Volatile.Read(location: ref Updates);

        for (var index = (updates.Length - 1); (index >= 0); index--) {
            var update = updates[index];

            if ((update.Types is null) || update.Types.Any(predicate: type => Reads(reads: reads, type: type))) {
                return $"{Volatile.Read(location: ref Token)}.{update.Revision}";
            }
        }

        return string.Empty;
    }
    /// <summary>Returns the applied identity (<see cref="AppliedTo"/>) of the code in the named assemblies.</summary>
    /// <param name="assemblies">The simple names of the assemblies the product's code lives in.</param>
    /// <returns>The applied identity, or empty.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="assemblies"/> is <see langword="null"/>.</exception>
    public static string AppliedToAssemblies(IReadOnlySet<string> assemblies) {
        ArgumentNullException.ThrowIfNull(argument: assemblies);

        return AppliedTo(reads: type => assemblies.Contains(item: (type.Assembly.GetName().Name ?? string.Empty)));
    }

    private static bool Reads(Func<Type, bool> reads, Type type) {
        for (var candidate = type; (candidate is not null); candidate = candidate.DeclaringType) {
            if (reads(arg: candidate)) {
                return true;
            }
        }

        return false;
    }

    // The runtime's hot-reload agent calls these by name, once per applied update: ClearCache before UpdateApplication.
    // Both run on the agent's thread while the application's threads keep running, so a cache sees the moved revision on
    // its next read and derives there, on its own thread. The update is recorded before Current moves, so a reader that
    // sees the new revision also sees what it changed.
    private static void ClearCache(Type[]? updatedTypes) {
        lock (Gate) {
            Volatile.Write(location: ref Token, value: (Token ?? Guid.NewGuid().ToString(format: "N")));

            var revision = (Applications + 1);

            Volatile.Write(location: ref Updates, value: [.. Updates, new Update(Revision: revision, Types: updatedTypes)]);
            Volatile.Write(location: ref Applications, value: revision);
        }
    }
    private static void UpdateApplication(Type[]? updatedTypes) => Applied?.Invoke(obj: updatedTypes);
}

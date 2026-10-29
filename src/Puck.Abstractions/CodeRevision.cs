[assembly: System.Reflection.Metadata.MetadataUpdateHandler(handlerType: typeof(Puck.Abstractions.CodeRevision))]

namespace Puck.Abstractions;

/// <summary>
/// The revision of the running process's code. It starts at zero and moves each time the runtime applies a metadata
/// update to a loaded assembly (a hot-reload edit under <c>dotnet watch</c>). A method-body edit changes what the next
/// call runs, but not what an earlier call already derived: a cache holding a product of code (a compiled camera rig, a
/// presentation derived from a delivered definition) compares this revision beside its other inputs and derives again
/// when it moved. Outside hot reload it never moves, so such a check costs one integer read.
/// </summary>
public static class CodeRevision {
    private static int Applications;

    /// <summary>Raised after an update is applied and <see cref="Current"/> has moved, on the thread that applied it,
    /// with the types the update changed, or <see langword="null"/> when the runtime did not say.</summary>
    public static event Action<Type[]?>? Applied;

    /// <summary>Gets the number of metadata updates applied to this process.</summary>
    public static int Current => Volatile.Read(location: ref Applications);

    // The runtime's hot-reload agent calls these by name, once per applied update: ClearCache before UpdateApplication.
    // Both run on the agent's thread while the application's threads keep running, so a cache sees the moved revision on
    // its next read and derives there, on its own thread.
    private static void ClearCache(Type[]? updatedTypes) => _ = Interlocked.Increment(location: ref Applications);
    private static void UpdateApplication(Type[]? updatedTypes) => Applied?.Invoke(obj: updatedTypes);
}

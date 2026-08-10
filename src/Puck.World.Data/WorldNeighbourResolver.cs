namespace Puck.World;

/// <summary>The outcome of an <see cref="IWorldNeighbourResolver"/> attempt to reach a named neighbour.</summary>
public enum WorldNeighbourResolutionKind {
    /// <summary>The neighbour's document was reached and parsed.</summary>
    Resolved,

    /// <summary>The neighbour could not be reached — not found, no permission, an unreachable endpoint, or any other
    /// transport fact. A first-class outcome rather than a thrown exception, because a zone is a separate authority
    /// that may run on a different host: unreachable is an ordinary answer, never a bug the caller works around.</summary>
    Unavailable,
}

/// <summary>The outcome of resolving one neighbour document — the injected seam's whole vocabulary. Carries no
/// storage type: a caller in <c>Puck.World.Data</c> knows only that a neighbour was reached (with its parsed
/// document) or was not (with a named reason), never HOW the attempt was made.</summary>
public readonly record struct WorldNeighbourResolution {
    private WorldNeighbourResolution(WorldNeighbourResolutionKind kind, WorldDefinition? definition, string reason) {
        Kind = kind;
        Definition = definition;
        Reason = reason;
    }

    /// <summary>Gets whether the neighbour was reached.</summary>
    public WorldNeighbourResolutionKind Kind { get; }

    /// <summary>Gets the neighbour's parsed document when <see cref="Kind"/> is <see cref="WorldNeighbourResolutionKind.Resolved"/>;
    /// otherwise <see langword="null"/>.</summary>
    public WorldDefinition? Definition { get; }

    /// <summary>Gets the named reason the neighbour could not be reached when <see cref="Kind"/> is
    /// <see cref="WorldNeighbourResolutionKind.Unavailable"/>; otherwise empty.</summary>
    public string Reason { get; }

    /// <summary>Builds a resolved outcome.</summary>
    /// <param name="definition">The neighbour's parsed document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldNeighbourResolution Resolved(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldNeighbourResolution(kind: WorldNeighbourResolutionKind.Resolved, definition: definition, reason: string.Empty);
    }

    /// <summary>Builds an unavailable outcome.</summary>
    /// <param name="reason">The named reason the neighbour could not be reached.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/> or whitespace.</exception>
    public static WorldNeighbourResolution Unavailable(string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: reason);

        return new WorldNeighbourResolution(kind: WorldNeighbourResolutionKind.Unavailable, definition: null, reason: reason);
    }
}

/// <summary>
/// Injection seam letting <see cref="WorldDefinitionValidator"/> read a named neighbour's document without knowing
/// how it is reached. <c>Puck.World.Data</c> carries no storage or filesystem dependency (see the project layering
/// rules), so this interface names only the fact a cross-document check needs — resolve a
/// <see cref="WorldReference.Document"/> string to a document, or say why not — and never a storage type. Mirrors
/// <see cref="WorldExtensionVocabularyHook"/>'s layering: declared where the document model needs it, wired by
/// whoever owns the transport (a cloud blob fetch, a local file read, or an in-process lookup are all equally valid
/// implementations).
/// </summary>
/// <remarks>
/// A zone is a separate authority that may run on a different host — colocation is a deployment fact,
/// never a design assumption, so a resolver must be able to answer "I cannot reach that neighbour" as an ordinary
/// result (<see cref="WorldNeighbourResolutionKind.Unavailable"/>) rather than assume the neighbour's document is
/// always on this disk. <see cref="WorldDefinitionValidator"/> refuses by name, never silently, whenever a check that
/// needs a neighbour cannot reach one — whether because no resolver was supplied at all, or because a supplied one
/// answered <see cref="WorldNeighbourResolutionKind.Unavailable"/>.
/// </remarks>
public interface IWorldNeighbourResolver {
    /// <summary>Resolves the document a <see cref="WorldReference.Document"/> row names.</summary>
    /// <param name="document">The <see cref="WorldReference.Document"/> value, authored verbatim.</param>
    /// <returns>The resolution outcome.</returns>
    WorldNeighbourResolution Resolve(string document);
}

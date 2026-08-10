namespace Puck.World;

/// <summary>
/// Composes several <see cref="IWorldNeighbourResolver"/>s into one, trying each in AUTHORED ORDER and returning the
/// first <see cref="WorldNeighbourResolutionKind.Resolved"/> answer. A locally-authored quilt (a neighbour on disk,
/// beside the loaded document — see <see cref="WorldFileNeighbourResolver"/>) and a cloud-synced owned-world catalog
/// (a neighbour in the user's storage container — see <see cref="Server.WorldStorageNeighbourResolver"/>) are both
/// legitimate places the SAME <see cref="WorldReference.Document"/> string might resolve, and a boot with both wired
/// tries the cheap local read first rather than a network round trip for content already on disk. Unavailable only
/// when EVERY inner resolver answers Unavailable, and the combined reason names each miss in order — an operator
/// reading it learns what was tried, not just that everything failed.
/// </summary>
public sealed class WorldCompositeNeighbourResolver : IWorldNeighbourResolver {
    private readonly IReadOnlyList<IWorldNeighbourResolver> m_resolvers;

    /// <summary>Initializes the resolver.</summary>
    /// <param name="resolvers">The inner resolvers, tried in this order.</param>
    /// <exception cref="ArgumentException"><paramref name="resolvers"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolvers"/> is <see langword="null"/>.</exception>
    public WorldCompositeNeighbourResolver(IReadOnlyList<IWorldNeighbourResolver> resolvers) {
        ArgumentNullException.ThrowIfNull(argument: resolvers);

        if (resolvers.Count == 0) {
            throw new ArgumentException(message: "a composite neighbour resolver needs at least one inner resolver.", paramName: nameof(resolvers));
        }

        m_resolvers = resolvers;
    }

    /// <inheritdoc/>
    public WorldNeighbourResolution Resolve(string document) {
        List<string>? misses = null;

        foreach (var resolver in m_resolvers) {
            var outcome = resolver.Resolve(document: document);

            if (outcome.Kind == WorldNeighbourResolutionKind.Resolved) {
                return outcome;
            }

            (misses ??= []).Add(item: outcome.Reason);
        }

        return WorldNeighbourResolution.Unavailable(reason: string.Join(separator: "; then ", values: misses!));
    }

    /// <summary>Folds any number of possibly-<see langword="null"/> resolvers into one: absent resolvers drop out,
    /// zero survivors yields <see langword="null"/> (the ordinary "no resolver wired" answer every call site already
    /// treats as a refuse-by-name signal), one survivor is returned directly (no wrapping overhead), and two or more
    /// compose in the order given.</summary>
    /// <param name="resolvers">The candidate resolvers, in try-order; any may be <see langword="null"/>.</param>
    /// <returns>The composed resolver, the sole survivor, or <see langword="null"/>.</returns>
    public static IWorldNeighbourResolver? Compose(params IWorldNeighbourResolver?[] resolvers) {
        ArgumentNullException.ThrowIfNull(argument: resolvers);

        var present = new List<IWorldNeighbourResolver>(capacity: resolvers.Length);

        foreach (var resolver in resolvers) {
            if (resolver is not null) {
                present.Add(item: resolver);
            }
        }

        return present.Count switch {
            0 => null,
            1 => present[0],
            _ => new WorldCompositeNeighbourResolver(resolvers: present),
        };
    }
}

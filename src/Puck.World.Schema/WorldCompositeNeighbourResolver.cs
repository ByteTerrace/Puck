namespace Puck.World;

/// <summary>
/// Composes several <see cref="IWorldNeighbourResolver"/>s into one, trying each in AUTHORED ORDER — see
/// <see cref="Resolve"/> for the precedence a lookup applies. A locally-authored quilt (a neighbour on disk, beside
/// the loaded document — see <see cref="WorldFileNeighbourResolver"/>) and a cloud-synced owned-world catalog (a
/// neighbour in the user's storage container — see <c>Server.WorldStorageNeighbourResolver</c>) are both legitimate
/// places the SAME <see cref="WorldReference.Document"/> string might resolve, and a boot with both wired tries the
/// cheap local read first rather than a network round trip for content already on disk.
/// <c>Server.WorldStorageNeighbourResolver</c> is the only production producer of an
/// <see cref="WorldNeighbourResolutionKind.Attested"/> outcome today; it reaches only the reader's own storage
/// container under that reader's access policy — trusting one's own blobs, never a counterparty's signature — which
/// is why a derived corner never accepts an attested answer and stays resolved-only.
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
            throw new ArgumentException(
                message: "a composite neighbour resolver needs at least one inner resolver.",
                paramName: nameof(resolvers)
            );
        }

        m_resolvers = resolvers;
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
    /// <summary>Resolves by trying every inner resolver in authored order and ranking the answers
    /// <see cref="WorldNeighbourResolutionKind.Resolved"/> over <see cref="WorldNeighbourResolutionKind.VerifiedAttested"/>
    /// over <see cref="WorldNeighbourResolutionKind.Attested"/>, first of a kind winning within it. A resolved answer
    /// short-circuits the rest of the list; a verified attestation outranks an unsigned one seen earlier, so a
    /// signed cross-owner claim is never pre-empted by a locally composed same-owner copy. An attested outcome of
    /// either kind is a legitimate answer, never a miss, and its empty <see cref="WorldNeighbourResolution.Reason"/>
    /// is never folded into the failure text. Unavailable only when every inner resolver misses, with the combined
    /// reason naming each miss in order.</summary>
    /// <param name="document">The <see cref="WorldReference.Document"/> value, authored verbatim.</param>
    /// <returns>The resolution outcome.</returns>
    public WorldNeighbourResolution Resolve(string document) {
        List<string>? misses = null;
        WorldNeighbourResolution? attested = null;
        WorldNeighbourResolution? verified = null;

        foreach (var resolver in m_resolvers) {
            var outcome = resolver.Resolve(document: document);

            if (outcome.Kind == WorldNeighbourResolutionKind.Resolved) {
                return outcome;
            }

            if (outcome.Kind == WorldNeighbourResolutionKind.VerifiedAttested) {
                verified ??= outcome;

                continue;
            }

            if (outcome.Kind == WorldNeighbourResolutionKind.Attested) {
                attested ??= outcome;

                continue;
            }

            (misses ??= []).Add(item: outcome.Reason);
        }

        if (verified is { } verifiedOutcome) {
            return verifiedOutcome;
        }

        if (attested is { } attestedOutcome) {
            return attestedOutcome;
        }

        return WorldNeighbourResolution.Unavailable(reason: string.Join(
            separator: "; then ",
            values: misses!
        ));
    }
}

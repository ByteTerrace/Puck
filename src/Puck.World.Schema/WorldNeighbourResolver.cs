namespace Puck.World;

/// <summary>The outcome of an <see cref="IWorldNeighbourResolver"/> attempt to reach a named neighbour.</summary>
public enum WorldNeighbourResolutionKind {
    /// <summary>The neighbour's document was reached and parsed.</summary>
    Resolved,

    /// <summary>The neighbour did not hand over its document, but a <see cref="WorldCounterpartAttestation"/> was
    /// composed for it locally — enough to prove reciprocity, extents, frame, and overlap for a direct adjacency,
    /// and nothing else. Carries no proof of who composed it: today's <c>WorldStorageNeighbourResolver</c> produces
    /// this arm from an unsigned fetched copy. Never accepted into a derived-corner proof — a corner names a third
    /// authority a plain attestation cannot make provable; see <see cref="VerifiedAttested"/>.</summary>
    Attested,

    /// <summary>The neighbour's attestation was verified through a signed claim whose chain-of-trust bound an
    /// authenticated subject to exactly the document the attestation names — the one non-<see cref="Resolved"/>
    /// outcome a derived-corner proof accepts. Proves authenticated consistency of the signed statement within its
    /// signed validity window; it does not prove the attester told the truth about its own geometry, and it does not
    /// prove correspondence to any document this resolver never saw. No production resolver in this repository
    /// produces this arm yet — it is the seam a verifying resolver populates.</summary>
    VerifiedAttested,

    /// <summary>The neighbour could not be reached — not found, no permission, an unreachable endpoint, or any other
    /// transport fact. A first-class outcome rather than a thrown exception, because a zone is a separate authority
    /// that may run on a different host: unreachable is an ordinary answer, never a bug the caller works around.</summary>
    Unavailable,
}
/// <summary>The outcome of resolving one neighbour document — the injected seam's whole vocabulary. Carries no
/// storage type: a caller in <c>Puck.World.Schema</c> knows only that a neighbour was reached (with its parsed
/// document) or was not (with a named reason), never HOW the attempt was made.</summary>
public readonly record struct WorldNeighbourResolution {
    private WorldNeighbourResolution(WorldNeighbourResolutionKind kind, WorldDefinition? definition, WorldCounterpartAttestation? attestation, string subject, string reason) {
        Kind = kind;
        Definition = definition;
        Attestation = attestation;
        Subject = subject;
        Reason = reason;
    }

    /// <summary>Gets the neighbour's seam attestation when <see cref="Kind"/> is <see cref="WorldNeighbourResolutionKind.Attested"/>
    /// or <see cref="WorldNeighbourResolutionKind.VerifiedAttested"/>; otherwise <see langword="null"/>.</summary>
    public WorldCounterpartAttestation? Attestation { get; }
    /// <summary>Gets the neighbour's parsed document when <see cref="Kind"/> is <see cref="WorldNeighbourResolutionKind.Resolved"/>;
    /// otherwise <see langword="null"/>.</summary>
    public WorldDefinition? Definition { get; }
    /// <summary>Gets whether the neighbour was reached.</summary>
    public WorldNeighbourResolutionKind Kind { get; }
    /// <summary>Gets the named reason the neighbour could not be reached when <see cref="Kind"/> is
    /// <see cref="WorldNeighbourResolutionKind.Unavailable"/>; otherwise empty.</summary>
    public string Reason { get; }
    /// <summary>Gets the authenticated subject a signed claim's chain-of-trust bound to <see cref="Attestation"/> when
    /// <see cref="Kind"/> is <see cref="WorldNeighbourResolutionKind.VerifiedAttested"/>; otherwise empty.</summary>
    public string Subject { get; }

    /// <summary>Builds an attested outcome — the neighbour proved its half of the seam without handing over its
    /// document, and without any binding to an authenticated subject. Proves an ordinary two-document adjacency
    /// only; never accepted into a derived-corner proof (see <see cref="WorldNeighbourResolutionKind.Attested"/>).</summary>
    /// <param name="attestation">The neighbour's locally composed attestation.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attestation"/> is <see langword="null"/>.</exception>
    public static WorldNeighbourResolution Attested(WorldCounterpartAttestation attestation) {
        ArgumentNullException.ThrowIfNull(argument: attestation);

        return new WorldNeighbourResolution(
            attestation: attestation,
            definition: null,
            kind: WorldNeighbourResolutionKind.Attested,
            reason: string.Empty,
            subject: string.Empty
        );
    }
    /// <summary>Builds a resolved outcome.</summary>
    /// <param name="definition">The neighbour's parsed document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public static WorldNeighbourResolution Resolved(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return new WorldNeighbourResolution(
            attestation: null,
            definition: definition,
            kind: WorldNeighbourResolutionKind.Resolved,
            reason: string.Empty,
            subject: string.Empty
        );
    }
    /// <summary>Builds an unavailable outcome.</summary>
    /// <param name="reason">The named reason the neighbour could not be reached.</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is <see langword="null"/> or whitespace.</exception>
    public static WorldNeighbourResolution Unavailable(string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: reason);

        return new WorldNeighbourResolution(
            attestation: null,
            definition: null,
            kind: WorldNeighbourResolutionKind.Unavailable,
            reason: reason,
            subject: string.Empty
        );
    }
    /// <summary>Builds a verified-attested outcome — a signed claim's own chain-of-trust bound an authenticated
    /// <paramref name="subject"/> to exactly the document <paramref name="attestation"/> attests. The one
    /// non-<see cref="WorldNeighbourResolutionKind.Resolved"/> outcome the derived-corner walk accepts.</summary>
    /// <param name="attestation">The neighbour's verified attestation.</param>
    /// <param name="subject">The authenticated subject the verification bound to the attested document.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attestation"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="subject"/> is <see langword="null"/> or whitespace.</exception>
    public static WorldNeighbourResolution VerifiedAttested(WorldCounterpartAttestation attestation, string subject) {
        ArgumentNullException.ThrowIfNull(argument: attestation);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: subject);

        return new WorldNeighbourResolution(
            attestation: attestation,
            definition: null,
            kind: WorldNeighbourResolutionKind.VerifiedAttested,
            reason: string.Empty,
            subject: subject
        );
    }
}
/// <summary>
/// Injection seam letting <see cref="WorldDefinitionValidator"/> read a named neighbour's document without knowing
/// how it is reached. <c>Puck.World.Schema</c> carries no storage or filesystem dependency (see the project layering
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

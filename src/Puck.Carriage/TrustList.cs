namespace Puck.Carriage;

/// <summary>Whether a trust list entry's key signs claims itself or vouches for others (docs/world-model.md, "Signed carriage").</summary>
public enum CarriageTrustMode {
    /// <summary>
    /// The pinned key signs claims directly and NO chain is walked beneath it — the entry pins one
    /// subject's own signing key, so a claim admitted this way arrives with zero bindings. This is how a
    /// world pins an individual (a friend, a known peer) without trusting the domain that minted them.
    /// It is not a way to shorten a domain's chain: a domain's claims always arrive under a
    /// <see cref="Vouches"/> entry, at exactly two hops.
    /// </summary>
    SignsDirectly,

    /// <summary>
    /// The pinned key is a domain's root and vouches for an issuing key, which vouches for subjects — the
    /// chain, ALWAYS exactly two bindings deep (docs/world-model.md: "A chain is at most two hops, because
    /// one cannot hold").
    /// </summary>
    Vouches,
}

/// <summary>
/// One trust list entry: a pinned id, the actual key bytes it names (needed for offline verification — a
/// hash alone cannot verify a signature), whether it signs directly or vouches, and which slots it reaches.
/// "Trusting a domain and pinning a key are one act" (docs/world-model.md) — a
/// <see cref="CarriageTrustMode.Vouches"/> entry pins the domain's ROOT id, which is what makes the whole
/// chain beneath it trusted.
/// </summary>
/// <param name="PinnedId">The trusted key's id. For <see cref="CarriageTrustMode.Vouches"/> this MUST be a root id (<see cref="KeyId.IsRoot"/>); for <see cref="CarriageTrustMode.SignsDirectly"/> it MUST carry a subject, since only a subject key signs claims.</param>
/// <param name="PublicKeySubjectPublicKeyInfo">The pinned key's actual SPKI bytes, authored alongside the id (never fetched).</param>
/// <param name="Mode">Whether this entry signs directly or vouches for a chain.</param>
/// <param name="Reach">
/// The slot names claims admitted by this entry may reach (docs/world-model.md: a trust entry says "which
/// slots it reaches"). Deny by default — an empty set admits a claim that reaches nothing, and there is
/// deliberately NO wildcard, because a wildcard is how a scope silently widens when a game adds a slot.
/// The verifier returns this set with an accepted claim (<see cref="CarriageVerifyResult.Reach"/>); it
/// never enforces it, because enforcing reach is the receiving world's policy (invariant 5).
/// </param>
/// <param name="MaximumAge">
/// This entry's own verifier-authored maximum claim age, overriding <see cref="TrustList.DefaultMaximumAge"/>
/// when set. The tighter of this (or the default) and the issuer's own window always governs.
/// </param>
public sealed record TrustListEntry(
    KeyId PinnedId,
    ReadOnlyMemory<byte> PublicKeySubjectPublicKeyInfo,
    CarriageTrustMode Mode,
    IReadOnlySet<string> Reach,
    TimeSpan? MaximumAge
) {
    /// <summary>
    /// Validates that <see cref="PublicKeySubjectPublicKeyInfo"/> actually hashes to <see cref="PinnedId"/>,
    /// that the pinned algorithm is a known SIGNING algorithm (a sealing key can never admit a claim), and
    /// that the id's shape matches <see cref="Mode"/>. <see cref="TrustList"/> calls this for every entry at
    /// construction, so an unvalidated list cannot reach the verifier — without that, an entry whose key
    /// bytes disagree with its pinned id would verify against the BYTES while the pin sat there decorative.
    /// </summary>
    /// <exception cref="ArgumentException">The entry is not self-consistent.</exception>
    public void Validate() {
        if (!string.Equals(a: KeyId.ComputeKeyHash(subjectPublicKeyInfo: PublicKeySubjectPublicKeyInfo.Span), b: PinnedId.KeyHash, comparisonType: StringComparison.Ordinal)) {
            throw new ArgumentException(message: "A trust list entry's public key does not hash to its own pinned id — it is not self-certifying.");
        }

        if (!CarriageAlgorithms.IsKnown(algorithm: PinnedId.Algorithm) || (CarriageAlgorithms.Resolve(algorithm: PinnedId.Algorithm).Role != CarriageKeyRole.Signing)) {
            throw new ArgumentException(message: $"A trust list entry pins algorithm '{PinnedId.Algorithm}', which is not a carriage SIGNING algorithm — a trust entry can only pin a key that signs.");
        }

        if ((Mode == CarriageTrustMode.Vouches) && !PinnedId.IsRoot) {
            throw new ArgumentException(message: "A vouching trust list entry must pin a root id — the chain it walks is always exactly two hops beneath a root.");
        }

        if ((Mode == CarriageTrustMode.SignsDirectly) && (PinnedId.Subject is null)) {
            throw new ArgumentException(message: "A directly-signing trust list entry must pin a SUBJECT key — only a subject key signs claims, and a root or issuing key that signed one would be indistinguishable from a binding.");
        }
    }
}

/// <summary>
/// The authored set of issuers a world accepts (docs/world-model.md, "Signed carriage"). An empty list
/// honours no foreign claim — deny by default like every other capability; the engine compiles in no root.
/// Every entry is validated at construction, so the verifier never walks an inconsistent list.
/// </summary>
public sealed record TrustList {
    /// <summary>Builds and validates a trust list.</summary>
    /// <param name="entries">
    /// The trusted entries. Each is validated, and no two may occupy the same LOOKUP SLOT — the same
    /// <c>(domain, subject, mode)</c> triple — whether or not they pin the same key. An ambiguous list is a
    /// bug, not a preference order.
    /// <para>The rule is slot identity rather than key identity because that is what the lookups are keyed
    /// on: <see cref="FindVouchingRoot"/> and <see cref="FindDirectSigner"/> match on domain, subject and
    /// mode and return the FIRST hit, so a second entry in one slot can never be reached and its reach and
    /// maximum age can never govern — exactly the undefined-governance the refusal names. Admitting it
    /// under a key-identity rule would accept a list one of whose entries is silently inert.</para>
    /// <para>The cost is real and worth naming: pinning an old and a new key for one subject — a rotation
    /// overlap — is therefore NOT expressible today. Supporting it means the lookup must try every entry in
    /// the slot rather than the first, which is a verifier change, not a relaxation of this check.</para>
    /// </param>
    /// <param name="defaultMaximumAge">
    /// The verifier's default maximum claim age when an entry does not override it, or <see langword="null"/>
    /// for no verifier-side ceiling (the issuer's own window is then the whole story).
    /// </param>
    /// <exception cref="ArgumentException">An entry is not self-consistent, or two entries collide.</exception>
    public TrustList(IReadOnlyList<TrustListEntry> entries, TimeSpan? defaultMaximumAge) {
        var seen = new HashSet<(string Domain, string? Subject, CarriageTrustMode Mode)>();

        foreach (var entry in entries) {
            entry.Validate();

            if (!seen.Add(item: (entry.PinnedId.Domain, entry.PinnedId.Subject, entry.Mode))) {
                throw new ArgumentException(message: $"A trust list pins domain '{entry.PinnedId.Domain}' (subject '{(entry.PinnedId.Subject ?? "(none)")}') twice in the same mode — lookup returns the first match, so the second entry's reach and maximum age could never govern, and the list is refused rather than resolved by order.");
            }
        }

        DefaultMaximumAge = defaultMaximumAge;
        Entries = entries;
    }

    /// <summary>The trusted entries, in authored order.</summary>
    public IReadOnlyList<TrustListEntry> Entries { get; }

    /// <summary>The verifier's default maximum claim age when an entry does not override it, or <see langword="null"/> for no ceiling.</summary>
    public TimeSpan? DefaultMaximumAge { get; }

    /// <summary>Finds the <see cref="CarriageTrustMode.Vouches"/> entry for a domain, or <see langword="null"/> if that domain is not trusted to vouch.</summary>
    /// <param name="domain">The root fingerprint to look up.</param>
    public TrustListEntry? FindVouchingRoot(string domain) {
        foreach (var entry in Entries) {
            if (
                (entry.Mode == CarriageTrustMode.Vouches) &&
                string.Equals(a: entry.PinnedId.Domain, b: domain, comparisonType: StringComparison.Ordinal)
            ) {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the <see cref="CarriageTrustMode.SignsDirectly"/> entry pinning one subject's own signing key,
    /// or <see langword="null"/> if no such key is pinned. A direct pin is strictly more specific than a
    /// vouching root, so the verifier consults this first.
    /// </summary>
    /// <param name="domain">The claim's domain (the pinned key's own root fingerprint).</param>
    /// <param name="subject">The claim's subject.</param>
    public TrustListEntry? FindDirectSigner(string domain, string? subject) {
        if (subject is null) {
            return null;
        }

        foreach (var entry in Entries) {
            if (
                (entry.Mode == CarriageTrustMode.SignsDirectly) &&
                string.Equals(a: entry.PinnedId.Domain, b: domain, comparisonType: StringComparison.Ordinal) &&
                string.Equals(a: entry.PinnedId.Subject, b: subject, comparisonType: StringComparison.Ordinal)
            ) {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The maximum age a claim from <paramref name="entry"/> may be accepted at, or <see langword="null"/> for no ceiling.</summary>
    public TimeSpan? MaximumAgeFor(TrustListEntry entry) => (entry.MaximumAge ?? DefaultMaximumAge);
}

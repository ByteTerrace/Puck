using System.Collections.Frozen;
using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>Whether a trust list entry's key signs claims itself or vouches for others (README.md, "Signed carriage").</summary>
public enum CarriageTrustMode {
    /// <summary>
    /// The pinned key signs claims directly and no chain is walked beneath it — the entry pins one
    /// subject's own signing key, so a claim admitted this way arrives with zero bindings. This is how a
    /// world pins an individual (a friend, a known peer) without trusting the domain that minted them.
    /// It is not a way to shorten a domain's chain: a domain's claims always arrive under a
    /// <see cref="Vouches"/> entry, at exactly two hops.
    /// </summary>
    SignsDirectly,

    /// <summary>
    /// The pinned key is a domain's root and vouches for an issuing key, which vouches for subjects — the
    /// chain, always exactly two bindings deep (README.md: "A chain is at most two hops, because
    /// one cannot hold").
    /// </summary>
    Vouches,
}

/// <summary>
/// One trust list entry: a pinned id, the actual key bytes it names (needed for offline verification — a
/// hash alone cannot verify a signature), whether it signs directly or vouches, and which slots it reaches.
/// "Trusting a domain and pinning a key are one act" (README.md) — a
/// <see cref="CarriageTrustMode.Vouches"/> entry pins the domain's root id, which is what makes the whole
/// chain beneath it trusted.
/// </summary>
/// <param name="PinnedId">The trusted key's id. For <see cref="CarriageTrustMode.Vouches"/> this must be a root id (<see cref="KeyId.IsRoot"/>); for <see cref="CarriageTrustMode.SignsDirectly"/> it must carry a subject, since only a subject key signs claims.</param>
/// <param name="PublicKeySubjectPublicKeyInfo">The pinned key's actual SPKI bytes, authored alongside the id (never fetched).</param>
/// <param name="Mode">Whether this entry signs directly or vouches for a chain.</param>
/// <param name="Reach">
/// The slot names claims admitted by this entry may reach (README.md: a trust entry says "which
/// slots it reaches"). Deny by default — an empty set admits a claim that reaches nothing, and there is
/// deliberately no wildcard, because a wildcard is how a scope silently widens when a game adds a slot.
/// The verification result keeps this set encapsulated and answers only slot-scoped
/// <see cref="CarriageVerifyResult.Admits"/> or <see cref="CarriageVerifyResult.TryGetReplayCommit"/>
/// queries; choosing the affected slot remains the receiving world's policy (invariant 5).
/// </param>
/// <param name="MaximumAge">
/// This entry's own verifier-authored maximum claim age, overriding <see cref="TrustList.DefaultMaximumAge"/>
/// when set. The tighter of this (or the default) and the issuer's own window always governs.
/// </param>
/// <param name="RootBindingMaximumAge">This entry's maximum age for the cold-root-to-issuing binding, independent of claim cadence. Only valid for a vouching entry.</param>
/// <param name="SubjectBindingMaximumAge">This entry's maximum age for issuing-to-subject bindings, independent of both the root binding and claims. Only valid for a vouching entry.</param>
public sealed record TrustListEntry(
    KeyId PinnedId,
    ReadOnlyMemory<byte> PublicKeySubjectPublicKeyInfo,
    CarriageTrustMode Mode,
    IReadOnlySet<string> Reach,
    TimeSpan? MaximumAge,
    TimeSpan? RootBindingMaximumAge = null,
    TimeSpan? SubjectBindingMaximumAge = null
) {
    /// <summary>
    /// Validates that <see cref="PublicKeySubjectPublicKeyInfo"/> actually hashes to <see cref="PinnedId"/>,
    /// that the pinned algorithm is a known signing algorithm (a sealing key can never admit a claim), that
    /// the bytes actually import as a key on the curve that algorithm names, and that the id's shape matches
    /// <see cref="Mode"/>. <see cref="TrustList"/> calls this for every entry at construction, so an unvalidated
    /// list cannot reach the verifier — without that, an entry whose key bytes disagree with its pinned id would
    /// verify against the bytes while the pin sat there decorative.
    /// </summary>
    /// <exception cref="ArgumentException">The entry is not self-consistent.</exception>
    public void Validate() {
        ValidateOptionalDuration(value: MaximumAge, name: nameof(MaximumAge));
        ValidateOptionalDuration(value: RootBindingMaximumAge, name: nameof(RootBindingMaximumAge));
        ValidateOptionalDuration(value: SubjectBindingMaximumAge, name: nameof(SubjectBindingMaximumAge));

        if (!string.Equals(
            a: KeyId.ComputeKeyHash(subjectPublicKeyInfo: PublicKeySubjectPublicKeyInfo.Span),
            b: PinnedId.KeyHash,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(message: "A trust list entry's public key does not hash to its own pinned id — it is not self-certifying.");
        }

        if (
            !CarriageAlgorithms.IsKnown(algorithm: PinnedId.Algorithm) ||
            (CarriageAlgorithms.Resolve(algorithm: PinnedId.Algorithm).Role != CarriageKeyRole.Signing)
        ) {
            throw new ArgumentException(message: $"A trust list entry pins algorithm '{PinnedId.Algorithm}', which is not a carriage SIGNING algorithm — a trust entry can only pin a key that signs.");
        }

        // The two checks above only prove the bytes are self-consistent (they hash to the pinned id) and that the
        // pinned name is a known signing algorithm — neither ever imports the bytes as an actual key. Malformed SPKI
        // bytes (or a well-formed key on the wrong curve for the named algorithm) would otherwise pass this
        // validation and fail only the first time a live connection tried to verify a signature against them — at
        // every runtime connection attempt, forever, rather than once. Import it now, the same way
        // CarriageVerifier.VerifySignature does at actual verification time, and require its curve to match — so a
        // bad key refuses at validation (boot, for a world's admission section — WorldDefinitionValidator's
        // ValidateAdmission runs this through TrustList's constructor), by name, never silently deferred.
        var descriptor = CarriageAlgorithms.Resolve(algorithm: PinnedId.Algorithm);

        using var ecdsa = ECDsa.Create();

        try {
            ecdsa.ImportSubjectPublicKeyInfo(
                source: PublicKeySubjectPublicKeyInfo.Span,
                bytesRead: out _
            );
        } catch (CryptographicException exception) {
            throw new ArgumentException(
                message: $"A trust list entry's public key bytes do not decode as a SubjectPublicKeyInfo usable with algorithm '{PinnedId.Algorithm}' — {exception.Message}",
                innerException: exception
            );
        }

        if (!CarriageCurves.Matches(
            key: ecdsa.ExportParameters(includePrivateParameters: false).Curve,
            expected: descriptor.Curve
        )) {
            throw new ArgumentException(message: $"A trust list entry's public key is not on the curve algorithm '{PinnedId.Algorithm}' names.");
        }

        if (
            (Mode == CarriageTrustMode.Vouches) &&
            !PinnedId.IsRoot
        ) {
            throw new ArgumentException(message: "A vouching trust list entry must pin a root id — the chain it walks is always exactly two hops beneath a root.");
        }

        if (
            (Mode == CarriageTrustMode.SignsDirectly) &&
            (PinnedId.Subject is null)
        ) {
            throw new ArgumentException(message: "A directly-signing trust list entry must pin a SUBJECT key — only a subject key signs claims, and a root or issuing key that signed one would be indistinguishable from a binding.");
        }

        if (
            (Mode == CarriageTrustMode.SignsDirectly) &&
            ((RootBindingMaximumAge is not null) || (SubjectBindingMaximumAge is not null))
        ) {
            throw new ArgumentException(message: "A directly-signing trust list entry cannot author binding-age policy because no binding is walked beneath it.");
        }
    }

    private static void ValidateOptionalDuration(TimeSpan? value, string name) {
        if (
            (value is not null) &&
            ((value.Value <= TimeSpan.Zero) || ((value.Value.Ticks % TimeSpan.TicksPerSecond) != 0))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: name,
                message: "A carriage maximum age must be positive and expressible as whole wire seconds."
            );
        }
    }
}

/// <summary>
/// The authored set of issuers a world accepts (README.md, "Signed carriage"). An empty list
/// honours no foreign claim — deny by default like every other capability; the engine compiles in no root.
/// Every entry is validated at construction, so the verifier never walks an inconsistent list.
/// </summary>
public sealed record TrustList {
    private readonly TrustListEntry[] m_entries;

    /// <summary>Builds and validates a trust list.</summary>
    /// <param name="entries">
    /// The trusted entries. Each is validated, and no two may occupy the same lookup slot — the same
    /// <c>(domain, subject, mode)</c> triple — whether or not they pin the same key. An ambiguous list is a
    /// bug, not a preference order. The list, every SPKI byte sequence, and every reach set are copied;
    /// later mutation of caller-owned storage cannot change verifier policy.
    /// <para>The rule is slot identity rather than key identity because that is what the lookups are keyed
    /// on: <see cref="FindVouchingRoot"/> and <see cref="FindDirectSigner"/> match on domain, subject and
    /// mode and return the first hit, so a second entry in one slot can never be reached and its reach and
    /// maximum age can never govern — exactly the undefined-governance the refusal names. Admitting it
    /// under a key-identity rule would accept a list one of whose entries is silently inert.</para>
    /// <para>The cost is real and worth naming: pinning an old and a new key for one subject — a rotation
    /// overlap — is therefore not expressible today. Supporting it means the lookup must try every entry in
    /// the slot rather than the first, which is a verifier change, not a relaxation of this check.</para>
    /// </param>
    /// <param name="defaultMaximumAge">
    /// The verifier's default maximum claim age when an entry does not override it, or <see langword="null"/>
    /// for no verifier-side ceiling (the issuer's own window is then the whole story).
    /// </param>
    /// <param name="defaultRootBindingMaximumAge">The default maximum age for cold-root-to-issuing bindings, independent of claim cadence. When omitted, <paramref name="defaultMaximumAge"/> is inherited so the existing safety ceiling cannot silently disappear.</param>
    /// <param name="defaultSubjectBindingMaximumAge">The default maximum age for issuing-to-subject bindings. When omitted, <paramref name="defaultMaximumAge"/> is inherited so the existing safety ceiling cannot silently disappear.</param>
    /// <param name="replayAcceptanceHorizon">The positive, whole-second verifier-wide replay horizon. When null, sequenced claims are refused because no safe finite retention bound exists.</param>
    /// <exception cref="ArgumentException">An entry is not self-consistent, or two entries collide.</exception>
    public TrustList(
        IReadOnlyList<TrustListEntry> entries,
        TimeSpan? defaultMaximumAge,
        TimeSpan? defaultRootBindingMaximumAge = null,
        TimeSpan? defaultSubjectBindingMaximumAge = null,
        TimeSpan? replayAcceptanceHorizon = null
    ) {
        ValidateOptionalDuration(value: defaultMaximumAge, name: nameof(defaultMaximumAge));
        ValidateOptionalDuration(value: defaultRootBindingMaximumAge, name: nameof(defaultRootBindingMaximumAge));
        ValidateOptionalDuration(value: defaultSubjectBindingMaximumAge, name: nameof(defaultSubjectBindingMaximumAge));
        ValidateOptionalDuration(value: replayAcceptanceHorizon, name: nameof(replayAcceptanceHorizon));

        var seen = new HashSet<(string Domain, string? Subject, CarriageTrustMode Mode)>();

        // Each entry is copied before its copy is validated: the verifier walks exactly the list, SPKI
        // bytes, and frozen reach set that were validated, so a caller mutating what it passed in after
        // construction cannot put an unvalidated entry in front of the verifier (the same reason
        // SignedCarriageEnvelope copies everything at its boundary).
        var defensiveEntries = new TrustListEntry[entries.Count];

        for (var index = 0; index < defensiveEntries.Length; index++) {
            var source = entries[index];
            var entry = source with {
                PublicKeySubjectPublicKeyInfo = source.PublicKeySubjectPublicKeyInfo.ToArray(),
                Reach = source.Reach.ToFrozenSet(comparer: StringComparer.Ordinal),
            };

            entry.Validate();

            if (!seen.Add(item: (entry.PinnedId.Domain, entry.PinnedId.Subject, entry.Mode))) {
                throw new ArgumentException(message: $"A trust list pins domain '{entry.PinnedId.Domain}' (subject '{(entry.PinnedId.Subject ?? "(none)")}') twice in the same mode — lookup returns the first match, so the second entry's reach and maximum age could never govern, and the list is refused rather than resolved by order.");
            }

            defensiveEntries[index] = entry;
        }

        DefaultMaximumAge = defaultMaximumAge;
        DefaultRootBindingMaximumAge = (defaultRootBindingMaximumAge ?? defaultMaximumAge);
        DefaultSubjectBindingMaximumAge = (defaultSubjectBindingMaximumAge ?? defaultMaximumAge);
        ReplayAcceptanceHorizon = replayAcceptanceHorizon;
        m_entries = defensiveEntries;
        Entries = Array.AsReadOnly(array: defensiveEntries.Select(selector: CreateDetachedEntry).ToArray());
    }

    /// <summary>Gets a detached, read-only snapshot of the trusted entries in authored order.</summary>
    public IReadOnlyList<TrustListEntry> Entries { get; }

    /// <summary>The verifier's default maximum claim age when an entry does not override it, or <see langword="null"/> for no ceiling.</summary>
    public TimeSpan? DefaultMaximumAge { get; }

    /// <summary>The default maximum age for a cold-root-to-issuing binding, or <see langword="null"/> for no verifier-side ceiling beyond the signed window.</summary>
    public TimeSpan? DefaultRootBindingMaximumAge { get; }

    /// <summary>The default maximum age for an issuing-to-subject binding, or <see langword="null"/> for no verifier-side ceiling beyond the signed window.</summary>
    public TimeSpan? DefaultSubjectBindingMaximumAge { get; }

    /// <summary>
    /// The verifier-wide finite horizon for every sequenced claim. It defines signed replay epochs and the
    /// earliest safe mark-retention deadline. <see langword="null"/> means sequenced claims are refused.
    /// </summary>
    public TimeSpan? ReplayAcceptanceHorizon { get; }

    /// <summary>Finds the <see cref="CarriageTrustMode.Vouches"/> entry for a domain, or <see langword="null"/> if that domain is not trusted to vouch.</summary>
    /// <param name="domain">The root fingerprint to look up.</param>
    /// <returns>A detached copy of the matching entry, or <see langword="null"/> when no vouching root matches.</returns>
    public TrustListEntry? FindVouchingRoot(string domain) {
        var entry = FindVouchingRootForVerification(domain: domain);

        return (entry is null) ? null : CreateDetachedEntry(entry: entry);
    }

    /// <summary>Finds the verifier-owned vouching-root snapshot without exposing it outside the assembly.</summary>
    /// <param name="domain">The root fingerprint to look up.</param>
    /// <returns>The verifier-owned matching entry, or <see langword="null"/> when no vouching root matches.</returns>
    internal TrustListEntry? FindVouchingRootForVerification(string domain) {
        foreach (var entry in m_entries) {
            if (
                (entry.Mode == CarriageTrustMode.Vouches) &&
                string.Equals(
                a: entry.PinnedId.Domain,
                b: domain,
                comparisonType: StringComparison.Ordinal
            )
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
    /// <returns>A detached copy of the matching entry, or <see langword="null"/> when no direct signer matches.</returns>
    public TrustListEntry? FindDirectSigner(string domain, string? subject) {
        var entry = FindDirectSignerForVerification(domain: domain, subject: subject);

        return (entry is null) ? null : CreateDetachedEntry(entry: entry);
    }

    /// <summary>Finds the verifier-owned direct-signer snapshot without exposing it outside the assembly.</summary>
    /// <param name="domain">The claim's root fingerprint.</param>
    /// <param name="subject">The claim's subject.</param>
    /// <returns>The verifier-owned matching entry, or <see langword="null"/> when no direct signer matches.</returns>
    internal TrustListEntry? FindDirectSignerForVerification(string domain, string? subject) {
        if (subject is null) {
            return null;
        }

        foreach (var entry in m_entries) {
            if (
                (entry.Mode == CarriageTrustMode.SignsDirectly) &&
                string.Equals(
                a: entry.PinnedId.Domain,
                b: domain,
                comparisonType: StringComparison.Ordinal
            ) &&
                string.Equals(
                a: entry.PinnedId.Subject,
                b: subject,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The maximum age a claim from <paramref name="entry"/> may be accepted at, or <see langword="null"/> for no ceiling.</summary>
    public TimeSpan? MaximumAgeFor(TrustListEntry entry) => (entry.MaximumAge ?? DefaultMaximumAge);

    /// <summary>The maximum age for a root-to-issuing binding admitted by <paramref name="entry"/>.</summary>
    public TimeSpan? RootBindingMaximumAgeFor(TrustListEntry entry) => (entry.RootBindingMaximumAge ?? DefaultRootBindingMaximumAge);

    /// <summary>The maximum age for an issuing-to-subject binding admitted by <paramref name="entry"/>.</summary>
    public TimeSpan? SubjectBindingMaximumAgeFor(TrustListEntry entry) => (entry.SubjectBindingMaximumAge ?? DefaultSubjectBindingMaximumAge);

    private static TrustListEntry CreateDetachedEntry(TrustListEntry entry) => entry with {
        PublicKeySubjectPublicKeyInfo = entry.PublicKeySubjectPublicKeyInfo.ToArray(),
    };

    private static void ValidateOptionalDuration(TimeSpan? value, string name) {
        if (
            (value is not null) &&
            ((value.Value <= TimeSpan.Zero) || ((value.Value.Ticks % TimeSpan.TicksPerSecond) != 0))
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: name,
                message: "A carriage maximum age or replay horizon must be positive and expressible as whole wire seconds."
            );
        }
    }
}

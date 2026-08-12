using System.Security.Cryptography;
using Puck.Carriage;

namespace Puck.World.Protocol;

/// <summary>An identity-door refusal — separate from <see cref="WorldHelloRefusal"/> by construction (see that
/// enum's own remarks): a version mismatch and an identity refusal must never share one spelling.</summary>
public enum WorldAdmissionRefusal : byte {
    /// <summary>This world authors no <c>admission</c> section (or an empty one) — no remote peer can ever verify,
    /// regardless of what it offers.</summary>
    NoAdmissionEntries,

    /// <summary>The trust list built from this document's admission entries could not accept the claim's chain, or
    /// the entries themselves do not form a valid trust list — see the carried detail for the exact reason (window,
    /// signature, algorithm, chain shape, self-certification).</summary>
    ChainRefused,

    /// <summary>The claim verified, but its payload does not carry the exact challenge this connection was issued —
    /// a genuine signature over a stale, foreign, or fabricated nonce.</summary>
    ChallengeMismatch,

    /// <summary>The claim verified against the trust list, but no admission entry names the exact (domain, subject,
    /// mode) that decided it — structurally unreachable given the trust list is built from these same entries, kept
    /// as a named refusal rather than an assert so this stays a refusal rather than a null-reference escape if that
    /// ever stops holding.</summary>
    NoMatchingEntry,

    /// <summary>A traveler arrived from an authenticated federation authority, but no
    /// <see cref="WorldAdmissionTrustMode.FederatedAuthority"/> entry names that authority namespace or
    /// <see cref="WorldAdmissionEntry.AnyAuthority"/>.</summary>
    NoArrivalAuthority,
}

/// <summary>
/// The identity door a remote TCP peer crosses after <see cref="WorldHelloDoor"/>'s protocol-version check succeeds
/// — a challenge-response over <c>Puck.Carriage</c>'s signed-carriage envelopes. <c>Server.WorldTcpHost</c> is the
/// one caller: it mints a fresh challenge per connection attempt, reads back a claim (and, for a
/// <see cref="WorldAdmissionTrustMode.Vouches"/> entry, its two-hop chain) over the socket, and calls
/// <see cref="TryAdmit"/> off the tick thread — this door touches no server state beyond a snapshot of the current
/// document's <see cref="WorldAdmissionEntry"/> rows, which is why it is safe to run there (mirrors
/// <see cref="WorldHelloDoor"/>'s own off-tick-thread reasoning; see <c>WorldTcpHost</c>'s class remarks).
/// <para><b>Freshness is the challenge nonce, not carriage's own audience/sequence machinery.</b> The claim is
/// directed (<see cref="Audience"/>-bound) and carries no sequence, which <c>Puck.Carriage.CarriageVerifier</c>'s
/// own docs call "replayable at its own audience" for a durable carried claim. That is fine here because this is
/// not a durable carried claim: <see cref="NewChallenge"/> mints a fresh cryptographically random nonce per
/// connection attempt, and this door additionally requires the claim's own payload bytes to equal that exact nonce
/// (<see cref="TryAdmit"/>'s own check below — carriage's verifier does not interpret payload content at all, so
/// this is this door's own responsibility, not a gap in the library). A captured claim only ever verifies against
/// the one nonce it was signed over, and a nonce is never reused, so replay against this door is defeated by nonce
/// uniqueness rather than by the audience/sequence story carriage carries for other purposes (durable cross-world
/// claims, issuer re-attestation).</para>
/// </summary>
public static class WorldAdmissionDoor {
    /// <summary>The fixed purpose every admission claim must declare — stops a claim minted for anything else (a
    /// different game, a different purpose within this one) being replayed here as an admission proof.</summary>
    public const string Purpose = "puck.world.tcp-admission";

    /// <summary>The fixed audience every admission claim must be directed at. A placeholder single-audience value
    /// until worlds carry an addressable per-document identity of their own (docs/vision.md's "Authenticating
    /// the game wire" row, and the open "unembodied session authority" question) — today every World process's
    /// admission door is the same addressable thing, so one constant names it honestly rather than inventing
    /// per-world scoping this change does not need yet.</summary>
    public const string Audience = "puck.world";

    /// <summary>The challenge nonce's byte width — generous against a birthday collision across any realistic
    /// connection volume, and small enough to stay a rounding error against the envelope bytes around it.</summary>
    public const int ChallengeBytes = 32;

    /// <summary>The outcome of one identity-door decision.</summary>
    /// <param name="Admitted">Whether the claim verified, matched an authored entry, and carried the challenge.</param>
    /// <param name="Refusal">The named refusal on failure; <see langword="null"/> when admitted.</param>
    /// <param name="Detail">Narration only — never used for control flow.</param>
    /// <param name="Verdict">What the admission authorizes, when admitted; <see langword="null"/> on refusal.</param>
    public readonly record struct AdmissionOutcome(bool Admitted, WorldAdmissionRefusal? Refusal, string Detail, WorldAdmissionVerdict? Verdict) {
        /// <summary>Gets the verified identity's domain, when admitted.</summary>
        public string? Domain => Verdict?.IdentityDomain;

        /// <summary>Gets the verified identity's subject, when admitted — empty, never null, for a
        /// <see cref="WorldAdmissionTrustMode.Vouches"/> entry's own chain-resolved subject, which the entry itself
        /// does not pin.</summary>
        public string? Subject => Verdict?.IdentitySubject;

        /// <summary>Gets the admitting entry's own authored grant templates, when admitted.</summary>
        public IReadOnlyList<WorldAdmissionGrant>? Grants => Verdict?.Templates;

        /// <summary>Builds a refusal outcome.</summary>
        public static AdmissionOutcome Refuse(WorldAdmissionRefusal refusal, string detail) => new(Admitted: false, Refusal: refusal, Detail: detail, Verdict: null);

        /// <summary>Builds an admitted outcome.</summary>
        public static AdmissionOutcome Admit(WorldAdmissionVerdict verdict) => new(Admitted: true, Refusal: null, Detail: string.Empty, Verdict: verdict);
    }

    /// <summary>Mints a fresh, cryptographically random challenge nonce.</summary>
    public static byte[] NewChallenge() => RandomNumberGenerator.GetBytes(count: ChallengeBytes);

    /// <summary>Verifies one connecting peer's presented identity against this world's authored admission entries.</summary>
    /// <param name="entries">The document's current <c>admission</c> rows (a snapshot; see this class's remarks).</param>
    /// <param name="challenge">The exact nonce <see cref="NewChallenge"/> minted for this connection attempt.</param>
    /// <param name="codec">The carriage serialisation the claim/chain bytes were decoded with.</param>
    /// <param name="claim">The peer's signed claim envelope.</param>
    /// <param name="chain">The peer's presented chain (0, 1, or 2 bindings — carriage's own depth rule governs which
    /// counts are even reachable for a given trust entry).</param>
    /// <param name="now">The verification instant — an admission-boundary read, never a mid-tick one; see
    /// <c>Puck.Carriage.CarriageVerifier.VerifyChain</c>'s own remarks on why that is legitimate here.</param>
    public static AdmissionOutcome TryAdmit(
        IReadOnlyList<WorldAdmissionEntry>? entries,
        ReadOnlySpan<byte> challenge,
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope> chain,
        DateTimeOffset now
    ) {
        ArgumentNullException.ThrowIfNull(argument: codec);
        ArgumentNullException.ThrowIfNull(argument: claim);
        ArgumentNullException.ThrowIfNull(argument: chain);

        if ((entries is not { Count: > 0 } rows) || !rows.Any(predicate: static entry => (entry.Mode != WorldAdmissionTrustMode.FederatedAuthority))) {
            return AdmissionOutcome.Refuse(WorldAdmissionRefusal.NoAdmissionEntries, "this world authors no key-bearing admission entries; no remote peer can ever verify");
        }

        TrustList trustList;

        try {
            trustList = BuildTrustList(entries: rows);
        } catch (Exception exception) when (exception is FormatException or ArgumentException or CryptographicException) {
            // The document validator already refuses a malformed admission row at load (base64 shape, algorithm,
            // self-certification), so this is a defensive backstop against a document that somehow reached this
            // door without crossing that gate — but a backstop that throws past its own caller is not one.
            return AdmissionOutcome.Refuse(WorldAdmissionRefusal.ChainRefused, $"this world's authored admission entries do not form a valid trust list: {exception.Message}");
        }

        var result = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trustList,
            now: now,
            expectedPurpose: Purpose,
            expectedAudience: Audience,
            sequenceStore: null
        );

        if (!result.Verified) {
            return AdmissionOutcome.Refuse(WorldAdmissionRefusal.ChainRefused, (result.RefusalReason ?? "refused"));
        }

        if ((claim.PayloadKind != CarriagePayloadKind.Opaque) || !challenge.SequenceEqual(other: claim.PayloadBytes.Span)) {
            return AdmissionOutcome.Refuse(WorldAdmissionRefusal.ChallengeMismatch, "the signed claim does not carry the exact challenge this connection was issued");
        }

        var domain = claim.Header.Domain;
        var subject = claim.Header.Subject;

        if (!TryMatchEntry(entries: rows, domain: domain, subject: subject, verdict: out var matched)) {
            return AdmissionOutcome.Refuse(WorldAdmissionRefusal.NoMatchingEntry, $"claim verified for domain '{domain}' subject '{(subject ?? "(none)")}', but no authored admission entry names it");
        }

        return AdmissionOutcome.Admit(verdict: matched);
    }

    /// <summary>Decides what a traveler handed over by an already-authenticated federation authority is minted.</summary>
    /// <remarks>Performs no cryptographic verification: the caller must already have completed
    /// <c>Server.WorldFederationSecurity</c>'s shared-secret handshake for <paramref name="sourceAuthority"/>, or be
    /// the in-process authority that produced the namespace itself. The verdict's identity fields name the AUTHORITY,
    /// never anything the traveler's own payload asserts.</remarks>
    /// <param name="entries">The document's current <c>admission</c> rows.</param>
    /// <param name="sourceAuthority">The authenticated source-authority namespace.</param>
    /// <param name="verdict">What the arrival is minted, when admitted.</param>
    /// <returns>The named refusal, or <see langword="null"/> when admitted.</returns>
    public static WorldAdmissionRefusal? TryAdmitArrival(IReadOnlyList<WorldAdmissionEntry>? entries, string sourceAuthority, out WorldAdmissionVerdict? verdict) {
        verdict = null;

        if (entries is not { Count: > 0 } rows) {
            return WorldAdmissionRefusal.NoAdmissionEntries;
        }

        WorldAdmissionEntry? wildcard = null;

        foreach (var entry in rows) {
            if (entry.Mode != WorldAdmissionTrustMode.FederatedAuthority) {
                continue;
            }

            if (string.Equals(a: entry.Domain, b: sourceAuthority, comparisonType: StringComparison.Ordinal)) {
                verdict = new WorldAdmissionVerdict(identityDomain: sourceAuthority, identitySubject: string.Empty, templates: entry.Grants);

                return null;
            }

            // A named authority beats the wildcard wherever both are authored, whichever order they appear in.
            wildcard ??= (string.Equals(a: entry.Domain, b: WorldAdmissionEntry.AnyAuthority, comparisonType: StringComparison.Ordinal) ? entry : null);
        }

        if (wildcard is { } any) {
            verdict = new WorldAdmissionVerdict(identityDomain: sourceAuthority, identitySubject: string.Empty, templates: any.Grants);

            return null;
        }

        return WorldAdmissionRefusal.NoArrivalAuthority;
    }

    /// <summary>Matches an already-verified (domain, subject) identity against a set of admission entries, through
    /// the same (domain, subject, mode) predicate <see cref="TryAdmit"/> uses at first connection — factored out so
    /// a later re-authorization (a whole-document rebuild re-checking an already-connected peer against the
    /// current policy rather than the connection-time one — see <c>Server.WorldServer.RemintPeerAdmissionGrants</c>)
    /// can never drift from what a fresh connection would decide. This performs no cryptographic verification at
    /// all — the caller already holds a verified identity; this only asks "does this set of admission entries still
    /// trust it, and if so, what does the matching entry mint."</summary>
    /// <param name="entries">The admission entries to match against — a candidate document's own, when
    /// re-authorizing at rebuild.</param>
    /// <param name="domain">The already-verified identity's domain.</param>
    /// <param name="subject">The already-verified identity's subject (empty/null for a Vouches root's
    /// chain-resolved subject, exactly as <see cref="TryAdmit"/> resolves and stores it).</param>
    /// <param name="verdict">What the matching entry authorizes, when matched.</param>
    /// <returns><see langword="true"/> when an entry matches.</returns>
    public static bool TryMatchEntry(IReadOnlyList<WorldAdmissionEntry>? entries, string? domain, string? subject, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out WorldAdmissionVerdict? verdict) {
        if (entries is { Count: > 0 } rows) {
            foreach (var entry in rows) {
                var matchesDirect = ((entry.Mode == WorldAdmissionTrustMode.SignsDirectly) && string.Equals(a: entry.Domain, b: domain, comparisonType: StringComparison.Ordinal) && string.Equals(a: entry.Subject, b: subject, comparisonType: StringComparison.Ordinal));
                var matchesVouching = ((entry.Mode == WorldAdmissionTrustMode.Vouches) && string.Equals(a: entry.Domain, b: domain, comparisonType: StringComparison.Ordinal));

                if (matchesDirect || matchesVouching) {
                    verdict = new WorldAdmissionVerdict(identityDomain: (domain ?? string.Empty), identitySubject: (subject ?? string.Empty), templates: entry.Grants);

                    return true;
                }
            }
        }

        verdict = null;

        return false;
    }

    // Reach is deliberately empty for every entry: this door never consults Puck.Carriage's own slot-reach
    // mechanism (that vocabulary is for a carried CLAIM's downstream authorization — see TrustListEntry.Reach's own
    // remarks), because this door already carries its OWN, more specific authorization vocabulary —
    // WorldAdmissionEntry.Grants, resolved directly into WorldGrant rows once a peer is admitted. Reusing Reach for
    // that would be the SAME decision expressed twice in two different string vocabularies, free to drift apart.
    private static readonly IReadOnlySet<string> s_noReach = new HashSet<string>(comparer: StringComparer.Ordinal);

    private static TrustList BuildTrustList(IReadOnlyList<WorldAdmissionEntry> entries) {
        var list = new List<TrustListEntry>(capacity: entries.Count);

        foreach (var entry in entries) {
            if (entry.Mode == WorldAdmissionTrustMode.FederatedAuthority) {
                // Keyless by construction: an arrival row authorizes a namespace the federation handshake already
                // authenticated, so it can never verify a carriage claim.
                continue;
            }

            var spki = Convert.FromBase64String(s: entry.PublicKey);
            // The AUTHORED Domain is what is trusted and later matched against — never a recomputed value. For
            // Vouches mode, TrustListEntry.Validate() (called by the TrustList constructor below) refuses an entry
            // whose Domain does not actually equal ComputeKeyHash(spki) (the root-self-certification rule), so an
            // authored typo refuses the WHOLE trust list rather than silently trusting a different root than the
            // document names.
            var pinnedId = new KeyId {
                Algorithm = entry.Algorithm,
                Domain = entry.Domain,
                KeyHash = KeyId.ComputeKeyHash(subjectPublicKeyInfo: spki),
                Subject = ((entry.Mode == WorldAdmissionTrustMode.Vouches) ? null : entry.Subject),
            };

            list.Add(item: new TrustListEntry(
                PinnedId: pinnedId,
                PublicKeySubjectPublicKeyInfo: spki,
                Mode: ((entry.Mode == WorldAdmissionTrustMode.Vouches) ? CarriageTrustMode.Vouches : CarriageTrustMode.SignsDirectly),
                Reach: s_noReach,
                MaximumAge: null
            ));
        }

        return new TrustList(entries: list, defaultMaximumAge: null);
    }
}

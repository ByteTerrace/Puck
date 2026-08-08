using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>
/// The one verify path for everything (docs/world-model.md, "Signed carriage"): a key binding and a claim
/// are both envelopes, and <see cref="VerifyChain"/> is the only entry point either goes through — a
/// binding is verified as a chain hop inside this method, never through a separate code path. Offline by
/// construction: a claim that arrives without its full chain is refused here, never resolved by fetching.
/// </summary>
/// <remarks>
/// <para><b>The depth rule, stated once.</b> A chain has exactly two admissible lengths and no others:</para>
/// <list type="bullet">
/// <item><b>Zero bindings</b>, when the trust list pins the signing subject's own key
/// (<see cref="CarriageTrustMode.SignsDirectly"/>). Nothing is vouched for, so there is nothing to walk.</item>
/// <item><b>Exactly two bindings</b>, when the trust list pins a domain root
/// (<see cref="CarriageTrustMode.Vouches"/>): root-vouches-issuing, then issuing-vouches-subject. A domain
/// with one user still mints both — depth one would put the cold root on every signup, which is the cost
/// the two-hop shape exists to avoid, so the root is additionally refused from vouching for ITSELF as the
/// issuing key (the same cost smuggled through a self-binding).</item>
/// </list>
/// <para>One binding is refused as a broken chain, three as an unbounded one. Two is a number this verifier
/// hard-codes, not an engine it runs — there is no path discovery and no cross-certification.</para>
/// </remarks>
public static class CarriageVerifier {
    /// <summary>
    /// Walks a claim's chain against a trust list and reports whether it verifies.
    /// </summary>
    /// <remarks>
    /// <b>This never runs inside a tick.</b> It is an ADMISSION-boundary operation, and it breaks
    /// world-model invariant 2 twice if it is not: the window is checked against wall-clock Unix seconds
    /// (see <paramref name="now"/>), and a claim carrying a sequence both reads and writes durable storage
    /// through <paramref name="sequenceStore"/>. Verification is also far too slow for 240 Hz. Call it at
    /// the boundary, tape the verdict, and let the simulation read the verdict — never call it from
    /// <c>Step</c>. Nothing in the build enforces this today, so it is stated where the caller is.
    /// </remarks>
    /// <param name="codec">The serialisation the claim and chain were encoded with. Must be the SAME codec the signer used — see <see cref="ICarriageCodec"/>'s remarks.</param>
    /// <param name="claim">The claim envelope — a non-key-binding purpose, signed by a subject key.</param>
    /// <param name="chain">
    /// The bindings the claim travels with, root-to-subject order: <c>chain[0]</c> is the root-vouches-issuing
    /// binding, <c>chain[1]</c> is the issuing-vouches-subject binding. See this class's remarks for the
    /// depth rule — exactly two under a vouching root, exactly zero under a directly-pinned key.
    /// </param>
    /// <param name="trustList">The verifying world's authored trust list. An empty list honours nothing.</param>
    /// <param name="now">
    /// The verification instant, compared as whole Unix seconds against the issuer's authored window.
    /// <b>It must be a value that was tick-stamped and taped at an admission boundary</b> — not a clock
    /// read at this call site. Passing <see cref="DateTimeOffset.UtcNow"/> here satisfies the letter of
    /// "the verifier does not read a clock" while being exactly as nondeterministic: the wall-clock read
    /// simply moved one stack frame out, and two replays of the same tape then disagree about whether a
    /// claim had expired. The parameter exists so the boundary owns the read, not so the caller can do it
    /// on the verifier's behalf (world-model invariant 2: foreign and nondeterministic state enters at one
    /// boundary, "never a mid-tick read of another document, of storage, or of a clock").
    /// </param>
    /// <param name="expectedPurpose">The purpose this call expects the claim to declare. Must be non-blank, and must not be <see cref="CarriagePurposes.KeyBinding"/> — that purpose is refused unconditionally, which is what stops a binding being replayed as a claim.</param>
    /// <param name="expectedAudience">The verifying world's own audience identity, checked against a directed claim's <see cref="CarriageEnvelopeHeader.Audience"/>. A claim with no audience travels anywhere and is checked against <paramref name="sequenceStore"/> instead.</param>
    /// <param name="sequenceStore">
    /// The durable per-(issuer domain, subject) high-water mark seam. Required whenever the claim carries a
    /// sequence — which a DIRECTED claim may also do, since binding an audience defends only against replay
    /// at another world and never against replay at the audience itself (docs/world-model.md: "Same-world
    /// replay needs the sequence either way"). May be <see langword="null"/> only if the caller knows no
    /// claim carrying a sequence will ever be presented; one that does is refused, never silently accepted.
    /// <para>The store's <see cref="ISequenceStore.TryAdvance"/> must be atomic per pair. This method makes
    /// exactly one call into it and treats the returned bool as the verdict, so a store that compares and
    /// advances non-atomically hands two concurrent receivers of one bearer claim two acceptances — and
    /// this specification's other implementation is a web service, where concurrent is the normal case
    /// rather than the exotic one (docs/signed-carriage-wire.md §8).</para>
    /// </param>
    public static CarriageVerifyResult VerifyChain(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string expectedPurpose,
        string? expectedAudience,
        ISequenceStore? sequenceStore
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: expectedPurpose);

        if (string.Equals(a: expectedPurpose, b: CarriagePurposes.KeyBinding, comparisonType: StringComparison.Ordinal)) {
            throw new ArgumentException(message: $"'{CarriagePurposes.KeyBinding}' is a reserved purpose; a caller never expects a claim to declare it.", paramName: nameof(expectedPurpose));
        }

        if (string.Equals(a: claim.Header.Purpose, b: CarriagePurposes.KeyBinding, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: "a key-binding envelope was presented as a claim (purpose replay)");
        }

        if (!string.Equals(a: claim.Header.Purpose, b: expectedPurpose, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: $"purpose mismatch: envelope declares '{claim.Header.Purpose}', caller expected '{expectedPurpose}'");
        }

        // Purpose separates signature USES; payload kind separates what the bytes MEAN. Both are inside the
        // signed portion, and a claim may only be opaque bytes or a sealed payload — a claim whose payload
        // announces itself as a key binding would hand the engine a chain hop dressed as game data.
        if ((claim.PayloadKind != CarriagePayloadKind.Opaque) && (claim.PayloadKind != CarriagePayloadKind.Sealed)) {
            return CarriageVerifyResult.Refuse(reason: $"a claim's payload kind must be opaque or sealed, but this envelope declares '{claim.PayloadKind}'");
        }

        // A direct pin is strictly more specific than a domain root, so it is consulted first: pinning a
        // person's own key is a statement about that person, not about whoever minted them.
        var directTrust = trustList.FindDirectSigner(domain: claim.Header.Domain, subject: claim.Header.Subject);

        if (directTrust is not null) {
            return VerifyDirectlyPinned(
                claim: claim,
                chain: chain,
                entry: directTrust,
                trustList: trustList,
                now: now,
                expectedAudience: expectedAudience,
                sequenceStore: sequenceStore
            );
        }

        return VerifyUnderVouchingRoot(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trustList,
            now: now,
            expectedAudience: expectedAudience,
            sequenceStore: sequenceStore
        );
    }

    /// <summary>
    /// The zero-hop case: the trust list pins this exact subject key, so nothing is vouched for and no
    /// binding may accompany the claim. A chain arriving anyway is refused rather than ignored — accepting
    /// unexamined bindings would let an attacker attach whatever they liked to a claim that verifies.
    /// </summary>
    private static CarriageVerifyResult VerifyDirectlyPinned(
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustListEntry entry,
        TrustList trustList,
        DateTimeOffset now,
        string? expectedAudience,
        ISequenceStore? sequenceStore
    ) {
        if ((chain is not null) && (chain.Count != 0)) {
            return CarriageVerifyResult.Refuse(reason: $"a directly-pinned key vouches for nothing, so its claim must arrive with no bindings, but {chain.Count} arrived");
        }

        if (!string.Equals(a: claim.Header.Algorithm, b: entry.PinnedId.Algorithm, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: $"algorithm confusion: claim declares '{claim.Header.Algorithm}', but the pinned subject key is '{entry.PinnedId.Algorithm}' — the algorithm always comes from the pin, never the envelope");
        }

        if (!VerifySignature(envelope: claim, publicKeySubjectPublicKeyInfo: entry.PublicKeySubjectPublicKeyInfo.Span, pinnedAlgorithm: entry.PinnedId.Algorithm)) {
            return CarriageVerifyResult.Refuse(reason: "claim signature does not verify against the pinned subject key");
        }

        return CheckClaimPolicy(
            claim: claim,
            subject: entry.PinnedId.Subject!,
            entry: entry,
            maximumAge: trustList.MaximumAgeFor(entry: entry),
            now: now,
            expectedAudience: expectedAudience,
            sequenceStore: sequenceStore
        );
    }

    /// <summary>The two-hop case: root vouches for an issuing key, the issuing key vouches for the subject, the subject signed the claim.</summary>
    private static CarriageVerifyResult VerifyUnderVouchingRoot(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string? expectedAudience,
        ISequenceStore? sequenceStore
    ) {
        if ((chain is null) || (chain.Count == 0)) {
            return CarriageVerifyResult.Refuse(reason: "missing chain: the claim arrived with no bindings, and offline verification never fetches the rest");
        }

        if (chain.Count != 2) {
            return CarriageVerifyResult.Refuse(reason: $"broken chain: expected exactly two bindings (root-vouches-issuing, issuing-vouches-subject), found {chain.Count}");
        }

        var rootTrust = trustList.FindVouchingRoot(domain: claim.Header.Domain);

        if (rootTrust is null) {
            return CarriageVerifyResult.Refuse(reason: $"domain '{claim.Header.Domain}' is not a trusted vouching root");
        }

        var maximumAge = trustList.MaximumAgeFor(entry: rootTrust);

        var rootHop = VerifyBindingHop(
            codec: codec,
            binding: chain[0],
            expectedDomain: claim.Header.Domain,
            pinnedSignerId: rootTrust.PinnedId,
            pinnedSignerSpki: rootTrust.PublicKeySubjectPublicKeyInfo.Span,
            now: now,
            maximumAge: maximumAge,
            hopLabel: "root-vouches-issuing binding"
        );

        if (rootHop.Refusal is not null) {
            return CarriageVerifyResult.Refuse(reason: rootHop.Refusal);
        }

        var issuingId = rootHop.TargetId!;

        if (issuingId.Subject is not null) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names a key with a subject, but an issuing key must carry none");
        }

        if (!string.Equals(a: issuingId.Domain, b: claim.Header.Domain, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names a key outside the claim's domain (cross-domain)");
        }

        // A root that vouches for ITSELF as the issuing key is depth one wearing a two-hop costume: the
        // root would still sign a binding per signup, which is exactly the cost the two-hop shape exists to
        // remove, and the warm key would no longer be replaceable without touching what everyone pinned.
        if (string.Equals(a: issuingId.KeyHash, b: rootTrust.PinnedId.KeyHash, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names the root key itself as the issuing key — that is depth one in disguise, and it keeps the cold root signing per subject");
        }

        var issuingHop = VerifyBindingHop(
            codec: codec,
            binding: chain[1],
            expectedDomain: claim.Header.Domain,
            pinnedSignerId: issuingId,
            pinnedSignerSpki: rootHop.TargetSubjectPublicKeyInfo.Span,
            now: now,
            maximumAge: maximumAge,
            hopLabel: "issuing-vouches-subject binding"
        );

        if (issuingHop.Refusal is not null) {
            return CarriageVerifyResult.Refuse(reason: issuingHop.Refusal);
        }

        var subjectId = issuingHop.TargetId!;

        if (subjectId.Subject is null) {
            return CarriageVerifyResult.Refuse(reason: "issuing-vouches-subject binding names a key with no subject, but a subject key must carry the platform user id");
        }

        if (!string.Equals(a: subjectId.Domain, b: claim.Header.Domain, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: "issuing-vouches-subject binding names a key outside the claim's domain (cross-domain)");
        }

        if (!string.Equals(a: claim.Header.Subject, b: subjectId.Subject, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: "the claim's subject does not match the chain's subject key");
        }

        if (!string.Equals(a: claim.Header.Algorithm, b: subjectId.Algorithm, comparisonType: StringComparison.Ordinal)) {
            return CarriageVerifyResult.Refuse(reason: $"algorithm confusion: claim declares '{claim.Header.Algorithm}', but the pinned subject key is '{subjectId.Algorithm}' — the algorithm always comes from the pin, never the envelope");
        }

        if (!VerifySignature(envelope: claim, publicKeySubjectPublicKeyInfo: issuingHop.TargetSubjectPublicKeyInfo.Span, pinnedAlgorithm: subjectId.Algorithm)) {
            return CarriageVerifyResult.Refuse(reason: "claim signature does not verify against the pinned subject key");
        }

        return CheckClaimPolicy(
            claim: claim,
            subject: subjectId.Subject,
            entry: rootTrust,
            maximumAge: maximumAge,
            now: now,
            expectedAudience: expectedAudience,
            sequenceStore: sequenceStore
        );
    }

    /// <summary>
    /// The policy tail both depths share: window, audience, and sequence. Audience and sequence are
    /// INDEPENDENT rather than exclusive — the doc's table pairs them because portability and statelessness
    /// are the authored trade, but it also says same-world replay needs the sequence either way, so a
    /// directed claim that carries one is checked against the mark exactly as a bearer claim is. Only the
    /// bearer case REQUIRES a sequence, because a claim with neither an audience nor a mark has no replay
    /// defence at all.
    /// </summary>
    private static CarriageVerifyResult CheckClaimPolicy(
        SignedCarriageEnvelope claim,
        string subject,
        TrustListEntry entry,
        TimeSpan? maximumAge,
        DateTimeOffset now,
        string? expectedAudience,
        ISequenceStore? sequenceStore
    ) {
        var windowFailure = CheckWindow(header: claim.Header, now: now, maximumAge: maximumAge);

        if (windowFailure is not null) {
            return CarriageVerifyResult.Refuse(reason: windowFailure);
        }

        if (claim.Header.Audience is not null) {
            if (!string.Equals(a: claim.Header.Audience, b: expectedAudience, comparisonType: StringComparison.Ordinal)) {
                return CarriageVerifyResult.Refuse(reason: $"audience mismatch: claim is bound to '{claim.Header.Audience}', this verifier is '{(expectedAudience ?? "(none)")}'");
            }
        } else if (claim.Header.Sequence is null) {
            return CarriageVerifyResult.Refuse(reason: "bearer claim (no audience) carries no sequence number");
        }

        if (claim.Header.Sequence is not null) {
            if (sequenceStore is null) {
                return CarriageVerifyResult.Refuse(reason: "the claim carries a sequence and no sequence store was supplied — a declared replay defence is never skipped because the receiver has nowhere to record it");
            }

            // One call, not compare-then-write: the store decides, atomically. Splitting it would put a
            // check-then-act race on the one check whose entire job is to make a claim usable once.
            //
            // A store that is unreachable, unreadable, or cannot durably record the advance REFUSES the
            // claim — it does not propagate (docs/signed-carriage-wire.md §8). "Accept because the store is
            // down" inverts the one rule the mark exists for: an unavailable store means the declared replay
            // defence is absent, and an absent replay defence is already a refusal three lines above. An
            // indeterminate outcome — a timeout, an aborted transaction, a lock nobody won — is not an
            // acceptance either. Nothing was consumed, so the same claim may be presented again later.
            bool advanced;

            try {
                advanced = sequenceStore.TryAdvance(domain: claim.Header.Domain, subject: subject, sequence: claim.Header.Sequence.Value);
            } catch (Exception exception) {
                return CarriageVerifyResult.Refuse(reason: $"the sequence mark store could not decide: {exception.GetType().Name}: {exception.Message} — an unavailable or indeterminate mark store refuses, never admits");
            }

            if (!advanced) {
                return CarriageVerifyResult.Refuse(reason: $"sequence replay: claim sequence {claim.Header.Sequence.Value} does not strictly exceed the recorded high-water mark");
            }
        }

        return CarriageVerifyResult.Accept(reach: entry.Reach);
    }

    /// <summary>The outcome of verifying one binding hop: either a refusal, or the vouched-for key's id and actual key bytes to carry into the next hop.</summary>
    private readonly record struct BindingHopResult(string? Refusal, KeyId? TargetId, ReadOnlyMemory<byte> TargetSubjectPublicKeyInfo);

    private static BindingHopResult VerifyBindingHop(
        ICarriageCodec codec,
        SignedCarriageEnvelope binding,
        string expectedDomain,
        KeyId pinnedSignerId,
        ReadOnlySpan<byte> pinnedSignerSpki,
        DateTimeOffset now,
        TimeSpan? maximumAge,
        string hopLabel
    ) {
        if (!string.Equals(a: binding.Header.Purpose, b: CarriagePurposes.KeyBinding, comparisonType: StringComparison.Ordinal)) {
            return new BindingHopResult(Refusal: $"{hopLabel} does not declare purpose '{CarriagePurposes.KeyBinding}'", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (!string.Equals(a: binding.Header.Domain, b: expectedDomain, comparisonType: StringComparison.Ordinal)) {
            return new BindingHopResult(Refusal: $"{hopLabel} is minted for a different domain than the claim (cross-domain)", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (!string.Equals(a: binding.Header.Subject, b: pinnedSignerId.Subject, comparisonType: StringComparison.Ordinal)) {
            return new BindingHopResult(Refusal: $"{hopLabel}'s declared signer subject does not match the pinned signer", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (!string.Equals(a: binding.Header.Algorithm, b: pinnedSignerId.Algorithm, comparisonType: StringComparison.Ordinal)) {
            return new BindingHopResult(Refusal: $"algorithm confusion: {hopLabel} declares '{binding.Header.Algorithm}', but the pinned signer is '{pinnedSignerId.Algorithm}'", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (binding.PayloadKind != CarriagePayloadKind.KeyBinding) {
            return new BindingHopResult(Refusal: $"{hopLabel}'s payload is not a key binding", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        // Signature first, payload second: everything below this line reads attacker-supplied bytes, and
        // the only thing that makes them safe to read is that the pinned signer committed to them.
        if (!VerifySignature(envelope: binding, publicKeySubjectPublicKeyInfo: pinnedSignerSpki, pinnedAlgorithm: pinnedSignerId.Algorithm)) {
            return new BindingHopResult(Refusal: $"{hopLabel} signature does not verify against the pinned signer key", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        var windowFailure = CheckWindow(header: binding.Header, now: now, maximumAge: maximumAge);

        if (windowFailure is not null) {
            return new BindingHopResult(Refusal: $"{hopLabel}: {windowFailure}", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        KeyBindingPayload payload;

        try {
            payload = codec.DecodeKeyBindingPayload(bytes: binding.PayloadBytes.Span);
        } catch (FormatException exception) {
            return new BindingHopResult(Refusal: $"{hopLabel}'s payload does not decode: {exception.Message}", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (!CarriageAlgorithms.IsKnown(algorithm: payload.TargetId.Algorithm)) {
            return new BindingHopResult(Refusal: $"{hopLabel} vouches for a key naming algorithm '{payload.TargetId.Algorithm}', which is not a carriage algorithm", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        if (!payload.IsSelfCertifying) {
            return new BindingHopResult(Refusal: $"{hopLabel}'s payload key does not hash to its own claimed id (not self-certifying)", TargetId: null, TargetSubjectPublicKeyInfo: default);
        }

        return new BindingHopResult(Refusal: null, TargetId: payload.TargetId, TargetSubjectPublicKeyInfo: payload.PublicKeySubjectPublicKeyInfo);
    }

    /// <summary>
    /// Verifies an envelope's signature. The algorithm rule lives here: <paramref name="pinnedAlgorithm"/>
    /// — taken from the trust list or a prior hop's binding, NEVER from <paramref name="envelope"/>'s own
    /// header — is the only thing that selects the hash algorithm this method verifies with. The caller has
    /// already checked <c>envelope.Header.Algorithm == pinnedAlgorithm</c> as a separate consistency check;
    /// this method does not re-read the header's algorithm at all. The imported key's own curve is checked
    /// against the pinned algorithm's curve too, so a key on some other curve cannot be smuggled in behind
    /// a name that promises P-256.
    /// </summary>
    /// <remarks>
    /// The signing input is <see cref="SignedCarriageEnvelope.SignedPortion"/> — the bytes that ARRIVED —
    /// and never a re-encoding of the parsed model (docs/signed-carriage-wire.md §2). Re-encoding would
    /// make this method verify a claim nobody signed whenever a decoder anywhere accepted two wire forms
    /// for one model: the forged bytes would be normalised away before the signature ever saw them, and
    /// every such laxity would silently become an accepted alternate encoding of a real claim.
    /// </remarks>
    private static bool VerifySignature(SignedCarriageEnvelope envelope, ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo, string pinnedAlgorithm) {
        var descriptor = CarriageAlgorithms.Resolve(algorithm: pinnedAlgorithm);

        if ((descriptor.Role != CarriageKeyRole.Signing) || (descriptor.SignatureHash is null)) {
            return false;
        }

        using var ecdsa = ECDsa.Create();

        try {
            ecdsa.ImportSubjectPublicKeyInfo(source: publicKeySubjectPublicKeyInfo, bytesRead: out _);
        } catch (CryptographicException) {
            return false;
        }

        if (!CarriageCurves.Matches(key: ecdsa.ExportParameters(includePrivateParameters: false).Curve, expected: descriptor.Curve)) {
            return false;
        }

        // IEEE P1363 fixed-field r‖s only: .NET refuses any length other than twice the field width, so no
        // alternate DER encoding of the same (r, s) is even a candidate. What this does NOT buy is signature
        // uniqueness — (r, s) and (r, n-s) are both valid for the same message, so signature bytes are never
        // an identity. Replay is defended by the sequence mark and the audience, never by signature equality.
        return ecdsa.VerifyData(data: envelope.SignedPortion.Span, signature: envelope.Signature.Span, hashAlgorithm: descriptor.SignatureHash.Value, signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Applies both ends of the validity rule (docs/world-model.md: "Validity is authored at both ends").
    /// There is deliberately NO clock-skew tolerance: an issuer that wants slack backdates
    /// <see cref="CarriageEnvelopeHeader.NotBefore"/>, which is authored, auditable and travels signed —
    /// unlike a verifier-side grace window, which every verifier would size differently and which would
    /// silently widen every window in the system by twice its size.
    /// </summary>
    private static string? CheckWindow(CarriageEnvelopeHeader header, DateTimeOffset now, TimeSpan? maximumAge) {
        var nowSeconds = now.ToUnixTimeSeconds();

        if (header.NotAfter < header.NotBefore) {
            return "malformed window: notAfter precedes notBefore";
        }

        if (nowSeconds < header.NotBefore) {
            return "not yet valid: before the issuer's window opens";
        }

        if (nowSeconds > header.NotAfter) {
            return "expired: past the issuer's own window";
        }

        if (maximumAge is not null) {
            var age = TimeSpan.FromSeconds(value: (nowSeconds - header.NotBefore));

            if (age > maximumAge.Value) {
                return $"expired: age {age} exceeds the verifier's maximum age {maximumAge.Value}";
            }
        }

        return null;
    }
}

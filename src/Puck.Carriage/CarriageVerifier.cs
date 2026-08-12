using System.Security.Cryptography;

namespace Puck.Carriage;

/// <summary>
/// The one verify path for everything (README.md, "Signed carriage"): a key binding and a claim
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
/// the two-hop shape exists to avoid, so the root is additionally refused from vouching for itself as the
/// issuing key (the same cost smuggled through a self-binding).</item>
/// </list>
/// <para>One binding is refused as a broken chain, three as an unbounded one. Two is a number this verifier
/// hard-codes, not an engine it runs — there is no path discovery and no cross-certification.</para>
/// </remarks>
internal static class CarriageVerifier {
    /// <summary>
    /// Walks a claim's chain against a trust list and reports whether it verifies.
    /// </summary>
    /// <remarks>
    /// <b>This never runs inside a tick.</b> It is an admission-boundary operation, and it breaks
    /// world-model invariant 2 if it is not: the window is checked against wall-clock Unix seconds
    /// (see <paramref name="now"/>). Verification is also far too slow for 240 Hz. Call it at
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
    /// <param name="expectedAudience">The verifying world's own audience identity, checked against a directed claim's <see cref="CarriageEnvelopeHeader.Audience"/>.</param>
    /// <param name="profile">The public facade's receiver-selected profile, used to stop an authenticated binding from selecting a disabled algorithm for the following hop; <see langword="null"/> only for this assembly's adversarial harness.</param>
    public static CarriageVerifyResult VerifyChain(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string expectedPurpose,
        string? expectedAudience,
        CarriageConformanceProfile? profile = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: expectedPurpose);

        if (!HasCoherentProjection(
            codec: codec,
            envelope: claim,
            label: "claim",
            refusal: out var projectionRefusal
        )) {
            return CarriageVerifyResult.Refuse(reason: projectionRefusal!);
        }

        if (string.Equals(
            a: expectedPurpose,
            b: CarriagePurposes.KeyBinding,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new ArgumentException(
                message: $"'{CarriagePurposes.KeyBinding}' is a reserved purpose; a caller never expects a claim to declare it.",
                paramName: nameof(expectedPurpose)
            );
        }

        if (string.Equals(
            a: claim.Header.Purpose,
            b: CarriagePurposes.KeyBinding,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: "a key-binding envelope was presented as a claim (purpose replay)");
        }

        if (!string.Equals(
            a: claim.Header.Purpose,
            b: expectedPurpose,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: $"purpose mismatch: envelope declares '{claim.Header.Purpose}', caller expected '{expectedPurpose}'");
        }

        // Purpose separates signature USES; payload kind separates what the bytes MEAN. Both are inside the
        // signed portion, and a claim may only be opaque bytes or a sealed payload — a claim whose payload
        // announces itself as a key binding would hand the engine a chain hop dressed as game data.
        if (
            (claim.PayloadKind != CarriagePayloadKind.Opaque) &&
            (claim.PayloadKind != CarriagePayloadKind.Sealed)
        ) {
            return CarriageVerifyResult.Refuse(reason: $"a claim's payload kind must be opaque or sealed, but this envelope declares '{claim.PayloadKind}'");
        }

        // A direct pin is strictly more specific than a domain root, so it is consulted first: pinning a
        // person's own key is a statement about that person, not about whoever minted them.
        var directTrust = trustList.FindDirectSigner(
            domain: claim.Header.Domain,
            subject: claim.Header.Subject
        );

        if (directTrust is not null) {
            return VerifyDirectlyPinned(
                codec: codec,
                claim: claim,
                chain: chain,
                entry: directTrust,
                trustList: trustList,
                now: now,
                expectedAudience: expectedAudience,
                profile: profile
            );
        }

        return VerifyUnderVouchingRoot(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trustList,
            now: now,
            expectedAudience: expectedAudience,
            profile: profile
        );
    }

    /// <summary>
    /// The zero-hop case: the trust list pins this exact subject key, so nothing is vouched for and no
    /// binding may accompany the claim. A chain arriving anyway is refused rather than ignored — accepting
    /// unexamined bindings would let an attacker attach whatever they liked to a claim that verifies.
    /// </summary>
    private static CarriageVerifyResult VerifyDirectlyPinned(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustListEntry entry,
        TrustList trustList,
        DateTimeOffset now,
        string? expectedAudience,
        CarriageConformanceProfile? profile
    ) {
        if (
            (chain is not null) &&
            (chain.Count != 0)
        ) {
            return CarriageVerifyResult.Refuse(reason: $"a directly-pinned key vouches for nothing, so its claim must arrive with no bindings, but {chain.Count} arrived");
        }

        if (!string.Equals(
            a: claim.Header.Algorithm,
            b: entry.PinnedId.Algorithm,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: $"algorithm confusion: claim declares '{claim.Header.Algorithm}', but the pinned subject key is '{entry.PinnedId.Algorithm}' — the algorithm always comes from the pin, never the envelope");
        }

        if (!VerifySignature(
            envelope: claim,
            publicKeySubjectPublicKeyInfo: entry.PublicKeySubjectPublicKeyInfo.Span,
            pinnedAlgorithm: entry.PinnedId.Algorithm
        )) {
            return CarriageVerifyResult.Refuse(reason: "claim signature does not verify against the pinned subject key");
        }

        var policy = CheckClaimPolicy(
            claim: claim,
            subject: entry.PinnedId.Subject!,
            entry: entry,
            maximumAge: trustList.MaximumAgeFor(entry: entry),
            replayAcceptanceHorizon: trustList.ReplayAcceptanceHorizon,
            now: now,
            expectedAudience: expectedAudience
        );

        if (!policy.Verified) {
            return policy;
        }

        var payloadRefusal = ValidateAuthenticatedClaimPayload(
            codec: codec,
            claim: claim
        );

        return ((payloadRefusal is null)
            ? policy
            : CarriageVerifyResult.Refuse(reason: payloadRefusal));
    }

    /// <summary>The two-hop case: root vouches for an issuing key, the issuing key vouches for the subject, the subject signed the claim.</summary>
    private static CarriageVerifyResult VerifyUnderVouchingRoot(
        ICarriageCodec codec,
        SignedCarriageEnvelope claim,
        IReadOnlyList<SignedCarriageEnvelope>? chain,
        TrustList trustList,
        DateTimeOffset now,
        string? expectedAudience,
        CarriageConformanceProfile? profile
    ) {
        if (
            (chain is null) ||
            (chain.Count == 0)
        ) {
            return CarriageVerifyResult.Refuse(reason: "missing chain: the claim arrived with no bindings, and offline verification never fetches the rest");
        }

        if (chain.Count != 2) {
            return CarriageVerifyResult.Refuse(reason: $"broken chain: expected exactly two bindings (root-vouches-issuing, issuing-vouches-subject), found {chain.Count}");
        }

        var rootTrust = trustList.FindVouchingRoot(domain: claim.Header.Domain);

        if (rootTrust is null) {
            return CarriageVerifyResult.Refuse(reason: $"domain '{claim.Header.Domain}' is not a trusted vouching root");
        }

        var rootHop = VerifyBindingHop(
            codec: codec,
            binding: chain[0],
            expectedDomain: claim.Header.Domain,
            pinnedSignerId: rootTrust.PinnedId,
            pinnedSignerSpki: rootTrust.PublicKeySubjectPublicKeyInfo.Span,
            now: now,
            maximumAge: trustList.RootBindingMaximumAgeFor(entry: rootTrust),
            hopLabel: "root-vouches-issuing binding",
            profile: profile
        );

        if (rootHop.Refusal is not null) {
            return CarriageVerifyResult.Refuse(reason: rootHop.Refusal);
        }

        var issuingId = rootHop.TargetId!;

        if (issuingId.Subject is not null) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names a key with a subject, but an issuing key must carry none");
        }

        if (!string.Equals(
            a: issuingId.Domain,
            b: claim.Header.Domain,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names a key outside the claim's domain (cross-domain)");
        }

        // A root that vouches for ITSELF as the issuing key is depth one wearing a two-hop costume: the
        // root would still sign a binding per signup, which is exactly the cost the two-hop shape exists to
        // remove, and the warm key would no longer be replaceable without touching what everyone pinned.
        if (string.Equals(
            a: issuingId.KeyHash,
            b: rootTrust.PinnedId.KeyHash,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: "root-vouches-issuing binding names the root key itself as the issuing key — that is depth one in disguise, and it keeps the cold root signing per subject");
        }

        var issuingHop = VerifyBindingHop(
            codec: codec,
            binding: chain[1],
            expectedDomain: claim.Header.Domain,
            pinnedSignerId: issuingId,
            pinnedSignerSpki: rootHop.TargetSubjectPublicKeyInfo.Span,
            now: now,
            maximumAge: trustList.SubjectBindingMaximumAgeFor(entry: rootTrust),
            hopLabel: "issuing-vouches-subject binding",
            profile: profile
        );

        if (issuingHop.Refusal is not null) {
            return CarriageVerifyResult.Refuse(reason: issuingHop.Refusal);
        }

        var subjectId = issuingHop.TargetId!;

        if (subjectId.Subject is null) {
            return CarriageVerifyResult.Refuse(reason: "issuing-vouches-subject binding names a key with no subject, but a subject key must carry the platform user id");
        }

        if (!string.Equals(
            a: subjectId.Domain,
            b: claim.Header.Domain,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: "issuing-vouches-subject binding names a key outside the claim's domain (cross-domain)");
        }

        if (!string.Equals(
            a: claim.Header.Subject,
            b: subjectId.Subject,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: "the claim's subject does not match the chain's subject key");
        }

        if (!string.Equals(
            a: claim.Header.Algorithm,
            b: subjectId.Algorithm,
            comparisonType: StringComparison.Ordinal
        )) {
            return CarriageVerifyResult.Refuse(reason: $"algorithm confusion: claim declares '{claim.Header.Algorithm}', but the pinned subject key is '{subjectId.Algorithm}' — the algorithm always comes from the pin, never the envelope");
        }

        if (!VerifySignature(
            envelope: claim,
            publicKeySubjectPublicKeyInfo: issuingHop.TargetSubjectPublicKeyInfo.Span,
            pinnedAlgorithm: subjectId.Algorithm
        )) {
            return CarriageVerifyResult.Refuse(reason: "claim signature does not verify against the pinned subject key");
        }

        var policy = CheckClaimPolicy(
            claim: claim,
            subject: subjectId.Subject,
            entry: rootTrust,
            maximumAge: trustList.MaximumAgeFor(entry: rootTrust),
            replayAcceptanceHorizon: trustList.ReplayAcceptanceHorizon,
            now: now,
            expectedAudience: expectedAudience
        );

        if (!policy.Verified) {
            return policy;
        }

        var payloadRefusal = ValidateAuthenticatedClaimPayload(
            codec: codec,
            claim: claim
        );

        return ((payloadRefusal is null)
            ? policy
            : CarriageVerifyResult.Refuse(reason: payloadRefusal));
    }

    /// <summary>
    /// Validates a claim payload whose signature has already authenticated its bytes. In particular, an
    /// attacker-controlled sealed payload is not decoded and its ephemeral EC key is not imported before
    /// the signature check succeeds.
    /// </summary>
    private static string? ValidateAuthenticatedClaimPayload(ICarriageCodec codec, SignedCarriageEnvelope claim) {
        if (claim.PayloadKind != CarriagePayloadKind.Sealed) {
            return null;
        }

        try {
            _ = codec.DecodeSealedPayload(bytes: claim.PayloadSpan);

            return null;
        } catch (Exception exception) when (
            (exception is FormatException) ||
            (exception is ArgumentException) ||
            (exception is NotSupportedException) ||
            (exception is CryptographicException)
        ) {
            return $"sealed claim payload is malformed: {exception.Message}";
        }
    }

    /// <summary>
    /// The policy tail both depths share: window, audience, and sequence. Audience and sequence are
    /// independent rather than exclusive — the doc's table pairs them because portability and statelessness
    /// are the authored trade, but it also says same-world replay needs the sequence either way, so a
    /// directed claim that carries one is checked against the mark exactly as a bearer claim is. Only the
    /// bearer case requires a sequence, because a claim with neither an audience nor a mark has no replay
    /// defence at all.
    /// </summary>
    private static CarriageVerifyResult CheckClaimPolicy(
        SignedCarriageEnvelope claim,
        string subject,
        TrustListEntry entry,
        TimeSpan? maximumAge,
        TimeSpan? replayAcceptanceHorizon,
        DateTimeOffset now,
        string? expectedAudience
    ) {
        var effectiveMaximumAge = Minimum(
            left: maximumAge,
            right: (claim.Header.Sequence is null) ? null : replayAcceptanceHorizon
        );
        var windowFailure = CheckWindow(
            header: claim.Header,
            now: now,
            maximumAge: effectiveMaximumAge
        );

        if (windowFailure is not null) {
            return CarriageVerifyResult.Refuse(reason: windowFailure);
        }

        if (claim.Header.Audience is not null) {
            if (!string.Equals(
                a: claim.Header.Audience,
                b: expectedAudience,
                comparisonType: StringComparison.Ordinal
            )) {
                return CarriageVerifyResult.Refuse(reason: $"audience mismatch: claim is bound to '{claim.Header.Audience}', this verifier is '{(expectedAudience ?? "(none)")}'");
            }
        } else if (claim.Header.Sequence is null) {
            return CarriageVerifyResult.Refuse(reason: "bearer claim (no audience) carries no sequence number");
        }

        ReplayCommitRequirement? replayCommit = null;

        if (claim.Header.Sequence is not null) {
            if (replayAcceptanceHorizon is null) {
                return CarriageVerifyResult.Refuse(reason: "the claim carries a sequence but this verifier has no finite replay-acceptance horizon — safe mark eviction cannot be derived");
            }

            if (!TryDeriveReplayEpoch(
                notBefore: claim.Header.NotBefore,
                notAfter: claim.Header.NotAfter,
                horizon: replayAcceptanceHorizon.Value,
                epochStartUnixSeconds: out var epochStartUnixSeconds,
                retainThroughUnixSeconds: out var retainThroughUnixSeconds,
                refusal: out var epochRefusal
            )) {
                return CarriageVerifyResult.Refuse(reason: epochRefusal!);
            }

            replayCommit = new ReplayCommitRequirement(
                Domain: claim.Header.Domain,
                Subject: subject,
                EpochStartUnixSeconds: epochStartUnixSeconds,
                RetainThroughUnixSeconds: retainThroughUnixSeconds,
                Sequence: claim.Header.Sequence.Value
            );
        }

        return CarriageVerifyResult.Accept(
            reach: entry.Reach,
            replayCommit: replayCommit
        );
    }

    private static TimeSpan? Minimum(TimeSpan? left, TimeSpan? right) {
        if (left is null) {
            return right;
        }

        if (right is null) {
            return left;
        }

        return (left.Value <= right.Value) ? left : right;
    }

    /// <summary>
    /// Derives a replay epoch solely from signed <c>notBefore</c>. Epoch width is the verifier-wide
    /// horizon. Keeping an epoch mark through the end of the following epoch is sufficient because a claim
    /// beginning in this epoch has a window no longer than one horizon and is also subject to that same
    /// maximum-age ceiling. A far-future lower sequence belongs to a later epoch instead of reopening this
    /// one after eviction.
    /// </summary>
    private static bool TryDeriveReplayEpoch(
        long notBefore,
        long notAfter,
        TimeSpan horizon,
        out long epochStartUnixSeconds,
        out long retainThroughUnixSeconds,
        out string? refusal
    ) {
        var horizonSeconds = checked((long)horizon.TotalSeconds);
        var signedWindowSeconds = ((decimal)notAfter - notBefore);

        epochStartUnixSeconds = default;
        retainThroughUnixSeconds = default;

        if (signedWindowSeconds > horizonSeconds) {
            refusal = $"sequenced claim window {signedWindowSeconds} seconds exceeds the verifier's replay-acceptance horizon of {horizonSeconds} seconds";

            return false;
        }

        var epoch = Math.DivRem(
            a: notBefore,
            b: horizonSeconds,
            result: out var remainder
        );

        if (remainder < 0) {
            epoch--;
        }

        try {
            epochStartUnixSeconds = checked(epoch * horizonSeconds);
            retainThroughUnixSeconds = checked(epochStartUnixSeconds + checked((2 * horizonSeconds) - 1));
        } catch (OverflowException) {
            refusal = "sequenced claim window lies outside the representable replay-epoch retention range";

            return false;
        }

        refusal = null;

        return true;
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
        string hopLabel,
        CarriageConformanceProfile? profile
    ) {
        if (!HasCoherentProjection(
            codec: codec,
            envelope: binding,
            label: hopLabel,
            refusal: out var projectionRefusal
        )) {
            return new BindingHopResult(
                Refusal: projectionRefusal,
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!string.Equals(
            a: binding.Header.Purpose,
            b: CarriagePurposes.KeyBinding,
            comparisonType: StringComparison.Ordinal
        )) {
            return new BindingHopResult(
                Refusal: $"{hopLabel} does not declare purpose '{CarriagePurposes.KeyBinding}'",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!string.Equals(
            a: binding.Header.Domain,
            b: expectedDomain,
            comparisonType: StringComparison.Ordinal
        )) {
            return new BindingHopResult(
                Refusal: $"{hopLabel} is minted for a different domain than the claim (cross-domain)",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!string.Equals(
            a: binding.Header.Subject,
            b: pinnedSignerId.Subject,
            comparisonType: StringComparison.Ordinal
        )) {
            return new BindingHopResult(
                Refusal: $"{hopLabel}'s declared signer subject does not match the pinned signer",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!string.Equals(
            a: binding.Header.Algorithm,
            b: pinnedSignerId.Algorithm,
            comparisonType: StringComparison.Ordinal
        )) {
            return new BindingHopResult(
                Refusal: $"algorithm confusion: {hopLabel} declares '{binding.Header.Algorithm}', but the pinned signer is '{pinnedSignerId.Algorithm}'",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (binding.PayloadKind != CarriagePayloadKind.KeyBinding) {
            return new BindingHopResult(
                Refusal: $"{hopLabel}'s payload is not a key binding",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        // Signature first, payload second: everything below this line reads attacker-supplied bytes, and
        // the only thing that makes them safe to read is that the pinned signer committed to them.
        if (!VerifySignature(
            envelope: binding,
            publicKeySubjectPublicKeyInfo: pinnedSignerSpki,
            pinnedAlgorithm: pinnedSignerId.Algorithm
        )) {
            return new BindingHopResult(
                Refusal: $"{hopLabel} signature does not verify against the pinned signer key",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        var windowFailure = CheckWindow(
            header: binding.Header,
            now: now,
            maximumAge: maximumAge
        );

        if (windowFailure is not null) {
            return new BindingHopResult(
                Refusal: $"{hopLabel}: {windowFailure}",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        KeyBindingPayload payload;

        try {
            payload = codec.DecodeKeyBindingPayload(bytes: binding.PayloadSpan);
        } catch (FormatException exception) {
            return new BindingHopResult(
                Refusal: $"{hopLabel}'s payload does not decode: {exception.Message}",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!CarriageAlgorithms.IsKnown(algorithm: payload.TargetId.Algorithm)) {
            return new BindingHopResult(
                Refusal: $"{hopLabel} vouches for a key naming algorithm '{payload.TargetId.Algorithm}', which is not a carriage algorithm",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (
            (profile is not null) &&
            !profile.TryValidateKeyBindingPayload(
                payload: payload,
                label: hopLabel,
                refusal: out var profileRefusal
            )
        ) {
            return new BindingHopResult(
                Refusal: profileRefusal,
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        if (!payload.IsSelfCertifying) {
            return new BindingHopResult(
                Refusal: $"{hopLabel}'s payload key does not hash to its own claimed id (not self-certifying)",
                TargetId: null,
                TargetSubjectPublicKeyInfo: default
            );
        }

        return new BindingHopResult(
            Refusal: null,
            TargetId: payload.TargetId,
            TargetSubjectPublicKeyInfo: payload.PublicKeySubjectPublicKeyInfo
        );
    }

    /// <summary>
    /// Defends the verifier's object boundary as well as its wire boundary. Raw signed-portion construction
    /// is assembly-only, but every envelope consumed here must still prove that its parsed projection is the
    /// canonical decoding of the bytes whose signature will be checked. Re-encoding is used only for this
    /// equality guard; <see cref="VerifySignature"/> continues to authenticate the original bytes.
    /// </summary>
    private static bool HasCoherentProjection(
        ICarriageCodec codec,
        SignedCarriageEnvelope envelope,
        string label,
        out string? refusal
    ) {
        try {
            var projectedBytes = codec.EncodeSignedPortion(
                header: envelope.Header,
                payloadKind: envelope.PayloadKind,
                payloadBytes: envelope.PayloadSpan
            );

            if (!projectedBytes.AsSpan().SequenceEqual(other: envelope.SignedPortionSpan)) {
                refusal = $"{label}'s parsed fields do not match its authenticated signed portion";

                return false;
            }
        } catch (Exception exception) {
            refusal = $"{label}'s parsed fields cannot be encoded for an integrity check: {exception.GetType().Name}: {exception.Message}";

            return false;
        }

        refusal = null;

        return true;
    }

    /// <summary>
    /// Verifies an envelope's signature. The algorithm rule lives here: <paramref name="pinnedAlgorithm"/>
    /// — taken from the trust list or a prior hop's binding, never from <paramref name="envelope"/>'s own
    /// header — is the only thing that selects the hash algorithm this method verifies with. The caller has
    /// already checked <c>envelope.Header.Algorithm == pinnedAlgorithm</c> as a separate consistency check;
    /// this method does not re-read the header's algorithm at all. The imported key's own curve is checked
    /// against the pinned algorithm's curve too, so a key on some other curve cannot be smuggled in behind
    /// a name that promises P-256.
    /// </summary>
    /// <remarks>
    /// The signing input is <see cref="SignedCarriageEnvelope.SignedPortion"/> — the bytes that arrived —
    /// and never a re-encoding of the parsed model (README.md §2). Re-encoding would
    /// make this method verify a claim nobody signed whenever a decoder anywhere accepted two wire forms
    /// for one model: the forged bytes would be normalised away before the signature ever saw them, and
    /// every such laxity would silently become an accepted alternate encoding of a real claim.
    /// </remarks>
    private static bool VerifySignature(SignedCarriageEnvelope envelope, ReadOnlySpan<byte> publicKeySubjectPublicKeyInfo, string pinnedAlgorithm) {
        var descriptor = CarriageAlgorithms.Resolve(algorithm: pinnedAlgorithm);

        if (
            (descriptor.Role != CarriageKeyRole.Signing) ||
            (descriptor.SignatureHash is null)
        ) {
            return false;
        }

        using var ecdsa = ECDsa.Create();

        try {
            ecdsa.ImportSubjectPublicKeyInfo(
                source: publicKeySubjectPublicKeyInfo,
                bytesRead: out _
            );
        } catch (CryptographicException) {
            return false;
        }

        if (!CarriageCurves.Matches(
            key: ecdsa.ExportParameters(includePrivateParameters: false).Curve,
            expected: descriptor.Curve
        )) {
            return false;
        }

        // IEEE P1363 fixed-field r‖s only: .NET refuses any length other than twice the field width, so no
        // alternate DER encoding of the same (r, s) is even a candidate. What this does NOT buy is signature
        // uniqueness — (r, s) and (r, n-s) are both valid for the same message, so signature bytes are never
        // an identity. Replay is defended by the sequence mark and the audience, never by signature equality.
        return ecdsa.VerifyData(
            data: envelope.SignedPortionSpan,
            signature: envelope.SignatureSpan,
            hashAlgorithm: descriptor.SignatureHash.Value,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
    }

    /// <summary>
    /// Applies both ends of the validity rule (README.md: "Validity is authored at both ends").
    /// There is deliberately no clock-skew tolerance: an issuer that wants slack backdates
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
            // Both values are signed 64-bit wire seconds. Subtracting them as Int64 can wrap, and converting
            // an attacker-authored extreme to TimeSpan can throw. Int128 covers the complete difference and
            // keeps every malformed/extreme window on the refusal path.
            var ageSeconds = ((Int128)nowSeconds - header.NotBefore);
            var maximumAgeSeconds = (Int128)(maximumAge.Value.Ticks / TimeSpan.TicksPerSecond);

            if (ageSeconds > maximumAgeSeconds) {
                return $"expired: age {ageSeconds} seconds exceeds the verifier's maximum age of {maximumAgeSeconds} seconds";
            }
        }

        return null;
    }
}

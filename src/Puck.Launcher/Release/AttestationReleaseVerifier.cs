using Puck.Assets.Documents;
using Puck.Attestation;

namespace Puck.Launcher.Release;

/// <summary>
/// The <c>puck.release.v1</c> signature verifier: the <c>sequence</c>-route bearer claim from
/// <see cref="Puck.Attestation.AttestationProfile.Base"/>, checked against the durable
/// <see cref="IReleaseSequenceStore"/> mark BEFORE anything else — a replayed old manifest is refused by sequence
/// alone, before its embedded <see cref="ReleaseManifest.Revoked"/>/<see cref="ReleaseManifest.MinimumSupported"/>
/// are even trusted. The mark advances only once hash, revocation, <c>minimumSupported</c>, and version
/// monotonicity have ALL passed, so it never records a claim whose effect was not actually accepted.
/// </summary>
/// <param name="trustList">The build-pinned root trust anchor, expressed as a <see cref="TrustList"/> reaching the <c>release</c> slot.</param>
/// <param name="sequenceStore">The durable per-app sequence high-water mark.</param>
/// <param name="codec">The attestation wire codec.</param>
public sealed class AttestationReleaseVerifier(TrustList trustList, IReleaseSequenceStore sequenceStore, IAttestationCodec codec) : IReleaseVerifier {
    /// <summary>The one purpose a release-manifest claim may declare.</summary>
    public const string Purpose = "release";
    /// <summary>The one slot a release-manifest claim's trust entry must reach.</summary>
    public const string Slot = "release";

    private readonly IAttestationCodec m_codec = codec;
    private readonly IReleaseSequenceStore m_sequenceStore = sequenceStore;
    private readonly TrustList m_trustList = trustList;

    /// <inheritdoc/>
    public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) {
        ArgumentNullException.ThrowIfNull(argument: manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: installedVersion);

        if (manifest.Signature is not { } signature) {
            return ReleaseVerifyOutcome.Refuse(reason: "manifest carries no signature");
        }

        SignedAttestation claim;
        List<SignedAttestation> chain;

        try {
            claim = AttestationProfile.Base.DecodeAttestation(codec: m_codec, wire: Convert.FromBase64String(s: signature.Claim));
            chain = signature.Chain.Select(selector: entry => AttestationProfile.Base.DecodeAttestation(codec: m_codec, wire: Convert.FromBase64String(s: entry))).ToList();
        } catch (FormatException exception) {
            return ReleaseVerifyOutcome.Refuse(reason: $"malformed signature: {exception.Message}");
        }

        var result = AttestationProfile.Base.VerifyChain(
            chain: chain,
            claim: claim,
            codec: m_codec,
            expectedAudience: null,
            expectedPurpose: Purpose,
            now: now,
            trustList: m_trustList
        );

        if (!result.TryGetReplayCommit(requirement: out var replayCommit, slot: Slot) || (replayCommit is not { } requirement)) {
            return ReleaseVerifyOutcome.Refuse(reason: (result.RefusalReason ?? "the claim did not verify as an admitted sequenced release bearer claim"));
        }

        // Compare-only: refuses a replayed old manifest before anything it carries is trusted. The durable write
        // happens only at the very end, once every other check below has also passed.
        if (!m_sequenceStore.IsAcceptable(requirement: requirement)) {
            return ReleaseVerifyOutcome.Refuse(reason: $"sequence {requirement.Sequence} at epoch {requirement.EpochStartUnixSeconds} does not exceed the stored high-water mark — refused as a replay");
        }

        CanonicalDocument<ReleaseManifest> expectedCanonical;

        try {
            expectedCanonical = ReleaseCanonicalizer.Canonicalize(document: (manifest with { Signature = null }));
        } catch (DocumentValidationException exception) {
            return ReleaseVerifyOutcome.Refuse(reason: $"manifest fails structural validation: {exception.Message}");
        }

        if (!claim.PayloadBytes.Span.SequenceEqual(other: expectedCanonical.Bytes)) {
            return ReleaseVerifyOutcome.Refuse(reason: "the signed claim payload does not match this manifest's own canonical bytes");
        }

        if ((manifest.Revoked is { Count: > 0 } revoked) && revoked.Contains(value: manifest.Version, comparer: StringComparer.Ordinal)) {
            return ReleaseVerifyOutcome.Refuse(reason: $"version '{manifest.Version}' is revoked");
        }

        if ((manifest.MinimumSupported is { } minimumSupported) && ReleaseVersion.IsStrictlyGreaterThan(left: minimumSupported, right: installedVersion)) {
            return ReleaseVerifyOutcome.Refuse(reason: $"installed version '{installedVersion}' is below this release's minimumSupported floor '{minimumSupported}'");
        }

        if (!ReleaseVersion.IsStrictlyGreaterThan(left: manifest.Version, right: installedVersion)) {
            return ReleaseVerifyOutcome.Refuse(reason: $"version '{manifest.Version}' is not strictly greater than the installed version '{installedVersion}'");
        }

        if (advanceSequence) {
            m_sequenceStore.Advance(requirement: requirement);
        }

        return ReleaseVerifyOutcome.Accept();
    }
}

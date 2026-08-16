using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Numerics;
using System.Security.Cryptography;

using Xunit;

namespace Puck.Attestation.Tests;

/// <summary>One minted domain's raw key material: root, issuing, and a subject's signing and sealing keys — all sharing the domain's root fingerprint.</summary>
internal sealed record DomainKeys(
    string Domain,
    ECDsa RootKey,
    byte[] RootSpki,
    KeyId RootId,
    ECDsa IssuingKey,
    byte[] IssuingSpki,
    KeyId IssuingId,
    string Subject,
    ECDsa SubjectSigningKey,
    byte[] SubjectSigningSpki,
    KeyId SubjectSigningId,
    ECDiffieHellman SubjectSealingKey,
    byte[] SubjectSealingSpki,
    KeyId SubjectSealingId
);
internal static class AttestationTestSupport {
    internal const long Epoch = 1_700_000_000L;

    /// <summary>The reach every ordinary test trust list authors, so a scenario that does not care about scoping still carries a real one.</summary>
    internal static readonly IReadOnlySet<string> DefaultReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet", "slot:title" };

    /// <summary>Mints a fresh domain's whole key set. Minting is randomised, so every call produces a distinct domain even for the same subject string.</summary>
    internal static DomainKeys MintDomainKeys(string subject) {
        var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var rootId = KeyId.ForRoot(
            algorithm: AttestationAlgorithms.EcdsaP256Sha256,
            subjectPublicKeyInfo: rootSpki
        );

        var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var issuingId = KeyId.ForIssuing(
            domain: rootId.Domain,
            subjectPublicKeyInfo: issuingSpki,
            algorithm: AttestationAlgorithms.EcdsaP256Sha256
        );

        var subjectSigningKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSigningSpki = subjectSigningKey.ExportSubjectPublicKeyInfo();
        var subjectSigningId = KeyId.ForSubject(
            domain: rootId.Domain,
            subject: subject,
            subjectPublicKeyInfo: subjectSigningSpki,
            algorithm: AttestationAlgorithms.EcdsaP256Sha256
        );

        var subjectSealingKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSealingSpki = subjectSealingKey.ExportSubjectPublicKeyInfo();
        var subjectSealingId = KeyId.ForSubject(
            domain: rootId.Domain,
            subject: subject,
            subjectPublicKeyInfo: subjectSealingSpki,
            algorithm: AttestationAlgorithms.EcdhP256HkdfSha256Aes256Gcm
        );

        return new DomainKeys(
            Domain: rootId.Domain,
            RootKey: rootKey,
            RootSpki: rootSpki,
            RootId: rootId,
            IssuingKey: issuingKey,
            IssuingSpki: issuingSpki,
            IssuingId: issuingId,
            Subject: subject,
            SubjectSigningKey: subjectSigningKey,
            SubjectSigningSpki: subjectSigningSpki,
            SubjectSigningId: subjectSigningId,
            SubjectSealingKey: subjectSealingKey,
            SubjectSealingSpki: subjectSealingSpki,
            SubjectSealingId: subjectSealingId
        );
    }
    /// <summary>Mints binding #1 (root vouches issuing) and binding #2 (issuing vouches subject) — the depth-exactly-two chain.</summary>
    internal static (SignedAttestation RootToIssuing, SignedAttestation IssuingToSubject) BuildChain(IAttestationCodec codec, DomainKeys keys, long notBefore, long notAfter) {
        var rootToIssuing = AttestationSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.RootKey,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            targetId: keys.IssuingId,
            targetSubjectPublicKeyInfo: keys.IssuingSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );

        var issuingToSubject = AttestationSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.IssuingKey,
            signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
            targetId: keys.SubjectSigningId,
            targetSubjectPublicKeyInfo: keys.SubjectSigningSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );

        return (rootToIssuing, issuingToSubject);
    }
    internal static TrustList BuildTrustList(DomainKeys keys, TimeSpan? defaultMaximumAge, IReadOnlySet<string>? reach = null) {
        var entry = new TrustListEntry(
            PinnedId: keys.RootId,
            PublicKeySubjectPublicKeyInfo: keys.RootSpki,
            Mode: AttestationTrustMode.Vouches,
            Reach: (reach ?? DefaultReach),
            MaximumAge: null
        );

        return new TrustList(
            entries: [entry],
            defaultMaximumAge: defaultMaximumAge,
            replayAcceptanceHorizon: defaultMaximumAge
        );
    }
    /// <summary>Builds a trust list that pins one subject's own signing key directly — the zero-hop shape, so a scenario exercises one signature rather than three.</summary>
    internal static TrustList BuildDirectTrustList(DomainKeys keys, IReadOnlySet<string> reach) =>
        new(
        entries: [
            new TrustListEntry(
                PinnedId: keys.SubjectSigningId,
                PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                Mode: AttestationTrustMode.SignsDirectly,
                Reach: reach,
                MaximumAge: null
            ),
        ],
        defaultMaximumAge: null
    );
    internal static SignedAttestation SignTestClaim(
        IAttestationCodec codec,
        DomainKeys keys,
        string purpose,
        long notBefore,
        long notAfter,
        string? audience,
        ulong? sequence,
        string text,
        string? declaredAlgorithm = null
    ) {
        var claimBytes = System.Text.Encoding.UTF8.GetBytes(s: text);

        if (declaredAlgorithm is null) {
            return AttestationSigner.SignClaim(
                codec: codec,
                domain: keys.Domain,
                subject: keys.Subject,
                signerKey: keys.SubjectSigningKey,
                signerAlgorithm: AttestationAlgorithms.EcdsaP256Sha256,
                purpose: purpose,
                notBefore: notBefore,
                notAfter: notAfter,
                audience: audience,
                sequence: sequence,
                claimBytes: claimBytes
            );
        }

        // The production signer refuses this mismatch. Build the hostile arrived bytes inside the test
        // assembly so the verifier's algorithm-confusion refusal remains independently exercised.
        var header = new AttestationHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: declaredAlgorithm,
            Purpose: purpose,
            NotBefore: notBefore,
            NotAfter: notAfter,
            Audience: audience,
            Sequence: sequence
        );
        var signedPortion = codec.EncodeSignedPortion(
            header: header,
            payloadBytes: claimBytes,
            payloadKind: AttestationPayloadKind.Opaque
        );
        var signature = keys.SubjectSigningKey.SignData(
            data: signedPortion,
            hashAlgorithm: HashAlgorithmName.SHA256,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        return SignedAttestation.FromSignedPortion(
            header: header,
            payloadKind: AttestationPayloadKind.Opaque,
            payloadBytes: claimBytes,
            signature: signature,
            signedPortion: signedPortion
        );
    }
    internal static void AssertAccepted(AttestationVerifyResult result) =>
        Assert.True(condition: result.Verified, userMessage: $"unexpectedly refused: {result.RefusalReason}");
    internal static void AssertRefused(AttestationVerifyResult result, string reasonMustContain) {
        Assert.False(condition: result.Verified, userMessage: "unexpectedly ACCEPTED");
        Assert.NotNull(@object: result.RefusalReason);
        Assert.Contains(
            expectedSubstring: reasonMustContain,
            actualString: result.RefusalReason!,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }
    /// <summary>
    /// Hand-builds a canonically encoded CBOR attestation with a chosen domain width and payload kind — the
    /// two fields whose wire values a signer could never produce but a decoder must still refuse. Nothing
    /// else about it is malformed, so at (32, 1) it decodes and only the field under test can refuse it.
    /// The signature is a placeholder; this never reaches a signature check.
    /// </summary>
    internal static byte[] BuildHandWrittenAttestation(int domainWidth = 32, ulong payloadKind = ((ulong)AttestationPayloadKind.Opaque)) {
        var signedPortionWriter = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        signedPortionWriter.WriteStartArray(definiteLength: 11);
        signedPortionWriter.WriteUInt64(value: CborAttestationCodec.FormatVersion);
        signedPortionWriter.WriteByteString(value: new byte[domainWidth]);
        signedPortionWriter.WriteTextString(value: "user:width");
        signedPortionWriter.WriteTextString(value: AttestationAlgorithms.EcdsaP256Sha256);
        signedPortionWriter.WriteTextString(value: "test.claim");
        signedPortionWriter.WriteInt64(value: 0L);
        signedPortionWriter.WriteInt64(value: 0L);
        signedPortionWriter.WriteNull();
        signedPortionWriter.WriteNull();
        signedPortionWriter.WriteUInt64(value: payloadKind);
        signedPortionWriter.WriteByteString(value: System.Text.Encoding.UTF8.GetBytes(s: "payload"));
        signedPortionWriter.WriteEndArray();

        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 2);
        writer.WriteByteString(value: signedPortionWriter.Encode());
        writer.WriteByteString(value: new byte[64]);
        writer.WriteEndArray();

        return writer.Encode();
    }
    /// <summary>Re-frames a valid 2-element CBOR attestation as an indefinite-length array carrying the same two items.</summary>
    internal static byte[] BuildIndefiniteLengthAttestation(byte[] wire) {
        var reader = new CborReader(
            data: wire,
            conformanceMode: CborConformanceMode.Strict
        );

        _ = reader.ReadStartArray();

        var signedPortion = reader.ReadByteString();
        var signature = reader.ReadByteString();

        reader.ReadEndArray();

        var writer = new CborWriter(conformanceMode: CborConformanceMode.Lax);

        writer.WriteStartArray(definiteLength: null);
        writer.WriteByteString(value: signedPortion);
        writer.WriteByteString(value: signature);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <summary>The P-256 group order, needed to build the (r, n-s) form of a signature.</summary>
    private static readonly BigInteger NistP256Order = BigInteger.Parse(
        style: System.Globalization.NumberStyles.HexNumber,
        value: "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"
    );

    /// <summary>Rewrites a P1363 <c>r‖s</c> signature as the equally valid <c>r‖(n-s)</c>.</summary>
    internal static byte[] MalleateSignature(ReadOnlySpan<byte> signature) {
        var half = (signature.Length / 2);
        var s = new BigInteger(
            value: signature[half..],
            isUnsigned: true,
            isBigEndian: true
        );
        var flipped = (NistP256Order - s);
        var result = signature.ToArray();
        var flippedBytes = flipped.ToByteArray(
            isBigEndian: true,
            isUnsigned: true
        );

        var destinationStart = (signature.Length - flippedBytes.Length);

        flippedBytes.AsSpan().CopyTo(destination: result.AsSpan(start: destinationStart));

        for (var index = half; (index < destinationStart); index += 1) {
            result[index] = 0x00;
        }

        return result;
    }
    /// <summary>Re-encodes a P1363 <c>r‖s</c> signature as the DER <c>SEQUENCE { INTEGER r, INTEGER s }</c> form.</summary>
    internal static byte[] EncodeSignatureAsDer(ReadOnlySpan<byte> signature) {
        var half = (signature.Length / 2);
        var writer = new AsnWriter(ruleSet: AsnEncodingRules.DER);

        using (writer.PushSequence()) {
            writer.WriteInteger(value: new BigInteger(
                value: signature[..half],
                isUnsigned: true,
                isBigEndian: true
            ));
            writer.WriteInteger(value: new BigInteger(
                value: signature[half..],
                isUnsigned: true,
                isBigEndian: true
            ));
        }

        return writer.Encode();
    }
}
/// <summary>
/// A receiver-side atomic replay commit store, mirroring what a real receiver does: compare/advance the
/// epoch high-water mark in the same durable transaction as the claim's semantic effect. The rendezvous
/// barrier makes a contended-store test deterministic — without it, concurrent callers would usually
/// serialise by luck and a broken store would pass anyway.
/// </summary>
internal sealed class ReplayTestStore(int participants = 1) {
    private readonly Barrier? m_barrier = ((participants > 1) ? new Barrier(participantCount: participants) : null);
    private readonly Dictionary<(string Domain, string Subject, long EpochStartUnixSeconds), ulong> m_marks = [];

    public AttestationVerifyResult Commit(AttestationVerifyResult result) {
        if (!result.Verified || (result.ReplayCommit is null)) {
            return result;
        }

        m_barrier?.SignalAndWait();

        var requirement = result.ReplayCommit;
        var key = (requirement.Domain, requirement.Subject, requirement.EpochStartUnixSeconds);

        lock (m_marks) {
            if (
                m_marks.TryGetValue(
                key: key,
                out var mark
            ) &&
                (requirement.Sequence <= mark)
            ) {
                return AttestationVerifyResult.Refuse(reason: $"sequence replay: claim sequence {requirement.Sequence} does not strictly exceed the recorded epoch high-water mark");
            }

            m_marks[key] = requirement.Sequence;

            return result;
        }
    }
}
/// <summary>
/// A deliberately broken replay store whose compare and advance happen under separate locks. Every caller
/// reads before any caller advances, so the concurrency demonstration deterministically exposes the replay.
/// </summary>
internal sealed class SplitReplayTestStore(int participants) {
    private readonly Barrier m_barrier = new(participantCount: participants);
    private readonly Dictionary<(string Domain, string Subject, long EpochStartUnixSeconds), ulong> m_marks = [];

    public AttestationVerifyResult Commit(AttestationVerifyResult result) {
        if (!result.Verified || (result.ReplayCommit is null)) {
            return result;
        }

        var requirement = result.ReplayCommit;
        var key = (requirement.Domain, requirement.Subject, requirement.EpochStartUnixSeconds);
        var hasMark = false;
        var mark = 0UL;

        lock (m_marks) {
            hasMark = m_marks.TryGetValue(key: key, value: out mark);
        }

        m_barrier.SignalAndWait();

        if (hasMark && (requirement.Sequence <= mark)) {
            return AttestationVerifyResult.Refuse(reason: $"sequence replay: claim sequence {requirement.Sequence} does not strictly exceed the recorded epoch high-water mark");
        }

        lock (m_marks) {
            m_marks[key] = requirement.Sequence;
        }

        return result;
    }
}

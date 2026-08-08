using System.Security.Cryptography;
using System.Text;

namespace Puck.Carriage;

/// <summary>
/// The cross-implementation check (docs/signed-carriage-wire.md §17): mint a chain to files, or pin a chain
/// minted by the OTHER implementation and verify it. The envelope is a specification each side implements
/// independently, and the only thing that proves the specification was written well enough is bytes minted
/// by one side verifying in the other — so this is a file-in/file-out mode of the harness rather than
/// anything the engine calls.
/// </summary>
/// <remarks>
/// <para>The interchange directory holds seven files:</para>
/// <list type="bullet">
/// <item><c>root.spki</c> — the root key's <c>SubjectPublicKeyInfo</c> DER bytes. The verifying side
/// recomputes the domain from these rather than trusting the manifest's copy.</item>
/// <item><c>binding-1.envelope</c> — root vouches issuing.</item>
/// <item><c>binding-2.envelope</c> — issuing vouches subject.</item>
/// <item><c>claim.envelope</c> — one signed claim by the subject key.</item>
/// <item><c>sealed.envelope</c> — one SEALED claim: an ordinary signed envelope with
/// <see cref="CarriagePayloadKind.Sealed"/>, whose payload is AEAD ciphertext under the recipient key
/// below.</item>
/// <item><c>recipient-sealing.pkcs8</c> — the recipient's PRIVATE sealing key, PKCS#8 DER. It is here so
/// the other side can actually unseal; see the warning below.</item>
/// <item><c>manifest.txt</c> — <c>key=value</c> lines naming what the verifier must expect: domain,
/// subject, algorithm, purpose, audience, sequence, and the sealed claim's purpose and expected
/// plaintext. The format is fixed by §17 — UTF-8, LF-terminated, first <c>=</c> splits, three backslash
/// escapes, unknown keys ignored.</item>
/// </list>
/// <para><b>Why the sealed artifact has to exist.</b> Sealed carriage's key derivation
/// (docs/signed-carriage-wire.md §14) fixes five construction inputs — raw agreement as HKDF input, absent
/// salt, the <c>puck.carriage.sealed.v1</c> info label, 32-byte output, 16-byte AEAD tag — and NONE of them
/// is observable from a signed envelope. Two implementations disagreeing about any one of them fail with an
/// AEAD tag mismatch, which is the same failure a tampered payload produces. Without ciphertext one side
/// actually opens, §14 is cross-verified by reading prose and hoping. The signed envelopes never exercised
/// it.</para>
/// <para><b>The private key in the directory is deliberate and is a FIXTURE ONLY.</b> A recipient key that
/// nobody can decrypt with proves nothing, so this one is minted fresh per export, used for one sealed
/// payload, and belongs to no identity. Nothing here is a key management pattern.</para>
/// <para><b>Neither verb may crash.</b> Every file in the directory is input, and input that is missing,
/// truncated, corrupt, or simply not what it claims to be is a FAILED CHECK with a name and a non-zero exit
/// — never an unhandled exception. A cross-checking implementer reading a stack trace cannot tell "your
/// bytes are bad" from "your tool fell over", and those are different verdicts (§17, the tool protocol).
/// That is why every step here runs through <see cref="TryStep{T}"/>.</para>
/// </remarks>
public static class CarriageInterchange {
    private const string BindingOneFileName = "binding-1.envelope";
    private const string BindingTwoFileName = "binding-2.envelope";
    private const string ClaimFileName = "claim.envelope";
    private const string ManifestFileName = "manifest.txt";
    private const string RecipientSealingKeyFileName = "recipient-sealing.pkcs8";
    private const string RootKeyFileName = "root.spki";
    private const string SealedFileName = "sealed.envelope";

    /// <summary>The manifest keys §17 requires, every one of which must be present with a non-empty value. Any OTHER key is ignored — the set is open, and <c>minted-by</c> is the optional key this implementation writes.</summary>
    private static readonly string[] s_requiredManifestKeys = [
        "algorithm",
        "audience",
        "domain",
        "purpose",
        "sealed-plaintext",
        "sealed-purpose",
        "sequence",
        "subject",
    ];

    /// <summary>The manifest's encoding: UTF-8 with no byte-order mark, refusing byte sequences that are not valid UTF-8 rather than substituting replacement characters.</summary>
    private static readonly UTF8Encoding s_manifestEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The purpose the interchange claim is minted with, so both sides expect the same one without negotiating.</summary>
    public const string InterchangePurpose = "carriage.cross-check";

    /// <summary>The purpose the interchange SEALED claim is minted with.</summary>
    public const string InterchangeSealedPurpose = "carriage.cross-check.sealed";

    /// <summary>The audience BOTH interchange envelopes are directed at — §17 fixes the sealed envelope's audience as the claim's, so the manifest carries one value for both.</summary>
    public const string InterchangeAudience = "world:interchange";

    /// <summary>The sequence the interchange CLAIM carries. The sealed envelope deliberately carries none (§17): a mark store shared across runs would refuse the second verification of the same file as a replay, correctly.</summary>
    public const ulong InterchangeSequence = 1UL;

    /// <summary>The subject the interchange chain is minted for.</summary>
    public const string InterchangeSubject = "puck:interchange-subject";

    /// <summary>The plaintext the interchange sealed payload carries, so the verifying side knows what a correct unseal must produce.</summary>
    public const string InterchangeSealedPlaintext = "sealed by Puck.Carriage under puck.carriage.sealed.v1";

    /// <summary>Mints a root, an issuing key, a subject key, both bindings, one claim, and one sealed claim, and writes them all to <paramref name="directory"/>.</summary>
    /// <param name="directory">The interchange directory to write. Created if absent.</param>
    /// <returns>0 when the fixture was written; 1 when it could not be.</returns>
    public static int Export(string directory) {
        try {
            return ExportCore(directory: directory);
        } catch (Exception exception) {
            Console.WriteLine(value: $"[FAIL] interchange export: the fixture could not be written to {directory} — {Describe(exception: exception)}");

            return 1;
        }
    }

    private static int ExportCore(string directory) {
        var codec = new CborCarriageCodec();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var notBefore = (now - 3_600L);
        var notAfter = (now + (86_400L * 30));

        using var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var subjectKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        using var recipientSealingKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);

        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var subjectSpki = subjectKey.ExportSubjectPublicKeyInfo();
        var rootId = KeyId.ForRoot(subjectPublicKeyInfo: rootSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);
        var issuingId = KeyId.ForIssuing(domain: rootId.Domain, subjectPublicKeyInfo: issuingSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);
        var subjectId = KeyId.ForSubject(domain: rootId.Domain, subject: InterchangeSubject, subjectPublicKeyInfo: subjectSpki, algorithm: CarriageAlgorithms.EcdsaP256Sha256);

        var bindingOne = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: rootId.Domain,
            signerKey: rootKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: issuingId,
            targetSubjectPublicKeyInfo: issuingSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );
        var bindingTwo = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: rootId.Domain,
            signerKey: issuingKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: subjectId,
            targetSubjectPublicKeyInfo: subjectSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );
        var claim = CarriageSigner.SignClaim(
            codec: codec,
            domain: rootId.Domain,
            subject: InterchangeSubject,
            signerKey: subjectKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: InterchangePurpose,
            notBefore: notBefore,
            notAfter: notAfter,
            audience: InterchangeAudience,
            sequence: InterchangeSequence,
            claimBytes: Encoding.UTF8.GetBytes(s: "minted by Puck.Carriage")
        );

        // The sealed claim. Its header is built FIRST because the header's own encoding is the AEAD
        // associated data (§14), so the ciphertext cannot exist until the context it is bound to does; then
        // the same header is signed, which is what names the sender (sealing alone proves nobody). Its
        // audience is the claim's — §17 fixes that rather than giving the fixture a second knob — and it
        // carries NO sequence, so re-verifying the same file against a durable mark store stays legal.
        var sealedHeader = new CarriageEnvelopeHeader(
            Domain: rootId.Domain,
            Subject: InterchangeSubject,
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: InterchangeSealedPurpose,
            NotBefore: notBefore,
            NotAfter: notAfter,
            Audience: InterchangeAudience,
            Sequence: null
        );
        var sealedPayload = SealedCarriage.Seal(
            recipientPublicKeySubjectPublicKeyInfo: recipientSealingKey.ExportSubjectPublicKeyInfo(),
            associatedData: codec.EncodeHeader(header: sealedHeader),
            plaintext: Encoding.UTF8.GetBytes(s: InterchangeSealedPlaintext)
        );
        var sealedClaim = CarriageSigner.Sign(
            codec: codec,
            header: sealedHeader,
            payloadKind: CarriagePayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: sealedPayload),
            signingKey: subjectKey,
            signingAlgorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: RootKeyFileName), bytes: rootSpki);
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: SealedFileName), bytes: codec.EncodeEnvelope(envelope: sealedClaim));
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: RecipientSealingKeyFileName), bytes: recipientSealingKey.ExportPkcs8PrivateKey());
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: BindingOneFileName), bytes: codec.EncodeEnvelope(envelope: bindingOne));
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: BindingTwoFileName), bytes: codec.EncodeEnvelope(envelope: bindingTwo));
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: ClaimFileName), bytes: codec.EncodeEnvelope(envelope: claim));
        WriteManifest(
            path: Path.Combine(path1: directory, path2: ManifestFileName),
            entries: [
                ("algorithm", CarriageAlgorithms.EcdsaP256Sha256),
                ("audience", InterchangeAudience),
                ("domain", rootId.Domain),
                ("minted-by", "puck.carriage"),
                ("purpose", InterchangePurpose),
                ("sealed-plaintext", InterchangeSealedPlaintext),
                ("sealed-purpose", InterchangeSealedPurpose),
                ("sequence", InterchangeSequence.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)),
                ("subject", InterchangeSubject),
            ]
        );

        Console.WriteLine(value: $"exported a Puck-minted chain to {directory}");
        Console.WriteLine(value: $"  domain  {rootId.Domain}");
        Console.WriteLine(value: $"  subject {InterchangeSubject}");
        Console.WriteLine(value: $"  sealed  {SealedFileName} opens with {RecipientSealingKeyFileName} to '{InterchangeSealedPlaintext}'");

        return 0;
    }

    /// <summary>
    /// Pins the exported root and verifies the imported chain, then tampers one byte of the claim and
    /// requires a refusal. Two checks at minimum, because an accepting verifier proves nothing on its own —
    /// one that accepts everything would pass the first.
    /// </summary>
    /// <param name="directory">The interchange directory to read.</param>
    /// <returns>0 when every check held, 1 when at least one failed.</returns>
    /// <remarks>
    /// This method never propagates an exception, whatever is in <paramref name="directory"/>. A corrupt
    /// <c>claim.envelope</c> used to escape as an unhandled <see cref="FormatException"/> and terminate the
    /// process, which reports the fixture as broken by crashing — the one thing §17's tool protocol forbids,
    /// because it is indistinguishable from the tool itself being broken.
    /// </remarks>
    public static int Verify(string directory) {
        try {
            return VerifyCore(directory: directory);
        } catch (Exception exception) {
            // The backstop. Nothing should reach here — every step below is guarded — but "should" is not a
            // verdict, and an escaping exception would be the exact defect this method exists to not have.
            Console.WriteLine(value: $"[FAIL] cross-verify: an unguarded failure while reading the fixture at {directory} — {Describe(exception: exception)}");

            return 1;
        }
    }

    private static int VerifyCore(string directory) {
        var codec = new CborCarriageCodec();
        var failures = 0;

        if (!TryStep(check: $"cross-verify manifest: reading {ManifestFileName}", body: () => ReadManifest(path: Path.Combine(path1: directory, path2: ManifestFileName)), value: out var manifest)) {
            return 1;
        }

        var missingKeys = s_requiredManifestKeys.Where(predicate: key => (!manifest.TryGetValue(key: key, value: out var value) || (value.Length == 0))).ToArray();

        if (missingKeys.Length != 0) {
            Console.WriteLine(value: $"[FAIL] cross-verify manifest: {ManifestFileName} is missing (or leaves empty) the required key(s) {string.Join(separator: ", ", values: missingKeys)} — §17 requires all {s_requiredManifestKeys.Length}");

            return 1;
        }

        var algorithm = manifest["algorithm"];

        if (!CarriageAlgorithms.IsKnown(algorithm: algorithm)) {
            Console.WriteLine(value: $"[FAIL] cross-verify manifest: the manifest names algorithm '{algorithm}', which is not in the §4 registry");

            return 1;
        }

        var expectedPurpose = manifest["purpose"];

        if (string.Equals(a: expectedPurpose, b: CarriagePurposes.KeyBinding, comparisonType: StringComparison.Ordinal)) {
            Console.WriteLine(value: $"[FAIL] cross-verify manifest: the manifest names purpose '{CarriagePurposes.KeyBinding}', which is reserved and can never be a claim's purpose (§8)");

            return 1;
        }

        if (
            !TryStep(check: $"cross-verify root key: reading {RootKeyFileName}", body: () => File.ReadAllBytes(path: Path.Combine(path1: directory, path2: RootKeyFileName)), value: out var rootSpki) ||
            !TryStep(check: $"cross-verify claim: reading {ClaimFileName}", body: () => File.ReadAllBytes(path: Path.Combine(path1: directory, path2: ClaimFileName)), value: out var claimBytes)
        ) {
            return 1;
        }

        var rootId = KeyId.ForRoot(subjectPublicKeyInfo: rootSpki, algorithm: algorithm);

        Console.WriteLine(value: $"verifying a chain minted by '{manifest.GetValueOrDefault(key: "minted-by", defaultValue: "(unstated)")}' from {directory}");
        Console.WriteLine(value: $"  domain recomputed from {RootKeyFileName}: {rootId.Domain}");

        if (!string.Equals(a: rootId.Domain, b: manifest["domain"], comparisonType: StringComparison.Ordinal)) {
            Console.WriteLine(value: $"[FAIL] cross-verify: the manifest names domain {manifest["domain"]}, but the exported root key hashes to {rootId.Domain}");

            return 1;
        }

        if (!TryStep(
            check: "cross-verify trust list: pinning the exported root",
            body: () => new TrustList(
                entries: [
                    new TrustListEntry(
                        PinnedId: rootId,
                        PublicKeySubjectPublicKeyInfo: rootSpki,
                        Mode: CarriageTrustMode.Vouches,
                        Reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:interchange" },
                        MaximumAge: null
                    ),
                ],
                defaultMaximumAge: null
            ),
            value: out var trustList
        )) {
            return 1;
        }

        if (!TryStep(
            check: $"cross-verify chain: decoding {BindingOneFileName} and {BindingTwoFileName}",
            body: () => new[] {
                codec.DecodeEnvelope(wire: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: BindingOneFileName))),
                codec.DecodeEnvelope(wire: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: BindingTwoFileName))),
            },
            value: out var chain
        )) {
            return 1;
        }

        // Reading the clock here is legitimate where it would not be in the engine: this is a file-in
        // file-out developer tool whose admission boundary IS the process invocation, so there is no tape
        // to replay and no tick to be inside (docs/signed-carriage-wire.md §9). The mark store is fresh per
        // run for the same reason §17 gives: a store carried between runs would refuse the second
        // verification of the same fixture as a replay, and be right to.
        CarriageVerifyResult VerifyClaimBytes(byte[] wire, string expected) =>
            CarriageVerifier.VerifyChain(
                codec: codec,
                claim: codec.DecodeEnvelope(wire: wire),
                chain: chain,
                trustList: trustList,
                now: DateTimeOffset.UtcNow,
                expectedPurpose: expected,
                expectedAudience: manifest["audience"],
                sequenceStore: new InMemorySequenceStore()
            );

        if (TryStep(check: "cross-verify", body: () => VerifyClaimBytes(wire: claimBytes, expected: expectedPurpose), value: out var result)) {
            if (result.Verified) {
                Console.WriteLine(value: $"[PASS] cross-verify: the imported chain and claim verify against the pinned root — reach [{string.Join(separator: ", ", values: result.Reach!)}]");
            } else {
                failures += 1;

                Console.WriteLine(value: $"[FAIL] cross-verify: the imported chain was refused — {result.RefusalReason}");
            }
        } else {
            failures += 1;
        }

        failures += CheckClaimHeader(codec: codec, claimBytes: claimBytes, manifest: manifest);

        // One flipped byte inside the claim's signature: the bytes still decode, so this lands on the
        // signature check rather than on the parser, which is what makes it a control for the case above.
        var tampered = (byte[])claimBytes.Clone();

        tampered[^1] ^= 0xFF;

        failures += ExpectRefusal(
            check: "cross-verify tamper",
            detail: "one flipped byte",
            body: () => VerifyClaimBytes(wire: tampered, expected: expectedPurpose)
        );

        failures += VerifySealed(codec: codec, directory: directory, manifest: manifest, trustList: trustList, chain: chain);

        return ((failures == 0) ? 0 : 1);
    }

    /// <summary>
    /// Checks the imported claim's own header against what the manifest says it carries. The manifest is not
    /// signed, so this is not a security check — it is the fixture's self-consistency check, and it is what
    /// catches an exporter whose manifest and bytes disagree before the other side spends a day on it.
    /// </summary>
    /// <param name="codec">The serialisation to decode with.</param>
    /// <param name="claimBytes">The claim envelope's wire bytes.</param>
    /// <param name="manifest">The parsed manifest.</param>
    /// <returns>The number of failures.</returns>
    private static int CheckClaimHeader(ICarriageCodec codec, byte[] claimBytes, Dictionary<string, string> manifest) {
        if (!TryStep(check: $"cross-verify manifest agreement: decoding {ClaimFileName}", body: () => codec.DecodeEnvelope(wire: claimBytes), value: out var claim)) {
            return 1;
        }

        var expectedSequence = manifest["sequence"];
        var disagreements = new List<string>();

        if (!string.Equals(a: claim.Header.Subject, b: manifest["subject"], comparisonType: StringComparison.Ordinal)) {
            disagreements.Add(item: $"subject '{(claim.Header.Subject ?? "(none)")}' against the manifest's '{manifest["subject"]}'");
        }

        if (!string.Equals(a: claim.Header.Algorithm, b: manifest["algorithm"], comparisonType: StringComparison.Ordinal)) {
            disagreements.Add(item: $"algorithm '{claim.Header.Algorithm}' against the manifest's '{manifest["algorithm"]}'");
        }

        if (!string.Equals(a: claim.Header.Purpose, b: manifest["purpose"], comparisonType: StringComparison.Ordinal)) {
            disagreements.Add(item: $"purpose '{claim.Header.Purpose}' against the manifest's '{manifest["purpose"]}'");
        }

        if (!string.Equals(a: claim.Header.Audience, b: manifest["audience"], comparisonType: StringComparison.Ordinal)) {
            disagreements.Add(item: $"audience '{(claim.Header.Audience ?? "(none)")}' against the manifest's '{manifest["audience"]}'");
        }

        if (!string.Equals(a: claim.Header.Sequence?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture), b: expectedSequence, comparisonType: StringComparison.Ordinal)) {
            disagreements.Add(item: $"sequence '{(claim.Header.Sequence?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "(none)")}' against the manifest's '{expectedSequence}'");
        }

        if (disagreements.Count != 0) {
            Console.WriteLine(value: $"[FAIL] cross-verify manifest agreement: {ClaimFileName} disagrees with {ManifestFileName} on {string.Join(separator: "; ", values: disagreements)}");

            return 1;
        }

        Console.WriteLine(value: $"[PASS] cross-verify manifest agreement: {ClaimFileName}'s header carries exactly what {ManifestFileName} names, sequence included");

        return 0;
    }

    /// <summary>
    /// Verifies the sealed artifact: the envelope's signature by the ordinary chain walk, then the AEAD
    /// open itself. This is the only part of the fixture that exercises §14's key derivation, and it is the
    /// only way an implementation can discover it disagrees about the salt, the info label, the output
    /// length, the tag length, or raw-versus-hashed agreement — none of which is visible in any signed
    /// envelope.
    /// </summary>
    /// <param name="codec">The serialisation to decode with.</param>
    /// <param name="directory">The interchange directory.</param>
    /// <param name="manifest">The parsed manifest.</param>
    /// <param name="trustList">The trust list pinning the exported root.</param>
    /// <param name="chain">The two verified bindings.</param>
    /// <returns>The number of failures — 0 when the sealed claim verifies AND opens to the expected plaintext.</returns>
    private static int VerifySealed(
        ICarriageCodec codec,
        string directory,
        Dictionary<string, string> manifest,
        TrustList trustList,
        IReadOnlyList<SignedCarriageEnvelope> chain
    ) {
        var sealedPath = Path.Combine(path1: directory, path2: SealedFileName);
        var keyPath = Path.Combine(path1: directory, path2: RecipientSealingKeyFileName);

        if (!File.Exists(path: sealedPath) || !File.Exists(path: keyPath)) {
            Console.WriteLine(value: $"[FAIL] cross-verify sealed: the fixture is missing {SealedFileName} or {RecipientSealingKeyFileName} — §14 cannot be cross-verified without ciphertext one side actually opens");

            return 1;
        }

        if (!TryStep(check: $"cross-verify sealed: decoding {SealedFileName}", body: () => codec.DecodeEnvelope(wire: File.ReadAllBytes(path: sealedPath)), value: out var sealedClaim)) {
            return 1;
        }

        if (!TryStep(
            check: "cross-verify sealed: walking the sealed envelope's own chain",
            body: () => CarriageVerifier.VerifyChain(
                codec: codec,
                claim: sealedClaim,
                chain: chain,
                trustList: trustList,
                now: DateTimeOffset.UtcNow,
                expectedPurpose: manifest["sealed-purpose"],
                expectedAudience: manifest["audience"],
                sequenceStore: new InMemorySequenceStore()
            ),
            value: out var result
        )) {
            return 1;
        }

        if (!result.Verified) {
            Console.WriteLine(value: $"[FAIL] cross-verify sealed: the sealed envelope's own signature was refused — {result.RefusalReason}");

            return 1;
        }

        if (sealedClaim.PayloadKind != CarriagePayloadKind.Sealed) {
            Console.WriteLine(value: $"[FAIL] cross-verify sealed: the sealed envelope declares payload kind '{sealedClaim.PayloadKind}', expected '{CarriagePayloadKind.Sealed}'");

            return 1;
        }

        // §17 fixes the sealed envelope's audience and sequence rather than adding manifest keys for them:
        // an unsigned hint about a signed field is a second source of truth, and a sequence here would make
        // the SECOND verification of the same file a legitimate replay refusal.
        if (!string.Equals(a: sealedClaim.Header.Audience, b: manifest["audience"], comparisonType: StringComparison.Ordinal) || (sealedClaim.Header.Sequence is not null)) {
            Console.WriteLine(value: $"[FAIL] cross-verify sealed: the sealed envelope carries audience '{(sealedClaim.Header.Audience ?? "(none)")}' and sequence '{(sealedClaim.Header.Sequence?.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) ?? "(none)")}'; §17 fixes them as the manifest's audience '{manifest["audience"]}' and no sequence");

            return 1;
        }

        if (!TryStep(
            check: $"cross-verify sealed: importing {RecipientSealingKeyFileName}",
            body: () => {
                var recipientKey = ECDiffieHellman.Create();

                try {
                    recipientKey.ImportPkcs8PrivateKey(source: File.ReadAllBytes(path: keyPath), bytesRead: out _);
                } catch {
                    recipientKey.Dispose();

                    throw;
                }

                return recipientKey;
            },
            value: out var recipientKey
        )) {
            return 1;
        }

        using (recipientKey) {
            if (!TryStep(check: "cross-verify sealed: decoding the sealed payload", body: () => codec.DecodeSealedPayload(bytes: sealedClaim.PayloadBytes.Span), value: out var payload)) {
                return 1;
            }

            var expected = manifest["sealed-plaintext"];
            var failures = 0;

            if (TryStep(
                check: "cross-verify sealed",
                body: () => Encoding.UTF8.GetString(bytes: SealedCarriage.Unseal(
                    recipientPrivateKey: recipientKey,
                    payload: payload,
                    associatedData: codec.EncodeHeader(header: sealedClaim.Header)
                )),
                value: out var plaintext,
                note: "This is what a §14 derivation disagreement looks like (salt, info label, output length, tag length, or raw-versus-hashed agreement); it is indistinguishable from tampering, so check all five before suspecting the bytes."
            )) {
                if (string.Equals(a: plaintext, b: expected, comparisonType: StringComparison.Ordinal)) {
                    Console.WriteLine(value: $"[PASS] cross-verify sealed: the imported sealed payload opens to '{plaintext}' — §14's derivation agrees on both sides");
                } else {
                    failures += 1;

                    Console.WriteLine(value: $"[FAIL] cross-verify sealed: opened to '{plaintext}', the manifest expects '{expected}'");
                }
            } else {
                failures += 1;
            }

            // The AAD control: the same ciphertext against a header that differs by one field must fail.
            // Without this, an implementation that passed the wrong associated data — or none — would look
            // correct.
            failures += ExpectRefusal(
                check: "cross-verify sealed AAD",
                detail: "the same ciphertext under a one-field-different header",
                body: () => _ = SealedCarriage.Unseal(
                    recipientPrivateKey: recipientKey,
                    payload: payload,
                    associatedData: codec.EncodeHeader(header: (sealedClaim.Header with { Audience = "world:elsewhere" }))
                )
            );

            return failures;
        }
    }

    /// <summary>
    /// Runs one fixture step and turns EVERY way it can fail into a named <c>[FAIL]</c> line rather than an
    /// escaping exception. The catch is deliberately unfiltered: a step's whole job is to interpret bytes
    /// that arrived from somewhere else, so there is no exception type it could raise that is a better
    /// outcome than a reported failure. §17's tool protocol makes this normative — a crash and a refusal are
    /// different verdicts, and a cross-checking implementer cannot tell them apart from a stack trace.
    /// </summary>
    /// <typeparam name="T">What the step produces.</typeparam>
    /// <param name="check">The check's name, printed with the failure.</param>
    /// <param name="body">The step.</param>
    /// <param name="value">What the step produced, or <see langword="default"/> when it failed.</param>
    /// <param name="note">An optional sentence appended to the failure line, telling the reader what to suspect.</param>
    /// <returns><see langword="true"/> when the step completed.</returns>
    private static bool TryStep<T>(string check, Func<T> body, out T value, string? note = null) {
        try {
            value = body();

            return true;
        } catch (Exception exception) {
            Console.WriteLine(value: $"[FAIL] {check}: {Describe(exception: exception)}{((note is null) ? string.Empty : $" {note}")}");

            value = default!;

            return false;
        }
    }

    /// <summary>
    /// Runs a control that MUST refuse. A refusal reported as a verdict and a refusal raised as an exception
    /// are the same outcome here — §0's "refuse" is a negative verdict, and throw-or-return is a language
    /// choice — so both pass and only an ACCEPTANCE fails.
    /// </summary>
    /// <param name="check">The check's name.</param>
    /// <param name="detail">What was done to the input, for the report line.</param>
    /// <param name="body">The control. Returning a <see cref="CarriageVerifyResult"/> that verified is an acceptance; returning anything else is too.</param>
    /// <returns>The number of failures — 0 when the control refused.</returns>
    private static int ExpectRefusal(string check, string detail, Func<object?> body) {
        try {
            if (body() is not CarriageVerifyResult result) {
                Console.WriteLine(value: $"[FAIL] {check}: {detail} was ACCEPTED");

                return 1;
            }

            if (result.Verified) {
                Console.WriteLine(value: $"[FAIL] {check}: {detail} was ACCEPTED");

                return 1;
            }

            Console.WriteLine(value: $"[PASS] {check}: {detail} refused — {result.RefusalReason}");

            return 0;
        } catch (Exception exception) {
            Console.WriteLine(value: $"[PASS] {check}: {detail} refused — {Describe(exception: exception)}");

            return 0;
        }
    }
    private static string Describe(Exception exception) => $"{exception.GetType().Name}: {exception.Message}";

    /// <summary>
    /// Writes the manifest in the format §17 fixes: UTF-8 with no byte-order mark, one <c>key=value</c> per
    /// line, LF line terminators including a final one, and the three backslash escapes applied to values.
    /// LF rather than the platform's newline, because a fixture crossing between implementations is bytes
    /// and the platform it was minted on is not part of the contract.
    /// </summary>
    /// <param name="path">The manifest path.</param>
    /// <param name="entries">The key/value pairs to write, already in the order they should appear.</param>
    private static void WriteManifest(string path, IReadOnlyList<(string Key, string Value)> entries) {
        var builder = new StringBuilder();

        foreach (var (key, value) in entries) {
            _ = builder.Append(value: key).Append(value: '=').Append(value: EscapeManifestValue(value: value)).Append(value: '\n');
        }

        File.WriteAllText(path: path, contents: builder.ToString(), encoding: s_manifestEncoding);
    }

    /// <summary>
    /// Reads the manifest by §17's rules. Empty lines are ignored; every other line MUST hold a
    /// <c>key=value</c> pair split at its FIRST <c>=</c>; a repeated key REFUSES rather than resolving by
    /// order. Silently skipping a line that does not parse is what makes a typo'd key read as an absent one,
    /// which is a fixture that quietly checks less than it claims.
    /// </summary>
    /// <param name="path">The manifest path.</param>
    /// <returns>The parsed keys, values already unescaped.</returns>
    /// <exception cref="FormatException">A line does not parse, a key repeats, or a value carries an escape the format does not define.</exception>
    private static Dictionary<string, string> ReadManifest(string path) {
        var manifest = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        var text = File.ReadAllText(path: path, encoding: s_manifestEncoding);
        var lineNumber = 0;

        foreach (var rawLine in text.Split(separator: '\n')) {
            // A CR immediately before the LF is tolerated and discarded, so a manifest written by a CRLF
            // platform still reads. Nothing else about the line is trimmed — whitespace inside a value is
            // part of the value.
            var line = (rawLine.EndsWith(value: '\r') ? rawLine[..^1] : rawLine);

            lineNumber += 1;

            if (line.Length == 0) {
                continue;
            }

            var separator = line.IndexOf(value: '=');

            if (separator < 1) {
                throw new FormatException(message: $"{ManifestFileName} line {lineNumber} is neither empty nor a 'key=value' pair: '{line}'.");
            }

            var key = line[..separator];

            if (!manifest.TryAdd(key: key, value: UnescapeManifestValue(value: line[(separator + 1)..], key: key))) {
                throw new FormatException(message: $"{ManifestFileName} names the key '{key}' more than once (line {lineNumber}); which one governs is undefined, so the manifest is refused rather than resolved by order.");
            }
        }

        return manifest;
    }

    /// <summary>Applies §17's three value escapes: a backslash, a line feed, and a carriage return. Nothing else is escaped — <c>=</c> needs no escape, because only the first one on a line splits.</summary>
    /// <param name="value">The raw value.</param>
    private static string EscapeManifestValue(string value) {
        var builder = new StringBuilder(capacity: value.Length);

        foreach (var character in value) {
            _ = character switch {
                '\\' => builder.Append(value: @"\\"),
                '\n' => builder.Append(value: @"\n"),
                '\r' => builder.Append(value: @"\r"),
                _ => builder.Append(value: character),
            };
        }

        return builder.ToString();
    }

    /// <summary>Reverses <see cref="EscapeManifestValue"/>, refusing an escape the format does not define rather than passing the backslash through — one escaped value must have exactly one unescaped reading.</summary>
    /// <param name="value">The escaped value.</param>
    /// <param name="key">The key it belongs to, for the refusal message.</param>
    /// <exception cref="FormatException">The value ends in a lone backslash, or names an escape outside the three.</exception>
    private static string UnescapeManifestValue(string value, string key) {
        if (!value.Contains(value: '\\')) {
            return value;
        }

        var builder = new StringBuilder(capacity: value.Length);

        for (var index = 0; (index < value.Length); index += 1) {
            if (value[index] != '\\') {
                _ = builder.Append(value: value[index]);

                continue;
            }

            index += 1;

            if (index == value.Length) {
                throw new FormatException(message: $"{ManifestFileName}'s '{key}' value ends with a lone backslash; the format defines exactly three escapes (\\\\, \\n, \\r).");
            }

            _ = value[index] switch {
                '\\' => builder.Append(value: '\\'),
                'n' => builder.Append(value: '\n'),
                'r' => builder.Append(value: '\r'),
                var other => throw new FormatException(message: $"{ManifestFileName}'s '{key}' value carries the escape '\\{other}', which is not one of the three the format defines (\\\\, \\n, \\r)."),
            };
        }

        return builder.ToString();
    }
}

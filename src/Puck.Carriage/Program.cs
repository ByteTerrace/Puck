using System.Formats.Cbor;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Puck.Carriage;

/// <summary>
/// The prototype's verification harness (README.md, "Signed carriage"). Not a persisted test
/// project — this console entry point IS the prototype's proof, run with
/// <c>dotnet run --project src/Puck.Carriage -c Release</c>. Every scenario prints PASS or FAIL and the
/// process exits non-zero if anything failed.
/// </summary>
/// <remarks>
/// Two extra modes drive the cross-implementation check against the other independent implementation of
/// README.md: <c>export &lt;directory&gt;</c> mints a chain to files, and
/// <c>verify &lt;directory&gt;</c> pins an exported root and verifies the chain beneath it. See
/// <see cref="CarriageInterchange"/>.
/// </remarks>
internal static class Program {
    private static int Passed;
    private static int Failed;

    /// <summary>
    /// The harness entry point, and the two interchange verbs README.md §17 fixes.
    /// </summary>
    /// <remarks>
    /// The exit-code contract is normative (§17): <b>0</b> when every check passed, <b>1</b> when at least
    /// one failed, <b>2</b> when the command line was not understood. Terminating any other way — an
    /// unhandled exception above all — is not a permitted way to report a failed check, because a crash and
    /// a refusal are different verdicts and a cross-checking implementer cannot tell them apart from a stack
    /// trace. Both verbs are already guarded internally; the catch here is the backstop that makes the
    /// contract unconditional.
    /// </remarks>
    private static int Main(string[] args) {
        if (
            (args.Length == 2) &&
            string.Equals(
            a: args[0],
            b: "export",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return CarriageInterchange.Export(directory: args[1]);
        }

        if (
            (args.Length == 2) &&
            string.Equals(
            a: args[0],
            b: "verify",
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return CarriageInterchange.Verify(directory: args[1]);
        }

        if (args.Length != 0) {
            Console.Error.WriteLine(value: "usage: Puck.Carriage [export <directory> | verify <directory>]");

            return 2;
        }

        Console.WriteLine(value: "Puck.Carriage — signed carriage envelope prototype");
        Console.WriteLine(value: "====================================================");

        RunCoreScenarios(codec: new FixedLayoutCarriageCodec());
        RunCoreScenarios(codec: new CborCarriageCodec());
        RunDepthAndTrustShapeScenarios();
        RunClockAndSequenceScenarios();
        RunParserLaxityScenarios();
        RunArrivedBytesScenarios();
        RunSignatureLevelScenarios();
        RunSerialisationCrossCheck();
        RunConformanceProfileScenarios();
        RunSealedCarriageScenarios();
        RunInterchangeRoundTrip();
        RunSizeAndComplexityReport();

        Console.WriteLine();
        Console.WriteLine(value: $"=== SUMMARY: {Passed} passed, {Failed} failed ===");

        return ((Failed == 0)
            ? 0
            : 1);
    }

    // ---- Reporting -----------------------------------------------------------------------------------

    private static void Check(string scenario, bool ok, string detail) {
        if (ok) {
            Passed += 1;
            Console.WriteLine(value: $"[PASS] {scenario} — {detail}");
        } else {
            Failed += 1;
            Console.WriteLine(value: $"[FAIL] {scenario} — {detail}");
        }
    }
    private static void ExpectAccept(string scenario, CarriageVerifyResult result) =>
        Check(
        scenario: scenario,
        ok: result.Verified,
        detail: (result.Verified
        ? "verified, as expected"
        : $"unexpectedly refused: {result.RefusalReason}")
    );
    private static void ExpectRefuse(string scenario, CarriageVerifyResult result, string reasonMustContain) {
        var containsExpected = ((result.RefusalReason is not null) && result.RefusalReason.Contains(
            value: reasonMustContain,
            comparisonType: StringComparison.OrdinalIgnoreCase
        ));
        var ok = (!result.Verified && containsExpected);
        var detail = (result.Verified
            ? "unexpectedly ACCEPTED"
            : (containsExpected
                ? $"refused as expected: {result.RefusalReason}"
                : $"refused, but not for the expected reason (wanted a reason containing '{reasonMustContain}'): {result.RefusalReason}"));

        Check(
            scenario: scenario,
            ok: ok,
            detail: detail
        );
    }

    /// <summary>Asserts that an operation throws <typeparamref name="TException"/> and that the message says why in the expected terms.</summary>
    private static void ExpectThrows<TException>(string scenario, Action action, string messageMustContain)
        where TException : Exception {
        try {
            action();
        } catch (TException exception) {
            var containsExpected = exception.Message.Contains(
                value: messageMustContain,
                comparisonType: StringComparison.OrdinalIgnoreCase
            );

            Check(
                scenario: scenario,
                ok: containsExpected,
                detail: (containsExpected
                ? $"refused as expected: {exception.Message}"
                : $"threw {typeof(TException).Name}, but not for the expected reason (wanted '{messageMustContain}'): {exception.Message}")
            );

            return;
        } catch (Exception exception) {
            Check(
                scenario: scenario,
                ok: false,
                detail: $"threw {exception.GetType().Name} rather than {typeof(TException).Name}: {exception.Message}"
            );

            return;
        }

        Check(
            scenario: scenario,
            ok: false,
            detail: $"did NOT throw — {typeof(TException).Name} was expected"
        );
    }

    /// <summary>Asserts that an operation completes without throwing — the control every refusal case needs.</summary>
    private static void ExpectNoThrow(string scenario, Action action, string detail) {
        try {
            action();
        } catch (Exception exception) {
            Check(
                scenario: scenario,
                ok: false,
                detail: $"unexpectedly threw {exception.GetType().Name}: {exception.Message}"
            );

            return;
        }

        Check(
            scenario: scenario,
            ok: true,
            detail: detail
        );
    }

    // ---- Domain / key material -------------------------------------------------------------------------

    /// <summary>One minted domain's raw key material: root, issuing, and a subject's signing and sealing keys — all sharing the domain's root fingerprint.</summary>
    private sealed record DomainKeys(
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

    /// <summary>
    /// Mints a fresh domain's whole key set. Minting is randomised and happens outside the tick — see
    /// <see cref="CarriageSigner"/>'s remarks — so every call produces a distinct domain even for the same
    /// subject string. Keys are intentionally never disposed here: this is a short-lived harness process,
    /// not production minting code.
    /// </summary>
    private static DomainKeys MintDomainKeys(string subject) {
        var rootKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var rootSpki = rootKey.ExportSubjectPublicKeyInfo();
        var rootId = KeyId.ForRoot(
            subjectPublicKeyInfo: rootSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        var issuingKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var issuingSpki = issuingKey.ExportSubjectPublicKeyInfo();
        var issuingId = KeyId.ForIssuing(
            domain: rootId.Domain,
            subjectPublicKeyInfo: issuingSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        var subjectSigningKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSigningSpki = subjectSigningKey.ExportSubjectPublicKeyInfo();
        var subjectSigningId = KeyId.ForSubject(
            domain: rootId.Domain,
            subject: subject,
            subjectPublicKeyInfo: subjectSigningSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        var subjectSealingKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP256);
        var subjectSealingSpki = subjectSealingKey.ExportSubjectPublicKeyInfo();
        var subjectSealingId = KeyId.ForSubject(
            domain: rootId.Domain,
            subject: subject,
            subjectPublicKeyInfo: subjectSealingSpki,
            algorithm: CarriageAlgorithms.EcdhP256HkdfSha256Aes256Gcm
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
    private static (SignedCarriageEnvelope RootToIssuing, SignedCarriageEnvelope IssuingToSubject) BuildChain(ICarriageCodec codec, DomainKeys keys, long notBefore, long notAfter) {
        var rootToIssuing = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.RootKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: keys.IssuingId,
            targetSubjectPublicKeyInfo: keys.IssuingSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );

        var issuingToSubject = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.IssuingKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: keys.SubjectSigningId,
            targetSubjectPublicKeyInfo: keys.SubjectSigningSpki,
            notBefore: notBefore,
            notAfter: notAfter
        );

        return (rootToIssuing, issuingToSubject);
    }

    /// <summary>The reach every ordinary harness trust list authors, so a scenario that does not care about scoping still carries a real one.</summary>
    private static readonly IReadOnlySet<string> DefaultReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:wallet", "slot:title" };

    private static TrustList BuildTrustList(DomainKeys keys, TimeSpan? defaultMaximumAge, IReadOnlySet<string>? reach = null) {
        var entry = new TrustListEntry(
            PinnedId: keys.RootId,
            PublicKeySubjectPublicKeyInfo: keys.RootSpki,
            Mode: CarriageTrustMode.Vouches,
            Reach: (reach ?? DefaultReach),
            MaximumAge: null
        );

        return new TrustList(
            entries: [entry],
            defaultMaximumAge: defaultMaximumAge,
            replayAcceptanceHorizon: defaultMaximumAge
        );
    }

    // ---- Core scenario matrix, run once per codec -------------------------------------------------------

    private static void RunCoreScenarios(ICarriageCodec codec) {
        Console.WriteLine();
        Console.WriteLine(value: $"=== Core scenarios ({codec.Name}) ===");

        const long Epoch = 1_700_000_000L;
        const long BindingNotBefore = (Epoch - 30L);
        const long BindingNotAfter = (Epoch + (86_400L * 30));

        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keysA = MintDomainKeys(subject: "user:alice");
        var keysB = MintDomainKeys(subject: "user:bob");

        var (rootToIssuingA, issuingToSubjectA) = BuildChain(
            codec: codec,
            keys: keysA,
            notBefore: BindingNotBefore,
            notAfter: BindingNotAfter
        );
        var (rootToIssuingB, issuingToSubjectB) = BuildChain(
            codec: codec,
            keys: keysB,
            notBefore: BindingNotBefore,
            notAfter: BindingNotAfter
        );
        var chainA = new[] { rootToIssuingA, issuingToSubjectA };
        var chainB = new[] { rootToIssuingB, issuingToSubjectB };
        var trustA = BuildTrustList(
            keys: keysA,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var trustB = BuildTrustList(
            keys: keysB,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );

        // 1. Happy path.
        var happyClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "hello from alice"
        );
        var happyResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: happyClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] happy path: claim + chain verifies",
            result: happyResult
        );

        // 2. Algorithm confusion: the header LIES about its algorithm; the real signature was produced
        // (and must be verified) under the algorithm the trust chain pins, never under what the header says.
        var confusedClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "algorithm-confused claim",
            declaredAlgorithm: CarriageAlgorithms.EcdsaP256Sha384
        );
        var confusedResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: confusedClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] algorithm confusion: header declares sha384, pinned subject key is sha256 (control: see happy path above)",
            result: confusedResult,
            reasonMustContain: "algorithm confusion"
        );

        // 3. Purpose replay: present a key-binding envelope AS a claim.
        var purposeReplayResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: issuingToSubjectA,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] purpose replay: a key-binding envelope presented as a claim (control: see happy path above)",
            result: purposeReplayResult,
            reasonMustContain: "purpose"
        );

        // 4. Cross-domain: domain B's own, fully self-consistent claim, verified against a trust list that only trusts domain A.
        var domainBClaim = SignTestClaim(
            codec: codec,
            keys: keysB,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "hello from bob"
        );
        var crossDomainResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: domainBClaim,
            chain: chainB,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] cross-domain: domain B's claim against a trust list that only trusts domain A",
            result: crossDomainResult,
            reasonMustContain: "trusted vouching root"
        );
        var crossDomainControlResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: domainBClaim,
            chain: chainB,
            trustList: trustB,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] cross-domain control: domain B's claim against domain B's own trust list",
            result: crossDomainControlResult
        );

        // 5. Expired window (issuer's own NotAfter has passed).
        var expiredClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 7_200),
            notAfter: (Epoch - 100),
            audience: "world:home",
            sequence: null,
            text: "stale claim"
        );
        var expiredResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: expiredClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] expired window: claim's own notAfter has passed",
            result: expiredResult,
            reasonMustContain: "expired"
        );
        var freshClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 100),
            notAfter: (Epoch + 100),
            audience: "world:home",
            sequence: null,
            text: "fresh claim"
        );
        var freshResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: freshClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] expired window control: claim within its own window verifies",
            result: freshResult
        );

        // 6. Tighter-of-two, direction 1: the ISSUER's window is the tighter one and governs.
        var trustGenerous = BuildTrustList(
            keys: keysA,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var tightIssuerClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 30),
            notAfter: (Epoch + 60),
            audience: "world:home",
            sequence: null,
            text: "tight issuer window"
        );
        var tightIssuerOkResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: tightIssuerClaim,
            chain: chainA,
            trustList: trustGenerous,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] tighter-of-two (issuer tighter) control: within the issuer's short window",
            result: tightIssuerOkResult
        );
        var laterNow = DateTimeOffset.FromUnixTimeSeconds(seconds: (Epoch + 200));
        var tightIssuerExpiredResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: tightIssuerClaim,
            chain: chainA,
            trustList: trustGenerous,
            now: laterNow,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] tighter-of-two: the issuer's 90-second window governs even though the verifier would allow 24h",
            result: tightIssuerExpiredResult,
            reasonMustContain: "issuer"
        );

        // 7. Tighter-of-two, direction 2: the VERIFIER's CLAIM maximum age is the tighter one and governs.
        // Binding ages are separate policy: this short claim cadence never forces the cold root to re-sign.
        var trustTight = BuildTrustList(
            keys: keysA,
            defaultMaximumAge: TimeSpan.FromHours(hours: 1)
        );
        var tightVerifierClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 100),
            notAfter: (Epoch + (86_400L * 30)),
            audience: "world:home",
            sequence: null,
            text: "tight verifier ceiling"
        );
        var tightVerifierOkResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: tightVerifierClaim,
            chain: chainA,
            trustList: trustTight,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] tighter-of-two (verifier tighter) control: within the verifier's 1-hour ceiling",
            result: tightVerifierOkResult
        );
        var muchLaterNow = DateTimeOffset.FromUnixTimeSeconds(seconds: (Epoch + 7_200));
        var tightVerifierExpiredResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: tightVerifierClaim,
            chain: chainA,
            trustList: trustTight,
            now: muchLaterNow,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] tighter-of-two: the verifier's 1-hour ceiling governs even though the issuer allows 30 days",
            result: tightVerifierExpiredResult,
            reasonMustContain: "verifier"
        );

        var rootAgedTrust = new TrustList(
            entries: [new TrustListEntry(
                PinnedId: keysA.RootId,
                PublicKeySubjectPublicKeyInfo: keysA.RootSpki,
                Mode: CarriageTrustMode.Vouches,
                Reach: DefaultReach,
                MaximumAge: TimeSpan.FromDays(value: 30),
                RootBindingMaximumAge: TimeSpan.FromHours(hours: 1),
                SubjectBindingMaximumAge: TimeSpan.FromDays(value: 7)
            )],
            defaultMaximumAge: null
        );
        var rootAgedResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: tightVerifierClaim,
            chain: chainA,
            trustList: rootAgedTrust,
            now: muchLaterNow,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] separated lifetime policy: a 1-hour root-binding ceiling refuses the aged root hop independently of the 30-day claim ceiling",
            result: rootAgedResult,
            reasonMustContain: "root-vouches-issuing"
        );

        // 8. Missing chain: a claim arrives with no bindings at all.
        var missingChainResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: happyClaim,
            chain: null,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] missing chain: claim presented with no bindings (control: see happy path above)",
            result: missingChainResult,
            reasonMustContain: "missing chain"
        );

        // 9. Broken chain: the issuing-vouches-subject binding is absent.
        var brokenChain = new[] { rootToIssuingA };
        var brokenChainResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: happyClaim,
            chain: brokenChain,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] broken chain: issuing-vouches-subject binding absent (control: see happy path above)",
            result: brokenChainResult,
            reasonMustContain: "broken chain"
        );

        // 10. Audience mismatch.
        var marketClaim = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:market",
            sequence: null,
            text: "market claim"
        );
        var audienceOkResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: marketClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:market"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] audience mismatch control: expected audience matches",
            result: audienceOkResult
        );
        var audienceMismatchResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: marketClaim,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:elsewhere"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] audience mismatch: claim bound to world:market, verifier is world:elsewhere",
            result: audienceMismatchResult,
            reasonMustContain: "audience mismatch"
        );

        // 11. Bearer sequence replay: equal AND lower are both refused; a higher sequence still advances.
        var sequenceStore = new ReplayTestStore();
        var bearer10 = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 10UL,
            text: "bearer sequence 10"
        );
        var bearer10Result = sequenceStore.Commit(CarriageVerifier.VerifyChain(
            codec: codec,
            claim: bearer10,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.bearer",
            expectedAudience: null
        ));

        ExpectAccept(
            scenario: $"[{codec.Name}] bearer sequence control: first use of sequence 10 accepted",
            result: bearer10Result
        );

        var bearerEqual = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 10UL,
            text: "bearer sequence 10 replay"
        );
        var bearerEqualResult = sequenceStore.Commit(CarriageVerifier.VerifyChain(
            codec: codec,
            claim: bearerEqual,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.bearer",
            expectedAudience: null
        ));

        ExpectRefuse(
            scenario: $"[{codec.Name}] bearer sequence replay: equal sequence (10 after 10) refused",
            result: bearerEqualResult,
            reasonMustContain: "replay"
        );

        var bearerLower = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 5UL,
            text: "bearer sequence 5 replay"
        );
        var bearerLowerResult = sequenceStore.Commit(CarriageVerifier.VerifyChain(
            codec: codec,
            claim: bearerLower,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.bearer",
            expectedAudience: null
        ));

        ExpectRefuse(
            scenario: $"[{codec.Name}] bearer sequence replay: lower sequence (5 after 10) refused",
            result: bearerLowerResult,
            reasonMustContain: "replay"
        );

        var bearer11 = SignTestClaim(
            codec: codec,
            keys: keysA,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 11UL,
            text: "bearer sequence 11"
        );
        var bearer11Result = sequenceStore.Commit(CarriageVerifier.VerifyChain(
            codec: codec,
            claim: bearer11,
            chain: chainA,
            trustList: trustA,
            now: now,
            expectedPurpose: "test.bearer",
            expectedAudience: null
        ));

        ExpectAccept(
            scenario: $"[{codec.Name}] bearer sequence control: higher sequence (11 after 10) accepted, mark advances",
            result: bearer11Result
        );
    }
    private static SignedCarriageEnvelope SignTestClaim(
        ICarriageCodec codec,
        DomainKeys keys,
        string purpose,
        long notBefore,
        long notAfter,
        string? audience,
        ulong? sequence,
        string text,
        string? declaredAlgorithm = null
    ) =>
        CarriageSigner.SignClaim(
        codec: codec,
        domain: keys.Domain,
        subject: keys.Subject,
        signerKey: keys.SubjectSigningKey,
        signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
        purpose: purpose,
        notBefore: notBefore,
        notAfter: notAfter,
        audience: audience,
        sequence: sequence,
        claimBytes: Encoding.UTF8.GetBytes(s: text),
        declaredAlgorithm: declaredAlgorithm
    );

    // ---- Chain depth, trust shape, and slot reach --------------------------------------------------------

    /// <summary>
    /// The depth rule and the trust list's own shape rules. Codec-independent — every check here is about
    /// what the verifier and the trust list will admit, not about bytes — so this runs once, under the
    /// fixed layout, rather than twice.
    /// </summary>
    private static void RunDepthAndTrustShapeScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Chain depth, trust shape, and slot reach ===");

        const long Epoch = 1_700_000_000L;

        var codec = new FixedLayoutCarriageCodec();
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:frank");

        var (rootToIssuing, issuingToSubject) = BuildChain(
            codec: codec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        Check(
            scenario: "binding-age compatibility: an omitted binding-specific default inherits the authored default ceiling",
            ok: (
                trust.DefaultRootBindingMaximumAge == trust.DefaultMaximumAge &&
                trust.DefaultSubjectBindingMaximumAge == trust.DefaultMaximumAge
            ),
            detail: "splitting lifetime policy does not silently remove an existing verifier ceiling"
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "frank's claim"
        );

        var twoHopResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: "depth control: exactly two bindings under a vouching root",
            result: twoHopResult
        );

        // Reach is reported with the verdict, never enforced — the engine decides what a verified claim may
        // touch, and it can only do that if the verifier hands the authored scope back.
        var reachReported = ((twoHopResult.Reach is not null) && twoHopResult.Reach.SetEquals(other: DefaultReach));

        Check(
            scenario: "slot reach: a verified claim carries the admitting entry's authored reach",
            ok: reachReported,
            detail: (reachReported
            ? $"reach reported as [{string.Join(
                separator: ", ",
                values: twoHopResult.Reach!
            )}] — the result encapsulates the authored scope behind slot-scoped admission queries"
            : $"reach was [{((twoHopResult.Reach is null)
                ? "(null)"
                : string.Join(
                separator: ", ",
                values: twoHopResult.Reach
            ))}], expected the authored set")
        );

        Check(
            scenario: "slot reach: Admits answers the question a receiving world actually has",
            ok: (twoHopResult.Admits(slot: "slot:wallet") && !twoHopResult.Admits(slot: "slot:unlisted")),
            detail: "Admits('slot:wallet')=true, Admits('slot:unlisted')=false — a verified claim reaches its entry's authored slots and no others"
        );

        var narrowTrust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24),
            reach: new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:title" }
        );
        var narrowResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: narrowTrust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );
        var narrowReported = (narrowResult.Verified && narrowResult.Admits(slot: "slot:title") && !narrowResult.Admits(slot: "slot:wallet"));

        Check(
            scenario: "slot reach: a narrower entry yields a narrower reach for the SAME claim (control: the case above)",
            ok: narrowReported,
            detail: (narrowReported
            ? "the identical claim now admits slot:title and not slot:wallet — reach is the verifier's authored scope, not a property of the claim"
            : $"unexpected: verified={narrowResult.Verified}, reach=[{((narrowResult.Reach is null)
                ? "(null)"
                : string.Join(
                separator: ", ",
                values: narrowResult.Reach
            ))}]")
        );

        // Deny by default, and the trap that goes with it: this claim VERIFIES. Everything about it is
        // sound — signature, chain, window, audience — and it may still touch nothing, because the entry
        // that admitted it reaches nothing. A caller branching on the verdict alone would let it through,
        // which is why the field is `Verified` rather than `Accepted` and why `Admits` exists.
        var emptyReachTrust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24),
            reach: new HashSet<string>(comparer: StringComparer.Ordinal)
        );
        var emptyReachResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: emptyReachTrust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );
        var emptyReachReported = (emptyReachResult.Verified && (emptyReachResult.Reach is not null) && (emptyReachResult.Reach.Count == 0));

        Check(
            scenario: "slot reach: deny by default — an entry with no reach yields a verified claim that reaches nothing",
            ok: emptyReachReported,
            detail: (emptyReachReported
            ? "verified with an empty reach — a signature that proves who said it and nothing about what it may touch"
            : $"unexpected: verified={emptyReachResult.Verified}, reach count={(emptyReachResult.Reach?.Count.ToString() ?? "(null)")}")
        );

        Check(
            scenario: "slot reach: VERIFIED is not admission — the empty-reach claim above admits no slot at all",
            ok: (emptyReachResult.Verified && !emptyReachResult.Admits(slot: "slot:wallet") && !emptyReachResult.Admits(slot: "slot:title") && !emptyReachResult.Admits(slot: "")),
            detail: "Verified=true and every Admits(...) is false — 'it verified' and 'it may act' are different questions, and only the second one is admission"
        );

        var refusedResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.other",
            expectedAudience: "world:home"
        );

        Check(
            scenario: "slot reach control: a REFUSED claim admits nothing either, and carries no reach to inspect",
            ok: (!refusedResult.Verified && !refusedResult.Admits(slot: "slot:wallet") && (refusedResult.Reach is null)),
            detail: "a refusal has no scope — there is no verified claim to scope"
        );

        // Purpose is the ONLY thing separating one signature use from another, so a claim minted for one
        // purpose must not satisfy a caller expecting a different one — the key-binding case above is just
        // the reserved instance of this rule, not the whole of it.
        var otherPurposeResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.other",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "purpose separation: a 'test.claim' envelope presented where 'test.other' is expected (control: depth control above)",
            result: otherPurposeResult,
            reasonMustContain: "purpose mismatch"
        );

        // Payload-kind separation: the same envelope re-encoded with its payload kind rewritten — which is
        // what an attacker actually puts on the wire, bytes and model together. The signature will not
        // verify either way, payload kind being inside the signed portion, but the kind is checked FIRST,
        // so the engine never even reaches a decode that would read chain bytes as game data.
        var kindConfused = SignedCarriageEnvelope.Reencode(
            codec: codec,
            header: claim.Header,
            payloadKind: CarriagePayloadKind.KeyBinding,
            payloadBytes: claim.PayloadBytes,
            signature: claim.Signature
        );
        var kindConfusedResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: kindConfused,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "payload-kind separation: a claim declaring a key-binding payload",
            result: kindConfusedResult,
            reasonMustContain: "payload kind must be opaque or sealed"
        );

        // Deny by default at the VERIFIER, for a kind outside the closed set. Reaching this needs an
        // envelope built in memory, because the decoders now refuse such a kind outright (see the parser
        // laxity section) — which is the point: two independent refusals, and the wire never gets past the
        // first one.
        var unknownKind = SignedCarriageEnvelope.Reencode(
            codec: codec,
            header: claim.Header,
            payloadKind: (CarriagePayloadKind)99,
            payloadBytes: claim.PayloadBytes,
            signature: claim.Signature
        );
        var unknownKindResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: unknownKind,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "payload-kind separation: a claim declaring a payload kind outside the enum, deny by default",
            result: unknownKindResult,
            reasonMustContain: "payload kind must be opaque or sealed"
        );

        // Depth three: the SUBJECT key vouches for a further key, which then signs the claim. This is the
        // unbounded chain the doc refuses by name, presented in its most plausible form.
        using var delegateKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var delegateSpki = delegateKey.ExportSubjectPublicKeyInfo();
        var delegateId = KeyId.ForSubject(
            domain: keys.Domain,
            subject: "user:frank",
            subjectPublicKeyInfo: delegateSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );
        var subjectToDelegate = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: delegateId,
            targetSubjectPublicKeyInfo: delegateSpki,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var delegateClaim = CarriageSigner.SignClaim(
            codec: codec,
            domain: keys.Domain,
            subject: "user:frank",
            signerKey: delegateKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            claimBytes: Encoding.UTF8.GetBytes(s: "claim signed one hop too deep")
        );
        var depthThreeResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: delegateClaim,
            chain: [rootToIssuing, issuingToSubject, subjectToDelegate],
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "depth three: a subject key vouches for a further key that signs the claim (control: depth control above)",
            result: depthThreeResult,
            reasonMustContain: "broken chain"
        );

        // Depth one in disguise: the root vouches for ITSELF as the issuing key, then signs the subject
        // binding. Structurally two hops, but the cold root is back to signing per subject.
        var rootAsIssuingId = KeyId.ForIssuing(
            domain: keys.Domain,
            subjectPublicKeyInfo: keys.RootSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );
        var rootVouchesItself = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.RootKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: rootAsIssuingId,
            targetSubjectPublicKeyInfo: keys.RootSpki,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var rootVouchesSubject = CarriageSigner.SignKeyBinding(
            codec: codec,
            domain: keys.Domain,
            signerKey: keys.RootKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            targetId: keys.SubjectSigningId,
            targetSubjectPublicKeyInfo: keys.SubjectSigningSpki,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var rootAsIssuingResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: [rootVouchesItself, rootVouchesSubject],
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "depth one in disguise: the root vouches for itself as the issuing key (control: depth control above)",
            result: rootAsIssuingResult,
            reasonMustContain: "depth one in disguise"
        );

        // A directly-pinned subject key: zero hops, because nothing is vouched for.
        var friendReach = new HashSet<string>(comparer: StringComparer.Ordinal) { "slot:friend" };
        var directTrust = new TrustList(
            entries: [
                new TrustListEntry(
                PinnedId: keys.SubjectSigningId,
                PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                Mode: CarriageTrustMode.SignsDirectly,
                Reach: friendReach,
                MaximumAge: null
            ),
            ],
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var directResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: null,
            trustList: directTrust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: "depth zero control: a directly-pinned subject key admits its own claim with no bindings",
            result: directResult
        );

        var directWithChainResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: claim,
            chain: chain,
            trustList: directTrust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "depth zero: bindings attached to a directly-pinned claim are refused, never ignored",
            result: directWithChainResult,
            reasonMustContain: "no bindings"
        );

        // The domain check compares the claim's domain against the pinned entry's domain, never against itself
        // (§11 step 3) — read as "the domain the claim expects", the check would be a tautology that always
        // accepts. This scenario differs from the depth control at the top of this method in exactly one field:
        // the same subject, key, bindings, trust list, and a genuine signature — but a header naming a domain
        // nothing pins. A check against the pin finds no entry addressing that domain and refuses before any
        // signature work.
        var foreignDomainKeys = MintDomainKeys(subject: keys.Subject);
        var foreignDomainClaim = CarriageSigner.SignClaim(
            codec: codec,
            domain: foreignDomainKeys.Domain,
            subject: keys.Subject,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            claimBytes: Encoding.UTF8.GetBytes(s: "frank's key, somebody else's domain")
        );
        var foreignDomainResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: foreignDomainClaim,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "pinned domain: a claim whose header names a domain no entry pins, carried by the pinned domain's own chain and signed by its own subject key (control: depth control above)",
            result: foreignDomainResult,
            reasonMustContain: "not a trusted vouching root"
        );

        // Trust list shape. Every one of these would otherwise reach the verifier as a list whose pin is
        // decorative — the key bytes would do the verifying while the id sat beside them unenforced.
        ExpectNoThrow(
            scenario: "trust list control: a self-consistent vouching entry constructs",
            action: () => _ = BuildTrustList(
                keys: keys,
                defaultMaximumAge: null
            ),
            detail: "constructed, as expected"
        );
        ExpectThrows<ArgumentException>(
            scenario: "trust list: an entry whose key bytes do not hash to its pinned id",
            action: () => _ = new TrustList(
                entries: [new TrustListEntry(
                    PinnedId: keys.RootId,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                    Mode: CarriageTrustMode.Vouches,
                    Reach: friendReach,
                    MaximumAge: null
                )],
                defaultMaximumAge: null
            ),
            messageMustContain: "not self-certifying"
        );
        ExpectThrows<ArgumentException>(
            scenario: "trust list: a vouching entry that pins an issuing key rather than a root",
            action: () => _ = new TrustList(
                entries: [new TrustListEntry(
                    PinnedId: keys.IssuingId,
                    PublicKeySubjectPublicKeyInfo: keys.IssuingSpki,
                    Mode: CarriageTrustMode.Vouches,
                    Reach: friendReach,
                    MaximumAge: null
                )],
                defaultMaximumAge: null
            ),
            messageMustContain: "must pin a root id"
        );
        ExpectThrows<ArgumentException>(
            scenario: "trust list: a directly-signing entry that pins a root rather than a subject key",
            action: () => _ = new TrustList(
                entries: [new TrustListEntry(
                    PinnedId: keys.RootId,
                    PublicKeySubjectPublicKeyInfo: keys.RootSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                )],
                defaultMaximumAge: null
            ),
            messageMustContain: "must pin a SUBJECT key"
        );
        ExpectThrows<ArgumentException>(
            scenario: "trust list: an entry pinning a SEALING key, which can never admit a claim",
            action: () => _ = new TrustList(
                entries: [new TrustListEntry(
                    PinnedId: keys.SubjectSealingId,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                )],
                defaultMaximumAge: null
            ),
            messageMustContain: "not a carriage SIGNING algorithm"
        );
        ExpectThrows<ArgumentException>(
            scenario: "trust list: the same domain pinned twice in one mode, so which reach governs is undefined",
            action: () => {
                var entry = new TrustListEntry(
                    PinnedId: keys.RootId,
                    PublicKeySubjectPublicKeyInfo: keys.RootSpki,
                    Mode: CarriageTrustMode.Vouches,
                    Reach: friendReach,
                    MaximumAge: null
                );

                _ = new TrustList(
                    entries: [entry, entry],
                    defaultMaximumAge: null
                );
            },
            messageMustContain: "twice in the same mode"
        );

        // The collision rule is slot identity, not key identity: lookups match on (domain, subject, mode) and
        // return the first hit, so a second entry in one slot could never be reached and its reach could never
        // govern. Two different keys for one subject is therefore the collision, and refusing it is refusing a
        // list with an inert entry in it.
        using var rotatedKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

        var rotatedSpki = rotatedKey.ExportSubjectPublicKeyInfo();
        var rotatedId = KeyId.ForSubject(
            domain: keys.Domain,
            subject: keys.Subject,
            subjectPublicKeyInfo: rotatedSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        ExpectThrows<ArgumentException>(
            scenario: "trust list: two DIFFERENT keys pinned for one subject in one mode (a rotation overlap) — the rule is slot identity, not key identity",
            action: () => _ = new TrustList(
                entries: [
                    new TrustListEntry(
                    PinnedId: keys.SubjectSigningId,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                ),
                    new TrustListEntry(
                    PinnedId: rotatedId,
                    PublicKeySubjectPublicKeyInfo: rotatedSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                ),
                ],
                defaultMaximumAge: null
            ),
            messageMustContain: "lookup returns the first match"
        );
        ExpectNoThrow(
            scenario: "trust list control: the SAME two keys in DIFFERENT slots (two subjects) construct fine",
            action: () => _ = new TrustList(
                entries: [
                    new TrustListEntry(
                    PinnedId: keys.SubjectSigningId,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                ),
                    new TrustListEntry(
                    PinnedId: KeyId.ForSubject(
                        domain: keys.Domain,
                        subject: "user:other",
                        subjectPublicKeyInfo: rotatedSpki,
                        algorithm: CarriageAlgorithms.EcdsaP256Sha256
                    ),
                    PublicKeySubjectPublicKeyInfo: rotatedSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: friendReach,
                    MaximumAge: null
                ),
                ],
                defaultMaximumAge: null
            ),
            detail: "constructed — it is the SLOT that must be unique, so distinct subjects never collide"
        );
    }

    // ---- Clock skew and the sequence mark ---------------------------------------------------------------

    /// <summary>
    /// The window's boundaries and the sequence mark's real scope. The interesting case is the last pair:
    /// a directed claim carrying a sequence is checked against the mark exactly as a bearer claim is,
    /// because binding an audience defends against replay elsewhere and never against replay at the
    /// audience itself.
    /// </summary>
    private static void RunClockAndSequenceScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Clock boundaries and the sequence mark ===");

        const long Epoch = 1_700_000_000L;

        var codec = new CborCarriageCodec();
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:gwen");

        var (rootToIssuing, issuingToSubject) = BuildChain(
            codec: codec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );

        CarriageVerifyResult VerifyAt(SignedCarriageEnvelope envelope, ReplayTestStore? store) {
            var result = CarriageVerifier.VerifyChain(
                codec: codec,
                claim: envelope,
                chain: chain,
                trustList: trust,
                now: now,
                expectedPurpose: "test.claim",
                expectedAudience: "world:home"
            );

            return (store is null) ? result : store.Commit(result: result);
        }

        // There is no skew tolerance: notBefore is honoured to the second, in both directions.
        var openingNowClaim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: Epoch,
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "opens exactly now"
        );

        ExpectAccept(
            scenario: "clock skew control: notBefore exactly equal to now is inside the window (the boundary is inclusive)",
            result: VerifyAt(
                envelope: openingNowClaim,
                store: null
            )
        );

        var oneSecondEarlyClaim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch + 1),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "one second early"
        );

        ExpectRefuse(
            scenario: "clock skew: notBefore one second in the future is refused — there is deliberately no grace window",
            result: VerifyAt(
                envelope: oneSecondEarlyClaim,
                store: null
            ),
            reasonMustContain: "not yet valid"
        );

        var closingNowClaim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: Epoch,
            audience: "world:home",
            sequence: null,
            text: "closes exactly now"
        );

        ExpectAccept(
            scenario: "clock skew control: notAfter exactly equal to now is inside the window (the boundary is inclusive)",
            result: VerifyAt(
                envelope: closingNowClaim,
                store: null
            )
        );

        var oneSecondLateClaim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch - 1),
            audience: "world:home",
            sequence: null,
            text: "one second late"
        );

        ExpectRefuse(
            scenario: "clock skew: notAfter one second in the past is refused",
            result: VerifyAt(
                envelope: oneSecondLateClaim,
                store: null
            ),
            reasonMustContain: "expired"
        );

        var invertedWindowClaim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch + 3_600),
            notAfter: (Epoch - 3_600),
            audience: "world:home",
            sequence: null,
            text: "inverted window"
        );

        ExpectRefuse(
            scenario: "malformed window: notAfter precedes notBefore",
            result: VerifyAt(
                envelope: invertedWindowClaim,
                store: null
            ),
            reasonMustContain: "malformed window"
        );

        // A directed claim WITHOUT a sequence replays freely at its own audience — that is the author's
        // trade, and it is the control that proves the next case is the sequence doing the work.
        var directedNoSequence = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "directed, no sequence"
        );
        var directedStore = new ReplayTestStore();

        ExpectAccept(
            scenario: "directed claim control: no sequence, first presentation accepted",
            result: VerifyAt(
                envelope: directedNoSequence,
                store: directedStore
            )
        );
        ExpectAccept(
            scenario: "directed claim control: no sequence, SECOND presentation also accepted — an audience alone does not stop same-world replay",
            result: VerifyAt(
                envelope: directedNoSequence,
                store: directedStore
            )
        );

        var directedWithSequence = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: 7UL,
            text: "directed, sequence 7"
        );

        ExpectAccept(
            scenario: "directed claim + sequence control: first presentation accepted and the mark advances",
            result: VerifyAt(
                envelope: directedWithSequence,
                store: directedStore
            )
        );
        ExpectRefuse(
            scenario: "directed claim + sequence: the SECOND presentation is refused — audience and sequence are independent, not exclusive",
            result: VerifyAt(
                envelope: directedWithSequence,
                store: directedStore
            ),
            reasonMustContain: "sequence replay"
        );
        var pureSequencedResult = VerifyAt(
                envelope: directedWithSequence,
                store: null
        );
        Check(
            scenario: "pure verification: a sequenced claim returns a replay commit requirement without mutating storage",
            ok: (pureSequencedResult.Verified && (pureSequencedResult.ReplayCommit is not null)),
            detail: "the receiver must atomically commit this requirement with the claim's semantic effect"
        );
        Check(
            scenario: "sequenced verification is not admission: Admits stays false until the receiver performs its transaction",
            ok: (
                !pureSequencedResult.Admits(slot: "slot:wallet") &&
                pureSequencedResult.TryGetReplayCommit(slot: "slot:wallet", requirement: out var scopedRequirement) &&
                (scopedRequirement is not null)
            ),
            detail: "the public result exposes a scoped prerequisite, never an already-admitted sequenced claim"
        );

        var commit = pureSequencedResult.ReplayCommit!;
        var horizonSeconds = 86_400L;
        var expectedEpochStart = Math.DivRem(
            a: directedWithSequence.Header.NotBefore,
            b: horizonSeconds,
            result: out _
        ) * horizonSeconds;
        Check(
            scenario: "finite replay horizon: verification derives the epoch and safe inclusive retention deadline from signed notBefore",
            ok: (
                (commit.EpochStartUnixSeconds == expectedEpochStart) &&
                (commit.RetainThroughUnixSeconds == (expectedEpochStart + (2 * horizonSeconds) - 1)) &&
                (commit.Sequence == 7UL)
            ),
            detail: $"epoch={commit.EpochStartUnixSeconds}, retainThrough={commit.RetainThroughUnixSeconds}, sequence={commit.Sequence}"
        );

        var noHorizonTrust = new TrustList(
            entries: [new TrustListEntry(
                PinnedId: keys.RootId,
                PublicKeySubjectPublicKeyInfo: keys.RootSpki,
                Mode: CarriageTrustMode.Vouches,
                Reach: DefaultReach,
                MaximumAge: null
            )],
            defaultMaximumAge: null
        );
        ExpectRefuse(
            scenario: "finite replay horizon: a verifier that authors no finite horizon refuses every sequenced claim",
            result: CarriageVerifier.VerifyChain(
                codec: codec,
                claim: directedWithSequence,
                chain: chain,
                trustList: noHorizonTrust,
                now: now,
                expectedPurpose: "test.claim",
                expectedAudience: "world:home"
            ),
            reasonMustContain: "no finite replay-acceptance horizon"
        );

        var tooLongSequenced = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + horizonSeconds + 60),
            audience: "world:home",
            sequence: 8UL,
            text: "window exceeds replay horizon"
        );
        ExpectRefuse(
            scenario: "finite replay horizon: a sequenced signed window longer than H is refused, closing the far-future lower-sequence hole",
            result: VerifyAt(envelope: tooLongSequenced, store: null),
            reasonMustContain: "exceeds the verifier's replay-acceptance horizon"
        );

        var extremeNotBefore = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: long.MinValue,
            notAfter: (Epoch + 60),
            audience: "world:home",
            sequence: 9UL,
            text: "extreme wire timestamp"
        );
        ExpectRefuse(
            scenario: "extreme notBefore refuses without Int64 subtraction or TimeSpan overflow",
            result: VerifyAt(envelope: extremeNotBefore, store: null),
            reasonMustContain: "exceeds the verifier's maximum age"
        );

        // The sequence check is one atomic store call, so the same bearer claim presented concurrently is
        // accepted exactly once: a store that let every caller read before any caller wrote would turn the race
        // from intermittent into certain, accepting both presentations.
        var bearerOnce = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 42UL,
            text: "one bearer claim, two receivers"
        );
        var contendedStore = new ReplayTestStore(participants: 4);
        var contendedResults = new CarriageVerifyResult[4];

        Parallel.For(
            fromInclusive: 0,
            toExclusive: 4,
            body: index => contendedResults[index] = contendedStore.Commit(CarriageVerifier.VerifyChain(
                codec: codec,
                claim: bearerOnce,
                chain: chain,
                trustList: trust,
                now: now,
                expectedPurpose: "test.bearer",
                expectedAudience: null
            ))
        );

        var verifiedCount = contendedResults.Count(predicate: result => result.Verified);

        Check(
            scenario: "sequence atomicity (§15's concurrency demonstration): ONE bearer claim presented by four receivers at once, all reaching the store in lockstep",
            ok: (verifiedCount == 1),
            detail: ((verifiedCount == 1)
            ? "accepted exactly once — compare-and-advance is indivisible, so the winner is arbitrary but unique"
            : $"accepted {verifiedCount} times; a bearer claim is usable once, so anything but 1 is a replay the mark was supposed to refuse")
        );

        var uncontendedStore = new ReplayTestStore(participants: 1);

        ExpectAccept(
            scenario: "sequence atomicity control: the same claim against the same store shape, uncontended, is accepted",
            result: uncontendedStore.Commit(CarriageVerifier.VerifyChain(
                codec: codec,
                claim: bearerOnce,
                chain: chain,
                trustList: trust,
                now: now,
                expectedPurpose: "test.bearer",
                expectedAudience: null
            ))
        );

    }

    /// <summary>
    /// A receiver-side atomic replay commit demonstration. A real receiver performs this compare/advance
    /// in the same durable transaction as the claim's semantic effect.
    /// The rendezvous waits until all participants have arrived, which makes the contention scenario deterministic: without it,
    /// four threads would usually serialise by luck and the test would pass on a broken store.
    /// </summary>
    private sealed class ReplayTestStore(int participants = 1) {
        private readonly Barrier? m_barrier = (participants > 1) ? new Barrier(participantCount: participants) : null;
        private readonly Dictionary<(string Domain, string Subject, long EpochStartUnixSeconds), ulong> m_marks = [];

        public CarriageVerifyResult Commit(CarriageVerifyResult result) {
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
                    return CarriageVerifyResult.Refuse(reason: $"sequence replay: claim sequence {requirement.Sequence} does not strictly exceed the recorded epoch high-water mark");
                }

                m_marks[key] = requirement.Sequence;

                return result;
            }
        }
    }

    // ---- Parser laxity ---------------------------------------------------------------------------------

    /// <summary>
    /// What the two decoders do with bytes nobody honest produced. Everything here runs against both
    /// codecs, because "the byte layout is all that must agree" is only true if each side refuses the same
    /// non-shapes.
    /// </summary>
    private static void RunParserLaxityScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Parser laxity ===");

        const long Epoch = 1_700_000_000L;

        var fixedCodec = new FixedLayoutCarriageCodec();
        var cborCodec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:hana");
        var fixedWire = fixedCodec.EncodeEnvelope(envelope: SignTestClaim(
            codec: fixedCodec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "a well-formed claim"
        ));
        var cborWire = cborCodec.EncodeEnvelope(envelope: SignTestClaim(
            codec: cborCodec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "a well-formed claim"
        ));

        foreach (var (codec, wire) in new (ICarriageCodec Codec, byte[] Wire)[] { (fixedCodec, fixedWire), (cborCodec, cborWire) }) {
            ExpectNoThrow(
                scenario: $"[{codec.Name}] parser control: an honestly encoded envelope decodes",
                action: () => _ = codec.DecodeEnvelope(wire: wire),
                detail: $"{wire.Length} byte(s) decoded"
            );

            ExpectThrows<FormatException>(
                scenario: $"[{codec.Name}] trailing garbage: one byte appended to a valid envelope",
                action: () => _ = codec.DecodeEnvelope(wire: [.. wire, 0x00]),
                messageMustContain: "trailing"
            );

            // Truncation sweep: every proper prefix of a valid envelope must refuse, and must refuse as a
            // FormatException rather than by indexing off the end. This is the "never trust a length beyond
            // the bytes that arrived" claim, checked over every possible truncation instead of asserted.
            var truncationFailures = new List<string>();

            for (var length = 0; (length < wire.Length); length += 1) {
                try {
                    _ = codec.DecodeEnvelope(wire: wire.AsSpan(
                        start: 0,
                        length: length
                    ));
                    truncationFailures.Add(item: $"length {length} decoded instead of refusing");
                } catch (FormatException) {
                    // Expected.
                } catch (Exception exception) {
                    truncationFailures.Add(item: $"length {length} threw {exception.GetType().Name}");
                }
            }

            Check(
                scenario: $"[{codec.Name}] truncation sweep: every one of {wire.Length} proper prefixes refuses as a FormatException",
                ok: (truncationFailures.Count == 0),
                detail: ((truncationFailures.Count == 0)
                ? $"all {wire.Length} truncations refused in bounds"
                : $"{truncationFailures.Count} truncation(s) misbehaved: {string.Join(
                    separator: "; ",
                    values: truncationFailures.Take(count: 5)
                )}")
            );
        }

        // Cross-codec STRUCTURAL confusion, both directions: not "the signature fails" but "these bytes are
        // not even this codec's shape". A fixed-layout envelope opens with format version 1; a CBOR one
        // opens with a 2-element array header, and neither is a legal opening for the other.
        ExpectThrows<FormatException>(
            scenario: "cross-codec structure: CBOR-signed envelope bytes fed to the fixed-layout decoder",
            action: () => _ = fixedCodec.DecodeEnvelope(wire: cborWire),
            messageMustContain: "format version"
        );
        ExpectThrows<FormatException>(
            scenario: "cross-codec structure: fixed-layout envelope bytes fed to the CBOR decoder",
            action: () => _ = cborCodec.DecodeEnvelope(wire: fixedWire),
            messageMustContain: "carriage envelope"
        );

        // CBOR non-canonical forms. Both DECODE — Strict conformance tolerates them — so the canonicality
        // rule is what refuses them, and without it one model would have many valid wire forms.
        var indefiniteWire = BuildIndefiniteLengthEnvelope(wire: cborWire);

        ExpectThrows<FormatException>(
            scenario: "[cbor-v1] non-canonical: the outer array re-encoded as indefinite-length",
            action: () => _ = cborCodec.DecodeEnvelope(wire: indefiniteWire),
            messageMustContain: "indefinite-length"
        );

        // 0x82 (array, 2 elements, minimally encoded) rewritten as 0x98 0x02 (array, count in a following
        // byte). Well-formed CBOR, accepted by Strict conformance, and a different byte string for the same
        // envelope — exactly the malleability the canonicality rule exists to close.
        byte[] nonMinimalWire = [0x98, 0x02, .. cborWire[1..]];

        ExpectThrows<FormatException>(
            scenario: "[cbor-v1] non-canonical: the outer array length written non-minimally (0x98 0x02 for 0x82) — §15's mutated-but-parseable demonstration for this codec",
            action: () => _ = cborCodec.DecodeEnvelope(wire: nonMinimalWire),
            messageMustContain: "not canonically encoded"
        );

        // A domain of the wrong width. This is well-formed, canonically encoded CBOR of the right shape —
        // only the fingerprint-width rule refuses it (README.md §15 row 3), and without
        // that rule two implementations do not agree on what a domain even is.
        ExpectThrows<FormatException>(
            scenario: "[cbor-v1] fingerprint width: a 31-byte domain field (control: the parser control above)",
            action: () => _ = cborCodec.DecodeEnvelope(wire: BuildHandWrittenEnvelope(domainWidth: 31)),
            messageMustContain: "fingerprint field is exactly 32"
        );
        ExpectNoThrow(
            scenario: "[cbor-v1] hand-built control: the same envelope with a 32-byte domain and payload kind 1 decodes",
            action: () => _ = cborCodec.DecodeEnvelope(wire: BuildHandWrittenEnvelope()),
            detail: "decoded — the field under test is the only thing the cases around it change"
        );

        // 258 truncates to 2 (key binding) in a byte-wide model, so a decoder that casts rather than
        // checks would hand the verifier a chain hop dressed as a claim.
        ExpectThrows<FormatException>(
            scenario: "[cbor-v1] payload kind out of range: a wire value of 258, which truncates to a legitimate kind",
            action: () => _ = cborCodec.DecodeEnvelope(wire: BuildHandWrittenEnvelope(payloadKind: 258UL)),
            messageMustContain: "outside the closed set"
        );

        // The same DECODER obligation on the fixed layout, which used to cast the kind byte with no range
        // check at all. It is verdict-neutral only while the verifier happens to re-refuse an out-of-set
        // kind on a claim: a fourth kind, or any consumer of the codec that does not run the verifier,
        // makes a cast-with-no-check an accepted non-kind. Deny by default belongs at the decoder.
        foreach (var kind in new byte[] { 0x00, 0x04, 0x63, 0xFF }) {
            ExpectThrows<FormatException>(
                scenario: $"[fixed-layout-v1] payload kind out of range: a wire value of {kind}, refused at the DECODER",
                action: () => _ = fixedCodec.DecodeEnvelope(wire: BuildFixedLayoutEnvelopeWithPayloadKind(
                    codec: fixedCodec,
                    keys: keys,
                    payloadKind: kind
                )),
                messageMustContain: "outside the closed set"
            );
        }

        foreach (var kind in new[] { CarriagePayloadKind.Opaque, CarriagePayloadKind.KeyBinding, CarriagePayloadKind.Sealed }) {
            ExpectNoThrow(
                scenario: $"[fixed-layout-v1] payload kind control: {(int)kind} ({kind}) is inside the closed set and decodes",
                action: () => _ = fixedCodec.DecodeEnvelope(wire: BuildFixedLayoutEnvelopeWithPayloadKind(
                    codec: fixedCodec,
                    keys: keys,
                    payloadKind: (byte)kind
                )),
                detail: "decoded — the field under test is the only thing the cases around it change"
            );
        }

        // Fixed-layout length-prefix fuzz at the boundaries that matter. The subject field's 4-byte length
        // prefix sits at a known offset: 1 version byte + 32 domain bytes + 1 presence byte = 34.
        const int SubjectLengthPrefixOffset = 34;

        var bytesAfterPrefix = (fixedWire.Length - (SubjectLengthPrefixOffset + sizeof(uint)));
        var fuzzValues = new (string Label, uint Value)[] {
            ("zero", 0u),
            ("exactly the bytes remaining", (uint)bytesAfterPrefix),
            ("one past the bytes remaining", (uint)(bytesAfterPrefix + 1)),
            ("uint.MaxValue", uint.MaxValue),
        };

        foreach (var (label, value) in fuzzValues) {
            var fuzzed = (byte[])fixedWire.Clone();

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
                destination: fuzzed.AsSpan(start: SubjectLengthPrefixOffset),
                value: value
            );

            // The property being proved is that a forged length is never an instruction to read or allocate
            // beyond what arrived. A refusal proves it directly; a decode that succeeds proves it too, so
            // long as it stayed in bounds and produced something that cannot then pass verification.
            var outcome = "?";
            var ok = false;

            try {
                var decoded = fixedCodec.DecodeEnvelope(wire: fuzzed);

                ok = !decoded.Header.Subject!.Equals(
                    value: "user:hana",
                    comparisonType: StringComparison.Ordinal
                );
                outcome = (ok
                    ? $"decoded in bounds to a DIFFERENT envelope (subject '{decoded.Header.Subject}'), which cannot carry the original signature"
                    : "decoded to the ORIGINAL envelope — the length prefix was not load-bearing");
            } catch (FormatException exception) {
                ok = true;
                outcome = $"refused: {exception.Message}";
            } catch (Exception exception) {
                outcome = $"threw {exception.GetType().Name} — a forged length escaped the bounds check: {exception.Message}";
            }

            Check(
                scenario: $"[fixed-layout-v1] length-prefix fuzz ({label}) at the subject field",
                ok: ok,
                detail: outcome
            );
        }
    }

    /// <summary>
    /// Hand-builds a canonically encoded CBOR envelope with a chosen domain width and payload kind — the
    /// two fields whose wire values a signer could never produce but a decoder must still refuse. Nothing
    /// else about it is malformed, so at (32, 1) it decodes and only the field under test can refuse it.
    /// The signature is a placeholder; this never reaches a signature check.
    /// </summary>
    private static byte[] BuildHandWrittenEnvelope(int domainWidth = 32, ulong payloadKind = (ulong)CarriagePayloadKind.Opaque) {
        var signedPortionWriter = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        signedPortionWriter.WriteStartArray(definiteLength: 11);
        signedPortionWriter.WriteUInt64(value: CborCarriageCodec.FormatVersion);
        signedPortionWriter.WriteByteString(value: new byte[domainWidth]);
        signedPortionWriter.WriteTextString(value: "user:width");
        signedPortionWriter.WriteTextString(value: CarriageAlgorithms.EcdsaP256Sha256);
        signedPortionWriter.WriteTextString(value: "test.claim");
        signedPortionWriter.WriteInt64(value: 0L);
        signedPortionWriter.WriteInt64(value: 0L);
        signedPortionWriter.WriteNull();
        signedPortionWriter.WriteNull();
        signedPortionWriter.WriteUInt64(value: payloadKind);
        signedPortionWriter.WriteByteString(value: Encoding.UTF8.GetBytes(s: "payload"));
        signedPortionWriter.WriteEndArray();

        var writer = new CborWriter(conformanceMode: CborConformanceMode.Strict);

        writer.WriteStartArray(definiteLength: 2);
        writer.WriteByteString(value: signedPortionWriter.Encode());
        writer.WriteByteString(value: new byte[64]);
        writer.WriteEndArray();

        return writer.Encode();
    }

    /// <summary>
    /// Hand-builds a fixed-layout envelope whose payload-kind byte is whatever the caller asks for,
    /// including values no signer could produce. The signature is a real one over the signed portion, so
    /// nothing but the kind byte can be what refuses it.
    /// </summary>
    private static byte[] BuildFixedLayoutEnvelopeWithPayloadKind(FixedLayoutCarriageCodec codec, DomainKeys keys, byte payloadKind) {
        const long Epoch = 1_700_000_000L;

        var header = new CarriageEnvelopeHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: "test.claim",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 3_600),
            Audience: "world:home",
            Sequence: null
        );

        // Encode under a legal kind, then overwrite the one byte — the layout's kind field is the single
        // byte immediately before the payload's 4-byte length prefix.
        var signedPortion = codec.EncodeSignedPortion(
            header: header,
            payloadKind: CarriagePayloadKind.Opaque,
            payloadBytes: "payload"u8
        );
        var payloadLength = "payload"u8.Length;

        signedPortion[(signedPortion.Length - ((payloadLength + sizeof(uint)) + 1))] = payloadKind;

        var signature = keys.SubjectSigningKey.SignData(
            data: signedPortion,
            hashAlgorithm: HashAlgorithmName.SHA256,
            signatureFormat: DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        var lengthPrefix = new byte[sizeof(uint)];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(
            destination: lengthPrefix,
            value: (uint)signature.Length
        );

        return [.. signedPortion, .. lengthPrefix, .. signature];
    }

    /// <summary>Re-frames a valid 2-element CBOR envelope as an indefinite-length array carrying the same two items.</summary>
    private static byte[] BuildIndefiniteLengthEnvelope(byte[] wire) {
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

    // ---- Signature-level attacks -----------------------------------------------------------------------

    /// <summary>
    /// What the signature field itself will and will not accept. The malleability case is a negative
    /// result recorded on purpose: it passes by accepting, which is the property ECDSA actually has, and
    /// the harness records it so nobody later builds replay defence on signature bytes.
    /// </summary>
    private static void RunSignatureLevelScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Signature-level attacks ===");

        const long Epoch = 1_700_000_000L;

        var codec = new CborCarriageCodec();
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:iris");

        var (rootToIssuing, issuingToSubject) = BuildChain(
            codec: codec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "iris's claim"
        );

        CarriageVerifyResult VerifyWithSignature(ReadOnlyMemory<byte> signature) =>
            CarriageVerifier.VerifyChain(
            codec: codec,
            claim: (claim with { Signature = signature }),
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: "signature control: the minted P1363 signature verifies",
            result: VerifyWithSignature(signature: claim.Signature)
        );

        // ECDSA malleability. (r, s) and (r, n-s) are both valid signatures over the same message, and no
        // low-s rule is imposed here: .NET's signer does not canonicalise s, so requiring it would refuse
        // the platform's own honest output roughly half the time. The consequence is recorded rather than
        // papered over — a signature is NOT a unique identifier for a claim, and neither are the envelope
        // bytes carrying it, so replay defence rests on the sequence mark and the audience, never on
        // "have I seen these bytes before".
        var malleated = MalleateSignature(signature: claim.Signature.Span);
        var malleatedResult = VerifyWithSignature(signature: malleated);
        var malleatedIsDifferent = !malleated.AsSpan().SequenceEqual(other: claim.Signature.Span);

        Check(
            scenario: "ECDSA malleability (NEGATIVE RESULT, expected to verify): (r, n-s) is a second valid signature over the same claim",
            ok: (malleatedResult.Verified && malleatedIsDifferent),
            detail: ((malleatedResult.Verified && malleatedIsDifferent)
            ? "verified, as ECDSA requires — signature bytes are therefore never a claim's identity, and nothing here may deduplicate on them"
            : $"unexpected: verified={malleatedResult.Verified}, differs={malleatedIsDifferent}, reason={malleatedResult.RefusalReason}")
        );

        // Encoding malleability, by contrast, is closed: P1363 is a fixed 64 bytes for P-256, so a DER
        // SEQUENCE of the same (r, s) is not a candidate encoding, and neither is a padded one.
        ExpectRefuse(
            scenario: "signature encoding: the same (r, s) re-encoded as DER",
            result: VerifyWithSignature(signature: EncodeSignatureAsDer(signature: claim.Signature.Span)),
            reasonMustContain: "signature does not verify"
        );
        ExpectRefuse(
            scenario: "signature encoding: a valid signature with one zero byte appended",
            result: VerifyWithSignature(signature: (byte[])[.. claim.Signature.Span, 0x00]),
            reasonMustContain: "signature does not verify"
        );
        ExpectRefuse(
            scenario: "signature encoding: a valid signature with its last byte removed",
            result: VerifyWithSignature(signature: claim.Signature[..^1]),
            reasonMustContain: "signature does not verify"
        );
        ExpectRefuse(
            scenario: "signature encoding: an all-zero signature of the right length",
            result: VerifyWithSignature(signature: new byte[claim.Signature.Length]),
            reasonMustContain: "signature does not verify"
        );
    }

    /// <summary>The P-256 group order, needed to build the (r, n-s) form of a signature.</summary>
    private static readonly BigInteger NistP256Order = BigInteger.Parse(
        value: "0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",
        style: System.Globalization.NumberStyles.HexNumber
    );

    /// <summary>Rewrites a P1363 <c>r‖s</c> signature as the equally valid <c>r‖(n-s)</c>.</summary>
    private static byte[] MalleateSignature(ReadOnlySpan<byte> signature) {
        var half = (signature.Length / 2);
        var s = new BigInteger(
            value: signature[half..],
            isUnsigned: true,
            isBigEndian: true
        );
        var flipped = (NistP256Order - s);
        var result = signature.ToArray();
        var flippedBytes = flipped.ToByteArray(
            isUnsigned: true,
            isBigEndian: true
        );

        var destinationStart = (signature.Length - flippedBytes.Length);

        flippedBytes.AsSpan().CopyTo(destination: result.AsSpan(start: destinationStart));

        for (var index = half; (index < destinationStart); index += 1) {
            result[index] = 0x00;
        }

        return result;
    }

    /// <summary>Re-encodes a P1363 <c>r‖s</c> signature as the DER <c>SEQUENCE { INTEGER r, INTEGER s }</c> form.</summary>
    private static byte[] EncodeSignatureAsDer(ReadOnlySpan<byte> signature) {
        var half = (signature.Length / 2);
        var writer = new System.Formats.Asn1.AsnWriter(ruleSet: System.Formats.Asn1.AsnEncodingRules.DER);

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

    // ---- Serialisation cross-check --------------------------------------------------------------------

    private static void RunSerialisationCrossCheck() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Serialisation cross-check ===");

        const long Epoch = 1_700_000_000L;

        var fixedCodec = new FixedLayoutCarriageCodec();
        var cborCodec = new CborCarriageCodec();
        var keys = MintDomainKeys(subject: "user:carol");

        var (fixedRootToIssuing, fixedIssuingToSubject) = BuildChain(
            codec: fixedCodec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var (cborRootToIssuing, cborIssuingToSubject) = BuildChain(
            codec: cborCodec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var fixedChain = new[] { fixedRootToIssuing, fixedIssuingToSubject };
        var cborChain = new[] { cborRootToIssuing, cborIssuingToSubject };
        var trust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);

        var claim = SignTestClaim(
            codec: fixedCodec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "signed once, under the fixed layout"
        );

        var controlResult = CarriageVerifier.VerifyChain(
            codec: fixedCodec,
            claim: claim,
            chain: fixedChain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: "serialisation cross-check control: fixed-layout-signed envelope verifies under the fixed codec",
            result: controlResult
        );

        // Round-trip the SAME in-memory model (same header values, same payload bytes, the ORIGINAL
        // fixed-layout-derived signature bytes copied verbatim — never re-signed) through the CBOR codec's
        // own encode/decode, then attempt to verify it against a chain that IS genuinely CBOR-signed. The
        // structure parses fine either way; only the claim's own signature check can fail, because the
        // bytes CBOR reproduces from the decoded header+payload are not the bytes that were actually
        // signed. This is the deliberate consequence of "the signing input is the canonical bytes" — the
        // two serialisations are not interchangeable at the byte level even though the field list matches.
        var reencodedAsCborWire = cborCodec.EncodeEnvelope(envelope: claim);
        var decodedFromCbor = cborCodec.DecodeEnvelope(wire: reencodedAsCborWire);
        var crossCheckResult = CarriageVerifier.VerifyChain(
            codec: cborCodec,
            claim: decodedFromCbor,
            chain: cborChain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "serialisation cross-check: a fixed-layout-signed envelope re-encoded as CBOR does NOT verify",
            result: crossCheckResult,
            reasonMustContain: "signature"
        );
    }

    // ---- Named conformance profiles ---------------------------------------------------------------------

    private static void RunConformanceProfileScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Named conformance profiles and resource ceilings ===");

        const long Epoch = 1_700_000_000L;

        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:profile");
        var cbor = new CborCarriageCodec();
        var fixedLayout = new FixedLayoutCarriageCodec();
        var trustSha256 = BuildDirectTrustList(keys: keys, reach: DefaultReach);
        var claimCbor = SignTestClaim(
            codec: cbor,
            keys: keys,
            purpose: "profile.test",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            text: "base profile"
        );
        var claimFixed = SignTestClaim(
            codec: fixedLayout,
            keys: keys,
            purpose: "profile.test",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            text: "fixed-layout extension"
        );

        ExpectAccept(
            scenario: "[profile] carriage-v1-base requires and accepts CBOR + ECDSA-P256-SHA256",
            result: CarriageConformanceProfile.Base.VerifyChain(
                codec: cbor,
                claim: claimCbor,
                chain: null,
                trustList: trustSha256,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            )
        );

        using var unrelatedKey = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var unrelatedSpki = unrelatedKey.ExportSubjectPublicKeyInfo();
        var unrelatedSha384Id = KeyId.ForSubject(
            domain: keys.Domain,
            subject: "user:unrelated-sha384",
            subjectPublicKeyInfo: unrelatedSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha384
        );
        var mixedTrust = new TrustList(
            entries: [
                trustSha256.Entries[0],
                new TrustListEntry(
                    PinnedId: unrelatedSha384Id,
                    PublicKeySubjectPublicKeyInfo: unrelatedSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: DefaultReach,
                    MaximumAge: null
                ),
            ],
            defaultMaximumAge: null
        );
        ExpectAccept(
            scenario: "[profile] a disabled algorithm on an unrelated trust entry cannot poison a selected SHA-256 admission",
            result: CarriageConformanceProfile.Base.VerifyChain(
                codec: cbor,
                claim: claimCbor,
                chain: null,
                trustList: mixedTrust,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            )
        );

        Check(
            scenario: "[profile] the resource/profile facade is the only public verification boundary",
            ok: !typeof(CarriageVerifier).IsPublic,
            detail: "the core verifier is assembly-only, so callers cannot bypass a receiver-selected profile"
        );

        ExpectRefuse(
            scenario: "[profile] carriage-v1-base does not silently accept the optional fixed layout",
            result: CarriageConformanceProfile.Base.VerifyChain(
                codec: fixedLayout,
                claim: claimFixed,
                chain: null,
                trustList: trustSha256,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            ),
            reasonMustContain: "does not enable"
        );

        var fixedProfile = CarriageConformanceProfile.Base.WithExtensions(
            extensions: CarriageConformanceExtensions.FixedLayoutV1
        );

        Check(
            scenario: "[profile] composed profile names use the normative extension spelling",
            ok: string.Equals(
                a: fixedProfile.Name,
                b: "carriage-v1-base+fixed-layout-v1",
                comparisonType: StringComparison.Ordinal
            ),
            detail: $"profile name is '{fixedProfile.Name}'"
        );

        ExpectAccept(
            scenario: "[profile] the named fixed-layout-v1 extension enables fixed layout without removing mandatory CBOR",
            result: fixedProfile.VerifyChain(
                codec: fixedLayout,
                claim: claimFixed,
                chain: null,
                trustList: trustSha256,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            )
        );

        var subjectSha384Id = KeyId.ForSubject(
            domain: keys.Domain,
            subject: keys.Subject,
            subjectPublicKeyInfo: keys.SubjectSigningSpki,
            algorithm: CarriageAlgorithms.EcdsaP256Sha384
        );
        var trustSha384 = new TrustList(
            entries: [
                new TrustListEntry(
                    PinnedId: subjectSha384Id,
                    PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                    Mode: CarriageTrustMode.SignsDirectly,
                    Reach: DefaultReach,
                    MaximumAge: null
                ),
            ],
            defaultMaximumAge: null
        );
        var claimSha384 = CarriageSigner.SignClaim(
            codec: cbor,
            domain: keys.Domain,
            subject: keys.Subject,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha384,
            purpose: "profile.test",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            claimBytes: "sha384 extension"u8.ToArray()
        );

        ExpectRefuse(
            scenario: "[profile] SHA-384 is not part of the mandatory base algorithm set",
            result: CarriageConformanceProfile.Base.VerifyChain(
                codec: cbor,
                claim: claimSha384,
                chain: null,
                trustList: trustSha384,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            ),
            reasonMustContain: "does not enable"
        );

        var sha384Profile = CarriageConformanceProfile.Base.WithExtensions(
            extensions: CarriageConformanceExtensions.EcdsaP256Sha384
        );

        ExpectAccept(
            scenario: "[profile] the named SHA-384 extension accepts an honestly pinned SHA-384 claim",
            result: sha384Profile.VerifyChain(
                codec: cbor,
                claim: claimSha384,
                chain: null,
                trustList: trustSha384,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            )
        );

        var confusedClaim = SignTestClaim(
            codec: cbor,
            keys: keys,
            purpose: "profile.test",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            text: "message-selected downgrade",
            declaredAlgorithm: CarriageAlgorithms.EcdsaP256Sha384
        );

        ExpectRefuse(
            scenario: "[profile] enabling SHA-384 does not let a message select it over its pinned SHA-256 key",
            result: sha384Profile.VerifyChain(
                codec: cbor,
                claim: confusedClaim,
                chain: null,
                trustList: trustSha256,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            ),
            reasonMustContain: "algorithm confusion"
        );

        ExpectThrows<FormatException>(
            scenario: "[profile] the envelope ceiling is enforced before CBOR parsing",
            action: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(
                codec: cbor,
                wire: new byte[CarriageResourceLimits.EnvelopeBytes + 1]
            ),
            messageMustContain: "permits at most"
        );

        var oversizedPayloadClaim = CarriageSigner.SignClaim(
            codec: cbor,
            domain: keys.Domain,
            subject: keys.Subject,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: "profile.test",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            claimBytes: new byte[CarriageResourceLimits.PayloadBytes + 1]
        );

        ExpectThrows<FormatException>(
            scenario: "[profile] a payload above the explicit ceiling is refused deterministically",
            action: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(
                codec: cbor,
                wire: cbor.EncodeEnvelope(envelope: oversizedPayloadClaim)
            ),
            messageMustContain: "payload"
        );

        var oversizedSignature = new byte[CarriageResourceLimits.SignatureBytes + 1];

        ExpectRefuse(
            scenario: "[profile] a P-256 signature outside the exact 64-byte ceiling is refused before signature verification",
            result: CarriageConformanceProfile.Base.VerifyChain(
                codec: cbor,
                claim: (claimCbor with { Signature = oversizedSignature }),
                chain: null,
                trustList: trustSha256,
                now: now,
                expectedPurpose: "profile.test",
                expectedAudience: "world:profile"
            ),
            reasonMustContain: "exactly 64"
        );

        var oversizedPurposeClaim = CarriageSigner.SignClaim(
            codec: cbor,
            domain: keys.Domain,
            subject: keys.Subject,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: new string(c: 'p', count: (CarriageResourceLimits.TextStringUtf8Bytes + 1)),
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:profile",
            sequence: null,
            claimBytes: Array.Empty<byte>()
        );

        ExpectThrows<FormatException>(
            scenario: "[profile] text ceilings are measured in UTF-8 bytes, not characters or allocator capacity",
            action: () => _ = CarriageConformanceProfile.Base.DecodeEnvelope(
                codec: cbor,
                wire: cbor.EncodeEnvelope(envelope: oversizedPurposeClaim)
            ),
            messageMustContain: "UTF-8 bytes"
        );

        Check(
            scenario: "[profile] interchange tooling is an explicit named extension",
            ok: (
                !CarriageConformanceProfile.Base.SupportsInterchangeTooling &&
                CarriageConformanceProfile.Base.WithExtensions(CarriageConformanceExtensions.InterchangeV1).SupportsInterchangeTooling
            ),
            detail: "the base verifier does not claim optional fixture tooling; the extension does"
        );
    }

    // ---- Sealed carriage -------------------------------------------------------------------------------

    private static void RunSealedCarriageScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Sealed carriage ===");

        const long Epoch = 1_700_000_000L;

        var codec = new FixedLayoutCarriageCodec();
        var keys = MintDomainKeys(subject: "user:dana");
        var header = new CarriageEnvelopeHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: CarriageAlgorithms.EcdhP256HkdfSha256Aes256Gcm,
            Purpose: "test.sealed-claim",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 3_600),
            Audience: "world:vault",
            Sequence: null
        );
        var headerBytes = codec.EncodeHeader(header: header);
        var plaintext = Encoding.UTF8.GetBytes(s: "a secret only dana's sealing key can open");

        var sealedPayload = SealedCarriage.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: headerBytes,
            plaintext: plaintext
        );
        var recovered = SealedCarriage.Unseal(
            recipientPrivateKey: keys.SubjectSealingKey,
            payload: sealedPayload,
            associatedData: headerBytes
        );

        Check(
            scenario: "sealed round trip: plaintext recovered exactly",
            ok: plaintext.AsSpan().SequenceEqual(other: recovered),
            detail: $"{recovered.Length} byte(s) recovered, matches input"
        );

        // The AAD must cover the WHOLE canonical header, not a prefix of it, so both ends are tampered:
        // byte 0 is the format version, and the last byte is inside the final header field.
        foreach (var (label, offset) in new (string Label, int Offset)[] { ("first", 0), ("last", (headerBytes.Length - 1)) }) {
            var tamperedHeaderBytes = (byte[])headerBytes.Clone();

            tamperedHeaderBytes[offset] ^= 0xFF;

            ExpectThrows<CryptographicException>(
                scenario: $"sealed AAD tamper: flipping the {label} header byte breaks decryption",
                action: () => _ = SealedCarriage.Unseal(
                    recipientPrivateKey: keys.SubjectSealingKey,
                    payload: sealedPayload,
                    associatedData: tamperedHeaderBytes
                ),
                messageMustContain: "tag"
            );
        }

        ExpectThrows<CryptographicException>(
            scenario: "sealed ciphertext tamper: flipping one ciphertext byte breaks decryption",
            action: () => {
                var tamperedCiphertext = sealedPayload.Ciphertext.ToArray();

                tamperedCiphertext[0] ^= 0xFF;

                _ = SealedCarriage.Unseal(
                    recipientPrivateKey: keys.SubjectSealingKey,
                    payload: (sealedPayload with { Ciphertext = tamperedCiphertext }),
                    associatedData: headerBytes
                );
            },
            messageMustContain: "tag"
        );

        // Wrong recipient: sealed carriage is ephemeral-static, so the recipient's private key is the only
        // thing that reproduces the agreement. A different key derives a different AEAD key and the tag
        // check fails — a clean refusal, not a garbled plaintext.
        var otherKeys = MintDomainKeys(subject: "user:eve");

        ExpectThrows<FormatException>(
            scenario: "sealed wrong recipient: another identity's sealing key is refused against the signed recipient id before decryption",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: otherKeys.SubjectSealingKey,
                payload: sealedPayload,
                associatedData: headerBytes
            ),
            messageMustContain: "does not identify"
        );

        ExpectThrows<FormatException>(
            scenario: "sealed role separation: a signing-algorithm id cannot be presented as a recipient sealing key",
            action: () => _ = SealedCarriage.Seal(
                recipientId: keys.SubjectSigningId,
                recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
                associatedData: headerBytes,
                plaintext: plaintext
            ),
            messageMustContain: "rather than a sealing algorithm"
        );

        ExpectThrows<CryptographicException>(
            scenario: "sealed recipient-context tamper: changing the recipient subject while retaining the same key breaks the AEAD tag",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: (sealedPayload with {
                    RecipientId = sealedPayload.RecipientId with { Subject = "user:someone-else" },
                }),
                associatedData: headerBytes
            ),
            messageMustContain: "tag"
        );

        // The ephemeral key travels on the wire, so it is attacker-chosen: a key on another curve must be
        // refused before it is ever handed to the agreement, or the recipient's static key becomes an
        // oracle for its own private scalar.
        using var wrongCurveKey = ECDiffieHellman.Create(curve: ECCurve.NamedCurves.nistP384);

        ExpectThrows<FormatException>(
            scenario: "sealed invalid curve: a P-384 ephemeral key offered against a P-256 recipient",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = wrongCurveKey.ExportSubjectPublicKeyInfo() }),
                associatedData: headerBytes
            ),
            messageMustContain: "not on P-256"
        );

        // Curve is not the whole of it: the ephemeral field carries an SPKI, and an SPKI names a KEY TYPE
        // before it names a curve. An RSA SPKI has no curve to check at all, so an implementation that
        // reached for the curve first would be asking a question the bytes cannot answer. The type check is
        // the AlgorithmIdentifier OID, and it has to come first (§14).
        using var rsaKey = RSA.Create(keySizeInBits: 2048);

        ExpectThrows<FormatException>(
            scenario: "sealed wrong key type: an RSA SPKI offered as the ephemeral key — refused on its algorithm OID, before any curve question",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = rsaKey.ExportSubjectPublicKeyInfo() }),
                associatedData: headerBytes
            ),
            messageMustContain: "does not import as an EC public key"
        );

        // The other half of the same rule, and the reason §14 states it as an OID check rather than an
        // intent check: an EC SPKI carries id-ecPublicKey whether its holder means to sign or to agree, so a
        // P-256 SIGNING key's SPKI is byte-identical in shape to a sealing key's and MUST import cleanly.
        // There is no "this one was meant for ECDSA" bit, and an implementation that invented one would
        // refuse honest ephemeral keys on a platform whose APIs happen to be split.
        ExpectThrows<CryptographicException>(
            scenario: "sealed key type control: a P-256 SPKI exported from an ECDSA key imports as an agreement key and fails only at the TAG — an EC SPKI carries no signing-versus-agreement intent",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: (sealedPayload with { EphemeralPublicKeySubjectPublicKeyInfo = keys.SubjectSigningSpki }),
                associatedData: headerBytes
            ),
            messageMustContain: "tag"
        );

        ExpectThrows<FormatException>(
            scenario: "sealed malformed nonce: an 11-byte nonce is refused as a format error, not as a crypto argument error",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: (sealedPayload with { Nonce = sealedPayload.Nonce[..^1] }),
                associatedData: headerBytes
            ),
            messageMustContain: "nonce must be"
        );

        foreach (var payloadCodec in new ICarriageCodec[] { new FixedLayoutCarriageCodec(), new CborCarriageCodec() }) {
            ExpectThrows<FormatException>(
                scenario: $"sealed encode/decode symmetry [{payloadCodec.Name}]: an invalid recipient fingerprint is refused by the encoder",
                action: () => _ = payloadCodec.EncodeSealedPayload(payload: sealedPayload with {
                    RecipientId = sealedPayload.RecipientId with { KeyHash = "not-a-fingerprint" },
                }),
                messageMustContain: "fingerprint"
            );
        }

        // The nonce is random per seal AND the AEAD key is fresh per seal (ephemeral agreement), so two
        // seals of the same plaintext under the same header share neither.
        var secondSeal = SealedCarriage.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: headerBytes,
            plaintext: plaintext
        );
        var nonceDiffers = !secondSeal.Nonce.Span.SequenceEqual(other: sealedPayload.Nonce.Span);
        var ephemeralDiffers = !secondSeal.EphemeralPublicKeySubjectPublicKeyInfo.Span.SequenceEqual(other: sealedPayload.EphemeralPublicKeySubjectPublicKeyInfo.Span);

        Check(
            scenario: "sealed nonce uniqueness: two seals of the same plaintext share neither nonce nor ephemeral key",
            ok: (nonceDiffers && ephemeralDiffers),
            detail: ((nonceDiffers && ephemeralDiffers)
            ? "both differ — a (key, nonce) repeat needs an ephemeral keypair collision AND a nonce collision"
            : $"nonce differs: {nonceDiffers}, ephemeral key differs: {ephemeralDiffers}")
        );

        // The whole sealed envelope, under both codecs: a sealed payload riding inside an ordinary signed
        // envelope, encoded, decoded, chain-verified, and opened — the shape the interchange fixture exports,
        // exercising §14's key derivation (salt, info label, output length, tag length, raw-versus-hashed
        // agreement) end to end rather than only through a tag-mismatch check indistinguishable from tampering.
        foreach (var envelopeCodec in new ICarriageCodec[] { new FixedLayoutCarriageCodec(), new CborCarriageCodec() }) {
            RunSealedEnvelopeRoundTrip(codec: envelopeCodec);
        }
    }

    /// <summary>Mints a sealed claim, sends it through the codec, verifies its chain, and opens it — the sealed path end to end for one serialisation.</summary>
    private static void RunSealedEnvelopeRoundTrip(ICarriageCodec codec) {
        const long Epoch = 1_700_000_000L;

        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:mira");

        var (rootToIssuing, issuingToSubject) = BuildChain(
            codec: codec,
            keys: keys,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var chain = new[] { rootToIssuing, issuingToSubject };
        var trust = BuildTrustList(
            keys: keys,
            defaultMaximumAge: TimeSpan.FromHours(hours: 24)
        );
        var plaintext = "a sealed claim riding inside a signed envelope"u8.ToArray();

        // The header is built first because its own encoding is the AEAD associated data, so the ciphertext
        // cannot exist before the context it is bound to. The same header is then signed by the SUBJECT
        // key, which is what names the sender — sealing alone names nobody.
        var header = new CarriageEnvelopeHeader(
            Domain: keys.Domain,
            Subject: keys.Subject,
            Algorithm: CarriageAlgorithms.EcdsaP256Sha256,
            Purpose: "test.sealed",
            NotBefore: (Epoch - 60),
            NotAfter: (Epoch + 3_600),
            Audience: "world:vault",
            Sequence: null
        );
        var payload = SealedCarriage.Seal(
            recipientId: keys.SubjectSealingId,
            recipientPublicKeySubjectPublicKeyInfo: keys.SubjectSealingSpki,
            associatedData: codec.EncodeHeader(header: header),
            plaintext: plaintext
        );
        var envelope = CarriageSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: CarriagePayloadKind.Sealed,
            payloadBytes: codec.EncodeSealedPayload(payload: payload),
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: CarriageAlgorithms.EcdsaP256Sha256
        );

        var decoded = codec.DecodeEnvelope(wire: codec.EncodeEnvelope(envelope: envelope));
        var result = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: decoded,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.sealed",
            expectedAudience: "world:vault"
        );

        ExpectAccept(
            scenario: $"[{codec.Name}] sealed envelope: a payload-kind-3 claim survives encode/decode and its chain verifies",
            result: result
        );

        // Structural validation belongs to verification, but only AFTER signature authentication. Sign a
        // malformed nested payload to prove the verifier examines it; then corrupt that signature to prove
        // the same bytes stop at authentication and are never interpreted first.
        var malformed = CarriageSigner.Sign(
            codec: codec,
            header: header,
            payloadKind: CarriagePayloadKind.Sealed,
            payloadBytes: new byte[] { 0x00 },
            signingKey: keys.SubjectSigningKey,
            signingAlgorithm: CarriageAlgorithms.EcdsaP256Sha256
        );
        var malformedResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: malformed,
            chain: chain,
            trustList: trust,
            now: now,
            expectedPurpose: "test.sealed",
            expectedAudience: "world:vault"
        );

        ExpectRefuse(
            scenario: $"[{codec.Name}] sealed envelope validation: an authenticated but malformed kind-3 payload is refused during claim verification",
            result: malformedResult,
            reasonMustContain: "sealed claim payload is malformed"
        );

        var badSignature = malformed.Signature.ToArray();

        badSignature[^1] ^= 0xFF;

        ExpectRefuse(
            scenario: $"[{codec.Name}] sealed envelope validation order: malformed nested bytes under a bad signature stop at authentication",
            result: CarriageVerifier.VerifyChain(
                codec: codec,
                claim: (malformed with { Signature = badSignature }),
                chain: chain,
                trustList: trust,
                now: now,
                expectedPurpose: "test.sealed",
                expectedAudience: "world:vault"
            ),
            reasonMustContain: "claim signature"
        );

        var recovered = SealedCarriage.Unseal(
            recipientPrivateKey: keys.SubjectSealingKey,
            payload: codec.DecodeSealedPayload(bytes: decoded.PayloadBytes.Span),
            associatedData: codec.EncodeHeader(header: decoded.Header)
        );

        Check(
            scenario: $"[{codec.Name}] sealed envelope: the decoded payload opens to the original plaintext",
            ok: plaintext.AsSpan().SequenceEqual(other: recovered),
            detail: $"{recovered.Length} byte(s) recovered through encode, decode, chain-verify, and unseal"
        );

        ExpectThrows<CryptographicException>(
            scenario: $"[{codec.Name}] sealed envelope AAD control: the same ciphertext under a one-field-different header is refused",
            action: () => _ = SealedCarriage.Unseal(
                recipientPrivateKey: keys.SubjectSealingKey,
                payload: codec.DecodeSealedPayload(bytes: decoded.PayloadBytes.Span),
                associatedData: codec.EncodeHeader(header: (decoded.Header with { Audience = "world:elsewhere" }))
            ),
            messageMustContain: "tag"
        );
    }

    // ---- Interchange fixture ----------------------------------------------------------------------------

    /// <summary>
    /// Exports the cross-implementation fixture and verifies it back, which is the only thing that proves
    /// the fixture the other implementation is handed actually round-trips. It had never been exercised by
    /// the harness at all: <c>export</c> and <c>verify</c> were reachable only as command-line modes, so a
    /// fixture that could not be verified would have been discovered by the other team.
    /// </summary>
    /// <remarks>
    /// The broken-fixture cases below are the §17 tool protocol's obligation, and they check the exit code
    /// rather than the console text: 0 means every check passed and non-zero means at least one did not, and
    /// a crash is not a permitted third answer. A corrupt <c>claim.envelope</c> used to escape
    /// <see cref="CarriageInterchange.Verify"/> as an unhandled <see cref="FormatException"/> and take the
    /// process with it, which reports "your bytes are bad" in a form indistinguishable from "my tool fell
    /// over" — and the harness never noticed, because it only ever fed the verb a fixture it had just
    /// minted itself.
    /// </remarks>
    private static void RunInterchangeRoundTrip() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Interchange fixture round trip ===");

        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-carriage-interchange-{Guid.NewGuid():N}"
        );

        try {
            var exported = CarriageInterchange.Export(directory: directory);

            Check(
                scenario: "interchange: export writes a fixture",
                ok: (exported == 0),
                detail: $"exit {exported}, {Directory.GetFiles(path: directory).Length} file(s) written"
            );

            var verified = CarriageInterchange.Verify(directory: directory);

            Check(
                scenario: "interchange: the exported fixture verifies — signed chain AND the sealed artifact §14 fixes",
                ok: (verified == 0),
                detail: ((verified == 0)
                ? "exit 0 — every check in the fixture held"
                : $"exit {verified} — the fixture handed to the other implementation does not verify against its own minting side")
            );

            Console.WriteLine(value: "  (every block below re-runs verify against a DELIBERATELY broken fixture — the [FAIL] lines inside them are the expected result)");

            // The sealed control: break the sealed artifact and require the fixture to fail. Without this, a
            // Verify that skipped the sealed file entirely would pass the case above.
            ExpectFixtureFailure(
                scenario: "interchange control: one flipped byte in the SEALED envelope fails the fixture",
                directory: directory,
                break_: fixture => FlipLastByte(path: Path.Combine(
                    path1: fixture,
                    path2: "sealed.envelope"
                ))
            );

            // The Part 1 defect, as a case. Byte 0 of a CBOR envelope is the outer array head, so flipping
            // it makes the file undecodable rather than merely unverifiable — the malformed-input path,
            // which is exactly the one that crashed.
            ExpectFixtureFailure(
                scenario: "interchange malformed input: a corrupt claim.envelope is a FAILED CHECK, never a crash",
                directory: directory,
                break_: fixture => FlipByte(
                    path: Path.Combine(
                        path1: fixture,
                        path2: "claim.envelope"
                    ),
                    offset: 0
                )
            );

            // The same obligation over every file in the fixture, so no one file is guarded by accident.
            foreach (var fileName in new[] { "binding-1.envelope", "binding-2.envelope", "manifest.txt", "recipient-sealing.pkcs8", "root.spki", "sealed.envelope" }) {
                ExpectFixtureFailure(
                    scenario: $"interchange malformed input: a corrupt {fileName} is a FAILED CHECK, never a crash",
                    directory: directory,
                    break_: fixture => FlipByte(
                        path: Path.Combine(
                            path1: fixture,
                            path2: fileName
                        ),
                        offset: 0
                    )
                );
            }

            // Absent, not merely corrupt. A missing file reaches different code than a damaged one.
            foreach (var fileName in new[] { "claim.envelope", "manifest.txt", "root.spki", "sealed.envelope" }) {
                ExpectFixtureFailure(
                    scenario: $"interchange missing input: an absent {fileName} is a FAILED CHECK, never a crash",
                    directory: directory,
                    break_: fixture => File.Delete(path: Path.Combine(
                        path1: fixture,
                        path2: fileName
                    ))
                );
            }

            // The manifest's own format rules (§17). Each of these is a manifest that parses under a lax
            // reader and means something different — or nothing — under this one.
            foreach (var (label, mutate) in new (string Label, Func<string, string> Mutate)[] {
                ("a key with no '='", manifest => $"{manifest}this line has no separator\n"),
                ("a duplicated key", manifest => $"{manifest}purpose=carriage.something-else\n"),
                ("an escape the format does not define", manifest => manifest.Replace(
                oldValue: "sealed-plaintext=sealed",
                newValue: @"sealed-plaintext=\qsealed",
                comparisonType: StringComparison.Ordinal
            )),
                ("a required key removed", manifest => manifest.Replace(
                oldValue: $"audience={CarriageInterchange.InterchangeAudience}\n",
                newValue: string.Empty,
                comparisonType: StringComparison.Ordinal
            )),
                ("a required key emptied", manifest => manifest.Replace(
                oldValue: $"subject={CarriageInterchange.InterchangeSubject}",
                newValue: "subject=",
                comparisonType: StringComparison.Ordinal
            )),
                ("an algorithm outside the §4 registry", manifest => manifest.Replace(
                oldValue: $"algorithm={CarriageAlgorithms.EcdsaP256Sha256}",
                newValue: "algorithm=ecdsa-p521-sha512",
                comparisonType: StringComparison.Ordinal
            )),
                ("a non-positive replay horizon", manifest => manifest.Replace(
                oldValue: $"replay-horizon-seconds={CarriageInterchange.InterchangeReplayHorizonSeconds}",
                newValue: "replay-horizon-seconds=0",
                comparisonType: StringComparison.Ordinal
            )),
                ("a sequence the claim does not carry", manifest => manifest.Replace(
                oldValue: "sequence=1",
                newValue: "sequence=2",
                comparisonType: StringComparison.Ordinal
            )),
            }) {
                ExpectFixtureFailure(
                    scenario: $"interchange manifest format: {label} fails the fixture",
                    directory: directory,
                    break_: fixture => {
                        var path = Path.Combine(
                            path1: fixture,
                            path2: "manifest.txt"
                        );

                        File.WriteAllText(
                            path: path,
                            contents: mutate(File.ReadAllText(path: path))
                        );
                    }
                );
            }

            // The controls the cases above need: a manifest change that the format explicitly TOLERATES must
            // leave the fixture verifying, or every case above would pass on a verb that simply refuses
            // everything.
            foreach (var (label, mutate) in new (string Label, Func<string, string> Mutate)[] {
                ("an unknown key", manifest => $"{manifest}some-future-key=a value the reader has never heard of\n"),
                ("an empty line", manifest => $"{manifest}\n\n"),
                ("CRLF line endings", manifest => manifest.Replace(
                oldValue: "\n",
                newValue: "\r\n",
                comparisonType: StringComparison.Ordinal
            )),
                ("a value carrying '='", manifest => $"{manifest}some-future-key=a=b=c\n"),
                ("no final newline", manifest => manifest.TrimEnd(trimChar: '\n')),
            }) {
                ExpectFixtureSuccess(
                    scenario: $"interchange manifest format control: {label} is tolerated and the fixture still verifies",
                    directory: directory,
                    mutate: fixture => {
                        var path = Path.Combine(
                            path1: fixture,
                            path2: "manifest.txt"
                        );

                        File.WriteAllText(
                            path: path,
                            contents: mutate(File.ReadAllText(path: path))
                        );
                    }
                );
            }
        } finally {
            if (Directory.Exists(path: directory)) {
                Directory.Delete(
                    path: directory,
                    recursive: true
                );
            }
        }
    }

    /// <summary>Copies a fixture, breaks it, and requires <see cref="CarriageInterchange.Verify"/> to report a non-zero exit without throwing.</summary>
    /// <param name="scenario">The case's name.</param>
    /// <param name="directory">The intact fixture to copy.</param>
    /// <param name="break_">What to do to the copy.</param>
    private static void ExpectFixtureFailure(string scenario, string directory, Action<string> break_) =>
        RunOnFixtureCopy(
        scenario: scenario,
        directory: directory,
        mutate: break_,
        wantExitZero: false,
        wanted: "a non-zero exit, reported rather than thrown",
        unwanted: "exit 0 — the broken fixture was accepted"
    );

    /// <summary>Copies a fixture, changes something the format tolerates, and requires it to still verify — the control every case above needs.</summary>
    /// <param name="scenario">The case's name.</param>
    /// <param name="directory">The intact fixture to copy.</param>
    /// <param name="mutate">What to do to the copy.</param>
    private static void ExpectFixtureSuccess(string scenario, string directory, Action<string> mutate) =>
        RunOnFixtureCopy(
        scenario: scenario,
        directory: directory,
        mutate: mutate,
        wantExitZero: true,
        wanted: "exit 0 — the change is genuinely tolerated",
        unwanted: "a non-zero exit — the reader refused something the format admits"
    );
    private static void RunOnFixtureCopy(string scenario, string directory, Action<string> mutate, bool wantExitZero, string wanted, string unwanted) {
        var copy = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-carriage-interchange-{Guid.NewGuid():N}"
        );

        try {
            Directory.CreateDirectory(path: copy);

            foreach (var file in Directory.GetFiles(path: directory)) {
                File.Copy(
                    sourceFileName: file,
                    destFileName: Path.Combine(
                        path1: copy,
                        path2: Path.GetFileName(path: file)
                    )
                );
            }

            mutate(copy);

            // The exit code IS the assertion: a thrown exception would escape this call and take the harness
            // with it, which is the outcome §17's tool protocol names as not permitted.
            var exit = CarriageInterchange.Verify(directory: copy);
            var ok = ((exit == 0) == wantExitZero);

            Check(
                scenario: scenario,
                ok: ok,
                detail: (ok
                ? $"exit {exit} — {wanted}"
                : $"exit {exit} — {unwanted}")
            );
        } catch (Exception exception) {
            Check(
                scenario: scenario,
                ok: false,
                detail: $"verify THREW {exception.GetType().Name} instead of returning a verdict: {exception.Message}"
            );
        } finally {
            if (Directory.Exists(path: copy)) {
                Directory.Delete(
                    path: copy,
                    recursive: true
                );
            }
        }
    }
    private static void FlipByte(string path, int offset) {
        var bytes = File.ReadAllBytes(path: path);

        bytes[offset] ^= 0xFF;

        File.WriteAllBytes(
            path: path,
            bytes: bytes
        );
    }
    private static void FlipLastByte(string path) {
        var bytes = File.ReadAllBytes(path: path);

        bytes[^1] ^= 0xFF;

        File.WriteAllBytes(
            path: path,
            bytes: bytes
        );
    }

    // ---- Bytes that arrived, versus bytes re-derived -----------------------------------------------------

    /// <summary>
    /// The class of defect that comes from verifying a re-encoding of a decoded model rather than the bytes
    /// that actually arrived. Every case here was an acceptance before
    /// <see cref="SignedCarriageEnvelope.SignedPortion"/> existed: the fixed layout read a presence flag as
    /// "non-zero means present", so flipping the subject flag at offset 33 from <c>0x01</c> to <c>0x02</c>
    /// produced different bytes that decoded to the identical model — and the verifier then re-encoded that
    /// model, normalising the forgery away before the signature ever saw it. One claim, 255 accepted wire
    /// forms per optional field.
    /// </summary>
    private static void RunArrivedBytesScenarios() {
        Console.WriteLine();
        Console.WriteLine(value: "=== The bytes that arrived ===");

        const long Epoch = 1_700_000_000L;

        // 1 version byte + 32 domain bytes lands exactly on the subject field's presence flag.
        const int SubjectPresenceFlagOffset = 33;

        var codec = new FixedLayoutCarriageCodec();
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: Epoch);
        var keys = MintDomainKeys(subject: "user:jun");
        var trust = BuildDirectTrustList(
            keys: keys,
            reach: DefaultReach
        );
        var claim = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: "jun's claim"
        );
        var wire = codec.EncodeEnvelope(envelope: claim);

        CarriageVerifyResult VerifyWire(byte[] bytes) =>
            CarriageVerifier.VerifyChain(
            codec: codec,
            claim: codec.DecodeEnvelope(wire: bytes),
            chain: null,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectAccept(
            scenario: "[fixed-layout-v1] arrived-bytes control: the honestly encoded envelope decodes and verifies",
            result: VerifyWire(bytes: wire)
        );

        Check(
            scenario: "[object boundary] raw signed-portion construction is not public",
            ok: (typeof(SignedCarriageEnvelope).GetMethod(
                name: nameof(SignedCarriageEnvelope.FromSignedPortion),
                bindingAttr: (System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ) is null),
            detail: "public callers can only construct an envelope through a codec-backed encoding path"
        );

        // This is the exact object-boundary attack the old public factory admitted: retain a valid
        // signature and its authentic bytes, but substitute the separately projected payload that the
        // application consumes after verification. The factory is now internal, and the verifier's
        // coherence guard makes even an accidentally forged in-assembly object fail closed.
        var forgedProjection = SignedCarriageEnvelope.FromSignedPortion(
            header: claim.Header,
            payloadKind: claim.PayloadKind,
            payloadBytes: Encoding.UTF8.GetBytes(s: "attacker-substituted payload"),
            signature: claim.Signature,
            signedPortion: claim.SignedPortion
        );
        var forgedProjectionResult = CarriageVerifier.VerifyChain(
            codec: codec,
            claim: forgedProjection,
            chain: null,
            trustList: trust,
            now: now,
            expectedPurpose: "test.claim",
            expectedAudience: "world:home"
        );

        ExpectRefuse(
            scenario: "[object boundary] a substituted payload projection cannot ride a valid signed portion",
            result: forgedProjectionResult,
            reasonMustContain: "parsed fields"
        );

        // ReadOnlyMemory is only a read-only VIEW; when it wraps a byte[], whoever owns that array can
        // still mutate it. Signing must therefore take its own copy rather than retaining caller storage.
        var callerOwnedPayload = Encoding.UTF8.GetBytes(s: "caller-owned payload");
        var callerOwnedControl = callerOwnedPayload.ToArray();
        var isolatedClaim = CarriageSigner.SignClaim(
            codec: codec,
            domain: keys.Domain,
            subject: keys.Subject,
            signerKey: keys.SubjectSigningKey,
            signerAlgorithm: CarriageAlgorithms.EcdsaP256Sha256,
            purpose: "test.claim",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            claimBytes: callerOwnedPayload
        );

        callerOwnedPayload.AsSpan().Fill(value: 0xA5);

        Check(
            scenario: "[object boundary] caller-owned payload storage cannot mutate a signed envelope",
            ok: isolatedClaim.PayloadBytes.Span.SequenceEqual(other: callerOwnedControl),
            detail: "the envelope owns defensive copies of all signed byte fields"
        );

        ExpectAccept(
            scenario: "[object boundary] mutating the caller's source array leaves the isolated envelope verifiable",
            result: CarriageVerifier.VerifyChain(
                codec: codec,
                claim: isolatedClaim,
                chain: null,
                trustList: trust,
                now: now,
                expectedPurpose: "test.claim",
                expectedAudience: "world:home"
            )
        );

        Check(
            scenario: "[fixed-layout-v1] arrived-bytes control: the decoded envelope carries the signed portion VERBATIM, not a re-encoding",
            ok: codec.DecodeEnvelope(wire: wire).SignedPortion.Span.SequenceEqual(other: wire.AsSpan(
                start: 0,
                length: (wire.Length - (sizeof(uint) + claim.Signature.Length))
            )),
            detail: "SignedPortion is the exact envelope prefix that arrived — what the signature is checked against"
        );

        foreach (var flag in new byte[] { 0x02, 0x03, 0x7F, 0xFF }) {
            var mutated = (byte[])wire.Clone();

            mutated[SubjectPresenceFlagOffset] = flag;

            ExpectThrows<FormatException>(
                scenario: $"[fixed-layout-v1] presence flag 0x{flag:X2} at offset {SubjectPresenceFlagOffset} (the subject field) — one model, one encoding (§15's mutated-but-parseable demonstration)",
                action: () => _ = codec.DecodeEnvelope(wire: mutated),
                messageMustContain: "presence flag"
            );
        }

        // The sequence field's presence flag, reached by position rather than by a hard-coded offset: a
        // second optional field proves the rule is the reader's, not one patched offset.
        var bearer = SignTestClaim(
            codec: codec,
            keys: keys,
            purpose: "test.bearer",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: null,
            sequence: 3UL,
            text: "jun's bearer claim"
        );
        var bearerWire = codec.EncodeEnvelope(envelope: bearer);
        var sequenceFlagOffset = FindSequencePresenceFlagOffset(wire: bearerWire);
        var mutatedBearer = (byte[])bearerWire.Clone();

        mutatedBearer[sequenceFlagOffset] = 0x02;

        ExpectThrows<FormatException>(
            scenario: $"[fixed-layout-v1] presence flag 0x02 at offset {sequenceFlagOffset} (the sequence field)",
            action: () => _ = codec.DecodeEnvelope(wire: mutatedBearer),
            messageMustContain: "presence flag"
        );

        // The general property, checked rather than argued: EVERY byte of a valid envelope is inside either
        // the signed portion or the signature, so no single-byte change can produce an accepted claim. This
        // is the sweep that would have caught the offset-33 defect without anyone having guessed the offset
        // — the previous fuzz targeted offset 34 and walked straight past it.
        var accepted = new List<string>();
        var crashed = new List<string>();
        var refusedAtDecode = 0;
        var refusedAtVerify = 0;

        for (var offset = 0; (offset < wire.Length); offset += 1) {
            for (var value = 0; (value < 256); value += 1) {
                if (value == wire[offset]) {
                    continue;
                }

                var mutated = (byte[])wire.Clone();

                mutated[offset] = (byte)value;

                try {
                    var result = VerifyWire(bytes: mutated);

                    if (result.Verified) {
                        accepted.Add(item: $"offset {offset} = 0x{value:X2}");
                    } else {
                        refusedAtVerify += 1;
                    }
                } catch (FormatException) {
                    refusedAtDecode += 1;
                } catch (Exception exception) {
                    crashed.Add(item: $"offset {offset} = 0x{value:X2} threw {exception.GetType().Name}");
                }
            }
        }

        Check(
            scenario: $"[fixed-layout-v1] single-byte mutation sweep: all {(wire.Length * 255):N0} single-byte mutations of a valid envelope",
            ok: ((accepted.Count == 0) && (crashed.Count == 0)),
            detail: (((accepted.Count == 0) && (crashed.Count == 0))
            ? $"none accepted — {refusedAtDecode:N0} refused at decode, {refusedAtVerify:N0} refused at verify"
            : $"{accepted.Count} ACCEPTED ({string.Join(
                separator: "; ",
                values: accepted.Take(count: 5)
            )}), {crashed.Count} crashed ({string.Join(
                separator: "; ",
                values: crashed.Take(count: 3)
            )})")
        );
    }

    /// <summary>
    /// Finds the sequence field's 1-byte presence flag in a fixed-layout envelope by walking the layout,
    /// rather than hard-coding an offset that only holds for one set of field widths.
    /// </summary>
    private static int FindSequencePresenceFlagOffset(byte[] wire) {
        // version(1) + domain(32), then subject, algorithm, purpose, notBefore(8), notAfter(8), audience.
        var offset = 33;

        int SkipOptionalString(int at) => ((wire[at] == 0)
            ? (at + 1)
            : SkipString(at: (at + 1)));
        int SkipString(int at) => ((at + sizeof(uint)) + (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(source: wire.AsSpan(start: at)));

        offset = SkipOptionalString(at: offset);
        offset = SkipString(at: offset);
        offset = SkipString(at: offset);
        offset += (sizeof(long) * 2);

        return SkipOptionalString(at: offset);
    }

    /// <summary>Builds a trust list that pins one subject's own signing key directly — the zero-hop shape, so a scenario exercises one signature rather than three.</summary>
    private static TrustList BuildDirectTrustList(DomainKeys keys, IReadOnlySet<string> reach) =>
        new(
        entries: [
                new TrustListEntry(
            PinnedId: keys.SubjectSigningId,
            PublicKeySubjectPublicKeyInfo: keys.SubjectSigningSpki,
            Mode: CarriageTrustMode.SignsDirectly,
            Reach: reach,
            MaximumAge: null
        ),
            ],
        defaultMaximumAge: null
    );

    // ---- Size and complexity report --------------------------------------------------------------------

    private static void RunSizeAndComplexityReport() {
        Console.WriteLine();
        Console.WriteLine(value: "=== Encoded size comparison (representative claim + 2-binding chain) ===");

        const long Epoch = 1_700_000_000L;

        var fixedCodec = new FixedLayoutCarriageCodec();
        var cborCodec = new CborCarriageCodec();
        var keysFixed = MintDomainKeys(subject: "user:erin");
        var keysCbor = MintDomainKeys(subject: "user:erin");

        var (rootToIssuingFixed, issuingToSubjectFixed) = BuildChain(
            codec: fixedCodec,
            keys: keysFixed,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var (rootToIssuingCbor, issuingToSubjectCbor) = BuildChain(
            codec: cborCodec,
            keys: keysCbor,
            notBefore: (Epoch - 30),
            notAfter: (Epoch + (86_400L * 30))
        );
        var claimText = "a representative claim payload of modest size, e.g. an item grant or a chat line";
        var claimFixed = SignTestClaim(
            codec: fixedCodec,
            keys: keysFixed,
            purpose: "chat.message",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: claimText
        );
        var claimCbor = SignTestClaim(
            codec: cborCodec,
            keys: keysCbor,
            purpose: "chat.message",
            notBefore: (Epoch - 60),
            notAfter: (Epoch + 3_600),
            audience: "world:home",
            sequence: null,
            text: claimText
        );

        var fixedRootBytes = fixedCodec.EncodeEnvelope(envelope: rootToIssuingFixed);
        var fixedIssuingBytes = fixedCodec.EncodeEnvelope(envelope: issuingToSubjectFixed);
        var fixedClaimBytes = fixedCodec.EncodeEnvelope(envelope: claimFixed);
        var cborRootBytes = cborCodec.EncodeEnvelope(envelope: rootToIssuingCbor);
        var cborIssuingBytes = cborCodec.EncodeEnvelope(envelope: issuingToSubjectCbor);
        var cborClaimBytes = cborCodec.EncodeEnvelope(envelope: claimCbor);

        var fixedTotal = ((fixedRootBytes.Length + fixedIssuingBytes.Length) + fixedClaimBytes.Length);
        var cborTotal = ((cborRootBytes.Length + cborIssuingBytes.Length) + cborClaimBytes.Length);

        Console.WriteLine(value: $"  fixed-layout: root-binding {fixedRootBytes.Length} B, issuing-binding {fixedIssuingBytes.Length} B, claim {fixedClaimBytes.Length} B, TOTAL {fixedTotal} B");
        Console.WriteLine(value: $"  cbor:         root-binding {cborRootBytes.Length} B, issuing-binding {cborIssuingBytes.Length} B, claim {cborClaimBytes.Length} B, TOTAL {cborTotal} B");
        Console.WriteLine(value: $"  difference:   CBOR is {(cborTotal - fixedTotal)} B ({((100.0 * (cborTotal - fixedTotal)) / fixedTotal):+0.0;-0.0}%) relative to fixed-layout for this representative set.");
        Console.WriteLine();
        Console.WriteLine(value: "  Recommendation: CBOR for the shipped format. Source-line totals are deliberately not printed:");
        Console.WriteLine(value: "  they stale on every edit and are not a protocol property. An earlier revision credited the fixed");
        Console.WriteLine(value: "  layout with getting canonicality 'for free'. It was not free, it was missing: the presence-flag");
        Console.WriteLine(value: "  rule (a reader treating non-zero as present gave one model 255 wire forms per optional field), the");
        Console.WriteLine(value: "  decoder-side payload-kind set check, and the re-encode identity check were all absent. The");
        Console.WriteLine(value: "  saving was measuring unwritten checks, not a simpler format. Every degree of freedom a");
        Console.WriteLine(value: "  format offers is one the decoder must close by hand, because a signature is over BYTES —");
        Console.WriteLine(value: "  and a hand-specified layout offers fewer of them but hands you no library that has already");
        Console.WriteLine(value: "  closed the rest. What actually decides it is reach: a third party can meet a CBOR spec with");
        Console.WriteLine(value: "  a library in any language, while the fixed layout obliges them to hand-write a parser from");
        Console.WriteLine(value: "  prose. The wire-size difference is noise (see the line above). The fixed layout remains");
        Console.WriteLine(value: "  worth keeping on the shelf for a context that cannot carry a CBOR implementation at all, or");
        Console.WriteLine(value: "  that wants every byte on the wire hand-specified with no library between spec and bytes.");
    }
}

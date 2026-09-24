using Xunit;

namespace Puck.Attestation.Tests;

/// <summary>
/// The test-only fixture-format check: <see cref="AttestationInterchangeHarness"/> mints a seven-file directory
/// and verifies it. Every case mints a fresh fixture in-process; the round trip proves the harness is
/// self-consistent, and the negative cases prove a corrupted claim or incomplete manifest is refused for the
/// reason each names, not merely refused.
/// </summary>
public sealed class InterchangeTests {
    private static void WithExportedFixture(Action<string> body) {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-attestation-interchange-{Guid.NewGuid():N}"
        );

        try {
            AttestationInterchangeHarness.Export(directory: directory);
            body(obj: directory);
        } finally {
            if (Directory.Exists(path: directory)) {
                Directory.Delete(
                    path: directory,
                    recursive: true
                );
            }
        }
    }

    [Fact]
    public void CorruptedClaim_OneFlippedByte_IsRefusedAsABadSignature() {
        WithExportedFixture(body: directory => {
            var claimPath = Path.Combine(
                path1: directory,
                path2: "claim.attestation"
            );
            var bytes = File.ReadAllBytes(path: claimPath);

            bytes[^1] ^= 0xFF;

            File.WriteAllBytes(
                bytes: bytes,
                path: claimPath
            );

            var findings = AttestationInterchangeHarness.Verify(directory: directory);

            // The claim is refused on its signature. The harness's own tamper control flips the same byte
            // back, so it restores the minted claim and is accepted — the second failure proves the control
            // tampers exactly the byte this test did.
            Assert.Equal(
                expected: [
                    (InterchangeCheck.Claim, "claim signature does not verify against the pinned subject key"),
                    (InterchangeCheck.TamperControl, "one flipped byte was accepted"),
                ],
                actual: findings.Where(predicate: finding => !finding.Passed).Select(selector: finding => (finding.Check, finding.Detail))
            );
            Assert.Contains(
                collection: findings,
                filter: finding => (finding is { Check: InterchangeCheck.ManifestAgreement, Passed: true })
            );
            Assert.Contains(
                collection: findings,
                filter: finding => (finding is { Check: InterchangeCheck.Sealed, Passed: true })
            );
        });
    }
    [Fact]
    public void MissingManifestKey_DroppingAudience_IsRefusedAsAMissingKey() {
        WithExportedFixture(body: directory => {
            var manifestPath = Path.Combine(
                path1: directory,
                path2: "manifest.txt"
            );
            var lines = File.ReadAllLines(path: manifestPath).Where(predicate: line => !line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "audience="
            ));

            File.WriteAllLines(
                contents: lines,
                path: manifestPath
            );

            var finding = Assert.Single(collection: AttestationInterchangeHarness.Verify(directory: directory));

            Assert.Equal(
                expected: new InterchangeFinding(
                    Check: InterchangeCheck.ManifestKeys,
                    Detail: "audience",
                    Passed: false
                ),
                actual: finding
            );
        });
    }
    [Fact]
    public void SelfRoundTrip_ExportedFixtureVerifiesAgainstItself() {
        WithExportedFixture(body: directory => {
            var findings = AttestationInterchangeHarness.Verify(directory: directory);

            Assert.All(
                action: finding => Assert.True(
                    condition: finding.Passed,
                    userMessage: $"{finding.Check}: {finding.Detail}"
                ),
                collection: findings
            );
            Assert.Equal(
                expected: [
                    InterchangeCheck.Claim,
                    InterchangeCheck.ReplayContract,
                    InterchangeCheck.ManifestAgreement,
                    InterchangeCheck.TamperControl,
                    InterchangeCheck.Sealed,
                    InterchangeCheck.SealedAadControl,
                ],
                actual: findings.Select(selector: finding => finding.Check)
            );
        });
    }
}

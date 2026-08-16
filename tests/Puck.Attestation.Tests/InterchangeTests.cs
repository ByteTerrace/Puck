using Xunit;

namespace Puck.Attestation.Tests;

/// <summary>
/// The test-only fixture-format check: <see cref="AttestationInterchangeHarness"/> mints a seven-file directory
/// and verifies it. Every case mints a fresh fixture in-process; the round trip proves the harness is
/// self-consistent, and the negative cases prove a corrupted claim or incomplete manifest is refused.
/// </summary>
public sealed class InterchangeTests {
    [Fact]
    public void SelfRoundTrip_ExportedFixtureVerifiesAgainstItself() {
        var directory = ExportToTempDirectory();

        try {
            Assert.Equal(expected: 0, actual: AttestationInterchangeHarness.Verify(directory: directory));
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    [Fact]
    public void CorruptedClaim_OneFlippedByte_IsRefused() {
        var directory = ExportToTempDirectory();

        try {
            var claimPath = Path.Combine(path1: directory, path2: "claim.attestation");
            var bytes = File.ReadAllBytes(path: claimPath);

            bytes[^1] ^= 0xFF;

            File.WriteAllBytes(bytes: bytes, path: claimPath);

            Assert.Equal(expected: 1, actual: AttestationInterchangeHarness.Verify(directory: directory));
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
    [Fact]
    public void MissingManifestKey_DroppingAudience_IsRefused() {
        var directory = ExportToTempDirectory();

        try {
            var manifestPath = Path.Combine(path1: directory, path2: "manifest.txt");
            var lines = File.ReadAllLines(path: manifestPath).Where(predicate: line => !line.StartsWith(comparisonType: StringComparison.Ordinal, value: "audience="));

            File.WriteAllLines(contents: lines, path: manifestPath);

            Assert.Equal(expected: 1, actual: AttestationInterchangeHarness.Verify(directory: directory));
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }

    private static string ExportToTempDirectory() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-attestation-interchange-{Guid.NewGuid():N}"
        );

        Assert.Equal(expected: 0, actual: AttestationInterchangeHarness.Export(directory: directory));

        return directory;
    }
}

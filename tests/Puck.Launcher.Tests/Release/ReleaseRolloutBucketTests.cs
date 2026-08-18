using System.Security.Cryptography;
using System.Text;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="ReleaseRolloutBucket"/>: the exact boundary at a chosen percent, 0 and 100 as
/// closed/open ends, and the mint-or-load persistence contract.</summary>
public sealed class ReleaseRolloutBucketTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-launcher-rollout-tests-{Guid.NewGuid():n}");

    public void Dispose() {
        if (Directory.Exists(path: m_root)) {
            Directory.Delete(path: m_root, recursive: true);
        }
    }

    // Finds an install id whose bucket value is EXACTLY the requested modulo-100 remainder, by trying candidate
    // hex ids in order — deterministic (no RNG at assertion time) and independent of the production hash path
    // beyond calling the exact function under test.
    private static string FindInstallIdWithBucket(uint targetBucket) {
        for (var candidate = 0UL; (candidate < 100_000UL); candidate++) {
            var installId = candidate.ToString(format: "x32");
            var hash = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: installId));
            var bucket = (((uint)hash[0]) << 24) | (((uint)hash[1]) << 16) | (((uint)hash[2]) << 8) | hash[3];

            if ((bucket % 100U) == targetBucket) {
                return installId;
            }
        }

        throw new InvalidOperationException(message: $"no candidate installId found with bucket {targetBucket} within the search budget.");
    }

    [Fact]
    public void IsIncluded_ExactBoundary_AcceptsBelowPercentRefusesAtPercent() {
        const int Percent = 37;
        var justBelow = FindInstallIdWithBucket(targetBucket: (Percent - 1));
        var atPercent = FindInstallIdWithBucket(targetBucket: Percent);

        Assert.True(condition: ReleaseRolloutBucket.IsIncluded(installId: justBelow, percent: Percent));
        Assert.False(condition: ReleaseRolloutBucket.IsIncluded(installId: atPercent, percent: Percent));
    }
    [Fact]
    public void IsIncluded_ZeroPercent_AlwaysExcludes() =>
        Assert.False(condition: ReleaseRolloutBucket.IsIncluded(installId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", percent: 0));
    [Fact]
    public void IsIncluded_HundredPercent_AlwaysIncludes() =>
        Assert.True(condition: ReleaseRolloutBucket.IsIncluded(installId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", percent: 100));
    [Fact]
    public void MintOrLoad_PersistsAcrossCalls() {
        var first = ReleaseRolloutBucket.MintOrLoad(cacheRoot: m_root);
        var second = ReleaseRolloutBucket.MintOrLoad(cacheRoot: m_root);

        Assert.Equal(actual: second, expected: first);
        Assert.Equal(expected: 32, actual: first.Length);
    }
}

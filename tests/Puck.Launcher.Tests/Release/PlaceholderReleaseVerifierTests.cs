using Microsoft.Extensions.DependencyInjection;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over the build-time placeholder trust anchor: a composition root that has not yet pinned a real
/// release-signing chain must refuse every manifest, never crash importing the placeholder's (empty) key bytes.</summary>
public sealed class PlaceholderReleaseVerifierTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    public void Dispose() => m_root.Dispose();

    [Fact]
    public void ReleaseTrustAnchor_Placeholder_IsRecognizedByDomainAlone() {
        Assert.True(condition: ReleaseTrustAnchor.Placeholder.IsPlaceholder);
        Assert.False(condition: new ReleaseChainFixture().TrustAnchor.IsPlaceholder);
    }
    [Fact]
    public void PlaceholderReleaseVerifier_Refuses_EvenAValidlySignedManifest() {
        var fixture = new ReleaseChainFixture();
        var signed = fixture.Sign(
            document: new ReleaseManifest(
                App: "puck.world",
                Channel: "stable",
                MinimumSupported: null,
                Notes: null,
                Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1)])],
                Revoked: null,
                Rollout: new ReleaseRollout(Percent: 100),
                Schema: ReleaseManifest.CurrentSchema,
                Signature: null,
                StateGeneration: 1,
                Version: "1.0.1"
            ),
            notAfter: (ReleaseChainFixture.Epoch + 3600),
            notBefore: ReleaseChainFixture.Epoch,
            sequence: 1
        );

        var outcome = new PlaceholderReleaseVerifier().Verify(
            advanceSequence: true,
            installedVersion: "1.0.0",
            manifest: signed,
            now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch)
        );

        Assert.False(condition: outcome.Accepted);
        Assert.Contains(actualString: outcome.RefusalReason!, comparisonType: StringComparison.Ordinal, expectedSubstring: "placeholder");
    }
    [Fact]
    public void AddSelfUpdate_ResolvesThePlaceholderVerifier_WhenTheTrustAnchorIsUnpinned() {
        var services = new ServiceCollection();

        services.AddSelfUpdate(
            options: new UpdateOptions(
                App: "puck.world",
                CacheRoot: m_root.RootPath,
                Channel: "stable",
                InstalledVersion: "0.0.0",
                TrustAnchor: ReleaseTrustAnchor.Placeholder
            ),
            releaseSource: new DirectoryReleaseSource(root: Path.Combine(path1: m_root.RootPath, path2: "release"))
        );

        var verifier = services.BuildServiceProvider().GetRequiredService<IReleaseVerifier>();

        Assert.IsType<PlaceholderReleaseVerifier>(@object: verifier);
    }
}

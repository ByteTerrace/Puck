using Puck.Assets;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

// A release manifest parses its pins through ContentPin, the same door the archive that retains it uses, so an
// uppercase pin is refused at validation rather than admitted and then refused (or missed) by the archive.
public sealed class WorldReleasePinLawTests {
    private static readonly string Upper = new(
        c: 'A',
        count: 64
    );

    private static WorldReleaseManifest Manifest() => new() {
        CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        Label = "pin-test",
        SourceRevision = new string(
        c: 'a',
        count: 40
    ),
        EngineImageDigest = ("sha256:" + new string(
        c: 'b',
        count: 64
    )),
        PersistenceContract = "test",
        PeerProtocolContract = "test",
        Definitions = new Dictionary<string, string> { ["owner/world"] = ContentPin.Compute(content: "world"u8).ToString() },
        DefinitionFiles = new Dictionary<string, string> { ["owner/world"] = "world.json" },
    };

    [Fact]
    public void LowercasePinsValidateAndIdentityIsAPin() {
        var manifest = Manifest();

        Assert.True(
            condition: WorldReleaseManifest.TryValidate(
                manifest: manifest,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(condition: ContentPin.TryParse(
            pin: out _,
            text: manifest.Identity
        ));
    }
    [Fact]
    public void UppercaseDefinitionPinIsRefused() {
        Assert.False(condition: WorldReleaseManifest.TryValidate(
            manifest: Manifest() with { Definitions = new Dictionary<string, string> { ["owner/world"] = $"sha256/{Upper}" } },
            reason: out _
        ));
    }
    [Fact]
    public void UppercaseArtifactPinIsRefused() {
        Assert.False(condition: WorldReleaseManifest.TryValidate(
            manifest: Manifest() with { Artifacts = new Dictionary<string, string> { ["engine/image.txt"] = $"sha256/{Upper}" } },
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "full sha256 pin"
        );
    }
    [Fact]
    public void UppercaseImageDigestIsRefused() {
        Assert.False(condition: WorldReleaseManifest.TryValidate(
            manifest: Manifest() with { EngineImageDigest = $"sha256:{Upper}" },
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "immutable sha256 digest"
        );
    }
}

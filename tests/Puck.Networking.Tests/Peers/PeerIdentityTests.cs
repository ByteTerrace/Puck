using System.Security.Cryptography;
using Puck.Networking.Peers;
using Xunit;

namespace Puck.Networking.Tests.Peers;

/// <summary>A <see cref="PeerIdentity"/> is exactly one P-256 key: bytes that carry anything else — another
/// curve, or trailing data after the key — are refused where they are loaded, by name. Its file form round-trips
/// the same <see cref="PeerIdentity.Id"/>, replaces whatever sat at the path before, leaves no temporary file
/// behind, and on Unix is readable by its owner alone.</summary>
public sealed class PeerIdentityTests {
    private static byte[] P384PrivateKey() {
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP384);

        return key.ExportPkcs8PrivateKey();
    }

    [Fact]
    public void FromPkcs8PrivateKey_WithAP384Key_ThrowsArgumentException() {
        var pkcs8 = P384PrivateKey();

        Assert.Throws<ArgumentException>(testCode: () => PeerIdentity.FromPkcs8PrivateKey(pkcs8PrivateKey: pkcs8));
    }
    [Fact]
    public void FromPkcs8PrivateKey_WithTrailingBytes_ThrowsArgumentException() {
        using var seed = PeerIdentity.Create();

        byte[] withTrailingByte = [.. seed.ExportPkcs8PrivateKey(), 0];

        Assert.Throws<ArgumentException>(testCode: () => PeerIdentity.FromPkcs8PrivateKey(pkcs8PrivateKey: withTrailingByte));
    }
    [Fact]
    public void SaveThenLoad_RoundTripsTheId_AndLeavesNoTemporaryFile() {
        using var directory = new TemporaryDirectory();
        using var identity = PeerIdentity.Create();

        var path = directory.PathOf(fileName: "peer.key");

        identity.Save(path: path);

        using var loaded = PeerIdentity.Load(path: path);

        Assert.Equal(
            expected: identity.Id.Domain,
            actual: loaded.Id.Domain
        );
        Assert.Equal(
            expected: identity.SubjectPublicKeyInfo,
            actual: loaded.SubjectPublicKeyInfo
        );
        Assert.Equal(
            expected: identity.ExportPkcs8PrivateKey(),
            actual: File.ReadAllBytes(path: path)
        );
        Assert.False(condition: File.Exists(path: (path + ".tmp")));
    }
    [Fact]
    public void Save_ReplacesAnExistingFile_AndAStaleTemporaryFile() {
        using var directory = new TemporaryDirectory();
        using var previous = PeerIdentity.Create();
        using var identity = PeerIdentity.Create();

        var path = directory.PathOf(fileName: "peer.key");

        // What a crash between an earlier Save's write and its move leaves behind: a real key at the path, and a
        // sibling .tmp that must not be mistaken for a file another writer is still filling.
        previous.Save(path: path);
        File.WriteAllText(
            contents: "not a key",
            path: (path + ".tmp")
        );

        identity.Save(path: path);

        using var loaded = PeerIdentity.Load(path: path);

        Assert.Equal(
            expected: identity.Id.Domain,
            actual: loaded.Id.Domain
        );
        Assert.NotEqual(
            expected: previous.Id.Domain,
            actual: loaded.Id.Domain
        );
        Assert.False(condition: File.Exists(path: (path + ".tmp")));
    }
    [Fact]
    public void Save_OnUnix_CreatesTheFileReadableAndWritableByItsOwnerAlone() {
        if (OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "a Unix create mode has no meaning on Windows");

            return;
        }

        using var directory = new TemporaryDirectory();
        using var identity = PeerIdentity.Create();

        var path = directory.PathOf(fileName: "peer.key");

        identity.Save(path: path);

        Assert.Equal(
            expected: UnixFileMode.UserRead | UnixFileMode.UserWrite,
            actual: File.GetUnixFileMode(path: path)
        );
    }

    /// <summary>A per-law directory under the temp root, created on construction and deleted whole on dispose; a
    /// deletion failure fails the law rather than masking a handle the tested code left open.</summary>
    private sealed class TemporaryDirectory : IDisposable {
        private readonly string m_root = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-peer-identity-{Guid.NewGuid():n}"
        );

        public TemporaryDirectory() {
            Directory.CreateDirectory(path: m_root);
        }

        public string PathOf(string fileName) => Path.Combine(
            path1: m_root,
            path2: fileName
        );
        public void Dispose() => Directory.Delete(
            path: m_root,
            recursive: true
        );
    }
}

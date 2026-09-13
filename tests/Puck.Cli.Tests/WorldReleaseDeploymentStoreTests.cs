using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Cli.Azure;
using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Retained deployment inputs survive controller replacement without following a mutable latest secret,
/// leaking bootstrap credentials to ordinary storage, or replacing an already published configuration.</summary>
public sealed class WorldReleaseDeploymentStoreTests : IDisposable {
    private readonly string m_directory = Path.Combine(Path.GetTempPath(), "puck-release-retention-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider m_provider;
    private readonly IObjectBlobStore m_blobs;
    private readonly Guid m_owner = Guid.NewGuid();
    private readonly Secrets m_secrets = new();
    private readonly WorldReleaseManifest m_manifest = new() {
        Label = "test", SourceRevision = "test", EngineImageDigest = "sha256:" + new string('a', 64),
        Definitions = new Dictionary<string, string> { ["world"] = "sha256/" + new string('b', 64) },
        DefinitionFiles = new Dictionary<string, string> { ["world"] = "world.json" },
        PersistenceContract = "test", PeerProtocolContract = "test",
    };
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public WorldReleaseDeploymentStoreTests() {
        var services = new ServiceCollection();
        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services);
        m_provider = services.BuildServiceProvider();
        m_blobs = m_provider.GetRequiredService<IObjectBlobStore>();
    }
    private WorldReleaseDeploymentStore Store() => new(m_blobs, new DirectoryObjectStorageTarget(m_directory), m_owner, m_secrets);
    private WorldReleaseDeploymentConfiguration Configuration() => new(m_manifest.Identity, "official", new JsonObject {
        ["release"] = "example.azurecr.io/world@" + m_manifest.EngineImageDigest,
        ["bootstrapCommand"] = "bootstrap-secret-value", ["capacity"] = 1,
    }, "public-key", new JsonObject { ["resources"] = new JsonArray() });

    [Fact]
    public async Task RestartReadsExactVersionAndOrdinaryStorageContainsNoBootstrapSecret() {
        var configuration = Configuration();
        await Store().SaveAsync(m_manifest, configuration, Token);
        configuration.Parameters["capacity"] = 2;
        m_secrets.Latest = "another-version";
        var restored = await Store().LoadAsync(m_manifest, "official", Token);
        Assert.NotNull(restored);
        Assert.Equal(1, restored.Parameters["capacity"]!.GetValue<int>());
        Assert.Equal("bootstrap-secret-value", restored.Parameters["bootstrapCommand"]!.GetValue<string>());
        foreach (var file in Directory.EnumerateFiles(m_directory, "*", SearchOption.AllDirectories)) {
            Assert.DoesNotContain("bootstrap-secret-value", Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SameReleaseCannotReplaceConfigurationAndPropertyOrderDoesNotCreateAConflict() {
        await Store().SaveAsync(m_manifest, Configuration(), Token);
        var reordered = Configuration() with { Parameters = new JsonObject {
            ["capacity"] = 1, ["bootstrapCommand"] = "bootstrap-secret-value",
            ["release"] = "example.azurecr.io/world@" + m_manifest.EngineImageDigest,
        } };
        await Store().SaveAsync(m_manifest, reordered, Token);
        reordered.Parameters["capacity"] = 2;
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(m_manifest, reordered, Token));
        Assert.Equal(1, m_secrets.Writes);
        Assert.Equal(1, (await Store().LoadAsync(m_manifest, "official", Token))!.Parameters["capacity"]!.GetValue<int>());
    }

    [Fact]
    public async Task LostSecretWriteResponseLeavesNoReferenceAndRetryPublishesVerifiedInputs() {
        m_secrets.LoseWriteResponse = true;
        await Assert.ThrowsAsync<IOException>(() => Store().SaveAsync(m_manifest, Configuration(), Token));
        Assert.Null(await Store().LoadAsync(m_manifest, "official", Token));
        m_secrets.LoseWriteResponse = false;
        await Store().SaveAsync(m_manifest, Configuration(), Token);
        Assert.NotNull(await Store().LoadAsync(m_manifest, "official", Token));
        m_secrets.CorruptRead = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().LoadAsync(m_manifest, "official", Token));
    }

    [Fact]
    public async Task CorruptSecretReadBackCannotPublishAReference() {
        m_secrets.CorruptRead = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(m_manifest, Configuration(), Token));
        Assert.Null(await Store().LoadAsync(m_manifest, "official", Token));
    }

    [Fact]
    public async Task ABackendWithoutAVersionCannotPublishAnUnrecoverableReference() {
        m_secrets.EmptyVersion = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(m_manifest, Configuration(), Token));
        Assert.Null(await Store().LoadAsync(m_manifest, "official", Token));
    }

    public void Dispose() {
        m_provider.Dispose();
        if (Directory.Exists(m_directory)) { Directory.Delete(m_directory, recursive: true); }
    }

    private sealed class Secrets : IWorldReleaseSecretVersions {
        private readonly Dictionary<string, byte[]> m_versions = [];
        public int Writes { get; private set; }
        public string? Latest { get; set; }
        public bool LoseWriteResponse { get; set; }
        public bool CorruptRead { get; set; }
        public bool EmptyVersion { get; set; }
        public Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) {
            var version = "https://example.vault.azure.net/secrets/release/" + (++Writes);
            m_versions.Add(version, content.ToArray()); Latest = version;
            return LoseWriteResponse ? Task.FromException<string>(new IOException("write response lost")) : Task.FromResult(EmptyVersion ? "" : version);
        }
        public Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<byte>>(CorruptRead ? "corrupt"u8.ToArray() : m_versions[version]);
    }
}

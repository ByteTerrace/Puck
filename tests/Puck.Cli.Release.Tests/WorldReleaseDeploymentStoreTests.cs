using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Cli.Azure;
using Puck.Storage;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Release.Tests;

/// <summary>Retained deployment inputs survive controller replacement without following a mutable latest secret,
/// leaking signing keys to ordinary storage, putting bulk inputs in the secret, or replacing an already published
/// configuration.</summary>
public sealed class WorldReleaseDeploymentStoreTests : IDisposable {
    private const string KeyValue = "c2lnbmluZy1rZXktc2VjcmV0LXZhbHVl";

    private readonly TemporaryDirectory m_directory = new(prefix: "puck-release-retention-");
    private readonly Guid m_owner = Guid.NewGuid();
    private readonly Secrets m_secrets = new();
    private readonly WorldReleaseManifest m_manifest = new() {
        CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        Label = "test",
        SourceRevision = "test",
        EngineImageDigest = ("sha256:" + new string(
        c: 'a',
        count: 64
    )),
        Definitions = new Dictionary<string, string> {
            ["world"] = ("sha256/" + new string(
        c: 'b',
        count: 64
    )),
        },
        DefinitionFiles = new Dictionary<string, string> { ["world"] = "world.json" },
        PersistenceContract = "test",
        PeerProtocolContract = "test",
    };
    private readonly Dictionary<string, string> m_keys = new() { ["federation.pk8"] = KeyValue };

    private readonly IObjectBlobStore m_blobs;
    private readonly ServiceProvider m_provider;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public WorldReleaseDeploymentStoreTests() {
        var services = new ServiceCollection();

        Puck.Storage.DependencyInjection.PuckStorageServiceRegistration.AddCore(services: services);
        m_provider = services.BuildServiceProvider();
        m_blobs = m_provider.GetRequiredService<IObjectBlobStore>();
    }

    private WorldReleaseDeploymentConfiguration Configuration() => new(
        m_manifest.Identity,
        "official",
        new JsonObject {
            ["release"] = ("example.azurecr.io/world@" + m_manifest.EngineImageDigest),
            ["capacity"] = 1,
        },
        "public-key",
        new JsonObject { ["resources"] = new JsonArray() },
        "bootstrap-script",
        "launcher-program"
    );
    private WorldReleaseDeploymentStore Store() => new(
        m_blobs,
        new DirectoryObjectStorageTarget(m_directory.RootPath),
        m_owner,
        m_secrets
    );
    private void AssertOrdinaryStorageHoldsNo(string value) {
        foreach (var file in Directory.EnumerateFiles(
            path: m_directory.RootPath,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        )) {
            Assert.DoesNotContain(
                value,
                Encoding.UTF8.GetString(bytes: File.ReadAllBytes(path: file)),
                StringComparison.Ordinal
            );
        }
    }

    [Fact]
    public async Task ABackendWithoutAVersionCannotPublishAnUnrecoverableReference() {
        m_secrets.EmptyVersion = true;
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Store().SaveAsync(
            m_manifest,
            Configuration(),
            m_keys,
            Token
        ));
        Assert.Null(@object: await Store().LoadAsync(
            m_manifest,
            "official",
            Token
        ));
    }
    [Fact]
    public async Task CorruptSecretReadBackCannotPublishAReference() {
        m_secrets.CorruptRead = true;
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Store().SaveAsync(
            m_manifest,
            Configuration(),
            m_keys,
            Token
        ));
        Assert.Null(@object: await Store().LoadAsync(
            m_manifest,
            "official",
            Token
        ));
    }
    public void Dispose() {
        m_provider.Dispose();
        m_directory.Dispose();
    }
    [Fact]
    public async Task InputsHoldingKeyMaterialAreRefusedBeforeAnythingIsWritten() {
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Store().SaveAsync(
            m_manifest,
            Configuration() with { Bootstrap = ("keys=" + KeyValue) },
            m_keys,
            Token
        ));
        Assert.Equal(
            0,
            m_secrets.Writes
        );
        Assert.Empty(collection: Directory.EnumerateFiles(
            path: m_directory.RootPath,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        ));
    }
    [Fact]
    public async Task LostSecretWriteResponseLeavesNoReferenceAndRetryPublishesVerifiedInputs() {
        m_secrets.LoseWriteResponse = true;
        await Assert.ThrowsAsync<IOException>(testCode: () => Store().SaveAsync(
            m_manifest,
            Configuration(),
            m_keys,
            Token
        ));
        Assert.Null(@object: await Store().LoadAsync(
            m_manifest,
            "official",
            Token
        ));
        m_secrets.LoseWriteResponse = false;
        await Store().SaveAsync(
            m_manifest,
            Configuration(),
            m_keys,
            Token
        );
        Assert.NotNull(@object: await Store().LoadAsync(
            m_manifest,
            "official",
            Token
        ));
        m_secrets.CorruptRead = true;
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Store().LoadAsync(
            m_manifest,
            "official",
            Token
        ));
    }
    /// <summary>A production-shaped release (the checkout's real bootstrap files, many co-hosted worlds and a large
    /// compute template) retains its inputs in the content store and leaves only the keys and the inputs' pin in a
    /// secret that fits Key Vault, and the protected command carries nothing else.</summary>
    [Fact]
    public async Task ProductionShapedInputsLeaveOnlyKeysAndAPinInTheSecret() {
        Assert.True(
            condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root),
            userMessage: "repository root is required"
        );
        const int Worlds = 64;
        var owner = m_owner.ToString(format: "D");
        var definitions = new Dictionary<string, string>();
        var files = new Dictionary<string, string>();

        for (var index = 0; (index < Worlds); index++) {
            var identity = ((owner + "/") + ((index == 0)
                ? "debug"
                : ("moon-" + index)));

            definitions[identity] = ContentPin.OfText(text: identity).ToString();
            files[identity] = (identity[(owner.Length + 1)..] + ".world.json");
        }
        var manifest = m_manifest with { Definitions = definitions, DefinitionFiles = files };
        var keys = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var identity in definitions.Keys) {
            using var key = System.Security.Cryptography.ECDsa.Create(curve: System.Security.Cryptography.ECCurve.NamedCurves.nistP256);

            keys[(identity.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/debug"
            )
                ? "federation.pk8"
                : (("federation-" + ContentDigest.HexOfText(text: identity)) + ".pk8"))] = Convert.ToBase64String(inArray: key.ExportPkcs8PrivateKey());
        }
        var template = new JsonObject { ["resources"] = new JsonArray([.. Enumerable.Range(
            count: 2048,
            start: 0
        ).Select(selector: index => (JsonNode)new JsonObject { ["name"] = ("resource-" + index), ["description"] = Guid.NewGuid().ToString(format: "N") })]) };
        var configuration = AzureCommand.RenderWorldReleaseDeployment(
            outputs: Outputs(owner: owner),
            manifest: manifest,
            image: ("example.azurecr.io/world-silo@" + manifest.EngineImageDigest),
            publicKey: "cHVibGljLWtleQ==",
            sshPublicKey: "ssh-ed25519 AAAA",
            computeTemplate: template,
            bootstrapScript: File.ReadAllText(path: Path.Combine(
                path1: root,
                path2: "build/Start-WorldSilo.sh"
            )),
            guardScript: File.ReadAllBytes(path: Path.Combine(
                path1: root,
                path2: "build/Guard-WorldRelease.py"
            )),
            launcherScript: File.ReadAllText(path: Path.Combine(
                path1: root,
                path2: "build/Boot-WorldSilo.py"
            ))
        );

        m_secrets.Budget = AzureCommand.WorldReleaseSecretBudget;
        await Store().SaveAsync(
            manifest,
            configuration,
            keys,
            Token
        );
        var restored = (await Store().LoadAsync(
            manifest,
            "official",
            Token
        ))!;
        var secret = Encoding.UTF8.GetString(bytes: m_secrets.LastWrite);

        Assert.Equal(
            configuration.Bootstrap,
            restored.Configuration.Bootstrap
        );
        Assert.Equal(
            Worlds,
            restored.Keys.Count
        );
        Assert.Contains(
            restored.Inputs,
            secret,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "resource-0",
            secret,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "release_guard",
            secret,
            StringComparison.Ordinal
        );
        foreach (var key in keys.Values) {
            AssertOrdinaryStorageHoldsNo(value: key);
        }
        var command = AzureCommand.WorldReleaseBootstrapCommand(deployment: restored);

        Assert.DoesNotContain(
            "resource-0",
            command,
            StringComparison.Ordinal
        );
        Assert.True(
            condition: (command.Length < (configuration.Bootstrap.Length + (Worlds * 512))),
            userMessage: $"the protected command holds the launcher, the pin and the keys only, yet is {command.Length} characters"
        );
    }
    [Fact]
    public async Task RestartReadsExactVersionAndOrdinaryStorageContainsNoSigningKey() {
        var configuration = Configuration();

        await Store().SaveAsync(
            m_manifest,
            configuration,
            m_keys,
            Token
        );
        configuration.Parameters["capacity"] = 2;
        m_secrets.Latest = "another-version";
        var restored = await Store().LoadAsync(
            m_manifest,
            "official",
            Token
        );

        Assert.NotNull(@object: restored);
        Assert.Equal(
            1,
            restored.Configuration.Parameters["capacity"]!.GetValue<int>()
        );
        Assert.Equal(
            KeyValue,
            restored.Keys["federation.pk8"]
        );
        AssertOrdinaryStorageHoldsNo(value: KeyValue);
    }
    [Fact]
    public async Task SameReleaseCannotReplaceConfigurationAndPropertyOrderDoesNotCreateAConflict() {
        await Store().SaveAsync(
            m_manifest,
            Configuration(),
            m_keys,
            Token
        );
        var reordered = Configuration() with {
            Parameters = new JsonObject {
                ["capacity"] = 1,
                ["release"] = ("example.azurecr.io/world@" + m_manifest.EngineImageDigest),
            },
        };

        await Store().SaveAsync(
            m_manifest,
            reordered,
            m_keys,
            Token
        );
        reordered.Parameters["capacity"] = 2;
        await Assert.ThrowsAsync<InvalidDataException>(testCode: () => Store().SaveAsync(
            m_manifest,
            reordered,
            m_keys,
            Token
        ));
        Assert.Equal(
            1,
            m_secrets.Writes
        );
        Assert.Equal(
            1,
            (await Store().LoadAsync(
                m_manifest,
                "official",
                Token
            ))!.Configuration.Parameters["capacity"]!.GetValue<int>()
        );
    }
    private static JsonNode Outputs(string owner) {
        static JsonObject Value(JsonNode value) => new() { ["value"] = value };

        return new JsonObject {
            ["worldSiloConfiguration"] = Value(value: new JsonObject {
                ["name"] = "official",
                ["worldName"] = "debug",
                ["port"] = 7825,
                ["dns"] = new JsonObject { ["recordName"] = "play", ["zoneName"] = "example.com" },
                ["lifecycle"] = new JsonObject {
                    ["healthPort"] = 8081,
                    ["shutdownSeconds"] = 60,
                    ["progressTimeoutSeconds"] = 30,
                    ["checkpointTimeoutSeconds"] = 30,
                    ["journalTimeoutSeconds"] = 30,
                    ["journalBacklogLimit"] = 1024,
                    ["azureScheduledEvents"] = true,
                    ["pollSeconds"] = 5,
                },
                ["authentication"] = new JsonObject { ["type"] = "azure.api-users", ["settings"] = new JsonObject { ["tenantId"] = Guid.NewGuid().ToString(format: "D") } },
                ["container"] = new JsonObject { ["cpu"] = 2, ["memoryInGB"] = 4 },
            }),
            ["worldSiloOwner"] = Value(value: owner),
            ["worldSiloStorageEndpoint"] = Value(value: "https://store.blob.example.net/"),
            ["worldSiloClientId"] = Value(value: Guid.NewGuid().ToString(format: "D")),
            ["deploymentLocation"] = Value(value: "centralus"),
            ["deploymentTags"] = Value(value: new JsonObject()),
            ["worldSiloIdentityResourceId"] = Value(value: "/subscriptions/0/identity"),
            ["worldMcpConfiguration"] = Value(value: new JsonObject {
                ["options"] = new JsonObject { ["publicUrl"] = "https://play.example.com/mcp", ["allowedSubjects"] = new JsonArray("subject") },
            }),
        };
    }

    private sealed class Secrets : IWorldReleaseSecretVersions {
        private readonly Dictionary<string, byte[]> m_versions = [];

        public int Budget { get; set; } = int.MaxValue;
        public bool CorruptRead { get; set; }
        public bool EmptyVersion { get; set; }
        public byte[] LastWrite { get; private set; } = [];
        public string? Latest { get; set; }
        public bool LoseWriteResponse { get; set; }
        public int Writes { get; private set; }

        public Task<ReadOnlyMemory<byte>> ReadAsync(string version, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<byte>>(result: (CorruptRead
                ? "corrupt"u8.ToArray()
                : m_versions[version]));
        public Task<string> WriteAsync(string release, ReadOnlyMemory<byte> content, CancellationToken cancellationToken) {
            var encoded = AzureCommand.EncodeWorldReleaseSecret(content: content.Span).Length;

            if (encoded > Budget) { throw new InvalidDataException(message: $"secret encodes to {encoded} characters, beyond {Budget}"); }
            var version = ("https://example.vault.azure.net/secrets/release/" + (++Writes));

            LastWrite = content.ToArray();
            m_versions.Add(
                key: version,
                value: LastWrite
            ); Latest = version;
            return (LoseWriteResponse
                ? Task.FromException<string>(exception: new IOException(message: "write response lost"))
                : Task.FromResult(result: (EmptyVersion
                    ? ""
                    : version))
            );
        }
    }
}

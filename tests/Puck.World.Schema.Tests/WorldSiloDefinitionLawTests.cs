using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves <see cref="WorldSiloDefinitionValidator"/>'s checks and <see cref="WorldSiloDefinitionSerialization"/>'s
/// round-trip over <c>puck.silo.def.v1</c>.</summary>
public sealed class WorldSiloDefinitionLawTests : IDisposable {
    [Fact]
    public void OmittedHealthSettingsUseTheDocumentedDefaults() {
        var definition = MakeValid();
        var document = System.Text.Json.Nodes.JsonNode.Parse(WorldSiloDefinitionSerialization.Serialize(definition: definition))!;

        document["lifecycle"] = System.Text.Json.Nodes.JsonNode.Parse("""{"shutdownSeconds":120,"healthPort":8081}""");
        var path = Path.Combine(path1: m_directory, path2: "lifecycle-defaults.json");

        File.WriteAllText(path, document.ToJsonString());
        Assert.True(condition: WorldSiloDefinitionSerialization.TryLoadFile(definition: out var loaded, path: path, reason: out var reason), userMessage: reason);
        Assert.Equal(new WorldSiloLifecycle(120, 8081), loaded!.Lifecycle);
    }
    [InlineData("Localhost", true)]
    [InlineData("localhost", false)]
    [InlineData("unknown", false)]
    [Theory]
    public void InstalledClusteringVocabularyIsCaseSensitive(string kind, bool accepted) {
        var definition = MakeValid() with { Clustering = new(kind) };

        Assert.Equal(accepted, WorldSiloDefinitionValidator.TryValidate(definition, out var reason, ["Localhost"]));
        if (!accepted) { Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "clustering.kind"); }
    }
    [InlineData("{\"path\":\"root\"}", true)]
    [InlineData("{\"path\":\"root\",\"path\":\"other\"}", false)]
    [InlineData("{\"path\":\"root\",\"extra\":1}", false)]
    [InlineData("{\"other\":\"root\"}", false)]
    [InlineData("{}", false)]
    [InlineData("[]", false)]
    [Theory]
    public void ProviderSettingsRejectExtraMissingAndDuplicateProperties(string json, bool accepted) {
        var settings = JsonElement.Parse(json);

        if (accepted) { Assert.Equal("root", WorldExtensionSettings.OnlySetting(settings, "path").GetString()); } else { Assert.Throws<ArgumentException>(() => WorldExtensionSettings.OnlySetting(settings, "path")); }
    }

    private readonly string m_directory;

    public WorldSiloDefinitionLawTests() {
        m_directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-silo-def-tests-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path: m_directory);
    }

    public void Dispose() {
        try {
            Directory.Delete(
                path: m_directory,
                recursive: true
            );
        } catch (IOException) {
        }
    }

    private string WriteKey(string name) {
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var path = Path.Combine(
            path1: m_directory,
            path2: name
        );

        File.WriteAllBytes(
            bytes: key.ExportPkcs8PrivateKey(),
            path: path
        );

        return path;
    }
    private WorldSiloDefinition MakeValid() {
        return new WorldSiloDefinition(
            Worlds: [
                new WorldSiloWorldRow(
                    Owner: Guid.NewGuid(),
                    World: (SafeName.TryParse(candidate: "quilt-nw", name: out var nw, reason: out _) ? nw : default),
                    Federation: new WorldSiloFederation(KeyFile: WriteKey(name: "nw.key")),
                    Pinned: true
                ),
                new WorldSiloWorldRow(
                    Owner: Guid.NewGuid(),
                    World: (SafeName.TryParse(candidate: "quilt-ne", name: out var ne, reason: out _) ? ne : default),
                    Federation: new WorldSiloFederation(KeyFile: WriteKey(name: "ne.key")),
                    Pinned: false
                ),
            ],
            Doors: new WorldSiloDoors(Budget: 5),
            Store: new WorldSiloExtension(Type: "directory", Settings: JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = m_directory })),
            StateDir: m_directory,
            Clustering: new WorldSiloClustering(Kind: "Localhost")
        );
    }

    [Fact]
    public void ValidDocument_Validates() {
        Assert.True(condition: WorldSiloDefinitionValidator.TryValidate(definition: MakeValid(), reason: out var reason));
        Assert.Empty(collection: reason);
    }
    [Fact]
    public void SerializeThenLoadFile_RoundTrips() {
        var definition = MakeValid();
        var bytes = WorldSiloDefinitionSerialization.Serialize(definition: definition);
        var path = Path.Combine(path1: m_directory, path2: "silo.json");

        File.WriteAllBytes(bytes: bytes, path: path);

        Assert.True(condition: WorldSiloDefinitionSerialization.TryLoadFile(definition: out var loaded, path: path, reason: out var reason));
        Assert.Empty(collection: reason);
        Assert.NotNull(@object: loaded);
        Assert.Equal(expected: definition.Worlds.Count, actual: loaded!.Worlds.Count);
        Assert.Equal(expected: definition.Doors.Budget, actual: loaded.Doors.Budget);
        Assert.Equal(expected: definition.Store.Type, actual: loaded.Store.Type);
        Assert.Equal(expected: definition.Clustering.Kind, actual: loaded.Clustering.Kind);
    }
    [Fact]
    public void DuplicateWorldId_Refuses() {
        var valid = MakeValid();
        var duplicated = valid with {
            Worlds = [.. valid.Worlds, valid.Worlds[0] with { Owner = Guid.NewGuid(), Federation = new WorldSiloFederation(KeyFile: WriteKey(name: "dup.key")) }],
        };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: duplicated, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "declared more than once");
    }
    [Fact]
    public void SharedKeyFile_Refuses() {
        var valid = MakeValid();
        var shared = valid with {
            Worlds = [valid.Worlds[0], valid.Worlds[1] with { Federation = valid.Worlds[0].Federation }],
        };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: shared, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "share the key file");
    }
    [Fact]
    public void MissingKeyFile_Refuses() {
        var valid = MakeValid();
        var missing = valid with {
            Worlds = [valid.Worlds[0] with { Federation = new WorldSiloFederation(KeyFile: Path.Combine(path1: m_directory, path2: "does-not-exist.key")) }],
        };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: missing, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "does not exist");
    }
    [Fact]
    public void MalformedKeyFile_Refuses() {
        var badPath = Path.Combine(path1: m_directory, path2: "bad.key");

        File.WriteAllBytes(bytes: [1, 2, 3, 4], path: badPath);

        var valid = MakeValid();
        var malformed = valid with {
            Worlds = [valid.Worlds[0] with { Federation = new WorldSiloFederation(KeyFile: badPath) }],
        };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: malformed, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "PKCS#8");
    }
    [Fact]
    public void PinnedCountExceedsBudget_Refuses() {
        var valid = MakeValid();
        var overPinned = valid with {
            Doors = new WorldSiloDoors(Budget: 1),
            Worlds = [valid.Worlds[0], valid.Worlds[1] with { Pinned = true }],
        };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: overPinned, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "exceed the declared doors.budget");
    }
    [Fact]
    public void WrongSchemaTag_Refuses() {
        var wrong = MakeValid() with { Schema = "puck.world.def.v1" };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: wrong, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "schema");
    }
    [Fact]
    public void StoreWithNonObjectSettings_Refuses() {
        var mismatched = MakeValid() with { Store = new WorldSiloExtension("directory", JsonSerializer.SerializeToElement("not an object")) };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: mismatched, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "store requires");
    }
    [Fact]
    public void ClusteringWithNoKind_Refuses() {
        var mismatched = MakeValid() with { Clustering = new WorldSiloClustering(Kind: "") };

        Assert.False(condition: WorldSiloDefinitionValidator.TryValidate(definition: mismatched, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "clustering.kind is required");
    }
    [Fact]
    public void ProviderSettingsRoundTripWithoutSchemaInterpretation() {
        var source = MakeValid() with {
            Store = new WorldSiloExtension("test.store", JsonDocument.Parse("""{"region":"test","options":{"count":3}}""").RootElement.Clone()),
            Lifecycle = new WorldSiloLifecycle(30, 8081, new WorldSiloExtension("test.retirement", JsonDocument.Parse("{}").RootElement.Clone())),
        };
        var path = Path.Combine(path1: m_directory, path2: "extensions.json");

        File.WriteAllBytes(path, WorldSiloDefinitionSerialization.Serialize(definition: source));
        Assert.True(condition: WorldSiloDefinitionSerialization.TryLoadFile(definition: out var loaded, path: path, reason: out var reason), userMessage: reason);
        Assert.Equal("test.store", loaded!.Store.Type);
        Assert.Equal(3, loaded.Store.Settings.GetProperty("options").GetProperty("count").GetInt32());
        Assert.Equal("test.retirement", loaded.Lifecycle!.Observer!.Type);
    }
}

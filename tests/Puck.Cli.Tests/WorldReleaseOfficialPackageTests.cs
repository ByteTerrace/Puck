using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Cli.Azure;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseOfficialPackageTests {
    [Fact]
    public void PublishedCommitReuseChecksTheIdentityExposedByEachDockerStore() {
        var index = "sha256:" + new string('a', 64);
        var config = "sha256:" + new string('b', 64);
        var modern = new JsonObject { ["Id"] = index, ["Descriptor"] = new JsonObject { ["digest"] = index } };
        Assert.True(AzureCommand.MatchesPublishedContainer(index, modern, new JsonObject { ["manifests"] = new JsonArray() }));
        Assert.False(AzureCommand.MatchesPublishedContainer(config, modern, new JsonObject { ["config"] = new JsonObject { ["digest"] = index } }));
        var classic = new JsonObject { ["Id"] = config };
        Assert.True(AzureCommand.MatchesPublishedContainer(index, classic, new JsonObject { ["config"] = new JsonObject { ["digest"] = config } }));
        Assert.False(AzureCommand.MatchesPublishedContainer(index, classic, new JsonObject { ["config"] = new JsonObject { ["digest"] = index } }));
        Assert.False(AzureCommand.MatchesPublishedContainer(index, classic, new JsonObject { ["manifests"] = new JsonArray() }));
    }

    [Fact]
    public void OfficialPreparationPinsEveryCohostedWorldAndBindsThePrimaryBeforeHashing() {
        var temporary = Directory.CreateTempSubdirectory("puck-official-package-");
        try {
            var source = Path.Combine(temporary.FullName, "source");
            var output = Path.Combine(temporary.FullName, "package");
            Directory.CreateDirectory(source);
            var original = WorldDefinitionSerialization.Serialize(new WorldDefinition(HostRaw: WorldHostDefaults.Absent with { Width = 320, Height = 200 }));
            foreach (var world in new[] { "amber", "plum" }) {
                File.WriteAllBytes(Path.Combine(source, world + ".world.json"), original);
            }
            var owner = Guid.NewGuid();
            var outputs = new JsonObject {
                ["worldSiloOwner"] = new JsonObject { ["value"] = owner.ToString("D") },
                ["worldSiloConfiguration"] = new JsonObject { ["value"] = new JsonObject {
                    ["worldName"] = "amber", ["port"] = 4433,
                    ["dns"] = new JsonObject { ["recordName"] = "world", ["zoneName"] = "example.com" },
                } },
            };
            var image = "example.azurecr.io/world-silo@sha256:" + new string('b', 64);
            AzureCommand.PrepareOfficialWorldReleasePackage(outputs, new string('a', 40), image, source, output);
            var first = File.ReadAllBytes(Path.Combine(output, "release.json"));
            var manifest = JsonSerializer.Deserialize<WorldReleaseManifest>(first)!;
            Assert.Equal(WorldReleaseManifest.CurrentCoordinatorContract, manifest.CoordinatorContract);
            Assert.True(WorldReleaseManifest.TryVerify(manifest, output, out var reason), reason);
            Assert.Equal(new[] { $"{owner:D}/amber", $"{owner:D}/plum" }, manifest.Definitions.Keys.Order(StringComparer.Ordinal));
            var primary = JsonNode.Parse(File.ReadAllBytes(Path.Combine(output, "amber.world.json")))!;
            Assert.Equal("world.example.com:4433", primary["host"]!["authority"]!.GetValue<string>());
            Assert.Equal("0.0.0.0:4433", primary["host"]!["listen"]!.GetValue<string>());
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(source, "amber.world.json")));
            AzureCommand.PrepareOfficialWorldReleasePackage(outputs, new string('a', 40), image, source, output);
            Assert.Equal(first, File.ReadAllBytes(Path.Combine(output, "release.json")));
            File.Delete(Path.Combine(source, "amber.world.json"));
            Assert.Throws<InvalidDataException>(() => AzureCommand.PrepareOfficialWorldReleasePackage(outputs, new string('a', 40), image, source, output));
            Assert.Equal(first, File.ReadAllBytes(Path.Combine(output, "release.json")));
        } finally { temporary.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("example.azurecr.io/world-silo:latest")]
    [InlineData("example.azurecr.io/world-silo@sha256:abc")]
    [InlineData("example.azurecr.io/../world@sha256:")]
    [InlineData("example.azurecr.io.evil.test/world@sha256:")]
    public void RegistryProtectionCannotFallBackToATagOrAnotherHost(string image) {
        Assert.Throws<InvalidDataException>(() => AzureCommand.ParseWorldReleaseImage(image));
    }

    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    public void RegistryReadbackMustProveTheExactDigestIsRetainedAndReadable(bool delete, bool write, bool read, bool refuses) {
        var digest = "sha256:" + new string('b', 64);
        var image = AzureCommand.ParseWorldReleaseImage("example.azurecr.io/world-silo@" + digest);
        Assert.Equal("example.azurecr.io", image.Server);
        Assert.Equal("example-dnslabel.azurecr.io", AzureCommand.ParseWorldReleaseImage("example-dnslabel.azurecr.io/world-silo@" + digest).Server);
        Assert.Equal("world-silo@" + digest, image.Reference);
        var result = new JsonObject { ["digest"] = digest, ["changeableAttributes"] = new JsonObject {
            ["deleteEnabled"] = delete, ["writeEnabled"] = write, ["readEnabled"] = read,
        } };
        if (refuses) { Assert.Throws<InvalidDataException>(() => AzureCommand.ValidateRetainedWorldReleaseImage(digest, result)); }
        else { AzureCommand.ValidateRetainedWorldReleaseImage(digest, result); }
        result["digest"] = "sha256:" + new string('c', 64);
        Assert.Throws<InvalidDataException>(() => AzureCommand.ValidateRetainedWorldReleaseImage(digest, result));
    }
}

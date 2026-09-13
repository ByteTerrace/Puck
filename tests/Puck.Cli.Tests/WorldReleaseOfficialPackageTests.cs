using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Cli.Automation;
using Puck.Cli.Azure;
using Puck.World;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseOfficialPackageTests {
    internal static byte[] DeferredDrawDefinition() {
        const string Json = """
            {"schema":"puck.world.definition.v1","host":{"width":320,"height":200},
             "state":{"world":[{"name":"cadence","kind":"Fixed","cells":[],
                "draw":{"generator":{"source":"UniformRange","rangeMin":65536,"rangeMax":131072},"timing":"Boot"}}]},
             "prototypes":[{"id":"actor","document":{"schema":"puck.creation.v1","name":"actor","shapes":[],
                "drivers":[{"name":"stride","signal":"planarTravel","cadence":"state.cadence"}]}}]}
            """;
        var tree = JsonNode.Parse(Json)!;

        tree["host"] = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: new WorldDefinition(HostRaw: WorldHostDefaults.Absent with { Width = 320, Height = 200 })))!["host"]!.DeepClone();
        Assert.True(
            condition: WorldDefinitionFileSource.TryParseComposed(
                tree.ToJsonString(),
                "draw fixture",
                null,
                false,
                out var definition,
                out var reason
            ),
            userMessage: reason
        );
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition!);

        Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: bytes));
        return bytes;
    }

    [Fact]
    public async Task HostedCompositionRebasesNestedMachineAssetsWithoutPinningTheBuildDirectory() {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-hosted-origins-");

        try {
            var source = Directory.CreateDirectory(path: Path.Combine(
                path1: temporary.FullName,
                path2: "worlds"
            ));
            var nested = Directory.CreateDirectory(path: Path.Combine(
                path1: source.FullName,
                path2: "nested"
            ));
            var output = Path.Combine(
                path1: temporary.FullName,
                path2: "output"
            );
            var primary = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: new WorldDefinition()))!;

            primary["references"] = JsonNode.Parse("""[{"name":"child","document":"nested/child.world.json"}]""");
            File.WriteAllText(
                Path.Combine(
                    path1: source.FullName,
                    path2: "puck.world.json"
                ),
                primary.ToJsonString()
            );
            var child = JsonNode.Parse(WorldDefinitionSerialization.Serialize(definition: new WorldDefinition()))!;

            child["machines"] = JsonNode.Parse("""
                [{"name":"console","engine":"gaming-brick","running":false,"configuration":{
                  "schema":"puck.gaming-brick.configuration.v1","model":"cgb","content":{"path":"../../cartridges/game.cgb"}}}]
                """);
            File.WriteAllText(
                Path.Combine(
                    path1: nested.FullName,
                    path2: "child.world.json"
                ),
                child.ToJsonString()
            );
            Assert.Equal(
                0,
                await WorldPrepareCommand.Create().Parse([source.FullName, output]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken)
            );
            var published = File.ReadAllText(path: Path.Combine(
                path1: output,
                path2: "child.world.json"
            ));

            Assert.Equal(
                "../cartridges/game.cgb",
                JsonNode.Parse(published)!["machines"]![0]!["configuration"]!["content"]!["path"]!.GetValue<string>()
            );
            Assert.DoesNotContain(
                temporary.FullName,
                published
            );
            Assert.Equal(
                child.ToJsonString(),
                File.ReadAllText(path: Path.Combine(
                    path1: nested.FullName,
                    path2: "child.world.json"
                ))
            );
        } finally { temporary.Delete(recursive: true); }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void OfficialPreparationPinsEveryCohostedWorldAndBindsThePrimaryBeforeHashing(bool deferredDraw) {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-official-package-");

        try {
            var source = Path.Combine(
                path1: temporary.FullName,
                path2: "source"
            );
            var output = Path.Combine(
                path1: temporary.FullName,
                path2: "package"
            );

            Directory.CreateDirectory(path: source);
            var original = (deferredDraw
                ? DeferredDrawDefinition()
                : WorldDefinitionSerialization.Serialize(definition: new WorldDefinition(HostRaw: WorldHostDefaults.Absent with { Width = 320, Height = 200 }))
            );

            foreach (var world in new[] { "amber", "plum" }) {
                File.WriteAllBytes(
                    Path.Combine(
                        path1: source,
                        path2: (world + ".world.json")
                    ),
                    original
                );
            }
            var owner = Guid.NewGuid();
            var outputs = new JsonObject {
                ["worldSiloOwner"] = new JsonObject { ["value"] = owner.ToString(format: "D") },
                ["worldSiloConfiguration"] = new JsonObject {
                    ["value"] = new JsonObject {
                        ["worldName"] = "amber",
                        ["port"] = 4433,
                        ["dns"] = new JsonObject { ["recordName"] = "world", ["zoneName"] = "example.com" },
                    },
                },
            };
            var image = ("example.azurecr.io/world-silo@sha256:" + new string(
                c: 'b',
                count: 64
            ));

            AzureCommand.PrepareOfficialWorldReleasePackage(
                outputs,
                new string(
                    c: 'a',
                    count: 40
                ),
                image,
                source,
                output
            );
            var first = File.ReadAllBytes(path: Path.Combine(
                path1: output,
                path2: "release.json"
            ));
            var manifest = JsonSerializer.Deserialize<WorldReleaseManifest>(first)!;

            Assert.Equal(
                WorldReleaseManifest.CurrentCoordinatorContract,
                manifest.CoordinatorContract
            );
            Assert.True(
                condition: WorldReleaseManifest.TryVerify(
                    manifest: manifest,
                    packageDirectory: output,
                    reason: out var reason
                ),
                userMessage: reason
            );
            Assert.Equal(
                new[] { $"{owner:D}/amber", $"{owner:D}/plum" },
                manifest.Definitions.Keys.Order(comparer: StringComparer.Ordinal)
            );
            var primary = JsonNode.Parse(File.ReadAllBytes(path: Path.Combine(
                path1: output,
                path2: "amber.world.json"
            )))!;

            Assert.Equal(
                "world.example.com:4433",
                primary["host"]!["authority"]!.GetValue<string>()
            );
            Assert.Equal(
                "0.0.0.0:4433",
                primary["host"]!["listen"]!.GetValue<string>()
            );
            Assert.Equal(
                original,
                File.ReadAllBytes(path: Path.Combine(
                    path1: source,
                    path2: "amber.world.json"
                ))
            );
            AzureCommand.PrepareOfficialWorldReleasePackage(
                outputs,
                new string(
                    c: 'a',
                    count: 40
                ),
                image,
                source,
                output
            );
            Assert.Equal(
                first,
                File.ReadAllBytes(path: Path.Combine(
                    path1: output,
                    path2: "release.json"
                ))
            );
            File.Delete(path: Path.Combine(
                path1: source,
                path2: "amber.world.json"
            ));
            Assert.Throws<InvalidDataException>(() => AzureCommand.PrepareOfficialWorldReleasePackage(
                outputs,
                new string(
                    c: 'a',
                    count: 40
                ),
                image,
                source,
                output
            ));
            Assert.Equal(
                first,
                File.ReadAllBytes(path: Path.Combine(
                    path1: output,
                    path2: "release.json"
                ))
            );
        } finally { temporary.Delete(recursive: true); }
    }
    [Fact]
    public void PublishedCommitReuseChecksTheIdentityExposedByEachDockerStore() {
        var index = ("sha256:" + new string(
            c: 'a',
            count: 64
        ));
        var config = ("sha256:" + new string(
            c: 'b',
            count: 64
        ));
        var modern = new JsonObject { ["Id"] = index, ["Descriptor"] = new JsonObject { ["digest"] = index } };

        Assert.True(AzureCommand.MatchesPublishedContainer(
            index,
            modern,
            new JsonObject { ["manifests"] = new JsonArray() }
        ));
        Assert.False(AzureCommand.MatchesPublishedContainer(
            config,
            modern,
            new JsonObject { ["config"] = new JsonObject { ["digest"] = index } }
        ));
        var classic = new JsonObject { ["Id"] = config };

        Assert.True(AzureCommand.MatchesPublishedContainer(
            index,
            classic,
            new JsonObject { ["config"] = new JsonObject { ["digest"] = config } }
        ));
        Assert.False(AzureCommand.MatchesPublishedContainer(
            index,
            classic,
            new JsonObject { ["config"] = new JsonObject { ["digest"] = index } }
        ));
        Assert.False(AzureCommand.MatchesPublishedContainer(
            index,
            classic,
            new JsonObject { ["manifests"] = new JsonArray() }
        ));
    }
    [InlineData("example.azurecr.io/world-silo:latest")]
    [InlineData("example.azurecr.io/world-silo@sha256:abc")]
    [InlineData("example.azurecr.io/../world@sha256:")]
    [InlineData("example.azurecr.io.evil.test/world@sha256:")]
    [Theory]
    public void RegistryProtectionCannotFallBackToATagOrAnotherHost(string image) {
        Assert.Throws<InvalidDataException>(() => AzureCommand.ParseWorldReleaseImage(image));
    }
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(false, false, false, true)]
    [Theory]
    public void RegistryReadbackMustProveTheExactDigestIsRetainedAndReadable(bool delete, bool write, bool read, bool refuses) {
        var digest = ("sha256:" + new string(
            c: 'b',
            count: 64
        ));
        var image = AzureCommand.ParseWorldReleaseImage(("example.azurecr.io/world-silo@" + digest));

        Assert.Equal(
            "example.azurecr.io",
            image.Server
        );
        Assert.Equal(
            "example-dnslabel.azurecr.io",
            AzureCommand.ParseWorldReleaseImage(("example-dnslabel.azurecr.io/world-silo@" + digest)).Server
        );
        Assert.Equal(
            ("world-silo@" + digest),
            image.Reference
        );
        var result = new JsonObject {
            ["digest"] = digest,
            ["changeableAttributes"] = new JsonObject {
                ["deleteEnabled"] = delete,
                ["writeEnabled"] = write,
                ["readEnabled"] = read,
            },
        };

        if (refuses) { Assert.Throws<InvalidDataException>(() => AzureCommand.ValidateRetainedWorldReleaseImage(
            digest,
            result
        )); } else { AzureCommand.ValidateRetainedWorldReleaseImage(
            digest,
            result
        ); }
        result["digest"] = ("sha256:" + new string(
            c: 'c',
            count: 64
        ));
        Assert.Throws<InvalidDataException>(() => AzureCommand.ValidateRetainedWorldReleaseImage(
            digest,
            result
        ));
    }
}

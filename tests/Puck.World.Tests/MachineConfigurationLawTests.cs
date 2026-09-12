using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Provider metadata governs configuration and resource preparation without host-owned hardware fields.</summary>
public sealed class MachineConfigurationLawTests {
    [Fact]
    public void AliasedDisplaysAndSpeakersKeepTheirNamedProducer() {
        var module = JsonNode.Parse("""
            {"machines":[{"name":"cabinet","engine":"gaming-brick","configuration":{"schema":"puck.gaming-brick.config.v1"}}],
             "screens":[{"index":0,"source":{"$type":"machine","instance":"cabinet","output":"video"}}],
             "speakers":[{"$type":"fixed","name":"speaker","position":[0,0,0],"feed":{"source":{"$type":"machine","instance":"cabinet","output":"audio"},"channel":"mix","gain":1}}]}
            """)!.AsObject();
        Assert.True(WorldModuleNamespace.TryApply(module, "room", out var reason), reason);
        Assert.Equal("room_cabinet", module["machines"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("room_cabinet", module["screens"]![0]!["source"]!["instance"]!.GetValue<string>());
        Assert.Equal("room_cabinet", module["speakers"]![0]!["feed"]!["source"]!["instance"]!.GetValue<string>());
        Assert.Equal("video", module["screens"]![0]!["source"]!["output"]!.GetValue<string>());
        Assert.Equal("audio", module["speakers"]![0]!["feed"]!["source"]!["output"]!.GetValue<string>());
    }

    [Fact]
    public void ProviderConfigurationReportsUnknownFieldsAndItsExactSchema() {
        var descriptor = new GamingBrickEngine().Descriptor.Configuration;
        using var document = JsonDocument.Parse("""{"schema":"wrong","model":"cgb","boot":"fast","modle":"dmg"}""");
        var errors = new List<string>();
        Assert.False(MachineConfigurationValidation.TryValidate(descriptor, document.RootElement, true, errors));
        Assert.Contains(errors, error => error.Contains("configuration.schema", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("configuration.modle", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredConstructionConsumesPreparedFirmwareWithoutReadingItsAuthoredPath() {
        IMachineEngine engine = new GamingBrickEngine();
        using var document = JsonDocument.Parse("""
            {"schema":"puck.gaming-brick.config.v1","model":"cgb","boot":"fast",
             "firmware":{"path":"an-intentionally-unresolved-firmware-path.bin"}}
            """);
        var image = HgbFirmware.GetImage(ConsoleModel.CgbE);
        var request = new MachineCreationRequest(document.RootElement, new Dictionary<string, PreparedMachineAsset> {
            ["firmware.path"] = new("an-intentionally-unresolved-firmware-path.bin", image, WorldDefinitionFileSource.ComputeContentHash(image), image)
        });
        using var runtime = engine.CreateMachine(request);
        Assert.Equal(MachineRuntimeStatus.Empty, runtime.Status);
        Assert.Single(Assert.IsAssignableFrom<IMachineVideoOutputs>(runtime).VideoOutputs);
        Assert.Throws<ArgumentException>(() => engine.CreateMachine(request with { Assets = new Dictionary<string, PreparedMachineAsset>() }));
    }

    [Fact]
    public void FieldMetadataRewritesNestedAndArrayReferencesWithoutTouchingOrdinaryStrings() {
        var descriptor = new MachineObjectDescriptor("puck.test.config.v1", [
            new("caption", MachineFieldKind.String, "Ordinary text."),
            new("rows", MachineFieldKind.Array, "State references.", Item:
                new("row", MachineFieldKind.String, "A state row.", Role: MachineFieldRole.StateReference)),
            new("media", MachineFieldKind.Array, "Firmware assets.", Item:
                new("asset", MachineFieldKind.Object, "One asset.", Fields: [
                    new("path", MachineFieldKind.String, "Firmware path.", Role: MachineFieldRole.AssetPath)
                ]))
        ]);
        var value = JsonNode.Parse("""{"caption":"score","rows":["score","lives"],"media":[{"path":"boot.bin"}]}""")!.AsObject();
        var assets = new List<string>();
        MachineConfigurationFields.Visit(value, descriptor, site => {
            if (site.Field.Role == MachineFieldRole.StateReference) {
                site.Value = JsonValue.Create("cabinet_" + site.Value!.GetValue<string>());
            } else if (site.Field.Role == MachineFieldRole.AssetPath) {
                assets.Add(site.Path);
            }
        });
        Assert.Equal("score", value["caption"]!.GetValue<string>());
        Assert.Equal("cabinet_score", value["rows"]![0]!.GetValue<string>());
        Assert.Equal("cabinet_lives", value["rows"]![1]!.GetValue<string>());
        Assert.Equal("media[0].path", Assert.Single(assets));
    }

    [Fact]
    public void IntegerMetadataAcceptsTheFullUnsignedAddressAndRefusesFractionalOrOutOfRangeValues() {
        var descriptor = new MachineObjectDescriptor("puck.test.config.v1", [
            new("address", MachineFieldKind.Integer, "An unsigned address.", Required: true, Minimum: 0, Maximum: ulong.MaxValue)
        ]);
        foreach (var (json, accepted) in new[] {
            ("""{"address":18446744073709551615}""", true),
            ("""{"address":18446744073709551616}""", false),
            ("""{"address":1.25}""", false),
            ("""{"address":-1}""", false),
        }) {
            using var document = JsonDocument.Parse(json);
            var errors = new List<string>();
            Assert.Equal(accepted, MachineConfigurationValidation.TryValidate(descriptor, document.RootElement, false, errors));
        }
    }

    [Fact]
    public void DescriptorValidationCoversNestedItemsAndRejectsAmbiguousPaths() {
        var descriptor = new MachineObjectDescriptor("puck.test.config.v1", [
            new("nested.path", MachineFieldKind.String, "Invalid path."),
            new("targets", MachineFieldKind.Array, "Nested references.", Item:
                new("target", MachineFieldKind.Integer, "Invalid reference kind.", Role: MachineFieldRole.StateReference))
        ]);
        var errors = new List<string>();

        Assert.False(MachineConfigurationFields.TryValidateDescriptor(descriptor, errors));
        Assert.Contains(errors, error => error.Contains("nested.path", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("targets[]", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("composition roles require a string", StringComparison.Ordinal));
    }

    [Fact]
    public void DescriptorValidationRejectsUndefinedKindAndRoleValues() {
        var descriptor = new MachineObjectDescriptor("puck.test.config.v1", [
            new("kind", (MachineFieldKind)123, "Unknown kind."),
            new("role", MachineFieldKind.String, "Unknown role.", Role: (MachineFieldRole)123)
        ]);
        var errors = new List<string>();

        Assert.False(MachineConfigurationFields.TryValidateDescriptor(descriptor, errors));
        Assert.Contains(errors, error => error.Contains("descriptor.fields.kind.kind", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("descriptor.fields.role.role", StringComparison.Ordinal));
    }
    [Fact]
    public void DescriptorFingerprintSeparatesDelimitedChoices() {
        var first = new MachineObjectDescriptor("puck.test.config.v1", [
            new("mode", MachineFieldKind.String, "Mode.", Choices: ["a,b", "c"])
        ]);
        var second = new MachineObjectDescriptor("puck.test.config.v1", [
            new("mode", MachineFieldKind.String, "Mode.", Choices: ["a", "b,c"])
        ]);

        Assert.NotEqual(
            MachineConfigurationFields.DescriptorFingerprint(first),
            MachineConfigurationFields.DescriptorFingerprint(second)
        );
    }

    [Fact]
    public void AliasedProviderMetadataKeepsWorldKindsAndLocalDeclarationsIndependent() {
        var descriptor = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", [
            new("stateTarget", MachineFieldKind.String, "State target.", Role: MachineFieldRole.StateReference),
            new("machineTarget", MachineFieldKind.String, "Machine target.", Role: MachineFieldRole.MachineReference),
            new("local", MachineFieldKind.String, "Local declaration.", Role: MachineFieldRole.Declaration),
            new("localRef", MachineFieldKind.String, "Local reference.", Role: MachineFieldRole.LocalReference),
            new("literal", MachineFieldKind.String, "Ordinary value."),
            new("rom", MachineFieldKind.String, "Asset.", Role: MachineFieldRole.AssetPath)
        ]));
        var catalog = new TestCatalog(descriptor);
        var module = JsonNode.Parse("""
            {
              "state": { "world": [ { "name": "stateTarget", "kind": "Int", "value": 0 } ] },
              "machines": [
                { "name": "machineTarget", "engine": "test", "configuration":
                  { "stateTarget": "machineTarget", "machineTarget": "stateTarget",
                    "local": "pin", "localRef": "pin", "literal": "stateTarget", "rom": "rom.bin" } }
              ]
            }
            """)!.AsObject();

        var original = (JsonObject)module.DeepClone();
        Assert.True(WorldModuleNamespace.TryApply(
            module,
            "left",
            catalog,
            "modules/cab/fragment.world.json",
            "world.world.json",
            out var reason
        ), reason);

        Assert.Equal("left_stateTarget", module["state"]!["world"]![0]!["name"]!.GetValue<string>());
        var config = module["machines"]![0]!["configuration"]!.AsObject();
        Assert.Equal("machineTarget", config["stateTarget"]!.GetValue<string>());
        Assert.Equal("stateTarget", config["machineTarget"]!.GetValue<string>());
        Assert.Equal("left_pin", config["local"]!.GetValue<string>());
        Assert.Equal("left_pin", config["localRef"]!.GetValue<string>());
        Assert.Equal("stateTarget", config["literal"]!.GetValue<string>());
        Assert.Equal("modules/cab/rom.bin", config["rom"]!.GetValue<string>());

        var second = original;
        Assert.True(WorldModuleNamespace.TryApply(
            second,
            "right",
            catalog,
            "modules/cab/fragment.world.json",
            "world.world.json",
            out reason
        ), reason);
        Assert.Equal("right_pin", second["machines"]![0]!["configuration"]!["local"]!.GetValue<string>());
    }

    [Fact]
    public void ProviderStateReferenceUsesTheExistingPrivateModuleSurface() {
        var descriptor = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", [
            new("target", MachineFieldKind.String, "State target.", Role: MachineFieldRole.StateReference)
        ]));
        var catalog = new TestCatalog(descriptor);
        var imported = JsonNode.Parse("""
            { "state": { "world": [ { "name": "privateRow", "kind": "Int", "value": 0 } ] },
              "machines": [ { "name": "cab", "engine": "test",
                "configuration": { "target": "privateRow" } } ] }
            """)!.AsObject();
        var host = JsonNode.Parse("""
            { "machines": [ { "name": "hostCab", "engine": "test",
                "configuration": { "target": "privateRow" } } ] }
            """)!.AsObject();

        Assert.False(WorldModuleExports.TryCheckLayers(
            "host.world.json",
            host,
            new JsonObject(),
            [("private.world.json", imported, new WorldExports())],
            out _,
            out var reason,
            catalog
        ));
        Assert.Contains("privateRow", reason, StringComparison.Ordinal);
        Assert.Contains("does not export", reason, StringComparison.Ordinal);
    }
    [Fact]
    public void CompositionCacheSeparatesCatalogFingerprintsForOnePath() {
        var directory = Path.Combine(Path.GetTempPath(), "puck-machine-c1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            File.WriteAllText(Path.Combine(directory, "root.world.json"), """
                { "imports": [ { "document": "fragment.world.json", "as": "cab" } ] }
                """);
            File.WriteAllText(Path.Combine(directory, "fragment.world.json"), """
                { "machines": [ { "name": "cab", "engine": "test",
                  "configuration": { "rom": "rom.bin" } } ] }
                """);
            var first = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", [
                new("rom", MachineFieldKind.String, "Asset.", Role: MachineFieldRole.AssetPath)
            ]));
            var second = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", [
                new("rom", MachineFieldKind.String, "Content.", Role: MachineFieldRole.ContentPath)
            ]));
            var firstCatalog = new TestCatalog(first);
            var secondCatalog = new TestCatalog(second);
            var firstFingerprint = MachineConfigurationFields.CatalogFingerprint([("test", first)]);
            var secondFingerprint = MachineConfigurationFields.CatalogFingerprint([("test", second)]);

            WorldDefinitionFileSource.ForgetComposedDocuments();
            Assert.True(WorldDefinitionFileSource.TryComposeDocumentTree(
                Path.Combine(directory, "root.world.json"),
                out var firstTree,
                out var firstReason,
                firstFingerprint,
                firstCatalog
            ), firstReason);
            Assert.True(WorldDefinitionFileSource.TryComposeDocumentTree(
                Path.Combine(directory, "root.world.json"),
                out var secondTree,
                out var secondReason,
                secondFingerprint,
                secondCatalog
            ), secondReason);
            Assert.NotNull(firstTree);
            Assert.NotNull(secondTree);
            Assert.Equal(4, WorldDefinitionFileSource.ComposedDocumentsHeld);
        } finally {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CompositionRejectsFingerprintThatDoesNotBelongToSelectedCatalog() {
        var directory = Path.Combine(Path.GetTempPath(), "puck-machine-c1-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var path = Path.Combine(directory, "root.world.json");
            File.WriteAllText(path, """{ "imports": [ { "document": "fragment.world.json", "as": "cab" } ] }""");
            File.WriteAllText(Path.Combine(directory, "fragment.world.json"), """{ "machines": [ { "name": "cab", "engine": "test", "configuration": {} } ] }""");
            var descriptor = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", []));
            var catalog = new TestCatalog(descriptor, "selected-catalog");
            Assert.False(WorldDefinitionFileSource.TryComposeDocumentTree(path, out _, out var reason, "wrong-catalog", catalog));
            Assert.Contains("selected catalog", reason, StringComparison.Ordinal);
        } finally {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void NestedImportsCarryProviderMetadataAndRelativeAssetsThroughBothAliases() {
        var directory = Path.Combine(Path.GetTempPath(), "puck-machine-c1-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "modules", "inner"));
        try {
            File.WriteAllText(Path.Combine(directory, "root.world.json"),
                "{ \"imports\": [ { \"document\": \"modules/middle.world.json\", \"as\": \"outer\" } ] }");
            File.WriteAllText(Path.Combine(directory, "modules", "middle.world.json"),
                "{ \"imports\": [ { \"document\": \"inner/fragment.world.json\", \"as\": \"inner\" } ] }");
            File.WriteAllText(Path.Combine(directory, "modules", "inner", "fragment.world.json"),
                "{ \"state\": { \"world\": [ { \"name\": \"privateRow\", \"kind\": \"Int\", \"value\": 0 } ] }, \"machines\": [ { \"name\": \"cab\", \"engine\": \"test\", \"configuration\": { \"target\": \"privateRow\", \"rom\": \"rom.bin\" } } ] }");
            var descriptor = TestCatalog.Engine(new MachineObjectDescriptor("puck.test.config.v1", [
                new("target", MachineFieldKind.String, "State target.", Role: MachineFieldRole.StateReference),
                new("rom", MachineFieldKind.String, "ROM asset.", Role: MachineFieldRole.AssetPath)
            ]));
            var catalog = new TestCatalog(descriptor);
            var fingerprint = MachineConfigurationFields.CatalogFingerprint([("test", descriptor)]);

            WorldDefinitionFileSource.ForgetComposedDocuments();
            Assert.True(WorldDefinitionFileSource.TryComposeDocumentTree(
                Path.Combine(directory, "root.world.json"),
                out var tree,
                out var reason,
                fingerprint,
                catalog
            ), reason);

            var json = tree!.ToJsonString();
            Assert.Contains("outer_inner_privateRow", json, StringComparison.Ordinal);
            Assert.Contains("modules/inner/rom.bin", json, StringComparison.Ordinal);
        } finally {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void UnknownProviderConfigurationSurvivesCompositionButRefusesCatalogValidation() {
        var catalog = new TestCatalog(TestCatalog.Engine(new MachineObjectDescriptor("known", [])));
        var module = JsonNode.Parse("""
            { "machines": [ { "name": "cab", "engine": "future",
              "configuration": { "opaque": [ { "reference": "private" } ] } } ] }
            """)!.AsObject();
        var original = module["machines"]![0]!["configuration"]!.ToJsonString();

        Assert.True(WorldModuleNamespace.TryApply(
            module,
            "future",
            catalog,
            "future.world.json",
            "root.world.json",
            out var compositionReason
        ), compositionReason);
        Assert.Equal(original, module["machines"]![0]!["configuration"]!.ToJsonString());

        using var configuration = JsonDocument.Parse("""{"opaque":[{"reference":"private"}]}""");
        var candidate = Fixtures.BuildDocument() with {
            ScreensRaw = null,
            MachinesRaw = [new("cab", "future", configuration.RootElement.Clone())]
        };
        var errors = new List<string>();
        var deferred = new List<string>();
        Assert.True(WorldDefinitionValidator.TryValidateLocally(candidate, null, errors, deferred, out _));
        Assert.Contains(deferred, error => error.Contains("no machine catalog", StringComparison.Ordinal));
        Assert.False(WorldDefinitionValidator.TryValidateLocally(candidate, catalog, out var reason));
        Assert.Contains("unavailable", reason, StringComparison.Ordinal);
    }
    private sealed class TestCatalog(MachineEngineDescriptor descriptor, string? fingerprint = null) : IMachineValidationCatalog {
        public string CompositionFingerprint => fingerprint ?? string.Empty;

        public bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? selected) {
            selected = string.Equals(engineId, descriptor.Id, StringComparison.Ordinal) ? descriptor : null;
            return selected is not null;
        }

        public bool IsRegistered(string engineId) =>
            string.Equals(engineId, descriptor.Id, StringComparison.Ordinal);

        public bool RequiresPreparation(string contentPath) => false;

        public bool CanPrepare(string engineId, string contentPath) => false;

        public static MachineEngineDescriptor Engine(MachineObjectDescriptor configuration) =>
            new("test", "Test engine.", configuration, [], [], [], [], []);
    }
}

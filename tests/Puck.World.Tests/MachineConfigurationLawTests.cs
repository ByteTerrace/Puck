using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Provider metadata governs configuration and resource preparation without host-owned hardware fields.</summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class MachineConfigurationLawTests {
    [Fact]
    public void AliasedDisplaysAndSpeakersKeepTheirNamedProducer() {
        var module = JsonNode.Parse("""
            {"machines":[{"name":"cabinet","engine":"gaming-brick","configuration":{"schema":"puck.gaming-brick.configuration.v1"}}],
             "screens":[{"index":0,"source":{"$type":"machine","instance":"cabinet","output":"video"}}],
             "speakers":[{"$type":"fixed","name":"speaker","position":[0,0,0],"feed":{"source":{"$type":"machine","instance":"cabinet","output":"audio"},"channel":"mix","gain":1}}]}
            """)!.AsObject();

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "room",
                module: module,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.Equal(
            "room_cabinet",
            module["machines"]![0]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            "room_cabinet",
            module["screens"]![0]!["source"]!["instance"]!.GetValue<string>()
        );
        Assert.Equal(
            "room_cabinet",
            module["speakers"]![0]!["feed"]!["source"]!["instance"]!.GetValue<string>()
        );
        Assert.Equal(
            "video",
            module["screens"]![0]!["source"]!["output"]!.GetValue<string>()
        );
        Assert.Equal(
            "audio",
            module["speakers"]![0]!["feed"]!["source"]!["output"]!.GetValue<string>()
        );
    }
    [Fact]
    public void AliasedProviderMetadataKeepsWorldKindsAndLocalDeclarationsIndependent() {
        var descriptor = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "stateTarget",
                    MachineFieldKind.String,
                    "State target.",
                    Role: MachineFieldRole.StateReference
                ),
            new(
                    "machineTarget",
                    MachineFieldKind.String,
                    "Machine target.",
                    Role: MachineFieldRole.MachineReference
                ),
            new(
                    "local",
                    MachineFieldKind.String,
                    "Local declaration.",
                    Role: MachineFieldRole.Declaration
                ),
            new(
                    "localRef",
                    MachineFieldKind.String,
                    "Local reference.",
                    Role: MachineFieldRole.LocalReference
                ),
            new(
                    "literal",
                    MachineFieldKind.String,
                    "Ordinary value."
                ),
            new(
                    "rom",
                    MachineFieldKind.String,
                    "Asset.",
                    Role: MachineFieldRole.AssetPath
                )
        ]
        ));
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

        var original = ((JsonObject)module.DeepClone());

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "left",
                catalog: catalog,
                module: module,
                reason: out var reason,
                sourceDocumentPath: "modules/cab/fragment.world.json",
                targetDocumentPath: "world.world.json"
            ),
            userMessage: reason
        );

        Assert.Equal(
            "left_stateTarget",
            module["state"]!["world"]![0]!["name"]!.GetValue<string>()
        );
        var config = module["machines"]![0]!["configuration"]!.AsObject();

        Assert.Equal(
            "machineTarget",
            config["stateTarget"]!.GetValue<string>()
        );
        Assert.Equal(
            "stateTarget",
            config["machineTarget"]!.GetValue<string>()
        );
        Assert.Equal(
            "left_pin",
            config["local"]!.GetValue<string>()
        );
        Assert.Equal(
            "left_pin",
            config["localRef"]!.GetValue<string>()
        );
        Assert.Equal(
            "stateTarget",
            config["literal"]!.GetValue<string>()
        );
        Assert.Equal(
            "modules/cab/rom.bin",
            config["rom"]!.GetValue<string>()
        );

        var second = original;

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "right",
                catalog: catalog,
                module: second,
                reason: out reason,
                sourceDocumentPath: "modules/cab/fragment.world.json",
                targetDocumentPath: "world.world.json"
            ),
            userMessage: reason
        );
        Assert.Equal(
            "right_pin",
            second["machines"]![0]!["configuration"]!["local"]!.GetValue<string>()
        );
    }
    [Fact]
    public void CompositionCacheSeparatesCatalogFingerprintsForOnePath() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-machine-c1-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: directory);
        try {
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "root.world.json"
                ),
                """
                { "imports": [ { "document": "fragment.world.json", "as": "cab" } ] }
                """
            );
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "fragment.world.json"
                ),
                """
                { "machines": [ { "name": "cab", "engine": "test",
                  "configuration": { "rom": "rom.bin" } } ] }
                """
            );
            var first = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
                "puck.test.config.v1",
                [
                new(
                        "rom",
                        MachineFieldKind.String,
                        "Asset.",
                        Role: MachineFieldRole.AssetPath
                    )
            ]
            ));
            var second = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
                "puck.test.config.v1",
                [
                new(
                        "rom",
                        MachineFieldKind.String,
                        "Content.",
                        Role: MachineFieldRole.ContentPath
                    )
            ]
            ));
            var firstCatalog = new TestCatalog(first);
            var secondCatalog = new TestCatalog(second);
            var firstFingerprint = MachineConfigurationFields.CatalogFingerprint(descriptors: [("test", first)]);
            var secondFingerprint = MachineConfigurationFields.CatalogFingerprint(descriptors: [("test", second)]);

            Assert.True(
                condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                    Path.Combine(
                        path1: directory,
                        path2: "root.world.json"
                    ),
                    out var firstTree,
                    out var firstReason,
                    firstFingerprint,
                    firstCatalog
                ),
                userMessage: firstReason
            );
            Assert.True(
                condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                    Path.Combine(
                        path1: directory,
                        path2: "root.world.json"
                    ),
                    out var secondTree,
                    out var secondReason,
                    secondFingerprint,
                    secondCatalog
                ),
                userMessage: secondReason
            );
            Assert.NotNull(@object: firstTree);
            Assert.NotNull(@object: secondTree);

            // The claim is per key, not a process-wide count: the store holds one image per (path, fingerprint)
            // pair, so one path under two catalogs occupies two slots and neither catalog reads the other's.
            foreach (var document in new[] { "root.world.json", "fragment.world.json" }) {
                var path = Path.Combine(
                    path1: directory,
                    path2: document
                );

                foreach (var fingerprint in new[] { firstFingerprint, secondFingerprint }) {
                    Assert.True(
                        condition: WorldDefinitionFileSource.HoldsComposedDocument(
                            catalogFingerprint: fingerprint,
                            resolvedPath: path
                        ),
                        userMessage: $"'{document}' holds no image under its own catalog fingerprint"
                    );
                }

                Assert.False(
                    condition: WorldDefinitionFileSource.HoldsComposedDocument(resolvedPath: path),
                    userMessage: $"'{document}' holds an image under no fingerprint at all"
                );
            }
        } finally {
            Directory.Delete(
                directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void CompositionRejectsFingerprintThatDoesNotBelongToSelectedCatalog() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-machine-c1-fingerprint-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: directory);
        try {
            var path = Path.Combine(
                path1: directory,
                path2: "root.world.json"
            );

            File.WriteAllText(
                contents: """{ "imports": [ { "document": "fragment.world.json", "as": "cab" } ] }""",
                path: path
            );
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "fragment.world.json"
                ),
                """{ "machines": [ { "name": "cab", "engine": "test", "configuration": {} } ] }"""
            );
            var descriptor = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
                Fields: [],
                Id: "puck.test.config.v1"
            ));
            var catalog = new TestCatalog(
                descriptor: descriptor,
                fingerprint: "selected-catalog"
            );

            Assert.False(condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                catalog: catalog,
                catalogFingerprint: "wrong-catalog",
                path: path,
                reason: out var reason,
                tree: out _
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "selected catalog"
            );
        } finally {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            Directory.Delete(
                directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void DescriptorFingerprintSeparatesDelimitedChoices() {
        var first = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "mode",
                    MachineFieldKind.String,
                    "Mode.",
                    Choices: ["a,b", "c"]
                )
        ]
        );
        var second = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "mode",
                    MachineFieldKind.String,
                    "Mode.",
                    Choices: ["a", "b,c"]
                )
        ]
        );

        Assert.NotEqual(
            MachineConfigurationFields.DescriptorFingerprint(descriptor: first),
            MachineConfigurationFields.DescriptorFingerprint(descriptor: second)
        );
    }
    [Fact]
    public void DescriptorValidationCoversNestedItemsAndRejectsAmbiguousPaths() {
        var descriptor = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "nested.path",
                    MachineFieldKind.String,
                    "Invalid path."
                ),
            new(
                    "targets",
                    MachineFieldKind.Array,
                    "Nested references.",
                    Item:
                new(
                        "target",
                        MachineFieldKind.Integer,
                        "Invalid reference kind.",
                        Role: MachineFieldRole.StateReference
                    )
                )
        ]
        );
        var errors = new List<string>();

        Assert.False(condition: MachineConfigurationFields.TryValidateDescriptor(
            descriptor: descriptor,
            errors: errors
        ));
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "nested.path"
            )
        );
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "targets[]"
            )
        );
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "composition roles require a string"
            )
        );
    }
    [Fact]
    public void DescriptorValidationRejectsUndefinedKindAndRoleValues() {
        var descriptor = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "kind",
                    ((MachineFieldKind)123),
                    "Unknown kind."
                ),
            new(
                    "role",
                    MachineFieldKind.String,
                    "Unknown role.",
                    Role: ((MachineFieldRole)123)
                )
        ]
        );
        var errors = new List<string>();

        Assert.False(condition: MachineConfigurationFields.TryValidateDescriptor(
            descriptor: descriptor,
            errors: errors
        ));
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "descriptor.fields.kind.kind"
            )
        );
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "descriptor.fields.role.role"
            )
        );
    }
    [Fact]
    public void FieldMetadataRewritesNestedAndArrayReferencesWithoutTouchingOrdinaryStrings() {
        var descriptor = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "caption",
                    MachineFieldKind.String,
                    "Ordinary text."
                ),
            new(
                    "rows",
                    MachineFieldKind.Array,
                    "State references.",
                    Item:
                new(
                        "row",
                        MachineFieldKind.String,
                        "A state row.",
                        Role: MachineFieldRole.StateReference
                    )
                ),
            new(
                    "media",
                    MachineFieldKind.Array,
                    "Firmware assets.",
                    Item:
                new(
                        "asset",
                        MachineFieldKind.Object,
                        "One asset.",
                        Fields: [
                    new(
                                "path",
                                MachineFieldKind.String,
                                "Firmware path.",
                                Role: MachineFieldRole.AssetPath
                            )
                ]
                    )
                )
        ]
        );
        var value = JsonNode.Parse("""{"caption":"score","rows":["score","lives"],"media":[{"path":"boot.bin"}]}""")!.AsObject();
        var assets = new List<string>();

        MachineConfigurationFields.Visit(
            configuration: value,
            descriptor: descriptor,
            visitor: site => {
            if (site.Field.Role == MachineFieldRole.StateReference) {
                site.Value = JsonValue.Create(("cabinet_" + site.Value!.GetValue<string>()));
            } else if (site.Field.Role == MachineFieldRole.AssetPath) {
                assets.Add(item: site.Path);
            }
        }
        );
        Assert.Equal(
            "score",
            value["caption"]!.GetValue<string>()
        );
        Assert.Equal(
            "cabinet_score",
            value["rows"]![0]!.GetValue<string>()
        );
        Assert.Equal(
            "cabinet_lives",
            value["rows"]![1]!.GetValue<string>()
        );
        Assert.Equal(
            "media[0].path",
            Assert.Single(collection: assets)
        );
    }
    [Fact]
    public void IntegerMetadataAcceptsTheFullUnsignedAddressAndRefusesFractionalOrOutOfRangeValues() {
        var descriptor = new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "address",
                    MachineFieldKind.Integer,
                    "An unsigned address.",
                    Required: true,
                    Minimum: 0,
                    Maximum: ulong.MaxValue
                )
        ]
        );

        foreach (var (json, accepted) in new[] {
            ("""{"address":18446744073709551615}""", true),
            ("""{"address":18446744073709551616}""", false),
            ("""{"address":1.25}""", false),
            ("""{"address":-1}""", false),
        }) {
            using var document = JsonDocument.Parse(json);
            var errors = new List<string>();

            Assert.Equal(
                accepted,
                MachineConfigurationValidation.TryValidate(
                    descriptor,
                    document.RootElement,
                    false,
                    errors
                )
            );
        }
    }
    [Fact]
    public void NestedImportsCarryProviderMetadataAndRelativeAssetsThroughBothAliases() {
        var directory = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-machine-c1-nested-" + Guid.NewGuid().ToString(format: "N"))
        );

        Directory.CreateDirectory(path: Path.Combine(
            path1: directory,
            path2: "modules",
            path3: "inner"
        ));
        try {
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "root.world.json"
                ),
                "{ \"imports\": [ { \"document\": \"modules/middle.world.json\", \"as\": \"outer\" } ] }"
            );
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "modules",
                    path3: "middle.world.json"
                ),
                "{ \"imports\": [ { \"document\": \"inner/fragment.world.json\", \"as\": \"inner\" } ] }"
            );
            File.WriteAllText(
                Path.Combine(
                    path1: directory,
                    path2: "modules",
                    path3: "inner",
                    path4: "fragment.world.json"
                ),
                "{ \"state\": { \"world\": [ { \"name\": \"privateRow\", \"kind\": \"Int\", \"value\": 0 } ] }, \"machines\": [ { \"name\": \"cab\", \"engine\": \"test\", \"configuration\": { \"target\": \"privateRow\", \"rom\": \"rom.bin\" } } ] }"
            );
            var descriptor = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
                "puck.test.config.v1",
                [
                new(
                        "target",
                        MachineFieldKind.String,
                        "State target.",
                        Role: MachineFieldRole.StateReference
                    ),
                new(
                        "rom",
                        MachineFieldKind.String,
                        "ROM asset.",
                        Role: MachineFieldRole.AssetPath
                    )
            ]
            ));
            var catalog = new TestCatalog(descriptor);
            var fingerprint = MachineConfigurationFields.CatalogFingerprint(descriptors: [("test", descriptor)]);

            WorldDefinitionFileSource.ForgetComposedDocuments();
            Assert.True(
                condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                    Path.Combine(
                        path1: directory,
                        path2: "root.world.json"
                    ),
                    out var tree,
                    out var reason,
                    fingerprint,
                    catalog
                ),
                userMessage: reason
            );

            var json = tree!.ToJsonString();

            Assert.Contains(
                actualString: json,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "outer_inner_privateRow"
            );
            Assert.Contains(
                actualString: json,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: "modules/inner/rom.bin"
            );
        } finally {
            WorldDefinitionFileSource.ForgetComposedDocuments();
            Directory.Delete(
                directory,
                recursive: true
            );
        }
    }
    [Fact]
    public void ProviderConfigurationReportsUnknownFieldsAndItsExactSchema() {
        var descriptor = new GamingBrickEngine().Descriptor.Configuration;
        using var document = JsonDocument.Parse("""{"schema":"wrong","model":"cgb","boot":"fast","modle":"dmg"}""");
        var errors = new List<string>();

        Assert.False(condition: MachineConfigurationValidation.TryValidate(
            descriptor,
            document.RootElement,
            true,
            errors
        ));
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "configuration.schema"
            )
        );
        Assert.Contains(
            collection: errors,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "configuration.modle"
            )
        );
    }
    [Fact]
    public void ProviderStateReferenceUsesTheExistingPrivateModuleSurface() {
        var descriptor = TestCatalog.Engine(configuration: new MachineObjectDescriptor(
            "puck.test.config.v1",
            [
            new(
                    "target",
                    MachineFieldKind.String,
                    "State target.",
                    Role: MachineFieldRole.StateReference
                )
        ]
        ));
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

        Assert.False(condition: WorldModuleExports.TryCheckLayers(
            "host.world.json",
            host,
            new JsonObject(),
            [("private.world.json", imported, new WorldExports())],
            out _,
            out var reason,
            catalog
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "privateRow"
        );
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "does not export"
        );
    }
    [Fact]
    public void StructuredConstructionConsumesPreparedFirmwareWithoutReadingItsAuthoredPath() {
        IMachineEngine engine = new GamingBrickEngine();
        using var document = JsonDocument.Parse("""
            {"schema":"puck.gaming-brick.configuration.v1","model":"cgb","boot":"fast",
             "firmware":{"path":"an-intentionally-unresolved-firmware-path.bin"}}
            """);
        var image = HgbFirmware.GetImage(model: ConsoleModel.CgbE);
        var request = new MachineCreationRequest(
            document.RootElement,
            new Dictionary<string, PreparedMachineAsset> {
            ["firmware.path"] = new(
                "an-intentionally-unresolved-firmware-path.bin",
                image,
                WorldDefinitionFileSource.ComputeContentHash(content: image),
                image
            ),
        }
        );
        using var runtime = engine.CreateMachine(request: request);

        Assert.Equal(
            MachineRuntimeStatus.Empty,
            runtime.Status
        );
        Assert.Single(collection: Assert.IsAssignableFrom<IMachineVideoOutputs>(@object: runtime).VideoOutputs);
        Assert.Throws<ArgumentException>(testCode: () => engine.CreateMachine(request: request with { Assets = new Dictionary<string, PreparedMachineAsset>() }));
    }
    [Fact]
    public void UnknownProviderConfigurationSurvivesCompositionButRefusesCatalogValidation() {
        var catalog = new TestCatalog(TestCatalog.Engine(configuration: new MachineObjectDescriptor(
            Fields: [],
            Id: "known"
        )));
        var module = JsonNode.Parse("""
            { "machines": [ { "name": "cab", "engine": "future",
              "configuration": { "opaque": [ { "reference": "private" } ] } } ] }
            """)!.AsObject();
        var original = module["machines"]![0]!["configuration"]!.ToJsonString();

        Assert.True(
            condition: WorldModuleNamespace.TryApply(
                alias: "future",
                catalog: catalog,
                module: module,
                reason: out var compositionReason,
                sourceDocumentPath: "future.world.json",
                targetDocumentPath: "root.world.json"
            ),
            userMessage: compositionReason
        );
        Assert.Equal(
            original,
            module["machines"]![0]!["configuration"]!.ToJsonString()
        );

        using var configuration = JsonDocument.Parse("""{"opaque":[{"reference":"private"}]}""");
        var candidate = Fixtures.BuildDocument() with {
            ScreensRaw = null,
            MachinesRaw = [new(
                "cab",
                "future",
                configuration.RootElement.Clone()
            )],
        };
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: WorldDefinitionValidator.TryValidateLocally(
            compilation: out _,
            deferred: deferred,
            definition: candidate,
            errors: errors,
            machines: null
        ));
        Assert.Contains(
            collection: deferred,
            filter: error => error.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "no machine catalog"
            )
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: candidate,
            machines: catalog,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "unavailable"
        );
    }

    private sealed class TestCatalog(MachineEngineDescriptor descriptor, string? fingerprint = null) : IMachineValidationCatalog {
        public string CompositionFingerprint => (fingerprint ?? string.Empty);

        public bool CanPrepare(string engineId, string contentPath) => false;
        public static MachineEngineDescriptor Engine(MachineObjectDescriptor configuration) =>
            new(
                AudioOutputs: [],
                Configuration: configuration,
                Description: "Test engine.",
                Id: "test",
                InputPorts: [],
                MemorySpaces: [],
                Operations: [],
                VideoOutputs: []
            );
        public bool IsRegistered(string engineId) =>
            string.Equals(
                a: engineId,
                b: descriptor.Id,
                comparisonType: StringComparison.Ordinal
            );
        public bool RequiresPreparation(string contentPath) => false;
        public bool TryDescriptor(string engineId, [NotNullWhen(true)] out MachineEngineDescriptor? selected) {
            selected = (string.Equals(
                a: engineId,
                b: descriptor.Id,
                comparisonType: StringComparison.Ordinal
            )
                ? descriptor
                : null
            );
            return (selected is not null);
        }
    }
}

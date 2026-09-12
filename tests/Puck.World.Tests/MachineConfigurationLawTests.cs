using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Provider metadata governs configuration and resource preparation without host-owned hardware fields.</summary>
public sealed class MachineConfigurationLawTests {
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
            ["firmware.path"] = new("an-intentionally-unresolved-firmware-path.bin", image, WorldDefinitionFileSource.ComputeContentHash(image))
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
}

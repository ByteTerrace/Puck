using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// An in-process substrate law directly against <see cref="WorldDefinitionSerialization"/>: an unknown JSON
/// member on a NESTED document row (a <c>WorldAddonRow</c>) refuses BY NAME, and the identical document minus
/// that one member parses clean; a root-level reserved-prefix member survives the same strict default through
/// <c>WorldDefinition.Extensions</c>'s <c>[JsonExtensionData]</c> carve-out.
/// <see cref="WorldDefinitionSerialization.Deserialize"/> wraps every parse/validation failure in one
/// <see cref="InvalidDataException"/> (its own documented contract), so the probe below unwraps to the
/// originating <see cref="JsonException"/> rather than catching the wrapper by message text. This suite cannot
/// prove the same strictness for a shipped world/scenario document that authors binding overlays against the
/// real engine's default vocabulary — see <see cref="TestHookInstaller"/>'s remarks; that sweep needs the real
/// composition root and is unproven here.
/// </summary>
public sealed class StrictParseLawTests {
    [Fact]
    public void UnknownMemberOnNestedRow_RefusesByName() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Fixtures.SabotagedAddonBytes()));

        Assert.IsType<JsonException>(@object: exception.InnerException);

        // "Refuses BY NAME" is the law's own claim, not incidental structure — this is the one place this suite
        // inspects an exception message, and only for the injected member's own name.
        Assert.Contains(expectedSubstring: "bogusField", actualString: exception.InnerException!.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void UnknownMemberOnNestedRow_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "strict-parse.addon-row-unmapped-member",
            deniedOutcome: static () => TryParse(bytes: Fixtures.SabotagedAddonBytes()),
            controlOutcome: static () => TryParse(bytes: Fixtures.DefaultWorldBytes()));
    }
    [Fact]
    public void MissingSeatLook_ParsesCleanAndResolvesToTheInertDefault() {
        // A seat's control feel is now optional: absence parses clean and resolves to WorldSeatCameraFeel.Default
        // (zero sensitivity, the drag disarmed) rather than refusing.
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: Fixtures.MissingSeatLookBytes());

        Assert.Equal(expected: WorldSeatCameraFeel.Default, actual: definition.PlayerDefaults.SeatLook);
    }
    [Fact]
    public void MissingRequiredConstructorMember_RefusesByName() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Fixtures.MissingHostPresentationBytes()));

        // Named, not merely refused. This is the OTHER half of strict parse: unmapped members were always caught,
        // missing ones were silently filled — an absent enum landing on 0 and answering with a value nobody authored.
        Assert.Contains(expectedSubstring: "presentation", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void MissingRequiredConstructorMember_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "strict-parse.host-missing-presentation",
            deniedOutcome: static () => TryParse(bytes: Fixtures.MissingHostPresentationBytes()),
            controlOutcome: static () => TryParse(bytes: Fixtures.DefaultWorldBytes()));
    }
    [Fact]
    public void AnOptionalMemberMayBeOmitted() {
        // The complement, and the reason the change is a contract rather than a blanket tightening: a member that
        // carries an explicit C# default is genuinely optional and its absence must still parse. Without this leg,
        // "everything is required" would satisfy the law above and break every always-rule in the tree.
        var node = System.Text.Json.Nodes.JsonNode.Parse(json: System.Text.Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();
        var rules = node["rules"]?.AsArray();

        if ((rules is null) || (rules.Count == 0)) {
            return;
        }

        _ = rules[0]!.AsObject().Remove(propertyName: "gate");

        _ = WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: node.ToJsonString()));
    }
    [Fact]
    public void DynamicsRow_UnmappedMember_RefusesByName() {
        var definition = Fixtures.BuildDocument() with {
            DynamicsRaw = [new WorldDynamicsRow(Name: "chase", Frequency: 1f, Damping: 1f, Response: 0f)],
        };
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)))!.AsObject();

        node["dynamics"]!.AsArray()[0]!["rate"] = 6;

        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString())));

        Assert.IsType<JsonException>(@object: exception.InnerException);
        Assert.Contains(expectedSubstring: "rate", actualString: exception.InnerException!.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void StateDynamicsTrait_RoundTripsDecimalByteIdentical() {
        var y0 = Puck.Maths.FixedQ4816.FromDouble(value: 12.5).Value;
        var v0 = Puck.Maths.FixedQ4816.FromDouble(value: -3.25).Value;
        var definition = Fixtures.BuildDocument() with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, new WorldDynamicsRow(Name: "gauge", Frequency: 1f, Damping: 1f, Response: 0f)],
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "hp"),
                    Kind: CellKind.Fixed,
                    Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0)],
                    Dynamics: new WorldStateDynamics(Row: "gauge", Y0: y0, V0: v0, EpochTick: 42)
                ),
            ]),
        };

        var first = WorldDefinitionSerialization.Serialize(definition: definition);
        var reparsed = WorldDefinitionSerialization.Deserialize(utf8Json: first);
        var second = WorldDefinitionSerialization.Serialize(definition: reparsed);

        Assert.Equal(expected: first, actual: second);
        Assert.Equal(expected: y0, actual: reparsed.State[0].Dynamics!.Y0);
        Assert.Equal(expected: v0, actual: reparsed.State[0].Dynamics!.V0);
        Assert.Equal(expected: 42, actual: reparsed.State[0].Dynamics!.EpochTick);
    }
    [Fact]
    public void RootReservedPrefixExtension_SurvivesTheStrictDefault() {
        // WorldDefinition.Extensions carries [JsonExtensionData], which System.Text.Json always honors over the
        // context-wide UnmappedMemberHandling.Disallow default — the one carve-out RootReservedPrefixExtension
        // covers on top of the plain unmapped-member refusals above.
        var node = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: Fixtures.DefaultWorldBytes()))!.AsObject();

        node["$probe"] = "root-extension-roundtrip";

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: Encoding.UTF8.GetBytes(s: node.ToJsonString()));
        var roundTripped = JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)))!.AsObject();

        Assert.Equal(expected: "root-extension-roundtrip", actual: roundTripped["$probe"]!.GetValue<string>());
    }

    private static bool TryParse(byte[] bytes) {
        try {
            _ = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

            return true;
        } catch (InvalidDataException) {
            return false;
        }
    }
}

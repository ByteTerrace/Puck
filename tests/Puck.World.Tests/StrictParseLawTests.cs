using System.Text.Json;

using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// Ports the law <c>docs/verification/strict-definition-parse/run.ps1</c> proves out-of-process (booting
/// <c>Puck.World</c> once per fixture and reading its transcript) as an in-process substrate law directly against
/// <see cref="WorldDefinitionSerialization"/>: an unknown JSON member on a NESTED document row (a
/// <c>WorldAddonRow</c>, the exact row the strict-parse gap was originally named against) refuses BY NAME, and the
/// identical document minus that one member parses clean. <see cref="WorldDefinitionSerialization.Deserialize"/>
/// wraps every parse/validation failure in one <see cref="InvalidDataException"/> (its own documented contract), so
/// the probe below unwraps to the originating <see cref="JsonException"/> rather than catching the wrapper by
/// message text. Absorption/deletion of the <c>.ps1</c> runner is a later step — this ports the law alongside it,
/// per the task charter; the runner itself is untouched.
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
    public void MissingSeatLook_RefusesByName() {
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: Fixtures.MissingSeatLookBytes()));

        // Named, not merely refused: a document missing its seats' control feel must say which member is absent, or
        // an author is left diffing against a schema to find out what a generic validation failure meant.
        //
        // The refusal now comes from the LOADER rather than the validator — once the context respects required
        // constructor parameters, an absent seatLook cannot reach validation at all. So this asserts the member's own
        // name, which both refusals carry, rather than the validator's dotted path, which only one of them does. That
        // is a strictly earlier and stricter refusal, not a weaker assertion: the member is named either way, and
        // pinning the wording of whichever layer happens to win would make this test a record of plumbing.
        Assert.Contains(expectedSubstring: "seatLook", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSeatLook_RefusesByName_ControlParsesClean() {
        Laws.RefusalWithControl(
            lawId: "strict-parse.player-defaults-missing-seat-look",
            deniedOutcome: static () => TryParse(bytes: Fixtures.MissingSeatLookBytes()),
            controlOutcome: static () => TryParse(bytes: Fixtures.DefaultWorldBytes()));
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

    private static bool TryParse(byte[] bytes) {
        try {
            _ = WorldDefinitionSerialization.Deserialize(utf8Json: bytes);

            return true;
        } catch (InvalidDataException) {
            return false;
        }
    }
}

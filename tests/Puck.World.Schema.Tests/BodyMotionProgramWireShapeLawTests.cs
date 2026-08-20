using Xunit;

using System.Text.Json;
using Puck.Physics.Motion;

namespace Puck.World.Schema.Tests;

/// <summary>
/// A <c>bodyMotionPrograms</c> row's authored spelling is a document contract, not an implementation detail of
/// whichever assembly declares its enums: the opcode names, the program-kind name, and the target's <c>$type</c>
/// discriminator are pinned here so a type that moves between assemblies cannot silently change what a shipped world
/// file has to say.
/// </summary>
public sealed class BodyMotionProgramWireShapeLawTests {
    private const string SensedProducerJson = """
{
  "name": "wander",
  "version": "puck.body-motion.v1",
  "kind": "Producer",
  "operations": [
    "SenseNearestInCone",
    "ProduceAttendIntent"
  ],
  "target": {
    "$type": "sensed",
    "scope": "Bodies",
    "range": 12,
    "halfAngleDegrees": 45,
    "requiresLineOfSight": true
  }
}
""";

    [Fact]
    public void AuthoredRowRoundTripsThroughItsPinnedSpelling() {
        var program = new BodyMotionProgram(
            Name: "wander",
            Version: BodyMotionProgram.CurrentVersion,
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.ProduceAttendIntent],
            Target: new BodyTargetSource.Sensed(
                Scope: BodyTargetScope.Bodies,
                Range: 12f,
                HalfAngleDegrees: 45f,
                RequiresLineOfSight: true
            )
        );
        var written = JsonSerializer.Serialize(
            value: program,
            jsonTypeInfo: WorldJsonContext.Default.BodyMotionProgram
        );

        Assert.Equal(
            expected: SensedProducerJson.ReplaceLineEndings(replacementText: "\n"),
            actual: written.ReplaceLineEndings(replacementText: "\n")
        );

        var read = JsonSerializer.Deserialize(
            json: SensedProducerJson,
            jsonTypeInfo: WorldJsonContext.Default.BodyMotionProgram
        )!;

        Assert.Equal(
            expected: program.Name,
            actual: read.Name
        );
        Assert.Equal(
            expected: program.Version,
            actual: read.Version
        );
        Assert.Equal(
            expected: program.Kind,
            actual: read.Kind
        );
        Assert.Equal(
            expected: program.Operations,
            actual: read.Operations
        );
        Assert.Equal(
            expected: program.Target,
            actual: read.Target
        );
    }
    [Fact]
    public void OpcodeNamesAreRefusedAsNumbers() {
        _ = Assert.Throws<JsonException>(testCode: () => JsonSerializer.Deserialize(
            json: """{"name":"n","version":"puck.body-motion.v1","kind":"Motion","operations":[0]}""",
            jsonTypeInfo: WorldJsonContext.Default.BodyMotionProgram
        ));
    }
}
